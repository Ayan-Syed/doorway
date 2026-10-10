using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using static UIBuild;
using Object = UnityEngine.Object;

// The UI: its camera, the canvas built twice (night, then day), and the night colours recorded from the
// difference. Rule: nothing in CreateCanvas may depend on the theme except colours and pictures. Both copies
// must make the same Graphics, halos and fields in the same order, or RecordNight can't pair them.
public static partial class BuildNavigateScene
{
    static NavigationScreen CreateScreen(Navigator navigator)
    {
        // The input module assigns the Input System's default UI actions (touch included) itself when it has none.
        new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        // The UI's own camera, after the AR camera: it clears to the map's ground while the map fills the screen
        // (the AR camera is off then), and only the depth over the camera feed after. Screen Space - Camera, not
        // Overlay, so the tilted map has room in front and behind instead of being cut off.
        var uiCamera = new GameObject("UI Camera").AddComponent<Camera>();
        uiCamera.orthographic = true;
        uiCamera.cullingMask = 1 << UILayer;
        uiCamera.depth = 10f;
        uiCamera.nearClipPlane = 0.3f;
        uiCamera.farClipPlane = 300f;
        uiCamera.clearFlags = CameraClearFlags.SolidColor;
        uiCamera.allowHDR = false;
        uiCamera.allowMSAA = false;

        // Night first: the day screen is the one kept, and the night one only lends its colours.
        string day = s_T.name;
        if (!UseTheme(NightTheme))
            return null;
        NavigationScreen night = CreateCanvas(navigator, uiCamera);
        if (!UseTheme(day))
            return null;
        NavigationScreen screen = CreateCanvas(navigator, uiCamera);

        uiCamera.backgroundColor = s_T.mapGround;
        screen.themeName = day;
        screen.nightThemeName = NightTheme;
        screen.uiCamera = uiCamera;
        screen.location = screen.gameObject.AddComponent<UserLocation>();
        screen.voiceInput = screen.gameObject.AddComponent<VoiceInput>();
        screen.voiceOutput = screen.gameObject.AddComponent<VoiceOutput>();
        screen.markerSprites = s_Emoji;
        screen.themeColors = screen.gameObject.AddComponent<ThemeColors>();

        bool recorded = RecordNight(screen.themeColors, screen.gameObject, night.gameObject);
        Object.DestroyImmediate(night.gameObject);
        return recorded ? screen : null;
    }

    static NavigationScreen CreateCanvas(Navigator navigator, Camera uiCamera)
    {
        var root = new GameObject("Navigation Screen", typeof(RectTransform));
        var rootRect = (RectTransform)root.transform;
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = uiCamera;
        canvas.planeDistance = 100f;
        // Portrait only, so the layout is sized by the screen width.
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.matchWidthOrHeight = 0f;
        root.AddComponent<GraphicRaycaster>();
        var screen = root.AddComponent<NavigationScreen>();
        screen.navigator = navigator;

        // The map runs under the status bar and gesture bar; everything else keeps to the safe area. A canvas of
        // its own, so the map moving doesn't rebuild the panels over it. Its ground Image keeps raycasts on: it is
        // what catches the map's drags and taps (FloorMap, on the same object, handles them).
        RectTransform window = Stretch("Map", rootRect);
        window.gameObject.AddComponent<Canvas>();
        window.gameObject.AddComponent<GraphicRaycaster>();
        window.gameObject.AddComponent<Image>().color = s_T.mapGround;
        screen.map = CreateFloorMap(window, 1f, true);
        // Out far enough for the whole campus. FloorMap's own default (0.6) is the minimap's: it would stop the
        // zoom long before the 900 m campus view (zoom 0.12) or the launch view (about 0.055), so without this you
        // couldn't zoom out past BA1.
        screen.map.minZoom = 0.04f;

        RectTransform safeArea = Stretch("Safe Area", rootRect);
        safeArea.gameObject.AddComponent<SafeArea>();
        screen.safeArea = safeArea;

        // Later siblings draw on top: guidance lowest, the snackbar over every panel.
        CreateGuide(screen, safeArea);
        CreateArrived(screen, safeArea);
        CreateDirections(screen, safeArea);
        CreateHome(screen, safeArea);
        CreateMic(screen, safeArea);
        CreateSettings(screen, safeArea);
        CreateSnackbar(screen, safeArea);
        CreateLaunch(screen, rootRect);

        // Google Sans's lines are tall for its size: a line a little taller than its box draws nothing at all
        // unless it may overflow. Best-fit text shrinks instead.
        foreach (Text text in root.GetComponentsInChildren<Text>(true))
        {
            if (!text.resizeTextForBestFit)
                text.verticalOverflow = VerticalWrapMode.Overflow;
        }
        // Only the UI camera draws it.
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = UILayer;
        return screen;
    }

    // Pairs the day and night screens part by part and keeps, in colors, whatever differs.
    static bool RecordNight(ThemeColors colors, GameObject day, GameObject night)
    {
        Graphic[] days = day.GetComponentsInChildren<Graphic>(true);
        Graphic[] nights = night.GetComponentsInChildren<Graphic>(true);
        if (days.Length != nights.Length)
        {
            Debug.LogError($"[Navigation] The day and night screens have {days.Length} and {nights.Length} parts.");
            return false;
        }
        for (int i = 0; i < days.Length; i++)
        {
            Graphic d = days[i], n = nights[i];
            if (d.name != n.name || d.GetType() != n.GetType())
            {
                Debug.LogError($"[Navigation] The day and night screens differ at {d.name} and {n.name}.");
                return false;
            }
            // Walls keep two colours of their own.
            if (d is WallMesh wall)
            {
                var nightWall = (WallMesh)n;
                if (wall.color != nightWall.color || wall.sideColor != nightWall.sideColor)
                    colors.walls.Add(new ThemeColors.WallColor
                    {
                        wall = wall, dayTop = wall.color, nightTop = nightWall.color,
                        daySide = wall.sideColor, nightSide = nightWall.sideColor,
                    });
                continue;
            }
            if (d.color != n.color)
                colors.graphics.Add(new ThemeColors.GraphicColor { graphic = d, day = d.color, night = n.color });
            // The map's picture and the launch logo.
            if (d is RawImage image && image.texture != ((RawImage)n).texture)
                colors.textures.Add(new ThemeColors.TextureSwap { image = image, day = image.texture, night = ((RawImage)n).texture });
        }

        TextHalo[] dayHalos = day.GetComponentsInChildren<TextHalo>(true);
        TextHalo[] nightHalos = night.GetComponentsInChildren<TextHalo>(true);
        InputField[] dayFields = day.GetComponentsInChildren<InputField>(true);
        InputField[] nightFields = night.GetComponentsInChildren<InputField>(true);
        if (dayHalos.Length != nightHalos.Length || dayFields.Length != nightFields.Length)
        {
            Debug.LogError("[Navigation] The day and night screens have different halos or fields.");
            return false;
        }
        for (int i = 0; i < dayHalos.Length; i++)
        {
            if (dayHalos[i].color != nightHalos[i].color)
                colors.halos.Add(new ThemeColors.HaloColor { halo = dayHalos[i], day = dayHalos[i].color, night = nightHalos[i].color });
        }
        for (int i = 0; i < dayFields.Length; i++)
        {
            InputField d = dayFields[i], n = nightFields[i];
            if (d.caretColor != n.caretColor || d.selectionColor != n.selectionColor)
                colors.fields.Add(new ThemeColors.FieldColor
                {
                    field = d, dayCaret = d.caretColor, nightCaret = n.caretColor,
                    daySelection = d.selectionColor, nightSelection = n.selectionColor,
                });
        }

        Debug.Log($"[Navigation] Night colours: {colors.graphics.Count} parts, {colors.textures.Count} pictures, " +
                  $"{colors.halos.Count} halos, {colors.fields.Count} fields, {colors.walls.Count} walls.");
        return true;
    }
}
