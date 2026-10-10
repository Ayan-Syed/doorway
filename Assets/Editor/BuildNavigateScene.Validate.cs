using System;
using UnityEditor;
using UnityEngine;

// The app's data, checked before anything is built: the floor plan (FloorPlan.Validate), then the place grid, the
// walls, the walkable grid and the campus, each through the reader the app itself uses, and how they fit together
// (the place grid made for these places, every walkway point on walkable ground, the campus projected with this
// plan's geo fit). BuildApk stops here on bad data instead of shipping an app that fails on the phone.
public static partial class BuildNavigateScene
{
    // Null if every file reads and they agree; otherwise what's wrong, naming the file.
    public static string DataProblem()
    {
        var json = AssetDatabase.LoadAssetAtPath<TextAsset>(PlanJsonPath);
        return json == null ? $"Missing {PlanJsonPath}." : DataProblem(json);
    }

    static string DataProblem(TextAsset json)
    {
        return DataProblem(json.text, Bytes(PlaceMapPath), Bytes(WallsPath), Bytes(WalkablePath),
            Bytes(CampusGeometryPath), AssetDatabase.LoadAssetAtPath<TextAsset>(CampusInfoPath)?.text);
    }

    // The same checks on data in memory, so tests can feed it broken copies. The campus pair is optional (the map
    // then shows the floor alone); everything else must be there.
    public static string DataProblem(string planJson, byte[] places, byte[] walls, byte[] walkable,
        byte[] campus, string campusJson)
    {
        if (!FloorPlan.Validate(planJson, out string error))
            return $"{PlanJsonPath}: {error}";
        FloorPlan plan = JsonUtility.FromJson<FloorPlan>(planJson);
        if (places == null)
            return $"Missing {PlaceMapPath}.";
        if (walls == null)
            return $"Missing {WallsPath}.";
        if (walkable == null)
            return $"Missing {WalkablePath}.";
        if ((campus == null) != (campusJson == null))
            return $"Only one of {CampusGeometryPath} and {CampusInfoPath} is there.";
        try
        {
            FloorMap.ReadPlaceGrid(places, PlaceMapPath, plan, out _, out _);
            WallMesh.Parse(walls, WallsPath, out _, out _, out _);

            Walkable ground = Walkable.Load(walkable);
            foreach (PlanNode node in plan.nodes)
            {
                if (!ground.IsWalkable(node.Position))
                    return $"{WalkablePath}: walkway point \"{node.id}\" is on a cell nobody can stand on.";
            }

            if (campus != null)
            {
                CampusData data = CampusData.Parse(campus, campusJson, CampusGeometryPath);
                if (data.geo == null || !SameGeo(data.geo, plan.geo))
                    return $"{CampusGeometryPath}: projected with a different geo fit than {PlanJsonPath}; "
                           + $"run Tools/{s_Floor}/make_data.py --campus.";
            }
        }
        catch (FormatException e)
        {
            return e.Message;
        }
        return null;
    }

    static bool SameGeo(PlanGeo a, PlanGeo b)
    {
        return Mathf.Abs(a.lat - b.lat) < 1e-6f && Mathf.Abs(a.lon - b.lon) < 1e-6f
            && Mathf.Abs(a.angle - b.angle) < 1e-4f && Mathf.Abs(a.x - b.x) < 1e-4f && Mathf.Abs(a.z - b.z) < 1e-4f;
    }

    static byte[] Bytes(string path) => AssetDatabase.LoadAssetAtPath<TextAsset>(path)?.bytes;
}
