using System.Collections.Generic;
using UnityEngine;

// Start ("Where are you?"): the walk starts at a door. Directions picks the door from the phone's location if it
// can; otherwise the user picks one in the list or taps the map.
public partial class NavigationScreen
{
    // The blue dot while browsing. Off once the start is picked: from Align on, the map's dot is the start.
    void UpdateGpsPuck()
    {
        if (m_Step > Step.Start)
            return;
        if (location != null && location.HasFix && OnMap(location.Position))
            map.ShowPuck(location.Position, 0f, ShownAccuracy(), false);
        else
            map.HidePuck();
    }

    // The accuracy as the map draws it, and the Align step says it.
    float ShownAccuracy()
    {
        return Mathf.Clamp(location.Accuracy, MinAccuracyShown, MaxAccuracyShown);
    }

    // A new fix, the fix lost, or location turned off or on.
    void OnLocationChanged()
    {
        UpdateGpsPuck();
        if (m_Step == Step.Start)
        {
            if (m_AutoStart && !TryAutoStart())
                UpdateFinding();
        }
        else if (m_Step == Step.Align && m_FromAuto && location.HasFix)
            originText.text = OriginNear();   // the "±" follows the fix, like the disc
    }

    // Outside the floor's building, by more than a fix in it is often off by.
    bool Outside(Vector2 at)
    {
        CampusData.Building building = map.Campus?.indoor;
        return building != null && !building.Contains(at) && building.Distance(at) > OutsideMargin;
    }

    // Every door but the destination's, nearest first. A new list each time: callers may call it again (through
    // ShowAlign) while going through one.
    List<(PlanPlace place, PlanDoor door, float distance)> DoorsNear(Vector2 at)
    {
        var doors = new List<(PlanPlace place, PlanDoor door, float distance)>();
        foreach (PlanPlace place in Plan.places)
        {
            if (place == m_Destination)
                continue;
            foreach (PlanDoor door in place.doors)
                doors.Add((place, door, Vector2.Distance(at, Plan.NodePosition(door.node))));
        }
        doors.Sort((a, b) => a.distance.CompareTo(b.distance));
        return doors;
    }

    // allowAuto (from the place card's Directions): start at the door nearest the phone's location, now if it's
    // good enough, or as soon as it is while the list shows and the user hasn't touched it.
    void ShowStart(PlanPlace destination, bool allowAuto)
    {
        Haptics.Tick(15);
        navigator.Stop();
        m_Destination = m_Selected = destination;
        string destinationTitle = PlaceStyle.Title(destination);
        destinationText.text = destinationTitle;
        map.ShowOnly(null);
        map.Select(destination);
        map.ShowPin(map.Anchor(destination), destinationTitle);

        m_AutoStart = false;
        if (allowAuto && location != null)
        {
            location.Retry(true);
            m_AutoStart = true;
            m_FindingSince = Time.unscaledTime;
            m_ToldWhy = false;
            if (TryAutoStart())
                return;
        }

        originField.gameObject.SetActive(true);
        originButton.gameObject.SetActive(false);
        startList.Clear();
        if (location != null && location.HasFix)
        {
            Vector2 at = location.Position;
            bool outside = Outside(at);
            int rows = 0;
            foreach (var (place, door, distance) in DoorsNear(at))
            {
                if (distance > MaxGpsDistance || rows == NearYouRows)
                    break;
                if (outside && place.kind != "exit")
                    continue;
                if (rows++ == 0)
                    startList.AddSection("Near you");
                // The phone's location goes along, so "Not here?" offers the doors around it.
                startList.AddRow(place.kind, PlaceStyle.DoorTitle(place, door), place.detail,
                    () => ShowAlign(place, door, false, at));
            }
        }
        foreach (var (title, kinds) in Sections)
        {
            bool header = false;
            foreach (PlanPlace place in Places(kinds))
            {
                if (place == destination)
                    continue;
                foreach (PlanDoor door in place.doors)
                {
                    if (!header)
                    {
                        startList.AddSection(title);
                        header = true;
                    }
                    startList.AddRow(place.kind, PlaceStyle.DoorTitle(place, door), place.detail,
                        () => ShowAlign(place, door, false, null));
                }
            }
        }
        startList.ResetSearch();

        Show(Step.Start);
        OpenSheet(startSheet, 1);
        map.ShowRoute(null);
        SetInsets(BottomOf(routeCard) + 8f, startSheet.stops[1]);
        map.ShowAll(!Application.isPlaying);
        UpdateFinding();
        if (!m_AutoStart && location != null && location.Unavailable && !m_ToldNoLocation && Application.isPlaying)
        {
            m_ToldNoLocation = true;
            Snack("Location is off · pick where you are");
        }
    }

    // The door nearest the phone's location, or the nearest entrance if it's outside, once the location is good
    // enough and at the building. If that door can't start a route, it stops picking.
    bool TryAutoStart()
    {
        if (location == null || !location.HasFix || location.Accuracy > MaxStartAccuracy)
            return false;
        Vector2 at = location.Position;
        bool outside = Outside(at);
        foreach (var (place, door, distance) in DoorsNear(at))
        {
            if (distance > MaxGpsDistance)
                return false;   // not at the building
            if (outside && place.kind != "exit")
                continue;       // outside: entrances only
            if (!ShowAlign(place, door, true, at))
            {
                m_AutoStart = false;
                return false;
            }
            if (outside)
                Snack($"You're outside · start at the {PlaceStyle.Title(place)}");
            return true;
        }
        return false;
    }

    // While Directions waits for the phone's location: a spinner under the title, for FindingSeconds. Then, or as
    // soon as there's no hope, a word on why it didn't pick (once), over the list to pick from.
    void UpdateFinding()
    {
        bool usable = location != null && location.HasFix && location.Accuracy <= MaxStartAccuracy;
        bool finding = m_AutoStart && !usable && location != null && !location.Unavailable &&
                       (!Application.isPlaying || Time.unscaledTime - m_FindingSince < FindingSeconds);
        if (findingRow.activeSelf != finding)
        {
            findingRow.SetActive(finding);
            startHint.SetActive(!finding);
        }
        if (!m_AutoStart || finding || m_ToldWhy || !Application.isPlaying || location == null)
            return;
        m_ToldWhy = true;
        if (location.Unavailable)
        {
            m_ToldNoLocation = true;
            Snack("Location is off · pick where you are");
        }
        else if (usable)
            Snack($"You're not at {m_ShortName} · pick where you are");
        else
            Snack("Couldn't find you · pick where you are");
    }
}
