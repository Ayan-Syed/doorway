using UnityEngine;
using UnityEngine.UI;

// The campus ground under the map: grass, trees, water, parking, paths and roads, in the theme's colours. Lives in
// the map's content like the floor's walls (plan metres x pixelsPerMeter, origin at plan (0, 0)). It sits on its own
// Canvas because it's one big mesh that only changes with the theme, so panning the map doesn't rebuild it.
[RequireComponent(typeof(CanvasRenderer))]
public class CampusGround : MaskableGraphic
{
    CampusData m_Campus;
    float m_PixelsPerMeter = 10f;
    Theme m_Theme;

    public void SetCampus(CampusData campus, float pixelsPerMeter)
    {
        m_Campus = campus;
        m_PixelsPerMeter = pixelsPerMeter;
        if (campus != null)
            Place(rectTransform, campus.extent, pixelsPerMeter);
        Restyle();
        SetVerticesDirty();
    }

    // Covers the whole campus with the pivot exactly at plan (0, 0), so a vertex at p x ppm lands in the right place
    // and the UI never culls the graphic as off screen.
    public static void Place(RectTransform rect, Rect extent, float pixelsPerMeter)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(-extent.xMin / extent.width, -extent.yMin / extent.height);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = extent.size * pixelsPerMeter;
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
        m_Theme = Theme.Current;
        Color[] layers = m_Theme.campusLayers;
        float ppm = m_PixelsPerMeter;
        // All chunks in one mesh (about 45k vertices now; uGUI tops out near 65k).
        foreach (CampusData.Chunk chunk in m_Campus.chunks)
        {
            Color color = layers[Mathf.Min(chunk.layer, layers.Length - 1)];
            int first = vh.currentVertCount;
            foreach (Vector2 p in chunk.points)
                vh.AddVert(new Vector3(p.x * ppm, p.y * ppm, 0f), color, Vector4.zero);
            ushort[] triangles = chunk.triangles;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
                vh.AddTriangle(first + triangles[i], first + triangles[i + 1], first + triangles[i + 2]);
        }
    }
}
