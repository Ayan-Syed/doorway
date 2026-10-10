using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

// Drag to pan (with a fling), pinch or scroll to zoom, tap a place or a building. No rotate or tilt gestures: the
// map turns only through Overview, Follow or FramePoints, and tilts only through the 3D setting.
public partial class FloorMap
{
    readonly Dictionary<int, Vector2> m_Pointers = new Dictionary<int, Vector2>();
    Vector2 m_Velocity;      // window units a second
    float m_LastDragTime;
    bool m_Pinched;          // two fingers were down at some point in this touch

    const float FlingPause = 0.08f;   // a drag that stopped this long before the finger lifted doesn't fling
    const float BadgeReach = 48f;     // a tap this near a shown icon picks it (a little generously)

    // A pointer in window units from the window's centre.
    Vector2 Local(PointerEventData e)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(Window, e.position, e.pressEventCamera, out Vector2 local);
        return local - Window.rect.center;
    }

    public void OnPointerDown(PointerEventData e)
    {
        if (!interactive)
            return;
        if (m_Pointers.Count == 0)
            m_Pinched = false;
        m_Pointers[e.pointerId] = Local(e);
        if (m_Pointers.Count > 1)
            m_Pinched = true;
        // A touch stops any fling, view animation or following.
        m_Velocity = Vector2.zero;
        m_Animating = false;
        m_Following = false;
    }

    public void OnPointerUp(PointerEventData e)
    {
        m_Pointers.Remove(e.pointerId);
    }

    // A finger that was down when the map was hidden never sends its up: forget them all, or the next one-finger
    // drag would be taken for a pinch and never fling.
    void OnDisable()
    {
        m_Pointers.Clear();
        m_Velocity = Vector2.zero;
        m_Pinched = false;
    }

    public void OnBeginDrag(PointerEventData e)
    {
        m_Velocity = Vector2.zero;
    }

    public void OnDrag(PointerEventData e)
    {
        if (!interactive || m_Plan == null)
            return;
        Vector2 now = Local(e);
        if (!m_Pointers.TryGetValue(e.pointerId, out Vector2 before))
            before = now;
        m_Pointers[e.pointerId] = now;

        if (m_Pointers.Count == 1)
        {
            Vector2 delta = now - before;
            Pan(delta);
            m_Velocity = Vector2.Lerp(m_Velocity, delta / Mathf.Max(Time.unscaledDeltaTime, 0.001f), 0.6f);
            m_LastDragTime = Time.unscaledTime;
            return;
        }

        // Two fingers: each finger's event moves only that finger, against the other's last known spot.
        Vector2 other = before;
        foreach (KeyValuePair<int, Vector2> pointer in m_Pointers)
            if (pointer.Key != e.pointerId)
            {
                other = pointer.Value;
                break;
            }
        Vector2 middleBefore = (before + other) * 0.5f, middleNow = (now + other) * 0.5f;
        float spanBefore = Vector2.Distance(before, other), spanNow = Vector2.Distance(now, other);
        if (spanBefore > 20f)
            ZoomAt(middleBefore, spanNow / spanBefore);
        Pan(middleNow - middleBefore);
        m_Velocity = Vector2.zero;
    }

    public void OnEndDrag(PointerEventData e)
    {
        bool fling = !m_Pinched && m_Pointers.Count == 0 && Time.unscaledTime - m_LastDragTime <= FlingPause;
        if (!fling)
            m_Velocity = Vector2.zero;
    }

    // The mouse wheel, in the editor and on a desktop.
    public void OnScroll(PointerEventData e)
    {
        if (!interactive || m_Plan == null)
            return;
        ZoomAt(Local(e), e.scrollDelta.y > 0f ? 1.2f : 1f / 1.2f);
    }

    // Moves the map with the finger; the middle stays over the campus (or the floor, without one).
    void Pan(Vector2 delta)
    {
        m_Center -= Turn(Unsquash(delta), -m_Rotation) / (pixelsPerMeter * m_Zoom);
        Rect limit = m_Campus != null ? m_Campus.extent : PlanRect;
        m_Center = new Vector2(Mathf.Clamp(m_Center.x, limit.xMin, limit.xMax), Mathf.Clamp(m_Center.y, limit.yMin, limit.yMax));
        m_Dirty = true;
    }

    // Zooms keeping what's under `at` where it is. The middle isn't clamped here; the next Pan does that.
    void ZoomAt(Vector2 at, float factor)
    {
        Vector2 under = ToPlan(at);
        m_Zoom = Mathf.Clamp(m_Zoom * factor, minZoom, maxZoom);
        m_Center = under - Turn(Unsquash(at), -m_Rotation) / (pixelsPerMeter * m_Zoom);
        m_Dirty = true;
    }

    public void OnPointerClick(PointerEventData e)
    {
        if (!interactive || e.dragging || m_Pinched || m_Plan == null)
            return;
        Vector2 at = Local(e);
        Vector2 point = ToPlan(at);

        PlanPlace place = BadgeAt(at);
        if (place == null && m_Indoor > 0.5f)
            place = PlaceAt(point);

        if (place == null && m_Campus != null)
        {
            CampusData.Building building = BuildingAt(at);
            // The floor's own building, while its floor shows, is a tap on the floor.
            if (building != null && !(building == m_Campus?.indoor && m_Indoor > 0.5f))
            {
                BuildingTapped?.Invoke(building);
                return;
            }
        }
        PlaceTapped?.Invoke(place, point);
    }

    // The shown place icon nearest a tap, within reach.
    PlanPlace BadgeAt(Vector2 at)
    {
        PlanPlace best = null;
        float bestDistance = BadgeReach;
        foreach (Marker badge in m_Badges)
        {
            if (!badge.shown)
                continue;
            float distance = Vector2.Distance(at, ToWindow(badge.position));
            if (distance <= bestDistance)
            {
                best = badge.place;
                bestDistance = distance;
            }
        }
        return best;
    }
}
