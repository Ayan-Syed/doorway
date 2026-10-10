using System;
using System.Collections.Generic;
using UnityEngine;

// Where the user is on the floor plan: Monte Carlo localization (Dellaert, Fox, Burgard & Thrun, ICRA 1999), the
// particle filter robots use to find themselves on a map. Each particle is a guess at three things: the user's plan
// position; how far the AR world is turned from the plan beyond the start alignment (a heading offset, degrees);
// and how many plan metres one AR metre is (the drawing isn't exactly to scale, and neither is ARCore).
//
// - Motion: ARCore's pose deltas through the odometry motion model (Thrun, Burgard & Fox, Probabilistic Robotics,
//   2005, §5.4, Table 5.6), each particle moving its own way; a step that crosses a wall kills the particle.
// - Measurements weigh the particles: depth scans and walls ARCore sees (Reweight), room-number signs
//   (AddSignObservation), and steps counted while the camera can't track (StepBlind).
// - Resampling: the low-variance sampler (Probabilistic Robotics, Table 4.4), only when the effective number of
//   particles falls under half. The number of particles adapts by KLD-sampling (Fox, IJRR 2003): few while the
//   cloud is tight, many while it's spread out. Augmented MCL (Probabilistic Robotics, Table 8.3) injects random
//   particles, from the walkable floor or round a sign just read, when measurements fit worse than they used to:
//   that's how a filter that settled in the wrong place (the kidnapped robot) finds itself again.
//
// Plain C# with its own seeded random numbers, so a walk replayed from its log runs exactly as it did on the phone.
// Plan and world: a world direction (x, z) turns into a plan direction by the plan's yaw, BaseYaw + the heading
// offset: the Unity yaw (clockwise from above) Navigator gives the plan root.
public sealed class PositionFilter
{
    public const int DefaultCount = 300;
    /// <summary>Camera travel gathered into one motion update, metres: the noise is per metre walked, so steps of
    /// one length keep the frame rate out of it, and walls are checked a few times a second, not every frame.</summary>
    public const float Step = 0.25f;
    // The odometry motion model's α1..α4 (Probabilistic Robotics, Table 5.6), for ARCore's visual-inertial odometry:
    // rotation noise from rotation and from translation (rad² per rad², rad² per m²), translation noise from
    // translation and from rotation (m² per m², m² per rad²). ARCore's local motion is good; its slow drift is the
    // heading offset's and the scale's own random walk below.
    const float Alpha1 = 0.003f, Alpha2 = 0.04f, Alpha3 = 0.0025f, Alpha4 = 0.0005f;
    /// <summary>Heading offset noise: degrees per square root of a metre walked (ARCore's yaw drifts slowly).</summary>
    public const float HeadingNoise = 0.4f;
    /// <summary>Scale noise: per square root of a metre walked.</summary>
    public const float ScaleNoise = 0.004f;
    /// <summary>Standing with your back to a door is rarely more than 15 degrees off square, and ARCore drifts a few
    /// degrees a building: more than this is a wrong match, not a correction (until the filter is lost).</summary>
    public const float MaxHeadingOffset = 30f;
    public const float MinScale = 0.9f, MaxScale = 1.1f;
    /// <summary>The camera moving further than this in one frame while tracking is ARCore relocalizing, not a walk.</summary>
    public const float JumpDistance = 1f;

    // KLD-sampling (Fox, IJRR 2003): error bound ε and confidence 1 - δ (its upper quantile z), on bins of
    // 0.25 m x 0.25 m x 10 degrees.
    const float KldEpsilon = 0.05f, KldQuantile = 2.326f;   // δ = 0.01
    const float BinSize = 0.25f, BinDegrees = 10f;
    // Augmented MCL's averages of the measurement likelihood: slow and fast (α_slow << α_fast, per update).
    const float AlphaSlow = 0.005f, AlphaFast = 0.1f;
    // At most this share of a resample is injected, and only once the slow average has seen this many updates.
    const float MaxInjection = 0.25f;
    const int InjectAfter = 20;

    const float StartScaleSpread = 0.03f;
    // Roughening (Gordon, Salmond & Smith, IEE Proc. F, 1993): a little jitter after each resample, so copies of one
    // particle spread out again, and after each measurement update (Reweight), so a cloud that has drawn tight on a
    // wrong heading while the phone stands still (no motion noise, no resample) can still slide to the right one.
    // The heading jitter follows the cloud's own heading spread, between these bounds: a tight cloud gets little, as
    // the jitter is a random walk on the heading offset and ARCore's own drifts only a few degrees a minute (with ten
    // updates a second, half a degree each would walk it 12 degrees a minute, and biased scans then pull it along).
    const float RoughenPosition = 0.03f, RoughenHeading = 0.75f, RoughenScale = 0.002f;
    const float UpdateRoughenPosition = 0.02f, MinUpdateRoughen = 0.1f, MaxUpdateRoughen = 2f, RoughenShare = 0.5f;
    const float MaxRegainStep = 5f;
    const float RegainSpread = 1.5f, RegainHeadingSpread = 6f;
    const float JumpSpread = 0.5f, JumpHeadingSpread = 3f;
    const float RecoverSpread = 1f, MaxRecoverSpread = 6f, RecoverHeadingSpread = 8f;
    const int SettleSteps = 8;
    // Wipe-outs are counted over this many metres walked: on a walk started where the filter was told, they don't
    // happen at all; started at the wrong door, they come every few metres.
    const float RecentWalk = 30f;
    const float ResampleBelow = 0.5f;
    // Sightings: bearing noise on top of the particles' own headings, and the least range noise believed.
    const float BearingSigma = 6f;
    const float MinRangeSigma = 0.25f;
    // A sighting no particle can explain is a misread while the filter is this sure, unless it's the second in a
    // row: then the filter is the one that's wrong, and it starts again from the sign.
    const float TrustAbove = 0.5f;
    const float NewFixHeadingSpread = 4f;
    // Line of sight to a sign is checked to a point this far out from its wall (the wall itself isn't walkable).
    static readonly float[] s_StandOffs = { 0.3f, 0.5f, 0.8f };
    // Starting from a sign: positions on the ring at the measured range in front of the plate, within this many
    // degrees of square (plates are read from the hallway, rarely more side-on than this).
    const float RingFan = 75f;
    // Walking blind (steps, no camera): back with tracking, the gap ARCore reports isn't walked again, only loosened.
    const float BlindRegainSpread = 0.4f, BlindRegainHeadingSpread = 3f;
    const float NearEstimate = 1f;
    // The estimate's mode: grid cells (metres), the mean-shift window (metres) and passes.
    const float ModeCell = 1.5f, ModeReach = 3f;
    const int ModeShifts = 3;
    readonly Dictionary<long, float> m_ModeCells = new Dictionary<long, float>();
    const int SeedTries = 12;

    readonly Walkable m_Walkable;
    readonly int m_Capacity;
    int m_Count;
    Vector2[] m_Position, m_SparePosition;
    float[] m_Heading, m_SpareHeading;
    float[] m_Scale, m_SpareScale;
    readonly float[] m_Weight;
    readonly float[] m_Likelihood;
    readonly float[] m_Distance;
    readonly int[] m_Order;
    readonly HashSet<long> m_Bins = new HashSet<long>();
    readonly List<Vector2> m_Cells = new List<Vector2>();
    readonly List<Vector2> m_StandOff = new List<Vector2>();
    readonly List<bool> m_HasStandOff = new List<bool>();
    readonly System.Random m_Random;
    bool m_HasSpareGaussian;
    float m_SpareGaussian;

    Vector2 m_Camera;
    float m_CameraYaw = float.NaN;
    bool m_Lost, m_Blind;
    int m_GoodSteps;
    int m_Contradictions;
    float m_Walked;
    readonly float[] m_RecoveredAt = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
    int m_Updates;

    // The sign hypothesis random particles are drawn round while it's set (Augmented MCL's injection, biased).
    readonly List<Vector2> m_RingLandmarks = new List<Vector2>();
    readonly List<Vector2> m_RingNormals = new List<Vector2>();
    float m_RingBearing, m_RingRange, m_RingSigma;

    /// <summary>The plan's yaw that Begin set: the offsets are on top of it.</summary>
    public float BaseYaw { get; private set; }
    /// <summary>How far, degrees, the heading offsets may go either way: MaxHeadingOffset from a door start, 180 (any
    /// heading) once lost.</summary>
    public float HeadingLimit { get; private set; } = MaxHeadingOffset;
    /// <summary>The camera's world (x, z) at the last motion update: the estimate is where it was then.</summary>
    public Vector2 Camera => m_Camera;
    /// <summary>The user's plan position at the last update; always on walkable floor when there's a walkable map.</summary>
    public Vector2 Estimate { get; private set; }
    public float HeadingOffset { get; private set; }
    public float Scale { get; private set; } = 1f;
    /// <summary>The weighted standard deviation of the particles' scales.</summary>
    public float ScaleSpread { get; private set; }
    /// <summary>The plan's yaw as the filter sees it: BaseYaw + HeadingOffset.</summary>
    public float Yaw => BaseYaw + HeadingOffset;
    /// <summary>How far the particles are spread round their mean, metres (root mean square).</summary>
    public float Spread { get; private set; }
    /// <summary>0 to 1: the share of the particles' weight within a metre of the estimate.</summary>
    public float Confidence { get; private set; }
    /// <summary>The heading offsets' circular standard deviation, degrees.</summary>
    public float HeadingSpread { get; private set; }
    public int Recoveries { get; private set; }

    /// <summary>A sign no particle can explain: whether it starts the cloud again on its rings (null: when the cloud
    /// isn't sure of itself, or on the second such sign in a row).</summary>
    public bool? SeedOnContradiction { get; set; }
    /// <summary>The likelihood a sign leaves a particle that couldn't have seen it (0: none), against 1 for one that
    /// sees it just so.</summary>
    public float PlateOutlier { get; set; }

    /// <summary>The log of the walk's likelihood so far under this cloud: each motion update adds the log of the
    /// weight that didn't walk into a wall (at least MinSurvival). Shadow hypotheses (LocalizerEngine) compare it.</summary>
    public double MotionLog { get; set; }
    const float MinSurvival = 1e-3f;

    /// <summary>The last sign observation's Σ weight × likelihood (0: nothing could have seen it).</summary>
    public float LastSignTotal { get; private set; }

    /// <summary>Whether the last sign observation started the cloud again on the sign's rings.</summary>
    public bool SignSeeded { get; private set; }
    public int RecoveriesInARow { get; private set; }
    /// <summary>Wipe-outs (every particle in a wall) in the last RecentWalk metres walked: two or more, and the walk
    /// doesn't fit the building from where the filter thinks it started.</summary>
    public int RecentRecoveries
    {
        get
        {
            int n = 0;
            foreach (float at in m_RecoveredAt)
            {
                if (m_Walked - at <= RecentWalk)
                    n++;
            }
            return n;
        }
    }
    /// <summary>Particles in use now.</summary>
    public int Count => m_Count;
    /// <summary>The most particles this filter can hold.</summary>
    public int Capacity => m_Capacity;
    /// <summary>KLD-sampling on: the count moves between MinCount and MaxCount with how spread the cloud is.</summary>
    public bool Adaptive { get; set; }
    public int MinCount { get; set; } = 100;
    int m_MaxCount;
    /// <summary>The most particles KLD-sampling may use: lowered when an update takes too long on the phone.</summary>
    public int MaxCount
    {
        get => m_MaxCount;
        set => m_MaxCount = Mathf.Clamp(value, 1, m_Capacity);
    }
    /// <summary>Augmented MCL's slow and fast averages of the measurement likelihood.</summary>
    public float SlowAverage { get; private set; }
    public float FastAverage { get; private set; }
    /// <summary>Random particles may be injected. Off while the filter is sure of itself: in a building of look-alike
    /// hallways a random particle can fit one scan by chance, and a sure filter would be hijacked by it. The
    /// Localizer allows it once lost (where Probabilistic Robotics' Table 8.3 allows it always).</summary>
    public bool AllowInjection { get; set; } = true;
    /// <summary>The share of the next resample to inject at random: 1 - fast / slow, when measurements have been
    /// fitting worse lately than over the long run.</summary>
    public float InjectionShare =>
        !AllowInjection ? 0f
        : Mathf.Max(MinimumInjection, m_Updates >= InjectAfter && SlowAverage > 0f
            ? Mathf.Clamp(1f - FastAverage / SlowAverage, 0f, MaxInjection) : 0f);
    /// <summary>Injected at every resample regardless of the averages (while a sign contradicts the filter, its rings
    /// get a few particles to prove themselves against the scan and the walls).</summary>
    public float MinimumInjection { get; set; }
    /// <summary>A sign hypothesis is set (SetRing): injected particles go round it.</summary>
    public bool HasRing => m_RingLandmarks.Count > 0;
    /// <summary>Particles injected at random so far.</summary>
    public int Injected { get; private set; }

    public PositionFilter(Walkable walkable, int count = DefaultCount, int seed = 1, int capacity = 0)
    {
        m_Walkable = walkable;
        m_Count = Mathf.Max(1, count);
        m_Capacity = Mathf.Max(m_Count, capacity);
        m_MaxCount = m_Capacity;
        m_Position = new Vector2[m_Capacity];
        m_SparePosition = new Vector2[m_Capacity];
        m_Heading = new float[m_Capacity];
        m_SpareHeading = new float[m_Capacity];
        m_Scale = new float[m_Capacity];
        m_SpareScale = new float[m_Capacity];
        m_Weight = new float[m_Capacity];
        m_Likelihood = new float[m_Capacity];
        m_Distance = new float[m_Capacity];
        m_Order = new int[m_Capacity];
        m_Random = new System.Random(seed);
    }

    // The walk starts: the user stands at plan point `at` (on walkable floor, or the nearest that is), and the camera
    // is at world (x, z) `camera`, with the plan turned by baseYaw.
    public void Reset(Vector2 at, Vector2 camera, float baseYaw, float positionSpread = 0.5f, float headingSpread = 10f,
        float headingLimit = MaxHeadingOffset) => Reset(at, camera, baseYaw, positionSpread, headingSpread, headingLimit, false);

    // uniformHeading: offsets spread evenly over ±headingSpread instead of normally (a snap: a door can be 25 degrees
    // off square, and the truth then needs particles near it from the start, not 2.5 deviations out).
    public void Reset(Vector2 at, Vector2 camera, float baseYaw, float positionSpread, float headingSpread,
        float headingLimit, bool uniformHeading)
    {
        BaseYaw = baseYaw;
        HeadingLimit = Mathf.Clamp(headingLimit, 0f, 180f);
        m_Camera = camera;
        m_CameraYaw = float.NaN;
        m_Lost = false;
        m_Blind = false;
        m_Contradictions = 0;
        m_Walked = 0f;
        Recoveries = 0;
        ForgetRecoveries();
        ResetAverages();
        ClearRing();
        Vector2 centre = OnFloor(at, 3f, at);
        for (int i = 0; i < m_Count; i++)
        {
            Seed(i, centre, positionSpread, 0f, uniformHeading ? 0f : headingSpread, 1f, StartScaleSpread);
            if (uniformHeading)
                m_Heading[i] = Bound(((i + (float)m_Random.NextDouble()) / m_Count * 2f - 1f) * headingSpread);
        }
        Uniform();
        UpdateEstimate();
    }

    public void Follow(Vector2 camera, bool tracking) => Follow(camera, float.NaN, tracking);

    // Each frame: where the AR camera is now (world x, z), which way it faces (world yaw, degrees; NaN if not known),
    // and whether ARCore is tracking. A motion update once the camera has moved Step; untracked poses are ignored, and
    // the walk picks up from the next tracked one.
    public void Follow(Vector2 camera, float cameraYaw, bool tracking)
    {
        if (!tracking)
        {
            m_Lost = true;
            return;
        }
        Vector2 move = camera - m_Camera;
        float length = move.magnitude;
        if (m_Lost && m_Blind)
        {
            // Steps moved the cloud while ARCore was lost: its pose meanwhile isn't a walk to take again.
            m_Lost = m_Blind = false;
            Diffuse(BlindRegainSpread, BlindRegainHeadingSpread);
            m_Camera = camera;
            m_CameraYaw = cameraYaw;
            return;
        }
        if (m_Lost)
        {
            m_Lost = false;
            // Walked blind: a plausible gap counts, with double the noise; past that, nothing is known but roughly here.
            if (length > MaxRegainStep)
            {
                Diffuse(RegainSpread, RegainHeadingSpread);
                m_Camera = camera;
                m_CameraYaw = cameraYaw;
            }
            else if (length >= Step)
            {
                // In Step-long pieces: the motion model's noise is for a step, and one long jump would turn every
                // particle's direction by far more than walking it does.
                int pieces = Mathf.CeilToInt(length / Step);
                for (int k = 0; k < pieces; k++)
                    Predict(move / pieces, 2f, float.NaN, float.NaN);
                m_Camera = camera;
                m_CameraYaw = cameraYaw;
            }
            return;
        }
        if (length > JumpDistance)
        {
            // The pose jumped, the user didn't: keep the cloud where it is, a little looser.
            Diffuse(JumpSpread, JumpHeadingSpread);
            m_Camera = camera;
            m_CameraYaw = cameraYaw;
            return;
        }
        if (length >= Step)
        {
            Predict(move, 1f, m_CameraYaw, cameraYaw);
            m_Camera = camera;
            m_CameraYaw = cameraYaw;
        }
    }

    // The user's plan position now, with the camera at world (x, z) `camera`: the estimate moved on by the bit of
    // the walk not yet stepped, unless that would go through a wall.
    public Vector2 Current(Vector2 camera)
    {
        Vector2 at = ToPlanPoint(camera);
        return m_Walkable != null && !m_Walkable.SegmentClear(Estimate, at) ? Estimate : at;
    }

    // A world (x, z) point as a plan point, by the estimate (no wall check).
    public Vector2 ToPlanPoint(Vector2 world)
    {
        return Estimate + ToPlan(world - m_Camera, Yaw) * Scale;
    }

    // Turns every particle's heading: a correction from elsewhere (walking straight along the route).
    public void ShiftHeading(float degrees)
    {
        for (int i = 0; i < m_Count; i++)
            m_Heading[i] = Bound(m_Heading[i] + degrees);
        UpdateEstimate();
    }

    // Particles first..first+count-1 moved to another hypothesis: round at (plan), the plan turned by yaw, spread as
    // a snap is. For a snap that may have been at another door: the walls and the scan then choose.
    public void SeedHypothesis(int first, int count, Vector2 at, float yaw, float positionSpread, float headingSpread)
    {
        Vector2 centre = OnFloor(at, 3f, at);
        for (int i = first; i < first + count && i < m_Count; i++)
        {
            Seed(i, centre, positionSpread, 0f, 0f, 1f, StartScaleSpread);
            m_Heading[i] = Bound(yaw - BaseYaw + ((float)m_Random.NextDouble() * 2f - 1f) * headingSpread);
        }
        UpdateEstimate();
    }

    /// <summary>Particles first..first+count anywhere in sight of at, from to reach metres away (walkable and with
    /// nothing in between), headings evenly within headingSpread of yaw.</summary>
    public void SeedNear(int first, int count, Vector2 at, float yaw, float from, float reach, float headingSpread)
    {
        for (int i = first; i < first + count && i < m_Count; i++)
        {
            Vector2 p = at;
            for (int t = 0; t < SeedTries; t++)
            {
                float r = from + (float)m_Random.NextDouble() * (reach - from);
                Vector2 c = at + HeadingVector((float)m_Random.NextDouble() * 360f) * r;
                if (m_Walkable == null || (m_Walkable.IsWalkable(c) && m_Walkable.SegmentClear(at, c)))
                {
                    p = c;
                    break;
                }
            }
            Seed(i, p, 0f, 0f, 0f, 1f, StartScaleSpread);
            m_Heading[i] = Bound(yaw - BaseYaw + ((float)m_Random.NextDouble() * 2f - 1f) * headingSpread);
        }
        UpdateEstimate();
    }

    /// <summary>Particles to use from the next Reset (a snap starts with a few hundred; KLD-sampling takes it on).</summary>
    public void SetCount(int count) => m_Count = Mathf.Clamp(count, 1, m_Capacity);

    /// <summary>Lost: any heading may be the right one from here on.</summary>
    public void FreeHeading() => HeadingLimit = 180f;

    /// <summary>A fresh start (a snap, or a sign that placed the user): wipe-outs before it don't count.</summary>
    public void ForgetRecoveries()
    {
        for (int i = 0; i < m_RecoveredAt.Length; i++)
            m_RecoveredAt[i] = float.NegativeInfinity;
        RecoveriesInARow = 0;
        m_GoodSteps = 0;
    }

    public void Predict(Vector2 worldMove, float noise = 1f) => Predict(worldMove, noise, float.NaN, float.NaN);

    // One motion update: the camera moved worldMove (world x, z), turning from yawFrom to yawTo (world degrees, NaN
    // if not known). The odometry motion model (Probabilistic Robotics, §5.4, sample_motion_model_odometry): the move
    // as a turn (rot1), a translation and a turn (rot2), each with noise from the α's; noise scales them all.
    public void Predict(Vector2 worldMove, float noise, float yawFrom, float yawTo)
    {
        float trans = worldMove.magnitude;
        if (trans <= 0f)
            return;
        m_Walked += trans;
        float travel = Heading(worldMove) * Mathf.Deg2Rad;
        float rot1 = 0f, rot2 = 0f;
        if (!float.IsNaN(yawFrom) && !float.IsNaN(yawTo))
        {
            rot1 = Mathf.DeltaAngle(yawFrom, travel * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            rot2 = Mathf.DeltaAngle(yawFrom + rot1 * Mathf.Rad2Deg, yawTo) * Mathf.Deg2Rad;
        }
        float rotSigma = Mathf.Sqrt(Alpha1 * rot1 * rot1 + Alpha2 * trans * trans) * noise;
        float transSigma = Mathf.Sqrt(Alpha3 * trans * trans + Alpha4 * (rot1 * rot1 + rot2 * rot2)) * noise;
        float headingSigma = HeadingNoise * Mathf.Sqrt(trans) * noise;
        float scaleSigma = ScaleNoise * Mathf.Sqrt(trans) * noise;
        Vector2 expected = Estimate + ToPlan(worldMove, Yaw) * Scale;

        float alive = 0f, before = TotalWeight();
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= 0f)
                continue;   // dead until the next resample replaces it
            float direction = travel + Gaussian() * rotSigma;
            float length = Mathf.Max(0f, trans + Gaussian() * transSigma);
            var noisy = new Vector2(Mathf.Sin(direction), Mathf.Cos(direction)) * length;
            Vector2 from = m_Position[i];
            Vector2 to = from + ToPlan(noisy, BaseYaw + m_Heading[i]) * m_Scale[i];
            if (m_Walkable != null && !m_Walkable.SegmentClear(from, to))
            {
                m_Weight[i] = 0f;
                continue;
            }
            m_Position[i] = to;
            m_Heading[i] = Bound(m_Heading[i] + Gaussian() * headingSigma);
            m_Scale[i] = Mathf.Clamp(m_Scale[i] + Gaussian() * scaleSigma, MinScale, MaxScale);
            alive += m_Weight[i];
        }

        Survived(alive, before);
        if (alive <= 0f)
        {
            Recover(expected);
            return;
        }
        Normalize(alive);
        if (++m_GoodSteps >= SettleSteps)
            RecoveriesInARow = 0;
        ResampleIfNeeded();
        UpdateEstimate();
    }

    // A step counted while ARCore can't track (the phone lowered or covered): map-constrained pedestrian dead
    // reckoning (Woodman & Harle, UbiComp 2008). Every particle moves planStep (plan metres), give or take
    // lengthSigma of its length and headingSigma degrees of its direction, and walls kill as usual.
    public void StepBlind(Vector2 planStep, float lengthSigma, float headingSigma) =>
        StepBlind(planStep, float.NaN, planStep.magnitude, lengthSigma, headingSigma);

    /// <summary>A step taken blind, its direction known in ARCore's world (degrees clockwise from world +z): each
    /// particle turns it onto the plan by its own heading, so a cloud of several headings walks each its own way.</summary>
    public void StepBlindWorld(float worldHeading, float length, float lengthSigma, float headingSigma) =>
        StepBlind(Vector2.zero, worldHeading, length, lengthSigma, headingSigma);

    void StepBlind(Vector2 planStep, float worldHeading, float length, float lengthSigma, float headingSigma)
    {
        if (length <= 0f)
            return;
        bool world = !float.IsNaN(worldHeading);
        if (world)
            planStep = HeadingVector(worldHeading - Yaw) * length;
        m_Blind = true;
        m_Walked += length;
        Vector2 direction = planStep / length;
        float alive = 0f, before = TotalWeight();
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= 0f)
                continue;
            float turn = Gaussian() * headingSigma * Mathf.Deg2Rad;
            if (world)
            {
                direction = HeadingVector(worldHeading - (BaseYaw + m_Heading[i]));
            }
            float c = Mathf.Cos(turn), s = Mathf.Sin(turn);
            var turned = new Vector2(direction.x * c + direction.y * s, -direction.x * s + direction.y * c);
            Vector2 from = m_Position[i];
            Vector2 to = from + turned * Mathf.Max(0.1f, length + Gaussian() * lengthSigma);
            if (m_Walkable != null && !m_Walkable.SegmentClear(from, to))
            {
                m_Weight[i] = 0f;
                continue;
            }
            m_Position[i] = to;
            alive += m_Weight[i];
        }
        Survived(alive, before);
        if (alive <= 0f)
        {
            Recover(Estimate + planStep);
            return;
        }
        Normalize(alive);
        ResampleIfNeeded();
        UpdateEstimate();
    }

    // Loosens the cloud where it stands (after a pose jump, or tracking coming back from far away). A particle
    // that would land in a wall stays put: nobody walked, so nothing is learnt from it.
    public void Diffuse(float positionSpread, float headingSpread)
    {
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= 0f)
                continue;
            Vector2 to = m_Position[i] + Gaussian2() * positionSpread;
            if (m_Walkable == null || m_Walkable.IsWalkable(to))
                m_Position[i] = to;
            m_Heading[i] = Bound(m_Heading[i] + Gaussian() * headingSpread);
        }
        UpdateEstimate();
    }

    // Spreads the cloud evenly over the walkable floor within radius of centre (the user asked to find themselves
    // again): any heading, or the present one give or take a little. Drawn from the walkable cells themselves, not by
    // trying random spots: in a narrow hallway most of a disc is wall, and particles that failed to land would pile up
    // on one spot and look like confidence.
    public void Scatter(Vector2 centre, float radius, bool anyHeading)
    {
        if (anyHeading)
            FreeHeading();
        if (Adaptive)
            m_Count = m_MaxCount;
        m_Cells.Clear();
        if (m_Walkable != null)
        {
            float cell = m_Walkable.cell;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((centre.x - radius - m_Walkable.left) / cell));
            int i1 = Mathf.Min(m_Walkable.width - 1, Mathf.FloorToInt((centre.x + radius - m_Walkable.left) / cell));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((m_Walkable.top - centre.y - radius) / cell));
            int j1 = Mathf.Min(m_Walkable.height - 1, Mathf.FloorToInt((m_Walkable.top - centre.y + radius) / cell));
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    var c = new Vector2(m_Walkable.left + (i + 0.5f) * cell, m_Walkable.top - (j + 0.5f) * cell);
                    if ((c - centre).sqrMagnitude <= radius * radius && m_Walkable.IsWalkable(c))
                        m_Cells.Add(c);
                }
            }
        }
        for (int i = 0; i < m_Count; i++)
        {
            Vector2 at;
            if (m_Cells.Count > 0)
            {
                // A random cell, and a random spot in it.
                float cell = m_Walkable.cell;
                at = m_Cells[m_Random.Next(m_Cells.Count)] +
                     new Vector2((float)m_Random.NextDouble() - 0.5f, (float)m_Random.NextDouble() - 0.5f) * cell;
            }
            else
            {
                float angle = (float)m_Random.NextDouble() * 2f * Mathf.PI;
                float r = radius * Mathf.Sqrt((float)m_Random.NextDouble());
                at = centre + new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * r;
            }
            m_Position[i] = at;
            m_Heading[i] = anyHeading ? RandomHeading() : Bound(HeadingOffset + Gaussian() * RecoverHeadingSpread);
            m_Scale[i] = Mathf.Clamp(1f + Gaussian() * StartScaleSpread, MinScale, MaxScale);
        }
        Uniform();
        ForgetRecoveries();
        ResetAverages();
        UpdateEstimate();
    }

    // Another signal's verdict: each particle's log-likelihood. Weights are scaled by e^(ll - max), the span held to
    // maxSpan so one update can't wipe out all but the best few. False if nothing was alive to weigh.
    public bool Reweight(float[] logLikelihood, float maxSpan)
    {
        float best = float.NegativeInfinity;
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] > 0f && logLikelihood[i] > best)
                best = logLikelihood[i];
        }
        if (float.IsNegativeInfinity(best) || float.IsNaN(best))
            return false;
        float total = 0f;
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= 0f)
                continue;
            m_Weight[i] *= Mathf.Exp(Mathf.Max(logLikelihood[i] - best, -maxSpan));
            total += m_Weight[i];
        }
        Normalize(total);
        ResampleIfNeeded();
        Roughen();
        UpdateEstimate();
        return true;
    }

    // After a measurement: jitter in proportion to the cloud's heading spread (see the constants), never collapsing
    // it to nothing.
    void Roughen()
    {
        float heading = Mathf.Clamp(HeadingSpread * RoughenShare, MinUpdateRoughen, MaxUpdateRoughen);
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= 0f)
                continue;
            Vector2 to = m_Position[i] + Gaussian2() * UpdateRoughenPosition;
            if (m_Walkable == null || m_Walkable.IsWalkable(to))
                m_Position[i] = to;
            m_Heading[i] = Bound(m_Heading[i] + Gaussian() * heading);
        }
    }

    // Augmented MCL's bookkeeping (Probabilistic Robotics, Table 8.3): the weighted mean likelihood of the latest
    // measurement, on a scale that's comparable from one update to the next (the scan's geometric mean per point).
    public void ReportLikelihood(float average)
    {
        if (!(average > 0f))
            return;
        if (m_Updates == 0)
        {
            SlowAverage = FastAverage = average;
        }
        else
        {
            SlowAverage += AlphaSlow * (average - SlowAverage);
            FastAverage += AlphaFast * (average - FastAverage);
        }
        m_Updates++;
        // Measurements fitting worse than usual: resample now, so the injection happens.
        if (InjectionShare > 0.05f)
        {
            Resample();
            UpdateEstimate();
        }
    }

    void ResetAverages()
    {
        m_Updates = 0;
        SlowAverage = FastAverage = 0f;
    }

    /// <summary>Random particles are drawn round this sign hypothesis (rather than anywhere on the floor) until
    /// ClearRing: a plate was just read.</summary>
    public void SetRing(float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
        IReadOnlyList<Vector2> normals)
    {
        m_RingLandmarks.Clear();
        m_RingNormals.Clear();
        for (int k = 0; k < landmarks.Count; k++)
        {
            m_RingLandmarks.Add(landmarks[k]);
            m_RingNormals.Add(normals != null && k < normals.Count ? normals[k] : Vector2.zero);
        }
        m_RingBearing = worldBearing;
        m_RingRange = range;
        m_RingSigma = Mathf.Max(rangeSigma, MinRangeSigma);
    }

    public void ClearRing() => m_RingLandmarks.Clear();

    public Vector2 ParticlePosition(int i) => m_Position[i];
    /// <summary>The plan's yaw by this particle: BaseYaw + its heading offset.</summary>
    public float ParticleYaw(int i) => BaseYaw + m_Heading[i];
    public float ParticleScale(int i) => m_Scale[i];
    public float ParticleWeight(int i) => m_Weight[i];

    /// <summary>The radius round the estimate holding this share of the weight, metres: 0.68 is what the map's halo
    /// draws. A cloud strung out along a hallway reads as wide, as it should.</summary>
    public float Radius(float share)
    {
        int n = 0;
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= 0f)
                continue;
            m_Distance[n] = (m_Position[i] - Estimate).sqrMagnitude;
            m_Order[n] = i;
            n++;
        }
        if (n == 0)
            return 0f;
        Array.Sort(m_Distance, m_Order, 0, n);
        float total = 0f;
        for (int k = 0; k < n; k++)
            total += m_Weight[m_Order[k]];
        float sum = 0f, target = share * total;
        for (int k = 0; k < n; k++)
        {
            sum += m_Weight[m_Order[k]];
            if (sum >= target)
                return Mathf.Sqrt(m_Distance[k]);
        }
        return Mathf.Sqrt(m_Distance[n - 1]);
    }

    // A room-number sign seen at worldBearing (degrees clockwise from world +z) and range metres from the camera's
    // position at the last motion update. Any of landmarks may be the sign (the same number can hang at two doors),
    // each with the unit normal out of its wall (zero if not known). False if the sighting wasn't used.
    public bool AddSignObservation(float worldBearing, float range, float rangeSigma, Vector2 landmark, Vector2 normal)
    {
        return AddSignObservation(worldBearing, range, rangeSigma, new[] { landmark }, new[] { normal });
    }

    public bool AddSignObservation(float worldBearing, float range, float rangeSigma,
        IReadOnlyList<Vector2> landmarks, IReadOnlyList<Vector2> normals)
    {
        SignSeeded = false;
        if (landmarks == null || landmarks.Count == 0 || !(range > 0f))
            return false;
        rangeSigma = Mathf.Max(rangeSigma, MinRangeSigma);
        PrepareStandOffs(landmarks, normals);

        float total = WeighSign(worldBearing, range, rangeSigma, landmarks, normals);
        LastSignTotal = total;
        if (total <= 1e-12f)
        {
            // Nothing here could have seen it: a misread while the filter is sure, else the filter is wrong.
            bool trusted = SeedOnContradiction.HasValue ? !SeedOnContradiction.Value
                : Confidence >= TrustAbove && m_Contradictions == 0;
            m_Contradictions++;
            if (trusted)
                return false;
            SeedRing(worldBearing, range, rangeSigma, landmarks, normals);
            SignSeeded = true;
            PrepareStandOffs(landmarks, normals);
            total = WeighSign(worldBearing, range, rangeSigma, landmarks, normals);
            if (total <= 1e-12f)
                return false;
        }
        m_Contradictions = 0;
        if (PlateOutlier > 0f)
        {
            // Robust: the plate may be a misread or a poster (Probabilistic Robotics §6.3's z_rand, for plates), so a
            // part of the cloud that couldn't have seen it is weighed down, not out.
            float sum = 0f;
            for (int i = 0; i < m_Count; i++)
            {
                m_Weight[i] *= PlateOutlier + m_Likelihood[i];
                sum += m_Weight[i];
            }
            Normalize(sum);
        }
        else
        {
            for (int i = 0; i < m_Count; i++)
                m_Weight[i] *= m_Likelihood[i] / total;
        }
        ResampleIfNeeded();
        UpdateEstimate();
        return true;
    }

    float WeighSign(float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
        IReadOnlyList<Vector2> normals)
    {
        float total = 0f;
        for (int i = 0; i < m_Count; i++)
        {
            m_Likelihood[i] = m_Weight[i] > 0f ? SignLikelihood(i, worldBearing, range, rangeSigma, landmarks, normals) : 0f;
            total += m_Weight[i] * m_Likelihood[i];
        }
        return total;
    }

    float SignLikelihood(int i, float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
        IReadOnlyList<Vector2> normals)
    {
        Vector2 p = m_Position[i];
        float observed = worldBearing - (BaseYaw + m_Heading[i]);   // the sign's bearing in the plan, for this particle
        float best = 0f;
        for (int k = 0; k < landmarks.Count; k++)
        {
            Vector2 toSign = landmarks[k] - p;
            Vector2 normal = normals != null && k < normals.Count ? normals[k] : Vector2.zero;
            if (normal.sqrMagnitude > 0.01f && Vector2.Dot(toSign, normal) >= 0f)
                continue;   // behind its wall
            float dr = (toSign.magnitude - range) / rangeSigma;
            float db = Mathf.DeltaAngle(observed, Heading(toSign)) / BearingSigma;
            float l = Mathf.Exp(-0.5f * (dr * dr + db * db));
            if (l < 1e-6f || l <= best)
                continue;
            if (m_Walkable != null && m_HasStandOff[k] && !m_Walkable.SegmentClear(p, m_StandOff[k]))
                continue;   // a wall in between
            best = l;
        }
        return best;
    }

    void PrepareStandOffs(IReadOnlyList<Vector2> landmarks, IReadOnlyList<Vector2> normals)
    {
        m_StandOff.Clear();
        m_HasStandOff.Clear();
        for (int k = 0; k < landmarks.Count; k++)
        {
            Vector2 normal = normals != null && k < normals.Count ? normals[k] : Vector2.zero;
            Vector2 point = landmarks[k];
            bool found = false;
            if (m_Walkable != null && normal.sqrMagnitude > 0.01f)
            {
                foreach (float d in s_StandOffs)
                {
                    Vector2 candidate = landmarks[k] + normal.normalized * d;
                    if (m_Walkable.IsWalkable(candidate))
                    {
                        point = candidate;
                        found = true;
                        break;
                    }
                }
            }
            m_StandOff.Add(point);
            m_HasStandOff.Add(found);
        }
    }

    // Starts the cloud again from a sighting alone, whatever the filter thought: for every sign with that text, on
    // the ring at the measured range in front of its wall, each particle facing so that it sees the sign along the
    // ray's bearing. Any heading: the filter is lost, or the sighting says it is. Walls, the scan and further signs
    // then pick the right part of the rings out.
    public void SeedRing(float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
        IReadOnlyList<Vector2> normals)
    {
        if (landmarks == null || landmarks.Count == 0)
            return;
        FreeHeading();
        if (Adaptive)
            m_Count = m_MaxCount;
        PrepareStandOffs(landmarks, normals);
        rangeSigma = Mathf.Max(rangeSigma, MinRangeSigma);
        for (int i = 0; i < m_Count; i++)
            RingParticle(i, i % landmarks.Count, worldBearing, range, rangeSigma, landmarks, normals);
        Uniform();
        ForgetRecoveries();
        ResetAverages();
        UpdateEstimate();
    }

    float TotalWeight()
    {
        float total = 0f;
        for (int i = 0; i < m_Count; i++)
            total += m_Weight[i];
        return total;
    }

    void Survived(float alive, float before)
    {
        if (before > 0f)
            MotionLog += Math.Log(Mathf.Max(alive / before, MinSurvival));
    }

    /// <summary>This cloud becomes a copy of other's (thinned by low-variance resampling if other has more particles
    /// than this one holds): pose, weights, base yaw and camera. For swapping a shadow hypothesis in.</summary>
    public void CopyFrom(PositionFilter other)
    {
        BaseYaw = other.BaseYaw;
        HeadingLimit = other.HeadingLimit;
        m_Camera = other.m_Camera;
        m_CameraYaw = other.m_CameraYaw;
        m_Lost = other.m_Lost;
        m_Blind = other.m_Blind;
        MotionLog = other.MotionLog;
        int n = Mathf.Min(other.m_Count, m_Capacity);
        float total = other.TotalWeight();
        float step = total / n, target = (float)m_Random.NextDouble() * step, cumulative = other.m_Weight[0];
        int j = 0;
        for (int i = 0; i < n; i++, target += step)
        {
            if (n == other.m_Count)
            {
                j = i;
            }
            else
            {
                while (target > cumulative && j < other.m_Count - 1)
                    cumulative += other.m_Weight[++j];
            }
            m_Position[i] = other.m_Position[j];
            m_Heading[i] = other.m_Heading[j];
            m_Scale[i] = other.m_Scale[j];
            m_Weight[i] = n == other.m_Count ? other.m_Weight[j] : 1f;
        }
        m_Count = n;
        Normalize(TotalWeight());
        ForgetRecoveries();
        ClearRing();
        UpdateEstimate();
    }

    /// <summary>share of the cloud goes onto the sign's rings, the rest stays where it was, all weighed alike: for a
    /// sign that contradicts a cloud nobody trusts (Lost) but that may itself be a misread. The scans that follow say
    /// which part was right.</summary>
    public void SplitRing(float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
        IReadOnlyList<Vector2> normals, float share)
    {
        if (landmarks == null || landmarks.Count == 0)
            return;
        FreeHeading();
        Resample();   // equal weights first, so a share of the particles is a share of the weight
        PrepareStandOffs(landmarks, normals);
        rangeSigma = Mathf.Max(rangeSigma, MinRangeSigma);
        for (int i = 0; i < m_Count; i++)
            if (m_Random.NextDouble() < share)
                RingParticle(i, i % landmarks.Count, worldBearing, range, rangeSigma, landmarks, normals);
        Uniform();
        UpdateEstimate();
    }

    // Particle i on landmark k's ring (PrepareStandOffs first). Keeps it where it was if no spot fits.
    void RingParticle(int i, int k, float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
        IReadOnlyList<Vector2> normals)
    {
        Vector2 normal = normals != null && k < normals.Count ? normals[k] : Vector2.zero;
        bool faced = normal.sqrMagnitude > 0.01f;
        float facing = faced ? Heading(normal) : 0f;
        float fan = faced ? RingFan : 180f;
        for (int t = 0; t < SeedTries * 2; t++)
        {
            float outward = facing + ((float)m_Random.NextDouble() * 2f - 1f) * fan;
            float r = Mathf.Max(0.3f, range + Gaussian() * rangeSigma);
            Vector2 c = landmarks[k] + HeadingVector(outward) * r;
            if (m_Walkable != null && (!m_Walkable.IsWalkable(c) ||
                                       m_HasStandOff[k] && !m_Walkable.SegmentClear(c, m_StandOff[k])))
                continue;
            m_Position[i] = c;
            // The plan's yaw that makes the ray's world bearing point from here at the sign.
            m_Heading[i] = Bound(worldBearing - Heading(landmarks[k] - c) - BaseYaw + Gaussian() * NewFixHeadingSpread);
            m_Scale[i] = Mathf.Clamp(1f + Gaussian() * StartScaleSpread, MinScale, MaxScale);
            return;
        }
    }

    // Every particle hit a wall: start again round where the estimate would have gone.
    void Recover(Vector2 centre)
    {
        m_RecoveredAt[Recoveries % m_RecoveredAt.Length] = m_Walked;
        Recoveries++;
        RecoveriesInARow++;
        m_GoodSteps = 0;
        float spread = Mathf.Min(RecoverSpread * (1 << Mathf.Min(RecoveriesInARow - 1, 3)), MaxRecoverSpread);
        centre = OnFloor(centre, spread * 2f, Estimate);
        for (int i = 0; i < m_Count; i++)
            Seed(i, centre, spread, HeadingOffset, RecoverHeadingSpread, Scale, StartScaleSpread);
        Uniform();
        UpdateEstimate();
    }

    void Seed(int i, Vector2 centre, float spread, float heading, float headingSpread, float scale, float scaleSpread)
    {
        Vector2 at = centre;
        for (int t = 0; t < SeedTries; t++)
        {
            Vector2 c = centre + Gaussian2() * spread;
            if (m_Walkable == null || m_Walkable.IsWalkable(c))
            {
                at = c;
                break;
            }
        }
        m_Position[i] = at;
        m_Heading[i] = Bound(heading + Gaussian() * headingSpread);
        m_Scale[i] = Mathf.Clamp(scale + Gaussian() * scaleSpread, MinScale, MaxScale);
    }

    // A random particle for Augmented MCL: round the last sign read if there is one, else anywhere on the floor.
    void Inject(int i)
    {
        Injected++;
        if (m_RingLandmarks.Count > 0)
        {
            PrepareStandOffs(m_RingLandmarks, m_RingNormals);
            RingParticle(i, m_Random.Next(m_RingLandmarks.Count), m_RingBearing, m_RingRange, m_RingSigma,
                m_RingLandmarks, m_RingNormals);
            return;
        }
        if (m_Walkable == null)
            return;
        for (int t = 0; t < SeedTries * 4; t++)
        {
            var c = new Vector2(m_Walkable.left + (float)m_Random.NextDouble() * m_Walkable.width * m_Walkable.cell,
                m_Walkable.top - (float)m_Random.NextDouble() * m_Walkable.height * m_Walkable.cell);
            if (!m_Walkable.IsWalkable(c))
                continue;
            m_Position[i] = c;
            m_Heading[i] = RandomHeading();
            m_Scale[i] = Mathf.Clamp(1f + Gaussian() * StartScaleSpread, MinScale, MaxScale);
            return;
        }
    }

    float RandomHeading()
    {
        float limit = HeadingLimit >= 180f ? 180f : HeadingLimit;
        return Bound(((float)m_Random.NextDouble() * 2f - 1f) * limit);
    }

    // p if it's walkable (or there's no map), else the nearest walkable spot within reach, else fallback.
    Vector2 OnFloor(Vector2 p, float reach, Vector2 fallback)
    {
        if (m_Walkable == null || m_Walkable.IsWalkable(p))
            return p;
        return m_Walkable.Nearest(p, reach, out Vector2 nearest) ? nearest : fallback;
    }

    // An offset wrapped to (-180, 180] and kept within HeadingLimit.
    float Bound(float heading)
    {
        heading = Mathf.DeltaAngle(0f, heading);
        return HeadingLimit >= 180f ? heading : Mathf.Clamp(heading, -HeadingLimit, HeadingLimit);
    }

    void Uniform()
    {
        float w = 1f / m_Count;
        for (int i = 0; i < m_Count; i++)
            m_Weight[i] = w;
    }

    void Normalize(float total)
    {
        for (int i = 0; i < m_Count; i++)
            m_Weight[i] /= total;
    }

    void ResampleIfNeeded()
    {
        float sum = 0f;
        for (int i = 0; i < m_Count; i++)
            sum += m_Weight[i] * m_Weight[i];
        if (sum > 0f && 1f / sum < ResampleBelow * m_Count)
            Resample();
    }

    // The low-variance sampler (Probabilistic Robotics, Table 4.4): one random number, then evenly spaced picks along
    // the cumulative weights. With Adaptive on, the new count is KLD-sampling's bound for the bins the weighted cloud
    // covers; Augmented MCL then swaps InjectionShare of the picks for random particles.
    void Resample()
    {
        int n = Adaptive ? KldCount() : m_Count;
        float total = 0f;
        for (int i = 0; i < m_Count; i++)
            total += m_Weight[i];
        if (!(total > 0f))
            return;
        float step = total / n;
        float target = (float)m_Random.NextDouble() * step;
        float cumulative = m_Weight[0];
        int j = 0, lastAlive = m_Weight[0] > 0f ? 0 : -1;
        for (int i = 0; i < n; i++, target += step)
        {
            while (target > cumulative && j < m_Count - 1)
            {
                j++;
                cumulative += m_Weight[j];
                if (m_Weight[j] > 0f)
                    lastAlive = j;
            }
            int pick = m_Weight[j] > 0f || lastAlive < 0 ? j : lastAlive;   // rounding at the end can land on a dead one
            m_SparePosition[i] = m_Position[pick];
            m_SpareHeading[i] = m_Heading[pick];
            m_SpareScale[i] = m_Scale[pick];
        }
        (m_Position, m_SparePosition) = (m_SparePosition, m_Position);
        (m_Heading, m_SpareHeading) = (m_SpareHeading, m_Heading);
        (m_Scale, m_SpareScale) = (m_SpareScale, m_Scale);
        m_Count = n;

        float inject = InjectionShare;
        float roughHeading = Mathf.Clamp(HeadingSpread * RoughenShare, MinUpdateRoughen, RoughenHeading);
        for (int i = 0; i < m_Count; i++)
        {
            if (inject > 0f && m_Random.NextDouble() < inject)
            {
                Inject(i);
                continue;
            }
            Vector2 to = m_Position[i] + Gaussian2() * RoughenPosition;
            if (m_Walkable == null || m_Walkable.IsWalkable(to))
                m_Position[i] = to;
            m_Heading[i] = Bound(m_Heading[i] + Gaussian() * roughHeading);
            m_Scale[i] = Mathf.Clamp(m_Scale[i] + Gaussian() * RoughenScale, MinScale, MaxScale);
        }
        Uniform();
    }

    // KLD-sampling (Fox, IJRR 2003): enough particles that the sample-based estimate is within ε (in K-L divergence)
    // of the true posterior with probability 1 - δ, given the k bins the weighted cloud occupies.
    int KldCount()
    {
        m_Bins.Clear();
        float floor = 0.2f / m_Count;
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= floor)
                continue;
            long x = Mathf.FloorToInt(m_Position[i].x / BinSize) & 0xFFFFF;
            long z = Mathf.FloorToInt(m_Position[i].y / BinSize) & 0xFFFFF;
            long h = Mathf.FloorToInt((m_Heading[i] + 180f) / BinDegrees) & 0xFF;
            m_Bins.Add((x << 28) | (z << 8) | h);
        }
        int k = m_Bins.Count;
        int n = MinCount;
        if (k > 1)
        {
            float a = 2f / (9f * (k - 1));
            float b = 1f - a + Mathf.Sqrt(a) * KldQuantile;
            n = Mathf.CeilToInt((k - 1) / (2f * KldEpsilon) * b * b * b);
        }
        return Mathf.Clamp(n, Mathf.Min(MinCount, m_MaxCount), m_MaxCount);
    }

    void UpdateEstimate()
    {
        float total = 0f, scale = 0f, scale2 = 0f, sin = 0f, cos = 0f;
        Vector2 mean = Vector2.zero;
        for (int i = 0; i < m_Count; i++)
        {
            float w = m_Weight[i];
            if (w <= 0f)
                continue;
            total += w;
            mean += m_Position[i] * w;
            // Round the circle, so headings either side of 180 average to 180, not 0.
            float r = m_Heading[i] * Mathf.Deg2Rad;
            sin += Mathf.Sin(r) * w;
            cos += Mathf.Cos(r) * w;
            scale += m_Scale[i] * w;
            scale2 += m_Scale[i] * m_Scale[i] * w;
        }
        if (total <= 0f)
            return;
        mean /= total;
        // The estimate is the heaviest mode's mean, not the whole cloud's: a cloud split between two doors (a snap's
        // alternatives, a sign's rings) has its mean between them, where the user surely isn't.
        Vector2 mode = Mode(mean, out float modeSin, out float modeCos);
        HeadingOffset = Mathf.Atan2(modeSin, modeCos) * Mathf.Rad2Deg;
        // Circular standard deviation from the mean resultant length R: sqrt(-2 ln R).
        float resultant = Mathf.Clamp(Mathf.Sqrt(sin * sin + cos * cos) / total, 1e-6f, 1f);
        HeadingSpread = Mathf.Sqrt(-2f * Mathf.Log(resultant)) * Mathf.Rad2Deg;
        Scale = scale / total;
        ScaleSpread = Mathf.Sqrt(Mathf.Max(0f, scale2 / total - Scale * Scale));

        // The mean of a cloud split by a wall can sit in the wall: then the particle nearest it stands in.
        Vector2 estimate = mode;
        if (m_Walkable != null && !m_Walkable.IsWalkable(mode))
        {
            float best = float.PositiveInfinity;
            for (int i = 0; i < m_Count; i++)
            {
                float d = (m_Position[i] - mode).sqrMagnitude;
                if (m_Weight[i] > 0f && d < best)
                {
                    best = d;
                    estimate = m_Position[i];
                }
            }
        }
        Estimate = estimate;

        float spread = 0f, near = 0f;
        for (int i = 0; i < m_Count; i++)
        {
            float w = m_Weight[i];
            if (w <= 0f)
                continue;
            spread += w * (m_Position[i] - mean).sqrMagnitude;
            if ((m_Position[i] - estimate).sqrMagnitude < NearEstimate * NearEstimate)
                near += w;
        }
        Spread = Mathf.Sqrt(spread / total);
        Confidence = near / total;
    }

    // The heaviest mode: the ModeCell grid's heaviest 3 x 3 block, then mean shift (a flat ModeReach window) from its
    // weighted mean. Its weighted heading sums come back for the heading estimate.
    Vector2 Mode(Vector2 mean, out float sin, out float cos)
    {
        m_ModeCells.Clear();
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= 0f)
                continue;
            long key = CellKey(m_Position[i]);
            m_ModeCells.TryGetValue(key, out float w);
            m_ModeCells[key] = w + m_Weight[i];
        }
        long bestKey = CellKey(mean);
        float bestWeight = -1f;
        foreach (var cell in m_ModeCells)
        {
            int cx = (int)(cell.Key >> 32), cy = (int)(cell.Key & 0xffffffffL);
            float w = 0f;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (m_ModeCells.TryGetValue(CellKey(cx + dx, cy + dy), out float c))
                        w += c;
            if (w > bestWeight)
            {
                bestWeight = w;
                bestKey = cell.Key;
            }
        }
        var centre = new Vector2(((int)(bestKey >> 32) + 0.5f) * ModeCell, ((int)(bestKey & 0xffffffffL) + 0.5f) * ModeCell);
        sin = cos = 0f;
        for (int pass = 0; pass < ModeShifts; pass++)
        {
            Vector2 sum = Vector2.zero;
            float total = 0f;
            sin = cos = 0f;
            for (int i = 0; i < m_Count; i++)
            {
                float w = m_Weight[i];
                if (w <= 0f || (m_Position[i] - centre).sqrMagnitude > ModeReach * ModeReach)
                    continue;
                sum += m_Position[i] * w;
                total += w;
                float r = m_Heading[i] * Mathf.Deg2Rad;
                sin += Mathf.Sin(r) * w;
                cos += Mathf.Cos(r) * w;
            }
            if (total <= 0f)
                break;
            centre = sum / total;
        }
        return centre;
    }

    static long CellKey(Vector2 p) => CellKey(Mathf.FloorToInt(p.x / ModeCell), Mathf.FloorToInt(p.y / ModeCell));
    static long CellKey(int x, int y) => ((long)x << 32) | (uint)y;

    float Gaussian()
    {
        if (m_HasSpareGaussian)
        {
            m_HasSpareGaussian = false;
            return m_SpareGaussian;
        }
        // Box-Muller: two at a time.
        double u = 1.0 - m_Random.NextDouble(), v = m_Random.NextDouble();
        double r = Math.Sqrt(-2.0 * Math.Log(u));
        m_SpareGaussian = (float)(r * Math.Sin(2.0 * Math.PI * v));
        m_HasSpareGaussian = true;
        return (float)(r * Math.Cos(2.0 * Math.PI * v));
    }

    Vector2 Gaussian2() => new Vector2(Gaussian(), Gaussian());

    /// <summary>A world (x, z) direction as a plan direction, with the plan turned by yaw degrees (Unity yaw).</summary>
    public static Vector2 ToPlan(Vector2 world, float yaw)
    {
        float r = -yaw * Mathf.Deg2Rad;
        float c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(world.x * c + world.y * s, -world.x * s + world.y * c);
    }

    /// <summary>Degrees clockwise from +z (plan up, or world forward), like a compass.</summary>
    public static float Heading(Vector2 direction) => Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;

    public static Vector2 HeadingVector(float heading)
    {
        float r = heading * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
    }
}
