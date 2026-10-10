using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Home: the map, the search bar, the filter chips, the floor menu and the building's directory.
public partial class NavigationScreen
{
    // Back to the map with nothing picked, and the search and filters cleared: out over the campus (as the app
    // opens) or on the floor (after a walk).
    void ResetHome(bool campus)
    {
        m_SavedQuery = "";
        m_Searching = false;
        searchField.SetTextWithoutNotify("");
        SetRoomQuery("");
        homeList.ResetSearch();
        ShowPlanBuilding();
        ShowHome();
        CloseSettings();
        SetKind(null, true);
        homeSheet.MoveTo(0, !Application.isPlaying);
        m_OnCampus = campus;
        FrameHome(!Application.isPlaying);
        UpdateIndoors(true);
    }

    void FrameHome(bool instant)
    {
        if (!m_OnCampus)
        {
            FrameAll(instant);
            return;
        }
        HomeInsets(homeSheet.Stop);
        map.ShowCampus(instant, FloorMap.CampusSpan * (m_Launching ? LaunchSpan : 1f));
    }

    // Back to the map from a place or a route, with the search and filters as they were.
    void ShowHome()
    {
        navigator.Stop();
        m_Destination = m_Selected = null;
        searchField.SetTextWithoutNotify(m_SavedQuery);
        if (m_SavedQuery.Length == 0 && RoomQuery.Length > 0)
            homeList.FilterWithin(RoomQuery);   // back from a room found in the sheet's own search
        else
            homeList.Filter(m_SavedQuery);
        Show(Step.Home);
        // A sheet that was at the top comes back to the middle, unless searching.
        OpenSheet(homeSheet, m_Searching ? homeSheet.stops.Length - 1 : Mathf.Min(homeSheet.Stop, 1));
        map.Select(null);
        map.HidePin();
        map.ShowRoute(null);
        map.ShowOnly(m_Kind);   // Directions showed every kind; the chip that's still on says otherwise.
        UpdateHomeChrome();
    }

    void FillHome()
    {
        homeList.Clear();
        foreach (var (title, kinds) in Sections)
        {
            bool header = false;
            foreach (PlanPlace place in Places(kinds))
            {
                if (!header)
                {
                    homeList.AddSection(title);
                    header = true;
                }
                homeList.AddRow(place.kind, PlaceStyle.Title(place), place.detail, () => ShowPlace(place));
            }
        }
    }

    // The plan's places of these kinds, in the kinds' order. Rooms by number, the rest as listed in the plan.
    IEnumerable<PlanPlace> Places(string[] kinds)
    {
        return Plan.places.Where(p => Array.IndexOf(kinds, p.kind) >= 0)
            .OrderBy(p => Array.IndexOf(kinds, p.kind))
            .ThenBy(p => p.kind == "room" ? p.name : "", StringComparer.Ordinal);
    }

    // The campus's buildings, as search results after the rooms: a few each frame while the app opens, so they
    // don't hold it up.
    void AddBuildingRows(int count)
    {
        CampusData campus = map.Campus;
        if (campus == null)
            return;
        if (m_BuildingList == null)
        {
            // The floor's own building first, then A to Z; names the map's font can't draw are left out.
            m_BuildingList = campus.buildings
                .Where(b => !string.IsNullOrEmpty(b.name) && FloorMap.Drawable(b.name))
                .OrderBy(b => b.indoor ? 0 : 1)
                .ThenBy(b => b.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            homeList.AddSection("Buildings");
        }
        int end = m_BuildingList.Count - m_BuildingRows <= count ? m_BuildingList.Count : m_BuildingRows + count;
        for (; m_BuildingRows < end; m_BuildingRows++)
        {
            CampusData.Building building = m_BuildingList[m_BuildingRows];
            string detail = building.indoor ? $"Indoor map · {Plan.floor}" : "UCF · No indoor map yet";
            homeList.AddRow("building", building.name, detail, () => OnBuildingRow(building), searchOnly: true);
        }
        homeList.Refresh();
    }

    void SetSearching(bool searching)
    {
        m_Searching = searching;
        searchLeadIcon.text = searching || m_Step == Step.Place ? IconBack : IconSearch;
        if (searching)
        {
            // The search bar searches everywhere; the sheet's search lets go of the list.
            if (RoomQuery.Length > 0)
            {
                SetRoomQuery("");
                homeList.Filter(searchField.text);
            }
            SetFloorMenu(false);
            ShowPlanBuilding();   // the search covers the floor's rooms and every building
            homeSheet.MoveTo(homeSheet.stops.Length - 1, false);
        }
        else if (searchField.isFocused)
            searchField.DeactivateInputField();
    }

    void EndSearch()
    {
        m_SavedQuery = "";
        searchField.text = "";   // notifies: the list filters again and scrolls to the top
        SetSearching(false);
        homeSheet.MoveTo(1, false);
    }

    void OnSearchLead()
    {
        if (m_Step == Step.Place)
            ShowHome();
        else if (m_Searching)
            EndSearch();
        else
            searchField.ActivateInputField();
    }

    void OnSearchClear()
    {
        if (m_Step == Step.Place)
        {
            ShowHome();
            return;
        }
        searchField.text = "";
        searchField.ActivateInputField();
    }

    // From a place, tapping its name in the search bar: back to the list, searching for that name.
    void SearchAgain()
    {
        string query = searchField.text;
        m_SavedQuery = query;
        ShowHome();
        searchField.SetTextWithoutNotify(query);
        homeList.Filter(query);
        SetSearching(true);
    }

    // A filter chip: the list and the map show only that kind, all of it in view.
    void SetKind(string kind, bool instant)
    {
        m_Kind = kind;
        homeList.SetKind(kind);
        map.ShowOnly(kind);
        StyleChips();
        int chip = kind == null ? -1 : Array.IndexOf(ChipKinds, kind);
        if (chip < 0)
        {
            homeSubtitle.text = HomeSubtitle;
            if (!instant)
                FrameAll(false);
            return;
        }
        ShowPlanBuilding();   // a chip is about the floor
        int count = homeList.Shown;
        homeSubtitle.text = count == 0 ? $"No {KindPlural[chip]} · {Plan.floor}"
            : $"{count} {(count == 1 ? KindSingular[chip] : KindPlural[chip])} · {Plan.floor}";
        if (homeSheet.Stop > 1)
            homeSheet.MoveTo(1, instant);
        var points = new List<Vector2>();
        foreach (PlanPlace place in Plan.places)
            if (place.kind == kind)
            {
                var bounds = map.Bounds(place);
                points.Add(bounds.min);
                points.Add(bounds.max);
            }
        if (points.Count == 0)
            FrameAll(instant);   // nothing of that kind to frame
        else
        {
            HomeInsets(homeSheet.Stop);
            map.FramePoints(points, 0f, 20f, instant);
        }
        Haptics.Tick(15);
    }

    void StyleChips()
    {
        Theme theme = Theme.Current;
        for (int i = 0; i < chips.Length && i < ChipKinds.Length; i++)
        {
            bool on = m_Kind == ChipKinds[i];
            chipBackgrounds[i].color = on ? theme.primaryContainer : theme.surface;
            chipLabels[i].color = on ? theme.primary : theme.onSurface;
        }
    }

    // The chips, floor button and whole-floor button make way as the sheet comes up over them.
    void UpdateHomeChrome()
    {
        float half = homeSheet.stops[Mathf.Min(1, homeSheet.stops.Length - 1)];
        float chrome = m_Searching ? 0f : 1f - Mathf.Clamp01((homeSheet.Height - half) / 160f);
        SetAlpha(chipBar, chrome);
        SetAlpha(floorPicker, chrome * m_IndoorChrome);
        SetAlpha(wholeFloor, chrome);
        if (locate != null)
            SetAlpha(locate, chrome);
        if (attribution != null)
            SetAlpha(attribution, chrome);
        // The sheet's search shows once the sheet's up past its peek.
        if (roomSearch != null)
            SetAlpha(roomSearch, Mathf.Clamp01((homeSheet.Height - HomePeek) / 80f));
        if (chrome < 0.5f && m_FloorMenuOpen)
            SetFloorMenu(false);
    }

    // In on the floor or out over the campus: the subtitle, Go inside or Whole floor, and the floor button (only
    // inside) follow the map.
    void UpdateIndoors(bool force)
    {
        bool indoors = map.Indoors;
        if (indoors != m_Indoors || force)
        {
            m_Indoors = indoors;
            if (indoors)
                ShowPlanBuilding();   // in on the floor, the sheet is the floor's again
            if (m_Kind == null)
                homeSubtitle.text = HomeSubtitle;
            wholeFloorLabel.text = indoors ? "Whole floor" : "Go inside";
            wholeFloorIcon.text = indoors ? IconWholeFloor : PlaceStyle.IconDoor;
        }
        float target = m_Indoors ? 1f : 0f;
        m_IndoorChrome = force || !Application.isPlaying ? target
            : Mathf.MoveTowards(m_IndoorChrome, target, Time.unscaledDeltaTime * FadeSpeed);
    }

    string HomeSubtitle =>
        m_Building != null ? "UCF · No indoor map yet"
        : m_Indoors ? $"{Plan.floor} · Tap any room to go there"
        : "Indoor map available · Tap Go inside";

    // The directory sheet is about the plan's building: its name, and its places in the list. Another building's
    // pin goes with its name (callers that pin a place pin it after this).
    void ShowPlanBuilding()
    {
        bool other = m_Building != null;
        m_Building = null;
        homeTitle.text = Plan.building;
        ShowDirectory(true);
        if (!other)
            return;
        map.HidePin();
        if (m_Kind == null)
            homeSubtitle.text = HomeSubtitle;
    }

    // Another building on the campus: the sheet names it and says there's no map of its insides, instead of
    // going on about the plan's building and listing its rooms.
    void ShowOtherBuilding(CampusData.Building building)
    {
        if (m_Searching)
            EndSearch();
        m_Building = building;
        if (m_Kind != null)
            SetKind(null, true);
        homeTitle.text = building.name;
        homeSubtitle.text = HomeSubtitle;
        ShowDirectory(false);
        homeSheet.MoveTo(0, !Application.isPlaying);   // nothing more to show under the name
    }

    void ShowDirectory(bool shown)
    {
        GameObject list = homeList.scroll != null ? homeList.scroll.gameObject : homeList.gameObject;
        if (list.activeSelf != shown)
            list.SetActive(shown);
        if (roomSearch != null && roomSearch.gameObject.activeSelf != shown)
            roomSearch.gameObject.SetActive(shown);
    }

    string RoomQuery => roomSearchField != null ? roomSearchField.text : "";

    // Typing in the sheet's search: the building's places containing every typed word, as in any part of a
    // room number or a name ("14" finds Room 145, "exch" 148 The Exchange). Campus buildings stay out of it.
    void OnRoomSearch(string query)
    {
        if (m_Searching || searchField.text.Length > 0)
        {
            m_SavedQuery = "";
            searchField.SetTextWithoutNotify("");
            SetSearching(false);
        }
        homeList.FilterWithin(query);
        roomSearchClear.gameObject.SetActive(query.Length > 0);
    }

    void ClearRoomSearch()
    {
        roomSearchField.text = "";   // notifies: the whole list again
    }

    // Writes the sheet's search without filtering; the caller filters.
    void SetRoomQuery(string query)
    {
        if (roomSearchField == null)
            return;
        roomSearchField.SetTextWithoutNotify(query);
        roomSearchClear.gameObject.SetActive(query.Length > 0);
    }

    // The Whole floor / Go inside button.
    void GoInside()
    {
        Haptics.Tick(15);
        ShowPlanBuilding();
        map.HidePin();
        FrameAll(false);
    }

    void FrameAll(bool instant)
    {
        if (m_Step == Step.Home)
            HomeInsets(homeSheet.Stop);
        map.ShowAll(instant);
    }

    // The map keeps clear of the chips at the top and of the sheet at this stop.
    void HomeInsets(int stop)
    {
        SetInsets(BottomOf(chipBar) + 8f, homeSheet.stops[stop]);
    }

    void SetFloorMenu(bool open)
    {
        if (m_FloorMenuOpen == open && floorMenu.gameObject.activeSelf == open)
            return;
        m_FloorMenuOpen = open;
        SetPanel(floorMenu, open);
        if (open)
            Haptics.Tick(15);
    }

    // Only the plan's floor is mapped.
    void PickFloor(int floor)
    {
        SetFloorMenu(false);
        if (floor == FloorNumber(Plan.floor))
            FrameAll(false);
        else
            Snack($"Floor {floor} isn't mapped yet");
    }

    // "Floor 2" is 2: the plan's floor as the floor menu numbers its rows (1 if it says no number). The scene
    // builder ticks that row.
    public static int FloorNumber(string floor)
    {
        int number = 0;
        if (floor != null)
            foreach (char c in floor)
                if (c >= '0' && c <= '9')
                    number = number * 10 + (c - '0');
        return number > 0 ? number : 1;
    }
}
