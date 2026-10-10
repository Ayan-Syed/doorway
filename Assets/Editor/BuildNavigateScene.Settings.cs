using UnityEngine;
using UnityEngine.UI;
using static UIBuild;

// Settings, the mic button, the snackbar and the launch screen.
public static partial class BuildNavigateScene
{
    // A sheet over the dimmed map: your marker, light or dark, the 3D map, pretending, spoken directions.
    static void CreateSettings(NavigationScreen screen, RectTransform safeArea)
    {
        CanvasGroup panelGroup = Panel("Settings", safeArea);
        screen.settingsPanel = panelGroup;
        var panel = (RectTransform)panelGroup.transform;

        // Past the safe area, over the status bar and gesture bar too. A tap on it closes the sheet.
        RectTransform scrim = Stretch("Scrim", panel, -200f, -200f, -300f, -300f);
        scrim.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);
        screen.settingsScrim = scrim.gameObject.AddComponent<CanvasGroup>();
        var scrimButton = scrim.gameObject.AddComponent<Button>();
        scrimButton.transition = Selectable.Transition.None;
        scrimButton.navigation = new Navigation { mode = Navigation.Mode.None };

        screen.settingsSheet = Sheet("Sheet", panel, out RectTransform sheet);
        TextIn(TopBand("Title", sheet, 44f, 72f, 48f, 240f), "Settings", s_Regular, 56, s_T.onSurface, TextAnchor.MiddleLeft);
        RectTransform done = Fixed("Done", sheet, Vector2.one, new Vector2(-120f, -80f), new Vector2(176f, 88f));
        screen.settingsDone = FlatButton(done, 44f);
        TextIn(Stretch("Label", done), "Done", s_SemiBold, 38, s_T.primary, TextAnchor.MiddleCenter);

        TextIn(TopBand("Marker", sheet, 148f, 50f, 48f, 48f), "Your marker", s_SemiBold, 38, s_T.onSurface, TextAnchor.MiddleLeft);
        TextIn(TopBand("Marker Detail", sheet, 198f, 44f, 48f, 48f), "Shows where you are on the map", s_Regular, 32,
            s_T.onSurfaceVariant, TextAnchor.MiddleLeft);

        // The blue dot, then the emoji: 5 a row, 3 rows (528 = 3 x 160 + 2 x 24).
        RectTransform markers = TopBand("Markers", sheet, 268f, 528f, 48f, 48f);
        var grid = markers.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(160f, 160f);
        grid.spacing = new Vector2(46f, 24f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 5;
        grid.childAlignment = TextAnchor.UpperCenter;
        int cells = s_Emoji.Length + 1;
        screen.markerCells = new Button[cells];
        screen.markerBackgrounds = new Image[cells];
        RectTransform blue = Child("Blue Dot", markers);
        screen.markerCells[0] = MakeButton(blue, s_T.surfaceVariant, 44f, out screen.markerBackgrounds[0]);
        RectTransform dot = Fixed("Dot", blue, Center, Vector2.zero, new Vector2(64f, 64f));
        Card(dot, Color.white, 32f, true);
        Dot("Fill", dot, Center, Vector2.zero, 46f, s_T.primary);
        for (int i = 1; i < cells; i++)
        {
            RectTransform cell = Child($"Emoji {i}", markers);
            screen.markerCells[i] = MakeButton(cell, s_T.surfaceVariant, 44f, out screen.markerBackgrounds[i]);
            var emoji = Fixed("Emoji", cell, Center, Vector2.zero, new Vector2(96f, 96f)).gameObject.AddComponent<Image>();
            emoji.sprite = s_Emoji[i - 1];
            emoji.preserveAspect = true;
            emoji.raycastTarget = false;
        }

        // Light, dark, or as the phone is.
        TextIn(TopBand("Appearance", sheet, 840f, 50f, 48f, 48f), "Appearance", s_SemiBold, 38, s_T.onSurface, TextAnchor.MiddleLeft);
        RectTransform modes = TopBand("Modes", sheet, 904f, 104f, 48f, 48f);
        Rounded(modes, s_T.surfaceVariant, 52f);
        HorizontalLayoutGroup modeRow = HorizontalRow(modes, 8f, TextAnchor.MiddleCenter);
        modeRow.padding = new RectOffset(8, 8, 8, 8);
        modeRow.childForceExpandWidth = true;
        modeRow.childForceExpandHeight = true;
        string[] modeNames = { "Auto", "Light", "Dark" };
        screen.appearanceButtons = new Button[modeNames.Length];
        screen.appearanceBackgrounds = new Image[modeNames.Length];
        screen.appearanceLabels = new Text[modeNames.Length];
        for (int i = 0; i < modeNames.Length; i++)
        {
            RectTransform mode = Child(modeNames[i], modes);
            mode.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            screen.appearanceButtons[i] = MakeButton(mode, s_T.primaryContainer, 44f, out screen.appearanceBackgrounds[i]);
            screen.appearanceButtons[i].GetComponent<PressFeedback>().pressedScale = 0.97f;
            screen.appearanceLabels[i] = TextIn(Stretch("Label", mode), modeNames[i], s_SemiBold, 36, s_T.primary,
                TextAnchor.MiddleCenter);
        }

        screen.tiltToggle = SwitchRow(sheet, "3D Map", 1032f, "3D map", "Tilt the map and raise the walls",
            out _, out _, out screen.switchTrack, out screen.switchKnob);
        // For demos away from the building. The screen names the building and the entrance.
        screen.pretendToggle = SwitchRow(sheet, "Pretend", 1152f, "Pretend I'm here", "For demos: you're at its entrance",
            out screen.pretendTitle, out screen.pretendDetail, out screen.pretendTrack, out screen.pretendKnob);

        screen.voiceToggle = SwitchRow(sheet, "Voice", 1272f, "Spoken directions",
            "Read turns aloud and answer when you talk", out _, out _, out screen.voiceTrack, out screen.voiceKnob);

        // Where the maps come from, as their licences ask: under everything else, in the small grey of the
        // settings' detail lines. It ends at 1472, inside the open sheet (1480 high).
        Text credits = TextIn(TopBand("Credits", sheet, 1400f, 72f, 48f, 48f),
            "Floor plan: Knight Hacks forge (MIT) · Map data © OpenStreetMap contributors",
            s_Regular, 32, s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        BestFit(credits, 22);
    }

    static void CreateSnackbar(NavigationScreen screen, RectTransform safeArea)
    {
        RectTransform snack = Fixed("Snackbar", safeArea, new Vector2(0.5f, 0f), new Vector2(0f, 300f), new Vector2(1016f, 112f));
        snack.pivot = new Vector2(0.5f, 0f);
        Card(snack, s_T.inverseSurface, 12f, false);
        var group = snack.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;
        screen.snackbar = group;
        screen.snackbarText = TextIn(Stretch("Text", snack, 40f, 40f), "", s_Regular, 38, s_T.onInverseSurface,
            TextAnchor.MiddleLeft);
        BestFit(screen.snackbarText, 28);
    }

    // Talk to the app: a round button at the lower left, which NavigationScreen keeps just above the sheet or card
    // under it. The ring behind it swells with the voice while listening; only its edge shows past the button.
    static void CreateMic(NavigationScreen screen, RectTransform safeArea)
    {
        RectTransform mic = Fixed("Mic", safeArea, Vector2.zero, new Vector2(32f, 300f), new Vector2(112f, 112f));
        mic.pivot = Vector2.zero;
        RectTransform ring = Fixed("Ring", mic, Center, Vector2.zero, new Vector2(112f, 112f));
        Rounded(ring, Alpha(s_T.primary, 0.28f), 56f);
        screen.micRing = ring;
        CardButton(mic, s_T.surface, 56f, true, out Image surface);
        screen.micBackground = surface;
        screen.micIcon = TextIn(Stretch("Icon", mic), IconMic, s_Icons, 56, s_T.primary, TextAnchor.MiddleCenter);
        screen.micButton = mic.GetComponent<CanvasGroup>();
    }

    // While the app opens: the logo, the name and a spinner on the theme's surface, over everything. A canvas of
    // its own, catching taps until it fades.
    static void CreateLaunch(NavigationScreen screen, RectTransform root)
    {
        RectTransform launch = Stretch("Launch", root);
        launch.gameObject.AddComponent<Canvas>();
        launch.gameObject.AddComponent<GraphicRaycaster>();
        screen.launchScreen = launch.gameObject.AddComponent<CanvasGroup>();
        launch.gameObject.AddComponent<Image>().color = s_T.surface;

        RawImage logo = Raw("Logo", launch, s_Logo, Color.white, null);
        RectTransform logoRect = logo.rectTransform;
        logoRect.anchorMin = Center;
        logoRect.anchorMax = Center;
        logoRect.anchoredPosition = new Vector2(0f, 150f);
        logoRect.sizeDelta = new Vector2(280f, 280f);
        logo.gameObject.SetActive(s_Logo != null);
        screen.launchLogo = logoRect;

        TextIn(Fixed("Name", launch, Center, new Vector2(0f, -54f), new Vector2(800f, 110f)), ProductName, s_Bold, 78,
            s_T.onSurface, TextAnchor.MiddleCenter);
        TextIn(Fixed("Tagline", launch, Center, new Vector2(0f, -132f), new Vector2(900f, 60f)), "Indoor maps for UCF",
            s_Regular, 38, s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
        ((RectTransform)MakeSpinner(launch, 76f, 7f, s_T.primary).transform).anchoredPosition = new Vector2(0f, -330f);
    }
}
