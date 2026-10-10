using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// A polyline for the UI, in the RectTransform's local units, drawn as round dots `spacing` apart along the
// line (a walking route). v7 also had a solid mode that nothing used; it was left out.
[RequireComponent(typeof(CanvasRenderer))]
public class UILine : MaskableGraphic
{
    [Tooltip("Dot diameter, in local units.")]
    public float width = 12f;
    [Tooltip("Distance between dot centres, in local units.")]
    public float spacing = 24f;

    const int DiscSides = 16;

    readonly List<Vector2> m_Points = new List<Vector2>();
    float m_DotOffset;

    // The line's points (null clears it). dotOffset: how far along the whole route the first point is, so the
    // dots stay put when the start is cut off.
    public void SetPoints(IList<Vector2> points, float dotOffset = 0f)
    {
        m_Points.Clear();
        if (points != null)
            m_Points.AddRange(points);
        m_DotOffset = dotOffset;
        SetVerticesDirty();
    }

    public void SetWidth(float value, float dotSpacing = 0f)
    {
        width = value;
        if (dotSpacing > 0f)
            spacing = dotSpacing;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (m_Points.Count < 2 || spacing <= 0f)
            return;

        Color32 c = color;
        float r = width * 0.5f;
        float next = Mathf.Repeat(-m_DotOffset, spacing);   // distance from this segment's start to the next dot
        for (int i = 0; i + 1 < m_Points.Count; i++)
        {
            Vector2 a = m_Points[i], b = m_Points[i + 1];
            float length = Vector2.Distance(a, b);
            while (next <= length)
            {
                Disc(vh, Vector2.Lerp(a, b, length > 0f ? next / length : 0f), r, c);
                next += spacing;
            }
            next -= length;                                  // carry the remainder into the next segment
        }
    }

    // A 16-sided fan: a centre and 17 rim points, the last repeating the first.
    static void Disc(VertexHelper vh, Vector2 center, float radius, Color32 c)
    {
        int middle = vh.currentVertCount;
        vh.AddVert(center, c, Vector4.zero);
        for (int k = 0; k <= DiscSides; k++)
        {
            float angle = k * Mathf.PI * 2f / DiscSides;
            vh.AddVert(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, c, Vector4.zero);
        }
        for (int k = 0; k < DiscSides; k++)
            vh.AddTriangle(middle, middle + k + 2, middle + k + 1);
    }
}
