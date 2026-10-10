using UnityEngine;
using UnityEngine.InputSystem;

// The state machine: Show is the one place the step changes, and Update runs the step on screen.
public partial class NavigationScreen
{
    void Show(Step step)
    {
        m_Step = step;
        if (step != Step.Start)
            m_AutoStart = false;
        // The map is behind Home to Align, and behind the arrived card: AR is off by then, so there's no camera
        // feed to show through.
        bool onMap = step <= Step.Align || step == Step.Arrived;
        if (map.gameObject.activeSelf != onMap)
            map.gameObject.SetActive(onMap);

        SetPanel(homePanel, step <= Step.Place);
        SetPanel(routePanel, step == Step.Start || step == Step.Align);
        SetPanel(faceChip, step == Step.Align);
        SetPanel(guidePanel, step == Step.Guide);
        SetPanel(arrivedPanel, step == Step.Arrived);

        // Each step opens its own sheet.
        KeepSheet(homeSheet, step == Step.Home);
        KeepSheet(placeSheet, step == Step.Place);
        KeepSheet(startSheet, step == Step.Start);
        KeepSheet(alignSheet, step == Step.Align);

        SetFloorMenu(false);
        if (step > Step.Place)
            CloseSettings();
        if (chipBar.gameObject.activeSelf != (step == Step.Home))
            chipBar.gameObject.SetActive(step == Step.Home);
        SetAlpha(floorPicker, 1f);
        searchLeadIcon.text = step == Step.Place || m_Searching ? IconBack : IconSearch;
        searchClear.gameObject.SetActive(step == Step.Place || searchField.text.Length > 0);

        // AR runs from picking a start (so it's tracking by Start) to the end of the walk. Before that the map fills
        // the screen and the camera would only cost frames and battery behind it; on the arrived card it's done.
        if (Application.isPlaying)
            navigator.SetARActive(step >= Step.Start && step <= Step.Guide);
        // With Depth, the AR camera's feed shows through behind the UI.
        if (uiCamera != null)
            uiCamera.clearFlags = onMap ? CameraClearFlags.SolidColor : CameraClearFlags.Depth;
        UpdateGpsPuck();
    }

    // Panels fade in (Update raises the alpha); they switch off at once.
    void SetPanel(CanvasGroup panel, bool visible)
    {
        if (panel.gameObject.activeSelf == visible)
            return;
        if (visible)
            panel.alpha = Application.isPlaying ? 0f : 1f;   // the editor's previews show it whole
        panel.gameObject.SetActive(visible);
    }

    static void KeepSheet(BottomSheet sheet, bool own)
    {
        if (!own && sheet.gameObject.activeSelf)
            sheet.gameObject.SetActive(false);
    }

    // A sheet already up moves to the stop; one that isn't slides up from the bottom edge.
    static void OpenSheet(BottomSheet sheet, int stop)
    {
        if (sheet.gameObject.activeSelf)
        {
            sheet.MoveTo(stop, false);
            return;
        }
        sheet.gameObject.SetActive(true);
        sheet.Present(stop);
    }

    // Writes only what changed; under half visible, it stops taking taps.
    static void SetAlpha(CanvasGroup group, float alpha)
    {
        if (group.alpha != alpha)
            group.alpha = alpha;
        bool on = alpha > 0.5f;
        if (group.blocksRaycasts != on)
            group.blocksRaycasts = on;
        if (group.interactable != on)
            group.interactable = on;
    }

    void Update()
    {
        // The safe area is applied in its first Update, and changes with rotation or a notch setting.
        if (Mathf.Abs(safeArea.rect.height - m_SafeHeight) > 1f)
            UpdateStops();
        // On the third frame the layout has its real size: frame the home view again.
        if (m_SettleFrames > 0 && --m_SettleFrames == 0 && m_Step == Step.Home)
            FrameHome(true);
        UpdateLaunch();
        if (m_BuildingList == null || m_BuildingRows < m_BuildingList.Count)
            AddBuildingRows(BuildingRowsPerFrame);
        UpdateSystemTheme();

        switch (m_Step)
        {
            case Step.Home:
                if (!m_Searching && searchField.isFocused)
                    SetSearching(true);
                bool clear = searchField.text.Length > 0;
                if (searchClear.gameObject.activeSelf != clear)
                    searchClear.gameObject.SetActive(clear);
                UpdateIndoors(false);
                UpdateHomeChrome();
                break;
            case Step.Place:
                if (searchField.isFocused)
                    SearchAgain();   // Tapping the place's name in the search bar searches again.
                break;
            case Step.Start:
                if (originField.isFocused && !startSheet.IsAtTop)
                    startSheet.MoveTo(startSheet.stops.Length - 1, false);   // the whole list while typing a door
                if (m_AutoStart && Pointer.current != null && Pointer.current.press.wasPressedThisFrame)
                    m_AutoStart = false;   // A touch anywhere: the user's picking, so stop picking for them.
                UpdateFinding();
                break;
            case Step.Align:
                UpdateAlign();
                break;
            case Step.Guide:
                UpdateGuide(false);
                break;
        }

        float fade = Time.unscaledDeltaTime * FadeSpeed;
        foreach (CanvasGroup panel in m_Panels)
            if (panel.gameObject.activeSelf && panel.alpha < 1f)
                panel.alpha = Mathf.MoveTowards(panel.alpha, 1f, fade);

        if (settingsPanel.gameObject.activeSelf)
        {
            // The scrim darkens with the settings sheet as it comes up, or as it's dragged down.
            float open = Mathf.Clamp01(settingsSheet.Height / settingsSheet.stops[settingsSheet.stops.Length - 1]);
            if (settingsScrim.alpha != open)
                settingsScrim.alpha = open;
            if (!m_SettingsOpen && open < 0.002f)
                settingsPanel.gameObject.SetActive(false);
        }

        UpdateVoice();
        if (snackbar.alpha > 0f)
        {
            PlaceSnackbar();   // it follows a sheet that's moving
            if (Time.unscaledTime > m_SnackbarHideTime)
                snackbar.alpha = Mathf.MoveTowards(snackbar.alpha, 0f, Time.unscaledDeltaTime * 4f);
        }

        // Android's back button.
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            Back();
    }

    // Android's back button, and the route card's back arrow.
    void Back()
    {
        if (m_SettingsOpen)
        {
            CloseSettings();
            return;
        }
        if (m_FloorMenuOpen)
        {
            SetFloorMenu(false);
            return;
        }
        switch (m_Step)
        {
            case Step.Home:
                if (m_Searching || searchField.text.Length > 0)
                    EndSearch();
                else if (m_Kind != null)
                    SetKind(null, false);
                else if (homeSheet.Stop > 0)
                    homeSheet.MoveTo(0, false);
                else if (Time.unscaledTime < m_ExitConfirmUntil)
                    Application.Quit();
                else
                {
                    // Two presses, so a stray one doesn't close the app.
                    m_ExitConfirmUntil = Time.unscaledTime + ExitConfirmSeconds;
                    Snack("Press back again to exit");
                }
                break;
            case Step.Place:
                ShowHome();
                break;
            case Step.Start:
                ShowPlace(m_Destination);
                break;
            case Step.Align:
                ShowStart(m_Destination, false);
                break;
            case Step.Guide:
                OnEndTapped();
                break;
            case Step.Arrived:
                ResetHome(false);
                break;
        }
    }

    // A new message replaces the old one at once.
    void Snack(string message)
    {
        snackbarText.text = message;
        snackbar.alpha = 1f;
        m_SnackbarHideTime = Time.unscaledTime + SnackbarSeconds;
        PlaceSnackbar();
    }

    // Just above whatever is along the bottom (and the mic button), but never above 60 % of the screen.
    void PlaceSnackbar()
    {
        float bottom = settingsPanel.gameObject.activeSelf ? settingsSheet.Height : BottomUI();
        var rect = (RectTransform)snackbar.transform;
        float y = Mathf.Min(bottom, safeArea.rect.height * 0.6f) + 24f;
        if (m_MicTop >= 0f && !settingsPanel.gameObject.activeSelf)
            y = Mathf.Max(y, m_MicTop + 16f);
        if (rect.anchoredPosition.y != y)
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, y);
    }

    // How far up the step's sheet or card reaches: the snackbar and the mic button go just above it.
    float BottomUI()
    {
        switch (m_Step)
        {
            case Step.Home: return homeSheet.Height;
            case Step.Place: return placeSheet.Height;
            case Step.Start: return startSheet.Height;
            case Step.Align: return alignSheet.Height;
            case Step.Guide: return TopOf(miniMapFrame);
            default: return TopOf(arrivedCard);
        }
    }

    // The sheets' stops for this safe area: peek, the middle, and the top just under the search or route card.
    void UpdateStops()
    {
        m_SafeHeight = safeArea.rect.height;
        float middle = Mathf.Round(m_SafeHeight * 0.44f);
        float homeTop = Mathf.Max(middle, m_SafeHeight - BottomOf(searchCard) - 20f);
        float startTop = Mathf.Max(middle, m_SafeHeight - BottomOf(routeCard) - 20f);
        homeSheet.SetStops(new[] { HomePeek, middle, homeTop }, homeSheet.Stop, true);
        startSheet.SetStops(new[] { StartPeek, middle, startTop }, startSheet.Stop, true);
        placeSheet.SetStops(new[] { PlaceHeight }, 0, true);
        alignSheet.SetStops(new[] { AlignHeight }, 0, true);
        settingsSheet.SetStops(new[] { 0f, Mathf.Min(SettingsHeight, m_SafeHeight - 60f) }, settingsSheet.Stop, true);
    }

    // How far below the safe area's top a piece of UI ends.
    float BottomOf(Component target)
    {
        ((RectTransform)target.transform).GetWorldCorners(s_Corners);
        return safeArea.rect.yMax - safeArea.InverseTransformPoint(s_Corners[0]).y;
    }

    // How far above the safe area's bottom a piece of UI reaches.
    float TopOf(RectTransform target)
    {
        target.GetWorldCorners(s_Corners);
        return safeArea.InverseTransformPoint(s_Corners[1]).y - safeArea.rect.yMin;
    }

    // Tells the map how much of it the UI covers, given in safe-area distances from the top and the bottom. The map
    // runs under the status and gesture bars, so they're added; its framing then centres in what's left.
    void SetInsets(float top, float bottom)
    {
        var window = (RectTransform)map.transform;
        safeArea.GetWorldCorners(s_Corners);
        Vector3 safeBottomLeft = s_Corners[0], safeTopLeft = s_Corners[1];
        map.topInset = window.rect.yMax - window.InverseTransformPoint(safeTopLeft).y + top;
        map.bottomInset = window.InverseTransformPoint(safeBottomLeft).y - window.rect.yMin + bottom;
    }
}
