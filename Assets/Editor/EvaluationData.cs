using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// The real floor, loaded once for the evaluator and the localization tests: the plan, its walkable map, wall outlines,
// distance field and wall matcher, and routes between doors (for synthetic walks), all from Assets/Navigation/Data.
public sealed class EvaluationData
{
    const string Data = "Assets/Navigation/Data/";
    /// <summary>Walk logs to replay (PoseLog files pulled off the phone): small ones are committed here.</summary>
    public const string WalksFolder = "Assets/Tests/EditMode/Walks";
    public const string OutputFolder = "Builds/Eval";

    public FloorPlan plan;
    public Walkable walkable;
    public Vector2[] wallPoints;
    public int[] wallRings;
    public DistanceField field;
    public WallMatcher walls;
    public List<(Vector2 a, Vector2 b)> wallSegments;
    public Vector2[] doorSpots;
    public List<Vector2[]> routes;
    /// <summary>Routes from an entrance's door spot: where a snap happens.</summary>
    public List<Vector2[]> entranceRoutes;

    static EvaluationData s_Loaded;

    /// <summary>Null if the floor's data isn't all there (no walkable map yet, say).</summary>
    public static EvaluationData Load()
    {
        if (s_Loaded != null)
            return s_Loaded;
        var json = AssetDatabase.LoadAssetAtPath<TextAsset>(Data + "ba1-floor1.json");
        var walkableAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(Data + "ba1-floor1-walkable.bytes");
        var wallsAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(Data + "ba1-floor1-walls.bytes");
        if (json == null || walkableAsset == null || wallsAsset == null)
            return null;
        var d = new EvaluationData { plan = FloorPlan.Load(json), walkable = Walkable.Load(walkableAsset.bytes) };
        WallMesh.Parse(wallsAsset.bytes, wallsAsset.name, out d.wallRings, out d.wallPoints, out _);
        d.field = DistanceField.Build(d.walkable, d.wallPoints, d.wallRings);
        d.walls = new WallMatcher(d.wallPoints, d.wallRings, d.walkable);
        d.wallSegments = new List<(Vector2, Vector2)>();
        for (int r = 0, start = 0; r < d.wallRings.Length; start += d.wallRings[r], r++)
        {
            for (int k = 0; k < d.wallRings[r]; k++)
            {
                Vector2 a = d.wallPoints[start + k], b = d.wallPoints[start + (k + 1) % d.wallRings[r]];
                if (Vector2.Distance(a, b) >= WallMatcher.MinLength)
                    d.wallSegments.Add((a, b));
            }
        }
        d.doorSpots = d.plan.places.SelectMany(p => p.doors).Select(door => d.plan.NodePosition(door.node)).ToArray();
        SyntheticWalks.Routes(d.plan, d.walkable, out d.routes, out d.entranceRoutes);
        s_Loaded = d;
        return d;
    }

    /// <summary>20 synthetic walks over the real floor, as logs.</summary>
    public List<ReplayEvaluator.Walk> SyntheticWalkSet(int count = 20)
    {
        var walks = new List<ReplayEvaluator.Walk>();
        for (int i = 0; i < count; i++)
        {
            List<string> lines = SyntheticWalks.Walk(routes[i % routes.Count], walkable, wallSegments, plan.signs, doorSpots,
                SyntheticWalks.Options.Default(100 + i));
            walks.Add(new ReplayEvaluator.Walk { name = $"synthetic-{i:D2}", lines = lines });
        }
        return walks;
    }

    /// <summary>The floor as the stress walks see it: doors with their directions, exits, every route.</summary>
    public SyntheticWalks.Floor StressFloor() =>
        SyntheticWalks.StressFloor(plan, walkable, field, wallSegments, routes.Concat(entranceRoutes).ToList());

    /// <summary>count stress walks, seeds first.. on, the tiers in turn (seed % 3).</summary>
    public List<ReplayEvaluator.StressWalk> StressWalkSet(int count, int first = 0)
    {
        SyntheticWalks.Floor floor = StressFloor();
        var walks = new List<ReplayEvaluator.StressWalk>();
        for (int seed = first; seed < first + count; seed++)
        {
            var tier = (SyntheticWalks.Tier)(seed % 3);
            List<string> lines = SyntheticWalks.Stress(floor, tier, seed, out SyntheticWalks.StressInfo info);
            walks.Add(new ReplayEvaluator.StressWalk { name = $"stress-{seed:D4}", tier = tier, info = info.ToString(), lines = lines });
        }
        return walks;
    }

    /// <summary>The walk logs committed under WalksFolder.</summary>
    public static List<ReplayEvaluator.Walk> LoggedWalks()
    {
        var walks = new List<ReplayEvaluator.Walk>();
        if (!Directory.Exists(WalksFolder))
            return walks;
        foreach (string file in Directory.GetFiles(WalksFolder, "*.csv").OrderBy(f => f))
            walks.Add(new ReplayEvaluator.Walk { name = Path.GetFileNameWithoutExtension(file), lines = File.ReadAllLines(file) });
        return walks;
    }
}
