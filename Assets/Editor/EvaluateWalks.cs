using System.IO;
using UnityEditor;
using UnityEngine;

// Navigation > Evaluate Walks: replays every walk log in Assets/Tests/EditMode/Walks, 20 synthetic walks over the
// real floor and StressWalks stress walks (a world unlike the plan, with depth, ARCore and the user at their worst;
// easy, typical and harsh in turn), through the localizer with each signal switched off in turn and the raw ARCore
// baseline; writes Builds/Eval/<set>/ and logs the tables. The first two are also run by LocalizerEvaluationTests.
public static class EvaluateWalks
{
    [MenuItem("Navigation/Evaluate Walks")]
    public static void Run()
    {
        EvaluationData data = EvaluationData.Load();
        if (data == null)
        {
            Debug.LogError("[Navigation] Evaluate Walks: the floor's plan, walkable map or walls are missing.");
            return;
        }
        var logged = EvaluationData.LoggedWalks();
        if (logged.Count > 0)
        {
            var summary = ReplayEvaluator.Run(data.plan, data.walkable, data.field, data.walls, logged,
                Path.Combine(EvaluationData.OutputFolder, "logged"), out _);
            Debug.Log($"[Navigation] {logged.Count} logged walks (odd places tune, even test):\n{ReplayEvaluator.Table(summary)}");
        }
        else
        {
            Debug.Log($"[Navigation] No walk logs in {EvaluationData.WalksFolder} yet.");
        }
        var synthetic = ReplayEvaluator.Run(data.plan, data.walkable, data.field, data.walls, data.SyntheticWalkSet(),
            Path.Combine(EvaluationData.OutputFolder, "synthetic"), out _);
        Debug.Log($"[Navigation] 20 synthetic walks on the real floor:\n{ReplayEvaluator.Table(synthetic)}");
        var stress = ReplayEvaluator.RunStress(data.plan, data.walkable, data.field,
            () => new WallMatcher(data.wallPoints, data.wallRings, data.walkable), data.StressWalkSet(StressWalks),
            Path.Combine(EvaluationData.OutputFolder, "stress"), out _);
        Debug.Log($"[Navigation] {StressWalks} stress walks:\n{ReplayEvaluator.StressTable(stress)}");
    }

    public const int StressWalks = 600;
}
