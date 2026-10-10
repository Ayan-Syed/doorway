using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// The campus round the floor: its ground and buildings, the buildings' names, and the blend from the campus
// into the floor as the map zooms in.
public partial class FloorMap
{
    CanvasGroup m_BasemapGroup, m_HighlightGroup, m_WallsGroup;

    // Zoomed in on the floor, its rooms showing, rather than out over the campus.
    public bool Indoors
    {
        get
        {
            if (m_Campus == null)
                return true;
            if (m_Indoor < 0.5f || m_Plan == null)
                return false;
            float margin = Window.rect.width * 0.3f / (pixelsPerMeter * m_Zoom);
            Rect r = PlanRect;
            return m_Center.x > r.xMin - margin && m_Center.x < r.xMax + margin
                && m_Center.y > r.yMin - margin && m_Center.y < r.yMax + margin;
        }
    }

    void SetCampus()
    {
        m_Campus = CampusData.Load(campusGeometry, campusInfo);
        if (m_Campus == null)
            return;
        if (campusGround != null)
            campusGround.SetCampus(m_Campus, pixelsPerMeter);
        foreach (CampusBuildings buildings in campusBuildings)
            if (buildings != null)
                buildings.SetCampus(m_Campus, pixelsPerMeter);

        if (buildingLabelTemplate == null)
            return;
        buildingLabelTemplate.gameObject.SetActive(false);
        // Made when first needed: most are never on screen at a size that shows them.
        foreach (CampusData.Building building in m_Campus.buildings)
            if (Drawable(building.name))
                m_BuildingLabels.Add(new Marker { position = building.Spot, area = building.area, building = building });
        // The floor's building first, then the biggest: labels claim their space first come, first served.
        m_BuildingLabels = m_BuildingLabels.OrderBy(m => m.building.indoor ? 0 : 1).ThenByDescending(m => m.area).ToList();
    }

    // Names the map's font can draw: Latin ones (not the fraternity houses' Greek letters).
    public static bool Drawable(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (char c in name)
            if (c > 'ɏ')
                return false;
        return true;
    }

    // A building's name on a line or two; a long one only up to its first comma.
    static string LabelText(string name)
    {
        if (name.Length > 28)
        {
            int comma = name.IndexOf(", ", StringComparison.Ordinal);
            if (comma >= 0)
                name = name.Substring(0, comma);
        }
        if (name.Length <= 18)
            return name;

        int middle = name.Length / 2, best = -1;
        for (int i = 0; i < name.Length; i++)
            if (name[i] == ' ' && (best < 0 || Mathf.Abs(i - middle) < Mathf.Abs(best - middle)))
                best = i;
        return best < 0 ? name : name.Substring(0, best) + "\n" + name.Substring(best + 1);
    }

    void MakeBuildingLabel(Marker label)
    {
        Text text = Instantiate(buildingLabelTemplate, buildingLabelTemplate.transform.parent, false);
        text.name = label.building.name;
        text.text = LabelText(label.building.name);
        text.gameObject.SetActive(true);
        label.rect = text.rectTransform;
        label.group = text.GetComponent<CanvasGroup>();
        label.group.alpha = 0f;
        label.group.blocksRaycasts = false;
        label.alpha = 0f;
        label.size = new Vector2(text.preferredWidth + 8f, text.preferredHeight);
        // Off until Fade switches it on.
        text.gameObject.SetActive(false);
        StyleBuildingLabel(label);
    }

    void StyleBuildingLabel(Marker label)
    {
        ThemeColors.CopyColors(buildingLabelTemplate, label.rect);
        if (label.building.indoor)
            label.rect.GetComponent<Text>().color = Theme.Current.primary;
    }

    // How tall a building is drawn: the floor's own sinks away as the floor comes in.
    float Height(CampusData.Building building) =>
        building.indoor ? building.height * (1f - Mathf.Clamp01(m_Indoor)) : building.height;

    // The building drawn under a tap, the nearest if two are: its roof, its middle or its foot.
    CampusData.Building BuildingAt(Vector2 at)
    {
        float lift = pixelsPerMeter * m_Zoom * Mathf.Sin(m_Tilt * Mathf.Deg2Rad);
        CampusData.Building best = null;
        float bestDepth = float.PositiveInfinity;
        foreach (CampusData.Building building in m_Campus.buildings)
        {
            // Lower on the screen is nearer the viewer on a tilted map.
            float depth = Turn(building.Spot - m_Center, m_Rotation).y;
            if (depth >= bestDepth)
                continue;
            float up = Height(building) * lift;
            for (int k = 0; k <= 2; k++)
                if (building.Contains(ToPlan(at - new Vector2(0f, up * k * 0.5f))))
                {
                    best = building;
                    bestDepth = depth;
                    break;
                }
        }
        return best;
    }

    // How far in to the floor: 0 out over the campus, the floor's building standing with its roof on; 1 on the
    // floor, the building sunk away.
    void ShowFloor(float indoor)
    {
        m_Indoor = indoor;
        if (basemap != null)
        {
            Group(basemap, ref m_BasemapGroup).alpha = indoor;
            // Its big texture costs fill rate even when invisible.
            basemap.enabled = indoor > 0.01f;
        }
        if (highlight != null)
            Group(highlight, ref m_HighlightGroup).alpha = indoor;
        if (walls != null)
            Group(walls, ref m_WallsGroup).alpha = indoor;

        foreach (CampusBuildings buildings in campusBuildings)
        {
            if (buildings == null || buildings.part != CampusBuildings.Part.Indoor)
                continue;
            bool on = indoor < 0.99f;
            if (buildings.gameObject.activeSelf != on)
                buildings.gameObject.SetActive(on);
            Vector3 scale = buildings.transform.localScale;
            scale.z = Mathf.Max(0.001f, 1f - indoor);   // the roof sinks toward the floor
            buildings.transform.localScale = scale;
            buildings.canvasRenderer.SetAlpha(1f - indoor);
        }
    }

    // A CanvasGroup to fade a graphic with, added when missing; it never takes taps.
    static CanvasGroup Group(Component target, ref CanvasGroup group)
    {
        if (group != null)
            return group;
        group = target.GetComponent<CanvasGroup>();
        if (group == null)
        {
            group = target.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
        }
        return group;
    }
}
