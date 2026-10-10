using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

// Navigator, continued: keeping the plan lined up while walking (heading, floor height) and rerouting.
public partial class Navigator
{
    // A new route from where the user is, after RerouteSeconds of tracked time more than RerouteDistance off it.
    // Any frame back within it, or without tracking, starts the wait again: no rerouting from a lost pose.
    void CheckOffRoute()
    {
        m_OffRouteTime = OffRoute > RerouteDistance && IsTracking ? m_OffRouteTime + Time.deltaTime : 0f;
        if (m_OffRouteTime < RerouteSeconds)
            return;
        Route route = m_Router.FindRoute(UserPosition, Route.place);
        if (route == null)
            return;   // the timer stays up, so it tries again next frame
        Route = route;
        Progress = Route.Project(UserPosition, 0f, out float offRoute);
        OffRoute = offRoute;
        m_OffRouteTime = 0f;
        m_Trail.Clear();
        // The nearest door may have changed.
        pin.Show(Route.place, Plan.NodePosition(Route.door.node), Route.door.Direction);
        Rerouted?.Invoke();
    }

    // Turns the plan, a little each frame, so the stretch of route being walked lines up with the walk. Only from
    // straight walking along a long stretch while on it, only small disagreements, and never more than
    // MaxTotalHeadingCorrection in total.
    void CorrectHeading()
    {
        Vector3 here = arCamera.position;
        here.y = 0f;
        if (m_Trail.Count == 0 || Vector3.Distance(here, m_Trail[m_Trail.Count - 1]) >= TrailSpacing)
        {
            m_Trail.Add(here);
            if (m_Trail.Count > MaxTrail)
                m_Trail.RemoveAt(0);
        }

        // The last few metres walked, if they were nearly straight.
        float walked = 0f;
        int first = m_Trail.Count - 1;
        while (first > 0 && walked < StraightWalk)
        {
            walked += Vector3.Distance(m_Trail[first - 1], m_Trail[first]);
            first--;
        }
        Vector3 shift = m_Trail[m_Trail.Count - 1] - m_Trail[first];
        if (walked < StraightWalk || shift.magnitude < StraightRatio * walked)
            return;

        // Only along a hallway-length stretch of the route, while on it.
        Route.SegmentAt(Progress, out Vector2 stretch, out float stretchLength);
        if (stretchLength < LongStretch || OffRoute > HeadingOffRouteLimit)
            return;
        Vector3 w = planRoot.InverseTransformDirection(shift);
        var walk = new Vector2(w.x, w.z);
        float delta = Vector2.SignedAngle(walk, stretch);
        if (Mathf.Abs(delta) > 90f)
            delta = Vector2.SignedAngle(walk, -stretch);   // walking the route backwards
        if (Mathf.Abs(delta) > MaxHeadingCorrection)
            return;

        // Positive Unity yaw turns clockwise seen from above; delta is counterclockwise from the walk to the route,
        // so turning the plan by +delta brings the route onto the walk. A low-pass filter: each frame takes off the
        // share 1 - e^(-dt/T) of what's left.
        float step = delta * (1f - Mathf.Exp(-Time.deltaTime / HeadingCorrectionSeconds));
        float total = Mathf.Clamp(m_HeadingCorrection + step, -MaxTotalHeadingCorrection, MaxTotalHeadingCorrection);
        step = total - m_HeadingCorrection;
        if (step == 0f)
            return;
        m_HeadingCorrection = total;
        planRoot.RotateAround(m_Anchor, Vector3.up, step);   // about the start spot, a vertical axis
    }

    // The biggest horizontal plane a standing person's phone could be above: that's the floor.
    void FindFloor(bool now)
    {
        if (planeManager == null || (!now && Time.time < m_NextFloorCheck))
            return;
        m_NextFloorCheck = Time.time + FloorCheckSeconds;

        float cameraY = arCamera.position.y;
        float bestArea = 0.5f;   // m²: anything smaller is a table top or a scrap of floor
        float floorY = 0f;
        bool found = false;
        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane.alignment != PlaneAlignment.HorizontalUp)
                continue;
            float y = plane.transform.position.y;
            float drop = cameraY - y;
            float area = plane.size.x * plane.size.y;
            if (drop > 0.8f && drop < 2.1f && area > bestArea)
            {
                bestArea = area;
                floorY = y;
                found = true;
            }
        }
        if (!found)
            return;

        // Straight onto it when lining up; halfway each check while walking, so the band never jumps.
        m_FloorY = now ? floorY : Mathf.Lerp(m_FloorY, floorY, 0.5f);
        Vector3 position = planRoot.position;
        position.y = m_FloorY;
        planRoot.position = position;
    }
}
