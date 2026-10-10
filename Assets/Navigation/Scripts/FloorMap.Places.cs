using System;
using UnityEngine;

// The place map (Data/*-places.bytes, little-endian: "DWPL", uint16 version 1, uint16 width and height, uint32
// FloorPlan.PlaceIdHash of the places it was made for, then a byte per cell, top row first, each the index + 1 of
// the place there or 0), and the selected place's highlight drawn from it.
public partial class FloorMap
{
    const string PlaceMapMagic = "DWPL";
    const int PlaceMapVersion = 1;
    int m_MapWidth, m_MapHeight;
    byte[] m_Places;
    Vector2Int[] m_PixelMin, m_PixelMax;   // each place's cells on the place map; max (-1, -1) for none
    Rect[] m_Bounds;
    Vector2[] m_Anchors;

    float[] m_Mask, m_Blur;
    byte[] m_HighlightBytes;
    Texture2D m_HighlightTexture;
    RectInt m_Lit;

    void ReadPlaceMap()
    {
        m_Places = null;
        if (placeMap == null)
            return;
        m_Places = ReadPlaceGrid(placeMap.bytes, placeMap.name, m_Plan, out m_MapWidth, out m_MapHeight);
    }

    // The cells of a place map made for this plan's places (FormatException, naming the file and the problem,
    // for a short, foreign or stale one: a grid for another place list would light up the wrong rooms).
    public static byte[] ReadPlaceGrid(byte[] bytes, string name, FloorPlan plan, out int width, out int height)
    {
        var reader = new DataReader(bytes, name);
        reader.Header(PlaceMapMagic, PlaceMapVersion);
        width = reader.UInt16();
        height = reader.UInt16();
        uint hash = reader.UInt32();
        if (plan != null && hash != plan.PlaceIdHash())
            throw reader.Fail("made for a different list of places than the floor plan's; run Tools/ba1-floor1/make_data.py");
        byte[] cells = reader.Bytes(width * height);
        reader.End();
        int count = plan?.places.Count ?? 255;
        foreach (byte v in cells)
        {
            if (v > count)
                throw reader.Fail($"a cell names place {v}, but the plan has {count}");
        }
        return cells;
    }

    // The place at a plan point: null in hallways, outside, and in rooms nobody navigates to.
    public PlanPlace PlaceAt(Vector2 p)
    {
        if (m_Plan == null || m_Places == null)
            return null;
        Vector2 size = PlanSize;
        int x = Mathf.FloorToInt((p.x - m_Plan.image.left) / size.x * m_MapWidth);
        int y = Mathf.FloorToInt((m_Plan.image.top - p.y) / size.y * m_MapHeight);
        if (x < 0 || y < 0 || x >= m_MapWidth || y >= m_MapHeight)
            return null;
        int v = m_Places[y * m_MapWidth + x];
        return v >= 1 && v <= m_Plan.places.Count ? m_Plan.places[v - 1] : null;
    }

    // Just inside the door, out of the hallway.
    Vector2 BadgeSpot(PlanDoor door) => m_Plan.NodePosition(door.node) + door.Direction.normalized * 1.2f;

    // Each place's extent (its floor area and its doors) and where its pin goes: a room's number, or the icon by
    // its door.
    void FindPlaces()
    {
        int count = m_Plan.places.Count;
        m_PixelMin = new Vector2Int[count];
        m_PixelMax = new Vector2Int[count];
        m_Bounds = new Rect[count];
        m_Anchors = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            m_PixelMin[i] = new Vector2Int(int.MaxValue, int.MaxValue);
            m_PixelMax[i] = new Vector2Int(-1, -1);
        }

        if (m_Places != null)
            for (int y = 0; y < m_MapHeight; y++)
                for (int x = 0; x < m_MapWidth; x++)
                {
                    int v = m_Places[y * m_MapWidth + x];
                    if (v == 0 || v > count)
                        continue;
                    m_PixelMin[v - 1] = Vector2Int.Min(m_PixelMin[v - 1], new Vector2Int(x, y));
                    m_PixelMax[v - 1] = Vector2Int.Max(m_PixelMax[v - 1], new Vector2Int(x, y));
                }

        Vector2 size = PlanSize;
        Vector2 cell = m_MapWidth > 0 ? new Vector2(size.x / m_MapWidth, size.y / m_MapHeight) : Vector2.zero;
        float left = m_Plan.image.left, top = m_Plan.image.top;
        for (int i = 0; i < count; i++)
        {
            PlanPlace place = m_Plan.places[i];
            bool hasPixels = m_PixelMax[i].x >= 0;
            Vector2 lo = Vector2.positiveInfinity, hi = Vector2.negativeInfinity;
            if (hasPixels)
            {
                lo = new Vector2(left + m_PixelMin[i].x * cell.x, top - (m_PixelMax[i].y + 1) * cell.y);
                hi = new Vector2(left + (m_PixelMax[i].x + 1) * cell.x, top - m_PixelMin[i].y * cell.y);
            }
            foreach (PlanDoor door in place.doors)
            {
                Vector2 p = m_Plan.NodePosition(door.node);
                lo = Vector2.Min(lo, p);
                hi = Vector2.Max(hi, p);
            }
            if (!hasPixels && place.doors.Count == 0)
                lo = hi = PlanRect.center;   // nowhere to put it: not in the shipped plan
            m_Bounds[i] = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y);

            m_Anchors[i] = place.doors.Count > 0 ? BadgeSpot(place.doors[0]) : m_Bounds[i].center;
            if (place.kind == "room" && hasPixels)
                m_Anchors[i] = m_Bounds[i].center;
        }

        // A room's pin goes where its number is printed: the roomiest spot in it.
        var bestArea = new float[count];
        foreach (PlanLabel label in m_Plan.labels)
        {
            PlanPlace place = PlaceAt(label.Position);
            if (place == null || place.kind != "room")
                continue;
            int i = m_Plan.places.IndexOf(place);
            if (label.area > bestArea[i])
            {
                bestArea[i] = label.area;
                m_Anchors[i] = label.Position;
            }
        }
    }

    // A place's floor area and doors, in plan metres.
    public Rect Bounds(PlanPlace place)
    {
        int i = m_Plan != null ? m_Plan.places.IndexOf(place) : -1;
        return i >= 0 ? m_Bounds[i] : new Rect();
    }

    // Where a place's pin goes.
    public Vector2 Anchor(PlanPlace place)
    {
        int i = m_Plan != null ? m_Plan.places.IndexOf(place) : -1;
        return i >= 0 ? m_Anchors[i] : Vector2.zero;
    }

    // Outlines and tints a place's floor area (if it has one on the place map); null clears it. Works only round
    // the place (and the last one, to clear it), in buffers kept between calls.
    public void Select(PlanPlace place)
    {
        if (m_Plan == null || m_Places == null || highlight == null)
            return;
        int w = m_MapWidth, h = m_MapHeight;
        if (m_Mask == null)
        {
            m_Mask = new float[w * h];
            m_Blur = new float[w * h];
            m_HighlightBytes = new byte[w * h];
        }

        // Texture rows go bottom to top; the place map's top to bottom.
        for (int y = m_Lit.yMin; y < m_Lit.yMax; y++)
            Array.Clear(m_HighlightBytes, (h - 1 - y) * w + m_Lit.xMin, m_Lit.width);
        m_Lit = new RectInt();

        int index = place != null ? m_Plan.places.IndexOf(place) : -1;
        bool any = index >= 0 && m_PixelMax[index].x >= 0;
        if (highlight.gameObject.activeSelf != any)
            highlight.gameObject.SetActive(any);
        if (!any)
            return;

        // The place and two pixels round it, as far as two blurs spread it.
        int x0 = Mathf.Max(m_PixelMin[index].x - 2, 0), y0 = Mathf.Max(m_PixelMin[index].y - 2, 0);
        int x1 = Mathf.Min(m_PixelMax[index].x + 3, w), y1 = Mathf.Min(m_PixelMax[index].y + 3, h);
        var area = new RectInt(x0, y0, x1 - x0, y1 - y0);

        byte value = (byte)(index + 1);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                m_Mask[y * w + x] = m_Places[y * w + x] == value ? 1f : 0f;

        // Blurred, so the shader's threshold at one half gives a smooth outline instead of steps.
        Blur(area);
        Blur(area);

        for (int y = y0; y < y1; y++)
        {
            int row = (h - 1 - y) * w;
            for (int x = x0; x < x1; x++)
                m_HighlightBytes[row + x] = (byte)(m_Mask[y * w + x] * 255f + 0.5f);
        }
        m_Lit = area;

        if (m_HighlightTexture == null)
            m_HighlightTexture = new Texture2D(w, h, TextureFormat.Alpha8, false)
            {
                name = "Map Highlight",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        m_HighlightTexture.SetPixelData(m_HighlightBytes, 0);
        m_HighlightTexture.Apply(false);
        highlight.texture = m_HighlightTexture;
    }

    // A 3 x 3 box blur of the mask in `area`; outside it counts as empty.
    void Blur(RectInt area)
    {
        int w = m_MapWidth;
        for (int y = area.yMin; y < area.yMax; y++)
            for (int x = area.xMin; x < area.xMax; x++)
            {
                int i = y * w + x;
                float sum = m_Mask[i] + (x > area.xMin ? m_Mask[i - 1] : 0f) + (x < area.xMax - 1 ? m_Mask[i + 1] : 0f);
                m_Blur[i] = sum / 3f;
            }
        for (int y = area.yMin; y < area.yMax; y++)
            for (int x = area.xMin; x < area.xMax; x++)
            {
                int i = y * w + x;
                float sum = m_Blur[i] + (y > area.yMin ? m_Blur[i - w] : 0f) + (y < area.yMax - 1 ? m_Blur[i + w] : 0f);
                m_Mask[i] = sum / 3f;
            }
    }

    // Only one kind's icons, always shown (a filter chip is on); null shows them all again.
    public void ShowOnly(string kind)
    {
        m_Kind = kind;
        MarkDirty();
    }
}
