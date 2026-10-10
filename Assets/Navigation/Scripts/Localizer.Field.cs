using System;
using UnityEngine;

// How far every spot on the floor is from the nearest wall, for matching depth scans against the plan (ScanMatcher):
// a likelihood field, as robots match lidar against a map. Built once at load from the walkable map (anything not
// walkable, outside the building included, counts as wall) and the plan's wall outlines drawn in on top, so a thin
// wall between two walkable rooms still stands. Exact Euclidean distances (Felzenszwalb and Huttenlocher's two-pass
// transform), stored to the centimetre and capped at MaxCentimetres: a scan point further than that from every wall
// is an outlier anyway. Also how far a spot outdoors is from the building, on a coarse grid with a margin round it,
// for GPS near the entrance.
public sealed class DistanceField
{
    /// <summary>Distances are stored to this many centimetres; further is the same as this.</summary>
    public const int MaxCentimetres = 255;
    // The outdoor grid: metres a cell, and how far beyond the floor's grid it reaches.
    const float OutdoorCell = 1f, OutdoorMargin = 60f;

    public readonly int width, height;
    public readonly float left, top, cell;
    readonly float m_InverseCell;
    readonly byte[] m_Centimetres;

    readonly int m_OutWidth, m_OutHeight;
    readonly float m_OutLeft, m_OutTop;
    readonly float[] m_Outside;

    DistanceField(Walkable walkable, byte[] centimetres, int outWidth, int outHeight, float outLeft, float outTop,
        float[] outside)
    {
        width = walkable.width;
        height = walkable.height;
        left = walkable.left;
        top = walkable.top;
        cell = walkable.cell;
        m_InverseCell = 1f / cell;
        m_Centimetres = centimetres;
        m_OutWidth = outWidth;
        m_OutHeight = outHeight;
        m_OutLeft = outLeft;
        m_OutTop = outTop;
        m_Outside = outside;
    }

    // Builds both fields: a few hundred thousand cells, so a background thread on the phone (it touches no Unity
    // object). wallPoints and rings are the plan's wall outlines (WallMesh.Parse); null for none.
    public static DistanceField Build(Walkable walkable, Vector2[] wallPoints, int[] rings)
    {
        int w = walkable.width, h = walkable.height;
        var wall = new bool[w * h];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
                wall[j * w + i] = !walkable.IsWalkable(new Vector2(walkable.left + (i + 0.5f) * walkable.cell,
                    walkable.top - (j + 0.5f) * walkable.cell));
        }
        if (wallPoints != null && rings != null)
        {
            for (int r = 0, start = 0; r < rings.Length; start += rings[r], r++)
            {
                for (int k = 0; k < rings[r]; k++)
                    Draw(wall, walkable, wallPoints[start + k], wallPoints[start + (k + 1) % rings[r]]);
            }
        }
        float[] squared = Transform(wall, w, h);
        var centimetres = new byte[w * h];
        float toCentimetres = walkable.cell * 100f;
        for (int i = 0; i < centimetres.Length; i++)
            centimetres[i] = (byte)Mathf.Min(MaxCentimetres, Mathf.RoundToInt(Mathf.Sqrt(squared[i]) * toCentimetres));

        // Outdoors: a coarse grid over the floor's and a margin, each cell building if any walkable cell is in it.
        int ow = Mathf.CeilToInt(w * walkable.cell / OutdoorCell + 2f * OutdoorMargin / OutdoorCell);
        int oh = Mathf.CeilToInt(h * walkable.cell / OutdoorCell + 2f * OutdoorMargin / OutdoorCell);
        float outLeft = walkable.left - OutdoorMargin, outTop = walkable.top + OutdoorMargin;
        var building = new bool[ow * oh];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                if (wall[j * w + i] && !walkable.IsWalkable(new Vector2(walkable.left + (i + 0.5f) * walkable.cell,
                        walkable.top - (j + 0.5f) * walkable.cell)))
                    continue;
                int oi = Mathf.FloorToInt((i * walkable.cell + OutdoorMargin) / OutdoorCell);
                int oj = Mathf.FloorToInt((j * walkable.cell + OutdoorMargin) / OutdoorCell);
                if (oi >= 0 && oj >= 0 && oi < ow && oj < oh)
                    building[oj * ow + oi] = true;
            }
        }
        float[] outside = Transform(building, ow, oh);
        for (int i = 0; i < outside.Length; i++)
            outside[i] = Mathf.Sqrt(outside[i]) * OutdoorCell;
        return new DistanceField(walkable, centimetres, ow, oh, outLeft, outTop, outside);
    }

    /// <summary>Metres from plan point p to the nearest wall; MaxCentimetres off the grid.</summary>
    public float Distance(Vector2 p) => Centimetres(p.x, p.y) * 0.01f;

    /// <summary>Centimetres from plan point (x, z) to the nearest wall, capped. Inlined into the scan's inner loop.</summary>
    public int Centimetres(float x, float z)
    {
        int i = (int)((x - left) * m_InverseCell);
        int j = (int)((top - z) * m_InverseCell);
        if (x < left || z > top || i >= width || j >= height)
            return MaxCentimetres;
        return m_Centimetres[j * width + i];
    }

    /// <summary>Metres from plan point p (from GPS) to the building, 0 inside it; past the margin, an estimate.</summary>
    public float OutsideDistance(Vector2 p)
    {
        int i = Mathf.FloorToInt((p.x - m_OutLeft) / OutdoorCell);
        int j = Mathf.FloorToInt((m_OutTop - p.y) / OutdoorCell);
        if (i >= 0 && j >= 0 && i < m_OutWidth && j < m_OutHeight)
            return m_Outside[j * m_OutWidth + i];
        // Off the grid: at least the margin, plus however far beyond it.
        float dx = Mathf.Max(0f, Mathf.Max(m_OutLeft - p.x, p.x - (m_OutLeft + m_OutWidth * OutdoorCell)));
        float dz = Mathf.Max(0f, Mathf.Max(p.y - m_OutTop, (m_OutTop - m_OutHeight * OutdoorCell) - p.y));
        return OutdoorMargin + Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Marks the cells along a wall outline's edge as wall.
    static void Draw(bool[] wall, Walkable walkable, Vector2 a, Vector2 b)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / (walkable.cell * 0.5f)));
        for (int k = 0; k <= steps; k++)
        {
            Vector2 p = Vector2.Lerp(a, b, k / (float)steps);
            int i = Mathf.FloorToInt((p.x - walkable.left) / walkable.cell);
            int j = Mathf.FloorToInt((walkable.top - p.y) / walkable.cell);
            if (i >= 0 && j >= 0 && i < walkable.width && j < walkable.height)
                wall[j * walkable.width + i] = true;
        }
    }

    // Squared distance, in cells, from every cell to the nearest set one: columns then rows of the 1-D transform.
    static float[] Transform(bool[] set, int w, int h)
    {
        const float Far = 1e20f;
        var grid = new float[w * h];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = set[i] ? 0f : Far;
        int n = Math.Max(w, h);
        var f = new float[n];
        var d = new float[n];
        var v = new int[n];
        var z = new float[n + 1];
        for (int i = 0; i < w; i++)
        {
            for (int j = 0; j < h; j++)
                f[j] = grid[j * w + i];
            Transform1D(f, h, d, v, z);
            for (int j = 0; j < h; j++)
                grid[j * w + i] = d[j];
        }
        for (int j = 0; j < h; j++)
        {
            Array.Copy(grid, j * w, f, 0, w);
            Transform1D(f, w, d, v, z);
            Array.Copy(d, 0, grid, j * w, w);
        }
        return grid;
    }

    // The lower envelope of parabolas rooted at each sample (Felzenszwalb and Huttenlocher, 2012).
    static void Transform1D(float[] f, int n, float[] d, int[] v, float[] z)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = (f[q] + q * q - (f[p] + p * p)) / (2f * (q - p));
                if (s > z[k] || k == 0)
                    break;
                k--;
            }
            if (s <= z[k])
            {
                // Only when k is 0 and the new parabola is lower everywhere.
                v[0] = q;
                z[0] = float.NegativeInfinity;
                z[1] = float.PositiveInfinity;
                continue;
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;
            float dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }
}
