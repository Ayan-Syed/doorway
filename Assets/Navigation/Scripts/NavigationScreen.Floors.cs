using System;
using UnityEngine;

// The buildings with indoor maps: one floor is loaded at a time (the Navigator's plan, the maps, the Localizer's
// walls), and a tap on another of them on the campus map loads its floor in place of this one. They share the
// campus map's plan metres, so the map stays where it is and only the floor under it changes.
public partial class NavigationScreen
{
    [Header("Floors")]
    [Tooltip("Every building's floor the app holds; the scene opens on the one the Navigator has.")]
    public FloorAssets[] floors = new FloorAssets[0];

    // The floor of a campus building, as an index into floors, or -1.
    public int FloorOf(CampusData.Building building)
    {
        if (building == null || floors == null)
            return -1;
        return Array.FindIndex(floors, f => f != null && f.plan != null && f.building == building.name);
    }

    // Which of floors is loaded, or -1.
    public int CurrentFloor => floors == null ? -1 : Array.FindIndex(floors, f => f != null && f.plan == navigator.planJson);

    // Loads floors[index] in place of the current floor, back on the home screen: any walk stops, the position
    // starts again outdoors, and the directory, search, voice and the pretend entrance are about the new building.
    // False if there's no such floor.
    public bool SwitchFloor(int index)
    {
        if (floors == null || index < 0 || index >= floors.Length || floors[index]?.plan == null)
            return false;
        FloorAssets floor = floors[index];
        if (floor.plan == navigator.planJson)
            return true;
        CloseSnapCard(true);
        ShowHome();   // stops the walk, and clears the destination, the selection, the route and the pin

        navigator.SetFloor(floor.plan, floor.walkable);
        if (localizer != null)
            localizer.LoadFloor(floor.walls);
        map.SetFloor(Plan, floor.placeMap, floor.walls);
        miniMap.SetFloor(Plan, floor.placeMap, floor.walls);
        UsePictures(floor);

        // What the screen built for the last floor.
        m_BuildingList = null;
        m_BuildingRows = 0;
        m_StartListBuilt = false;
        m_StartListFor = null;
        m_StartListNear.Clear();
        m_NearYou.Clear();
        m_Building = null;
        ResetVoice();
        m_Interpreter = new VoiceParser(Plan);
        FillHome();
        NameBuilding();
        SetPretend(m_Pretend, false);
        if (location != null && Application.isPlaying)
            location.Begin(Plan);
        ApplyAppearance(true);   // the new floor's picture for the time of day, and its labels in the theme
        return true;
    }

    // The building's names: what people call it, the floor button and menu, the directory's title and the
    // pretend setting's words.
    void NameBuilding()
    {
        m_ShortName = string.IsNullOrEmpty(Plan.shortName) ? ShortName(Plan.building) : Plan.shortName;
        NameFloor();
        ShowPlanBuilding();
        PlanPlace entrance = PretendEntrance();
        pretendToggle.gameObject.SetActive(entrance != null);
        if (entrance != null)
        {
            pretendTitle.text = $"Pretend I'm at {m_ShortName}";
            pretendDetail.text = $"For demos: you're at its {PlaceStyle.Title(entrance)}";
        }
    }

    // The maps' pictures: the floor's, by day and by night (ThemeColors swaps them with the theme).
    void UsePictures(FloorAssets floor)
    {
        if (themeColors != null)
        {
            for (int i = 0; i < themeColors.textures.Count; i++)
            {
                ThemeColors.TextureSwap swap = themeColors.textures[i];
                if (swap.image == null || swap.image != map.basemap && swap.image != miniMap.basemap)
                    continue;
                swap.day = floor.dayMap;
                swap.night = floor.nightMap;
                themeColors.textures[i] = swap;
            }
        }
        foreach (FloorMap m in new[] { map, miniMap })
            if (m != null && m.basemap != null)
                m.basemap.texture = m_Night && floor.nightMap != null ? floor.nightMap : floor.dayMap;
    }
}
