using UnityEngine;

// Navigator, continued: what to tell the user next, and the editor's preview pose.
public partial class Navigator
{
    // Within this of the route's start and facing more than TurnFirstAngle away from it: "turn first".
    const float TurnFirstDistance = 3f;
    const float TurnFirstAngle = 50f;

    // What to do next: a turn coming up (and how far away), or arriving. then is the step after that, gap metres
    // further on.
    public Maneuver NextStep(out float distance, out Maneuver then, out float gap)
    {
        // A turn stays up until a metre past it.
        int next = 0;
        while (next < Route.turns.Count && Route.turns[next].distance <= Progress - 1f)
            next++;

        // Positive: the route goes off to the user's left.
        float facingOff = Vector2.SignedAngle(HeadingVector(UserHeading), Route.DirectionAt(Progress));
        Maneuver step;
        float at;
        if (Progress < TurnFirstDistance && Route.length > TurnFirstDistance && Mathf.Abs(facingOff) > TurnFirstAngle)
        {
            // Just started (or rerouted) and facing well away from where the route leads: turn first.
            step = Route.Classify(facingOff);
            at = Progress;
            next--;   // so "then" is the first real turn
        }
        else
        {
            step = StepAt(next, out at);
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
        OffRoute = 0f;
        UserPosition = Route.PointAt(progress);
        UserHeading = Heading(Route.DirectionAt(progress)) + turn;
        planRoot.gameObject.SetActive(true);
        if (arrived)
            ribbon.Hide();
        else
            ribbon.Show(Route, progress);
        pin.Show(Route.place, Plan.NodePosition(Route.door.node), Route.door.Direction);
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
