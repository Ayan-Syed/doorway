using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Starting up: wiring the controls, reading the saved settings, and the launch screen.
public partial class NavigationScreen
{
    void Awake()
    {
        Initialize();
        // The UI animates at the screen's rate (Android defaults to 30). (Show keeps the screen on while walking.)
        Application.targetFrameRate = 60;
        // Unity's default drag threshold is 10 px, a twitch on a phone screen: taps in the lists would scroll.
        if (EventSystem.current != null && Screen.dpi > 0f)
            EventSystem.current.pixelDragThreshold = Mathf.Max(10, Mathf.RoundToInt(Screen.dpi / 160f * 8f));
        if (launchScreen != null)
        {
            m_Launching = true;
            launchScreen.gameObject.SetActive(true);
            launchScreen.alpha = 1f;
        }
        ResetHome(true);   // The app opens out over the campus.
        if (location != null)
            location.Begin(Plan);
    }

    // Once, from Awake or from whichever preview hook comes first.
    void Initialize()
    {
        if (m_Initialized)
            return;
        m_Initialized = true;
        m_Panels = snapPanel != null
            ? new[] { homePanel, routePanel, faceChip, guidePanel, arrivedPanel, floorMenu, snapPanel }
            : new[] { homePanel, routePanel, faceChip, guidePanel, arrivedPanel, floorMenu };

        Theme.Set(themeName);
        navigator.Load();
        map.SetPlan(Plan);
        miniMap.SetPlan(Plan);
        map.PlaceTapped += OnMapTapped;
        map.BuildingTapped += OnBuildingTapped;
        if (location != null)
            location.Changed += OnLocationChanged;

        searchLead.onClick.AddListener(OnSearchLead);
        searchClear.onClick.AddListener(OnSearchClear);
        settingsButton.onClick.AddListener(OpenSettings);
        if (roomSearchField != null)
        {
            roomSearchField.onValueChanged.AddListener(OnRoomSearch);
            roomSearchClear.onClick.AddListener(ClearRoomSearch);
            roomSearchClear.gameObject.SetActive(false);
        }
        for (int i = 0; i < chips.Length && i < ChipKinds.Length; i++)
        {
            string kind = ChipKinds[i];
            chips[i].onClick.AddListener(() => SetKind(m_Kind == kind ? null : kind, false));   // a second tap turns it off
        }
        floorPicker.GetComponent<Button>().onClick.AddListener(() => SetFloorMenu(!m_FloorMenuOpen));
        floorMenuBlocker.onClick.AddListener(() => SetFloorMenu(false));
        for (int i = 0; i < floorButtons.Length; i++)
        {
            int floor = i + 1;
            floorButtons[i].onClick.AddListener(() => PickFloor(floor));
        }
        wholeFloor.GetComponent<Button>().onClick.AddListener(GoInside);
        if (locate != null)
            locate.GetComponent<Button>().onClick.AddListener(OnLocate);

        directionsButton.onClick.AddListener(OnDirections);   // indoors, straight to walking; outdoors, GPS picks the start
        placeClose.onClick.AddListener(ShowHome);
        routeBack.onClick.AddListener(Back);
        originButton.onClick.AddListener(() => ShowStart(m_Destination, false));
        destinationButton.onClick.AddListener(() =>
        {
            if (m_Destination != null)
                ShowPlace(m_Destination);
        });
        for (int i = 0; i < nearbyChips.Length; i++)
        {
            int chip = i;
            nearbyChips[i].onClick.AddListener(() =>
            {
                if (chip < m_Nearby.Count)
                    ShowAlign(m_Nearby[chip].place, m_Nearby[chip].door, false, m_NearbyAt);
            });
        }
        moreButton.onClick.AddListener(() => ShowStart(m_Destination, false));
        beginButton.onClick.AddListener(Begin);
        realignButton.onClick.AddListener(Realign);
        endButton.onClick.AddListener(OnEndTapped);
        doneButton.onClick.AddListener(() => ResetHome(false));

        settingsScrim.GetComponent<Button>().onClick.AddListener(CloseSettings);
        settingsDone.onClick.AddListener(CloseSettings);
        for (int i = 0; i < markerCells.Length; i++)
        {
            int marker = i;
            markerCells[i].onClick.AddListener(() => SetMarker(marker, true));
        }
        for (int i = 0; i < appearanceButtons.Length; i++)
        {
            int mode = i;
            appearanceButtons[i].onClick.AddListener(() => SetAppearance(mode));
        }
        tiltToggle.onClick.AddListener(() => Set3D(!m_Map3D, true));
        pretendToggle.onClick.AddListener(() => SetPretend(!m_Pretend, true));
        InitializeVoice();

        navigator.Arrived += OnArrived;
        navigator.Rerouted += OnRerouted;
        settingsSheet.StopChanged += stop =>
        {
            if (stop == 0)
                CloseSettings();   // Dragged down shut.
        };

        // The editor's previews never read what a phone saved.
        if (Application.isPlaying)
        {
            m_Marker = PlayerPrefs.GetInt(MarkerKey, 0);
            m_Appearance = PlayerPrefs.GetInt(AppearanceKey, AppearanceAuto);
            m_Map3D = PlayerPrefs.GetInt(Map3DKey, 1) == 1;
            m_Pretend = PlayerPrefs.GetInt(PretendKey, 0) == 1;
            m_Voice = PlayerPrefs.GetInt(VoiceKey, 1) == 1;
        }

        FillHome();
        WireIndoor();
        m_ShortName = ShortName(Plan.building);
        NameFloor();
        ShowPlanBuilding();

        PlanPlace entrance = PretendEntrance();
        pretendToggle.gameObject.SetActive(entrance != null);
        if (entrance != null)
        {
            pretendTitle.text = $"Pretend I'm at {m_ShortName}";
            pretendDetail.text = $"For demos: you're at its {PlaceStyle.Title(entrance)}";
        }

        ApplyAppearance(true);
        SetMarker(m_Marker, false);
        Set3D(m_Map3D, false);
        SetPretend(m_Pretend, false);
        UpdateStops();

        // Hidden by their canvases from here on, never switched off (Layer). The maps stop with theirs.
        m_Layers = new Dictionary<GameObject, Layer>();
        AddLayer(map, map);
        AddLayer(homePanel);
        AddLayer(routePanel);
        AddLayer(guidePanel, miniMap);
        AddLayer(arrivedPanel);
        foreach (BottomSheet sheet in new[] { homeSheet, placeSheet, startSheet, alignSheet })
            AddLayer(sheet, sheet);
        foreach (Layer layer in m_Layers.Values)
            layer.Claim(m_Layers);

        snackbar.alpha = 0f;
        settingsPanel.gameObject.SetActive(false);
        floorMenu.gameObject.SetActive(false);
        if (launchScreen != null)
            launchScreen.gameObject.SetActive(false);   // Awake puts the launch screen up; the previews don't.
        if (beginSpinner != null)
            beginSpinner.gameObject.SetActive(false);
    }

    // The floor button and the floor menu's header name the plan's floor and building, not the builder's.
    void NameFloor()
    {
        if (floorLabel == null && floorPicker != null)
            foreach (Text text in floorPicker.GetComponentsInChildren<Text>(true))
                if (text.gameObject.name != "Icon" && text.text.Length > 1)
                {
                    floorLabel = text;
                    break;
                }
        if (floorMenuTitle == null && floorMenu != null)
        {
            Transform header = floorMenu.transform.Find("Card/Building");
            if (header != null)
                floorMenuTitle = header.GetComponent<Text>();
        }
        if (floorLabel != null)
            floorLabel.text = Plan.floor;
        if (floorMenuTitle != null)
            floorMenuTitle.text = Plan.building;
    }

    void OnDestroy()
    {
        if (navigator != null)
        {
            navigator.Arrived -= OnArrived;
            navigator.Rerouted -= OnRerouted;
        }
        if (map != null)
        {
            map.PlaceTapped -= OnMapTapped;
            map.BuildingTapped -= OnBuildingTapped;
        }
        if (location != null)
            location.Changed -= OnLocationChanged;
        UnwireIndoor();
        ReleaseVoice();
    }

    // Back from another app: the phone may have switched between light and dark.
    void OnApplicationFocus(bool focused)
    {
        if (focused && m_Initialized && m_Appearance == AppearanceAuto)
            ApplyAppearance(false);
    }

    // The launch screen is up for LaunchSeconds at least and until the map has settled, then fades as the map
    // comes down over the campus.
    void UpdateLaunch()
    {
        if (launchScreen == null || !launchScreen.gameObject.activeSelf)
            return;
        if (m_LaunchTime < 0f)
            m_LaunchTime = Time.unscaledTime;   // Counted from the first frame: the ones before it can take a while.
        if (m_Launching)
        {
            if (m_SettleFrames > 0 || Time.unscaledTime - m_LaunchTime < LaunchSeconds)
                return;
            m_Launching = false;
            launchScreen.blocksRaycasts = false;   // taps reach the map while it fades
            if (m_Step == Step.Home && m_OnCampus)
                FrameHome(false);                  // from the wider launch view down to the campus
        }
        // A slow frame doesn't skip the fade.
        launchScreen.alpha = Mathf.MoveTowards(launchScreen.alpha, 0f, Mathf.Min(Time.unscaledDeltaTime, 0.05f) / LaunchFade);
        if (launchLogo != null)
            launchLogo.localScale = Vector3.one * (1f + 0.12f * (1f - launchScreen.alpha));
        if (launchScreen.alpha <= 0f)
        {
            launchScreen.gameObject.SetActive(false);
            ReleaseLaunchLogo();
        }
    }

    // The logo shows once, as the app opens: its pictures (day and night) needn't stay in memory after that.
    void ReleaseLaunchLogo()
    {
        if (!Application.isPlaying || launchLogo == null || !launchLogo.TryGetComponent(out RawImage logo))
            return;
        var pictures = new List<Texture> { logo.texture };
        if (themeColors != null)
            themeColors.Forget(logo, pictures);
        logo.texture = null;
        foreach (Texture picture in new HashSet<Texture>(pictures))
            if (picture != null)
                Resources.UnloadAsset(picture);
    }
}
