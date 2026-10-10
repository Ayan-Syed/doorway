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
// While walking, AR tracking moves the user over the plan, and two corrections keep the plan lined up:
// - Heading: walking straight down a long stretch of the route turns the plan, about the start spot, until the
//   route matches the walking direction. The start heading is only as good as how squarely the user stood.
// - Floor height: the plan root drops onto the floor plane below the phone once ARCore finds one.
//
// The other parts are Navigator.Corrections.cs (heading, floor, rerouting) and Navigator.Steps.cs (what to do next).
public partial class Navigator : MonoBehaviour
{
    public TextAsset planJson;
    public ARSession session;
    public Transform arCamera;
    public ARPlaneManager planeManager;
    [Tooltip("Parent of everything drawn on the floor plan (route ribbon, destination pin). Local x/z = plan metres.")]
    public Transform planRoot;
    public RouteRibbon ribbon;
    public DestinationPin pin;

    // Route thresholds, all here and chosen together. Metres from the route unless noted. In increasing order:
    //   heading correction stops (1.5) < the guide screen warns (2.5) < arrival by distance left stops (3) < reroute (4).
    // Heading correction learns only from walking while clearly on the route: a step or two aside may be a detour,
    // and turning the plan to match it would make things worse. The warning comes well before the reroute, so the
    // user gets the chance to step back first. The reroute needs 4 m held for 1.5 s of tracked time, so stepping
    // round people or one jumpy pose doesn't throw the route away.

    /// <summary>Heading correction runs only while the user is within this of the route.</summary>
    public const float HeadingOffRouteLimit = 1.5f;
    /// <summary>Further than this from the route, the guide screen shows its off-route warning.</summary>
    public const float OffRouteWarning = 2.5f;
    /// <summary>Arriving by distance left counts only while within this of the route.</summary>
    public const float ArriveOffRouteLimit = 3f;
    /// <summary>Further than this from the route, held for RerouteSeconds while tracking, finds a new route.</summary>
    public const float RerouteDistance = 4f;
    /// <summary>Seconds of continuous tracked time past RerouteDistance before rerouting.</summary>
    public const float RerouteSeconds = 1.5f;
    /// <summary>Arrival radius: this little of the route left, or this close to the door on the last stretch.</summary>
    public const float ArriveDistance = 2f;
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
    const float MaxTotalHeadingCorrection = 30f;
    const float FloorCheckSeconds = 0.5f;

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int ChevronColorId = Shader.PropertyToID("_ChevronColor");

    public FloorPlan Plan { get; private set; }
    public Route Route { get; private set; }
    public bool IsGuiding { get; private set; }
    public bool HasArrived { get; private set; }
    /// <summary>The camera's spot on the plan, plan metres.</summary>
    public Vector2 UserPosition { get; private set; }
    /// <summary>Where the phone faces: degrees clockwise from plan up (+z).</summary>
    public float UserHeading { get; private set; }
    /// <summary>Metres along the route.</summary>
    public float Progress { get; private set; }
    /// <summary>Metres from the route.</summary>
    public float OffRoute { get; private set; }
    public float Remaining => Route == null ? 0f : Mathf.Max(0f, Route.length - Progress);
    public float RemainingMinutes => Remaining / WalkingSpeed / 60f;
    /// <summary>Far enough off the route for the guide screen's warning.</summary>
    public bool IsOffRoute => OffRoute > OffRouteWarning;
    public bool IsTracking => ARSession.state == ARSessionState.SessionTracking;

    public event Action Rerouted;
    public event Action Arrived;

    Router m_Router;
    Vector3 m_Anchor;
    float m_FloorY;
    float m_NextFloorCheck;
    float m_OffRouteTime;
    float m_HeadingCorrection;
    readonly List<Vector3> m_Trail = new List<Vector3>();
    int m_ARActive = -1;

    void Awake()
    {
        Load();
        planRoot.gameObject.SetActive(false);
    }

    // NavigationScreen calls this too, so the plan is ready whichever Awake runs first.
    public void Load()
    {
        if (Plan != null)
            return;
        Plan = FloorPlan.Load(planJson);
        m_Router = new Router(Plan);
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
        int state = on ? 1 : 0;
        if (m_ARActive == state)
            return;
        m_ARActive = state;
        if (session != null)
            session.enabled = on;
        if (planeManager != null)
            planeManager.enabled = on;
        if (arCamera != null && arCamera.TryGetComponent(out Camera camera))
            camera.enabled = on;
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

    // The user is outside the door, back to it. Lines the plan up with them and starts guiding.
    public void Begin(PlanDoor from, PlanPlace to)
    {
        Route = PlanRoute(from, to);
        if (Route == null)
            return;
        AlignTo(from);
        Progress = 0f;
        OffRoute = 0f;
        m_OffRouteTime = 0f;
        IsGuiding = true;
        HasArrived = false;
        UserPosition = Plan.NodePosition(from.node);
        ribbon.Show(Route, 0f);
        pin.Show(Route.place, Plan.NodePosition(Route.door.node), Route.door.Direction);
    }

    // The pin goes with the plan root; it has no Hide of its own.
    public void Stop()
    {
        IsGuiding = false;
        HasArrived = false;
        Route = null;
        if (ribbon != null)
            ribbon.Hide();
        planRoot.gameObject.SetActive(false);
    }

    // Puts the door spot under the camera at floor height and turns the plan's "away from the door" direction
    // onto where the phone faces.
    void AlignTo(PlanDoor door)
    {
        Vector2 spot = Plan.NodePosition(door.node);
        Vector2 away = -door.Direction.normalized;                     // door.Direction points spot -> door
        Vector3 facing = FlatForward();
        // Both are compass headings (clockwise from +z), so the difference is the Unity yaw that turns plan "away"
        // onto world "facing".
        float yaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg - Mathf.Atan2(away.x, away.y) * Mathf.Rad2Deg;
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);

        Vector3 camera = arCamera.position;
        m_FloorY = camera.y - EyeHeight;                                // a guess until a floor plane turns up
        FindFloor(true);
        planRoot.SetPositionAndRotation(
            new Vector3(camera.x, m_FloorY, camera.z) - rotation * new Vector3(spot.x, 0f, spot.y),
            rotation);
        planRoot.gameObject.SetActive(true);

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
        if (!IsGuiding || Route == null)
            return;
        FindFloor(false);
        Vector3 local = planRoot.InverseTransformPoint(arCamera.position);
        UserPosition = new Vector2(local.x, local.z);
        Vector3 facing = planRoot.InverseTransformDirection(FlatForward());
        UserHeading = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
        Progress = Route.Project(UserPosition, Progress, out float offRoute);
        OffRoute = offRoute;

        if (IsTracking)
            CorrectHeading();
        else
            m_Trail.Clear();   // the pose can jump when tracking comes back; a trail across the jump isn't a walk

        if (HasArrived)
            return;
        CheckOffRoute();
        if (ArrivedNow())
        {
            HasArrived = true;
            ribbon.Hide();
            Arrived?.Invoke();
        }
        else
        {
            ribbon.Show(Route, Progress);   // cheap: rebuilds only when progress moves
        }
    }

    // Close to the end of the route while on it, or close to the door itself on the route's last stretch. Only the
    // last stretch: a route can pass the destination's door earlier on (round a loop), and that isn't arriving.
    bool ArrivedNow()
    {
        if (Remaining < ArriveDistance && OffRoute < ArriveOffRouteLimit)
            return true;
        bool lastStretch = Progress >= Route.DistanceAtPoint(Route.points.Count - 2);
        return lastStretch && Vector2.Distance(UserPosition, Plan.NodePosition(Route.door.node)) < ArriveDistance;
    }
}
