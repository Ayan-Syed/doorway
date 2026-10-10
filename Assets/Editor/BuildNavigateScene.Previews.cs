using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

[assembly: InternalsVisibleTo("Doorway.Tests.EditMode")]

// Screenshots without a phone: the scene is built as for an APK, then every screen is put up through
// NavigationScreen's Preview hooks and rendered, by day and by night, to Builds/Previews/<theme>/<name>.png at a
// Galaxy Note 10's 1080 x 2280. There's no AR in the editor, so a grey camera over the floor plan laid on the ground
// stands in for the camera feed, and the route and the destination pin have something to stand on.
//   Unity.exe -batchmode -projectPath <project> -executeMethod BuildNavigateScene.RenderPreviews -logFile <log>
//   Not -nographics: it renders. It exits by itself: 0 when every screen rendered, 1 otherwise.
// The menu item does the same, then reopens the saved scene: the renders change the open one.
public static partial class BuildNavigateScene
{
    // The walk the screens show: from room 113's door to room 121.
    const string PreviewFrom = "113";
    const string PreviewTo = "121";
    // The phone held up at eye height, pitched down a little.
    const float PreviewEyeHeight = 1.4f;
    const float PreviewPitch = 22f;
    // The phone's location as indoor GPS gives it: a few metres off the door, this far off at most.
    const float PreviewAccuracy = 6f;
    // The phone's status bar and gesture bar, which the safe area keeps clear of.
    const float PreviewStatusBar = 86f;
    const float PreviewGestureBar = 40f;
    static readonly Color PreviewCameraColor = new Color32(120, 122, 126, 255);
    static readonly Color PreviewFloorColor = new Color32(150, 148, 142, 255);

    public static void RenderPreviews()
    {
        try
        {
            if (!CreateScene(DefaultTheme, out NavigationScreen screen))
            {
                EditorApplication.Exit(1);
                return;
            }
            int failed = RenderAll(screen);
            EditorApplication.Exit(failed == 0 ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Navigation/Render Previews")]
    static void RenderPreviewsFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        int failed = -1;
        try
        {
            if (CreateScene(DefaultTheme, out NavigationScreen screen))
                failed = RenderAll(screen);
        }
        finally
        {
            if (File.Exists(ScenePath))
                EditorSceneManager.OpenScene(ScenePath);
        }
        if (failed >= 0)
            EditorUtility.DisplayDialog("Navigation", failed == 0
                ? $"Rendered every screen to {PreviewFolder}."
                : $"{failed} screens didn't render: see the Console.", "OK");
    }

    // For the flow tests: the scene built as for an APK, its screen laid out as a phone's. Null if it can't be built.
    internal static NavigationScreen BuildForTests()
    {
        if (!CreateScene(DefaultTheme, out NavigationScreen screen))
            return null;
        LayOutLikeAPhone(screen);
        return screen;
    }

    // The canvas in the world at a Galaxy Note 10's size, with the status and gesture bars the safe area avoids
    // (SafeArea only runs on a phone).
    static RectTransform LayOutLikeAPhone(NavigationScreen screen)
    {
        var canvas = screen.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        if (screen.TryGetComponent(out CanvasScaler scaler))
            scaler.enabled = false;
        var rect = (RectTransform)canvas.transform;
        rect.SetPositionAndRotation(new Vector3(5000f, 0f, 0f), Quaternion.identity);
        rect.localScale = Vector3.one;
        rect.sizeDelta = new Vector2(PreviewWidth, PreviewHeight);
        foreach (SafeArea area in screen.GetComponentsInChildren<SafeArea>(true))
        {
            var safe = (RectTransform)area.transform;
            safe.anchorMin = Vector2.zero;
            safe.anchorMax = Vector2.one;
            safe.offsetMin = new Vector2(0f, PreviewGestureBar);
            safe.offsetMax = new Vector2(0f, -PreviewStatusBar);
        }
        Canvas.ForceUpdateCanvases();
        return rect;
    }

    // Every screen by day (with the shield marker) and by night (with the dot). Returns how many failed.
    static int RenderAll(NavigationScreen screen)
    {
        using (var rig = new PreviewRig(screen))
        {
            int failed = rig.Screens(screen.themeName, 4);
            screen.PreviewAppearance(2);
            failed += rig.Screens(screen.nightThemeName, 0);
            // Day again: the night run recoloured the AR materials, which are assets.
            screen.PreviewAppearance(1);
            Debug.Log($"[Navigation] Previews in {PreviewFolder}: {(failed == 0 ? "all rendered" : $"{failed} failed")}.");
            return failed;
        }
    }

    // The screen laid out as a phone's, two cameras rendering into one picture, and the walk the screens show.
    sealed class PreviewRig : IDisposable
    {
        readonly NavigationScreen m_Screen;
        readonly Navigator m_Navigator;
        readonly RectTransform m_Canvas;
        readonly RenderTexture m_Target;
        readonly Camera m_UI;
        readonly Camera m_AR;
        readonly List<Object> m_Made = new List<Object>();
        readonly PlanPlace m_To;
        readonly PlanPlace m_From;
        readonly PlanDoor m_Door;
        readonly Route m_Route;
        readonly Vector2 m_Spot, m_Near, m_Outside;
        readonly PlanPlace m_Entrance;
        readonly string m_Neighbour;
        string m_Folder;
        int m_Failed;

        public PreviewRig(NavigationScreen screen)
        {
            m_Screen = screen;
            m_Navigator = screen.navigator;
            m_Navigator.Load();
            FloorPlan plan = m_Navigator.Plan;
            m_From = plan.places.Find(p => p.name == PreviewFrom && p.doors.Count > 0);
            m_To = plan.places.Find(p => p.name == PreviewTo);
            if (m_From == null || m_To == null)
                throw new InvalidOperationException($"The plan has no room {PreviewFrom} or {PreviewTo} to walk between.");
            m_Door = m_From.doors[0];
            m_Route = m_Navigator.PlanRoute(m_Door, m_To);
            if (m_Route == null)
                throw new InvalidOperationException($"No route from {PreviewFrom} to {PreviewTo}.");
            m_Spot = plan.NodePosition(m_Door.node);
            m_Near = m_Spot + new Vector2(2f, 1.5f);
            // Outside, out past an entrance's doors (the east one's, if there is one).
            PlanPlace exit = plan.places.Find(p => p.kind == "exit" && p.doors.Count > 0 && p.name.StartsWith("East"))
                             ?? plan.places.Find(p => p.kind == "exit" && p.doors.Count > 0);
            m_Entrance = exit;
            m_Outside = exit != null
                ? plan.NodePosition(exit.doors[0].node) + exit.doors[0].Direction.normalized * 14f
                : m_Spot + new Vector2(0f, 80f);

            m_Canvas = LayOutLikeAPhone(screen);

            m_Target = new RenderTexture(PreviewWidth, PreviewHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            m_Target.Create();

            // The UI camera as the screen leaves it: it clears to the map's ground on the map, depth only over the
            // camera feed. Rendered by hand.
            m_UI = screen.uiCamera;
            m_UI.orthographic = true;
            m_UI.orthographicSize = PreviewHeight / 2f;
            m_UI.transform.SetPositionAndRotation(m_Canvas.position + new Vector3(0f, 0f, -5000f), Quaternion.identity);
            m_UI.nearClipPlane = 1f;
            m_UI.farClipPlane = 10000f;
            m_UI.targetTexture = m_Target;
            m_UI.enabled = false;

            // The camera feed's stand-in: everything but the UI, over grey.
            var arObject = new GameObject("Preview AR Camera");
            m_Made.Add(arObject);
            m_AR = arObject.AddComponent<Camera>();
            m_AR.fieldOfView = 64f;
            m_AR.nearClipPlane = 0.05f;
            m_AR.farClipPlane = 200f;
            m_AR.clearFlags = CameraClearFlags.SolidColor;
            m_AR.backgroundColor = PreviewCameraColor;
            m_AR.cullingMask = ~(1 << UILayer);
            m_AR.targetTexture = m_Target;
            m_AR.enabled = false;
            StandInFloor(plan);

            // Puts the screen up once (and fits its sheets to this safe area), then finds the building next door.
            screen.PreviewSnackbar(null);
            m_Neighbour = Neighbour(screen.map.Campus);
        }

        // The floor's walls drawn on a grey ground, in plan metres, under the plan root put back at the origin.
        void StandInFloor(FloorPlan plan)
        {
            m_Navigator.planRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            m_Navigator.planRoot.localScale = Vector3.one;
            PlanImage image = plan.image;
            float width = image.width * image.metersPerPixel, height = image.height * image.metersPerPixel;
            var center = new Vector3(image.left + width / 2f, 0f, image.top - height / 2f);
            Mesh quad = QuadMesh("Quad", -0.5f);
            Quad("Preview Floor", quad, null, PreviewFloorColor, 2000, center + new Vector3(0f, -0.002f, 0f),
                new Vector3(400f, 400f, 1f));
            if (s_PlanImage != null)
                Quad("Preview Walls", quad, s_PlanImage, new Color(0.12f, 0.12f, 0.14f, 0.9f), 2500,
                    center + new Vector3(0f, -0.001f, 0f), new Vector3(width, height, 1f));
        }

        void Quad(string name, Mesh mesh, Texture texture, Color color, int queue, Vector3 position, Vector3 scale)
        {
            var material = new Material(s_Shader) { mainTexture = texture, color = color, renderQueue = queue };
            m_Made.Add(material);
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            m_Made.Add(go);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(90f, 0f, 0f));   // lying on the ground
            go.transform.localScale = scale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        // The named building nearest the floor's, for the "another building" screen.
        static string Neighbour(CampusData campus)
        {
            if (campus?.indoor == null)
                return null;
            string best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (CampusData.Building building in campus.buildings)
            {
                if (building.indoor || !FloorMap.Drawable(building.name))
                    continue;
                float distance = (building.Spot - campus.indoor.Spot).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = building.name;
                }
            }
            return best;
        }

        // Every screen, into Builds/Previews/<theme>. marker: 0 for the dot, n for the nth shape.
        public int Screens(string theme, int marker)
        {
            m_Folder = Path.Combine(PreviewFolder, theme);
            if (Directory.Exists(m_Folder))
                Directory.Delete(m_Folder, true);   // so no picture from an older run is left to mislead
            Directory.CreateDirectory(m_Folder);
            m_Failed = 0;
            NavigationScreen s = m_Screen;
            Look(m_Spot, Navigator.Heading(-m_Door.Direction));
            s.PreviewMarker(marker);
            s.PreviewLocation(m_Near, PreviewAccuracy);

            Capture("0-launch", () =>
            {
                s.PreviewCampus(true);
                if (s.launchScreen != null)
                    foreach (Spinner spinner in s.launchScreen.GetComponentsInChildren<Spinner>(true))
                        spinner.SetTime(0.45f);   // a still frame
            });
            Capture("0b-campus", () => s.PreviewCampus(false));
            Capture("0c-approach", () => s.PreviewApproach(230f));
            if (m_Neighbour != null)
                Capture("0d-building", () => s.PreviewBuilding(m_Neighbour));
            Capture("1-home", () => s.PreviewHome(0, null, null));
            Capture("1b-floors", s.PreviewFloorMenu);
            Capture("1c-settings", s.PreviewSettings);
            Capture("2-directory", () => s.PreviewHome(1, null, null));
            Capture("2c-directory-open", () => s.PreviewDirectoryOpen("Other spaces", "Room 145"));
            Capture("2b-restrooms", () => s.PreviewHome(1, null, "restroom"));
            Capture("3-search", () => s.PreviewHome(2, "13", null));
            Capture("3b-search-buildings", () => s.PreviewHome(2, "eng", null));
            Capture("4-place", () => s.PreviewPlace(m_To));
            Capture("5-directions-gps", () =>
            {
                s.PreviewLocation(m_Near, PreviewAccuracy);
                s.PreviewDirections(m_To, true);
            });
            Capture("5a-finding", () => s.PreviewFinding(m_To));
            Capture("5b-start", () => s.PreviewStart(m_To));
            Capture("5c-directions-outside", () =>
            {
                s.PreviewLocation(m_Outside, PreviewAccuracy);
                s.PreviewDirections(m_To, true);
            });
            Capture("6-line-up", () =>
            {
                s.PreviewLocation(m_Near, PreviewAccuracy);
                s.PreviewAlign(m_To, m_From, m_Door, true);
            });
            Capture("6b-line-up-waiting", () => s.PreviewAlign(m_To, m_From, m_Door, false));
            Capture("7-guide", () => GuideAt(3f));
            Capture("8-guide-turn-now", () => GuideAt(FirstTurn() - 0.8f));
            Capture("8d-guide-big-minimap", () => { GuideAt(3f); m_Screen.PreviewMiniMapBig(); });
            Capture("8e-realign", () => { GuideAt(FirstTurn() + 3f); m_Screen.PreviewRealign(); });
            // The same turn coming up while the Localizer is unsure, then lost: no turn, a faint (then no) ribbon.
            Capture("8b-guide-unsure", () => GuideIndoorAt(FirstTurn() - 0.8f, LocalizerState.Converging, 6f));
            Capture("8c-guide-lost", () => GuideIndoorAt(FirstTurn() - 0.8f, LocalizerState.Lost, 10f));
            Capture("9-arriving", () => GuideAt(m_Route.length - 5f));
            Capture("10-arrived", () =>
            {
                m_Navigator.Preview(m_Door, m_To, m_Route.length, 0f, true);
                Look(m_Navigator.UserPosition, m_Navigator.UserHeading);
                s.PreviewArrived(m_To);
            });

            // Indoor positioning: the snap card at an entrance, the dot in each state a few metres down the
            // hallway (halo as wide as the estimate is unsure), and the team's debug overlay.
            Vector2 hallway = m_Route.PointAt(Mathf.Min(6f, m_Route.length / 2f));
            float facing = Navigator.Heading(m_Route.DirectionAt(Mathf.Min(6f, m_Route.length / 2f)));
            if (m_Entrance != null)
                Capture("11-entrance-prompt", () => s.PreviewEntrance(m_Entrance));
            Capture("12-indoor-converging", () => s.PreviewIndoor(LocalizerState.Converging, hallway, facing, 5f, true));
            Capture("12b-indoor-localized", () => s.PreviewIndoor(LocalizerState.Localized, hallway, facing, 1.2f, false));
            Capture("12c-indoor-lost", () => s.PreviewIndoor(LocalizerState.Lost, hallway, facing, 9f, false));
            Capture("13-debug-overlay", () => s.PreviewDebug(hallway, facing, 4f));
            // The other building: HEC's floor in place of BA1's, under the same campus map, then back.
            if (s.SwitchFloor(1))
            {
                Capture("14-hec-home", () => s.PreviewHome(0, null, null));
                Capture("14b-hec-directory", () => s.PreviewHome(1, null, null));
                PlanPlace hall = m_Navigator.Plan.places.Find(p => p.id == "125");
                if (hall != null)
                    Capture("14c-hec-place", () => s.PreviewPlace(hall));
                s.SwitchFloor(0);
            }
            return m_Failed;
        }

        // Walking, progress metres along the route, looking along it.
        void GuideAt(float progress)
        {
            m_Navigator.Preview(m_Door, m_To, Mathf.Clamp(progress, 0f, m_Route.length), 0f, false);
            Look(m_Navigator.UserPosition, m_Navigator.UserHeading);
            m_Screen.PreviewGuide(m_To, null);
        }

        void GuideIndoorAt(float progress, LocalizerState state, float spread)
        {
            m_Navigator.Preview(m_Door, m_To, Mathf.Clamp(progress, 0f, m_Route.length), 0f, false);
            Look(m_Navigator.UserPosition, m_Navigator.UserHeading);
            m_Screen.PreviewGuideIndoor(m_To, state, spread);
        }

        // Where the route first turns, past its first few metres (its middle if it never does).
        float FirstTurn()
        {
            foreach (RouteTurn turn in m_Route.turns)
                if (turn.distance > 4f)
                    return turn.distance;
            return m_Route.length / 2f;
        }

        // The stand-in camera at a plan point, facing a heading (degrees clockwise from plan up).
        void Look(Vector2 at, float heading)
        {
            m_AR.transform.SetPositionAndRotation(new Vector3(at.x, PreviewEyeHeight, at.y),
                Quaternion.Euler(PreviewPitch, heading, 0f));
        }

        // One screen: set up, laid out, the camera feed's stand-in and then the UI over it, saved as a PNG. A screen
        // that throws is logged and counted, and the rest still render.
        void Capture(string name, Action setup)
        {
            try
            {
                m_Screen.PreviewSnackbar(null);   // a message from the last screen doesn't carry over
                setup();
                for (int i = 0; i < 2; i++)
                {
                    Canvas.ForceUpdateCanvases();
                    LayoutRebuilder.ForceRebuildLayoutImmediate(m_Canvas);
                }
                if (m_Navigator.pin != null)
                    m_Navigator.pin.FaceCamera(m_AR.transform.position, 0.45f);
                m_AR.Render();
                m_UI.Render();
                Save(Path.Combine(m_Folder, name + ".png"));
            }
            catch (Exception e)
            {
                m_Failed++;
                Debug.LogError($"[Navigation] Preview {name} failed: {e}");
            }
        }

        void Save(string path)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = m_Target;
            var picture = new Texture2D(PreviewWidth, PreviewHeight, TextureFormat.RGB24, false);
            try
            {
                picture.ReadPixels(new Rect(0f, 0f, PreviewWidth, PreviewHeight), 0, 0);
                picture.Apply();
                File.WriteAllBytes(path, picture.EncodeToPNG());
                Debug.Log($"[Navigation] Preview {path}");
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(picture);
            }
        }

        public void Dispose()
        {
            if (m_UI != null)
                m_UI.targetTexture = null;
            foreach (Object made in m_Made)
                if (made != null)
                    Object.DestroyImmediate(made);
            if (m_Target != null)
            {
                m_Target.Release();
                Object.DestroyImmediate(m_Target);
            }
        }
    }
}
