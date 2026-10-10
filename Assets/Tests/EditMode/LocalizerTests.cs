using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// The Localizer's engine on the real floor (BA1 floor 1's walkable map, walls and plates), driven by synthetic walks
// in the phone's log format (SyntheticWalks): the same code the phone runs, deterministic. The truth comes with each
// walk (T rows), so errors are measured all along it, not just at checkpoints.
public class LocalizerTests
{
    // What the tests hold the engine to.
    const float MedianTarget = 1.5f, RecoverySeconds = 5f;

    // One replayed walk: the error to the truth all along, and what happened when.
    public sealed class Run
    {
        public readonly List<(double t, float error, LocalizerState state)> errors = new List<(double, float, LocalizerState)>();
        public readonly List<(double t, LocalizerState state)> states = new List<(double, LocalizerState)>();
        public double snapAt = double.NaN, firstLocalized = double.NaN, firstSignWhileLost = double.NaN;
        public double firstSignWhileUnsure = double.NaN;   // Lost or Converging
        public int violations, signsUsed;
        public LocalizerEngine engine;

        public float Final => errors.Count > 0 ? errors[errors.Count - 1].error : float.NaN;

        /// <summary>Seconds from t until the error first falls under limit (infinite if it never does).</summary>
        public double SecondsUntilWithin(double t, float limit)
        {
            foreach (var e in errors)
            {
                if (e.t >= t && e.error < limit)
                    return e.t - t;
            }
            return double.PositiveInfinity;
        }
    }

    public static Run Replay(EvaluationData data, IList<string> lines, LocalizerEngine.Config config)
    {
        var run = new Run { engine = new LocalizerEngine(data.plan, data.walkable, data.field, data.walls, config, 1) };
        var feeder = new ReplayEvaluator.Feeder(run.engine);
        double now = 0;
        run.engine.StateChanged += (state, why) =>
        {
            run.states.Add((now, state));
            if (state == LocalizerState.Localized && double.IsNaN(run.firstLocalized))
                run.firstLocalized = now;
        };
        run.engine.SignUsed += _ => run.signsUsed++;
        foreach (string line in lines)
        {
            bool lostBefore = run.engine.State == LocalizerState.Lost;
            if (line.StartsWith("s,", StringComparison.Ordinal) && lostBefore && double.IsNaN(run.firstSignWhileLost))
                run.firstSignWhileLost = double.Parse(line.Split(',')[1], CultureInfo.InvariantCulture);
            if (line.StartsWith("s,", StringComparison.Ordinal) && !double.IsNaN(run.snapAt) &&
                run.engine.State != LocalizerState.Localized && double.IsNaN(run.firstSignWhileUnsure))
                run.firstSignWhileUnsure = double.Parse(line.Split(',')[1], CultureInfo.InvariantCulture);
            if (!feeder.Feed(line, out string[] c, out double t))
                continue;
            now = t;
            switch (c[0])
            {
                case "n":
                    run.snapAt = t;
                    break;
                case "a":
                    if (run.engine.State != LocalizerState.Outdoors && !data.walkable.IsWalkable(run.engine.Position))
                        run.violations++;
                    break;
                case "T":
                    var truth = new Vector2(F(c[2]), F(c[3]));
                    run.errors.Add((t, Vector2.Distance(run.engine.Position, truth), run.engine.State));
                    break;
            }
        }
        return run;
    }

    static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    static EvaluationData Data()
    {
        EvaluationData data = EvaluationData.Load();
        if (data == null)
            Assert.Ignore("The floor's plan, walkable map or walls are missing.");
        return data;
    }

    static List<string> Walk(EvaluationData data, Vector2[] route, SyntheticWalks.Options options) =>
        SyntheticWalks.Walk(route, data.walkable, data.wallSegments, data.plan.signs, data.doorSpots, options);

    static float Median(IEnumerable<float> values) =>
        ReplayEvaluator.Percentile(values.OrderBy(v => v).ToList(), 0.5f);

    [Test]
    public void ASnapAtAnEntranceLocalizesQuickly()
    {
        EvaluationData data = Data();
        Assert.IsNotEmpty(data.entranceRoutes);
        var times = new List<double>();
        foreach (var (route, i) in data.entranceRoutes.Take(6).Select((r, i) => (r, i)))
        {
            var options = SyntheticWalks.Options.Default(500 + i);
            options.lossChance = 0f;
            Run run = Replay(data, Walk(data, route, options), LocalizerEngine.Config.Full);
            times.Add(run.firstLocalized - run.snapAt);
            float early = run.errors.Where(e => e.t - run.snapAt < 5.0).Max(e => e.error);
            Assert.Less(early, 1f, $"route {i}: {early:F2} m off in the first 5 s");
            Assert.AreEqual(0, run.violations);
        }
        // Most within 3 s (sure needs the heading within 8 degrees as well, and the scan is weighed every half metre);
        // a plain hallway takes longer, as the snap keeps a share a few metres on from the door (the door is often
        // tapped a few steps in) until the walk shows where along it the user is.
        times.Sort();
        string all = string.Join(" ", times.Select(t => t.ToString("F1")));
        Assert.Less(times[times.Count / 2], 3.0, all);
        Assert.Less(times[times.Count - 1], 10.0, all);
    }

    [Test]
    public void AHundredMetreWalkWithFiveDegreesOfBiasAndThreePercentScale()
    {
        EvaluationData data = Data();
        var errors = new List<float>();
        int walks = 0;
        foreach (var (route, i) in data.routes.Select((r, i) => (r, i)))
        {
            // There and back: one continuous walk of 100 m or more.
            float length = 0f;
            for (int k = 1; k < route.Length; k++)
                length += Vector2.Distance(route[k - 1], route[k]);
            if (length < 50f)
                continue;
            Vector2[] there = route.Concat(route.Reverse().Skip(1)).ToArray();
            var options = SyntheticWalks.Options.Default(600 + i);
            options.bias = 5f;
            options.scale = -0.03f;
            options.exact = true;
            Run run = Replay(data, Walk(data, there, options), LocalizerEngine.Config.Full);
            Assert.AreEqual(0, run.violations, $"route {i}: estimate in a wall");
            errors.AddRange(run.errors.Select(e => e.error));
            if (++walks == 5)
                break;
        }
        Assert.Greater(walks, 0, "no route of 50 m or more");
        float median = Median(errors);
        Assert.Less(median, MedianTarget, $"median {median:F2} m over {walks} walks");
    }

    [Test]
    public void AWrongEntranceSnapRecoversThroughWallsAndSigns()
    {
        EvaluationData data = Data();
        int recovered = 0, tried = 0;
        var finals = new List<float>();
        for (int i = 0; i < 10; i++)
        {
            var options = SyntheticWalks.Options.Default(700 + i);
            options.wrongSnap = 15f;
            options.lossChance = 0f;
            Run run = Replay(data, Walk(data, data.routes[i % data.routes.Count], options), LocalizerEngine.Config.Full);
            Assert.AreEqual(0, run.violations, $"walk {i}: estimate in a wall");
            finals.Add(run.Final);
            // Lost, or kept from Localized by a cloud gone wide: either way unsure, and a sign should settle it.
            // (A sign in the walk's last RecoverySeconds can't show whether it recovers in time: not counted.)
            if (double.IsNaN(run.firstSignWhileUnsure) ||
                run.errors[run.errors.Count - 1].t - run.firstSignWhileUnsure < RecoverySeconds)
                continue;
            tried++;
            if (run.SecondsUntilWithin(run.firstSignWhileUnsure, MedianTarget) <= RecoverySeconds)
                recovered++;
        }
        Assert.Greater(tried, 0, "no walk saw a sign while unsure");
        Assert.GreaterOrEqual(recovered, tried * 0.8f, $"{recovered} of {tried} recovered within {RecoverySeconds} s of a sign");
        Assert.Less(Median(finals), MedianTarget, $"final errors {string.Join(", ", finals.Select(f => f.ToString("F1")))}");
    }

    [Test]
    public void ATrackingLossIsBridgedBySteps()
    {
        EvaluationData data = Data();
        var errors = new List<float>();
        for (int i = 0; i < 10; i++)
        {
            var options = SyntheticWalks.Options.Default(800 + i);
            options.lossChance = 1f;
            Run run = Replay(data, Walk(data, data.routes[i % data.routes.Count], options), LocalizerEngine.Config.Full);
            Assert.AreEqual(0, run.violations, $"walk {i}: estimate in a wall");
            errors.AddRange(run.errors.Select(e => e.error));
        }
        Assert.Less(Median(errors), MedianTarget);
    }

    [Test]
    public void LostByAskingThenOnePlateAndSomeWalkingFindsTheUser()
    {
        EvaluationData data = Data();
        int recovered = 0, tried = 0;
        var after = new List<float>();
        for (int i = 0; i < 10; i++)
        {
            var options = SyntheticWalks.Options.Default(900 + i);
            options.relocalizeAt = 15f;
            options.lossChance = 0f;
            Run run = Replay(data, Walk(data, data.routes[i % data.routes.Count], options), LocalizerEngine.Config.Full);
            Assert.AreEqual(0, run.violations, $"walk {i}: estimate in a wall");
            if (double.IsNaN(run.firstSignWhileLost))
                continue;
            tried++;
            if (run.SecondsUntilWithin(run.firstSignWhileLost, MedianTarget) <= RecoverySeconds)
                recovered++;
            after.AddRange(run.errors.Where(e => e.t > run.firstSignWhileLost + 10.0).Select(e => e.error));
        }
        Assert.Greater(tried, 0, "no walk saw a sign while lost");
        Assert.GreaterOrEqual(recovered, tried * 0.8f, $"{recovered} of {tried} recovered within {RecoverySeconds} s of a sign");
        if (after.Count > 0)
            Assert.Less(Median(after), MedianTarget);
    }

    // A scan seen from a plan spot over two seconds of looking round, in a world turned by worldYaw from the plan.
    static int Scan(EvaluationData data, System.Random random, Vector2 at, float facing, float worldYaw, Vector2[] output)
    {
        var buffer = new ScanBuffer();
        for (int f = 0; f < 10; f++)
        {
            for (int k = 0; k < 40; k++)
            {
                float heading = facing + (k / 39f - 0.5f) * 60f + (f - 5) * 6f;
                Vector2 direction = PositionFilter.HeadingVector(heading);
                float hit = -1f;
                for (float s = 0.05f; s <= 6f; s += 0.05f)
                {
                    if (!data.walkable.IsWalkable(at + direction * s))
                    {
                        hit = s;
                        break;
                    }
                }
                if (hit < 0f)
                    continue;
                float range = hit + Gaussian(random) * 0.05f;
                if (random.NextDouble() < 0.2)
                    range = 0.4f + (float)random.NextDouble() * (hit - 0.4f);   // 20% outliers
                if (range >= 0.4f)
                    buffer.Add(f * 0.2, PositionFilter.ToPlan(direction * range, -worldYaw));
            }
        }
        return buffer.Build(2.0, output);
    }

    static float Gaussian(System.Random random)
    {
        double u = 1.0 - random.NextDouble(), v = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v));
    }

    // A snap believed 25 degrees off, standing 0.3 m off the door spot, then 10 s of depth scans while looking
    // round (the scan only: no walls, plates or walking), through the engine as the phone runs it. Over every route
    // start and two seeds. A start whose view has nothing within the depth's 6 m says nothing about the heading:
    // those are counted apart (and must be few).
    [Test]
    public void DepthScansRecoverTheHeadingFrom25DegreesOff()
    {
        EvaluationData data = Data();
        var headings = new List<float>();
        var sideways = new List<float>();
        int blind = 0;
        var starts = data.routes.Concat(data.entranceRoutes).Where(r => r.Length > 1).ToList();
        for (int s = 0; s < starts.Count; s++)
        {
            for (int seed = 0; seed < 2; seed++)
            {
                var random = new System.Random(1000 * seed + s);
                Vector2 truth = starts[s][0];
                Vector2 ahead = (starts[s][1] - truth).normalized, across = new Vector2(-ahead.y, ahead.x);
                float facing0 = PositionFilter.Heading(ahead);
                var engine = new LocalizerEngine(data.plan, data.walkable, data.field, data.walls,
                    LocalizerEngine.Config.Full, seed + 1);
                engine.Snap(0, truth - across * 0.3f, (s + seed) % 2 == 0 ? 25f : -25f, Vector2.zero);
                var buffer = new ScanBuffer();
                var points = new Vector2[ScanBuffer.Target];
                int seen = 0;
                for (int u = 0; u < 50; u++)
                {
                    double t = 0.2 * (u + 1);
                    float facing = facing0 + (u % 5 - 2) * 20f;
                    engine.Pose(t, Vector2.zero, facing, true);   // the AR world is the plan here: the truth is yaw 0
                    int n = Look(data, random, truth, facing, t, buffer, points);
                    if (n >= ScanMatcher.MinPoints)
                        seen++;
                    engine.Scan(t, points, n);
                }
                if (seen < 5)
                {
                    blind++;
                    continue;
                }
                headings.Add(Mathf.Abs(Mathf.DeltaAngle(engine.Filter.Yaw, 0f)));
                sideways.Add(Mathf.Abs(Vector2.Dot(engine.Filter.Estimate - truth, across)));
                Assert.IsTrue(data.walkable.IsWalkable(engine.Filter.Estimate));
            }
        }
        headings.Sort();
        sideways.Sort();
        string spread = $"{headings.Count} runs ({blind} blind): median {ReplayEvaluator.Percentile(headings, 0.5f):F2}, " +
                        $"p95 {ReplayEvaluator.Percentile(headings, 0.95f):F2}, max {headings.Last():F2} degrees";
        Assert.Less(blind, (headings.Count + blind) / 4, spread);
        Assert.Less(ReplayEvaluator.Percentile(headings, 0.5f), 2f, spread);
        Assert.Less(ReplayEvaluator.Percentile(headings, 0.95f), 5f, spread);
        Assert.Less(ReplayEvaluator.Percentile(sideways, 0.5f), 0.3f, $"sideways median {ReplayEvaluator.Percentile(sideways, 0.5f):F2} m");
    }

    // One depth frame from a plan spot: 40 rays across 60 degrees, to the first wall within 6 m, with 5 cm noise and
    // 20% outliers short of the wall; into the buffer, and the buffer's scan out.
    static int Look(EvaluationData data, System.Random random, Vector2 at, float facing, double t, ScanBuffer buffer,
        Vector2[] output)
    {
        for (int f = 0; f < 2; f++)
        {
            for (int k = 0; k < 40; k++)
            {
                Vector2 direction = PositionFilter.HeadingVector(facing + (k / 39f - 0.5f) * 60f);
                float hit = -1f;
                for (float d = 0.05f; d <= 6f; d += 0.05f)
                {
                    if (!data.walkable.IsWalkable(at + direction * d))
                    {
                        hit = d;
                        break;
                    }
                }
                if (hit < 0f)
                    continue;
                float range = random.NextDouble() < 0.2 ? 0.4f + (float)random.NextDouble() * (hit - 0.4f)
                    : hit + Gaussian(random) * 0.05f;
                if (range >= 0.4f)
                    buffer.Add(t, direction * range);
            }
        }
        return buffer.Build(t, output);
    }

    [Test]
    public void AnAtriumWithFewWallsInRangeStaysOnTheFloor()
    {
        EvaluationData data = Data();
        // The walkable spot furthest from every wall.
        Vector2 open = default;
        float far = 0f;
        Walkable w = data.walkable;
        for (float x = w.left; x < w.left + w.width * w.cell; x += 0.5f)
        {
            for (float z = w.top - w.height * w.cell; z < w.top; z += 0.5f)
            {
                var p = new Vector2(x, z);
                if (w.IsWalkable(p) && data.field.Distance(p) > far)
                {
                    far = data.field.Distance(p);
                    open = p;
                }
            }
        }
        var random = new System.Random(6);
        var points = new Vector2[ScanBuffer.Target];
        var filter = new PositionFilter(w, 300, 4, 2000) { Adaptive = true };
        filter.Reset(open + new Vector2(0.5f, 0.5f), Vector2.zero, 0f, 1f, 10f, 45f);
        var matcher = new ScanMatcher(data.field, 2000);
        for (int u = 0; u < 25; u++)
            matcher.Weigh(filter, points, Scan(data, random, open, u * 14f, 0f, points));
        Assert.IsTrue(w.IsWalkable(filter.Estimate));
        Assert.Less(Vector2.Distance(filter.Estimate, open), 1f, $"estimate {filter.Estimate}, truth {open}");
    }

    [Test]
    public void ScanMatchingFitsTheTimeBudget()
    {
        EvaluationData data = Data();
        var random = new System.Random(7);
        var points = new Vector2[ScanBuffer.Target];
        Vector2 at = data.routes[0][0];
        int n = Scan(data, random, at, 0f, 0f, points);
        var full = new Vector2[ScanMatcher.MaxPoints];
        for (int j = 0; j < full.Length; j++)
            full[j] = points[j % n];
        var filter = new PositionFilter(data.walkable, 300, 1, 2000);
        var matcher = new ScanMatcher(data.field, 2000);
        matcher.Weigh(filter, full, full.Length);
        double total = 0;
        for (int i = 0; i < 20; i++)
        {
            filter.Reset(at, Vector2.zero, 0f);
            var watch = Stopwatch.StartNew();
            matcher.Weigh(filter, full, full.Length);
            total += watch.Elapsed.TotalMilliseconds;
        }
        // The phone's budget is 3 ms; a desktop editor is a few times faster, so this is the generous end.
        Assert.Less(total / 20, 3.0, $"{total / 20:F2} ms for 300 particles x 200 points");
    }
}
