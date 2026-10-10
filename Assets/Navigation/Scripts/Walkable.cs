using System;
using UnityEngine;

// Where a person can stand on one floor: hallways, room interiors and the doorways between them, as a grid in plan
// metres. The position filter keeps the user's estimate on it, and rerouting joins the walkway graph only along a
// clear line.
//
// Data/*-walkable.bytes (Tools/ba1-floor1/walkable.py), little-endian:
//   "DWWK", uint16 version (1), uint16 width, uint16 height, float32 left, float32 top, float32 cell,
//   then width x height bytes, top row first: 1 walkable, 0 not.
// Cell (i, j) covers x from left + i * cell and z down from top - j * cell (plan z grows up the map, like north).
public sealed class Walkable
{
    const string Magic = "DWWK";
    const int Version = 1;
    const int HeaderSize = 22;

    public readonly int width, height;
    public readonly float left, top, cell;
    readonly byte[] m_Cells;

    Walkable(int width, int height, float left, float top, float cell, byte[] cells)
    {
        this.width = width;
        this.height = height;
        this.left = left;
        this.top = top;
        this.cell = cell;
        m_Cells = cells;
    }

    /// <summary>Reads a walkable file; FormatException, naming what's wrong, for anything else.</summary>
    public static Walkable Load(byte[] bytes)
    {
        if (bytes == null || bytes.Length < HeaderSize)
            throw new FormatException("walkable: shorter than its header");
        for (int i = 0; i < Magic.Length; i++)
        {
            if (bytes[i] != Magic[i])
                throw new FormatException("walkable: not a walkable file (no DWWK)");
        }
        int version = BitConverter.ToUInt16(bytes, 4);
        if (version != Version)
            throw new FormatException($"walkable: version {version}, this app reads {Version}");
        int width = BitConverter.ToUInt16(bytes, 6);
        int height = BitConverter.ToUInt16(bytes, 8);
        float left = BitConverter.ToSingle(bytes, 10);
        float top = BitConverter.ToSingle(bytes, 14);
        float cell = BitConverter.ToSingle(bytes, 18);
        if (width == 0 || height == 0 || !(cell > 0f))
            throw new FormatException($"walkable: bad grid {width} x {height}, cell {cell}");
        if (bytes.Length != HeaderSize + width * height)
            throw new FormatException($"walkable: {bytes.Length} bytes, expected {HeaderSize + width * height}");
        var cells = new byte[width * height];
        Buffer.BlockCopy(bytes, HeaderSize, cells, 0, cells.Length);
        return new Walkable(width, height, left, top, cell, cells);
    }

    /// <summary>For tests: a grid from rows of text, top row first, '.' walkable and anything else not.</summary>
    public static Walkable FromRows(float left, float top, float cell, params string[] rows)
    {
        int h = rows.Length, w = rows[0].Length;
        var cells = new byte[w * h];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
                cells[j * w + i] = (byte)(rows[j][i] == '.' ? 1 : 0);
        }
        return new Walkable(w, h, left, top, cell, cells);
    }

    public bool IsWalkable(Vector2 p)
    {
        return Cell(p, out int i, out int j) && m_Cells[j * width + i] != 0;
    }

    /// <summary>True when every cell on the straight line from a to b is walkable: no wall in between.</summary>
    public bool SegmentClear(Vector2 a, Vector2 b)
    {
        float length = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(length / (cell * 0.5f)));
        for (int k = 0; k <= steps; k++)
        {
            if (!IsWalkable(Vector2.Lerp(a, b, k / (float)steps)))
                return false;
        }
        return true;
    }

    /// <summary>The centre of the nearest walkable cell within maxDistance of p; false if there is none.</summary>
    public bool Nearest(Vector2 p, float maxDistance, out Vector2 nearest)
    {
        nearest = p;
        if (IsWalkable(p))
            return true;
        int ci = Mathf.FloorToInt((p.x - left) / cell);
        int cj = Mathf.FloorToInt((top - p.y) / cell);
        int rings = Mathf.CeilToInt(maxDistance / cell);
        float best = maxDistance * maxDistance;
        bool found = false;
        for (int r = 1; r <= rings; r++)
        {
            for (int dj = -r; dj <= r; dj++)
            {
                for (int di = -r; di <= r; di++)
                {
                    if (Mathf.Abs(di) != r && Mathf.Abs(dj) != r)
                        continue;   // only the ring's edge; the inside was searched already
                    int i = ci + di, j = cj + dj;
                    if (i < 0 || j < 0 || i >= width || j >= height || m_Cells[j * width + i] == 0)
                        continue;
                    var centre = new Vector2(left + (i + 0.5f) * cell, top - (j + 0.5f) * cell);
                    float d = (centre - p).sqrMagnitude;
                    if (d <= best)
                    {
                        best = d;
                        nearest = centre;
                        found = true;
                    }
                }
            }
            // A cell found in ring r can still lose to one in ring r + 1 (corners are further than edges).
            if (found && (r + 1) * cell > Mathf.Sqrt(best) * 1.5f)
                break;
        }
        return found;
    }

    bool Cell(Vector2 p, out int i, out int j)
    {
        i = Mathf.FloorToInt((p.x - left) / cell);
        j = Mathf.FloorToInt((top - p.y) / cell);
        return i >= 0 && j >= 0 && i < width && j < height;
    }
}
