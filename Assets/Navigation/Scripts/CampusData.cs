using System;
using System.Collections.Generic;
using UnityEngine;

// The campus around the building, from OpenStreetMap (Tools/campus/campus.py): ground triangles in layers, and every
// building's outline, roof, height and name. Plan metres, like the floor plan. Read once and shared, so the big map
// and the minimap parse it only once.
public class CampusData
{
    [Serializable]
    public class Building
    {
        public string name;
        public float x;
        public float y;
        public float area;
        public float height;
        public bool indoor;

        [NonSerialized] public int index;
        // Every ring one after another (outer anticlockwise, holes clockwise), metres.
        [NonSerialized] public Vector2[] points;
        // Points per ring.
        [NonSerialized] public int[] rings;
        // Roof triangles, indices into points, clockwise with y up.
        [NonSerialized] public ushort[] roof;
        [NonSerialized] public Rect bounds;

        // Where the name goes.
        public Vector2 Spot => new Vector2(x, y);

        // Even-odd over every ring, so a point in a courtyard (a hole) is outside.
        public bool Contains(Vector2 p)
        {
            if (!bounds.Contains(p))
                return false;
            bool inside = false;
            for (int r = 0, start = 0; r < rings.Length; start += rings[r], r++)
            {
                int count = rings[r];
                for (int i = 0, j = count - 1; i < count; j = i++)
                {
                    Vector2 a = points[start + i], b = points[start + j];
                    if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                        inside = !inside;
                }
            }
            return inside;
        }

        // Metres from p to the nearest wall of the outline.
        public float Distance(Vector2 p)
        {
            float best = float.PositiveInfinity;
            for (int r = 0, start = 0; r < rings.Length; start += rings[r], r++)
            {
                int count = rings[r];
                for (int i = 0, j = count - 1; i < count; j = i++)
                {
                    Vector2 a = points[start + j], ab = points[start + i] - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                    best = Mathf.Min(best, (a + ab * t - p).sqrMagnitude);
                }
            }
            return Mathf.Sqrt(best);
        }
    }

    // One triangle mesh of one ground layer (grass, paths, roads...).
    public struct Chunk
    {
        public int layer;
        public Vector2[] points;
        public ushort[] triangles;
    }

    [Serializable]
    class Info
    {
        public string attribution;
        public float[] middle;
        public float[] min;
        public float[] max;
        public Building[] buildings;
    }

    public readonly Chunk[] chunks;
    public readonly Building[] buildings;
    // The building with the indoor map, or null.
    public readonly Building indoor;
    // Where the map opens: the middle of campus.
    public readonly Vector2 middle;
    public readonly Rect extent;
    public readonly string attribution;

    static readonly Dictionary<TextAsset, CampusData> s_Loaded = new Dictionary<TextAsset, CampusData>();

    // The file while reading it.
    byte[] m_Bytes;
    int m_At;

    // Null if either file is missing (the map then shows the floor alone).
    public static CampusData Load(TextAsset geometry, TextAsset info)
    {
        if (geometry == null || info == null)
            return null;
        if (!s_Loaded.TryGetValue(geometry, out CampusData campus))
        {
            campus = new CampusData(geometry.bytes, info.text);
            s_Loaded[geometry] = campus;
        }
        return campus;
    }

    CampusData(byte[] bytes, string json)
    {
        Info info = JsonUtility.FromJson<Info>(json);
        attribution = info.attribution;
        middle = new Vector2(info.middle[0], info.middle[1]);
        extent = Rect.MinMaxRect(info.min[0], info.min[1], info.max[0], info.max[1]);

        m_Bytes = bytes;
        m_At = 0;
        chunks = new Chunk[Int()];
        for (int c = 0; c < chunks.Length; c++)
        {
            int layer = Int(), vertexCount = Int(), indexCount = Int();
            chunks[c] = new Chunk { layer = layer, points = Points(vertexCount), triangles = Indices(indexCount) };
        }

        int buildingCount = Int();
        int names = info.buildings?.Length ?? 0;
        if (buildingCount != names)
            throw new FormatException($"ucf-campus: {buildingCount} buildings but {names} names");
        buildings = info.buildings ?? new Building[0];
        for (int b = 0; b < buildings.Length; b++)
        {
            Building building = buildings[b];
            building.index = b;
            Int();   // the height again; the info's is the same
            int ringCount = Int(), indexCount = Int();
            building.rings = new int[ringCount];
            int total = 0;
            for (int r = 0; r < ringCount; r++)
                total += building.rings[r] = Int();
            building.points = Points(total);
            building.roof = Indices(indexCount);
            building.bounds = Bounds(building.points);
            if (building.indoor && indoor == null)
                indoor = building;
        }
        m_Bytes = null;
    }

    static Rect Bounds(Vector2[] points)
    {
        if (points.Length == 0)
            return Rect.zero;
        Vector2 min = points[0], max = points[0];
        foreach (Vector2 p in points)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    // Little-endian, like the file (BitConverter uses the machine's order; Android ARM and PCs are little-endian).
    int Int()
    {
        int value = BitConverter.ToInt32(m_Bytes, m_At);
        m_At += 4;
        return value;
    }

    // Plan decimetres in the file, metres here.
    Vector2[] Points(int count)
    {
        var points = new Vector2[count];
        for (int i = 0; i < count; i++, m_At += 4)
            points[i] = new Vector2(BitConverter.ToInt16(m_Bytes, m_At), BitConverter.ToInt16(m_Bytes, m_At + 2)) * 0.1f;
        return points;
    }

    ushort[] Indices(int count)
    {
        var indices = new ushort[count];
        for (int i = 0; i < count; i++, m_At += 2)
            indices[i] = BitConverter.ToUInt16(m_Bytes, m_At);
        return indices;
    }
}
