using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// The campus buildings standing up as plain blocks on the tilted map. The UI has no depth buffer, so everything is
// drawn back to front, and the floor plan has to show between the buildings behind it and those in front: the map
// has three of these, one per Part, each on its own Canvas with the "Map Walls" material (which culls back faces).
// Plan metres x pixelsPerMeter, like CampusGround; local -z is up, because the map's rotator leans back about x.
[RequireComponent(typeof(CanvasRenderer))]
public class CampusBuildings : MaskableGraphic
{
    public enum Part { Behind, Indoor, Front }

    [Tooltip("Which buildings this graphic draws: those behind the floor's building, the floor's building itself, or those in front.")]
    public Part part;

    // Where the light comes from, in plan directions (the plan's upper left). Walls facing it are lighter.
    static readonly Vector2 Light = new Vector2(-0.45f, 0.89f);

    CampusData m_Campus;
    float m_PixelsPerMeter = 10f;
    float m_Rotation = float.NaN;
    Theme m_Theme;
    readonly List<CampusData.Building> m_Kept = new List<CampusData.Building>();
    readonly List<(Vector2 a, Vector2 b, float depth)> m_Sides = new List<(Vector2, Vector2, float)>();

    public void SetCampus(CampusData campus, float pixelsPerMeter)
    {
        m_Campus = campus;
        m_PixelsPerMeter = pixelsPerMeter;
        if (campus != null)
            CampusGround.Place(rectTransform, campus.extent, pixelsPerMeter);
        m_Theme = Theme.Current;
        SetVerticesDirty();
    }

    // The draw order depends on which way is into the screen, so turning the map needs a re-sort. That takes a few
    // milliseconds, so it waits for a big turn, or for the map to settle after a small one.
    public void SetView(float rotation, bool settled)
    {
        float turned = float.IsNaN(m_Rotation) ? 360f : Mathf.Abs(Mathf.DeltaAngle(rotation, m_Rotation));
        if (turned > 30f || (settled && turned > 3f))
        {
            m_Rotation = rotation;
            SetVerticesDirty();
        }
    }

    // After a day/night switch.
    public void Restyle()
    {
        if (m_Theme == Theme.Current)
            return;
        m_Theme = Theme.Current;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (m_Campus == null)
            return;
        Theme theme = Theme.Current;
        m_Theme = theme;
        Color top = theme.buildingTop, side = theme.buildingSide;
        if (part == Part.Indoor)
        {
            // The one you can go into, tinted like a selection.
            top = Color.Lerp(top, theme.primary, 0.22f);
            side = Color.Lerp(side, theme.primary, 0.3f);
        }

        // Depth along "into the screen": the same as the y of FloorMap's Turn. Bigger is farther away.
        float r = float.IsNaN(m_Rotation) ? 0f : m_Rotation * Mathf.Deg2Rad;
        var into = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
        CampusData.Building indoor = m_Campus.indoor;
        float indoorDepth = indoor != null ? Vector2.Dot(indoor.Spot, into) : float.PositiveInfinity;

        m_Kept.Clear();
        foreach (CampusData.Building building in m_Campus.buildings)
        {
            Part its = building == indoor ? Part.Indoor
                : Vector2.Dot(building.Spot, into) > indoorDepth ? Part.Behind
                : Part.Front;
            if (its == part)
                m_Kept.Add(building);
        }
        // Farthest first.
        m_Kept.Sort((a, b) => Vector2.Dot(b.Spot, into).CompareTo(Vector2.Dot(a.Spot, into)));

        foreach (CampusData.Building building in m_Kept)
            AddBuilding(vh, building, into, top, side);
    }

    void AddBuilding(VertexHelper vh, CampusData.Building building, Vector2 into, Color top, Color side)
    {
        float ppm = m_PixelsPerMeter;
        float up = -building.height * ppm;
        Vector2[] points = building.points;

        // The walls that face the viewer, farthest first. Rings keep the outside on their right, so (b - a) turned
        // clockwise is the outward normal; walls facing away are hidden by the roof anyway.
        m_Sides.Clear();
        for (int ring = 0, start = 0; ring < building.rings.Length; start += building.rings[ring], ring++)
        {
            int count = building.rings[ring];
            for (int i = 0; i < count; i++)
            {
                Vector2 a = points[start + i], b = points[start + (i + 1) % count];
                var normal = new Vector2(b.y - a.y, a.x - b.x);
                if (Vector2.Dot(normal, into) >= 0f || normal.sqrMagnitude < 1e-4f)
                    continue;
                m_Sides.Add((a, b, Vector2.Dot(a + b, into)));
            }
        }
        m_Sides.Sort((x, y) => y.depth.CompareTo(x.depth));

        foreach (var (a, b, _) in m_Sides)
        {
            Vector2 normal = new Vector2(b.y - a.y, a.x - b.x).normalized;
            float light = Mathf.Lerp(0.8f, 1.05f, Vector2.Dot(normal, Light) * 0.5f + 0.5f);
            Color lit = Shade(side, light);           // top edge of the wall
            Color foot = Shade(side, light * 0.84f);  // bottom edge, a bit darker
            int v = vh.currentVertCount;
            vh.AddVert(new Vector3(a.x * ppm, a.y * ppm, 0f), foot, Vector4.zero);
            vh.AddVert(new Vector3(b.x * ppm, b.y * ppm, 0f), foot, Vector4.zero);
            vh.AddVert(new Vector3(b.x * ppm, b.y * ppm, up), lit, Vector4.zero);
            vh.AddVert(new Vector3(a.x * ppm, a.y * ppm, up), lit, Vector4.zero);
            vh.AddTriangle(v, v + 2, v + 1);   // clockwise on screen from the front, as Map Walls wants
            vh.AddTriangle(v, v + 3, v + 2);
        }

        // The roof last, so it covers the tops of the walls.
        int first = vh.currentVertCount;
        foreach (Vector2 p in points)
            vh.AddVert(new Vector3(p.x * ppm, p.y * ppm, up), top, Vector4.zero);
        ushort[] roof = building.roof;
        for (int i = 0; i + 2 < roof.Length; i += 3)
            vh.AddTriangle(first + roof[i], first + roof[i + 1], first + roof[i + 2]);
    }

    static Color Shade(Color c, float k)
    {
        return new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);
    }
}
