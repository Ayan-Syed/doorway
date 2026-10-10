using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Debug = UnityEngine.Debug;

// Where the user is indoors, all the time (not only on a route). Outdoors it's GPS, and GPS only suggests which
// entrance the user came in by. At the entrance the user snaps (back to the door, phone up, Confirm), and from then
// on the camera stays on and Monte Carlo localization (LocalizerEngine, PositionFilter) follows them:
// - ARCore's motion, constrained by the walls (a step through a wall kills a guess);
// - depth scans matched against the plan's walls, the main correction (ScanMatcher; feature points without depth);
// - the walls ARCore finds, matched against the plan's walls (WallMatcher);
// - room-number plates read by the camera (SignReader), which also find a lost user again;
// - steps and the compass while the camera can't track (StepCounter).
// Every input goes to the engine and into the walk log (PoseLog), so ReplayEvaluator can run the walk again.
// Without the floor's walkable map it does nothing and stays Outdoors: walks start at a door, as before.
// The parts: Localizer.Sensors.cs (depth, points, walls, plates, steps), Localizer.Outdoors.cs (GPS: the entrance
// prompt, leaving), Localizer.Engine.cs, .Field.cs and .Scan.cs (the plain C# underneath).
public partial class Localizer : MonoBehaviour
{
    public Navigator navigator;
    [Tooltip("The plan's wall outlines (Data/*-walls.bytes), for the distance field and wall matching.")]
    public TextAsset walls;
    [Tooltip("ARCore's feature points: the depth scan's stand-in on phones without the Depth API.")]
    public ARPointCloudManager pointClouds;
    [Tooltip("Steps and compass heading, for when the camera can't track.")]
    public StepCounter steps;
    [Tooltip("Reads room-number plates; null: the Navigator's.")]
    public SignReader signReader;

    /// <summary>A route may start from the Localizer's position while it's this sure (metres, the 68% radius).</summary>
    public const float RouteSpread = 3f;
    // How often the scan, the walls and the outputs update (5 Hz), and the compass is sampled.
    const float MeasureSeconds = 0.2f, CompassSeconds = 0.5f;
    // The per-update time budget on the phone: over it a few times in a row, and the particle cap comes down a step.
    const float BudgetMilliseconds = 3f, BudgetCut = 0.8f;
    const int OverBudgetUpdates = 3;
    // Not confident, and the camera pointing at the floor or the ceiling: the phone needs holding up.
    const float LoweredY = 0.75f;
    // A Begin within this long of a snap at the same door is the same start (the Line up screen snaps, then begins).
    const float SameSnapSeconds = 3f;
    const string DigitHeightKey = "plateDigitHeight";

    public LocalizerState State { get; private set; } = LocalizerState.Outdoors;
    public bool Indoors => State != LocalizerState.Outdoors;
    /// <summary>Plan metres.</summary>
    public Vector2 Position { get; private set; }
    /// <summary>Plan degrees, clockwise from plan up: Navigator.UserHeading's convention.</summary>
    public float Heading { get; private set; }
    /// <summary>Metres: the radius round Position holding about 68% of the particles' weight (the map's halo).</summary>
    public float Spread { get; private set; }
    public float Confidence { get; private set; }
    /// <summary>Not confident, and the camera can't see (lowered, or ARCore not tracking): "Hold your phone up".</summary>
    public bool NeedsView { get; private set; }
    /// <summary>Lost: "Point your camera at a room number".</summary>
    public bool AskForSign => State == LocalizerState.Lost;
    /// <summary>Outdoors: the entrance GPS puts the user nearest, or null.</summary>
    public PlanDoor EntranceGuess { get; private set; }
    /// <summary>GPS says the user just walked in near EntranceGuess: offer the snap there.</summary>
    public bool EntrancePrompt { get; private set; }
    public int ParticleCount => m_Engine?.Filter?.Count ?? 0;
    /// <summary>Debug: walls ARCore sees that matched the plan on the last update.</summary>
    public int WallsMatched { get; private set; }
    /// <summary>The plates' digit height, metres: measured from plates read while Localized (and kept).</summary>
    public float PlateDigitHeight { get; private set; } = SignReader.PlateDigitHeight;
    /// <summary>The filter, indoors; null outdoors or without the walkable map.</summary>
    public PositionFilter Filter => Indoors ? m_Engine?.Filter : null;

    /// <summary>A plate counted as a fix: its text.</summary>
    public event Action<string> SignRead;
    public event Action StateChanged;

    LocalizerEngine m_Engine;
    Task<DistanceField> m_FieldTask;
    WallMatcher m_WallMatcher;
    Vector2[] m_WallPoints;
    int[] m_WallRings;
    UserLocation m_Location;
    PoseLog m_Log;
    float m_NextMeasure, m_NextCompass;
    int m_OverBudget;
    PlanDoor m_SnappedTo;
    float m_SnappedAt = -100f;
    readonly Stopwatch m_Watch = new Stopwatch();
    readonly List<float> m_DigitHeights = new List<float>();

    void Awake()
    {
        if (signReader == null && navigator != null)
            signReader = navigator.signReader;
        float saved = PlayerPrefs.GetFloat(DigitHeightKey, 0f);
        if (saved > 0.02f && saved < 0.15f)
            PlateDigitHeight = SignReader.DigitHeight = saved;
    }

    void Start()
    {
        if (navigator == null)
            return;
        navigator.Load();
        m_Location = FindAnyObjectByType<UserLocation>();
        if (navigator.Walkable == null)
            return;   // no walkable map: GPS only, every walk starts at a door
        if (walls != null)
        {
            try
            {
                WallMesh.Parse(walls.bytes, walls.name, out m_WallRings, out m_WallPoints, out _);
                m_WallMatcher = new WallMatcher(m_WallPoints, m_WallRings, navigator.Walkable);
            }
            catch (FormatException e)
            {
                Debug.LogWarning($"[Navigation] {e.Message}: no wall matching.");
            }
        }
        // The distance field is a few hundred thousand cells: built off the main thread (it touches no Unity object).
        Walkable walkable = navigator.Walkable;
        Vector2[] points = m_WallPoints;
        int[] rings = m_WallRings;
        m_FieldTask = Task.Run(() => DistanceField.Build(walkable, points, rings));
        if (signReader != null)
        {
            signReader.SetTexts(navigator.SignTexts);
            signReader.Sighted += AddSighting;
        }
        if (steps != null)
            steps.Stepped += OnStepped;
        if (navigator.planeManager != null)
            navigator.planeManager.trackablesChanged.AddListener(OnPlanesChanged);
        StartSensors();
    }

    void OnDestroy()
    {
        if (signReader != null)
            signReader.Sighted -= AddSighting;
        if (steps != null)
            steps.Stepped -= OnStepped;
        if (navigator != null && navigator.planeManager != null)
            navigator.planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
        m_Log?.End();
    }

    // The engine, once the distance field is ready (waits for it the first time it's needed).
    LocalizerEngine Engine()
    {
        if (m_Engine != null || m_FieldTask == null)
            return m_Engine;
        DistanceField field = null;
        try
        {
            field = m_FieldTask.Result;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Navigation] No distance field ({e.Message}): no depth matching.");
        }
        m_Engine = new LocalizerEngine(navigator.Plan, navigator.Walkable, field, m_WallMatcher, LocalizerEngine.Config.Full);
        m_Engine.StateChanged += OnEngineState;
        m_Engine.SignUsed += text => SignRead?.Invoke(text);
        m_Field = field;
        return m_Engine;
    }

    public void DismissEntrancePrompt()
    {
        EntrancePrompt = false;
        m_PromptDismissed = true;
    }

    // Standing just inside this door, back to it, phone up: indoors from here.
    public void SnapToDoor(PlanDoor door)
    {
        EntrancePrompt = false;
        if (door == null || navigator == null || Engine() == null)
            return;
        double t = Now;
        navigator.ResetFloor();
        navigator.DoorPose(door, out Vector2 spot, out float yaw);
        Vector2 camera = CameraXZ;
        if (m_Log == null)
            OpenLog();
        m_Log?.Row('n', t);
        m_Log?.Add(door.node);
        LogXZ(spot);
        m_Log?.Add(yaw, 2);
        LogXZ(camera);
        m_Log?.EndRow();
        m_Engine.Snap(t, spot, yaw, camera);
        m_SnappedTo = door;
        m_SnappedAt = Time.unscaledTime;
        Publish();
        ApplyIndoors();
    }

    /// <summary>Snapped at this door a moment ago: a Begin there is the same start, not a second one.</summary>
    public bool JustSnappedTo(PlanDoor door) => door == m_SnappedTo && Time.unscaledTime - m_SnappedAt < SameSnapSeconds;

    // The user asked: spread out and wait for the walls or a sign.
    public void Relocalize()
    {
        if (!Indoors || m_Engine == null)
            return;
        m_Log?.Row('l', Now);
        m_Log?.EndRow();
        m_Engine.Relocalize(Now);
        Publish();
    }

    // Back to GPS, AR off.
    public void GoOutdoors()
    {
        bool was = Indoors;
        if (m_Engine != null && was)
        {
            m_Log?.Row('o', Now);
            m_Log?.EndRow();
            m_Engine.Outdoors(Now);   // StateChanged comes through OnEngineState
        }
        State = LocalizerState.Outdoors;
        m_Log?.End();
        m_Log = null;
        PlayerPrefs.Save();
        ApplyIndoors();
    }

    // Ground truth for tuning: the user says they're at this door now.
    public void Checkpoint(PlanDoor door)
    {
        if (door == null || navigator?.Plan == null || m_Log == null)
            return;
        Vector2 at = navigator.Plan.NodePosition(door.node);
        m_Log.Row('c', Now);
        m_Log.Add(door.node);
        LogXZ(at);
        m_Log.EndRow();
    }

    /// <summary>A line in the walk log (Navigator's route events).</summary>
    public void Note(string what)
    {
        if (m_Log == null)
            return;
        m_Log.Row('e', Now);
        m_Log.Add(what);
        m_Log.EndRow();
    }

    public void Particle(int i, out Vector2 position, out float weight)
    {
        PositionFilter filter = Filter;
        if (filter == null || i < 0 || i >= filter.Count)
        {
            position = Vector2.zero;
            weight = 0f;
            return;
        }
        position = filter.ParticlePosition(i);
        weight = filter.ParticleWeight(i);
    }

    void Update()
    {
        if (navigator == null)
            return;
        if (!Indoors)
        {
            UpdateOutdoors();
            return;
        }
        double t = Now;
        bool tracking = navigator.IsTracking;
        Vector2 camera = CameraXZ;
        float yaw = navigator.CameraYaw;
        m_Engine.Pose(t, camera, yaw, tracking);
        if (m_Log != null)
        {
            Vector3 c = navigator.arCamera.position;
            m_Log.Row('a', t);
            m_Log.Add(tracking ? 1 : 0);
            m_Log.Add(c.x, 3);
            m_Log.Add(c.y, 3);
            m_Log.Add(c.z, 3);
            m_Log.Add(yaw, 2);
            m_Log.EndRow();
        }
        if (tracking)
            CollectScan(t);
        if (Time.unscaledTime >= m_NextMeasure)
        {
            m_NextMeasure = Time.unscaledTime + MeasureSeconds;
            Measure(t, tracking);
        }
        if (tracking && Time.unscaledTime >= m_NextCompass)
        {
            m_NextCompass = Time.unscaledTime + CompassSeconds;
            Compass(t);
        }
        Publish();
        UpdateIndoorsDemand(tracking);
        UpdateLeaving();
    }

    // Five times a second: the scan and the walls into the engine, timed against the budget, and the outputs logged.
    void Measure(double t, bool tracking)
    {
        m_Watch.Restart();
        if (tracking)
        {
            MatchScan(t);
            MatchWalls(t);
        }
        m_Watch.Stop();
        float ms = (float)m_Watch.Elapsed.TotalMilliseconds;
        // Over budget a few times in a row: fewer particles from now on (logged, so a replay does the same).
        m_OverBudget = ms > BudgetMilliseconds ? m_OverBudget + 1 : 0;
        if (m_OverBudget >= OverBudgetUpdates && m_Engine.Filter.MaxCount > LocalizerEngine.MinParticles)
        {
            m_OverBudget = 0;
            int max = Mathf.Max(LocalizerEngine.MinParticles, Mathf.RoundToInt(m_Engine.Filter.MaxCount * BudgetCut));
            m_Engine.LimitParticles(max);
            m_Log?.Row('q', t);
            m_Log?.Add(max);
            m_Log?.EndRow();
        }
        if (m_Log != null)
        {
            m_Log.Row('r', t);
            m_Log.Add((int)State);
            LogXZ(Position);
            m_Log.Add(Heading, 1);
            m_Log.Add(Spread, 2);
            m_Log.Add(Confidence, 2);
            m_Log.Add(ParticleCount);
            m_Log.Add(WallsMatched);
            m_Log.Add(ms, 2);
            m_Log.EndRow();
        }
    }

    void Publish()
    {
        if (m_Engine == null)
            return;
        Position = m_Engine.Position;
        Heading = m_Engine.Heading;
        Spread = m_Engine.Spread;
        Confidence = m_Engine.Confidence;
        WallsMatched = m_Engine.WallsMatched;
        Transform camera = navigator.arCamera;
        bool lowered = camera != null && Mathf.Abs(camera.forward.y) > LoweredY;
        NeedsView = Indoors && State != LocalizerState.Localized && (lowered || !navigator.IsTracking);
    }

    void OnEngineState(LocalizerState state, string why)
    {
        State = state;
        if (m_Log != null)
        {
            m_Log.Row('e', Now);
            m_Log.Add($"state {state} {why}");
            m_Log.EndRow();
        }
        StateChanged?.Invoke();
    }

    // What being indoors switches on: AR, the walls ARCore finds, depth, plate reading and steps; GPS rests.
    void ApplyIndoors()
    {
        bool indoors = Indoors;
        navigator.KeepAR = indoors;
        navigator.KeepVerticalPlanes = indoors;
        ApplySensors(indoors);
        if (!indoors)
        {
            if (signReader != null)
                signReader.reading = false;
            if (steps != null)
                steps.running = false;
        }
        if (m_Location != null)
        {
            m_Location.Resting = indoors;
            m_Location.Needed = false;
        }
    }

    // Each frame indoors: plates are read while ARCore tracks; steps count once the location dialog is answered
    // (the step counter's own permission dialog would clash with it).
    void UpdateIndoorsDemand(bool tracking)
    {
        if (signReader != null && signReader.reading != tracking)
            signReader.reading = tracking;
        bool counting = m_Location == null || !m_Location.IsAsking;
        if (steps != null && steps.running != counting)
            steps.running = counting;
    }

    void OpenLog()
    {
        m_Log = PoseLog.Open($"indoors, {navigator.Plan?.building} {navigator.Plan?.floor}");
        FlushGpsHistory();
    }

    void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            m_Log?.Flush();
            PlayerPrefs.Save();
        }
    }

    static double Now => Time.realtimeSinceStartupAsDouble;

    Vector2 CameraXZ
    {
        get
        {
            Vector3 c = navigator.arCamera.position;
            return new Vector2(c.x, c.z);
        }
    }

    void LogXZ(Vector2 v)
    {
        m_Log?.Add(v.x, 3);
        m_Log?.Add(v.y, 3);
    }
}
