using System;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using static UIBuild;
using Object = UnityEngine.Object;

// Builds Assets/Scenes/Navigate.unity from nothing, every time:
//   AR Session; XR Origin with an AR Plane Manager for the floor height; Navigator with the plan root (route ribbon,
//   destination pin); a camera for the UI; and the navigation screen. That screen holds a launch screen, the map
//   (OpenStreetMap campus with buildings standing up, and the indoor floor once zoomed in with walls standing up),
//   a search bar, filter chips, a floor menu, the directory in a sheet, a place's sheet, the directions card, the
//   start door from the phone's location, lining up, turn-by-turn guidance over the camera, arrived, and settings
//   (your marker, light or dark, the 3D map).
// Layout is in reference pixels, 1080 wide. Never hand-edit the scene: change this builder and run it again.
// BuildApk builds the APK; RenderPreviews renders every screen by day and by night to Builds/Previews/<theme>,
// without a phone.
public static partial class BuildNavigateScene
{
    const string ScenePath = "Assets/Scenes/Navigate.unity";
    const string ApkPath = "Builds/BuildingNav-Navigate.apk";
    const string PreviewFolder = "Builds/Previews";
    const string DataFolder = "Assets/Navigation/Data";
    const string PlanJsonPath = DataFolder + "/ba1-floor1.json";
    const string PlanImagePath = DataFolder + "/ba1-floor1-plan.png";
    const string PlaceMapPath = DataFolder + "/ba1-floor1-places.bytes";
    const string WallsPath = DataFolder + "/ba1-floor1-walls.bytes";
    const string CampusGeometryPath = DataFolder + "/ucf-campus.bytes";
    const string CampusInfoPath = DataFolder + "/ucf-campus.json";
    const string EmojiFolder = UIFolder + "/Emoji";
    const string MeshFolder = "Assets/Navigation/Meshes";
    const string ProductName = "Doorway";
    const string DefaultTheme = "campus";
    const string NightTheme = "knight";

    // Unity's built-in "UI" layer. Only the UI camera draws it.
    const int UILayer = 5;
    static readonly Vector2 Center = new Vector2(0.5f, 0.5f);

    // The preview size: a Galaxy Note 10 screen.
    const int PreviewWidth = 1080;
    const int PreviewHeight = 2280;

    // Material Icons code points (MaterialIcons-Regular.ttf). Kept as escapes: pasted glyphs are invisible.
    const string IconSearch = "";
    const string IconBack = "";
    const string IconClose = "";
    const string IconCheck = "";
    const string IconArrowUp = "";
    const string IconStraight = "";
    const string IconTurnLeft = "";
    const string IconSettings = "";
    const string IconDropDown = "";
    const string IconDone = "";
    const string IconMyLocation = "";
    const string IconWholeFloor = "";

    // The markers Settings offers after the blue dot: Noto Color Emoji pictures (UI/Emoji/LICENSE.txt).
    static readonly string[] MarkerEmoji =
    {
        "1f642", "1f60e", "1f913", "1f393", "1f431", "1f436", "1f422",
        "1f984", "1f47b", "1f680", "1f525", "26a1", "2694", "1f45f",
    };

    // The current theme: every helper takes its colours from it, so whichever theme is current when a
    // GameObject is made gives it its colours.
    static Theme s_T;
    static Shader s_Shader, s_RibbonShader, s_HighlightShader;
    static Texture2D s_PlanImage, s_Basemap, s_RouteMask, s_Logo;
    static TextAsset s_PlaceMap, s_Walls, s_CampusGeometry, s_CampusInfo;
    static Material s_HighlightMaterial, s_WallMaterial;
    static Sprite s_Pin, s_Cone;
    static Sprite[] s_Emoji;

    [MenuItem("Navigation/Build Navigate Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        if (CreateScene(DefaultTheme, out _))
            EditorUtility.DisplayDialog("Navigation",
                $"Built {ScenePath} and set it as the only scene in Build Settings.", "OK");
    }

    // Unity.exe -batchmode -projectPath <project> -buildTarget Android -executeMethod BuildNavigateScene.BuildApk
    //     [-theme paper] [-versionCode N]
    // (No -quit needed: this exits by itself, 0 on success and 1 on any failure.) Signing: see ReleaseSettings.
    public static void BuildApk()
    {
        try
        {
            // A fresh clone has Unity's defaults (no ARCore, com.DefaultCompany, Mono): set ours first.
            ProjectSetup.Apply();
            if (!ValidatePlan() || !CreateScene(ThemeArgument(), out _))
            {
                EditorApplication.Exit(1);
                return;
            }
            BuildReport report;
            using (new ReleaseSettings())
            {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = ApkPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.None,
                });
            }
            Debug.Log($"[Navigation] Build {report.summary.result}: {ApkPath}, {report.summary.totalErrors} errors.");
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    // -theme <name> on the command line picks the day theme; campus without it. The night theme is always knight.
    static string ThemeArgument()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-theme")
                return args[i + 1];
        }
        return DefaultTheme;
    }

    // Bad data (a node that isn't there, a place grid for other places, a truncated walls or campus file...) would
    // otherwise only fail on the phone. Every data file is checked (BuildNavigateScene.Validate) before anything
    // is built.
    static bool ValidatePlan()
    {
        var json = AssetDatabase.LoadAssetAtPath<TextAsset>(PlanJsonPath);
        if (json == null)
        {
            Debug.LogError($"[Navigation] Missing {PlanJsonPath}: not building.");
            return false;
        }
        return ValidatePlan(json);
    }

    static bool ValidatePlan(TextAsset json)
    {
        string problem = DataProblem(json);
        if (problem == null)
            return true;
        Debug.LogError($"[Navigation] Bad app data: {problem} Fix the data (python Tools/ba1-floor1/make_data.py " +
                       "--campus) and build again; not building.");
        return false;
    }

    // Builds and saves the scene in the given day theme (the night theme is recorded alongside). False, with the
    // reason logged, if anything it needs is missing.
    static bool CreateScene(string theme, out NavigationScreen screen)
    {
        screen = null;
        if (!LoadStyle())
            return false;

        // Google Sans in place of Inter: rounder, the map-app look.
        s_Regular = LoadFont("GoogleSans-400.ttf", s_Regular);
        s_SemiBold = LoadFont("GoogleSans-500.ttf", s_SemiBold);
        s_Bold = LoadFont("GoogleSans-700.ttf", s_Bold);

        var planJson = AssetDatabase.LoadAssetAtPath<TextAsset>(PlanJsonPath);
        s_PlaceMap = AssetDatabase.LoadAssetAtPath<TextAsset>(PlaceMapPath);
        s_Walls = AssetDatabase.LoadAssetAtPath<TextAsset>(WallsPath);
        s_CampusGeometry = AssetDatabase.LoadAssetAtPath<TextAsset>(CampusGeometryPath);
        s_CampusInfo = AssetDatabase.LoadAssetAtPath<TextAsset>(CampusInfoPath);
        if (s_CampusGeometry == null || s_CampusInfo == null)
        {
            Debug.LogWarning($"[Navigation] Missing {CampusGeometryPath} or {CampusInfoPath}: the map shows the floor alone.");
            s_CampusGeometry = null;
            s_CampusInfo = null;
        }

        s_Shader = Shader.Find("Navigation/Scrolling Unlit");
        s_RibbonShader = Shader.Find("Navigation/Route Ribbon");
        s_HighlightShader = Shader.Find("Navigation/Map Highlight");
        Shader wallShader = Shader.Find("Navigation/Map Walls");
        if (planJson == null || s_PlaceMap == null || s_Walls == null
            || s_Shader == null || s_RibbonShader == null || s_HighlightShader == null || wallShader == null)
        {
            Debug.LogError($"[Navigation] Missing {PlanJsonPath}, {PlaceMapPath}, {WallsPath} or one of the Navigation shaders.");
            return false;
        }
        if (!ValidatePlan(planJson))   // the menu and the previews too, not just BuildApk
            return false;

        if (!ImportTextures())
            return false;
        s_Emoji = ImportEmoji();
        if (s_Pin == null || s_Cone == null || s_RouteMask == null || s_Emoji == null)
        {
            Debug.LogError($"[Navigation] Missing a sprite in {UIFolder} or an emoji in {EmojiFolder}.");
            return false;
        }

        // Both maps' pictures; the day theme last, so it's the one the builder colours with.
        if (!UseTheme(NightTheme) || !UseTheme(theme))
            return false;

        s_HighlightMaterial = MaterialAsset("MapHighlight", s_HighlightShader);
        s_HighlightMaterial.SetFloat("_FillAlpha", 0.16f);
        s_HighlightMaterial.SetFloat("_StrokeWidth", 2.2f);
        s_WallMaterial = MaterialAsset("MapWalls", wallShader);

        PlayerSettings.productName = ProductName;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
        PlayerSettings.SplashScreen.show = false;   // the app's own launch screen replaces Unity's
        SetAppIcons(s_T.name);

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        ARSession session = ARSceneParts.CreateARSession();
        XROrigin origin = ARSceneParts.CreateXROrigin();
        Camera arCamera = origin.Camera;
        arCamera.farClipPlane = 100f;   // Hallways are long: the route and the destination beam can be 50 m away.
        arCamera.nearClipPlane = 0.05f;
        arCamera.cullingMask &= ~(1 << UILayer);   // The UI has a camera of its own.

        // Planes are tracked (Navigator reads the floor's height from them) but never drawn: no prefab.
        var planes = origin.gameObject.AddComponent<ARPlaneManager>();
        planes.requestedDetectionMode = PlaneDetectionMode.Horizontal;

        Navigator navigator = CreateNavigator(planJson, arCamera.transform, planes);
        navigator.signReader = CreateSignReader(arCamera);
        navigator.session = session;

        screen = CreateScreen(navigator);
        if (screen == null)
            return false;

        EnsureFolder("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        // This is the only builder, so this is always the one scene a manual File > Build ships.
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log($"[Navigation] Built {ScenePath} ({s_T.name}, night {NightTheme}).");
        return true;
    }

    // Makes a theme current: its colours for the helpers, and its map picture and logo.
    static bool UseTheme(string name)
    {
        Theme.Set(name);
        s_T = Theme.Current;
        s_Basemap = ImportBasemap(s_T.name);
        if (s_Basemap == null)
        {
            Debug.LogError($"[Navigation] Missing the {s_T.name} map picture in {DataFolder}.");
            return false;
        }
        s_Logo = ImportLogo(s_T.name);
        return true;
    }

    static Font LoadFont(string file, Font fallback)
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontFolder + "/" + file);
        return font != null ? font : fallback;
    }

    static Color Alpha(Color color, float a)
    {
        color.a = a;
        return color;
    }

    static Texture2D UITexture(string file) => AssetDatabase.LoadAssetAtPath<Texture2D>(UIFolder + "/" + file);
}
