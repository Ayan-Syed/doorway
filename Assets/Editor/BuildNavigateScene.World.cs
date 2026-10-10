using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using static UIBuild;

// What's in AR: the Navigator, its plan root, the route on the floor and the destination pin; and the Localizer
// beside it, which positions the user indoors.
public static partial class BuildNavigateScene
{
    const string WalkablePath = DataFolder + "/ba1-floor1-walkable.bytes";

    static Navigator CreateNavigator(TextAsset planJson, Transform arCamera, ARPlaneManager planes)
    {
        var go = new GameObject("Navigator");
        var navigator = go.AddComponent<Navigator>();

        // The floor plan's frame: Navigator places it in AR space.
        var planRoot = new GameObject("Plan Root").transform;
        planRoot.SetParent(go.transform, false);

        // The band in the route colour, chevrons flowing toward the destination at a slow walk.
        Material ribbonMaterial = MaterialAsset("RouteRibbon", s_RibbonShader);
        ribbonMaterial.mainTexture = s_RouteMask;
        ribbonMaterial.SetColor("_Color", s_T.Ribbon);
        ribbonMaterial.SetColor("_ChevronColor", s_T.RibbonChevron);
        ribbonMaterial.SetColor("_GlowColor", s_T.ribbonGlow);
        ribbonMaterial.SetFloat("_ScrollSpeed", 1.2f);
        ribbonMaterial.renderQueue = 3002;

        var ribbonGo = new GameObject("Route Ribbon", typeof(MeshFilter), typeof(MeshRenderer));
        ribbonGo.transform.SetParent(planRoot, false);
        var ribbonRenderer = ribbonGo.GetComponent<MeshRenderer>();
        ribbonRenderer.sharedMaterial = ribbonMaterial;
        NoShadows(ribbonRenderer);
        var ribbon = ribbonGo.AddComponent<RouteRibbon>();   // builds its mesh at runtime

        navigator.planJson = planJson;
        // Where a person can stand (Tools/ba1-floor1/walkable.py). Without it the app guides as before: no walls.
        navigator.walkable = AssetDatabase.LoadAssetAtPath<TextAsset>(WalkablePath);
        if (navigator.walkable == null)
            Debug.LogWarning($"[Navigation] No {WalkablePath}: guiding without walls.");
        navigator.arCamera = arCamera;
        navigator.planeManager = planes;
        navigator.planRoot = planRoot;
        navigator.ribbon = ribbon;
        navigator.pin = CreatePin(planRoot, arCamera);
        return navigator;
    }

    // Indoor positioning, on the Navigator's object: the plan's walls for matching, ARCore's feature points for
    // phones without depth, the step counter, and the plate reader the Navigator has. Made before the screen, which
    // finds it.
    static Localizer CreateLocalizer(Navigator navigator, ARPointCloudManager points)
    {
        var localizer = navigator.gameObject.AddComponent<Localizer>();
        localizer.navigator = navigator;
        localizer.walls = s_Walls;
        localizer.pointClouds = points;
        localizer.signReader = navigator.signReader;
        localizer.steps = CreateStepCounter(navigator.gameObject);
        navigator.localizer = localizer;
        return localizer;
    }

    // A ring on the floor at the door, a pulse spreading from it, a beam of light, and a floating name tag.
    static DestinationPin CreatePin(Transform planRoot, Transform arCamera)
    {
        var go = new GameObject("Destination Pin");
        go.transform.SetParent(planRoot, false);
        var pin = go.AddComponent<DestinationPin>();
        pin.color = s_T.destination;
        pin.arCamera = arCamera;

        Mesh quad = QuadMesh("Quad", -0.5f);
        Mesh beamQuad = QuadMesh("BeamQuad", 0f);
        Texture2D ring = UITexture("Ring.png");
        Texture2D beam = UITexture("Beam.png");

        // Render queues: the floor marks (3001) before the ribbon (3002), the beam (3003) over both.
        pin.ring = Part("Ring", go.transform, quad,
            ScrollMaterial("PinRing", ring, s_T.destination, 0f, 3001), 0.02f, 1.3f, true).transform;
        MeshRenderer pulse = Part("Pulse", go.transform, quad,
            ScrollMaterial("PinPulse", ring, s_T.destination, 0f, 3001), 0.025f, 1f, true);
        pin.pulse = pulse.transform;
        pin.pulseRenderer = pulse;
        MeshRenderer beamPart = Part("Beam", go.transform, beamQuad,
            ScrollMaterial("PinBeam", beam, Alpha(s_T.destination, 0.5f), 0f, 3003), 0f, 1f, false);
        beamPart.transform.localScale = new Vector3(0.6f, 2.6f, 1f);   // 0.6 m wide, 2.6 m tall, standing up
        pin.beam = beamPart.transform;

        // The name tag: a world-space canvas, sized to the name by DestinationPin.Show. A card like the map's
        // labels: the place's icon in the destination colour, the name in ink. It stays on the Default layer,
        // so the AR camera draws it.
        const int fontSize = 64, iconSize = 72;
        float height = fontSize * 1.9f;          // 121.6
        float iconWidth = iconSize * 1.1f;       // 79.2
        float inset = fontSize * 0.65f;          // 41.6
        RectTransform label = Child("Label", go.transform);
        var canvas = label.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        // Text drawn at twice its size, so it stays crisp up close.
        label.gameObject.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 2f;
        label.sizeDelta = new Vector2(600f, height);
        pin.labelBackground = Rounded(label, s_T.surface, fontSize * 0.95f);

        RectTransform icon = Fixed("Icon", label, new Vector2(0f, 0.5f), new Vector2(inset + iconWidth / 2f, 0f),
            new Vector2(iconWidth, iconWidth));
        pin.labelIcon = TextIn(icon, PlaceStyle.IconRoom, s_Icons, iconSize, s_T.destination, TextAnchor.MiddleCenter);

        Text name = TextIn(Stretch("Name", label, inset + iconWidth, inset), "", s_SemiBold, fontSize, s_T.onSurface,
            TextAnchor.MiddleLeft);
        name.horizontalOverflow = HorizontalWrapMode.Overflow;
        pin.labelText = name;

        pin.label = label;
        pin.labelBackgroundRadius = SpriteRadius;
        return pin;
    }

    // A child with a mesh: lying on the floor (flat) or standing up, y metres up, scaled by size.
    static MeshRenderer Part(string name, Transform parent, Mesh mesh, Material material, float y, float size, bool flat)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, y, 0f);
        go.transform.localRotation = flat ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
        go.transform.localScale = Vector3.one * size;
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        NoShadows(renderer);
        return renderer;
    }

    // Nothing in AR casts, takes or reflects light: every material is unlit.
    static void NoShadows(Renderer renderer)
    {
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }
}
