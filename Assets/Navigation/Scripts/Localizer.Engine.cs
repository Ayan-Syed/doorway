using System;
using System.Collections.Generic;
using UnityEngine;

// Outdoors: GPS only. Converging: indoors, the cloud not yet tight. Localized: tight and confident. Lost: spread too
// wide, or the walls disagree: a room number puts it right.
public enum LocalizerState { Outdoors, Converging, Localized, Lost }

// The Localizer's working part, with no Unity objects in it: every input arrives through a method with its time
// (the AR pose, a depth scan, the walls ARCore sees, a plate read, a step, a snap), and the same inputs in the same
// order give the same answer. The Localizer feeds it on the phone and logs each input as it goes (PoseLog); the
// evaluator (ReplayEvaluator) feeds it the logged inputs again, with signals switched off one at a time to see what
// each is worth.
public sealed class LocalizerEngine
{
    // Which signals are used. Raw is the baseline: ARCore's pose laid on the plan from the snap, nothing else.
    public struct Config
    {
        public string name;
        public bool depth, wallPlanes, plates, steps, raw;

        public static Config Full => new Config { name = "full", depth = true, wallPlanes = true, plates = true, steps = true };
        public static Config Raw => new Config { name = "raw", raw = true };

        public static IEnumerable<Config> Evaluated()
        {
            yield return Full;
            Config c = Full; c.name = "no-depth"; c.depth = false; yield return c;
            c = Full; c.name = "no-wallplanes"; c.wallPlanes = false; yield return c;
            c = Full; c.name = "no-plates"; c.plates = false; yield return c;
            c = Full; c.name = "no-steps"; c.steps = false; yield return c;
            yield return Raw;
        }
    }

    public enum Sighted { Unknown, Ignored, Used, Placed }

    // Particles: as many as a snap needs at first; KLD-sampling then keeps between Min and Max.
    public const int SnapParticles = 500, MinParticles = 200, MaxParticles = 2000;
    // The snap: how sure standing with your back to the door is (headings evenly over ±SnapHeadingSpread: a door's
    // direction in the data can be 25 degrees off square), and how far the heading may later turn from it.
    const float SnapSpread = 0.5f, SnapHeadingSpread = 30f, SnapHeadingLimit = 45f;
    // The first scans after a snap or a fresh start weigh from FirstScan of their usual strength up to all of it over
    // RampScans updates.
    const float FirstScan = 0.3f;
    const int RampScans = 10;
    // States, with hysteresis: Localized needs the cloud tight and confident for SettleSeconds; it's left again only
    // once clearly loose for as long. Lost is a cloud wider than LostSpread, or two wipe-outs within 30 m walked, or
    // a scan that fits nowhere for MisfitSeconds. Only a sign gets out of Lost: in a building of look-alike
    // hallways, walls and scans can settle on the wrong one with every sign of confidence (seen in the synthetic
    // relocalizations), so the user is asked for a room number instead.
    public const float LocalizedSpread = 1.5f, LocalizedConfidence = 0.6f;
    public const float LooseSpread = 2.5f, LooseConfidence = 0.4f;
    public const float LostSpread = 6f;
    public const int LostRecoveries = 2;
    const float SettleSeconds = 1f;
    // A sign the filter can't explain, while sure of itself: probably a misread, but its rings get this share of
    // every resample for RingSeconds, to win if they fit the scan and the walls better (Augmented MCL, biased).
    const float DoubtInjection = 0.05f;
    // A scan that fits nowhere near the cloud for this long, while Localized, is lost too (the wrong building wing).
    const float MisfitShare = 0.15f, MisfitSeconds = 8f;
    const int MisfitPoints = 60;
    const float RelocalizeRadius = 10f;
    // Steps (map-constrained PDR): 0.7 m a step give or take 0.1 (Weinberg's estimate needs the acceleration's
    // amplitude, which the step counter doesn't give), along the compass plus the offset learnt while tracking.
    public const float StepLength = 0.7f, StepLengthSigma = 0.1f;
    const float StepHeadingSigma = 12f, UnknownHeadingSigma = 35f;
    const float CompassLearning = 0.1f;
    const int CompassSamples = 8;
    // A plate read biases Augmented MCL's random particles toward its rings for this long.
    const float RingSeconds = 10f;
    // The scan and the walls weigh again only once the camera has moved or turned this much since they last did, as
    // ROS's amcl does (update_min_d, update_min_a): the same view again isn't new evidence, and counting it again
    // and again would make the filter surer than it is.
    const float UpdateDistance = 0.15f, UpdateAngle = 10f;

    readonly FloorPlan m_Plan;
    readonly Walkable m_Walkable;
    readonly ScanMatcher m_Scan;
    readonly WallMatcher m_Walls;
    readonly Config m_Config;
    readonly PositionFilter m_Filter;
    readonly Dictionary<string, (List<Vector2> at, List<Vector2> normal)> m_Signs =
        new Dictionary<string, (List<Vector2>, List<Vector2>)>(StringComparer.OrdinalIgnoreCase);

    Vector2 m_Camera, m_SnapCamera, m_SnapSpot;
    float m_CameraYaw = float.NaN, m_SnapYaw;
    bool m_Tracking;
    double m_Time, m_SureSince = -1, m_LooseSince = -1, m_MisfitSince = -1, m_RingUntil = -1;
    float m_CompassSin, m_CompassCos;
    int m_CompassCount;
    Vector2 m_LastWalk;
    Vector2 m_ScanCamera, m_WallsCamera;
    int m_Scans;
    float m_ScanYaw = float.NaN, m_WallsYaw = float.NaN;

    public LocalizerState State { get; private set; } = LocalizerState.Outdoors;
    public PositionFilter Filter => m_Config.raw ? null : m_Filter;
    public Config Settings => m_Config;
    /// <summary>The user's plan position now.</summary>
    public Vector2 Position { get; private set; }
    /// <summary>Where the phone faces on the plan: degrees clockwise from plan up.</summary>
    public float Heading { get; private set; }
    /// <summary>The plan's yaw in the AR world (Unity yaw): what the plan root is turned by.</summary>
    public float Yaw => m_Config.raw ? m_SnapYaw : m_Filter.Yaw;
    /// <summary>The radius round the estimate holding 68% of the weight, metres.</summary>
    public float Spread { get; private set; }
    public float Confidence { get; private set; }
    /// <summary>The compass-to-plan offset is learnt (steps can follow the compass).</summary>
    public bool CompassKnown => m_CompassCount >= CompassSamples;
    public float CompassOffset => Mathf.Atan2(m_CompassSin, m_CompassCos) * Mathf.Rad2Deg;
    public int WallsMatched { get; private set; }
    public ScanMatcher ScanMatcher => m_Scan;
    /// <summary>Times the state went into Lost.</summary>
    public int Relocalizations { get; private set; }

    /// <summary>The state changed: the new state and why ("snap", "sure", "loose", "spread", "walls", "scan",
    /// "sign", "found", "asked", "outdoors").</summary>
    public event Action<LocalizerState, string> StateChanged;
    /// <summary>A plate counted: its text.</summary>
    public event Action<string> SignUsed;

    public LocalizerEngine(FloorPlan plan, Walkable walkable, DistanceField field, WallMatcher walls, Config config,
        int seed = 1)
    {
        m_Plan = plan;
        m_Walkable = walkable;
        m_Config = config;
        m_Walls = walls;
        m_Filter = new PositionFilter(walkable, SnapParticles, seed, MaxParticles)
        {
            Adaptive = true,
            MinCount = MinParticles,
        };
        m_Scan = field != null ? new ScanMatcher(field, MaxParticles) : null;
        if (plan?.signs != null)
        {
            foreach (PlanSign sign in plan.signs)
            {
                if (string.IsNullOrEmpty(sign?.text))
                    continue;
                if (!m_Signs.TryGetValue(sign.text, out var list))
                    m_Signs[sign.text] = list = (new List<Vector2>(), new List<Vector2>());
                list.at.Add(sign.Position);
                list.normal.Add(sign.Normal);
            }
        }
    }

    // Standing just inside a door with your back to it: the plan's spot there is under the camera (world x, z), the
    // plan turned by baseYaw (Navigator's AlignTo works it out from the door and where the phone faces).
    public void Snap(double t, Vector2 spot, float baseYaw, Vector2 camera)
    {
        m_Time = t;
        m_SnapSpot = spot;
        m_SnapYaw = baseYaw;
        m_SnapCamera = m_Camera = camera;
        m_Tracking = true;
        m_Filter.SetCount(SnapParticles);
        m_Filter.Reset(spot, camera, baseYaw, SnapSpread, SnapHeadingSpread, SnapHeadingLimit, true);
        m_Scans = 0;
        m_SureSince = m_LooseSince = m_MisfitSince = -1;
        m_RingUntil = -1;
        m_ScanYaw = m_WallsYaw = float.NaN;
        Position = m_Config.raw ? spot : m_Filter.Estimate;
        SetState(LocalizerState.Converging, "snap");
        Measure();
    }

    // The user asked: spread out round the estimate, any heading, and wait for the walls, the scan or a sign.
    public void Relocalize(double t)
    {
        m_Time = t;
        if (State == LocalizerState.Outdoors || m_Config.raw)
            return;
        m_Filter.Scatter(m_Filter.Estimate, RelocalizeRadius, true);
        m_Scans = 0;
        Measure();
        SetState(LocalizerState.Lost, "asked");
    }

    public void Outdoors(double t)
    {
        m_Time = t;
        SetState(LocalizerState.Outdoors, "outdoors");
    }

    // Each frame: the AR camera (world x, z), its yaw (world degrees, where it faces flattened), and tracking.
    public void Pose(double t, Vector2 camera, float cameraYaw, bool tracking)
    {
        m_Time = t;
        if (State == LocalizerState.Outdoors)
            return;
        if (tracking && m_Tracking && (camera - m_Camera).sqrMagnitude > 0.0025f)
            m_LastWalk = camera - m_Camera;
        m_Camera = camera;
        m_Tracking = tracking;
        if (tracking)
            m_CameraYaw = cameraYaw;
        if (m_Config.raw)
        {
            Position = m_SnapSpot + PositionFilter.ToPlan(camera - m_SnapCamera, m_SnapYaw);
            Heading = Mathf.DeltaAngle(0f, m_CameraYaw - m_SnapYaw);
            Spread = 0f;
            Confidence = 1f;
            if (State == LocalizerState.Converging)
                SetState(LocalizerState.Localized, "raw");
            return;
        }
        m_Filter.Follow(camera, cameraYaw, tracking);
        if (m_RingUntil >= 0 && t > m_RingUntil)
        {
            m_Filter.ClearRing();
            m_RingUntil = -1;
            Doubt(false);
        }
        Measure();
    }

    // A depth scan: count world (x, z) points round the camera from the last couple of seconds.
    public bool Scan(double t, Vector2[] points, int count)
    {
        m_Time = t;
        if (!Active || !m_Config.depth || m_Scan == null || !Moved(ref m_ScanCamera, ref m_ScanYaw))
            return false;
        float strength = Mathf.Min(1f, FirstScan + (1f - FirstScan) * m_Scans / RampScans);
        if (!m_Scan.Weigh(m_Filter, points, count, strength))
            return false;
        m_Scans++;
        // A scan that fits nowhere near the cloud for long, while sure: it's somewhere else.
        bool misfit = m_Scan.LastPoints >= MisfitPoints && m_Scan.LastHitShare < MisfitShare;
        if (!misfit || State != LocalizerState.Localized)
            m_MisfitSince = -1;
        else if (m_MisfitSince < 0)
            m_MisfitSince = t;
        Measure();
        return true;
    }

    // The walls ARCore sees round the camera (world x, z).
    public int Walls(double t, IReadOnlyList<WallObservation> walls)
    {
        m_Time = t;
        if (!Active || !m_Config.wallPlanes || m_Walls == null || !Moved(ref m_WallsCamera, ref m_WallsYaw))
            return WallsMatched;
        WallsMatched = m_Walls.Weigh(m_Filter, walls, m_Camera);
        Measure();
        return WallsMatched;
    }

    // A plate read: its text, and from the camera where the frame was taken (world x, z), the world bearing (degrees
    // clockwise from world +z) and flat range in metres, with the range's standard deviation.
    public Sighted Sighting(double t, string text, Vector2 camera, float worldBearing, float worldRange, float sigma)
    {
        m_Time = t;
        if (!Active || !m_Config.plates)
            return Sighted.Ignored;
        if (string.IsNullOrEmpty(text) || !m_Signs.TryGetValue(text.Trim(), out var sign) || !(worldRange > 0f))
            return Sighted.Unknown;
        // From the camera where the filter last moved: the sign stays put while the user walks on, so a late
        // sighting is as good as a fresh one.
        Vector2 toSign = camera + PositionFilter.HeadingVector(worldBearing) * worldRange - m_Filter.Camera;
        float bearing = PositionFilter.Heading(toSign), range = toSign.magnitude;
        Sighted result;
        // Augmented MCL's random particles go round this sign for a while (Probabilistic Robotics, Table 8.3, biased).
        m_Filter.SetRing(bearing, range, sigma, sign.at, sign.normal);
        m_RingUntil = t + RingSeconds;
        if (State == LocalizerState.Lost)
        {
            // Lost: the sign alone says where (on its rings), any heading; then it's weighed as usual.
            m_Filter.SeedRing(bearing, range, sigma, sign.at, sign.normal);
            m_Scans = 0;
            m_Filter.AddSignObservation(bearing, range, sigma, sign.at, sign.normal);
            result = Sighted.Placed;
        }
        else
        {
            if (!m_Filter.AddSignObservation(bearing, range, sigma, sign.at, sign.normal))
            {
                // Put down to a misread, but doubted: the sign's rings get a share of the particles meanwhile.
                Doubt(true);
                if (State == LocalizerState.Localized)
                    SetState(LocalizerState.Converging, "doubt");
                return Sighted.Ignored;
            }
            result = Sighted.Used;
        }
        SignUsed?.Invoke(text);
        if (State == LocalizerState.Lost)
        {
            m_SureSince = m_LooseSince = -1;
            SetState(LocalizerState.Converging, "sign");
        }
        Measure();
        return result;
    }

    // The compass (degrees clockwise from north, where the camera looks) while ARCore tracks: learns the offset from
    // compass to plan, for steps taken blind.
    public void Compass(double t, float compass)
    {
        m_Time = t;
        if (!Active || !m_Tracking || float.IsNaN(compass) || float.IsNaN(m_CameraYaw) || State != LocalizerState.Localized)
            return;
        float plan = m_CameraYaw - Yaw;
        float offset = Mathf.DeltaAngle(compass, plan) * Mathf.Deg2Rad;
        float k = m_CompassCount == 0 ? 1f : CompassLearning;
        m_CompassSin += k * (Mathf.Sin(offset) - m_CompassSin);
        m_CompassCos += k * (Mathf.Cos(offset) - m_CompassCos);
        m_CompassCount++;
    }

    // A step while ARCore can't track: along the compass (NaN if not known) by length metres.
    public void Step(double t, float compass, float length)
    {
        m_Time = t;
        if (!Active || !m_Config.steps || m_Tracking)
            return;
        float heading, sigma;
        if (!float.IsNaN(compass) && CompassKnown)
        {
            heading = compass + CompassOffset;
            sigma = StepHeadingSigma;
        }
        else if (m_LastWalk.sqrMagnitude > 0f)
        {
            // No compass yet: carry on the way the last tracked walk went.
            heading = PositionFilter.Heading(PositionFilter.ToPlan(m_LastWalk, Yaw));
            sigma = UnknownHeadingSigma;
        }
        else
        {
            return;
        }
        m_Filter.StepBlind(PositionFilter.HeadingVector(heading) * (length > 0f ? length : StepLength), StepLengthSigma,
            sigma);
        Measure();
    }

    /// <summary>The time budget on the phone: at most this many particles from now on.</summary>
    public void LimitParticles(int max) => m_Filter.MaxCount = Mathf.Max(MinParticles, max);

    bool Active => State != LocalizerState.Outdoors && !m_Config.raw;

    // Moved or turned enough since this signal last weighed (and if so, now is the new last time).
    bool Moved(ref Vector2 camera, ref float yaw)
    {
        if (!float.IsNaN(yaw) && (m_Camera - camera).sqrMagnitude < UpdateDistance * UpdateDistance &&
            Mathf.Abs(Mathf.DeltaAngle(yaw, m_CameraYaw)) < UpdateAngle)
            return false;
        camera = m_Camera;
        yaw = float.IsNaN(m_CameraYaw) ? 0f : m_CameraYaw;
        return true;
    }

    // The outputs, and the state machine on them.
    void Measure()
    {
        if (m_Config.raw || State == LocalizerState.Outdoors)
            return;
        Position = m_Tracking ? m_Filter.Current(m_Camera) : m_Filter.Estimate;
        if (!float.IsNaN(m_CameraYaw))
            Heading = Mathf.DeltaAngle(0f, m_CameraYaw - m_Filter.Yaw);
        Spread = m_Filter.Radius(0.68f);
        Confidence = m_Filter.Confidence;

        bool sure = Spread < LocalizedSpread && Confidence > LocalizedConfidence;
        bool loose = Spread > LooseSpread || Confidence < LooseConfidence;
        if (!sure)
            m_SureSince = -1;
        else if (m_SureSince < 0)
            m_SureSince = m_Time;
        if (!loose)
            m_LooseSince = -1;
        else if (m_LooseSince < 0)
            m_LooseSince = m_Time;

        if (State != LocalizerState.Lost)
        {
            if (Spread > LostSpread)
                SetState(LocalizerState.Lost, "spread");
            else if (m_Filter.RecentRecoveries >= LostRecoveries)
                SetState(LocalizerState.Lost, "walls");
            else if (m_MisfitSince >= 0 && m_Time - m_MisfitSince >= MisfitSeconds)
                SetState(LocalizerState.Lost, "scan");
            else if (State == LocalizerState.Converging && m_SureSince >= 0 && m_Time - m_SureSince >= SettleSeconds)
                SetState(LocalizerState.Localized, "sure");
            else if (State == LocalizerState.Localized && m_LooseSince >= 0 && m_Time - m_LooseSince >= SettleSeconds)
                SetState(LocalizerState.Converging, "loose");
        }
    }

    // Doubting the cloud: the last sign's rings get DoubtInjection of every resample, Lost or not.
    void Doubt(bool on)
    {
        m_Filter.MinimumInjection = on ? DoubtInjection : 0f;
        m_Filter.AllowInjection = on || State == LocalizerState.Lost;
    }

    void SetState(LocalizerState state, string why)
    {
        if (state == State)
            return;
        if (state == LocalizerState.Lost)
        {
            Relocalizations++;
            m_Filter.FreeHeading();
            m_MisfitSince = -1;
        }
        if (State == LocalizerState.Lost)
            m_Filter.ForgetRecoveries();   // out of Lost: the wipe-outs that put it there are history
        // Augmented MCL's random particles only while lost or doubting (see PositionFilter.AllowInjection).
        State = state;
        Doubt(m_Filter.MinimumInjection > 0f && m_Filter.HasRing);
        StateChanged?.Invoke(state, why);
    }
}
