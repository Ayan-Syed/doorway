using System;
using UnityEngine;

// One building's floor as the app holds it: its plan, place map, walls, walkable map and map pictures (Data/<floor>*,
// from Tools/<floor>/make_data.py). Every floor is in the same plan metres as the campus map, so one is swapped for
// another without moving the map (NavigationScreen.SwitchFloor).
[Serializable]
public class FloorAssets
{
    [Tooltip("The plan's building, as the campus map names it: tapping that building loads this floor.")]
    public string building;
    public TextAsset plan;
    public TextAsset placeMap;
    public TextAsset walls;
    public TextAsset walkable;
    [Tooltip("The filled map in the day theme and the night theme.")]
    public Texture2D dayMap;
    public Texture2D nightMap;
}
