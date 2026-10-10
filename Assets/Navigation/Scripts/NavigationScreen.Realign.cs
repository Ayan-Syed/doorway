using System.Collections.Generic;
using UnityEngine;

// Saying where you are, made easy: Re-align opens the door list over the map zoomed in on where the walk had got
// to, with the doors round that spot first ("Near where you were"); and a tap anywhere on the map, on a room or in
// the hallway, picks the door nearest it.
public partial class NavigationScreen
{
    // The map shows this much round where the walk had got to, in metres.
    const float RealignSpan = 30f;
    // A tap picks the nearest door this close to it; further out, it asks for a tap nearer.
    const float TapDoorReach = 10f;
    // "Near where you were" lists the doors this close to that spot, NearYouRows at most.
    const float RealignNear = 20f;

    Vector2? m_RealignAt;          // set by Realign for the next ShowStart, which takes it
    bool m_StartListWalked;        // the start list's "Near" section was built round a realign's spot

    // The spot Realign left for this ShowStart, once: any other way onto Start shows the whole floor.
    Vector2? TakeRealignSpot()
    {
        Vector2? at = m_RealignAt;
        m_RealignAt = null;
        return at;
    }

    // The doors round where the walk had got to, nearest first, into near (as FillStartList's "Near you").
    void NearWalk(Vector2 at, List<(PlanPlace place, PlanDoor door)> near)
    {
        m_NearYouAt = at;
        foreach (var (place, door, distance) in DoorsNear(at))
        {
            if (distance > RealignNear || near.Count == NearYouRows)
                break;
            near.Add((place, door));
        }
    }

    // The map in close round the spot, with the user's dot there, so the doors near it are big enough to tap.
    void FrameRealign(Vector2 at)
    {
        var half = new Vector2(RealignSpan / 2f, RealignSpan / 2f);
        map.FramePoints(new List<Vector2> { at - half, at + half }, 0f, RealignSpan, !Application.isPlaying);
        map.ShowPuck(at, 0f, 0f, false);
    }

    // The door nearest a tap on the map, within TapDoorReach, the destination's left out; false if there's none.
    bool DoorNearTap(Vector2 point, out PlanPlace place, out PlanDoor door)
    {
        place = null;
        door = null;
        foreach (var (p, d, distance) in DoorsNear(point))
        {
            if (distance > TapDoorReach)
                break;
            place = p;
            door = d;
            return true;
        }
        return false;
    }
}
