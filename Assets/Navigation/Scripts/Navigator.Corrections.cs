using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

// Navigator, continued: keeping the plan lined up while walking (heading, floor height, walls, signs) and rerouting.
public partial class Navigator
{
    // A sighting's distance, once its ray lands on a wall ARCore is tracking.
    const float WallHitSigma = 0.15f;
    // A sighting's ray must be at least this level (its horizontal share): looking steeply up or down, the bearing
    // is poor, and plates hang at eye level anyway.
    const float MinSightingLevel = 0.3f;

    readonly List<Vector2> m_Landmarks = new List<Vector2>();
    readonly List<Vector2> m_Normals = new List<Vector2>();

    // Off the route this long (tracked, sure of the position) before rerouting. In the field a 1.5 s wait rerouted
    // three times in twenty seconds on a 1-2 m error: the route is worth more than a quick reroute.
    const float SureRerouteSeconds = 3f;

    // A new route from where the user is, after SureRerouteSeconds of tracked time more than RerouteDistance off it,
    // sure of the position all the while. Any frame back within it, untracked, or unsure starts the wait again.
    void CheckOffRoute()
    {
        bool sure = SureOfPosition();
        if (sure && localizer != null && localizer.Indoors)
        {
            NextStep(out float toTurn, out _, out _);
            sure = MayReroute(localizer.State, localizer.Spread, localizer.AlongSpread, toTurn);
        }
        m_OffRouteTime = OffRoute > RerouteDistance && IsTracking && sure ? m_OffRouteTime + Time.deltaTime : 0f;
        if (m_OffRouteTime < SureRerouteSeconds)
            return;
        Route route = m_Router.FindRoute(UserPosition, Route.place);
        if (route == null)
            return;   // the timer stays up, so it tries again next frame
        if (SameStart(Route, Progress, route))
        {
            // The same way on from here: the old route stands (the user is on it further along), quietly.
            Progress = Route.Project(UserPosition, Progress, out float along);
            OffRoute = along;
            m_OffRouteTime = 0f;
            m_Log?.Note("reroute kept");
            return;
        }
        Route = route;
        Progress = Route.Project(UserPosition, 0f, out float offRoute);
        OffRoute = offRoute;
        IsOffRoute = false;
        m_OffRouteTime = 0f;
        m_Trail.Clear();
        // The nearest door may have changed.
        Vector2 door = Plan.NodePosition(Route.door.node);
        pin.Show(Route.place, door, Route.door.Direction);
        m_Log?.Rerouted(door);
        if (m_SharedFilter)
            localizer.Note("rerouted");
        Rerouted?.Invoke();
    }

    // Turns the plan, a little each frame, so the stretch of route being walked lines up with the walk. Only from
    // straight walking along a long stretch while on it, only when all of that walk was along this one stretch
    // (just round a bend, the walk is still the last stretch's), only small disagreements, and never more than
    // MaxTotalHeadingCorrection in total. With a walkable map the filter's particles turn instead of the plan root.
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
        int stretch = StretchAt(Progress);
        Vector2 a = Route.points[stretch], b = Route.points[stretch + 1];
        float stretchLength = Vector2.Distance(a, b);
        if (stretchLength < LongStretch || OffRoute > HeadingOffRouteLimit)
            return;
        for (int i = first; i < m_Trail.Count; i++)
        {
            if (!OnStretch(PlanPoint(m_Trail[i]), a, b))
                return;
        }

        Vector2 walk = PlanDirection(shift);
        Vector2 along = (b - a) / stretchLength;
        float delta = Vector2.SignedAngle(walk, along);
        if (Mathf.Abs(delta) > 90f)
            delta = Vector2.SignedAngle(walk, -along);   // walking the route backwards
        if (Mathf.Abs(delta) > MaxHeadingCorrection)
            return;

        // Positive Unity yaw turns clockwise seen from above; delta is counterclockwise from the walk to the route,
        // so turning the plan by +delta brings the route onto the walk. A low-pass filter: each frame takes off the
        // share 1 - e^(-dt/T) of what's left.
        float step = delta * (1f - Mathf.Exp(-Time.deltaTime / HeadingCorrectionSeconds));
        if (m_Filter != null)
        {
            m_Filter.ShiftHeading(step);   // its offsets are capped at the same total
            return;
        }
        float total = Mathf.Clamp(m_HeadingCorrection + step, -MaxTotalHeadingCorrection, MaxTotalHeadingCorrection);
        step = total - m_HeadingCorrection;
        if (step == 0f)
            return;
        m_HeadingCorrection = total;
        planRoot.RotateAround(m_Anchor, Vector3.up, step);   // about the start spot, a vertical axis
    }

    // The route's stretch (points[i] to points[i + 1]) at this distance along it, as Route itself picks it.
    int StretchAt(float distance)
    {
        int last = Route.points.Count - 2;
        for (int i = 0; i < last; i++)
        {
            if (distance < Route.DistanceAtPoint(i + 1))
                return i;
        }
        return last;
    }

    // p lies alongside the stretch a to b (not before or past its ends), and within the heading correction's reach.
    static bool OnStretch(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Vector2.Dot(p - a, ab) / ab.sqrMagnitude;
        return t >= 0f && t <= 1f && Vector2.Distance(p, a + ab * t) <= HeadingOffRouteLimit;
    }

    // A world point and a world direction on the plan: through the plan root, or the filter's estimate when there
    // is one (the plan root trails it by up to a second).
    Vector2 PlanPoint(Vector3 world)
    {
        if (m_Filter != null)
            return m_Filter.ToPlanPoint(new Vector2(world.x, world.z));
        Vector3 local = planRoot.InverseTransformPoint(world);
        return new Vector2(local.x, local.z);
    }

    Vector2 PlanDirection(Vector3 world)
    {
        if (m_Filter != null)
            return PositionFilter.ToPlan(new Vector2(world.x, world.z), m_Filter.Yaw);
        Vector3 local = planRoot.InverseTransformDirection(world);
        return new Vector2(local.x, local.z);
    }

    // The biggest horizontal plane a standing person's phone could be above: that's the floor. Only planes ARCore
    // is tracking now, and not ones merged into a bigger one (they keep their old size). Once found, the floor
    // only moves within FloorLock: a table, a bench or a stair landing is another surface, not a better floor.
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
            if (plane.alignment != PlaneAlignment.HorizontalUp || plane.trackingState != TrackingState.Tracking ||
                plane.subsumedBy != null)
                continue;
            float y = plane.transform.position.y;
            float drop = cameraY - y;
            float area = plane.size.x * plane.size.y;
            if (drop <= 0.8f || drop >= 2.1f || area <= bestArea)
                continue;
            if (m_FloorFound && Mathf.Abs(y - m_FloorY) > FloorLock)
                continue;
            bestArea = area;
            floorY = y;
            found = true;
        }
        if (!found)
            return;

        // Straight onto it when lining up; halfway each check while walking, so the band never jumps.
        float before = m_FloorY;
        m_FloorY = now ? floorY : Mathf.Lerp(m_FloorY, floorY, 0.5f);
        m_FloorFound = true;
        Vector3 position = planRoot.position;
        position.y = m_FloorY;
        planRoot.position = position;

        // Settled for a couple of seconds: stop looking for more floor (plane detection costs CPU and battery).
        m_FloorSteady = Mathf.Abs(m_FloorY - before) < FloorSteadyChange ? m_FloorSteady + 1 : 0;
        if (!m_FloorSettled && m_FloorSteady >= FloorSteadyChecks)
        {
            m_FloorSettled = true;
            UpdatePlaneDetection();
        }
    }

    // Horizontal planes until the walk's floor has settled, and walls while the sign reader wants them.
    void UpdatePlaneDetection()
    {
        if (planeManager == null)
            return;
        PlaneDetectionMode mode = (IsGuiding || m_KeepAR) && m_FloorSettled ? PlaneDetectionMode.None
            : PlaneDetectionMode.Horizontal;
        if (m_KeepVerticalPlanes)
            mode |= PlaneDetectionMode.Vertical;
        if (planeManager.requestedDetectionMode != mode)
            planeManager.requestedDetectionMode = mode;
    }

    // With a walkable map: the filter starts at the door spot, under the camera, turned as Begin turned the plan.
    void StartFilter(Vector2 spot)
    {
        m_Filter = null;
        if (Walkable == null)
            return;
        Vector3 camera = arCamera.position;
        m_Filter = new PositionFilter(Walkable, PositionFilter.DefaultCount, Environment.TickCount);
        m_Filter.Reset(spot, new Vector2(camera.x, camera.z), m_BaseYaw);
        UserPosition = m_Filter.Estimate;
    }

    // Each frame with a walkable map: the camera's move into the filter, the user where the filter says, and the
    // plan root eased toward the filter's alignment.
    void FollowFilter(bool tracking)
    {
        Vector3 camera = arCamera.position;
        var here = new Vector2(camera.x, camera.z);
        m_Filter.Follow(here, tracking);
        // Lost, the pose is a guess: hold the estimate until tracking is back.
        UserPosition = tracking ? m_Filter.Current(here) : m_Filter.Estimate;
        UserHeading = PlanHeading(FlatForward(), m_Filter.Yaw);
        FollowEstimate();
    }

    // With the Localizer's filter: it moves the filter; the user is where it says.
    void FollowLocalizer()
    {
        UserPosition = localizer.Position;
        UserHeading = localizer.Heading;
        FollowEstimate();
    }

    // Moves the plan root part of the way to where the filter puts it: the estimate's plan spot under the camera
    // where the filter last stepped, turned by the filter's yaw. It turns about that spot, the user's, so the band
    // at the user's feet doesn't swing when the plan turns.
    void FollowEstimate()
    {
        float k = 1f - Mathf.Exp(-Time.deltaTime / FollowSeconds);
        Vector2 at = m_Filter.Estimate;
        var spot = new Vector3(at.x, 0f, at.y);
        Vector2 camera = m_Filter.Camera;
        Vector3 shown = planRoot.TransformPoint(spot);
        Vector3 position = Vector3.Lerp(shown, new Vector3(camera.x, m_FloorY, camera.y), k);
        position.y = m_FloorY;
        Quaternion rotation = Quaternion.Euler(0f, Mathf.LerpAngle(planRoot.eulerAngles.y, m_Filter.Yaw, k), 0f);
        planRoot.SetPositionAndRotation(position - rotation * spot, rotation);
    }

    // A room-number sign the camera read (from SignReader): a measurement for the filter. Used only while guiding
    // with a walkable map; logged either way.
    public void AddSighting(SignSighting sighting)
    {
        if (localizer != null)
        {
            localizer.AddSighting(sighting);
            return;
        }
        bool used = UseSighting(sighting, out float bearing, out float range, out float sigma);
        m_Log?.Sighting(sighting, bearing, range, sigma, used);
    }

    bool UseSighting(in SignSighting s, out float bearing, out float range, out float sigma)
    {
        bearing = range = sigma = 0f;
        if (m_Filter == null || !IsGuiding || string.IsNullOrEmpty(s.text) || Plan.signs == null)
            return false;
        // The same number can hang at two doors (or a misread matches another): every one is a candidate.
        string text = s.text.Trim();
        m_Landmarks.Clear();
        m_Normals.Clear();
        foreach (PlanSign sign in Plan.signs)
        {
            if (sign != null && string.Equals(sign.text, text, StringComparison.OrdinalIgnoreCase))
            {
                m_Landmarks.Add(sign.Position);
                m_Normals.Add(sign.Normal);
            }
        }
        if (m_Landmarks.Count == 0)
            return false;

        Vector3 direction = s.direction.normalized;
        float level = new Vector2(direction.x, direction.z).magnitude;
        if (level < MinSightingLevel || !(s.distance > 0f))
            return false;
        float distance = s.distance, distanceSigma = Mathf.Max(s.distanceSigma, 0.05f);
        // A tracked wall where the ray meets it says the distance better than the digits' size does.
        if (RaycastWall(s.cameraPosition, direction, out float hit) && Mathf.Abs(hit - distance) < 3f * distanceSigma + 0.3f)
        {
            distance = hit;
            distanceSigma = Mathf.Min(distanceSigma, WallHitSigma);
        }

        // Measured from the camera where the filter last stepped, not where the frame was taken: the sign stays put
        // while the user walks on, so a late sighting is as good as a fresh one.
        Vector3 signAt = s.cameraPosition + direction * distance;
        Vector2 from = m_Filter.Camera;
        var toSign = new Vector2(signAt.x - from.x, signAt.z - from.y);
        range = toSign.magnitude;
        bearing = PositionFilter.Heading(toSign);
        sigma = distanceSigma * level;
        return m_Filter.AddSignObservation(bearing, range, sigma, m_Landmarks, m_Normals);
    }

    // Distance along the ray to the nearest wall ARCore is tracking (a vertical plane, within its extent).
    bool RaycastWall(Vector3 origin, Vector3 direction, out float distance)
    {
        distance = float.PositiveInfinity;
        if (planeManager == null)
            return false;
        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane.alignment != PlaneAlignment.Vertical || plane.trackingState != TrackingState.Tracking ||
                plane.subsumedBy != null)
                continue;
            Vector3 normal = plane.normal;
            float facing = Vector3.Dot(normal, direction);
            if (Mathf.Abs(facing) < 0.2f)
                continue;   // grazing: a small error in either moves the hit a long way
            float t = Vector3.Dot(plane.center - origin, normal) / facing;
            if (t < 0.3f || t >= distance)
                continue;
            Vector3 local = plane.transform.InverseTransformPoint(origin + direction * t);
            Vector2 centre = plane.centerInPlaneSpace, extents = plane.extents;
            if (Mathf.Abs(local.x - centre.x) > extents.x + 0.2f || Mathf.Abs(local.z - centre.y) > extents.y + 0.2f)
                continue;
            distance = t;
        }
        return !float.IsPositiveInfinity(distance);
    }
}
