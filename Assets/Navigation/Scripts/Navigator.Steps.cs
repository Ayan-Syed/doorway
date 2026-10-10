using UnityEngine;

// Navigator, continued: what to tell the user next, and the editor's preview pose.
public partial class Navigator
{
    // Within this of the route's start and heading more than TurnFirstAngle away from it: "turn first".
    const float TurnFirstDistance = 3f;
    const float TurnFirstAngle = 50f;
    // The way the user is going is the camera's move over its last WalkWindow metres (not the way the phone faces:
    // walking down a hallway pointing it at the room numbers says nothing about the walk). A move that old counts
    // as walking for WalkFresh seconds; after that the user is standing, and the facing may say which way, but only
    // with the phone held up and upright (not pitched at the floor or ceiling, not turned on its side).
    const float WalkWindow = 1.5f;
    const float WalkFresh = 3f;
    // A move this big between frames is the pose jumping (tracking back), not a walk.
    const float WalkJump = 4f;
    const float MaxFacingPitch = 0.6f;    // |forward.y|: about 37 degrees up or down
    const float MinUpright = 0.7f;        // the phone's up against the world's: tilted under about 45 degrees
    // The turn-first side (left or right) changes at most once in this long.
    const float SideHold = 3f;
    // The same turn keeps its word (Bear, Turn, Sharp) unless its angle moves more than this; "same" is within
    // SameTurnReach metres of where it was.
    const float TypeHold = 20f;
    const float SameTurnReach = 1.5f;

    Vector3 m_WalkAnchor;
    bool m_WalkAnchored;
    Vector2 m_WalkDirection;
    float m_WalkAt = float.NegativeInfinity;
    int m_FirstSide;            // the last turn-first's side: +1 left, -1 right, 0 none yet
    float m_FirstSideAt = float.NegativeInfinity;
    Vector2 m_ShownTurnAt = new Vector2(float.NaN, float.NaN);
    float m_ShownTurnAngle;
    Maneuver m_ShownTurn;

    // Called each frame by NextStep: where the camera has gone over its last WalkWindow metres, on the plan.
    void TrackWalk()
    {
        if (arCamera == null)
            return;
        Vector3 here = arCamera.position;
        here.y = 0f;
        if (!m_WalkAnchored)
        {
            m_WalkAnchor = here;
            m_WalkAnchored = true;
            return;
        }
        Vector3 shift = here - m_WalkAnchor;
        if (shift.magnitude < WalkWindow)
            return;
        if (shift.magnitude > WalkJump)
        {
            m_WalkAnchor = here;
            return;
        }
        Vector2 walk = PlanDirection(shift);
        if (walk.sqrMagnitude > 1e-6f)
        {
            m_WalkDirection = walk.normalized;
            m_WalkAt = Time.unscaledTime;
        }
        m_WalkAnchor = here;
    }

    // A new walk: nothing walked yet, no turn shown.
    void ForgetWalk()
    {
        m_WalkAnchored = false;
        m_WalkAt = float.NegativeInfinity;
        m_FirstSide = 0;
        m_ShownTurnAt = new Vector2(float.NaN, float.NaN);
    }

    // The heading to judge "turn first" by: the walk's while walking, the facing while standing with the phone up
    // and upright, else none (false).
    bool GoingHeading(out Vector2 going)
    {
        if (Time.unscaledTime - m_WalkAt < WalkFresh)
        {
            going = m_WalkDirection;
            return true;
        }
        going = HeadingVector(UserHeading);
        if (arCamera == null)
            return true;
        return Mathf.Abs(arCamera.forward.y) <= MaxFacingPitch && Vector3.Dot(arCamera.up, Vector3.up) >= MinUpright;
    }

    // The turn at a spot and its angle as a word, kept as it was while it's the same turn and hardly changed.
    Maneuver StableTurn(Vector2 at, float angle)
    {
        Maneuver word = Route.Classify(angle);
        if (!float.IsNaN(m_ShownTurnAt.x) && Vector2.Distance(at, m_ShownTurnAt) <= SameTurnReach
            && Mathf.Abs(Mathf.DeltaAngle(angle, m_ShownTurnAngle)) <= TypeHold && SameSide(word, m_ShownTurn))
            return m_ShownTurn;
        m_ShownTurnAt = at;
        m_ShownTurnAngle = angle;
        m_ShownTurn = word;
        return word;
    }

    static bool SameSide(Maneuver a, Maneuver b) => Side(a) == Side(b);

    static int Side(Maneuver m)
    {
        switch (m)
        {
            case Maneuver.SlightLeft:
            case Maneuver.Left:
            case Maneuver.SharpLeft:
                return 1;
            case Maneuver.SlightRight:
            case Maneuver.Right:
            case Maneuver.SharpRight:
                return -1;
            default:
                return 0;
        }
    }

    // What to do next: a turn coming up (and how far away), or arriving. then is the step after that, gap metres
    // further on. The guide screen asks every frame, so this and DoorSide allocate nothing.
    public Maneuver NextStep(out float distance, out Maneuver then, out float gap)
    {
        // A turn stays up until a metre past it.
        int next = 0;
        while (next < Route.turns.Count && Route.turns[next].distance <= Progress - 1f)
            next++;

        TrackWalk();
        // Positive: the route goes off to the user's left of the way they're going.
        bool known = GoingHeading(out Vector2 going);
        float off = known ? Vector2.SignedAngle(going, Route.DirectionAt(Progress)) : 0f;
        int side = off > 0f ? 1 : -1;
        bool turnFirst = known && Progress < TurnFirstDistance && Route.length > TurnFirstDistance
                         && Mathf.Abs(off) > TurnFirstAngle;
        // The other side so soon after one: a phone swung, not a user who turned round. Not yet.
        if (turnFirst && m_FirstSide != 0 && side != m_FirstSide && Time.unscaledTime - m_FirstSideAt < SideHold)
            turnFirst = false;
        Maneuver step;
        float at;
        if (turnFirst)
        {
            // Just started (or rerouted) and going well away from where the route leads: turn first.
            if (side != m_FirstSide)
            {
                m_FirstSide = side;
                m_FirstSideAt = Time.unscaledTime;
            }
            step = StableTurn(Route.points[0], off);
            at = Progress;
            next--;   // so "then" is the first real turn
        }
        else
        {
            step = StepAt(next, out at);
            if (step != Maneuver.Arrive && next >= 0 && next < Route.turns.Count)
                step = StableTurn(Route.PointAt(Route.turns[next].distance), Route.turns[next].angle);
        }

        distance = Mathf.Max(0f, at - Progress);
        if (step == Maneuver.Arrive)
        {
            then = Maneuver.Arrive;
            gap = float.PositiveInfinity;
        }
        else
        {
            then = StepAt(next + 1, out float thenAt);
            gap = thenAt - at;
        }
        return step;
    }

    // The route's turn number `turn`, or arriving once the turns run out.
    Maneuver StepAt(int turn, out float at)
    {
        if (turn < Route.turns.Count)
        {
            at = Route.turns[turn].distance;
            return Route.Classify(Route.turns[turn].angle);
        }
        at = Route.length;
        return Maneuver.Arrive;
    }

    // Which side the destination's door is on, walking in at the end of the route.
    public string DoorSide()
    {
        Vector2 walk = Route.DirectionAt(Route.length);
        if (walk.sqrMagnitude < 0.01f)
            walk = HeadingVector(UserHeading);   // a zero-length route: whichever way the phone faces
        float angle = Vector2.SignedAngle(walk, Route.door.Direction);
        if (Mathf.Abs(angle) < 35f)
            return "ahead";
        if (Mathf.Abs(angle) > 145f)
            return "behind you";
        return angle > 0f ? "on your left" : "on your right";
    }

    // For the editor's preview renders (no AR): guiding as if the user stood `progress` metres along the route
    // looking `turn` degrees clockwise off it. The plan root stays where it is.
    public void Preview(PlanDoor from, PlanPlace to, float progress, float turn, bool arrived)
    {
        Route = PlanRoute(from, to);
        if (Route == null)
        {
            Debug.LogWarning($"[Navigation] Preview: no route from {from.node} to {to?.name}.");
            return;
        }
        IsGuiding = true;
        HasArrived = arrived;
        Progress = progress;
        ForgetWalk();
        OffRoute = 0f;
        IsOffRoute = false;
        ShouldRealign = false;
        m_LostTime = 0f;
        m_Filter = null;
        m_SharedFilter = false;
        UserPosition = Route.PointAt(progress);
        RawPosition = UserPosition;
        UserHeading = Heading(Route.DirectionAt(progress)) + turn;
        planRoot.gameObject.SetActive(true);
        if (arrived)
            ribbon.Hide();
        else
            ribbon.Show(Route, progress);
        pin.Show(Route.place, Plan.NodePosition(Route.door.node), Route.door.Direction);
    }

    // Rerouting is only for a position the user can trust: sure of it (as the guide screen judges, without its
    // delay) and not just before a turn the along-route error could put in the wrong corridor.
    public static bool MayReroute(LocalizerState state, float spread, float alongSpread, float toTurn)
    {
        if (state == LocalizerState.Outdoors)
            return true;
        return NavigationScreen.WantedGuidance(state, spread, alongSpread, toTurn, NavigationScreen.Guidance.Sure)
               == NavigationScreen.Guidance.Sure;
    }

    // A new route that starts as the old one goes on from here (its first SameStartMetres within SameStartReach of
    // it) isn't a new route: keep the old one, and say nothing.
    public const float SameStartMetres = 10f;
    const float SameStartReach = 1.5f;

    public static bool SameStart(Route old, float progress, Route fresh)
    {
        if (old == null || fresh == null)
            return false;
        float hint = progress;
        float end = Mathf.Min(SameStartMetres, fresh.length);
        for (float d = 0f; d <= end; d += 1f)
        {
            hint = old.Project(fresh.PointAt(d), hint, out float off);
            if (off > SameStartReach)
                return false;
        }
        return true;
    }

    // A heading (degrees clockwise from plan up) as a plan direction.
    public static Vector2 HeadingVector(float heading)
    {
        float r = heading * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
    }

    // A plan direction as a heading: degrees clockwise from plan up, like a compass.
    public static float Heading(Vector2 direction)
    {
        return Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;
    }
}
