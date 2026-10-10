using System;
using UnityEngine;
using UnityEngine.UI;

// The floor's walls standing up off the map, for when it's tilted: a side along every wall outline and a top
// over each wall, `height` metres up. Lives in the map's content, in plan metres x pixelsPerMeter, its origin
// at plan (0, 0). Local -z is up off the floor (the map tilts its +y away from the camera).
//
// walls (Data/*-walls.bytes, from Tools/ba1-floor1/walls.py, little-endian): "DWWL", uint16 version 1, uint16 0;
// int32 point count, ring count, index count; int32 points in each ring; int16 x, z of each point in plan
// centimetres; int32 indices, three to a triangle, for the tops.
[RequireComponent(typeof(CanvasRenderer))]
public class WallMesh : MaskableGraphic
{
    public TextAsset walls;
    [Tooltip("Canvas units per plan metre (FloorMap sets its own).")]
    public float pixelsPerMeter = 10f;
    [Tooltip("Wall height in metres.")]
    public float height = 1.4f;
    public Color sideColor = new Color(0.72f, 0.70f, 0.66f);

    // Where the light comes from, in plan directions: sides facing it are lighter.
    static readonly Vector2 Light = new Vector2(-0.45f, 0.89f);

    const string Magic = "DWWL";
    const int Version = 1;

    int[] m_Rings;
    Vector2[] m_Points;
    int[] m_Triangles;

    public void SetColors(Color top, Color side)
    {
        if (top == color && side == sideColor)
            return;
        sideColor = side;
        color = top;
        SetVerticesDirty();
    }

    // Another floor's walls: read afresh on the next draw.
    public void SetWalls(TextAsset outlines)
    {
        walls = outlines;
        m_Points = null;
        SetVerticesDirty();
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        m_Points = null;
    }
#endif

    bool Read()
    {
        if (m_Points != null)
            return true;
        if (walls == null)
            return false;

        Vector2[] points;
        try
        {
            Parse(walls.bytes, walls.name, out m_Rings, out points, out m_Triangles);
        }
        catch (FormatException e)
        {
            // Once, not every rebuild: the map works on without its walls standing up.
            Debug.LogError(e.Message);
            m_Rings = new int[0];
            m_Triangles = new int[0];
            m_Points = new Vector2[0];
            return true;
        }

        // Tops face up, so clockwise from above (y up on screen).
        for (int i = 0; i + 2 < m_Triangles.Length; i += 3)
        {
            Vector2 u = points[m_Triangles[i + 1]] - points[m_Triangles[i]];
            Vector2 w = points[m_Triangles[i + 2]] - points[m_Triangles[i]];
            if (u.x * w.y - u.y * w.x > 0f)
                (m_Triangles[i + 1], m_Triangles[i + 2]) = (m_Triangles[i + 2], m_Triangles[i + 1]);
        }

        m_Points = points;
        return true;
    }

    // Reads a walls file: FormatException, naming the file and the problem, for a short, foreign or inconsistent
    // one (rings that don't add up to the points, a triangle index past them).
    public static void Parse(byte[] bytes, string name, out int[] rings, out Vector2[] points, out int[] triangles)
    {
        var reader = new DataReader(bytes, name);
        reader.Header(Magic, Version);
        reader.UInt16();
        int pointCount = reader.Int32(), ringCount = reader.Count("rings", 4), indexCount = reader.Int32();
        rings = new int[ringCount];
        long total = 0;
        for (int r = 0; r < ringCount; r++)
            total += rings[r] = reader.Int32();
        if (pointCount < 0 || total != pointCount)
            throw reader.Fail($"the rings hold {total} points but the file says {pointCount}");
        points = reader.Points(pointCount, 0.01f);
        if (indexCount < 0 || indexCount % 3 != 0)
            throw reader.Fail($"{indexCount} triangle indices, not a whole number of triangles");
        triangles = new int[indexCount];
        for (int i = 0; i < indexCount; i++)
        {
            int v = reader.Int32();
            if (v < 0 || v >= pointCount)
                throw reader.Fail($"triangle index {v} is past the {pointCount} points");
            triangles[i] = v;
        }
        reader.End();
    }

    static float SignedArea(Vector2[] points, int start, int count)
    {
        float sum = 0f;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 a = points[start + j], b = points[start + i];
            sum += a.x * b.y - b.x * a.y;
        }
        return sum * 0.5f;
    }

    static Color Shade(Color c, float k) =>
        new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (!Read() || m_Points.Length == 0)
            return;

        float ppm = pixelsPerMeter;
        float up = -height * ppm;

        // Which way is outward: the biggest ring is an outside edge, and every ring runs the same way round its wall.
        float biggest = 0f, outward = 1f;
        for (int r = 0, start = 0; r < m_Rings.Length; start += m_Rings[r], r++)
        {
            float area = SignedArea(m_Points, start, m_Rings[r]);
            if (Mathf.Abs(area) > biggest)
            {
                biggest = Mathf.Abs(area);
                outward = Mathf.Sign(area);
            }
        }

        // Sides first, so the tops cover the ones facing away.
        for (int r = 0, start = 0; r < m_Rings.Length; start += m_Rings[r], r++)
        {
            int count = m_Rings[r];
            for (int i = 0; i < count; i++)
            {
                Vector2 a = m_Points[start + i], b = m_Points[start + (i + 1) % count];
                Vector2 along = b - a;
                if (along.sqrMagnitude < 1e-6f)
                    continue;

                Vector2 normal = new Vector2(along.y, -along.x).normalized * outward;
                float light = Mathf.Lerp(0.82f, 1.06f, Vector2.Dot(normal, Light) * 0.5f + 0.5f);
                Color lit = Shade(sideColor, light);
                Color foot = Shade(sideColor, light * 0.86f);

                int v = vh.currentVertCount;
                vh.AddVert(new Vector3(a.x * ppm, a.y * ppm, 0f), foot, Vector4.zero);
                vh.AddVert(new Vector3(b.x * ppm, b.y * ppm, 0f), foot, Vector4.zero);
                vh.AddVert(new Vector3(b.x * ppm, b.y * ppm, up), lit, Vector4.zero);
                vh.AddVert(new Vector3(a.x * ppm, a.y * ppm, up), lit, Vector4.zero);

                // Front faces are the ones that wind clockwise on screen (Map Walls culls the rest): a side whose
                // outside faces you runs foot a, foot b, top b anticlockwise unless the ring goes the other way.
                if (outward > 0f)
                {
                    vh.AddTriangle(v, v + 2, v + 1);
                    vh.AddTriangle(v, v + 3, v + 2);
                }
                else
                {
                    vh.AddTriangle(v, v + 1, v + 2);
                    vh.AddTriangle(v, v + 2, v + 3);
                }
            }
        }

        // Tops.
        int first = vh.currentVertCount;
        Color top = color;
        foreach (Vector2 p in m_Points)
            vh.AddVert(new Vector3(p.x * ppm, p.y * ppm, up), top, Vector4.zero);
        for (int i = 0; i + 2 < m_Triangles.Length; i += 3)
            vh.AddTriangle(first + m_Triangles[i], first + m_Triangles[i + 1], first + m_Triangles[i + 2]);
    }
}
