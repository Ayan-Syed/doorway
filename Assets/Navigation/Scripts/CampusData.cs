using System;
using System.Collections.Generic;
using UnityEngine;

// The campus around the building, from OpenStreetMap (Tools/campus/campus.py): ground triangles in layers, and every
// building's outline, roof, height and name. Plan metres, like the floor plan. Read once and shared, so the big map
// and the minimap parse it only once.
//
// ucf-campus.bytes (little-endian): "DWCP", uint16 version 1, uint16 0; int32 chunk count, then per chunk int32
// layer, vertex count, index count, int16 x, y decimetres per vertex, uint16 indices; int32 building count, then per
// building int32 height (dm), ring count, index count, int32 points per ring, int16 x, y dm, uint16 indices.
// ucf-campus.json: attribution, middle, min, max, the floor plan's geo block it was built with, and the buildings.
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
        public PlanGeo geo;
        public Building[] buildings;
    }

    const string Magic = "DWCP";
    const int Version = 1;

    public readonly Chunk[] chunks;
    public readonly Building[] buildings;
    // The building with the indoor map, or null.
    public readonly Building indoor;
    // Where the map opens: the middle of campus.
    public readonly Vector2 middle;
    public readonly Rect extent;
    public readonly string attribution;
    // The floor plan's geo block the campus was projected with (null in a file from before it was recorded).
    public readonly PlanGeo geo;

    static readonly Dictionary<TextAsset, CampusData> s_Loaded = new Dictionary<TextAsset, CampusData>();

    // Null if either file is missing (the map then shows the floor alone). FormatException, naming the file and
    // the problem, if one is broken.
    public static CampusData Load(TextAsset geometry, TextAsset info)
    {
        if (geometry == null || info == null)
            return null;
        if (!s_Loaded.TryGetValue(geometry, out CampusData campus))
        {
            campus = Parse(geometry.bytes, info.text, geometry.name);
            s_Loaded[geometry] = campus;
        }
        return campus;
    }

    // Reads the pair without keeping it (the editor's data check, tests).
    public static CampusData Parse(byte[] bytes, string json, string name)
    {
        return new CampusData(bytes, json, name ?? "ucf-campus");
    }

    CampusData(byte[] bytes, string json, string name)
    {
        Info info;
        try
        {
            info = JsonUtility.FromJson<Info>(json ?? "");
        }
        catch (Exception e)
        {
            throw new FormatException($"{name}.json: not valid JSON ({e.Message})");
        }
        if (info == null || info.middle == null || info.middle.Length < 2 || info.min == null || info.min.Length < 2
            || info.max == null || info.max.Length < 2)
            throw new FormatException($"{name}.json: middle, min or max is missing");
        attribution = info.attribution;
        middle = new Vector2(info.middle[0], info.middle[1]);
        extent = Rect.MinMaxRect(info.min[0], info.min[1], info.max[0], info.max[1]);
        geo = info.geo != null && info.geo.IsSet ? info.geo : null;

        var reader = new DataReader(bytes, name);
        reader.Header(Magic, Version);
        reader.UInt16();
        chunks = new Chunk[reader.Count("chunks", 12)];
        for (int c = 0; c < chunks.Length; c++)
        {
            int layer = reader.Int32();
            int vertexCount = reader.Count("ground points", 4);
            int indexCount = reader.Int32();
            Vector2[] points = reader.Points(vertexCount, 0.1f);
            chunks[c] = new Chunk { layer = layer, points = points, triangles = Indices(reader, indexCount, vertexCount) };
        }

        int buildingCount = reader.Count("buildings", 12);
        int names = info.buildings?.Length ?? 0;
        if (buildingCount != names)
            throw reader.Fail($"{buildingCount} buildings but {names} names in {name}.json");
        buildings = info.buildings ?? new Building[0];
        for (int b = 0; b < buildings.Length; b++)
        {
            Building building = buildings[b];
            building.index = b;
            reader.Int32();   // the height again; the info's is the same
            int ringCount = reader.Count("rings", 4), indexCount = reader.Int32();
            building.rings = new int[ringCount];
            int total = 0;
            for (int r = 0; r < ringCount; r++)
            {
                int count = reader.Int32();
                if (count < 0)
                    throw reader.Fail($"building {b} has a ring of {count} points");
                total += building.rings[r] = count;
            }
            building.points = reader.Points(total, 0.1f);
            building.roof = Indices(reader, indexCount, total);
            building.bounds = Bounds(building.points);
            if (building.indoor && indoor == null)
                indoor = building;
        }
        reader.End();
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

    // Triangle indices (uint16), each checked against the points they index.
    static ushort[] Indices(DataReader reader, int count, int points)
    {
        if (count < 0 || count % 3 != 0)
            throw reader.Fail($"{count} indices, not a whole number of triangles");
        var indices = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            int v = reader.UInt16();
            if (v >= points)
                throw reader.Fail($"index {v} is past its {points} points");
            indices[i] = (ushort)v;
        }
        return indices;
    }
}
