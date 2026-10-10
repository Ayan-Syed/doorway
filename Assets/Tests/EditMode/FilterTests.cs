using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public class FilterTests
{
    const float Cell = 0.25f;
    // The arrival radius (Navigator.ArriveDistance): a logged walk that arrived ended about this far from the door.
    const float ArriveDistance = 2f;

    // An L of hallway 2 m wide: east along z 0..2 from x 0 to 32, then north along x 30..32 up to z 22.
    static Walkable Corner()
    {
        return Grid(-1f, 23f, 34f, 24f, p =>
            (p.x > 0f && p.x < 32f && p.y > 0f && p.y < 2f) || (p.x > 30f && p.x < 32f && p.y > 0f && p.y < 22f));
    }

    // A straight hallway 2 m wide, x 0..30, z 0..2.
    static Walkable Hallway()
    {
        return Grid(-1f, 3f, 32f, 4f, p => p.x > 0f && p.x < 30f && p.y > 0f && p.y < 2f);
    }

    static Walkable Grid(float left, float top, float width, float height, Func<Vector2, bool> walkable)
    {
        int w = Mathf.RoundToInt(width / Cell), h = Mathf.RoundToInt(height / Cell);
        var rows = new string[h];
        var chars = new char[w];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
                chars[i] = walkable(new Vector2(left + (i + 0.5f) * Cell, top - (j + 0.5f) * Cell)) ? '.' : '#';
            rows[j] = new string(chars);
        }
        return Walkable.FromRows(left, top, Cell, rows);
    }

    // A plan direction as ARCore would report it: the AR world turned bias degrees from the plan.
    static Vector2 ToWorld(Vector2 plan, float bias) => PositionFilter.ToPlan(plan, -bias);

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void WalkRoundACornerWithHeadingAndScaleErrors(int seed)
    {
        Walkable walkable = Corner();
        var random = new System.Random(seed * 7919);
        var filter = new PositionFilter(walkable, PositionFilter.DefaultCount, seed);
        Vector2 start = new Vector2(1f, 1f);
        filter.Reset(start, Vector2.zero, 0f);

        // ARCore's view of the walk: turned 5 degrees, 3 % short, and jittering a centimetre.
        const float bias = 5f, scale = 0.97f, jitter = 0.01f, pace = 0.04f;   // 0.04 m a frame: 1.2 m/s at 30 fps
        Vector2[] path = { start, new Vector2(31f, 1f), new Vector2(31f, 21f) };
        Vector2 truth = start, world = Vector2.zero;
        for (int leg = 1; leg < path.Length; leg++)
        {
            Vector2 direction = (path[leg] - path[leg - 1]).normalized;
            int frames = Mathf.RoundToInt(Vector2.Distance(path[leg - 1], path[leg]) / pace);
            for (int f = 0; f < frames; f++)
            {
                truth += direction * pace;
                world += ToWorld(direction * pace, bias) * scale;
                Vector2 camera = world + new Vector2(Gaussian(random), Gaussian(random)) * jitter;
                filter.Follow(camera, true);
                Vector2 shown = filter.Current(camera);
                Assert.IsTrue(walkable.IsWalkable(filter.Estimate), $"estimate {filter.Estimate} in a wall (truth {truth})");
                Assert.IsTrue(walkable.IsWalkable(shown), $"shown {shown} in a wall (truth {truth})");
            }
        }

        // Dead reckoning alone (the AR walk laid on the plan as it is) ends this far off; the walls bring it back.
        Vector2 reckoned = start + world;
        Assert.Greater(Vector2.Distance(reckoned, truth), 2f, "the test walk should be hard without walls");
        float error = Vector2.Distance(filter.Estimate, truth);
        Assert.Less(error, 1.5f, $"ended {error:F2} m off: estimate {filter.Estimate}, truth {truth}, " +
                                 $"heading {filter.HeadingOffset:F1}, scale {filter.Scale:F3}, recoveries {filter.Recoveries}");
        // A walk that fits the building never wipes the cloud out, so it never asks to re-align.
        Assert.AreEqual(0, filter.RecentRecoveries);
    }

    [Test]
    public void AWalkThatDoesntFitTheBuildingAsksToRealign()
    {
        // Told the walk starts in the north leg, but it heads east as if from the east leg: straight into the wall.
        Walkable walkable = Corner();
        var filter = new PositionFilter(walkable, PositionFilter.DefaultCount, 21);
        filter.Reset(new Vector2(31f, 10f), Vector2.zero, 0f);
        for (int i = 1; i <= 250; i++)
        {
            filter.Follow(new Vector2(i * 0.04f, 0f), true);
            Assert.IsTrue(walkable.IsWalkable(filter.Estimate), $"estimate {filter.Estimate} in a wall");
        }
        Assert.GreaterOrEqual(filter.RecentRecoveries, 2);
    }

    [Test]
    public void LostTrackingAndAPoseJumpKeepTheEstimateOnTheFloor()
    {
        Walkable walkable = Hallway();
        var filter = new PositionFilter(walkable, PositionFilter.DefaultCount, 22);
        filter.Reset(new Vector2(1f, 1f), Vector2.zero, 0f);
        Vector2 truth = new Vector2(1f, 1f);
        for (int i = 1; i <= 600; i++)
        {
            truth.x += 0.04f;
            // Lost for 3 s from the 5 m mark; from 15 m on the pose reads 1.4 m off (ARCore relocalized).
            bool tracking = i < 125 || i >= 215;
            Vector2 camera = new Vector2(i * 0.04f, 0f) + (i >= 375 ? new Vector2(0f, 1.4f) : Vector2.zero);
            filter.Follow(camera, tracking);
            Assert.IsTrue(walkable.IsWalkable(filter.Estimate), $"estimate {filter.Estimate} in a wall at frame {i}");
        }
        Assert.Less(Vector2.Distance(filter.Estimate, truth), 1.5f, $"estimate {filter.Estimate}, truth {truth}");
    }

    [Test]
    public void ASightingPullsASpreadCloudToTheUser()
    {
        Walkable walkable = Hallway();
        var filter = new PositionFilter(walkable, PositionFilter.DefaultCount, 11);
        filter.Reset(new Vector2(8f, 1f), Vector2.zero, 0f, 4f, 2f);
        Vector2 truth = new Vector2(10f, 1f), sign = new Vector2(12f, 2.1f), normal = new Vector2(0f, -1f);

        Vector2 toSign = sign - truth;
        Assert.IsTrue(filter.AddSignObservation(PositionFilter.Heading(toSign), toSign.magnitude, 0.3f, sign, normal));
        Assert.Less(Vector2.Distance(filter.Estimate, truth), 0.75f, $"estimate {filter.Estimate}");
        Assert.IsTrue(walkable.IsWalkable(filter.Estimate));
    }

    [Test]
    public void OneContradictingSightingIsAMisreadTwoStartAgainFromTheSign()
    {
        Walkable walkable = Hallway();
        var filter = new PositionFilter(walkable, PositionFilter.DefaultCount, 12);
        filter.Reset(new Vector2(4f, 1f), Vector2.zero, 0f, 0.3f, 2f);
        Vector2 before = filter.Estimate;
        Vector2 truth = new Vector2(10f, 1f), sign = new Vector2(12f, 2.1f), normal = new Vector2(0f, -1f);
        Vector2 toSign = sign - truth;

        Assert.IsFalse(filter.AddSignObservation(PositionFilter.Heading(toSign), toSign.magnitude, 0.3f, sign, normal));
        Assert.AreEqual(before, filter.Estimate);
        // The second: the filter starts again on the sign's ring, any heading (one sign can't say where on the ring)...
        Assert.IsTrue(filter.AddSignObservation(PositionFilter.Heading(toSign), toSign.magnitude, 0.3f, sign, normal));
        Assert.AreEqual(180f, filter.HeadingLimit);
        // ...and a second sign seen from the same spot says where.
        Vector2 other = new Vector2(7f, 2.1f), toOther = other - truth;
        Assert.IsTrue(filter.AddSignObservation(PositionFilter.Heading(toOther), toOther.magnitude, 0.3f, other, normal));
        Assert.Less(Vector2.Distance(filter.Estimate, truth), 0.75f, $"estimate {filter.Estimate}");
    }

    [Test]
    public void ASignBehindItsWallExplainsNothing()
    {
        Walkable walkable = Hallway();
        var filter = new PositionFilter(walkable, PositionFilter.DefaultCount, 13);
        filter.Reset(new Vector2(10f, 1f), Vector2.zero, 0f, 0.3f, 2f);
        // The same spot and bearing, but the sign faces away from the hallway (it's in the room behind).
        Vector2 sign = new Vector2(12f, 2.1f), away = new Vector2(0f, 1f);
        Vector2 toSign = sign - new Vector2(10f, 1f);
        Assert.IsFalse(filter.AddSignObservation(PositionFilter.Heading(toSign), toSign.magnitude, 0.3f, sign, away));
    }

    [Test]
    public void WithoutAWalkableMapItIsDeadReckoning()
    {
        var filter = new PositionFilter(null, 50, 14);
        filter.Reset(Vector2.zero, Vector2.zero, 90f, 0f, 0f);
        // Plan turned 90 degrees: world +x is plan +z.
        for (int i = 1; i <= 40; i++)
            filter.Follow(new Vector2(i * 0.1f, 0f), true);
        Assert.Less(Vector2.Distance(filter.Estimate, new Vector2(0f, 4f)), 0.6f, $"estimate {filter.Estimate}");
    }

    // Replays walks logged on the phone (PoseLog) from Assets/Tests/EditMode/Walks/*.csv, against the floor's real
    // walkable map: the estimate never leaves the floor, and a walk that arrived ends near its door.
    [Test]
    public void ReplayLoggedWalks()
    {
        string folder = Path.Combine(Application.dataPath, "Tests", "EditMode", "Walks");
        string[] logs = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.csv") : new string[0];
        string walkablePath = Path.Combine(Application.dataPath, "Navigation", "Data", "ba1-floor1-walkable.bytes");
        if (logs.Length == 0 || !File.Exists(walkablePath))
            Assert.Ignore("No walk logs in Tests/EditMode/Walks, or no walkable map.");

        Walkable walkable = Walkable.Load(File.ReadAllBytes(walkablePath));
        FloorPlan plan = JsonUtility.FromJson<FloorPlan>(
            File.ReadAllText(Path.Combine(Application.dataPath, "Navigation", "Data", "ba1-floor1.json")));
        foreach (string log in logs)
            Replay(log, walkable, plan);
    }

    static void Replay(string log, Walkable walkable, FloorPlan plan)
    {
        string name = Path.GetFileName(log);
        PositionFilter filter = null;
        Vector2 end = Vector2.zero;
        bool arrived = false;
        var landmarks = new List<Vector2>();
        var normals = new List<Vector2>();
        foreach (string line in File.ReadLines(log))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] c = line.Split(',');
            switch (c[0])
            {
                case "b":   // b,t,yaw,camx,camz,x,z,endx,endz
                    filter = new PositionFilter(walkable, PositionFilter.DefaultCount, 1);
                    filter.Reset(new Vector2(F(c[5]), F(c[6])), new Vector2(F(c[3]), F(c[4])), F(c[2]));
                    end = new Vector2(F(c[7]), F(c[8]));
                    arrived = false;
                    break;
                case "r":   // r,t,endx,endz: rerouted, maybe to another door
                    end = new Vector2(F(c[2]), F(c[3]));
                    break;
                case "f":   // f,t,state,tracking,cx,cy,cz,...
                    if (filter == null)
                        break;
                    var camera = new Vector2(F(c[4]), F(c[6]));
                    filter.Follow(camera, c[3] == "1");
                    Assert.IsTrue(walkable.IsWalkable(filter.Estimate), $"{name} t={c[1]}: estimate {filter.Estimate} in a wall");
                    break;
                case "s":   // s,t,text,bearing,range,sigma,used,...
                    if (filter == null || plan?.signs == null)
                        break;
                    landmarks.Clear();
                    normals.Clear();
                    foreach (PlanSign sign in plan.signs)
                    {
                        if (string.Equals(sign.text, c[2], StringComparison.OrdinalIgnoreCase))
                        {
                            landmarks.Add(sign.Position);
                            normals.Add(sign.Normal);
                        }
                    }
                    filter.AddSignObservation(F(c[3]), F(c[4]), F(c[5]), landmarks, normals);
                    break;
                case "e":
                    arrived |= c.Length > 2 && c[2] == "arrived";
                    break;
            }
        }
        if (filter != null && arrived)
        {
            float error = Vector2.Distance(filter.Estimate, end);
            Assert.Less(error, ArriveDistance + 1.5f, $"{name}: arrived {error:F2} m from the door");
        }
    }

    static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    static float Gaussian(System.Random random)
    {
        double u = 1.0 - random.NextDouble(), v = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v));
    }
}
