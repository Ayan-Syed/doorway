using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// Stress walks (SyntheticWalks.Stress): a world unlike the plan, depth, ARCore and the user at their typical worst.
// Each test takes the walks that carry one way the localizer used to end up sure of a wrong place (a false or
// misread plate, a wrong door, a stride or an ARCore scale it doesn't expect...) and replays each with several
// localizer seeds: the filter is chaotic, and floating point differs between the editor, the phone (IL2CPP) and
// the harness, so one replay of one walk proves little; how often a set of them goes wrong says how it behaves.
public class LocalizerStressTests
{
    const int Engines = 5;          // localizer seeds per walk
    const float WrongShown = 5f;    // a position shown as sure this far off is a failure

    static EvaluationData Data()
    {
        EvaluationData data = EvaluationData.Load();
        if (data == null)
            Assert.Ignore("The floor's plan, walkable map or walls aren't there.");
        return data;
    }

    // The first count walks of the tier (seeds 3k + tier) that carry the trigger.
    static List<int> SeedsWith(EvaluationData data, SyntheticWalks.Tier tier, Func<SyntheticWalks.StressInfo, bool> trigger,
        int count)
    {
        SyntheticWalks.Floor floor = data.StressFloor();
        var seeds = new List<int>();
        for (int k = 0; k < 300 && seeds.Count < count; k++)
        {
            int seed = 3 * k + (int)tier;
            SyntheticWalks.Stress(floor, tier, seed, out SyntheticWalks.StressInfo info);
            if (trigger(info))
                seeds.Add(seed);
        }
        Assert.AreEqual(count, seeds.Count, "not enough walks with the trigger");
        return seeds;
    }

    // Every walk replayed with every localizer seed.
    static List<ReplayEvaluator.StressWalkResult> Replays(EvaluationData data, IEnumerable<int> seeds)
    {
        SyntheticWalks.Floor floor = data.StressFloor();
        var results = new List<ReplayEvaluator.StressWalkResult>();
        foreach (int seed in seeds)
        {
            var tier = (SyntheticWalks.Tier)(seed % 3);
            var lines = SyntheticWalks.Stress(floor, tier, seed, out SyntheticWalks.StressInfo info);
            var walk = new ReplayEvaluator.StressWalk { name = $"stress-{seed:D4}", tier = tier, info = info.ToString(), lines = lines };
            for (int e = 1; e <= Engines; e++)
                results.Add(ReplayEvaluator.ReplayStress(data.plan, data.walkable, data.field, data.walls,
                    LocalizerEngine.Config.Full, walk, e));
        }
        return results;
    }

    static float ShownMax(ReplayEvaluator.StressWalkResult r) => r.localizedErrors.Count > 0 ? r.localizedErrors.Max() : 0f;

    static string Failures(IEnumerable<ReplayEvaluator.StressWalkResult> failed) =>
        string.Join("; ", failed.Select(r => $"{r.walk}: shown {ShownMax(r):F1} m off, {r.max:F1} m at worst"));

    // At most one replay in the set shown as sure WrongShown or more off.
    static void RarelyShownWrong(List<ReplayEvaluator.StressWalkResult> results, string what)
    {
        var failed = results.Where(r => ShownMax(r) > WrongShown).ToList();
        Assert.LessOrEqual(failed.Count, 1, $"{what}: {failed.Count} of {results.Count} replays | {Failures(failed)}");
        Assert.AreEqual(0, results.Sum(r => r.violations), $"{what}: an estimate in a wall");
    }

    static readonly Dictionary<string, Func<SyntheticWalks.StressInfo, bool>> Triggers =
        new Dictionary<string, Func<SyntheticWalks.StressInfo, bool>>
        {
            ["a false plate"] = i => i.falsePlates > 0,
            ["a misread plate"] = i => i.misreads > 0,
            ["a snap at the wrong door"] = i => i.wrongSnap,
            ["a snap tapped a few metres on"] = i => i.insideSnap,
            ["two or more ARCore losses"] = i => i.losses >= 2,
        };

    [TestCase("a false plate")]
    [TestCase("a misread plate")]
    [TestCase("a snap at the wrong door")]
    [TestCase("a snap tapped a few metres on")]
    [TestCase("two or more ARCore losses")]
    public void TypicalWalksWithATrapAreRarelySureOfAWrongPlace(string trap)
    {
        EvaluationData data = Data();
        RarelyShownWrong(Replays(data, SeedsWith(data, SyntheticWalks.Tier.Typical, Triggers[trap], 3)), trap);
    }

    // Easy walks with a wrong plate: one plate (or two at once that can't both be right) used to start the cloud again
    // tens of metres off, where without plates it never strayed 3 m. Now it stays close, sure or not.
    [Test]
    public void AWrongPlateInEasyConditionsMovesNothingFar()
    {
        EvaluationData data = Data();
        var results = Replays(data, SeedsWith(data, SyntheticWalks.Tier.Easy, i => i.falsePlates > 0 || i.misreads > 0, 4));
        var failed = results.Where(r => r.max > WrongShown).ToList();
        Assert.LessOrEqual(failed.Count, 1, $"{failed.Count} of {results.Count} replays | {Failures(failed)}");
    }

    // Lost, then a plate: back within 5 s of it (a poster read while Lost no longer throws a right cloud away).
    [Test]
    public void APlateWhileLostBringsTheUserBackQuickly()
    {
        EvaluationData data = Data();
        var results = Replays(data, SeedsWith(data, SyntheticWalks.Tier.Typical, i => i.losses >= 1 && i.falsePlates > 0, 3));
        var slow = results.Where(r => r.recoveries.Any(x => x > 5f)).ToList();
        Assert.LessOrEqual(slow.Count, 2, $"{slow.Count} of {results.Count} replays recovered slowly | " +
            string.Join("; ", slow.Select(r => $"{r.walk}: {string.Join(", ", r.recoveries.Select(x => x.ToString("F1")))} s")));
    }

    // With their back to the door the user sees the plates across the hallway, not their own: a nearby room's plate
    // read close by is no sign of the wrong door unless it fits standing at that door. A plain "other room's plate
    // within 3 m" rule moved right snaps to wrong doors.
    [Test]
    public void APlateAcrossTheHallwayDoesNotMoveARightSnap()
    {
        EvaluationData data = Data();
        SyntheticWalks.Floor floor = data.StressFloor();
        int corrected = 0, runs = 0;
        foreach (int seed in SeedsWith(data, SyntheticWalks.Tier.Typical, i => !i.wrongSnap, 5))
        {
            var lines = SyntheticWalks.Stress(floor, SyntheticWalks.Tier.Typical, seed, out _);
            for (int e = 1; e <= Engines; e++, runs++)
            {
                var engine = new LocalizerEngine(data.plan, data.walkable, data.field, data.walls, LocalizerEngine.Config.Full, e);
                var feeder = new ReplayEvaluator.Feeder(engine);
                foreach (string line in lines)
                    feeder.Feed(line, out _, out _);
                if (engine.LastSnapCheck == LocalizerEngine.SnapCheck.Corrected)
                    corrected++;
            }
        }
        Assert.LessOrEqual(corrected, 1, $"{corrected} of {runs} right snaps corrected to another door");
    }

    // A user whose stride is 0.85 m, not the 0.7 m assumed, losing ARCore now and then (so walking on by steps):
    // the step length is learnt from ARCore, and blind steps don't fall short.
    [Test]
    public void ALongerStrideIsLearntFromARCore()
    {
        EvaluationData data = Data();
        WithMismatch(m => { m.stepLength = 0.85f; return m; }, () =>
        {
            var seeds = SeedsWith(data, SyntheticWalks.Tier.Typical, i => i.losses >= 2, 3);
            SyntheticWalks.Floor floor = data.StressFloor();
            foreach (int seed in seeds)
            {
                var lines = SyntheticWalks.Stress(floor, SyntheticWalks.Tier.Typical, seed, out _);
                var engine = new LocalizerEngine(data.plan, data.walkable, data.field, data.walls, LocalizerEngine.Config.Full, 1);
                var feeder = new ReplayEvaluator.Feeder(engine);
                foreach (string line in lines)
                    feeder.Feed(line, out _, out _);
                Assert.That(engine.LearntStepLength, Is.InRange(0.75f, 0.95f), $"seed {seed}: learnt step");
            }
            RarelyShownWrong(Replays(data, seeds), "a 0.85 m stride");
        });
    }

    // ARCore measuring 8% short: until the scale is learnt the cloud's place along a hallway drifts; the scale term
    // (the scale's spread times the walk since the last wall across the way or plate) keeps it from being sure then.
    [Test]
    public void AShortARCoreScaleIsRarelySureOfAWrongPlace()
    {
        EvaluationData data = Data();
        WithMismatch(m => { m.arScale = -0.08f; return m; },
            () => RarelyShownWrong(Replays(data, new[] { 253, 373, 1, 4 }), "ARCore 8% short"));
    }

    static void WithMismatch(Func<SyntheticWalks.MismatchKnobs, SyntheticWalks.MismatchKnobs> set, Action run)
    {
        SyntheticWalks.Mismatch = set(SyntheticWalks.MismatchKnobs.None);
        try
        {
            run();
        }
        finally
        {
            SyntheticWalks.Mismatch = SyntheticWalks.MismatchKnobs.None;
        }
    }

    [Test]
    public void TypicalStressWalksMeetTheTargets()
    {
        EvaluationData data = Data();
        var results = Enumerable.Range(0, 40).Select(k => 3 * k + 1).Select(seed =>
        {
            var lines = SyntheticWalks.Stress(data.StressFloor(), SyntheticWalks.Tier.Typical, seed, out var info);
            var walk = new ReplayEvaluator.StressWalk { name = $"stress-{seed:D4}", tier = SyntheticWalks.Tier.Typical, info = info.ToString(), lines = lines };
            return ReplayEvaluator.ReplayStress(data.plan, data.walkable, data.field, data.walls, LocalizerEngine.Config.Full, walk);
        }).ToList();
        ReplayEvaluator.StressSummary s = ReplayEvaluator.SummarizeStress(results).Single();
        string table = ReplayEvaluator.StressTable(new[] { s });
        Assert.Less(s.p75, 1.5f, table);
        Assert.Less(s.lostShare, 0.05f, table);
        Assert.Less(s.wrongShare, 0.01f, table);
        Assert.AreEqual(0, s.violations, table);
        Assert.Less(s.localizedP95, 2f, table);                      // displayed error
        Assert.Less(s.localizedMax, 5f, table);
        Assert.That(s.calibration, Is.InRange(0.6f, 0.85f), table);  // Spread holds the error about 68% of the time
    }
}
