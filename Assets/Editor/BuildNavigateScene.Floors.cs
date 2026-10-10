using System;
using UnityEditor;
using UnityEngine;

// Which floor the app is built for. The app holds one floor: its plan, place grid, walls, walkable grid and maps
// (Data/<floor>*, from Tools/<floor>/make_data.py), with the campus map projected into that floor's plan metres.
// Every entry point picks it first: -floor <id> on the command line (BuildApk, RenderPreviews), else the editor's
// choice under Navigation > Floor, else BA1 floor 1. The tests always build BA1's.
public static partial class BuildNavigateScene
{
    const string DefaultFloor = "ba1-floor1";
    const string FloorPref = "Navigation.Floor";
    const string FloorMenu = "Navigation/Floor/";
    static readonly string[] Floors = { "ba1-floor1", "hec-floor1" };
    static readonly string[] FloorNames = { "BA1 floor 1", "HEC floor 1" };

    static string s_Floor = DefaultFloor;

    // BA1's campus map keeps its first name; another floor's is ucf-campus-<floor> (Tools/campus/campus.py --floor).
    static string CampusName => s_Floor == DefaultFloor ? "ucf-campus" : $"ucf-campus-{s_Floor}";

    static bool PickFloor() => UseFloor(Argument("-floor") ?? EditorPrefs.GetString(FloorPref, DefaultFloor));

    static bool UseFloor(string floor)
    {
        if (Array.IndexOf(Floors, floor) < 0)
        {
            Debug.LogError($"[Navigation] No floor \"{floor}\": pick one of {string.Join(", ", Floors)}.");
            return false;
        }
        s_Floor = floor;
        return true;
    }

    [MenuItem(FloorMenu + "BA1 floor 1")]
    static void ChooseBA1() => Choose(0);

    [MenuItem(FloorMenu + "BA1 floor 1", true)]
    static bool ShowBA1() => Show(0);

    [MenuItem(FloorMenu + "HEC floor 1")]
    static void ChooseHEC() => Choose(1);

    [MenuItem(FloorMenu + "HEC floor 1", true)]
    static bool ShowHEC() => Show(1);

    static void Choose(int i)
    {
        EditorPrefs.SetString(FloorPref, Floors[i]);
        Debug.Log($"[Navigation] Building for {FloorNames[i]} from now on: run Navigation > Build Navigate Scene.");
    }

    // Ticks the chosen floor in the menu.
    static bool Show(int i)
    {
        Menu.SetChecked(FloorMenu + FloorNames[i], EditorPrefs.GetString(FloorPref, DefaultFloor) == Floors[i]);
        return true;
    }
}
