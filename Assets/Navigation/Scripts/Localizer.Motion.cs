using System.Collections.Generic;
using UnityEngine;

// The snap, aware of how the camera moved just before it. Standing (moved under WalkingMoved in WalkingSeconds): the
// user is where the app asked, back to the door: the usual prior. Walking: they tapped the door a few steps in, and
// their phone faces wherever they were going, so the back-to-door heading is wrong (the guided-error tail of the
// stress walks). Then the plan's yaw comes from the walk itself (the AR camera's displacement over the last
// HeadingWalk metres is the door's inward way, ± WalkHeadingSpread), and the position is along the inward way from
// LineFrom to the walk since the camera last stood still plus LineMargin. Turned more than TurnedAngle since then:
// past the vestibule, nothing simple holds: any heading, anywhere within the walk, and a plate before sure.
public sealed partial class LocalizerEngine
{
    public enum SnapMotion { Standing, Walking, Turned }

    const float WalkingSeconds = 4f, WalkingMoved = 0.8f, HeadingWalk = 2f, WalkHeadingSpread = 25f, TurnedAngle = 60f;
    const float LineFrom = 0.5f, LineMargin = 1f, LineLateral = 0.3f, MaxWalked = 10f;
    const float StillSeconds = 1f, StillReach = 0.2f, FirstWalk = 1.5f;

    /// <summary>How the camera moved before the last snap.</summary>
    public SnapMotion LastSnapMotion { get; private set; }

    struct Motion
    {
        public SnapMotion kind;
        public float heading;   // world: the recent walk's way
        public float walked;    // metres since the camera last stood still
    }

    // history: the camera's world (x, z) in x, y and the seconds before the snap (negative) in z, oldest first.
    static Motion Analyse(IReadOnlyList<Vector3> history, Vector2 camera)
    {
        var none = new Motion { kind = SnapMotion.Standing };
        if (history == null || history.Count < 3)
            return none;
        int n = history.Count;
        Vector2 P(int i) => new Vector2(history[i].x, history[i].y);
        // Walking: moved WalkingMoved in the last WalkingSeconds.
        int recent = n - 1;
        while (recent > 0 && history[recent - 1].z >= -WalkingSeconds)
            recent--;
        if (Vector2.Distance(camera, P(recent)) < WalkingMoved)
            return none;
        // Back to where it last stood still (within StillReach for StillSeconds), adding up the walk.
        int still = 0;
        float walked = 0f;
        for (int i = n - 1; i > 0; i--)
        {
            int j = i;
            while (j > 0 && history[i].z - history[j - 1].z <= StillSeconds &&
                   Vector2.Distance(P(j - 1), P(i)) <= StillReach)
                j--;
            if (history[i].z - history[j].z >= StillSeconds * 0.9f)
            {
                still = i;
                break;
            }
            walked += Vector2.Distance(P(i), P(i - 1));
        }
        // The way of the last HeadingWalk metres, and of the first FirstWalk after standing still.
        int back = n - 1;
        float length = 0f;
        while (back > still && length < HeadingWalk)
        {
            length += Vector2.Distance(P(back), P(back - 1));
            back--;
        }
        int first = still;
        length = 0f;
        while (first < n - 1 && length < FirstWalk)
        {
            length += Vector2.Distance(P(first + 1), P(first));
            first++;
        }
        float heading = PositionFilter.Heading(camera - P(back));
        float start = PositionFilter.Heading(P(first) - P(still));
        bool turned = Mathf.Abs(Mathf.DeltaAngle(start, heading)) > TurnedAngle;
        return new Motion
        {
            kind = turned ? SnapMotion.Turned : SnapMotion.Walking,
            heading = heading,
            walked = Mathf.Min(walked, MaxWalked),
        };
    }

    // The door's inward way at a spot (plan heading), NaN if no door is there.
    float DoorInward(Vector2 spot)
    {
        if (m_Plan?.places == null)
            return float.NaN;
        foreach (PlanPlace place in m_Plan.places)
            foreach (PlanDoor door in place.doors)
                if ((m_Plan.NodePosition(door.node) - spot).sqrMagnitude < 0.01f && door.Direction.sqrMagnitude > 0f)
                    return PositionFilter.Heading(-door.Direction);
        return float.NaN;
    }
}
