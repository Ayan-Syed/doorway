using System.Collections.Generic;
using UnityEngine;

// Shortest walking routes over a floor plan's walkway graph (Dijkstra; a floor has a hundred-odd points).
public class Router
{
    readonly FloorPlan m_Plan;
    readonly Vector2[] m_Positions;
    readonly List<int>[] m_Links;

    // Where people can walk (Data/*-walkable.bytes). With it, a route from an arbitrary spot joins the graph only
    // along a line that crosses no wall; without it, at the nearest link as the crow flies.
    public Walkable Walkable { get; set; }

    public Router(FloorPlan plan, Walkable walkable = null)
    {
        m_Plan = plan;
        Walkable = walkable;
        int count = plan.nodes.Count;
        m_Positions = new Vector2[count];
        m_Links = new List<int>[count];
        for (int i = 0; i < count; i++)
        {
            m_Positions[i] = plan.nodes[i].Position;
            m_Links[i] = new List<int>();
        }
        // FloorPlan.Load has already checked that every edge names real nodes.
        foreach (PlanEdge edge in plan.edges)
        {
            int a = plan.NodeIndex(edge.a), b = plan.NodeIndex(edge.b);
            m_Links[a].Add(b);
            m_Links[b].Add(a);
        }
    }

    // From a door spot (a graph point) to the nearest door of the place.
    public Route FindRoute(string fromNode, PlanPlace to)
    {
        int start = m_Plan.NodeIndex(fromNode);
        if (start < 0)
            throw new KeyNotFoundException($"No node \"{fromNode}\" in the floor plan.");
        float[] cost = NewCosts();
        cost[start] = 0f;
        return Search(cost, null, to);
    }

    // From anywhere on the floor (after walking off the route): joins the graph at the nearest spot on a link that
    // can be walked to in a straight line (see Walkable), or at the nearest spot of all if none can.
    public Route FindRoute(Vector2 from, PlanPlace to)
    {
        if (!FindJoin(from, true, out int bestA, out int bestB, out Vector2 join))
            FindJoin(from, false, out bestA, out bestB, out join);
        float best = Vector2.Distance(from, join);
        // The user enters the graph at the join point and can walk either way along that link.
        float[] cost = NewCosts();
        cost[bestA] = Vector2.Distance(join, m_Positions[bestA]);
        cost[bestB] = Mathf.Min(cost[bestB], Vector2.Distance(join, m_Positions[bestB]));

        // Starts where the user is, unless that's practically on the link already.
        var lead = new List<Vector2>();
        if (best > 1f)
            lead.Add(from);
        lead.Add(join);
        return Search(cost, lead, to);
    }

    // The nearest point on any link to p (its closest point or either end); with clear, only points the straight
    // line from p reaches without crossing a wall. False if there's none (or no Walkable to say).
    bool FindJoin(Vector2 p, bool clear, out int bestA, out int bestB, out Vector2 join)
    {
        bestA = bestB = 0;
        join = p;
        Walkable walkable = Walkable;
        if (clear && walkable == null)
            return false;
        // A position a little inside a wall (tracking noise) looks from the nearest open spot instead.
        Vector2 eye = p;
        if (clear && !walkable.IsWalkable(p) && walkable.Nearest(p, 1.5f, out Vector2 open))
            eye = open;

        float best = float.PositiveInfinity;
        bool found = false;
        foreach (PlanEdge edge in m_Plan.edges)
        {
            int a = m_Plan.NodeIndex(edge.a), b = m_Plan.NodeIndex(edge.b);
            Vector2 pa = m_Positions[a], pb = m_Positions[b];
            Vector2 q = ClosestOnSegment(p, pa, pb);
            foreach (Vector2 c in clear ? new[] { q, pa, pb } : new[] { q })
            {
                float d = Vector2.Distance(p, c);
                if (d >= best || clear && !walkable.SegmentClear(eye, c))
                    continue;
                best = d;
                bestA = a;
                bestB = b;
                join = c;
                found = true;
            }
        }
        return found;
    }

    float[] NewCosts()
    {
        var cost = new float[m_Positions.Length];
        for (int i = 0; i < cost.Length; i++)
            cost[i] = float.PositiveInfinity;
        return cost;
    }

    // Dijkstra from the seeded costs, then the nearest of the place's doors by walking distance.
    // lead: points before the graph (the user's spot, the join), in walking order.
    Route Search(float[] cost, List<Vector2> lead, PlanPlace to)
    {
        int count = m_Positions.Length;
        var previous = new int[count];
        var done = new bool[count];
        for (int i = 0; i < count; i++)
            previous[i] = -1;

        // A linear scan instead of a priority queue: there are a hundred-odd points.
        while (true)
        {
            int current = -1;
            for (int i = 0; i < count; i++)
                if (!done[i] && !float.IsPositiveInfinity(cost[i]) && (current < 0 || cost[i] < cost[current]))
                    current = i;
            if (current < 0)
                break;
            done[current] = true;
            foreach (int next in m_Links[current])
            {
                float c = cost[current] + Vector2.Distance(m_Positions[current], m_Positions[next]);
                if (c < cost[next])
                {
                    cost[next] = c;
                    previous[next] = current;
                }
            }
        }

        int end = -1;
        PlanDoor door = null;
        if (to?.doors != null)
        {
            foreach (PlanDoor candidate in to.doors)
            {
                int i = m_Plan.NodeIndex(candidate.node);
                if (i >= 0 && (end < 0 || cost[i] < cost[end]))
                {
                    end = i;
                    door = candidate;
                }
            }
        }
        if (end < 0 || float.IsPositiveInfinity(cost[end]))
            return null;

        // Back from the door to a seeded start, then the lead-in, then turned round to run start to end.
        var path = new List<Vector2>();
        for (int i = end; i >= 0; i = previous[i])
            path.Add(m_Positions[i]);
        if (lead != null)
        {
            for (int i = lead.Count - 1; i >= 0; i--)
                if (Vector2.Distance(path[path.Count - 1], lead[i]) > 0.05f)
                    path.Add(lead[i]);
        }
        path.Reverse();
        return new Route(Simplify(path, 0.5f), to, door);
    }

    // Douglas-Peucker: drops points that bend the line by less than the tolerance (door-spot detours, kinks).
    static List<Vector2> Simplify(List<Vector2> points, float tolerance)
    {
        if (points.Count < 3)
            return points;
        int last = points.Count - 1;
        var keep = new bool[points.Count];
        keep[0] = keep[last] = true;
        var ranges = new Stack<(int, int)>();
        ranges.Push((0, last));
        while (ranges.Count > 0)
        {
            var (first, end) = ranges.Pop();
            float farthest = 0f;
            int index = -1;
            for (int i = first + 1; i < end; i++)
            {
                float d = Vector2.Distance(points[i], ClosestOnSegment(points[i], points[first], points[end]));
                if (d > farthest)
                {
                    farthest = d;
                    index = i;
                }
            }
            if (index >= 0 && farthest > tolerance)
            {
                keep[index] = true;
                ranges.Push((first, index));
                ranges.Push((index, end));
            }
        }
        var kept = new List<Vector2>();
        for (int i = 0; i < points.Count; i++)
            if (keep[i])
                kept.Add(points[i]);
        return kept;
    }

    public static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        if (ab.sqrMagnitude < 1e-6f)
            return a;
        return a + ab * Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
    }
}

public enum Maneuver { Straight, SlightLeft, Left, SharpLeft, SlightRight, Right, SharpRight, TurnAround, Arrive }

// A turn along a route: where it is (metres from the start) and how far it turns (degrees, + is left).
public struct RouteTurn
{
    public float distance;
    public float angle;
}

// A walking route: a polyline in plan metres, from the start to the spot outside the destination's door.
public class Route
{
    public readonly List<Vector2> points;
    public readonly PlanPlace place;
    public readonly PlanDoor door;
    public readonly float length;
    public readonly List<RouteTurn> turns = new List<RouteTurn>();
    readonly float[] m_Distances;

    public Route(List<Vector2> points, PlanPlace place, PlanDoor door)
    {
        // Already there: a zero-length route still has one segment, so nothing below needs a special case.
        if (points.Count == 1)
            points.Add(points[0]);
        this.points = points;
        this.place = place;
        this.door = door;
        m_Distances = new float[points.Count];
        for (int i = 1; i < points.Count; i++)
            m_Distances[i] = m_Distances[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        length = m_Distances[points.Count - 1];
        FindTurns();
    }

    public float DistanceAtPoint(int index) => m_Distances[index];

    // Bends closer than a few metres count as one turn, so a jog around a pillar or a door spot either disappears
    // (the bends cancel out) or reads as the single turn a person would make. A cluster's angle is the heading in
    // against the heading out, so two 105-degree bends read as the 150-degree turn they make, not as 210 degrees.
    // Then a slight bend one way and a slight bend back within a few metres (a hallway that jogs) cancel out.
    void FindTurns()
    {
        const float clusterGap = 3f;
        const float jogGap = 5f;
        const float minimumTurn = 25f;
        const float slightTurn = 55f;
        var found = new List<RouteTurn>();
        int i = 1;
        while (i < points.Count - 1)
        {
            int j = i + 1;
            while (j < points.Count - 1 && m_Distances[j] - m_Distances[j - 1] < clusterGap)
                j++;
            // Segment (i - 1, i) in against segment (j - 1, j) out; zero-length segments read as no turn.
            float angle = Vector2.SignedAngle(points[i] - points[i - 1], points[j] - points[j - 1]);
            if (Mathf.Abs(angle) >= minimumTurn)
                found.Add(new RouteTurn { distance = m_Distances[i], angle = angle });
            i = j;
        }

        for (int k = 0; k < found.Count; k++)
        {
            if (k + 1 < found.Count && found[k + 1].distance - found[k].distance < jogGap
                && found[k].angle * found[k + 1].angle < 0f
                && Mathf.Abs(found[k].angle) < slightTurn && Mathf.Abs(found[k + 1].angle) < slightTurn
                && Mathf.Abs(found[k].angle + found[k + 1].angle) < minimumTurn)
            {
                k++;   // both go
                continue;
            }
            turns.Add(found[k]);
        }
    }

    public static Maneuver Classify(float angle)
    {
        float a = Mathf.Abs(angle);
        if (a < 25f)
            return Maneuver.Straight;
        if (a > 155f)
            return Maneuver.TurnAround;
        if (a < 55f)
            return angle > 0f ? Maneuver.SlightLeft : Maneuver.SlightRight;
        if (a < 125f)
            return angle > 0f ? Maneuver.Left : Maneuver.Right;
        return angle > 0f ? Maneuver.SharpLeft : Maneuver.SharpRight;
    }

    // The segment (points[i] to points[i + 1]) that this distance falls on; past either end, the end segment.
    int SegmentIndex(float distance)
    {
        if (points.Count < 2)
            return 0;
        for (int i = 0; i < points.Count - 2; i++)
            if (distance < m_Distances[i + 1])
                return i;
        return points.Count - 2;
    }

    public Vector2 PointAt(float distance)
    {
        int i = SegmentIndex(distance);
        float span = m_Distances[i + 1] - m_Distances[i];
        float t = span > 1e-5f ? Mathf.Clamp01((distance - m_Distances[i]) / span) : 0f;
        return Vector2.Lerp(points[i], points[i + 1], t);
    }

    public Vector2 DirectionAt(float distance)
    {
        int i = SegmentIndex(distance);
        return (points[i + 1] - points[i]).normalized;
    }

    // The straight piece of route at this distance: its direction and length.
    public void SegmentAt(float distance, out Vector2 direction, out float segmentLength)
    {
        int i = SegmentIndex(distance);
        direction = (points[i + 1] - points[i]).normalized;
        segmentLength = m_Distances[i + 1] - m_Distances[i];
    }

    // Distance along the route of the spot nearest to p, and how far p is from it. The hint (the last answer)
    // keeps it from jumping to another stretch of the route that happens to pass close by.
    public float Project(Vector2 p, float hint, out float offRoute)
    {
        float bestCost = float.PositiveInfinity, bestAlong = 0f;
        offRoute = 0f;
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 a = points[i];
            Vector2 q = Router.ClosestOnSegment(p, a, points[i + 1]);
            float along = m_Distances[i] + Vector2.Distance(q, a);
            float off = Vector2.Distance(p, q);
            // Free within 6 m of the last answer; beyond that, each metre of jump costs a quarter metre.
            float cost = off + 0.25f * Mathf.Max(0f, Mathf.Abs(along - hint) - 6f);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestAlong = along;
                offRoute = off;
            }
        }
        return bestAlong;
    }
}
