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
public sealed partial class LocalizerEngine
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
    // And the door is often tapped a few steps on from it: this share of the particles goes anywhere in sight of the
    // spot from NearFrom to NearReach metres away, so a walk along a plain corridor can't settle a few metres out.
    // A snap may have been at another door (the user picked the wrong one, or GPS suggested it): this share of the
    // particles goes to the alternatives (other entrances, or doors nearby), the rest to the door snapped at.
    const float AlternativeShare = 0.15f;
    const float NearShare = 0.15f, NearFrom = 1f, NearReach = 4f;
    // Localized only on evidence: this many scans that fit (a share of points near a wall of at least FitShare) or
    // walls matched or a plate, and almost all the weight (Wide of it) within twice LocalizedSpread: a snap alone, or
    // a cloud still split between two doors, isn't a position.
    const int EvidenceNeeded = 3;
    const float FitShare = 0.35f, Wide = 0.95f;
    // States, with hysteresis: Localized needs the cloud tight and confident for SettleSeconds; it's left again only
    // once clearly loose for as long. Lost is a cloud wider than LostSpread for WideSeconds (not for a moment: a
    // snap's other doors and a sign's rings widen it on purpose, and the next scans narrow it again), or two wipe-outs within 30 m walked, or
    // a scan that fits nowhere for MisfitSeconds. A sign gets out of Lost at once; the walls and the scan only to
    // Converging, after FoundSeconds of a tight cloud, and from there Localized needs a sign: in a building of
    // look-alike hallways the walls can settle on the wrong one with every sign of confidence (seen in the synthetic
    // relocalizations), so being sure again takes a room number.
    public const float LocalizedSpread = 1.5f, LocalizedConfidence = 0.6f;
    public const float LooseSpread = 2.5f, LooseConfidence = 0.4f;
    // And the heading: a cloud tight in position but 15 degrees wide in heading walks off a metre every 4 m.
    public const float LocalizedHeadingSpread = 8f, LooseHeadingSpread = 15f;
    public const float LostSpread = 6f;
    public const int LostRecoveries = 2;
    // Plates: the likelihood left to a particle that couldn't have seen one (it may be a misread or a poster), and how
    // long a contradicting plate waits for a second one.
    const float LostSplit = 0.5f;

    const float PlateOutlier = 0.01f, ContradictionSeconds = 10f, AgreeSlack = 1.5f;
    // The scale term (see Measure): the least scale spread believed, the most it may be while sure (metres), and
    // what a scan needs to fix the place along the hallway (its fit, and FixPoints on a wall across the way within
    // FixReach).
    // The least scale spread believed is ARCore's own scale error, as far as known: the cloud's spread can draw tight
    // on a wrong scale (stress walks with ARCore 8% short), and at 3% that still left them sure and wrong twice
    // as often as at 4%, which costs about 6 points of sure time (5%: 0.4% wrong, 9 points).
    const float MinScaleSigma = 0.04f, ScaleGate = 1.5f, FixShare = 0.6f, FixReach = 6f;
    const int FixPoints = 15, FixScans = 3;
    int m_FixStreak;
    float m_FixWalk;

    /// <summary>The scale term in Spread, metres: the scale's spread times the walk since the last absolute fix.</summary>
    public float ScaleTerm { get; private set; }
    /// <summary>Walls across the way seen so far (absolute fixes along the hallway).</summary>
    public int WallFixes { get; private set; }
    const float SpreadScale = 0.4f, SpreadAfterLoss = 0.8f, LossMemory = 60f;
    double m_LastLoss = double.NegativeInfinity;
    const float SettleSeconds = 1f, FoundSeconds = 5f, WideSeconds = 5f;
    // Steps alone (ARCore not tracking) for this long: not sure any more, until ARCore and the scans are back. A turn
    // the steps don't see (no compass yet, or a compass the building's steel turns) puts them metres out in seconds.
    const float BlindSeconds = 2f;
    // The estimate jumping this far in one update (a mode switch) starts the settling again; a sign moving it this far
    // needs a second to agree.
    const float ModeJump = 2f, SignMove = 2f;
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
    // The scan only every ScanDistance walked (or UpdateAngle turned): its buffer holds two seconds of points, some
    // 2.5 m of walking, so weighed every 15 cm each point would count a dozen times or more over, and the clutter and
    // plan errors in it with it. The stress walks drew tight on wrong poses half as often at 0.5 m, and at 0.75 m
    // (with z_rand 0.3) less again, and without a sharp edge either side (the sensitivity analysis).
    const float ScanDistance = 0.75f;

    readonly FloorPlan m_Plan;
    readonly Walkable m_Walkable;
    double m_SnapTime;
    readonly ScanMatcher m_Scan;
    readonly WallMatcher m_Walls;
    readonly Config m_Config;
    readonly PositionFilter m_Filter;
    readonly Dictionary<string, (List<Vector2> at, List<Vector2> normal)> m_Signs =
        new Dictionary<string, (List<Vector2>, List<Vector2>)>(StringComparer.OrdinalIgnoreCase);

    Vector2 m_Camera, m_SnapCamera, m_SnapSpot;
    float m_CameraYaw = float.NaN, m_SnapYaw;
    bool m_Tracking;
    double m_Time, m_SureSince = -1, m_LooseSince = -1, m_MisfitSince = -1, m_RingUntil = -1, m_WideSince = -1, m_UntrackedSince = -1;
    float m_CompassSin, m_CompassCos;
    int m_CompassCount;
    Vector2 m_LastWalk;
    Vector2 m_ScanCamera, m_WallsCamera;
    int m_Scans, m_Evidence;
    Vector2 m_LastEstimate;
    bool m_HadEstimate;
    bool m_NeedsSign;
    string m_PlacedBy;
    string m_Contradicted;   // the last plate nothing in the cloud could have seen, and when
    double m_ContradictedAt = double.NegativeInfinity;
    List<Vector2> m_ContradictedSigns;
    float m_ContradictedRange;
    Vector2 m_ContradictedCamera;

    // A plate weighed robustly (PlateOutlier: it may be a misread or a poster). One that nothing in the cloud could
    // have seen is put down to a misread (false; remembered); a second, another plate, that also says otherwise within
    // ContradictionSeconds and could have been seen along with the first starts the cloud again on its rings.
    bool WeighPlate(double t, string text, List<Vector2> at, float worldRange, Vector2 camera, float bearing, float range,
        float sigma, List<Vector2> normals)
    {
        string key = text.Trim();
        m_Filter.SeedOnContradiction = m_Contradicted != null && m_Contradicted != key &&
                                       t - m_ContradictedAt < ContradictionSeconds && Agree(at, worldRange, camera);
        m_Filter.PlateOutlier = PlateOutlier;
        bool fits = m_Filter.AddSignObservation(bearing, range, sigma, at, normals);
        m_Filter.SeedOnContradiction = null;
        m_Filter.PlateOutlier = 0f;
        if (m_Filter.SignSeeded)
            DropShadows();   // two plates agreed on somewhere else: the snap's doors are history
        else
            PlateShadows(bearing, range, sigma, at, normals);
        if (CheckSnap(t, key, worldRange) && LastSnapCheck == SnapCheck.Corrected)
            fits = true;   // the plate's own door's cloud took over, and it fits there
        if (fits)
        {
            m_Contradicted = null;
            return true;
        }
        m_Contradicted = key;
        m_ContradictedAt = t;
        m_ContradictedSigns = at;
        m_ContradictedRange = worldRange;
        m_ContradictedCamera = camera;
        return false;
    }

    // Whether a plate seen at range from camera (world) and the last contradicting one could both have been seen,
    // walking between: some pair of their plates no further apart than the two ranges and the walk, give or take.
    // Two plates that can't both be right (a misread and a poster in one frame, say) don't add up to a fix.
    bool Agree(List<Vector2> signs, float range, Vector2 camera)
    {
        float reach = range + m_ContradictedRange + Vector2.Distance(camera, m_ContradictedCamera) + AgreeSlack;
        foreach (Vector2 a in signs)
            foreach (Vector2 b in m_ContradictedSigns)
                if ((a - b).sqrMagnitude <= reach * reach)
                    return true;
        return false;
    }   // the plate that last placed the cloud: it can't confirm itself   // been Lost since the last sign: Localized again only after one
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
    /// <summary>The compass-to-ARCore-world offset is learnt (steps can follow the compass).</summary>
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
        m_Field = field;
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
    public void Snap(double t, Vector2 spot, float baseYaw, Vector2 camera) => Snap(t, spot, baseYaw, camera, null, null);

    // With the other doors the snap might really have been at (their spots, and the plan's yaw if it was there).
    public void Snap(double t, Vector2 spot, float baseYaw, Vector2 camera, IReadOnlyList<Vector2> alternatives,
        IReadOnlyList<float> alternativeYaws)
    {
        m_Time = t;
        m_SnapSpot = spot;
        m_SnapYaw = baseYaw;
        m_SnapCamera = m_Camera = camera;
        m_Tracking = true;
        int others = alternatives?.Count ?? 0, count = SnapParticles;
        m_Filter.SetCount(count);
        m_Filter.Reset(spot, camera, baseYaw, SnapSpread, SnapHeadingSpread, SnapHeadingLimit, true);
        m_Scans = 0;
        m_Evidence = 0;
        m_NeedsSign = false;
        m_HadEstimate = false;
        m_FixWalk = 0f;
        int near = Mathf.RoundToInt(count * NearShare);
        m_Filter.SeedNear(0, near, spot, baseYaw, NearFrom, NearReach, SnapHeadingSpread);
        if (others > 0)
        {
            m_Filter.FreeHeading();   // another door faces another way
            int each = Mathf.Max(1, Mathf.RoundToInt(count * AlternativeShare / others));
            for (int k = 0; k < others; k++)
                m_Filter.SeedHypothesis(count - each * (k + 1), each, alternatives[k], alternativeYaws[k], SnapSpread,
                    SnapHeadingSpread);
        }
        m_SnapTime = t;
        StartShadows(spot, camera, alternatives, alternativeYaws);
        m_SureSince = m_LooseSince = m_MisfitSince = m_WideSince = -1;
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
        DropShadows();
        m_Filter.Scatter(m_Filter.Estimate, RelocalizeRadius, true);
        m_Scans = 0;
        m_Evidence = 0;
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
        if (tracking && m_Tracking)
            m_FixWalk += Vector2.Distance(camera, m_Camera);
        m_Camera = camera;
        if (tracking)
            m_UntrackedSince = -1;
        else if (m_UntrackedSince < 0)
            m_UntrackedSince = m_LastLoss = t;
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
        FollowShadows(camera, cameraYaw, tracking);
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
        if (!Active || !m_Config.depth || m_Scan == null || !Moved(ref m_ScanCamera, ref m_ScanYaw, ScanDistance))
            return false;
        float strength = Mathf.Min(1f, FirstScan + (1f - FirstScan) * m_Scans / RampScans);
        ScanShadows(points, count, strength);
        if (!m_Scan.Weigh(m_Filter, points, count, strength))
            return false;
        ScannedMain();
        m_Scans++;
        if (m_Scan.LastHitShare >= FitShare)
            m_Evidence++;
        // A wall across the way, well fitted, in FixScans scans in a row (seen from places ScanDistance apart, so one
        // lucky fit of a wrong cloud doesn't count): an absolute fix along the hallway (see the scale term).
        bool facing = m_Scan.LastHitShare >= FixShare && m_LastWalk.sqrMagnitude > 0f &&
                      m_Scan.HitsFacing(m_Filter, PositionFilter.ToPlan(m_LastWalk, m_Filter.Yaw), FixReach) >= FixPoints;
        m_FixStreak = facing ? m_FixStreak + 1 : 0;
        if (m_FixStreak >= FixScans)
        {
            m_FixWalk = 0f;
            WallFixes++;
        }
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
        if (WallsMatched > 0)
            m_Evidence++;
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
        bool seeded;
        bool tight = m_Filter.Radius(0.68f) < LooseSpread;
        Vector2 was = m_Filter.Estimate;
        // Augmented MCL's random particles go round this sign for a while (Probabilistic Robotics, Table 8.3, biased).
        m_Filter.SetRing(bearing, range, sigma, sign.at, sign.normal);
        m_RingUntil = t + RingSeconds;
        if (State == LocalizerState.Lost)
        {
            // Lost: a tight cloud is weighed against it first (it may well be right already, just not trusted), as
            // when not Lost; if nothing in it could have seen the plate, LostSplit of it goes onto the plate's rings
            // and the rest stays (the plate may be a misread), unless a second plate agrees: then all of it. A wide
            // cloud has too few particles on the sign's rings to say where on them: those are sampled afresh.
            seeded = !tight;
            if (tight)
            {
                if (!WeighPlate(t, text, sign.at, worldRange, camera, bearing, range, sigma, sign.normal))
                {
                    m_Filter.SplitRing(bearing, range, sigma, sign.at, sign.normal, LostSplit);
                    seeded = true;
                }
            }
            else
            {
                m_Filter.SeedRing(bearing, range, sigma, sign.at, sign.normal);
                m_Filter.AddSignObservation(bearing, range, sigma, sign.at, sign.normal);
                seeded = true;
            }
            seeded |= m_Filter.SignSeeded;
            m_Scans = 0;
            result = Sighted.Placed;
        }
        else
        {
            if (!WeighPlate(t, text, sign.at, worldRange, camera, bearing, range, sigma, sign.normal))
            {
                // Put down to a misread, but doubted: the sign's rings get a share of the particles meanwhile.
                Doubt(true);
                if (State == LocalizerState.Localized)
                    SetState(LocalizerState.Converging, "doubt");
                return Sighted.Ignored;
            }
            // A sign that moved the estimate far chose a minor mode (a snap's other door, a far side of the cloud):
            // as much a single witness as one that started the cloud again.
            seeded = m_Filter.SignSeeded || (m_Filter.Estimate - was).sqrMagnitude > SignMove * SignMove;
            result = seeded ? Sighted.Placed : Sighted.Used;
        }
        SignUsed?.Invoke(text);
        m_FixWalk = 0f;   // a plate: an absolute fix
        // A sign that agrees with a cloud that was already sure of itself confirms it. One that had to choose (a cloud
        // started again, moved, or wide) fixes a ring round the sign, and every heading has a place on it: the
        // scans or the walls have to agree with the choice before it's sure.
        bool chose = seeded || m_NeedsSign;
        m_Evidence = chose ? 0 : m_Evidence + EvidenceNeeded;
        // One plate that put the cloud somewhere new is a single witness (plates are misread, posters look like
        // plates): Localized waits for a second that agrees with where the first put it, and another plate, not the
        // same one read again.
        string key = text.Trim();
        if (seeded)
            m_PlacedBy = key;
        m_NeedsSign = seeded || (m_NeedsSign && key == m_PlacedBy);
        if (!seeded && State != LocalizerState.Lost)
            Doubt(false);   // a sign that agrees with the cloud settles an earlier one that didn't
        if (seeded && State == LocalizerState.Localized)
            SetState(LocalizerState.Converging, "placed");
        if (State == LocalizerState.Lost)
        {
            m_SureSince = m_LooseSince = -1;
            SetState(LocalizerState.Converging, "sign");
        }
        Measure();
        return result;
    }

    // The compass (degrees clockwise from north, where the camera looks) while ARCore tracks: learns the offset from
    // compass to ARCore's world, for steps taken blind. To the world, not the plan: that needs no position fix, so it's
    // learnt from the first seconds, and each particle turns a blind step onto the plan by its own heading.
    public void Compass(double t, float compass)
    {
        m_Time = t;
        if (!Active || !m_Tracking || float.IsNaN(compass) || float.IsNaN(m_CameraYaw))
            return;
        float offset = Mathf.DeltaAngle(compass, m_CameraYaw) * Mathf.Deg2Rad;
        float k = m_CompassCount == 0 ? 1f : CompassLearning;
        m_CompassSin += k * (Mathf.Sin(offset) - m_CompassSin);
        m_CompassCos += k * (Mathf.Cos(offset) - m_CompassCos);
        m_CompassCount++;
    }

    // A step while ARCore can't track: along the compass (NaN if not known) by length metres.
    public void Step(double t, float compass, float length)
    {
        m_Time = t;
        if (!Active || !m_Config.steps)
            return;
        if (m_Tracking)
        {
            LearnStep(t);
            return;
        }
        m_StepAt = double.NaN;
        float heading, sigma;   // in ARCore's world
        if (!float.IsNaN(compass) && CompassKnown)
        {
            heading = compass + CompassOffset;
            sigma = StepHeadingSigma;
        }
        else if (m_LastWalk.sqrMagnitude > 0f)
        {
            // No compass yet: carry on the way the last tracked walk went.
            heading = PositionFilter.Heading(m_LastWalk);
            sigma = UnknownHeadingSigma;
        }
        else
        {
            return;
        }
        if (!(length > 0f))
            length = LearntStepLength;
        m_FixWalk += length;
        m_Filter.StepBlindWorld(heading, length, StepLengthSigma, sigma);
        StepShadows(heading, length, sigma);
        Measure();
    }

    // The user's step, learnt while ARCore tracks (as the plate digits' height is): the camera's walk from one step to
    // the next, steps under StepGapSeconds apart and of a plausible length only (a stop, a turn on the spot or a pose
    // jump between two steps says nothing), averaged slowly. People's steps run 0.55 to 0.85 m; StepLength until learnt.
    void LearnStep(double t)
    {
        if (!double.IsNaN(m_StepAt) && t - m_StepAt < StepGapSeconds)
        {
            float d = Vector2.Distance(m_Camera, m_StepCamera);
            if (d >= MinStepLength && d <= MaxStepLength)
            {
                m_StepCount++;
                // A running mean with StepLength counted as StepPrior steps, then a slow average.
                float k = Mathf.Max(StepLearning, 1f / (m_StepCount + StepPrior));
                LearntStepLength += k * (d - LearntStepLength);
            }
        }
        m_StepAt = t;
        m_StepCamera = m_Camera;
    }

    /// <summary>The step length blind steps use, metres: StepLength until learnt from ARCore.</summary>
    public float LearntStepLength { get; private set; } = StepLength;
    const float StepGapSeconds = 1.5f, MinStepLength = 0.3f, MaxStepLength = 1.2f, StepLearning = 0.05f;
    double m_StepAt = double.NaN;
    Vector2 m_StepCamera;
    int m_StepCount;
    const int StepPrior = 5;

    /// <summary>The time budget on the phone: at most this many particles from now on.</summary>
    public void LimitParticles(int max) => m_Filter.MaxCount = Mathf.Max(MinParticles, max);

    bool Active => State != LocalizerState.Outdoors && !m_Config.raw;

    // Moved or turned enough since this signal last weighed (and if so, now is the new last time).
    bool Moved(ref Vector2 camera, ref float yaw) => Moved(ref camera, ref yaw, UpdateDistance);

    bool Moved(ref Vector2 camera, ref float yaw, float distance)
    {
        if (!float.IsNaN(yaw) && (m_Camera - camera).sqrMagnitude < distance * distance &&
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
        DecideShadows();
        Vector2 previous = m_Filter.Estimate;
        bool jumped = m_HadEstimate && (previous - m_LastEstimate).sqrMagnitude > ModeJump * ModeJump;
        m_LastEstimate = previous;
        m_HadEstimate = true;
        Position = m_Tracking ? m_Filter.Current(m_Camera) : m_Filter.Estimate;
        if (!float.IsNaN(m_CameraYaw))
            Heading = Mathf.DeltaAngle(0f, m_CameraYaw - m_Filter.Yaw);
        float radius = m_Filter.Radius(0.68f);
        // ARCore's scale isn't known exactly: walked since the last absolute fix, the cloud's place along the way is
        // off by up to the scale's own spread times that walk (the cloud alone can't show it: every particle walks
        // it alike but for its scale). In Spread, and sure only while it's under ScaleGate.
        ScaleTerm = Mathf.Max(m_Filter.ScaleSpread, MinScaleSigma) * m_FixWalk;
        // Spread is the cloud's 68% radius, scaled, with the scale term on top, to hold the error about 68% of the time
        // (the stress walks: the cloud is wider than the error, except after ARCore has lost tracking, when its jumps
        // are on top).
        Spread = radius * (m_Time - m_LastLoss < LossMemory ? SpreadAfterLoss : SpreadScale);
        Spread = Mathf.Sqrt(Spread * Spread + ScaleTerm * ScaleTerm);
        Confidence = m_Filter.Confidence;

        bool blind = m_UntrackedSince >= 0 && m_Time - m_UntrackedSince >= BlindSeconds;
        bool sure = Spread < LocalizedSpread && Confidence > LocalizedConfidence && !jumped && m_Tracking &&
                    ScaleTerm < ScaleGate &&
                    m_Filter.HeadingSpread < LocalizedHeadingSpread;
        bool loose = Spread > LooseSpread || Confidence < LooseConfidence || m_Filter.HeadingSpread > LooseHeadingSpread;
        WalkItOut(sure && m_Evidence >= EvidenceNeeded && !Doubting && m_Filter.Radius(Wide) < 2f * LocalizedSpread);
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
            if (radius <= LostSpread)
            {
                m_WideSince = -1;
            }
            else if (m_WideSince < 0)
            {
                // Wide even for a moment: whichever part of it wins, being sure again takes a sign.
                m_WideSince = m_Time;
                m_NeedsSign = true;
                if (State == LocalizerState.Localized)
                    SetState(LocalizerState.Converging, "wide");
            }
            if (m_WideSince >= 0 && m_Time - m_WideSince >= WideSeconds)
                SetState(LocalizerState.Lost, "spread");
            else if (m_Filter.RecentRecoveries >= LostRecoveries)
                SetState(LocalizerState.Lost, "walls");
            else if (m_MisfitSince >= 0 && m_Time - m_MisfitSince >= MisfitSeconds)
                SetState(LocalizerState.Lost, "scan");
            else if (State == LocalizerState.Converging && m_SureSince >= 0 && m_Time - m_SureSince >= SettleSeconds &&
                     m_Evidence >= EvidenceNeeded && !m_NeedsSign && !Doubting && m_Shadows.Count == 0 &&
                     m_Filter.Radius(Wide) < 2f * LocalizedSpread)
                SetState(LocalizerState.Localized, "sure");
            else if (State == LocalizerState.Localized && m_LooseSince >= 0 && m_Time - m_LooseSince >= SettleSeconds)
                SetState(LocalizerState.Converging, "loose");
            else if (State == LocalizerState.Localized && jumped)
                SetState(LocalizerState.Converging, "jumped");
            else if (State == LocalizerState.Localized && blind)
                SetState(LocalizerState.Converging, "blind");
        }
        else if (m_SureSince >= 0 && m_Time - m_SureSince >= FoundSeconds && m_Evidence >= EvidenceNeeded)
        {
            SetState(LocalizerState.Converging, "found");
        }
        NotSure = State == LocalizerState.Localized ? null
            : State == LocalizerState.Lost ? "lost"
            : !m_Tracking ? "blind"
            : m_Shadows.Count > 0 ? "other doors"
            : m_NeedsSign ? "needs plate"
            : Doubting ? "doubting a plate"
            : ScaleTerm >= ScaleGate ? "scale"
            : Spread >= LocalizedSpread ? "position spread"
            : m_Filter.HeadingSpread >= LocalizedHeadingSpread ? "heading spread"
            : Confidence <= LocalizedConfidence ? "low confidence"
            : m_Filter.Radius(Wide) >= 2f * LocalizedSpread ? "a far tail"
            : m_Evidence < EvidenceNeeded ? "evidence"
            : "settling";
    }

    // Topology instead of a plate: a cloud that has stayed tight and alone while the user walked WalkOutDistance and
    // turned WalkOutTurn degrees in all (corners, junctions) has matched the walk's shape to one place in the building;
    // look-alike hallways seldom share a whole stretch of turns. Then no plate is needed to be sure again.
    void WalkItOut(bool tight)
    {
        if (!m_NeedsSign || !tight || !m_Tracking)
        {
            m_OutWalked = m_OutTurned = 0f;
            m_OutFrom = m_Camera;
            m_OutDirection = float.NaN;
            return;
        }
        Vector2 step = m_Camera - m_OutFrom;
        if (step.sqrMagnitude < OutSegment * OutSegment)
            return;
        float direction = PositionFilter.Heading(step);
        if (!float.IsNaN(m_OutDirection))
            m_OutTurned += Mathf.Abs(Mathf.DeltaAngle(m_OutDirection, direction));
        m_OutDirection = direction;
        m_OutWalked += step.magnitude;
        m_OutFrom = m_Camera;
        if (m_OutWalked >= WalkOutDistance && m_OutTurned >= WalkOutTurn)
        {
            m_NeedsSign = false;
            m_OutWalked = m_OutTurned = 0f;
        }
    }

    const float WalkOutDistance = 15f, WalkOutTurn = 60f, OutSegment = 1f;
    float m_OutWalked, m_OutTurned, m_OutDirection = float.NaN;
    Vector2 m_OutFrom;

    /// <summary>Why it isn't Localized (null when it is): for the evaluator's breakdown of the time not sure.</summary>
    public string NotSure { get; private set; }

    // A sign said otherwise a moment ago (its rings still take particles): not sure until it agrees or times out.
    bool Doubting => m_Filter.MinimumInjection > 0f && m_Filter.HasRing;

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
            DropShadows();
            m_NeedsSign = true;
            m_PlacedBy = null;
            m_Evidence = 0;
            m_Filter.FreeHeading();
            m_MisfitSince = -1;
        }
        if (State == LocalizerState.Lost)
            m_Filter.ForgetRecoveries();   // out of Lost: the wipe-outs that put it there are history
        // Augmented MCL's random particles only while lost or doubting (see PositionFilter.AllowInjection).
        if (State == LocalizerState.Localized)
            m_SureSince = -1;   // doubted or loose: sure again takes SettleSeconds afresh
        State = state;
        // Sure again: the doubt is settled, and a sign's rings stop taking particles from a cloud that fits.
        Doubt(state != LocalizerState.Localized && Doubting);
        StateChanged?.Invoke(state, why);
    }
}
