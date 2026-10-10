using System.Linq;
using NUnit.Framework;

// Stress walks (SyntheticWalks.Stress): a world unlike the plan, depth, ARCore and the user at their typical worst.
// Each pinned seed reproduced one way the localizer used to end up sure of a wrong place; the batch holds the typical
// tier to its targets.
public class LocalizerStressTests
{
    // Seeds on the real floor (EvaluationData.StressFloor), all in the typical tier (seed % 3 == 1).
    static readonly (int seed, string what)[] Pinned =
    {
        (52, "a false plate, after a snap at the wrong door and an ARCore loss"),
        (61, "two false plates and five ARCore losses"),
        (394, "a misread plate and seven ARCore losses"),
        (361, "a misread plate after a snap at the wrong door"),
        (115, "a snap at the wrong door, then five ARCore losses"),
        (73, "a snap tapped 3 m on from the door"),
        (64, "two ARCore losses, walking on by steps"),
        (112, "a snap at an entrance by a room door 3 m away, which was the door really (now a candidate)"),
    };

    static ReplayEvaluator.StressWalkResult Replay(EvaluationData data, int seed)
    {
        var tier = (SyntheticWalks.Tier)(seed % 3);
        var lines = SyntheticWalks.Stress(data.StressFloor(), tier, seed, out SyntheticWalks.StressInfo info);
        var walk = new ReplayEvaluator.StressWalk { name = $"stress-{seed:D4}", tier = tier, info = info.ToString(), lines = lines };
        return ReplayEvaluator.ReplayStress(data.plan, data.walkable, data.field, data.walls, LocalizerEngine.Config.Full, walk);
    }

    static EvaluationData Data()
    {
        EvaluationData data = EvaluationData.Load();
        if (data == null)
            Assert.Ignore("The floor's plan, walkable map or walls aren't there.");
        return data;
    }

    [Test]
    public void PinnedStressWalksAreNeverSureOfAWrongPlace()
    {
        EvaluationData data = Data();
        foreach (var (seed, what) in Pinned)
        {
            ReplayEvaluator.StressWalkResult r = Replay(data, seed);
            float worst = r.localizedErrors.Count > 0 ? r.localizedErrors.Max() : 0f;
            Assert.AreEqual(0, r.wrong, $"seed {seed} ({what}): Localized {worst:F1} m off | {r.info}");
            Assert.AreEqual(0, r.violations, $"seed {seed} ({what}): estimate in a wall");
        }
    }

    // Easy walks where one misread or false plate (or two at once that couldn't both be right) used to start the cloud
    // again on the wrong plate's rings, tens of metres off; without plates they never strayed 3 m.
    [TestCase(393)]
    [TestCase(591)]
    [TestCase(330)]
    [TestCase(444)]
    public void AWrongPlateInEasyConditionsMovesNothingFar(int seed)
    {
        ReplayEvaluator.StressWalkResult r = Replay(Data(), seed);
        Assert.Less(r.max, 3f, $"seed {seed}: {r.max:F1} m off at worst | {r.info}");
    }

    // ARCore's scan misfit sent a right cloud Lost, and the next plate (a poster) started it again 16 m off: a minute
    // to recover. A contradicting plate while Lost now takes half the cloud, not all of it.
    [Test]
    public void APosterWhileLostDoesNotThrowARightCloudAway()
    {
        ReplayEvaluator.StressWalkResult r = Replay(Data(), 379);
        Assert.Less(r.max, 5f, $"{r.max:F1} m off at worst | {r.info}");
        Assert.IsTrue(r.recoveries.All(x => x < 5f), $"recoveries {string.Join(", ", r.recoveries)} s");
    }

    // With their back to the door the user sees the plates across the hallway, not their own: a nearby room's plate
    // read close by is no sign of the wrong door unless it fits standing at that door. A plain "other room's plate
    // within 3 m" rule moved these right snaps to wrong doors.
    [TestCase(199)]
    [TestCase(288)]
    [TestCase(418)]
    public void APlateAcrossTheHallwayDoesNotMoveARightSnap(int seed)
    {
        EvaluationData data = Data();
        var tier = (SyntheticWalks.Tier)(seed % 3);
        var lines = SyntheticWalks.Stress(data.StressFloor(), tier, seed, out SyntheticWalks.StressInfo info);
        var engine = new LocalizerEngine(data.plan, data.walkable, data.field, data.walls, LocalizerEngine.Config.Full, 1);
        var feeder = new ReplayEvaluator.Feeder(engine);
        foreach (string line in lines)
            feeder.Feed(line, out _, out _);
        Assert.IsFalse(info.wrongSnap);
        Assert.AreNotEqual(LocalizerEngine.SnapCheck.Corrected, engine.LastSnapCheck, $"corrected to {engine.SnapCheckText}");
    }

    [Test]
    public void TypicalStressWalksMeetTheTargets()
    {
        EvaluationData data = Data();
        var results = Enumerable.Range(0, 40).Select(k => Replay(data, 3 * k + 1)).ToList();
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
