using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// What the map looks at (framing, following), and what it draws on top: the route, the pin and the user's puck.
public partial class FloorMap
{
    Vector2 m_PinAt;
    Vector2 m_PuckAt;
    float m_PuckHeading, m_PuckAccuracy;

    static readonly List<Vector2> s_Points = new List<Vector2>();

    // Every view goes here. In the editor every view is instant.
    void GoTo(Vector2 center, float zoom, float rotation, bool instant)
    {
        m_Following = false;
        m_TargetCenter = center;
        m_TargetZoom = zoom;
        m_TargetRotation = rotation;
        m_Velocity = Vector2.zero;
        m_Animating = !instant && Application.isPlaying;
        if (m_Animating)
            return;
        m_Center = center;
        m_Zoom = zoom;
        m_Rotation = rotation;
        Layout();
    }

    // The whole floor, the right way up.
    public void ShowAll(bool instant)
    {
        if (m_Plan == null)
            return;
        Rect r = PlanRect;
        FramePoints(new[] { r.min, r.max, new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMin) }, 0f, 0f, instant);
    }

    // The campus, `span` metres across, plan-up, in the part of the window the insets leave.
    public void ShowCampus(bool instant, float span = CampusSpan)
    {
        if (m_Campus == null)
        {
            ShowAll(instant);
            return;
        }
        float zoom = Mathf.Clamp(Window.rect.width / (span * pixelsPerMeter), minZoom, maxZoom);
        var shift = new Vector2(0f, (bottomInset - topInset) * 0.5f / Squash(m_TargetTilt));
        GoTo(m_Campus.middle - shift / (pixelsPerMeter * zoom), zoom, 0f, instant);
    }

    public void ShowBuilding(CampusData.Building building, bool instant)
    {
        FramePoints(new[] { building.bounds.min, building.bounds.max }, 0f, 60f, instant, 96f);
    }

    // A place and what's around it, the right way up.
    public void ShowPlace(PlanPlace place, bool instant)
    {
        Rect r = Bounds(place);
        FramePoints(new[] { r.min, r.max }, 0f, 26f, instant);
    }

    // Plan points, turned by `rotation`, as large as fits but at least `minSpan` metres across, `margin` pixels in
    // from the edges (and clear of the insets).
    public void FramePoints(IEnumerable<Vector2> points, float rotation, float minSpan, bool instant, float margin = Margin)
    {
        Vector2 lo = Vector2.positiveInfinity, hi = Vector2.negativeInfinity;
        foreach (Vector2 p in points)
        {
            Vector2 q = Turn(p, rotation);
            lo = Vector2.Min(lo, q);
            hi = Vector2.Max(hi, q);
        }
        if (lo.x > hi.x)
            return;   // no points: nothing to frame

        Vector2 size = Window.rect.size;
        Vector2 extent = Vector2.Max(hi - lo, new Vector2(minSpan, minSpan)) * pixelsPerMeter;
        var room = new Vector2(size.x - 2f * margin, size.y - topInset - bottomInset - 2f * margin);
        float squash = Squash(m_TargetTilt);   // the tilt it will have, not the current one
        float zoom = Mathf.Clamp(Mathf.Min(room.x / Mathf.Max(extent.x, 1f), room.y / Mathf.Max(extent.y * squash, 1f)),
            minZoom, maxZoom);
        Vector2 middle = Turn((lo + hi) * 0.5f, -rotation);   // back to plan space
        var shift = new Vector2(0f, (bottomInset - topInset) * 0.5f / squash);   // the middle of the uncovered strip
        GoTo(middle - Turn(shift, -rotation) / (pixelsPerMeter * zoom), zoom, rotation, instant);
    }

    // A route from start to end, turned so `upHeading` points up. Room for the dot's beam and the pin's head past
    // the route's ends.
    public void Overview(Route route, Vector2 start, float upHeading, bool instant)
    {
        DrawRoute(route, 0f);
        SetPuck(start, upHeading, 0f, true);
        var points = route != null ? new List<Vector2>(route.points) { start } : new List<Vector2> { start };
        FramePoints(points, upHeading, 18f, instant, 120f);
    }

    // Heading-up around the user; the walked part of the route is cut off. Call it every frame.
    // accuracy: the halo round the user, in metres (0 for none).
    public void Follow(Route route, float progress, Vector2 position, float heading, bool instant, float accuracy = 0f)
    {
        if (!m_Following)
        {
            m_Following = true;
            instant = true;
        }
        // Re-cut only after 0.3 m, so the dots aren't re-meshed every frame.
        if (route != m_Route || Mathf.Abs(progress - m_RouteFrom) > 0.3f)
            DrawRoute(route, progress);
        SetPuck(position, heading, accuracy, true);

        float dt = Time.unscaledDeltaTime;
        m_Rotation = Mathf.LerpAngle(m_Rotation, heading, instant ? 1f : 1f - Mathf.Exp(-dt * 8f));
        m_Zoom = followZoom;
        // The middle is ahead of the user, the way they face.
        Vector2 ahead = Turn(new Vector2(0f, followAhead * Window.rect.height / Squash(m_Tilt)), -m_Rotation)
            / (pixelsPerMeter * m_Zoom);
        m_Center = Vector2.Lerp(m_Center, position + ahead, instant ? 1f : 1f - Mathf.Exp(-dt * 12f));
        m_TargetCenter = m_Center;
        m_TargetZoom = m_Zoom;
        m_TargetRotation = m_Rotation;
        m_Animating = false;
        Layout();
    }

    // The walking route as dots; null hides it.
    public void ShowRoute(Route route)
    {
        DrawRoute(route, 0f);
    }

    // The route from `from` metres on, in content units (plan metres x pixelsPerMeter).
    void DrawRoute(Route route, float from)
    {
        m_Route = route;
        m_RouteFrom = from;
        bool on = route != null;
        foreach (UILine line in new[] { routeCasing, routeLine })
            if (line != null && line.gameObject.activeSelf != on)
                line.gameObject.SetActive(on);
        if (!on)
            return;

        float ppm = pixelsPerMeter;
        s_Points.Clear();
        s_Points.Add(route.PointAt(from) * ppm);
        for (int i = 1; i < route.points.Count; i++)
            if (route.DistanceAtPoint(i) > from)
                s_Points.Add(route.points[i] * ppm);

        // Dots counted from the route's start, so they stay put as the walked part is cut off.
        if (routeLine != null)
            routeLine.SetPoints(s_Points, from * ppm);
        if (routeCasing != null)
            routeCasing.SetPoints(s_Points, from * ppm);
    }

    // The selected place's pin at a plan point, with its name beside it (none for null).
    public void ShowPin(Vector2 at, string label)
    {
        m_PinAt = at;
        pin.SetActive(true);
        bool named = !string.IsNullOrEmpty(label);
        pinLabel.text = named ? label : "";
        pinLabel.gameObject.SetActive(named);
        if (named)
        {
            RectTransform rect = pinLabel.rectTransform;
            rect.sizeDelta = new Vector2(pinLabel.preferredWidth + 8f, rect.sizeDelta.y);
        }
        Layout();
    }

    public void HidePin()
    {
        pin.SetActive(false);
        Layout();
    }

    // The user's dot, its beam pointing `heading` (degrees clockwise from plan up), or no beam (a GPS fix has no
    // heading). accuracy: the radius, in metres, of the disc round it.
    public void ShowPuck(Vector2 at, float heading, float accuracy = 0f, bool beam = true)
    {
        SetPuck(at, heading, accuracy, beam);
        Layout();
    }

    void SetPuck(Vector2 at, float heading, float accuracy, bool beam)
    {
        m_PuckAt = at;
        m_PuckHeading = heading;
        m_PuckAccuracy = accuracy;
        if (!puck.activeSelf)
            puck.SetActive(true);
        if (puckCone != null && puckCone.activeSelf != beam)
            puckCone.SetActive(beam);
    }

    public void HidePuck()
    {
        puck.SetActive(false);
        Layout();
    }

    // The marker's colour (the dot's fill and the disc under it) and its "?" badge: how sure the position is.
    // Writes only what changed, so it can be called every frame (a day/night switch repaints the dot its usual way).
    public void TintPuck(Color color, bool unknown)
    {
        if (puckFill != null && puckFill.color != color)
            puckFill.color = color;
        if (puckAccuracy != null && puckAccuracy.TryGetComponent(out Image disc))
        {
            Color tint = color;
            tint.a = 0.15f;
            if (disc.color != tint)
                disc.color = tint;
        }
        if (puckUnknown != null && puckUnknown.activeSelf != unknown)
            puckUnknown.SetActive(unknown);
    }

    public void ShowPuckCheck(bool on)
    {
        if (puckCheck != null && puckCheck.activeSelf != on)
            puckCheck.SetActive(on);
    }

    // Whether a plan point is on screen.
    public bool Shows(Vector2 point)
    {
        Vector2 at = ToWindow(point), half = Window.rect.size * 0.5f;
        return Mathf.Abs(at.x) < half.x && Mathf.Abs(at.y) < half.y;
    }

    // The user's marker: an emoji on a disc, or the blue dot for null.
    public void SetMarker(Sprite emoji)
    {
        bool avatar = emoji != null;
        puckAvatar.SetActive(avatar);
        puckDot.SetActive(!avatar);
        if (avatar && puckEmoji != null)
            puckEmoji.sprite = emoji;
        MarkDirty();
    }

    // Leans the floor back by `degrees` (0 for flat, straight down), the walls standing up off it.
    public void SetTilt(float degrees, bool instant)
    {
        m_TargetTilt = degrees;
        if (instant || !Application.isPlaying)
            m_Tilt = degrees;
        MarkDirty();
    }

    // Lays out on the next frame; in the editor (no Update) right away, so a built scene previews right.
    void MarkDirty()
    {
        m_Dirty = true;
        if (!Application.isPlaying)
            Layout();
    }
}
