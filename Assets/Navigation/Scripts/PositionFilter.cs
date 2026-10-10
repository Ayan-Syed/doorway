using System;
using System.Collections.Generic;
using UnityEngine;

// Where the user is on the floor plan, from how the AR camera moves and where the walls are: a particle filter.
// Each particle is a guess at three things: the user's plan position; how far the AR world is turned from the plan
// beyond the start alignment (a heading offset, degrees); and how many plan metres one AR metre is (the drawing isn't
// exactly to scale, and neither is ARCore). Every step the camera takes moves each particle its own way, and a step
// that crosses a wall kills the particle. So a wrong heading dies out within a few metres of hallway, a wrong scale
// at the next corner, and what's left agrees with the building. Room-number signs read by the camera weigh the
// particles by how well each one explains the sighting. Plain C#, so the tests can walk it without Unity running.
//
// Plan and world: a world direction (x, z) turns into a plan direction by the plan's yaw, BaseYaw + the heading
// offset: the Unity yaw (clockwise from above) Navigator gives the plan root.
public sealed class PositionFilter
{
    public const int DefaultCount = 300;
    /// <summary>Camera travel gathered into one step, metres. The noise is per metre walked, so steps of one length
    /// keep the frame rate out of it, and the walls are checked a few times a second, not every frame.</summary>
    public const float Step = 0.25f;
    /// <summary>Position noise: metres per metre walked, one standard deviation on each axis.</summary>
    public const float PositionNoise = 0.05f;
    /// <summary>Heading offset noise: degrees per square root of a metre walked (ARCore's yaw drifts slowly).</summary>
    public const float HeadingNoise = 0.4f;
    /// <summary>Scale noise: per square root of a metre walked.</summary>
    public const float ScaleNoise = 0.004f;
    /// <summary>Standing with your back to a door is rarely more than 15 degrees off square, and ARCore drifts a few
    /// degrees a building: more than this is a wrong match, not a correction.</summary>
    public const float MaxHeadingOffset = 30f;
    public const float MinScale = 0.9f, MaxScale = 1.1f;
    /// <summary>The camera moving further than this in one frame while tracking is ARCore relocalizing, not a walk.</summary>
    public const float JumpDistance = 1f;

    // How unsure the start is: the scale, before any corner has said otherwise.
    const float StartScaleSpread = 0.03f;
    // Back from lost tracking: a gap up to this is taken as walked (with double noise); more, and the cloud spreads.
    const float MaxRegainStep = 5f;
    const float RegainSpread = 1.5f, RegainHeadingSpread = 6f;
    const float JumpSpread = 0.5f, JumpHeadingSpread = 3f;
    // Every particle hit a wall: start again round the last estimate, wider each time in a row.
    const float RecoverSpread = 1f, MaxRecoverSpread = 6f, RecoverHeadingSpread = 8f;
    // Steps without a wipe-out before the wipe-outs in a row count from zero again.
    const int SettleSteps = 8;
    // Wipe-outs are counted over this many metres walked: on a walk started where the filter was told, they don't
    // happen at all; started at the wrong door, they come every few metres.
    const float RecentWalk = 15f;
    // Resample when the effective number of particles falls under this share of them.
    const float ResampleBelow = 0.5f;
    // Sightings: bearing noise on top of the particles' own headings, and the least range noise believed.
    const float BearingSigma = 6f;
    const float MinRangeSigma = 0.25f;
    // A sighting no particle can explain is put down to a misread while the filter is this sure, unless it's the
    // second such sighting in a row: then the filter is the one that's wrong, and it starts again from the sign.
    const float TrustAbove = 0.5f;
    const float NewFixSpread = 0.3f, NewFixHeadingSpread = 4f;
    // Line of sight to a sign is checked to a point this far out from its wall (the wall itself isn't walkable).
    static readonly float[] s_StandOffs = { 0.3f, 0.5f, 0.8f };
    // Confidence: the share of the weight within this of the estimate.
    const float NearEstimate = 1f;
    const int SeedTries = 12;

    readonly Walkable m_Walkable;
    readonly int m_Count;
    Vector2[] m_Position, m_SparePosition;
    float[] m_Heading, m_SpareHeading;
    float[] m_Scale, m_SpareScale;
    readonly float[] m_Weight;
    readonly float[] m_Likelihood;
    readonly List<Vector2> m_StandOff = new List<Vector2>();
    readonly List<bool> m_HasStandOff = new List<bool>();
    readonly System.Random m_Random;
    bool m_HasSpareGaussian;
    float m_SpareGaussian;

    Vector2 m_Camera;
    bool m_Lost;
    int m_GoodSteps;
    int m_Contradictions;
    float m_Walked;
    readonly float[] m_RecoveredAt = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };

    /// <summary>The plan's yaw that Begin set: the offsets are on top of it.</summary>
    public float BaseYaw { get; private set; }
    /// <summary>The camera's world (x, z) at the last step: the estimate is where it was then.</summary>
    public Vector2 Camera => m_Camera;
    /// <summary>The user's plan position at the last step; always on walkable floor when there's a walkable map.</summary>
    public Vector2 Estimate { get; private set; }
    public float HeadingOffset { get; private set; }
    public float Scale { get; private set; } = 1f;
    /// <summary>The plan's yaw as the filter sees it: BaseYaw + HeadingOffset.</summary>
    public float Yaw => BaseYaw + HeadingOffset;
    /// <summary>How far the particles are spread round the estimate, metres (root mean square).</summary>
    public float Spread { get; private set; }
    /// <summary>0 to 1: the share of the particles' weight within a metre of the estimate.</summary>
    public float Confidence { get; private set; }
    /// <summary>Times every particle hit a wall and the filter started again round its last estimate.</summary>
    public int Recoveries { get; private set; }
    /// <summary>Those restarts with no settled stretch between them (each restart spreads wider).</summary>
    public int RecoveriesInARow { get; private set; }
    /// <summary>Restarts in the last RecentWalk metres walked: two or more, and the walk doesn't fit the building
    /// from where the filter thinks it started (the wrong door, or a plan turned too far to correct).</summary>
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
    public int Count => m_Count;

    public PositionFilter(Walkable walkable, int count = DefaultCount, int seed = 1)
    {
        m_Walkable = walkable;
        m_Count = Mathf.Max(1, count);
        m_Position = new Vector2[m_Count];
        m_SparePosition = new Vector2[m_Count];
        m_Heading = new float[m_Count];
        m_SpareHeading = new float[m_Count];
        m_Scale = new float[m_Count];
        m_SpareScale = new float[m_Count];
        m_Weight = new float[m_Count];
        m_Likelihood = new float[m_Count];
        m_Random = new System.Random(seed);
    }

    // The walk starts: the user stands at plan point `at` (on walkable floor, or the nearest that is), and the camera
    // is at world (x, z) `camera`, with the plan turned by baseYaw.
    public void Reset(Vector2 at, Vector2 camera, float baseYaw, float positionSpread = 0.5f, float headingSpread = 10f)
    {
        BaseYaw = baseYaw;
        m_Camera = camera;
        m_Lost = false;
        m_GoodSteps = 0;
        m_Contradictions = 0;
        m_Walked = 0f;
        for (int i = 0; i < m_RecoveredAt.Length; i++)
            m_RecoveredAt[i] = float.NegativeInfinity;
        Recoveries = 0;
        RecoveriesInARow = 0;
        Vector2 centre = OnFloor(at, 3f, at);
        for (int i = 0; i < m_Count; i++)
            Seed(i, centre, positionSpread, 0f, headingSpread, 1f, StartScaleSpread);
        Uniform();
        UpdateEstimate();
    }

    // Each frame: where the AR camera is now, and whether ARCore is tracking. The filter steps once the camera has
    // moved Step; untracked poses are ignored, and the walk picks up from the next tracked one.
    public void Follow(Vector2 camera, bool tracking)
    {
        if (!tracking)
        {
            m_Lost = true;
            return;
        }
        Vector2 move = camera - m_Camera;
        float length = move.magnitude;
        if (m_Lost)
        {
            m_Lost = false;
            // Walked blind: a plausible gap counts, with double the noise; past that, nothing is known but roughly here.
            if (length > MaxRegainStep)
            {
                Diffuse(RegainSpread, RegainHeadingSpread);
                m_Camera = camera;
            }
            else if (length >= Step)
            {
                Predict(move, 2f);
                m_Camera = camera;
            }
            return;
        }
        if (length > JumpDistance)
        {
            // The pose jumped, the user didn't: keep the cloud where it is, a little looser.
            Diffuse(JumpSpread, JumpHeadingSpread);
            m_Camera = camera;
            return;
        }
        if (length >= Step)
        {
            Predict(move, 1f);
            m_Camera = camera;
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
            m_Heading[i] = Mathf.Clamp(m_Heading[i] + degrees, -MaxHeadingOffset, MaxHeadingOffset);
        UpdateEstimate();
    }

    // One step of the camera, worldMove metres in world (x, z). noise scales the usual noise.
    public void Predict(Vector2 worldMove, float noise = 1f)
    {
        float length = worldMove.magnitude;
        if (length <= 0f)
            return;
        m_Walked += length;
        float positionSigma = PositionNoise * length * noise;
        float headingSigma = HeadingNoise * Mathf.Sqrt(length) * noise;
        float scaleSigma = ScaleNoise * Mathf.Sqrt(length) * noise;
        Vector2 expected = Estimate + ToPlan(worldMove, Yaw) * Scale;

        float alive = 0f;
        for (int i = 0; i < m_Count; i++)
        {
            if (m_Weight[i] <= 0f)
                continue;   // dead until the next resample replaces it
            Vector2 from = m_Position[i];
            Vector2 to = from + ToPlan(worldMove, BaseYaw + m_Heading[i]) * m_Scale[i] + Gaussian2() * positionSigma;
            if (m_Walkable != null && !m_Walkable.SegmentClear(from, to))
            {
                m_Weight[i] = 0f;
                continue;
            }
            m_Position[i] = to;
            m_Heading[i] = Mathf.Clamp(m_Heading[i] + Gaussian() * headingSigma, -MaxHeadingOffset, MaxHeadingOffset);
            m_Scale[i] = Mathf.Clamp(m_Scale[i] + Gaussian() * scaleSigma, MinScale, MaxScale);
            alive += m_Weight[i];
        }

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
            m_Heading[i] = Mathf.Clamp(m_Heading[i] + Gaussian() * headingSpread, -MaxHeadingOffset, MaxHeadingOffset);
        }
        UpdateEstimate();
    }

    // A room-number sign seen at worldBearing (degrees clockwise from world +z) and range metres from the camera's
    // position at the last step. Any of landmarks may be the sign (the same number can hang at two doors), each with
    // the unit normal out of its wall (zero if not known). False if the sighting wasn't used.
    public bool AddSignObservation(float worldBearing, float range, float rangeSigma, Vector2 landmark, Vector2 normal)
    {
        return AddSignObservation(worldBearing, range, rangeSigma, new[] { landmark }, new[] { normal });
    }

    public bool AddSignObservation(float worldBearing, float range, float rangeSigma,
        IReadOnlyList<Vector2> landmarks, IReadOnlyList<Vector2> normals)
    {
        if (landmarks == null || landmarks.Count == 0 || !(range > 0f))
            return false;
        rangeSigma = Mathf.Max(rangeSigma, MinRangeSigma);
        PrepareStandOffs(landmarks, normals);

        float total = Weigh(worldBearing, range, rangeSigma, landmarks, normals);
        if (total <= 1e-12f)
        {
            // Nothing here could have seen it.
            bool trusted = Confidence >= TrustAbove && m_Contradictions == 0;
            m_Contradictions++;
            if (trusted)
                return false;
            SeedFromSign(worldBearing, range, rangeSigma, landmarks, normals);
            total = Weigh(worldBearing, range, rangeSigma, landmarks, normals);
            if (total <= 1e-12f)
                return false;
        }
        m_Contradictions = 0;
        for (int i = 0; i < m_Count; i++)
            m_Weight[i] *= m_Likelihood[i] / total;
        ResampleIfNeeded();
        UpdateEstimate();
        return true;
    }

    // Each particle's likelihood of the sighting, into m_Likelihood; returns the weighted total.
    float Weigh(float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
        IReadOnlyList<Vector2> normals)
    {
        float total = 0f;
        for (int i = 0; i < m_Count; i++)
        {
            m_Likelihood[i] = m_Weight[i] > 0f ? Likelihood(i, worldBearing, range, rangeSigma, landmarks, normals) : 0f;
            total += m_Weight[i] * m_Likelihood[i];
        }
        return total;
    }

    float Likelihood(int i, float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
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

    // Starts the cloud again where the sighting puts the user: in front of each candidate sign, spread by the
    // range noise and a little heading.
    void SeedFromSign(float worldBearing, float range, float rangeSigma, IReadOnlyList<Vector2> landmarks,
        IReadOnlyList<Vector2> normals)
    {
        for (int i = 0; i < m_Count; i++)
        {
            int k = i % landmarks.Count;
            Vector2 normal = normals != null && k < normals.Count ? normals[k] : Vector2.zero;
            Vector2 at = m_Position[i];
            float heading = HeadingOffset;
            for (int t = 0; t < SeedTries; t++)
            {
                float h = Mathf.Clamp(HeadingOffset + Gaussian() * NewFixHeadingSpread, -MaxHeadingOffset, MaxHeadingOffset);
                float r = Mathf.Max(0.3f, range + Gaussian() * rangeSigma);
                Vector2 c = landmarks[k] - HeadingVector(worldBearing - (BaseYaw + h)) * r + Gaussian2() * NewFixSpread;
                if (normal.sqrMagnitude > 0.01f && Vector2.Dot(c - landmarks[k], normal) <= 0f)
                    continue;
                if (m_Walkable != null && !m_Walkable.IsWalkable(c))
                    continue;
                at = c;
                heading = h;
                break;
            }
            m_Position[i] = at;
            m_Heading[i] = heading;
            m_Scale[i] = Scale;
        }
        Uniform();
        RecoveriesInARow = 0;
        m_GoodSteps = 0;
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
        m_Heading[i] = Mathf.Clamp(heading + Gaussian() * headingSpread, -MaxHeadingOffset, MaxHeadingOffset);
        m_Scale[i] = Mathf.Clamp(scale + Gaussian() * scaleSpread, MinScale, MaxScale);
    }

    // p if it's walkable (or there's no map), else the nearest walkable spot within reach, else fallback.
    Vector2 OnFloor(Vector2 p, float reach, Vector2 fallback)
    {
        if (m_Walkable == null || m_Walkable.IsWalkable(p))
            return p;
        return m_Walkable.Nearest(p, reach, out Vector2 nearest) ? nearest : fallback;
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

    // Systematic resampling: m_Count evenly spaced picks along the cumulative weights.
    void Resample()
    {
        float step = 1f / m_Count;
        float target = (float)m_Random.NextDouble() * step;
        float cumulative = m_Weight[0];
        int j = 0, lastAlive = m_Weight[0] > 0f ? 0 : -1;
        for (int i = 0; i < m_Count; i++, target += step)
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
        Uniform();
    }

    void UpdateEstimate()
    {
        float total = 0f, heading = 0f, scale = 0f;
        Vector2 mean = Vector2.zero;
        for (int i = 0; i < m_Count; i++)
        {
            float w = m_Weight[i];
            if (w <= 0f)
                continue;
            total += w;
            mean += m_Position[i] * w;
            heading += m_Heading[i] * w;
            scale += m_Scale[i] * w;
        }
        if (total <= 0f)
            return;
        mean /= total;
        HeadingOffset = heading / total;
        Scale = scale / total;

        // The mean of a cloud split by a wall can sit in the wall: then the particle nearest it stands in.
        Vector2 estimate = mean;
        if (m_Walkable != null && !m_Walkable.IsWalkable(mean))
        {
            float best = float.PositiveInfinity;
            for (int i = 0; i < m_Count; i++)
            {
                float d = (m_Position[i] - mean).sqrMagnitude;
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
