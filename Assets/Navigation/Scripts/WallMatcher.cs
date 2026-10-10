using System.Collections.Generic;
using UnityEngine;

// A wall ARCore has found (a vertical plane), flattened to a line on the floor in AR world space (x, z).
public struct WallObservation
{
    public Vector2 centre;
    /// <summary>Unit, along the wall.</summary>
    public Vector2 direction;
    /// <summary>Unit, out of the wall toward where it was seen from.</summary>
    public Vector2 normal;
    /// <summary>Metres along the wall that ARCore has seen.</summary>
    public float length;
}

// Matches the walls ARCore sees against the plan's walls: a second, sparser measurement after the depth scan
// (ScanMatcher). For each particle, each observed wall is put on the plan by that particle's pose and scored by the
// nearest plan wall running the same way and facing the same side: by the angle between them and the distance
// across. A wall the plan doesn't have (a cabinet, a partition, a glass case) still scores Outlier, so it can't sink
// a good particle. Like the scan, the score is tempered: the same planes come round again five times a second.
// The plan walls are the wall outlines (walls.bytes), indexed on a 1 m grid.
public sealed class WallMatcher
{
    /// <summary>Observed walls shorter than this aren't used (ARCore's first guess at a plane often turns).</summary>
    public const float MinLength = 0.8f;
    /// <summary>Only walls this close to the camera: further ones are mostly seen at a slant, and drift more.</summary>
    public const float MaxRange = 4f;
    // Plan wall pieces shorter than this are corners and jags of the outline, not walls to match.
    const float MinSegment = 0.3f;
    // How far across and how far round a plan wall may be to match, and the noise of each.
    const float MatchDistance = 1f, MaxAngle = 25f;
    const float DistanceSigma = 0.2f, AngleSigma = 6f;
    // The score of a wall the plan doesn't have, and the tempering per update.
    const float Outlier = 0.3f, Tempering = 0.5f;
    const float MaxSpan = 6f;
    const float GridCell = 1f;

    readonly Vector2[] m_A, m_Direction, m_Open;
    readonly float[] m_Length;
    readonly int[] m_CellStart, m_CellItems;
    readonly int m_GridWidth, m_GridHeight;
    readonly float m_GridLeft, m_GridBottom;
    float[] m_LogLikelihood = new float[0];
    readonly List<WallObservation> m_Used = new List<WallObservation>();

    public int SegmentCount => m_A.Length;

    // From the plan's wall outlines (WallMesh.Parse): every ring's edges. walkable says which side of each is open.
    public WallMatcher(Vector2[] points, int[] rings, Walkable walkable)
        : this(Edges(points, rings), walkable)
    {
    }

    // From wall segments as point pairs (the tests' synthetic corridors).
    public WallMatcher(List<(Vector2 a, Vector2 b)> segments, Walkable walkable)
    {
        var kept = new List<(Vector2 a, Vector2 b)>();
        foreach (var s in segments)
        {
            if (Vector2.Distance(s.a, s.b) >= MinSegment)
                kept.Add(s);
        }
        int n = kept.Count;
        m_A = new Vector2[n];
        m_Direction = new Vector2[n];
        m_Open = new Vector2[n];
        m_Length = new float[n];
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = -min;
        for (int k = 0; k < n; k++)
        {
            Vector2 a = kept[k].a, b = kept[k].b;
            m_A[k] = a;
            m_Length[k] = Vector2.Distance(a, b);
            m_Direction[k] = (b - a) / m_Length[k];
            // The open side: the side of the wall a person can stand on (none if both or neither).
            var across = new Vector2(-m_Direction[k].y, m_Direction[k].x);
            Vector2 middle = (a + b) * 0.5f;
            bool left = walkable == null || walkable.IsWalkable(middle + across * 0.2f);
            bool right = walkable == null || walkable.IsWalkable(middle - across * 0.2f);
            m_Open[k] = left == right ? Vector2.zero : left ? across : -across;
            min = Vector2.Min(min, Vector2.Min(a, b));
            max = Vector2.Max(max, Vector2.Max(a, b));
        }
        if (n == 0)
            min = max = Vector2.zero;

        // Each segment goes in every grid cell within MatchDistance of it, so a lookup is one cell.
        m_GridLeft = min.x - MatchDistance - GridCell;
        m_GridBottom = min.y - MatchDistance - GridCell;
        m_GridWidth = Mathf.CeilToInt((max.x - m_GridLeft + MatchDistance + GridCell) / GridCell) + 1;
        m_GridHeight = Mathf.CeilToInt((max.y - m_GridBottom + MatchDistance + GridCell) / GridCell) + 1;
        var cells = new List<int>[m_GridWidth * m_GridHeight];
        for (int k = 0; k < n; k++)
        {
            Vector2 a = m_A[k], b = a + m_Direction[k] * m_Length[k];
            int i0 = Cell(Mathf.Min(a.x, b.x) - MatchDistance, true), i1 = Cell(Mathf.Max(a.x, b.x) + MatchDistance, true);
            int j0 = Cell(Mathf.Min(a.y, b.y) - MatchDistance, false), j1 = Cell(Mathf.Max(a.y, b.y) + MatchDistance, false);
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    int c = j * m_GridWidth + i;
                    (cells[c] ??= new List<int>()).Add(k);
                }
            }
        }
        m_CellStart = new int[cells.Length + 1];
        var items = new List<int>();
        for (int c = 0; c < cells.Length; c++)
        {
            m_CellStart[c] = items.Count;
            if (cells[c] != null)
                items.AddRange(cells[c]);
        }
        m_CellStart[cells.Length] = items.Count;
        m_CellItems = items.ToArray();
    }

    static List<(Vector2 a, Vector2 b)> Edges(Vector2[] points, int[] rings)
    {
        var edges = new List<(Vector2 a, Vector2 b)>();
        for (int r = 0, start = 0; r < rings.Length; start += rings[r], r++)
        {
            for (int k = 0; k < rings[r]; k++)
                edges.Add((points[start + k], points[start + (k + 1) % rings[r]]));
        }
        return edges;
    }

    int Cell(float v, bool x)
    {
        return x ? Mathf.Clamp(Mathf.FloorToInt((v - m_GridLeft) / GridCell), 0, m_GridWidth - 1)
            : Mathf.Clamp(Mathf.FloorToInt((v - m_GridBottom) / GridCell), 0, m_GridHeight - 1);
    }

    // Weighs the filter's particles by the observed walls near camera (world x, z). Returns how many of them match
    // a plan wall from the estimate's pose (the debug overlay's count).
    public int Weigh(PositionFilter filter, IReadOnlyList<WallObservation> walls, Vector2 camera)
    {
        m_Used.Clear();
        foreach (WallObservation wall in walls)
        {
            if (wall.length >= MinLength && DistanceToSegment(camera, wall) <= MaxRange)
                m_Used.Add(wall);
        }
        if (m_Used.Count == 0 || m_A.Length == 0)
            return 0;
        if (m_LogLikelihood.Length < filter.Capacity)
            m_LogLikelihood = new float[filter.Capacity];

        Vector2 origin = filter.Camera;
        for (int i = 0; i < filter.Count; i++)
        {
            if (filter.ParticleWeight(i) <= 0f)
            {
                m_LogLikelihood[i] = float.NegativeInfinity;
                continue;
            }
            float sum = 0f;
            foreach (WallObservation wall in m_Used)
            {
                float score = Score(wall, origin, filter.ParticlePosition(i), filter.ParticleYaw(i), filter.ParticleScale(i));
                sum += Mathf.Log(Outlier + (1f - Outlier) * score);
            }
            m_LogLikelihood[i] = sum * Tempering;
        }
        filter.Reweight(m_LogLikelihood, MaxSpan);

        int matched = 0;
        foreach (WallObservation wall in m_Used)
        {
            if (Score(wall, origin, filter.Estimate, filter.Yaw, filter.Scale) > 0.3f)
                matched++;
        }
        return matched;
    }

    // How well an observed wall fits the plan from one pose (plan position at, plan yaw, scale): 0 to 1.
    float Score(in WallObservation wall, Vector2 origin, Vector2 at, float yaw, float scale)
    {
        Vector2 centre = at + PositionFilter.ToPlan(wall.centre - origin, yaw) * scale;
        Vector2 direction = PositionFilter.ToPlan(wall.direction, yaw);
        Vector2 normal = PositionFilter.ToPlan(wall.normal, yaw);
        int i = Mathf.FloorToInt((centre.x - m_GridLeft) / GridCell), j = Mathf.FloorToInt((centre.y - m_GridBottom) / GridCell);
        if (i < 0 || j < 0 || i >= m_GridWidth || j >= m_GridHeight)
            return 0f;
        int cell = j * m_GridWidth + i;
        float best = 0f, minCos = Mathf.Cos(MaxAngle * Mathf.Deg2Rad);
        for (int c = m_CellStart[cell]; c < m_CellStart[cell + 1]; c++)
        {
            int k = m_CellItems[c];
            Vector2 d = m_Direction[k];
            float cos = Mathf.Abs(Vector2.Dot(direction, d));
            if (cos < minCos)
                continue;
            Vector2 offset = centre - m_A[k];
            float along = Vector2.Dot(offset, d);
            if (along < -0.5f || along > m_Length[k] + 0.5f)
                continue;
            float across = Mathf.Abs(offset.x * d.y - offset.y * d.x);
            if (across > MatchDistance)
                continue;
            // Facing the open side: the far face of the same wall is a different wall.
            if (m_Open[k] != Vector2.zero && Vector2.Dot(normal, m_Open[k]) < 0.3f)
                continue;
            float angle = Mathf.Acos(Mathf.Min(1f, cos)) * Mathf.Rad2Deg / AngleSigma;
            float distance = across / DistanceSigma;
            float score = Mathf.Exp(-0.5f * (angle * angle + distance * distance));
            if (score > best)
                best = score;
        }
        return best;
    }

    static float DistanceToSegment(Vector2 p, in WallObservation wall)
    {
        Vector2 half = wall.direction * (wall.length * 0.5f);
        return Vector2.Distance(p, Router.ClosestOnSegment(p, wall.centre - half, wall.centre + half));
    }
}
