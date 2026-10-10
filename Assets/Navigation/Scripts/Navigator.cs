using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

// Walking guidance on one floor, started from a door the user picks (no signs or markers needed).
//
// Begin(): the user stands in the hallway right outside that door with their back to it, phone upright. That fixes
// where the floor plan sits in the AR world: the plan root is moved and turned so the door spot is under the phone
// and the plan's "away from the door" direction is where the camera looks.
//
// While walking, AR tracking moves the user over the plan, and corrections keep the plan lined up:
// - Heading: walking straight down a long stretch of the route turns the plan until the route matches the walking
//   direction. The start heading is only as good as how squarely the user stood.
// - Floor height: the plan root drops onto the floor plane below the phone once ARCore finds one.
// - Walls, when the floor's walkable map is there: a particle filter (PositionFilter) keeps the user's estimate on
//   walkable floor, learning the heading and scale errors from the walls, and room-number signs read by the camera
//   (AddSighting) pin it down. The plan root follows the estimate, so the band on the floor and the map dot agree.
//   Without the walkable map the plan simply follows the AR camera, as it always has.
//
// With a Localizer indoors (it positions the user all the time, not only on a route), the filter is the
// Localizer's: Navigator reads the position from it and only follows routes (progress, off-route, arrival,
// rerouting), and a door start is a snap there.
//
// The other parts are Navigator.Corrections.cs (heading, floor, walls, signs, rerouting) and Navigator.Steps.cs
// (what to do next).
public partial class Navigator : MonoBehaviour
{
    public TextAsset planJson;
    [Tooltip("Where a person can stand on the floor (Data/*-walkable.bytes). None: the walls aren't used and the plan " +
             "follows the AR camera alone.")]
    public TextAsset walkable;
    [Tooltip("Reads room-number plates while guiding with the walls; each one it reads is a position fix. Optional.")]
    public SignReader signReader;
    [Tooltip("Positions the user indoors all the time. With it, routes start from its position and plates go to it.")]
    public Localizer localizer;
    public ARSession session;
    public Transform arCamera;
    public ARPlaneManager planeManager;
    [Tooltip("Parent of everything drawn on the floor plan (route ribbon, destination pin). Local x/z = plan metres.")]
    public Transform planRoot;
    public RouteRibbon ribbon;
    public DestinationPin pin;

    // Route thresholds, all here and chosen together. Metres from the route unless noted. In increasing order:
    //   heading correction stops (1.5) < the off-route warning clears (2) < it shows (2.5) < arrival by distance left
    //   stops (3) < reroute (4).
    // Heading correction learns only from walking while clearly on the route: a step or two aside may be a detour,
    // and turning the plan to match it would make things worse. The warning comes well before the reroute, so the
    // user gets the chance to step back first, and it clears half a metre closer than it shows, so standing on the
    // line doesn't make it flicker. The reroute needs 4 m held for 1.5 s of tracked time, so stepping round people
    // or one jumpy pose doesn't throw the route away; arrival needs 0.5 s of tracked time for the same reason.

    /// <summary>Heading correction runs only while the user is within this of the route.</summary>
    public const float HeadingOffRouteLimit = 1.5f;
    /// <summary>Further than this from the route, the guide screen shows its off-route warning.</summary>
    public const float OffRouteWarning = 2.5f;
    /// <summary>Once shown, the off-route warning goes again only within this of the route.</summary>
    public const float OffRouteClear = 2f;
    /// <summary>Arriving by distance left counts only while within this of the route.</summary>
    public const float ArriveOffRouteLimit = 3f;
    /// <summary>Further than this from the route, held for RerouteSeconds while tracking, finds a new route.</summary>
    public const float RerouteDistance = 4f;
    /// <summary>Seconds of continuous tracked time past RerouteDistance before rerouting.</summary>
    public const float RerouteSeconds = 1.5f;
    /// <summary>Arrival radius: this little of the route left, or this close to the door on the last stretch.</summary>
    public const float ArriveDistance = 2f;
    /// <summary>Seconds of continuous tracked time the arrival condition must hold.</summary>
    public const float ArriveSeconds = 0.5f;
    /// <summary>Seconds without tracking before the guide screen says tracking is lost (ARCore blinks to Limited
    /// for a frame or two on a quick turn).</summary>
    public const float TrackingLostSeconds = 0.5f;
    /// <summary>Seconds without tracking after which the plan can't be trusted: the user should line up again.</summary>
    public const float RealignSeconds = 5f;
    /// <summary>The position filter's restarts (every particle in a wall) within its recent walk after which the
    /// user should line up again: the walk doesn't fit the building from the door they picked.</summary>
    public const int RealignRecoveries = 2;
    /// <summary>Walking pace for every time estimate, metres per second.</summary>
    public const float WalkingSpeed = 1.25f;

    // Phone height above the floor until a floor plane is found.
    const float EyeHeight = 1.4f;
    const float TrailSpacing = 0.3f;
    const int MaxTrail = 30;                    // at most ~9 m of walk history
    // Heading correction: only from straight walking along a long stretch, and only small turns.
    const float StraightWalk = 3f;
    const float StraightRatio = 0.92f;
    const float LongStretch = 4f;
    const float MaxHeadingCorrection = 20f;
    const float HeadingCorrectionSeconds = 1.5f;
    // The most the corrections may turn the plan in total, away from where Begin put it. Standing with your back
    // to a door is rarely more than 10-15 degrees off square, and ARCore's yaw drift over a building-length walk is
    // a few degrees, so a true correction stays well inside 30. More than that means the walk is being matched to
    // the wrong stretch (a parallel hallway, a detour); stop there and leave it to Re-align.
    const float MaxTotalHeadingCorrection = PositionFilter.MaxHeadingOffset;
    const float FloorCheckSeconds = 0.5f;
    // Once found, the floor moves at most this much: anything further is a table, a bench or a stair landing.
    const float FloorLock = 0.3f;
    // Floor checks in a row moving it less than FloorSteadyChange before horizontal plane detection stops.
    const int FloorSteadyChecks = 4;
    const float FloorSteadyChange = 0.02f;
    // The plan root follows the filter's estimate with this time constant: most of the way in a second.
    const float FollowSeconds = 0.33f;
    // Back with tracking for this long, the re-align prompt goes again by itself.
    const float RealignClearSeconds = 15f;
    // How long AR runs without camera permission before it counts as refused (the dialog takes a moment to show).
    const float CameraCheckSeconds = 2f;
    const float CameraPollSeconds = 0.5f;

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int ChevronColorId = Shader.PropertyToID("_ChevronColor");

    public FloorPlan Plan { get; private set; }
    /// <summary>The floor's walkable map, or null: then nothing below uses walls.</summary>
    public Walkable Walkable { get; private set; }
    public Route Route { get; private set; }
    public bool IsGuiding { get; private set; }
    public bool HasArrived { get; private set; }
    /// <summary>The camera's spot on the plan, plan metres: the filtered estimate when there's a walkable map.</summary>
    public Vector2 UserPosition { get; private set; }
    /// <summary>Where the phone faces: degrees clockwise from plan up (+z).</summary>
    public float UserHeading { get; private set; }
    /// <summary>The camera's spot on the plan as Begin's alignment alone puts it (no corrections), for the log.</summary>
    public Vector2 RawPosition { get; private set; }
    /// <summary>Metres along the route.</summary>
    public float Progress { get; private set; }
    /// <summary>Metres from the route.</summary>
    public float OffRoute { get; private set; }
    public float Remaining => Route == null ? 0f : Mathf.Max(0f, Route.length - Progress);
    public float RemainingMinutes => Remaining / WalkingSpeed / 60f;
    /// <summary>Far enough off the route for the guide screen's warning: on past OffRouteWarning, off within OffRouteClear.</summary>
    public bool IsOffRoute { get; private set; }
    public bool IsTracking => ARSession.state == ARSessionState.SessionTracking;
    /// <summary>Tracking has been gone for TrackingLostSeconds while guiding.</summary>
    public bool TrackingLost => IsGuiding && m_LostTime >= TrackingLostSeconds;
    /// <summary>The plan may no longer match the hallway: tracking was lost for a while, the app was in the
    /// background, or the walls lost track of the user. Re-aligning at a door fixes it.</summary>
    public bool ShouldRealign { get; private set; }
    /// <summary>The position filter, while guiding with a walkable map; else null.</summary>
    public PositionFilter Filter => m_Filter;
    /// <summary>Every sign text in the plan, for the sign reader to look for.</summary>
    public IEnumerable<string> SignTexts => m_SignTexts;

    /// <summary>AR stays on whatever SetARActive says (the Localizer, indoors): the camera is how it knows where the
    /// user is.</summary>
    public bool KeepAR
    {
        get => m_KeepAR;
        set
        {
            if (m_KeepAR == value)
                return;
            m_KeepAR = value;
            ApplyAR();
        }
    }

    /// <summary>The floor's height in the AR world (a guess until a floor plane is found).</summary>
    public float FloorY => m_FloorY;

    /// <summary>AR waits while this is set (Android shows one permission dialog at a time, and ARCore asks for the
    /// camera as it starts).</summary>
    public bool HoldAR
    {
        get => m_HoldAR;
        set
        {
            if (m_HoldAR == value)
                return;
            m_HoldAR = value;
            ApplyAR();
        }
    }

    /// <summary>Keep ARCore finding walls (vertical planes) while AR runs: the sign reader measures distances to
    /// them. Horizontal planes stop by themselves once the floor is settled.</summary>
    public bool KeepVerticalPlanes
    {
        get => m_KeepVerticalPlanes;
        set
        {
            m_KeepVerticalPlanes = value;
            UpdatePlaneDetection();
        }
    }

    /// <summary>AR is on, but the camera permission was refused: AR can't start until it's given in Settings.</summary>
    public bool CameraDenied => m_CameraDenied && m_ARActive == 1;
    /// <summary>AR is on, but the user declined to install Google Play Services for AR (the session gave up).</summary>
    public bool InstallDeclined =>
        m_ARActive == 1 && session != null && !session.enabled && ARSession.state == ARSessionState.NeedsInstall;

    public event Action Rerouted;
    public event Action Arrived;

    Router m_Router;
    Vector3 m_Anchor;
    float m_FloorY;
    bool m_FloorFound, m_FloorSettled;
    int m_FloorSteady;
    float m_NextFloorCheck;
    float m_OffRouteTime;
    float m_ArriveTime;
    float m_LostTime;
    float m_TrackedTime;
    float m_HeadingCorrection;
    // Begin's alignment, before any correction: the plan root's yaw and position then.
    float m_BaseYaw;
    Vector3 m_BasePosition;
    readonly List<Vector3> m_Trail = new List<Vector3>();
    readonly List<string> m_SignTexts = new List<string>();
    PositionFilter m_Filter;
    PoseLog m_Log;
    ARCameraManager m_CameraManager;
    int m_ARActive = -1;
    bool m_WantAR, m_HoldAR, m_KeepVerticalPlanes, m_KeepAR;
    // The filter is the Localizer's (it moves it each frame; Navigator only reads it and turns the plan root).
    bool m_SharedFilter;
    bool m_CameraDenied;
    float m_ARSince, m_NextCameraPoll;
    bool m_PausedWhileGuiding;

    void Awake()
    {
        Load();
        planRoot.gameObject.SetActive(false);
        if (arCamera != null)
            arCamera.TryGetComponent(out m_CameraManager);
    }

    // Checked once at launch, not just when AR is first wanted: with no ARCore at all (no XR loader, or a phone
    // Google doesn't support) the session never starts, and only this says so.
    void Start()
    {
        StartCoroutine(ARSession.CheckAvailability());
    }

    // NavigationScreen calls this too, so the plan is ready whichever Awake runs first.
    public void Load()
    {
        if (Plan != null)
            return;
        Plan = FloorPlan.Load(planJson);
        if (walkable != null)
        {
            try
            {
                Walkable = Walkable.Load(walkable.bytes);
            }
            catch (FormatException e)
            {
                Debug.LogWarning($"[Navigation] {e.Message}: guiding without walls.");
            }
        }
        // With the walls, a reroute joins the walkway graph only along a line that crosses none.
        m_Router = new Router(Plan, Walkable);
        if (Plan.signs != null)
        {
            foreach (PlanSign sign in Plan.signs)
            {
                if (!string.IsNullOrEmpty(sign?.text) && !m_SignTexts.Contains(sign.text))
                    m_SignTexts.Add(sign.text);
            }
        }
        // With a Localizer, plates are its to read.
        if (signReader != null && localizer == null)
        {
            signReader.SetTexts(m_SignTexts);
            signReader.Sighted -= AddSighting;
            signReader.Sighted += AddSighting;
        }
    }

    public Route PlanRoute(PlanDoor from, PlanPlace to)
    {
        Load();
        return m_Router.FindRoute(from.node, to);
    }

    // AR runs only while it's needed (lining up and walking): the camera feed and tracking cost frame time and
    // battery behind the map. Paused, the session keeps what it knows and picks up again when resumed.
    public void SetARActive(bool on)
    {
        m_WantAR = on;
        ApplyAR();
    }

    void ApplyAR()
    {
        bool on = (m_WantAR || m_KeepAR) && !m_HoldAR;
        int state = on ? 1 : 0;
        if (m_ARActive == state)
            return;
        m_ARActive = state;
        m_ARSince = Time.unscaledTime;
        m_CameraDenied = false;   // asked afresh as the session starts
        if (session != null)
            session.enabled = on;
        if (planeManager != null)
            planeManager.enabled = on;
        if (arCamera != null && arCamera.TryGetComponent(out Camera camera))
            camera.enabled = on;
        UpdatePlaneDetection();
    }

    // After the user declined to install AR services: ask again (the session's start offers the install).
    public void InstallAR()
    {
        if (session != null && m_ARActive == 1)
            session.enabled = true;
    }

    // Starts the session over, so ARCore asks for the camera (or opens it) again.
    void RestartSession()
    {
        if (session == null || m_ARActive != 1)
            return;
        session.enabled = false;
        session.enabled = true;
        m_ARSince = Time.unscaledTime;
    }

    // Twice a second while AR runs: has the camera been refused, or given since in Settings?
    void WatchCamera()
    {
        if (m_ARActive != 1 || m_CameraManager == null || Application.platform != RuntimePlatform.Android ||
            Time.unscaledTime < m_NextCameraPoll)
            return;
        m_NextCameraPoll = Time.unscaledTime + CameraPollSeconds;
        if (m_CameraManager.permissionGranted)
        {
            if (m_CameraDenied)
            {
                m_CameraDenied = false;
                RestartSession();
            }
            return;
        }
        // Until ARCore has had the chance to ask, no camera is just not yet; and while the dialog is up (it takes
        // the focus) the user is still deciding.
        if (!Application.isFocused)
        {
            m_ARSince = Time.unscaledTime;
            return;
        }
        ARSessionState state = ARSession.state;
        bool asked = state == ARSessionState.Ready || state == ARSessionState.SessionInitializing;
        if (asked && Time.unscaledTime - m_ARSince > CameraCheckSeconds)
            m_CameraDenied = true;
    }

    public void ApplyTheme(Theme theme)
    {
        if (ribbon != null && ribbon.TryGetComponent(out Renderer renderer))
        {
            Material material = Application.isPlaying ? renderer.material : renderer.sharedMaterial;
            if (material != null)
            {
                material.SetColor(ColorId, theme.primary);
                material.SetColor(ChevronColorId, theme.onPrimary);
            }
        }
        if (pin != null)
            pin.ApplyTheme(theme);
    }

    // The user is outside the door, back to it. Lines the plan up with them and starts guiding. With a Localizer
    // that's a snap at the door (unless it has just snapped there: the Line up screen snaps first), and the route
    // follows the Localizer's filter from then on.
    public void Begin(PlanDoor from, PlanPlace to)
    {
        Route = PlanRoute(from, to);
        if (Route == null)
            return;
        if (UsesLocalizer)
        {
            if (!localizer.JustSnappedTo(from))
                localizer.SnapToDoor(from);
            StartGuiding(Plan.NodePosition(from.node), from.node, to);
            return;
        }
        AlignTo(from);
        StartGuiding(Plan.NodePosition(from.node), from.node, to);
    }

    bool UsesLocalizer => localizer != null && localizer.Filter != null && localizer.Indoors;

    // Everything a route starts with, from a plan spot (the door, or where the Localizer puts the user).
    void StartGuiding(Vector2 start, string from, PlanPlace to)
    {
        Progress = 0f;
        OffRoute = 0f;
        IsOffRoute = false;
        m_OffRouteTime = 0f;
        m_ArriveTime = 0f;
        m_LostTime = 0f;
        m_TrackedTime = 0f;
        ShouldRealign = false;
        m_PausedWhileGuiding = false;
        IsGuiding = true;
        HasArrived = false;
        UserPosition = start;
        RawPosition = UserPosition;
        m_Log?.End();
        m_Log = null;
        if (UsesLocalizer)
        {
            // The Localizer's filter, its log; the plan root goes straight to where it puts the user.
            m_SharedFilter = true;
            m_Filter = localizer.Filter;
            UserPosition = localizer.Position;
            PlaceFromFilter();
            localizer.Note($"route {from} {to?.id}");
        }
        else
        {
            m_SharedFilter = false;
            StartFilter(UserPosition);
            SetReadingSigns(Walkable != null);   // a sighting only counts against the walls
            m_Log = PoseLog.Begin(from, to?.id, m_BaseYaw, arCamera.position, UserPosition,
                Plan.NodePosition(Route.door.node));
        }
        ribbon.Show(Route, 0f);
        pin.Show(Route.place, Plan.NodePosition(Route.door.node), Route.door.Direction);
    }

    // The plan root put where the filter says, at once (a route from the Localizer's position starts here).
    void PlaceFromFilter()
    {
        Vector2 at = m_Filter.Estimate, camera = m_Filter.Camera;
        Quaternion rotation = Quaternion.Euler(0f, m_Filter.Yaw, 0f);
        var spot = new Vector3(at.x, 0f, at.y);
        planRoot.SetPositionAndRotation(new Vector3(camera.x, m_FloorY, camera.y) - rotation * spot, rotation);
        planRoot.gameObject.SetActive(true);
        m_BaseYaw = m_Filter.Yaw;
        m_BasePosition = planRoot.position;
        m_Anchor = new Vector3(camera.x, 0f, camera.y);
        m_HeadingCorrection = 0f;
        m_Trail.Clear();
    }

    // The sign reader needs walls ARCore has found, to measure how far a plate is.
    void SetReadingSigns(bool on)
    {
        if (signReader == null)
            return;
        signReader.reading = on;
        KeepVerticalPlanes = on;
    }

    // A route from where the Localizer puts the user now, facing the way they face (the first step may be "Turn
    // around"). False if there's no Localizer indoors, it isn't sure enough, or there's no way from there.
    public bool BeginFrom(PlanPlace to)
    {
        if (!UsesLocalizer || localizer.State == LocalizerState.Lost || localizer.Spread > Localizer.RouteSpread)
            return false;
        Load();
        Route = m_Router.FindRoute(localizer.Position, to);
        if (Route == null)
            return false;
        StartGuiding(localizer.Position, "here", to);
        return true;
    }

    // The pin goes with the plan root; it has no Hide of its own.
    public void Stop()
    {
        IsGuiding = false;
        HasArrived = false;
        IsOffRoute = false;
        ShouldRealign = false;
        m_LostTime = 0f;
        Route = null;
        m_Filter = null;
        if (!m_SharedFilter)
            SetReadingSigns(false);
        m_SharedFilter = false;
        m_Log?.End();
        m_Log = null;
        if (ribbon != null)
            ribbon.Hide();
        planRoot.gameObject.SetActive(false);
        UpdatePlaneDetection();
    }

    // Standing at this door with your back to it: the door's spot, and the plan's yaw in the AR world that turns the
    // plan's "away from the door" onto where the phone faces.
    public void DoorPose(PlanDoor door, out Vector2 spot, out float yaw)
    {
        Load();
        spot = Plan.NodePosition(door.node);
        Vector2 away = -door.Direction.normalized;                     // door.Direction points spot -> door
        Vector3 facing = FlatForward();
        // Both are compass headings (clockwise from +z), so the difference is the Unity yaw that turns plan "away"
        // onto world "facing".
        yaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg - Mathf.Atan2(away.x, away.y) * Mathf.Rad2Deg;
    }

    /// <summary>The camera's flat yaw in the AR world: degrees clockwise from world +z.</summary>
    public float CameraYaw
    {
        get
        {
            Vector3 facing = FlatForward();
            return Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
        }
    }

    // A new floor guess under the camera, and a floor plane straight onto it if one's there (a snap, a door start).
    public void ResetFloor()
    {
        m_FloorY = arCamera.position.y - EyeHeight;                     // a guess until a floor plane turns up
        m_FloorFound = false;
        m_FloorSettled = false;
        m_FloorSteady = 0;
        FindFloor(true);
        UpdatePlaneDetection();
    }

    // Puts the door spot under the camera at floor height and turns the plan's "away from the door" direction
    // onto where the phone faces.
    void AlignTo(PlanDoor door)
    {
        DoorPose(door, out Vector2 spot, out float yaw);
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
        Vector3 camera = arCamera.position;
        ResetFloor();
        planRoot.SetPositionAndRotation(
            new Vector3(camera.x, m_FloorY, camera.z) - rotation * new Vector3(spot.x, 0f, spot.y),
            rotation);
        planRoot.gameObject.SetActive(true);

        m_BaseYaw = yaw;
        m_BasePosition = planRoot.position;
        m_Anchor = new Vector3(camera.x, 0f, camera.z);                 // the pivot for heading corrections
        m_HeadingCorrection = 0f;
        m_Trail.Clear();
    }

    // Where the phone points, flattened onto the floor. Held flat (camera down), the top edge points ahead.
    Vector3 FlatForward()
    {
        Vector3 forward = arCamera.forward;
        forward.y = 0f;
        if (forward.magnitude < 0.3f)
        {
            forward = arCamera.up;
            forward.y = 0f;
        }
        return forward.normalized;
    }

    void Update()
    {
        WatchCamera();
        // Indoors the floor is wanted all the time (the depth scan keeps only points between floor and ceiling).
        if (m_KeepAR && !IsGuiding)
            FindFloor(false);
        if (!IsGuiding || Route == null)
            return;
        bool tracking = IsTracking;
        WatchTracking(tracking);
        FindFloor(false);
        if (m_SharedFilter && (localizer == null || !localizer.Indoors))
            Stop();   // gone outdoors mid-route: nothing to guide by
        if (!IsGuiding)
            return;
        if (m_SharedFilter)
            FollowLocalizer();
        else if (m_Filter != null)
            FollowFilter(tracking);
        else
            FollowCamera();
        RawPosition = BasePoint(arCamera.position);
        Progress = Route.Project(UserPosition, Progress, out float offRoute);
        OffRoute = offRoute;
        IsOffRoute = OffRoute > (IsOffRoute ? OffRouteClear : OffRouteWarning);

        // The Localizer's filter changes only by its own logged inputs (so a walk replays exactly): no route-based
        // heading correction on it; its scan and walls correct the heading anyway.
        if (tracking && !m_SharedFilter)
            CorrectHeading();
        else
            m_Trail.Clear();   // the pose can jump when tracking comes back; a trail across the jump isn't a walk
        if (!m_SharedFilter)
            m_Log?.Frame(tracking, (int)ARSession.state, arCamera.position, arCamera.rotation, RawPosition,
            PlanHeading(FlatForward(), m_BaseYaw), UserPosition, UserHeading, m_Filter?.Spread ?? -1f,
            m_Filter?.Confidence ?? -1f, Progress, OffRoute, m_FloorY);

        if (HasArrived)
            return;
        CheckOffRoute();
        // Arrival must hold for a moment of tracked time: a pose that jumps on a lost or jittery frame isn't a walk.
        // Nor does it count while the localizer is unsure where the user is (the banner and ribbon wait for it too).
        m_ArriveTime = tracking && SureOfPosition() && ArrivedNow() ? m_ArriveTime + Time.deltaTime : 0f;
        if (m_ArriveTime >= ArriveSeconds)
        {
            HasArrived = true;
            ribbon.Hide();
            m_Log?.Note("arrived");
            if (m_SharedFilter)
                localizer.Note("arrived");
            Arrived?.Invoke();
        }
        else
        {
            ribbon.Show(Route, Progress);   // cheap: rebuilds only when progress moves
        }
    }

    // Without a walkable map: the plan root is where Begin and the heading corrections put it, and the user is
    // wherever the AR camera is over it.
    void FollowCamera()
    {
        Vector3 local = planRoot.InverseTransformPoint(arCamera.position);
        UserPosition = new Vector2(local.x, local.z);
        Vector3 facing = planRoot.InverseTransformDirection(FlatForward());
        UserHeading = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
    }

    // How long tracking has been gone (or back), and whether the user should line up again.
    void WatchTracking(bool tracking)
    {
        float dt = Time.unscaledDeltaTime;
        if (tracking)
        {
            m_LostTime = 0f;
            m_TrackedTime += dt;
            if (ShouldRealign && m_TrackedTime >= RealignClearSeconds)
                ShouldRealign = false;
        }
        else
        {
            m_LostTime += dt;
            m_TrackedTime = 0f;
            if (m_LostTime >= RealignSeconds)
                RequestRealign("lost");
        }
        if (m_Filter != null && m_Filter.RecentRecoveries >= RealignRecoveries)
            RequestRealign("walls");
    }

    void RequestRealign(string why)
    {
        if (ShouldRealign)
            return;
        ShouldRealign = true;
        m_TrackedTime = 0f;
        m_Log?.Note("realign " + why);
    }

    // Back from the background mid-walk: nothing says where the phone went meanwhile, or whether ARCore will find
    // its map again, so the user is asked to line up again; but not after a moment away (a permission dialog pauses
    // the app too).
    void OnApplicationPause(bool paused)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (paused)
        {
            m_PausedWhileGuiding = IsGuiding;
            m_PausedAt = now;
            m_Log?.Flush();
        }
        else if (m_PausedWhileGuiding && IsGuiding)
        {
            m_PausedWhileGuiding = false;
            if (LongPause(now - m_PausedAt))
                RequestRealign("resumed");
        }
    }

    double m_PausedAt = double.NaN;

    /// <summary>Paused long enough that the phone may have gone somewhere: ShortPauseSeconds or more (NaN: never
    /// paused, so no).</summary>
    public static bool LongPause(double seconds) => seconds >= ShortPauseSeconds;

    public const float ShortPauseSeconds = 3f;

    // Back from Settings or a permission dialog: give ARCore a moment before calling the camera refused.
    void OnApplicationFocus(bool focused)
    {
        if (focused)
            m_ARSince = Time.unscaledTime;
    }

    void OnDestroy()
    {
        m_Log?.End();
    }

    // Close to the end of the route while on it, or close to the door itself on the route's last stretch. Only the
    // last stretch: a route can pass the destination's door earlier on (round a loop), and that isn't arriving.
    // Sure enough of the position for arrival to count: no localizer indoors (ARCore alone, or outdoors), Localized,
    // or Converging with the cloud within ArriveSpread.
    bool SureOfPosition() => localizer == null || !localizer.Indoors ||
                             SureOfPosition(localizer.State, localizer.Spread);

    public const float ArriveSpread = 2f;

    /// <summary>Whether arrival may count in this localizer state with this spread (metres).</summary>
    public static bool SureOfPosition(LocalizerState state, float spread) =>
        state == LocalizerState.Localized || state == LocalizerState.Converging && spread <= ArriveSpread;

    bool ArrivedNow()
    {
        if (Remaining < ArriveDistance && OffRoute < ArriveOffRouteLimit)
            return true;
        bool lastStretch = Progress >= Route.DistanceAtPoint(Route.points.Count - 2);
        return lastStretch && Vector2.Distance(UserPosition, Plan.NodePosition(Route.door.node)) < ArriveDistance;
    }

    // A world point on the plan as Begin's alignment alone puts it.
    Vector2 BasePoint(Vector3 world)
    {
        var offset = new Vector2(world.x - m_BasePosition.x, world.z - m_BasePosition.z);
        return PositionFilter.ToPlan(offset, m_BaseYaw);
    }

    // A world heading (degrees clockwise from +z) as a plan heading, for a plan turned by yaw.
    static float PlanHeading(Vector3 worldDirection, float yaw)
    {
        return Mathf.DeltaAngle(0f, Mathf.Atan2(worldDirection.x, worldDirection.z) * Mathf.Rad2Deg - yaw);
    }
}
