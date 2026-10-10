using System;
using System.Collections.Generic;
using UnityEngine;

// One floor's walkways and places, exported from the floor plan (Data/ba1-floor1.json).
// Positions are plan metres: x toward the plan's right, z toward the plan's top. Vector2 (x, z) throughout.
// Field names match the JSON keys, because JsonUtility maps them by name.
[Serializable]
public class FloorPlan
{
    public string building;
    public string floor;
    public List<PlanNode> nodes = new List<PlanNode>();
    public List<PlanEdge> edges = new List<PlanEdge>();
    public List<PlanPlace> places = new List<PlanPlace>();
    public List<PlanLabel> labels = new List<PlanLabel>();
    public PlanImage image;
    public PlanGeo geo;

    [NonSerialized] Dictionary<string, int> m_NodeIndex;

    // Throws FormatException naming the bad id if an edge, door or place points at a node that isn't there,
    // so broken data fails here, at load, and not deep inside the Router mid-walk.
    public static FloorPlan Load(TextAsset json)
    {
        if (json == null)
            throw new ArgumentNullException(nameof(json), "No floor plan JSON assigned.");
        FloorPlan plan = JsonUtility.FromJson<FloorPlan>(json.text);
        if (plan == null)
            throw new FormatException($"{json.name}: not a floor plan.");
        string problem = plan.Problem();
        if (problem != null)
            throw new FormatException($"{json.name}: {problem}");
        return plan;
    }

    // For the editor builder, before it builds anything: false (and why) if the JSON doesn't parse or refers to
    // a node that doesn't exist.
    public static bool Validate(string json, out string error)
    {
        FloorPlan plan;
        try
        {
            plan = JsonUtility.FromJson<FloorPlan>(json ?? "");
        }
        catch (Exception e)
        {
            error = $"Not valid JSON: {e.Message}";
            return false;
        }
        error = plan == null ? "Not a floor plan." : plan.Problem();
        return error == null;
    }

    // Fills the node index and checks every reference to a node. Null if the plan is sound.
    string Problem()
    {
        nodes ??= new List<PlanNode>();
        edges ??= new List<PlanEdge>();
        places ??= new List<PlanPlace>();
        labels ??= new List<PlanLabel>();
        if (nodes.Count == 0)
            return "the plan has no nodes.";

        m_NodeIndex = new Dictionary<string, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            string id = nodes[i]?.id;
            if (string.IsNullOrEmpty(id))
                return $"node {i} has no id.";
            if (m_NodeIndex.ContainsKey(id))
                return $"node id \"{id}\" is used twice.";
            m_NodeIndex.Add(id, i);
        }

        for (int i = 0; i < edges.Count; i++)
        {
            PlanEdge edge = edges[i];
            if (edge == null)
                return $"edge {i} is empty.";
            if (!m_NodeIndex.ContainsKey(edge.a ?? ""))
                return $"edge {i} ({edge.a} - {edge.b}) names unknown node \"{edge.a}\".";
            if (!m_NodeIndex.ContainsKey(edge.b ?? ""))
                return $"edge {i} ({edge.a} - {edge.b}) names unknown node \"{edge.b}\".";
        }

        for (int i = 0; i < places.Count; i++)
        {
            PlanPlace place = places[i];
            if (place == null)
                return $"place {i} is empty.";
            if (place.doors == null || place.doors.Count == 0)
                return $"place \"{place.id}\" has no doors.";
            foreach (PlanDoor door in place.doors)
            {
                if (door == null || !m_NodeIndex.ContainsKey(door.node ?? ""))
                    return $"place \"{place.id}\" has a door at unknown node \"{door?.node}\".";
            }
        }
        return null;
    }

    // The node's index in nodes, or -1 if there is no such node.
    public int NodeIndex(string id)
    {
        return id != null && m_NodeIndex != null && m_NodeIndex.TryGetValue(id, out int index) ? index : -1;
    }

    public Vector2 NodePosition(string id)
    {
        int index = NodeIndex(id);
        if (index < 0)
            throw new KeyNotFoundException($"No node \"{id}\" in the floor plan.");
        return nodes[index].Position;
    }
}

[Serializable]
public class PlanNode
{
    public string id;
    public float x;
    public float z;

    public Vector2 Position => new Vector2(x, z);
}

// An undirected walkway link between two node ids.
[Serializable]
public class PlanEdge
{
    public string a;
    public string b;
}

// A room, restroom, elevator, stairs or exit. kind is one of those words.
[Serializable]
public class PlanPlace
{
    public string id;
    public string name;
    public string detail;
    public string kind;
    public List<PlanDoor> doors = new List<PlanDoor>();
}

// A spot in the hallway right outside a door. (dx, dz) points from that spot toward the door.
[Serializable]
public class PlanDoor
{
    public string node;
    public string label;
    public float dx;
    public float dz;

    public Vector2 Direction => new Vector2(dx, dz);
}

// A room number, drawn where the room has the most space. area is the room's area in m², so the map can show
// a label only once the room is big enough on screen.
[Serializable]
public class PlanLabel
{
    public string text;
    public float x;
    public float z;
    public float area;

    public Vector2 Position => new Vector2(x, z);
}

// Where the plan image sits: its top-left corner in plan metres, and its size.
[Serializable]
public class PlanImage
{
    public float left;
    public float top;
    public float metersPerPixel;
    public int width;
    public int height;
}

// Where the plan sits on Earth: the plan point (x, z) is at (lat, lon), and east/north metres turned angle degrees
// anticlockwise are plan metres. (So the plan's +x is angle degrees clockwise from east.)
[Serializable]
public class PlanGeo
{
    public float lat;
    public float lon;
    public float angle;
    public float x;
    public float z;

    public bool IsSet => lat != 0f || lon != 0f;

    // Plan metres (x, z) of a latitude and longitude: a flat Earth around the anchor, which is plenty across a
    // campus. Same maths as Tools/campus/campus.py; keep them identical.
    public Vector2 ToPlan(double latitude, double longitude)
    {
        const double EarthRadius = 6378137.0;   // WGS-84 equatorial radius, metres
        double toRadians = Math.PI / 180.0;
        double east = (longitude - lon) * toRadians * EarthRadius * Math.Cos(lat * toRadians);
        double north = (latitude - lat) * toRadians * EarthRadius;
        double a = angle * toRadians;
        return new Vector2(
            (float)(east * Math.Cos(a) - north * Math.Sin(a)) + x,
            (float)(east * Math.Sin(a) + north * Math.Cos(a)) + z);
    }
}
