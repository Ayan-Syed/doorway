using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// The evaluator (Navigation > Evaluate Walks) as a test: 20 synthetic walks on the real floor, and every committed
// walk log, through each configuration; writes Builds/Eval and holds the full configuration to its targets.
public class LocalizerEvaluationTests
{
    [Test]
    public void EvaluateSyntheticWalks()
    {
        EvaluationData data = EvaluationData.Load();
        if (data == null)
            Assert.Ignore("The floor's plan, walkable map or walls are missing.");
        var summaries = ReplayEvaluator.Run(data.plan, data.walkable, data.field, data.walls, data.SyntheticWalkSet(),
            Path.Combine(EvaluationData.OutputFolder, "synthetic"), out _);
        Debug.Log(ReplayEvaluator.Table(summaries));
        var full = summaries.First(s => s.config == "full" && s.split == "all");
        var raw = summaries.First(s => s.config == "raw" && s.split == "all");
        Assert.AreEqual(0, full.violations, "an estimate on unwalkable floor");
        Assert.Less(full.median, 1.5f);
        Assert.Less(full.p75, raw.p75, "the full localizer should beat raw ARCore");
        Assert.IsTrue(File.Exists(Path.Combine(EvaluationData.OutputFolder, "synthetic", "summary.csv")));
    }

    [Test]
    public void EvaluateLoggedWalks()
    {
        var logged = EvaluationData.LoggedWalks();
        EvaluationData data = EvaluationData.Load();
        if (data == null || logged.Count == 0)
            Assert.Ignore($"No walk logs in {EvaluationData.WalksFolder} yet.");
        var summaries = ReplayEvaluator.Run(data.plan, data.walkable, data.field, data.walls, logged,
            Path.Combine(EvaluationData.OutputFolder, "logged"), out _);
        Debug.Log(ReplayEvaluator.Table(summaries));
        Assert.AreEqual(0, summaries.First(s => s.config == "full" && s.split == "all").violations);
    }
}
