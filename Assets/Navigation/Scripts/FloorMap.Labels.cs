using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Layout: the map's transforms, the indoor fade, and which room numbers, icons and building names show. Things
// that must show claim their space first; then icons; then room numbers, biggest rooms first; then building
// names. Earlier claims win: that greedy pass is the whole decluttering scheme.
public partial class FloorMap
{
    // Something drawn over the map at a plan point, staying upright: a room number, a place icon or a building's
    // name. It fades toward `shown`.
    class Marker
    {
        public RectTransform rect;    // null for a building name not made yet
        public CanvasGroup group;
        public Vector2 position;      // plan metres
        public Vector2 size;          // window units
        public float area;            // m², for who claims first and who fits
        public PlanPlace place;
        public CampusData.Building building;
        public bool shown;
        public float alpha;
    }

    List<Marker> m_Labels = new List<Marker>();
    List<Marker> m_Badges = new List<Marker>();
    List<Marker> m_BuildingLabels = new List<Marker>();
    readonly List<Rect> m_Taken = new List<Rect>();

    // Every marker starts invisible and fades in from Layout.
    static Marker AddMarker(RectTransform rect, Vector2 position)
    {
        rect.gameObject.SetActive(true);
        CanvasGroup group = rect.GetComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        return new Marker { rect = rect, group = group, position = position };
    }

    void Layout()
    {
        m_Dirty = false;
        if (m_Plan == null || rotator == null || content == null)
            return;

        // Turned in the floor's plane, then leaned back; walls scale up with the zoom like everything else.
        rotator.localRotation = Quaternion.Euler(m_Tilt, 0f, 0f) * Quaternion.Euler(0f, 0f, m_Rotation);
        rotator.localScale = new Vector3(m_Zoom, m_Zoom, m_Zoom);
        content.anchoredPosition = -m_Center * pixelsPerMeter;

        float indoor = m_Campus == null ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(indoorFrom, indoorTo, m_Zoom));
        if (indoor != m_Indoor)
            ShowFloor(indoor);

        // Walls only while the map is tilted and the floor shows.
        bool standing = m_Tilt > 0.5f && indoor > 0.01f;
        if (walls != null && walls.gameObject.activeSelf != standing)
            walls.gameObject.SetActive(standing);

        foreach (CampusBuildings buildings in campusBuildings)
            if (buildings != null)
                buildings.SetView(m_Rotation, !m_Animating);

        // The route lives in the zoomed content: dividing by the zoom keeps its dots one size on screen.
        if (!Mathf.Approximately(m_LinesZoom, m_Zoom))
        {
            m_LinesZoom = m_Zoom;
            if (routeLine != null)
                routeLine.SetWidth(routeWidth / m_Zoom, dotSpacing / m_Zoom);
            if (routeCasing != null)
                routeCasing.SetWidth(casingWidth / m_Zoom, dotSpacing / m_Zoom);
        }

        m_Taken.Clear();
        ClaimKeepClear();
        float squash = Squash(m_Tilt);
        LayoutPuck(squash);
        LayoutPin();

        Vector2 half = Window.rect.size * 0.5f + new Vector2(40f, 40f);   // fade in just before coming on screen
        float scale = pixelsPerMeter * m_Zoom;   // px per metre

        foreach (Marker badge in m_Badges)
        {
            Vector2 at = ToWindow(badge.position);
            badge.rect.anchoredPosition = at;
            bool kind = m_Indoor > 0.5f && (m_Kind == null || badge.place.kind == m_Kind);
            badge.shown = kind && OnScreen(at, half) && (m_Kind != null || Claim(at, badge.size));
        }

        foreach (Marker label in m_Labels)
        {
            Vector2 at = ToWindow(label.position);
            label.rect.anchoredPosition = at;
            bool fits = m_Indoor > 0.5f && label.area * scale * scale * squash >= labelRoom * label.size.x * label.size.y;
            label.shown = fits && OnScreen(at, half) && Claim(at, label.size);
        }

        LayoutBuildingLabels(scale, half);

        // In the editor nothing fades: the built scene shows each marker as it will settle.
        if (!Application.isPlaying)
        {
            Fade(m_Labels, 1f, false);
            Fade(m_Badges, 1f, false);
            Fade(m_BuildingLabels, 1f, true);
        }
    }

    // The map's credit and buttons: labels keep out from under them while they show.
    void ClaimKeepClear()
    {
        Vector2 middle = Window.rect.center;
        foreach (RectTransform rect in keepClear)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy)
                continue;
            // Not while it's faded out (its own group, or a panel it's on).
            CanvasGroup group = rect.GetComponent<CanvasGroup>();
            if (group != null && group.alpha < 0.5f)
                continue;
            Graphic graphic = rect.GetComponent<Graphic>();
            if (graphic != null && graphic.canvasRenderer.GetInheritedAlpha() < 0.5f)
                continue;
            rect.GetWorldCorners(s_Corners);
            Vector2 a = (Vector2)Window.InverseTransformPoint(s_Corners[0]) - middle;
            Vector2 b = (Vector2)Window.InverseTransformPoint(s_Corners[2]) - middle;
            m_Taken.Add(Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)));
        }
    }

    void LayoutPuck(float squash)
    {
        if (puck == null || !puck.activeSelf)
            return;
        Vector2 at = ToWindow(m_PuckAt);
        RectOf(puck).anchoredPosition = at;

        if (puckCone != null && puckCone.activeSelf)
        {
            // Where the heading points on the leaning floor.
            float h = m_PuckHeading * Mathf.Deg2Rad;
            Vector2 facing = Turn(new Vector2(Mathf.Sin(h), Mathf.Cos(h)), m_Rotation);
            puckCone.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(-facing.x, facing.y * squash) * Mathf.Rad2Deg);
        }

        GameObject marker = puckAvatar != null && puckAvatar.activeSelf ? puckAvatar : puckDot;
        Vector2 markerSize = marker != null ? RectOf(marker).sizeDelta : Vector2.zero;
        m_Taken.Add(Box(at, markerSize + new Vector2(6f, 6f)));

        // A disc on the floor, so squashed like it; not when it'd hide under the marker.
        if (puckAccuracy != null)
        {
            float across = m_PuckAccuracy * 2f * pixelsPerMeter * m_Zoom;
            bool disc = across > markerSize.x * 1.2f;
            if (puckAccuracy.gameObject.activeSelf != disc)
                puckAccuracy.gameObject.SetActive(disc);
            if (disc)
                puckAccuracy.localScale = new Vector3(across / 100f, across * squash / 100f, 1f);
        }
    }

    void LayoutPin()
    {
        if (pin == null || !pin.activeSelf)
            return;
        Vector2 at = ToWindow(m_PinAt);
        RectTransform rect = RectOf(pin);
        rect.anchoredPosition = at;
        // The pivot is the tip, so the pin stands above it.
        Vector2 size = rect.sizeDelta;
        m_Taken.Add(Box(at + new Vector2(0f, size.y * 0.5f), size));
        if (pinLabel != null && pinLabel.gameObject.activeSelf)
        {
            RectTransform label = pinLabel.rectTransform;
            Vector2 labelAt = at + (Vector2)label.localPosition + new Vector2(label.sizeDelta.x * 0.5f, 0f);
            m_Taken.Add(Box(labelAt, label.sizeDelta));
        }
    }

    // On the roofs, which a tilt lifts up the screen.
    void LayoutBuildingLabels(float scale, Vector2 half)
    {
        if (m_BuildingLabels.Count == 0)
            return;
        float lift = scale * Mathf.Sin(m_Tilt * Mathf.Deg2Rad);
        foreach (Marker label in m_BuildingLabels)
        {
            CampusData.Building building = label.building;
            Vector2 at = ToWindow(label.position) + new Vector2(0f, Height(building) * lift);
            // The floor's building is named only while you're outside it.
            bool fits = (building.indoor ? m_Indoor < 0.5f : Mathf.Sqrt(label.area) * scale >= BuildingLabelRoom)
                && OnScreen(at, half);
            if (!fits)
            {
                label.shown = false;
                if (label.rect != null)
                    label.rect.anchoredPosition = at;
                continue;
            }
            if (label.rect == null)
                MakeBuildingLabel(label);

            bool shown = Claim(at, label.size);
            if (!shown && building.indoor)
            {
                // The floor's building is the one to find: when something's on its spot (the puck, say), its name
                // goes just below that, or above.
                var step = new Vector2(0f, label.size.y * 0.5f + 60f);
                if (Claim(at - step, label.size))
                {
                    at -= step;
                    shown = true;
                }
                else if (Claim(at + step, label.size))
                {
                    at += step;
                    shown = true;
                }
            }
            label.rect.anchoredPosition = at;
            label.shown = shown;
        }
    }

    static bool OnScreen(Vector2 at, Vector2 half) => Mathf.Abs(at.x) < half.x && Mathf.Abs(at.y) < half.y;

    static Rect Box(Vector2 center, Vector2 size) => new Rect(center - size * 0.5f, size);

    // Takes the space round `center` if nothing has yet.
    bool Claim(Vector2 center, Vector2 size)
    {
        Rect box = Box(center, size + new Vector2(6f, 4f));
        foreach (Rect taken in m_Taken)
            if (taken.Overlaps(box))
                return false;
        m_Taken.Add(box);
        return true;
    }

    // Steps each marker's alpha toward shown or hidden. off: switched off once faded out, so the many that
    // aren't showing cost nothing.
    static void Fade(List<Marker> markers, float step, bool off)
    {
        foreach (Marker marker in markers)
        {
            if (marker.rect == null)
                continue;
            float target = marker.shown ? 1f : 0f;
            if (marker.alpha == target)
                continue;
            if (off && marker.shown && !marker.rect.gameObject.activeSelf)
                marker.rect.gameObject.SetActive(true);
            marker.alpha = Mathf.MoveTowards(marker.alpha, target, step);
            marker.group.alpha = marker.alpha;
            if (off && marker.alpha == 0f)
                marker.rect.gameObject.SetActive(false);
        }
    }
}
