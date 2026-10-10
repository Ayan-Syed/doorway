using System.Collections.Generic;
using UnityEngine;

// The map's taps and the my-location button.
public partial class NavigationScreen
{
    // The my-location button: the map over the phone's location, as a map app does.
    void OnLocate()
    {
        Haptics.Tick(15);
        if (location != null && location.HasFix && OnMap(location.Position))
        {
            Vector2 at = location.Position;
            float span = Mathf.Clamp(location.Accuracy * 3f, 60f, 300f);
            var half = new Vector2(span / 2f, span / 2f);
            ShowPlanBuilding();   // the pin goes, and the sheet with it
            HomeInsets(homeSheet.Stop);
            map.HidePin();
            map.FramePoints(new List<Vector2> { at - half, at + half }, 0f, span, false);
            UpdateGpsPuck();
        }
        else if (location == null || location.Unavailable)
            Snack("Location is off");
        else if (location.HasFix)
            Snack("You're not on the UCF campus");
        else
            Snack("Finding your location…");
    }

    // Whether a plan point is anywhere the map can show.
    bool OnMap(Vector2 point)
    {
        return map.Campus != null ? map.Campus.extent.Contains(point) : NearBuilding(point);
    }

    // Without a campus: the floor plan's picture, and MaxGpsDistance round it.
    bool NearBuilding(Vector2 point)
    {
        PlanImage image = Plan.image;
        float width = image.width * image.metersPerPixel;
        float height = image.height * image.metersPerPixel;
        var area = new Rect(image.left - MaxGpsDistance, image.top - height - MaxGpsDistance,
            width + 2f * MaxGpsDistance, height + 2f * MaxGpsDistance);
        return area.Contains(point);
    }

    // A tap on the map: the place there (null for nothing) and the plan point.
    void OnMapTapped(PlanPlace place, Vector2 point)
    {
        switch (m_Step)
        {
            case Step.Home:
                if (place != null)
                    ShowPlace(place);
                else if (!m_Searching)
                {
                    homeSheet.MoveTo(0, false);
                    map.HidePin();
                    ShowPlanBuilding();
                }
                break;
            case Step.Place:
                if (place == null)
                    ShowHome();
                else if (place != m_Selected)
                    ShowPlace(place);
                break;
            case Step.Start:
            case Step.Align:
                if (place != null && place.doors.Count > 0)
                    ShowAlign(place, NearestDoor(place, point), false, null);
                else
                    Snack("Tap the room or entrance you're at");
                break;
        }
    }

    // Where you are: start from that room's nearest door.
    PlanDoor NearestDoor(PlanPlace place, Vector2 point)
    {
        PlanDoor nearest = place.doors[0];
        float best = float.PositiveInfinity;
        foreach (PlanDoor door in place.doors)
        {
            float distance = (Plan.NodePosition(door.node) - point).sqrMagnitude;
            if (distance < best)
            {
                best = distance;
                nearest = door;
            }
        }
        return nearest;
    }

    // A building on the campus map: the floor's own goes inside; the others get a pin, as there's no map of their
    // insides yet.
    void OnBuildingTapped(CampusData.Building building)
    {
        if (m_Step > Step.Place)
        {
            if (m_Step <= Step.Align)
                Snack("Tap the room or entrance you're at");
            return;   // under the arrived card the map is only to look at
        }
        if (m_Step == Step.Place)
            ShowHome();
        if (building.indoor)
        {
            homeSheet.MoveTo(1, false);   // its rooms come up in the sheet under the floor
            GoInside();
            return;
        }
        Haptics.Tick(15);
        ShowOtherBuilding(building);
        HomeInsets(homeSheet.Stop);
        map.ShowPin(building.Spot, building.name);
        map.ShowBuilding(building, !Application.isPlaying);
        Snack("No indoor map for this building yet");
    }

    // A building picked in search.
    void OnBuildingRow(CampusData.Building building)
    {
        EndSearch();
        homeSheet.MoveTo(0, !Application.isPlaying);
        OnBuildingTapped(building);
    }
}
