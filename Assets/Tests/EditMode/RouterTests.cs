using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class RouterTests
{
    const string PlanPath = "Assets/Navigation/Data/ba1-floor1.json";

    static FloorPlan Shipped()
    {
        var json = AssetDatabase.LoadAssetAtPath<TextAsset>(PlanPath);
        Assert.IsNotNull(json, $"Missing {PlanPath}");
        return FloorPlan.Load(json);
    }

    // A plan made in code: nodes as (id, x, z), links as "a-b", and one place whose doors are at the given nodes,
    // each facing +z.
    static FloorPlan Plan(IEnumerable<(string id, float x, float z)> nodes, string[] links, params string[] doors)
    {
        var plan = new FloorPlan();
        foreach (var (id, x, z) in nodes)
            plan.nodes.Add(new PlanNode { id = id, x = x, z = z });
        foreach (string link in links)
        {
            string[] ends = link.Split('-');
            plan.edges.Add(new PlanEdge { a = ends[0], b = ends[1] });
        }
        var place = new PlanPlace { id = "goal", name = "Goal", kind = "room" };
        foreach (string door in doors)
            place.doors.Add(new PlanDoor { node = door, dx = 0f, dz = 1f });
        plan.places.Add(place);
        return plan;
    }

    static Maneuver[] Maneuvers(Route route) => route.turns.Select(t => Route.Classify(t.angle)).ToArray();

    [Test]
    public void TurningFromEastToNorthIsALeft()
    {
        FloorPlan plan = Plan(new[] { ("a", 0f, 0f), ("b", 10f, 0f), ("c", 10f, 10f) }, new[] { "a-b", "b-c" }, "c");
        Route route = new Router(plan).FindRoute("a", plan.places[0]);
        Assert.AreEqual(20f, route.length, 1e-3f);
        CollectionAssert.AreEqual(new[] { Maneuver.Left }, Maneuvers(route));
        Assert.AreEqual(10f, route.turns[0].distance, 1e-3f);
    }

    [Test]
    public void TurningFromEastToSouthIsARight()
    {
        FloorPlan plan = Plan(new[] { ("a", 0f, 0f), ("b", 10f, 0f), ("c", 10f, -10f) }, new[] { "a-b", "b-c" }, "c");
        CollectionAssert.AreEqual(new[] { Maneuver.Right }, Maneuvers(new Router(plan).FindRoute("a", plan.places[0])));
    }

    // Two 105-degree left bends a metre apart turn 210 degrees left, which a walker makes as 150 degrees right.
    [Test]
    public void TwoCloseBendsReadAsTheirNetTurn()
    {
        Vector2 b = new Vector2(5f, 0f);
        Vector2 c = b + new Vector2(Mathf.Cos(105f * Mathf.Deg2Rad), Mathf.Sin(105f * Mathf.Deg2Rad));
        Vector2 d = c + 5f * new Vector2(Mathf.Cos(210f * Mathf.Deg2Rad), Mathf.Sin(210f * Mathf.Deg2Rad));
        FloorPlan plan = Plan(new[] { ("a", 0f, 0f), ("b", b.x, b.y), ("c", c.x, c.y), ("d", d.x, d.y) },
            new[] { "a-b", "b-c", "c-d" }, "d");
        Route route = new Router(plan).FindRoute("a", plan.places[0]);
        CollectionAssert.AreEqual(new[] { Maneuver.SharpRight }, Maneuvers(route));
        Assert.Less(Mathf.Abs(route.turns[0].angle), 180f);
    }

    // A hallway that jogs a little one way and back within a few metres is no turn at all.
    [Test]
    public void AJogIsNotATurn()
    {
        FloorPlan plan = Plan(new[] { ("a", 0f, 0f), ("b", 10f, 0f), ("c", 13f, 1.5f), ("d", 25f, 1.5f) },
            new[] { "a-b", "b-c", "c-d" }, "d");
        Assert.IsEmpty(new Router(plan).FindRoute("a", plan.places[0]).turns);
    }

    [Test]
    public void NoWayThereIsNull()
    {
        FloorPlan plan = Plan(new[] { ("a", 0f, 0f), ("b", 5f, 0f), ("c", 20f, 0f), ("d", 25f, 0f) },
            new[] { "a-b", "c-d" }, "d");
        var router = new Router(plan);
        Assert.IsNull(router.FindRoute("a", plan.places[0]));
        Assert.IsNull(router.FindRoute(new Vector2(1f, 1f), plan.places[0]));
    }

    [Test]
    public void StartingAtTheDoorIsAlreadyThere()
    {
        FloorPlan plan = Plan(new[] { ("a", 0f, 0f), ("b", 5f, 0f) }, new[] { "a-b" }, "a");
        Route route = new Router(plan).FindRoute("a", plan.places[0]);
        Assert.AreEqual(0f, route.length, 1e-5f);
        Assert.AreEqual(2, route.points.Count);
        Assert.IsEmpty(route.turns);
    }

    // Two parallel hallways 3 m apart with a wall between, joined at their east ends. Someone in the south one,
    // nearer the north one as the crow flies, must join the south one, through no wall; without a Walkable the
    // router can only take the nearest.
    [Test]
    public void LeadInDoesNotGoThroughAWall()
    {
        FloorPlan plan = Plan(new[] { ("n0", 0f, 0f), ("n1", 10f, 0f), ("s1", 10f, -3f), ("s0", 0f, -3f) },
            new[] { "n0-n1", "n1-s1", "s1-s0" }, "s0");
        // 0.5 m cells from x = -1, z = 1 down to z = -4; a wall along z -0.5 to -1 from the west end to x = 9.
        var rows = new string[10];
        for (int j = 0; j < rows.Length; j++)
        {
            var row = new char[26];
            for (int i = 0; i < row.Length; i++)
                row[i] = j == 3 && i < 20 ? '#' : '.';
            rows[j] = new string(row);
        }
        Walkable ground = Walkable.FromRows(-1f, 1f, 0.5f, rows);
        var user = new Vector2(5f, -1.2f);

        Route blind = new Router(plan).FindRoute(user, plan.places[0]);
        Assert.AreEqual(0f, Vector2.Distance(blind.points[1], new Vector2(5f, 0f)), 1e-3f);

        Route seeing = new Router(plan, ground).FindRoute(user, plan.places[0]);
        Assert.AreEqual(0f, Vector2.Distance(seeing.points[1], new Vector2(5f, -3f)), 1e-3f);
        Assert.AreEqual(1.8f + 5f, seeing.length, 1e-3f);
    }

    [Test]
    public void APlanMadeInCodeNeedsNoLoad()
    {
        FloorPlan plan = Plan(new[] { ("a", 0f, 0f), ("b", 5f, 0f) }, new[] { "a-b" }, "b");
        Assert.AreEqual(1, plan.NodeIndex("b"));
        plan.nodes.Add(new PlanNode { id = "c", x = 9f, z = 0f });
        Assert.AreEqual(2, plan.NodeIndex("c"));
    }

    // Golden routes on the shipped floor (Tools/tests/test_routes.py walks the same ones in Python).
    [TestCase("p110", "113", 6.07f, new Maneuver[0])]
    [TestCase("p113", "110", 6.07f, new Maneuver[0])]
    [TestCase("p107w", "elev-a", 15.17f, new[] { Maneuver.Right })]
    [TestCase("p113", "121", 47.91f, new[] { Maneuver.Right, Maneuver.Left, Maneuver.SlightRight, Maneuver.SlightRight })]
    [TestCase("p121s", "stair-120", 8.23f, new Maneuver[0])]
    public void GoldenRoute(string from, string to, float length, Maneuver[] turns)
    {
        FloorPlan plan = Shipped();
        Route route = new Router(plan).FindRoute(from, plan.places.Find(p => p.id == to));
        Assert.AreEqual(length, route.length, 0.15f);
        CollectionAssert.AreEqual(turns, Maneuvers(route));
    }

    [Test]
    public void EveryPlaceIsReachableFromEveryDoorWithoutATurnAroundMidRoute()
    {
        FloorPlan plan = Shipped();
        var router = new Router(plan);
        var starts = plan.places.SelectMany(p => p.doors).Select(d => d.node).Distinct().ToList();
        foreach (string start in starts)
        {
            foreach (PlanPlace place in plan.places)
            {
                if (place.doors.Any(d => d.node == start))
                    continue;
                Route route = router.FindRoute(start, place);
                Assert.IsNotNull(route, $"{start} to {place.id}");
                foreach (RouteTurn turn in route.turns)
                {
                    Assert.LessOrEqual(Mathf.Abs(turn.angle), 180f);
                    // A turn-around is right only where the destination's door is a few steps behind the start's.
                    if (Route.Classify(turn.angle) == Maneuver.TurnAround)
                        Assert.Less(route.length, 9f, $"{start} to {place.id}: turn around at {turn.distance:0.0} m");
                }
            }
        }
    }
}
