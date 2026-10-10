using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// The floors the app holds: each a set of Data/<floor>* files from Tools/<floor>/make_data.py, all in the same plan
// metres as the one campus map. The scene is built for DefaultFloor's (the path properties read s_Floor); every
// floor goes to NavigationScreen.floors, for the app to swap between them when another building is tapped.
public static partial class BuildNavigateScene
{
    const string DefaultFloor = "ba1-floor1";
    const string CampusName = "ucf-campus";
    static readonly string[] Floors = { "ba1-floor1", "hec-floor1" };

    static string s_Floor = DefaultFloor;

    static bool UseFloor(string floor)
    {
        if (System.Array.IndexOf(Floors, floor) < 0)
        {
            Debug.LogError($"[Navigation] No floor \"{floor}\": pick one of {string.Join(", ", Floors)}.");
            return false;
        }
        s_Floor = floor;
        return true;
    }

    // Every floor's files, each checked as the scene's own floor is, with its map pictures in the day and night
    // themes. Null, with the reason logged, if any is missing or bad.
    static FloorAssets[] LoadFloors(string dayTheme)
    {
        var floors = new List<FloorAssets>();
        try
        {
            foreach (string floor in Floors)
            {
                UseFloor(floor);
                var plan = AssetDatabase.LoadAssetAtPath<TextAsset>(PlanJsonPath);
                if (plan == null)
                {
                    Debug.LogError($"[Navigation] Missing {PlanJsonPath}: run Tools/{floor}/make_data.py.");
                    return null;
                }
                if (!ValidatePlan(plan))
                    return null;
                var assets = new FloorAssets
                {
                    building = JsonUtility.FromJson<FloorPlan>(plan.text).building,
                    plan = plan,
                    placeMap = AssetDatabase.LoadAssetAtPath<TextAsset>(PlaceMapPath),
                    walls = AssetDatabase.LoadAssetAtPath<TextAsset>(WallsPath),
                    walkable = AssetDatabase.LoadAssetAtPath<TextAsset>(WalkablePath),
                    dayMap = ImportBasemap(dayTheme),
                    nightMap = ImportBasemap(NightTheme),
                };
                if (assets.dayMap == null || assets.nightMap == null)
                {
                    Debug.LogError($"[Navigation] Missing a {floor} map picture ({dayTheme} or {NightTheme}) in {DataFolder}.");
                    return null;
                }
                floors.Add(assets);
            }
        }
        finally
        {
            UseFloor(DefaultFloor);
        }
        return floors.ToArray();
    }
}
