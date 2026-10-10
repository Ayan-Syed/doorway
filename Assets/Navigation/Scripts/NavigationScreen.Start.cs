using System.Collections.Generic;
using UnityEngine;

// Start ("Where are you?"): the walk starts at a door. Directions picks the door from the phone's location if it
// can; otherwise the user picks one in the list or taps the map.
public partial class NavigationScreen
{
    // The start list as last built: for which destination, and which doors were "Near you" (and from where). The
    // same again is kept as it is, so going back and forth between Start and Align, or Re-align, rebuilds nothing.
    PlanPlace m_StartListFor;
    bool m_StartListBuilt;
    readonly List<PlanDoor> m_StartListNear = new List<PlanDoor>();
    readonly List<(PlanPlace place, PlanDoor door)> m_NearYou = new List<(PlanPlace place, PlanDoor door)>();
    Vector2 m_NearYouAt;

    // The blue dot while browsing. Off once the start is picked: from Align on, the map's dot is the start.
    // Show calls this on every step change, so it also rests the GPS while walking and on the arrived card, where
    // nothing uses it (and wakes it after).
    void UpdateGpsPuck()
    {
        if (location != null && Application.isPlaying)
            location.Suspend(m_Step == Step.Guide || m_Step == Step.Arrived);
        if (m_Step > Step.Start || m_SnapOpen)
            return;   // the snap card shows the dot on its door
        if (ShowIndoorDot())
            return;   // indoors the dot is the Localizer's
        if (location != null && location.HasFix && OnMap(location.Position))
            map.ShowPuck(location.Position, 0f, ShownAccuracy(), false);
        else
            map.HidePuck();
    }

    // The accuracy as the map draws it, and the Align step says it: up to the worst fix that may pick a start door,
    // so a fix that's allowed to pick never claims to be better than it is.
    float ShownAccuracy()
    {
        return Mathf.Clamp(location.Accuracy, MinAccuracyShown, MaxStartAccuracy);
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

    // Outside the floor's building: further out than the fix may be off by (and never less than OutsideMargin).
    // A ±30 m fix landing 10 m outside the outline is as likely inside near that wall.
    bool Outside(Vector2 at, float accuracy)
    {
        CampusData.Building building = map.Campus?.indoor;
        return building != null && !building.Contains(at) && building.Distance(at) > Mathf.Max(OutsideMargin, accuracy);
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
    // good enough, or as soon as it is while the list shows and the user hasn't touched it. No destination: the
    // list is for saying where you are (ShowDoorPicker).
    void ShowStart(PlanPlace destination, bool allowAuto)
    {
        Haptics.Tick(15);
        navigator.Stop();
        m_PickingDoor = false;
        m_Destination = m_Selected = destination;
        map.ShowOnly(null);
        map.Select(destination);
        if (destination != null)
        {
            string destinationTitle = PlaceStyle.Title(destination);
            destinationText.text = destinationTitle;
            map.ShowPin(map.Anchor(destination), destinationTitle);
        }
        else
        {
            allowAuto = false;
            map.HidePin();
        }

        m_AutoStart = false;
        if (allowAuto && location != null)
        {
            location.Retry(true);
            m_AutoStart = true;
            m_FindingSince = Time.unscaledTime;
            m_ToldWhy = false;
        }
        // AR starts with this step, and ARCore asks for the camera as it does: not while the location dialog is up
        // (Android shows one at a time and refuses the other unseen).
        if (Application.isPlaying && location != null)
            navigator.HoldAR = location.IsAsking;
        if (m_AutoStart && TryAutoStart())
            return;

        originField.gameObject.SetActive(true);
        originButton.gameObject.SetActive(false);
        Vector2? walkedTo = TakeRealignSpot();   // re-aligning: where the walk had got to (NavigationScreen.Realign)
        FillStartList(destination, walkedTo);

        Show(Step.Start);
        OpenSheet(startSheet, 1);
        map.ShowRoute(null);
        SetInsets(BottomOf(routeCard) + 8f, startSheet.stops[1]);
        if (walkedTo.HasValue)
            FrameRealign(walkedTo.Value);
        else
            map.ShowAll(!Application.isPlaying);
        UpdateFinding();
        if (!m_AutoStart && location != null && location.Unavailable && !m_ToldNoLocation && Application.isPlaying)
        {
            m_ToldNoLocation = true;
            Snack("Location is off · pick where you are");
        }
    }

    // "Near you" (the doors round the phone's location, or round where the walk had got to when re-aligning), then
    // every door, grouped as the directory is. Built again only when the destination or the doors near you have
    // changed since last time.
    void FillStartList(PlanPlace destination, Vector2? walkedTo = null)
    {
        m_NearYou.Clear();
        if (walkedTo.HasValue)
            NearWalk(walkedTo.Value, m_NearYou);
        else if (location != null && location.HasFix)
        {
            Vector2 at = location.Position;
            m_NearYouAt = at;
            bool outside = Outside(at, location.Accuracy);
            foreach (var (place, door, distance) in DoorsNear(at))
            {
                if (distance > MaxGpsDistance || m_NearYou.Count == NearYouRows)
                    break;
                if (outside && place.kind != "exit")
                    continue;
                m_NearYou.Add((place, door));
            }
        }

        bool same = m_StartListBuilt && destination == m_StartListFor && m_NearYou.Count == m_StartListNear.Count
                    && walkedTo.HasValue == m_StartListWalked;
        m_StartListWalked = walkedTo.HasValue;
        for (int i = 0; same && i < m_NearYou.Count; i++)
            same = m_NearYou[i].door == m_StartListNear[i];
        if (same)
        {
            startList.ResetSearch();
            return;
        }
        m_StartListFor = destination;
        m_StartListBuilt = true;
        m_StartListNear.Clear();

        startList.Clear();
        for (int i = 0; i < m_NearYou.Count; i++)
        {
            var (place, door) = m_NearYou[i];
            m_StartListNear.Add(door);
            if (i == 0)
                startList.AddSection(walkedTo.HasValue ? "Near where you were" : "Near you");
            // The phone's location goes along (as it is when tapped), so "Not here?" offers the doors around it.
            startList.AddRow(place.kind, PlaceStyle.DoorTitle(place, door), place.detail,
                () => StartFrom(place, door, m_NearYouAt));
        }
        // Every door, in the directory's dropdowns: a row per door, and the doors of the rooms inside a room under
        // that room's first door.
        AddGrouped(startList, destination, (place, parent) =>
        {
            int first = -1;
            foreach (PlanDoor door in place.doors)
            {
                PlanDoor at = door;
                int row = startList.AddRow(place.kind, PlaceStyle.DoorTitle(place, at), place.detail,
                    () => StartFrom(place, at, null), parent: parent);
                if (first < 0)
                    first = row;
            }
            return first;
        });
        startList.ResetSearch();
    }

    // The door nearest the phone's location, or the nearest entrance if it's outside, once the location is good
    // enough and at the building. If that door can't start a route, it stops picking.
    bool TryAutoStart()
    {
        if (location == null || !location.HasFix || location.Accuracy > MaxStartAccuracy)
            return false;
        Vector2 at = location.Position;
        bool outside = Outside(at, location.Accuracy);
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
        if (Application.isPlaying && location != null)
            navigator.HoldAR = location.IsAsking;   // AR waits for the location dialog's answer (ShowStart)
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
