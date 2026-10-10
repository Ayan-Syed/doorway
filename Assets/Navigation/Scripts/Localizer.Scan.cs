using UnityEngine;

// Matches a depth scan (points ARCore measured on whatever is round the user, in AR world space) against the plan's
// walls: the likelihood field model of Thrun, Burgard & Fox, Probabilistic Robotics (2005), §6.4, as lidar is
// matched in robot localization. For each particle, every point is put on the plan by that particle's pose and
// scored by its distance d to the nearest wall (DistanceField): z_hit · N(d; 0, σ_hit) + z_rand. The z_rand share
// keeps people, furniture and glass from dominating; z_max (no return) doesn't apply, since depth pixels without a
// reading are dropped before they get here. The points are subsampled (§6.3.4) and the product over them tempered:
// points 10 cm apart on one wall aren't independent readings, and the same points come round again in the next
// updates, so the whole scan counts as Strength readings per update. Without that the cloud would collapse onto
// one guess in a hallway where the scan can't tell positions along it apart.
// The likelihood field alone ignores what lies between the camera and a point, and in a building full of walls a
// turned, shifted pose can put every point near some wall. So each point's ray is also checked at two places along
// it: a ray that passes through a wall on the way (a point "seen" through a wall) scores like an outlier at best,
// a cheap stand-in for the beam model's free space (§6.3).
public sealed class ScanMatcher
{
    /// <summary>σ_hit, metres: the plan's walls are good to about 10 cm, depth to about 5.</summary>
    public const float HitSigma = 0.15f;
    /// <summary>z_hit and z_rand (Probabilistic Robotics, §6.4).</summary>
    public const float HitWeight = 0.8f, RandomWeight = 0.2f;
    /// <summary>At most this many points of a scan are scored (§6.3.4's subsampling).</summary>
    public const int MaxPoints = 200;
    /// <summary>Fewer points than this aren't a scan.</summary>
    public const int MinPoints = 12;
    /// <summary>A full scan counts as this many independent readings per update; one with fewer than FullScan
    /// points as proportionally fewer (an atrium with little in range says little).</summary>
    public const float Strength = 4f;
    const int FullScan = 100;
    // The most one update may change any weight by, in log terms.
    const float MaxSpan = 10f;
    // A point this close to a wall counts as a hit for the fit statistics.
    const float HitDistance = 0.3f;
    // Where along each ray it's checked for a wall in the way (shares of the range), and what such a point scores.
    const float ThroughA = 0.5f, ThroughB = 0.8f;
    const float ThroughWall = 0.05f;

    readonly DistanceField m_Field;
    readonly float[] m_Table = new float[DistanceField.MaxCentimetres + 1];
    readonly float m_Through = Mathf.Log(ThroughWall);
    readonly float[] m_LogLikelihood;
    readonly float[] m_Mean;
    readonly float[] m_DX = new float[MaxPoints], m_DZ = new float[MaxPoints];

    /// <summary>Points scored in the last update.</summary>
    public int LastPoints { get; private set; }
    /// <summary>The best particle's mean distance from a scan point to the nearest wall, metres: the main tuning
    /// signal (small when the scan fits).</summary>
    public float LastBestMean { get; private set; }
    /// <summary>The best particle's share of points within 30 cm of a wall.</summary>
    public float LastHitShare { get; private set; }
    /// <summary>The weighted geometric mean of the per-point likelihood: what Augmented MCL averages.</summary>
    public float LastAverage { get; private set; }

    public ScanMatcher(DistanceField field, int capacity)
    {
        m_Field = field;
        m_LogLikelihood = new float[capacity];
        m_Mean = new float[capacity];
        for (int cm = 0; cm <= DistanceField.MaxCentimetres; cm++)
        {
            float d = cm * 0.01f / HitSigma;
            m_Table[cm] = Mathf.Log(HitWeight * Mathf.Exp(-0.5f * d * d) + RandomWeight);
        }
    }

    // Weighs the filter's particles by how well the scan (count world (x, z) points) fits the walls from each.
    // False if the scan was too small to use.
    public bool Weigh(PositionFilter filter, Vector2[] points, int count) => Weigh(filter, points, count, 1f);

    // strength scales Strength: the engine weighs the first scans after a snap more lightly, so the cloud doesn't
    // close round a wrong heading before the scans from a few directions have had their say.
    public bool Weigh(PositionFilter filter, Vector2[] points, int count, float strength)
    {
        LastPoints = 0;
        if (m_Field == null || points == null || count < MinPoints)
            return false;
        // Evenly subsampled, relative to the camera where the filter last moved.
        int n = Mathf.Min(count, MaxPoints);
        float stride = count / (float)n;
        Vector2 camera = filter.Camera;
        for (int j = 0; j < n; j++)
        {
            Vector2 p = points[(int)(j * stride)];
            m_DX[j] = p.x - camera.x;
            m_DZ[j] = p.y - camera.y;
        }
        LastPoints = n;
        float tempering = Strength * strength * Mathf.Min(1f, n / (float)FullScan);

        int particles = filter.Count, best = -1;
        float average = 0f, bestLog = float.NegativeInfinity;
        for (int i = 0; i < particles; i++)
        {
            float w = filter.ParticleWeight(i);
            if (w <= 0f)
            {
                m_LogLikelihood[i] = float.NegativeInfinity;
                continue;
            }
            // The particle's pose, as the rotation and scale that put a world offset on the plan.
            float r = -filter.ParticleYaw(i) * Mathf.Deg2Rad;
            float scale = filter.ParticleScale(i);
            float c = Mathf.Cos(r) * scale, s = Mathf.Sin(r) * scale;
            Vector2 at = filter.ParticlePosition(i);
            float sum = 0f;
            for (int j = 0; j < n; j++)
            {
                float px = m_DX[j] * c + m_DZ[j] * s, pz = -m_DX[j] * s + m_DZ[j] * c;
                // Through a wall on the way: no better than an outlier, however well the end lands.
                if (m_Field.Centimetres(at.x + px * ThroughA, at.y + pz * ThroughA) == 0 ||
                    m_Field.Centimetres(at.x + px * ThroughB, at.y + pz * ThroughB) == 0)
                    sum += m_Through;
                else
                    sum += m_Table[m_Field.Centimetres(at.x + px, at.y + pz)];
            }
            float mean = sum / n;
            m_Mean[i] = mean;
            m_LogLikelihood[i] = mean * tempering;
            average += w * Mathf.Exp(mean);
            if (m_LogLikelihood[i] > bestLog)
            {
                bestLog = m_LogLikelihood[i];
                best = i;
            }
        }
        if (best < 0)
            return false;
        LastAverage = average;
        Statistics(filter, best, n);
        // Table 8.3: the averages see the measurement's likelihood under the prediction, before resampling.
        filter.Reweight(m_LogLikelihood, MaxSpan);
        filter.ReportLikelihood(average);
        return true;
    }

    // The best particle's fit: mean distance to a wall and share of hits.
    void Statistics(PositionFilter filter, int i, int n)
    {
        float r = -filter.ParticleYaw(i) * Mathf.Deg2Rad;
        float scale = filter.ParticleScale(i);
        float c = Mathf.Cos(r) * scale, s = Mathf.Sin(r) * scale;
        Vector2 at = filter.ParticlePosition(i);
        float distance = 0f;
        int hits = 0;
        for (int j = 0; j < n; j++)
        {
            float dx = m_DX[j], dz = m_DZ[j];
            float d = m_Field.Centimetres(at.x + dx * c + dz * s, at.y - dx * s + dz * c) * 0.01f;
            distance += d;
            if (d < HitDistance)
                hits++;
        }
        LastBestMean = distance / n;
        LastHitShare = hits / (float)n;
    }
}

// The last couple of seconds of depth points (AR world space, flattened to x, z), thinned to one per 10 cm cell and a
// few hundred in all: what ScanMatcher scores. ARCore's pose is consistent over a few seconds, so points measured
// from a few places add up to more of the walls than one frame sees. The newest point in each cell is kept.
public sealed class ScanBuffer
{
    public const float Window = 2f, Voxel = 0.1f;
    public const int Target = 250;
    const int Capacity = 4096;

    readonly Vector2[] m_Points = new Vector2[Capacity];
    readonly double[] m_Times = new double[Capacity];
    readonly System.Collections.Generic.HashSet<long> m_Cells = new System.Collections.Generic.HashSet<long>();
    readonly Vector2[] m_Kept = new Vector2[Capacity];
    int m_Start, m_Count;

    public void Clear() => m_Start = m_Count = 0;

    public void Add(double time, Vector2 point)
    {
        if (m_Count == Capacity)
        {
            m_Start = (m_Start + 1) % Capacity;   // full: the oldest goes
            m_Count--;
        }
        int at = (m_Start + m_Count) % Capacity;
        m_Points[at] = point;
        m_Times[at] = time;
        m_Count++;
    }

    // The scan as of now into output (at least Target long): newest first, one per cell, evenly thinned to Target.
    public int Build(double now, Vector2[] output)
    {
        while (m_Count > 0 && now - m_Times[m_Start] > Window)
        {
            m_Start = (m_Start + 1) % Capacity;
            m_Count--;
        }
        m_Cells.Clear();
        int kept = 0;
        for (int k = m_Count - 1; k >= 0; k--)
        {
            Vector2 p = m_Points[(m_Start + k) % Capacity];
            long x = Mathf.FloorToInt(p.x / Voxel) & 0xFFFFFFF, z = Mathf.FloorToInt(p.y / Voxel) & 0xFFFFFFF;
            if (m_Cells.Add((x << 28) | z))
                m_Kept[kept++] = p;
        }
        int n = Mathf.Min(kept, Mathf.Min(Target, output.Length));
        float stride = kept / (float)Mathf.Max(1, n);
        for (int j = 0; j < n; j++)
            output[j] = m_Kept[(int)(j * stride)];
        return n;
    }
}
