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
    public List<PlanSign> signs = new List<PlanSign>();
    public PlanImage image;
    public PlanGeo geo;

    [NonSerialized] Dictionary<string, int> m_NodeIndex;
    [NonSerialized] int m_IndexedCount = -1;

    // Throws FormatException naming what's wrong (an unknown node, a duplicate id, a second piece of walkway...),
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

    // For the editor builder, before it builds anything: false (and why) if the JSON doesn't parse or the plan
    // isn't sound (see Problem).
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

    // The same checks on a plan already in memory (one made in code, say). Null if it's sound.
    public string Check() => Problem();

    // Every check the app relies on: ids unique and resolvable, one connected walkway, doors that face somewhere,
    // labels with areas (basemap.py adds them), signs on real doors, and the image and geo blocks. Null if sound.
    string Problem()
    {
        nodes ??= new List<PlanNode>();
        edges ??= new List<PlanEdge>();
        places ??= new List<PlanPlace>();
        labels ??= new List<PlanLabel>();
        signs ??= new List<PlanSign>();
        if (nodes.Count == 0)
            return "the plan has no nodes.";

        var seen = new HashSet<string>();
        for (int i = 0; i < nodes.Count; i++)
        {
            PlanNode node = nodes[i];
            if (node == null || string.IsNullOrEmpty(node.id))
                return $"node {i} has no id.";
            if (!seen.Add(node.id))
                return $"node id \"{node.id}\" is used twice.";
            if (!Finite(node.x) || !Finite(node.z))
                return $"node \"{node.id}\" has no position.";
        }
        m_NodeIndex = null;   // ids may have changed in place since the last look
        Dictionary<string, int> index = Index();

        for (int i = 0; i < edges.Count; i++)
        {
            PlanEdge edge = edges[i];
            if (edge == null)
                return $"edge {i} is empty.";
            if (!index.ContainsKey(edge.a ?? ""))
                return $"edge {i} ({edge.a} - {edge.b}) names unknown node \"{edge.a}\".";
            if (!index.ContainsKey(edge.b ?? ""))
                return $"edge {i} ({edge.a} - {edge.b}) names unknown node \"{edge.b}\".";
            if (edge.a == edge.b)
                return $"edge {i} joins \"{edge.a}\" to itself.";
        }
        string apart = Unreached(index);
        if (apart != null)
            return $"node \"{apart}\" can't be walked to from \"{nodes[0].id}\": the walkway is in pieces.";

        var placeIds = new HashSet<string>();
        for (int i = 0; i < places.Count; i++)
        {
            PlanPlace place = places[i];
            if (place == null || string.IsNullOrEmpty(place.id))
                return $"place {i} is empty or has no id.";
            if (!placeIds.Add(place.id))
                return $"place id \"{place.id}\" is used twice.";
            if (place.doors == null || place.doors.Count == 0)
                return $"place \"{place.id}\" has no doors.";
            foreach (PlanDoor door in place.doors)
            {
                if (door == null || !index.ContainsKey(door.node ?? ""))
                    return $"place \"{place.id}\" has a door at unknown node \"{door?.node}\".";
                // Lining up turns the whole plan by this direction, so it must be a real one.
                if (!(door.Direction.sqrMagnitude > 0.25f))
                    return $"place \"{place.id}\" has a door at \"{door.node}\" that faces nowhere (dx, dz = 0).";
            }
        }

        foreach (PlanLabel label in labels)
        {
            if (label == null || string.IsNullOrEmpty(label.text))
                return "a label has no text.";
            if (!(label.area > 0f))
                return $"label \"{label.text}\" has no area: run basemap.py after graph.py --export (or make_data.py).";
        }

        foreach (PlanSign sign in signs)
        {
            if (sign == null || string.IsNullOrEmpty(sign.text))
                return "a sign has no text.";
            PlanPlace place = places.Find(p => p.id == sign.place);
            if (place == null)
                return $"sign \"{sign.text}\" belongs to unknown place \"{sign.place}\".";
            if (!place.doors.Exists(d => d.node == sign.door))
                return $"sign \"{sign.text}\" is at \"{sign.door}\", which isn't a door of \"{sign.place}\".";
            if (!Finite(sign.x) || !Finite(sign.z))
                return $"sign \"{sign.text}\" has no position.";
            if (Mathf.Abs(sign.Normal.magnitude - 1f) > 0.05f)
                return $"sign \"{sign.text}\" at \"{sign.door}\" has a normal that isn't unit length.";
        }

        if (image == null || image.width <= 0 || image.height <= 0 || !(image.metersPerPixel > 0f))
            return "the image block (the map picture's extent) is missing or empty.";
        if (geo == null || !geo.IsSet || Mathf.Abs(geo.lat) > 90f || Mathf.Abs(geo.lon) > 180f || !Finite(geo.angle))
            return "the geo block (where the plan sits on Earth) is missing or out of range.";
        return null;
    }

    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    // The first node a walk from nodes[0] can't reach, or null if the walkway is in one piece.
    string Unreached(Dictionary<string, int> index)
    {
        var links = new List<int>[nodes.Count];
        for (int i = 0; i < links.Length; i++)
            links[i] = new List<int>();
        foreach (PlanEdge edge in edges)
        {
            int a = index[edge.a], b = index[edge.b];
            links[a].Add(b);
            links[b].Add(a);
        }
        var reached = new bool[nodes.Count];
        var todo = new Stack<int>();
        todo.Push(0);
        reached[0] = true;
        while (todo.Count > 0)
        {
            foreach (int next in links[todo.Pop()])
            {
                if (!reached[next])
                {
                    reached[next] = true;
                    todo.Push(next);
                }
            }
        }
        int missing = Array.IndexOf(reached, false);
        return missing < 0 ? null : nodes[missing].id;
    }

    // Built on first use and again whenever nodes were added since, so a plan made in code (tests) needs no Load.
    // The first of two nodes with the same id wins; Problem reports the clash.
    Dictionary<string, int> Index()
    {
        if (m_NodeIndex != null && m_IndexedCount == nodes.Count)
            return m_NodeIndex;
        m_NodeIndex = new Dictionary<string, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            string id = nodes[i]?.id;
            if (id != null && !m_NodeIndex.ContainsKey(id))
                m_NodeIndex.Add(id, i);
        }
        m_IndexedCount = nodes.Count;
        return m_NodeIndex;
    }

    // The node's index in nodes, or -1 if there is no such node.
    public int NodeIndex(string id)
    {
        return id != null && nodes != null && Index().TryGetValue(id, out int index) ? index : -1;
    }

    public Vector2 NodePosition(string id)
    {
        int index = NodeIndex(id);
        if (index < 0)
            throw new KeyNotFoundException($"No node \"{id}\" in the floor plan.");
        return nodes[index].Position;
    }

    // FNV-1a (32-bit) of the place ids in order, one per line. The place grid (*-places.bytes) stores place
    // indexes and carries this, so a grid made for another place list or order is caught at load, not shown wrong.
    // Same as graph.place_id_hash in Tools/ba1-floor1.
    public uint PlaceIdHash()
    {
        uint hash = 0x811C9DC5;
        bool first = true;
        foreach (PlanPlace place in places)
        {
            if (!first)
                hash = (hash ^ (byte)'\n') * 0x01000193u;
            first = false;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(place?.id ?? ""))
                hash = (hash ^ b) * 0x01000193u;
        }
        return hash;
    }
}

// Reads the little-endian .bytes files the data pipeline writes (places, walls, campus), checking every read
// against the length, so a short, stale or foreign file fails with a FormatException that names the file and
// what's wrong instead of an index error somewhere in the map.
public sealed class DataReader
{
    readonly byte[] m_Bytes;
    readonly string m_Name;
    int m_At;

    public DataReader(byte[] bytes, string name)
    {
        m_Bytes = bytes ?? Array.Empty<byte>();
        m_Name = name;
    }

    public FormatException Fail(string why) => new FormatException($"{m_Name}: {why}");

    // The 4-letter magic and the uint16 version every file starts with.
    public void Header(string magic, int version)
    {
        Need(6);
        for (int i = 0; i < 4; i++)
        {
            if (m_Bytes[i] != magic[i])
                throw Fail($"not a {magic} file; build it again with Tools/ba1-floor1/make_data.py");
        }
        m_At = 4;
        int found = UInt16();
        if (found != version)
            throw Fail($"version {found}, this app reads {version}; build it again with make_data.py");
    }

    public int UInt16()
    {
        Need(2);
        int v = BitConverter.ToUInt16(m_Bytes, m_At);
        m_At += 2;
        return v;
    }

    public uint UInt32()
    {
        Need(4);
        uint v = BitConverter.ToUInt32(m_Bytes, m_At);
        m_At += 4;
        return v;
    }

    public int Int32()
    {
        Need(4);
        int v = BitConverter.ToInt32(m_Bytes, m_At);
        m_At += 4;
        return v;
    }

    // A count of items each `size` bytes long, checked to fit in what's left.
    public int Count(string what, int size)
    {
        int n = Int32();
        if (n < 0 || (long)n * size > m_Bytes.Length - m_At)
            throw Fail($"{n} {what} don't fit in the {m_Bytes.Length - m_At} bytes left");
        return n;
    }

    public byte[] Bytes(int count)
    {
        Need(count);
        var bytes = new byte[count];
        Buffer.BlockCopy(m_Bytes, m_At, bytes, 0, count);
        m_At += count;
        return bytes;
    }

    // count points stored as int16 x, y pairs, times scale.
    public Vector2[] Points(int count, float scale)
    {
        Need(count * 4L);
        var points = new Vector2[count];
        for (int i = 0; i < count; i++, m_At += 4)
            points[i] = new Vector2(BitConverter.ToInt16(m_Bytes, m_At), BitConverter.ToInt16(m_Bytes, m_At + 2)) * scale;
        return points;
    }

    public void End()
    {
        if (m_At != m_Bytes.Length)
            throw Fail($"{m_Bytes.Length - m_At} bytes left over after the data");
    }

    void Need(long count)
    {
        if (count < 0 || m_At + count > m_Bytes.Length)
            throw Fail($"ends early, at byte {m_Bytes.Length} (needed {m_At + count})");
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

// A room-number plate on the wall beside a door, for reading with the camera: text as printed ("121", "107A"),
// (x, z) on the wall in plan metres, (nx, nz) the unit normal out of the wall into the hallway, and the door node
// and place it belongs to.
[Serializable]
public class PlanSign
{
    public string text;
    public float x, z;
    public float nx, nz;
    public string door;
    public string place;

    public Vector2 Position => new Vector2(x, z);
    public Vector2 Normal => new Vector2(nx, nz);
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
