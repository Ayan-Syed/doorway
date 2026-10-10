using System.Collections.Generic;
using UnityEngine;

// The route drawn on the floor: a band with chevrons (the material scrolls them forward), from the user's feet to a
// few dozen metres ahead, rounded at the corners, fading in at the start and out at the far end. Lives under the
// plan root, so the mesh is in plan metres (x, z) just above the floor.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RouteRibbon : MonoBehaviour
{
    public float width = 0.55f;
    [Tooltip("Metres per repeat of the texture (one chevron).")]
    public float tileLength = 0.6f;
    public float lookAhead = 28f;
    [Tooltip("Lift above the floor, so it doesn't fight with the real floor or AR planes.")]
    public float height = 0.03f;
    public float cornerRadius = 1.2f;
    public float fadeIn = 1.5f;
    [Tooltip("Fade at the far end when that isn't the destination.")]
    public float fadeOut = 6f;

    // Longest gap between vertices: the fades are vertex colours, so long stretches need vertices along them.
    const float MaxSegment = 0.5f;

    Mesh m_Mesh;
    Route m_Route;
    float m_From = float.NegativeInfinity;
    readonly List<Vector2> m_Line = new List<Vector2>();
    readonly List<float> m_LineDistances = new List<float>();
    readonly List<Vector2> m_Path = new List<Vector2>();
    readonly List<float> m_PathDistances = new List<float>();
    readonly List<Vector3> m_Vertices = new List<Vector3>();
    readonly List<Vector2> m_Uvs = new List<Vector2>();
    readonly List<Color32> m_Colors = new List<Color32>();
    readonly List<int> m_Triangles = new List<int>();
    readonly List<Color32> m_Faded = new List<Color32>();

    // How sure guidance is (NavigationScreen.SetGuidance): the whole band fades toward a target and the chevrons
    // stop running while it's unsure, so an unsure route never looks like a confident one.
    const float FadeSpeed = 2.5f;          // alpha a second: about 0.3 s from full to faint
    const float StillBelow = 0.6f;         // the chevrons stop (or run again) once faded past this
    static readonly int ScrollSpeedId = Shader.PropertyToID("_ScrollSpeed");
    float m_Alpha = 1f, m_TargetAlpha = 1f;
    bool m_Scrolling = true;
    float m_ScrollSpeed = float.NaN;
    MaterialPropertyBlock m_Block;

    // alpha: 1 normal, about 0.3 unsure, 0 hidden (lost). scroll: the chevrons run. instant: no fade (the editor's
    // previews, which have no Update).
    public void SetGuidance(float alpha, bool scroll, bool instant = false)
    {
        m_TargetAlpha = Mathf.Clamp01(alpha);
        m_Scrolling = scroll;
        if (!instant || m_Alpha == m_TargetAlpha)
            return;
        m_Alpha = m_TargetAlpha;
        ApplyAlpha();
        SetScrolling(scroll);
    }

    void Update()
    {
        if (m_Alpha != m_TargetAlpha)
        {
            m_Alpha = Mathf.MoveTowards(m_Alpha, m_TargetAlpha, Time.unscaledDeltaTime * FadeSpeed);
            ApplyAlpha();
        }
        // The chevrons stop or start only while faint, where the jump to the shader's resting phase doesn't show.
        SetScrolling(m_Scrolling || m_Alpha > StillBelow);
    }

    void SetScrolling(bool run)
    {
        var renderer = GetComponent<MeshRenderer>();
        if (renderer == null || renderer.sharedMaterial == null)
            return;
        float speed = run ? renderer.sharedMaterial.GetFloat(ScrollSpeedId) : 0f;
        if (speed == m_ScrollSpeed)
            return;
        m_ScrollSpeed = speed;
        m_Block ??= new MaterialPropertyBlock();
        renderer.GetPropertyBlock(m_Block);
        m_Block.SetFloat(ScrollSpeedId, speed);
        renderer.SetPropertyBlock(m_Block);
    }

    // The fades' vertex alphas times the band's alpha, without building the mesh again.
    void ApplyAlpha()
    {
        if (m_Mesh == null || m_Colors.Count == 0 || m_Mesh.vertexCount != m_Colors.Count)
            return;
        m_Faded.Clear();
        foreach (Color32 c in m_Colors)
            m_Faded.Add(new Color32(c.r, c.g, c.b, (byte)(c.a * m_Alpha)));
        m_Mesh.SetColors(m_Faded);
    }

    void EnsureMesh()
    {
        if (m_Mesh != null)
            return;
        m_Mesh = new Mesh { name = "Route Ribbon" };
        m_Mesh.MarkDynamic();
        GetComponent<MeshFilter>().sharedMesh = m_Mesh;
    }

    // Draws the route ahead of this distance along it. Cheap to call every frame: rebuilds only on change.
    public void Show(Route route, float from)
    {
        if (route == m_Route && Mathf.Abs(from - m_From) < 0.05f)
            return;
        m_Route = route;
        m_From = from;
        Build();
    }

    public void Hide()
    {
        m_Route = null;
        m_From = float.NegativeInfinity;
        if (m_Mesh != null)
            m_Mesh.Clear();
    }

    void Build()
    {
        EnsureMesh();
        m_Mesh.Clear();
        if (m_Route == null)
            return;
        // The band starts a little ahead of the user; the walked part isn't drawn.
        float start = Mathf.Clamp(m_From + 0.3f, 0f, m_Route.length);
        float end = Mathf.Min(m_Route.length, start + lookAhead);
        if (end - start < 0.1f)
            return;

        m_Line.Clear();
        m_LineDistances.Clear();
        m_Line.Add(m_Route.PointAt(start));
        m_LineDistances.Add(start);
        for (int i = 1; i < m_Route.points.Count - 1; i++)
        {
            float d = m_Route.DistanceAtPoint(i);
            if (d > start + 0.01f && d < end - 0.01f)
            {
                m_Line.Add(m_Route.points[i]);
                m_LineDistances.Add(d);
            }
        }
        m_Line.Add(m_Route.PointAt(end));
        m_LineDistances.Add(end);

        RoundCorners();
        BuildStrip(start, end, end >= m_Route.length - 0.01f);
    }

    // Each corner becomes a short curve that starts and ends up to cornerRadius from it.
    void RoundCorners()
    {
        m_Path.Clear();
        m_PathDistances.Clear();
        AddPathPoint(m_Line[0], m_LineDistances[0]);
        for (int i = 1; i < m_Line.Count - 1; i++)
        {
            Vector2 before = m_Line[i - 1], corner = m_Line[i], after = m_Line[i + 1];
            // Under half of each side, so two curves never overlap on a short piece.
            float r = Mathf.Min(cornerRadius, 0.45f * Vector2.Distance(before, corner), 0.45f * Vector2.Distance(corner, after));
            Vector2 a = corner + (before - corner).normalized * r;
            Vector2 b = corner + (after - corner).normalized * r;
            const int steps = 8;
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                // A quadratic Bezier with the corner as its control point. The distance is a straight stand-in,
                // only used for the fades.
                Vector2 p = (1 - t) * (1 - t) * a + 2 * (1 - t) * t * corner + t * t * b;
                AddPathPoint(p, m_LineDistances[i] + (t - 0.5f) * 2f * r);
            }
        }
        AddPathPoint(m_Line[m_Line.Count - 1], m_LineDistances[m_LineDistances.Count - 1]);
    }

    // Adds a point to the path, after evenly spaced points along the straight stretch leading to it. The fades are
    // vertex colours, so they need vertices where they happen, not just at the ends of a long stretch.
    void AddPathPoint(Vector2 point, float distance)
    {
        int count = m_Path.Count;
        if (count > 0)
        {
            Vector2 last = m_Path[count - 1];
            float lastDistance = m_PathDistances[count - 1];
            int pieces = Mathf.CeilToInt(Vector2.Distance(last, point) / MaxSegment);
            for (int k = 1; k < pieces; k++)
            {
                float t = k / (float)pieces;
                m_Path.Add(Vector2.Lerp(last, point, t));
                m_PathDistances.Add(Mathf.Lerp(lastDistance, distance, t));
            }
        }
        m_Path.Add(point);
        m_PathDistances.Add(distance);
    }

    void BuildStrip(float start, float end, bool reachesDestination)
    {
        m_Vertices.Clear();
        m_Uvs.Clear();
        m_Colors.Clear();
        m_Triangles.Clear();
        float half = width * 0.5f;
        int last = m_Path.Count - 1;
        // Texture distance runs along the actual (curved) path, starting from the route distance so the chevrons
        // stay put on the floor as the start of the band moves with the user. Less whole repeats, so v stays small
        // enough for full precision on the GPU.
        float travelled = start;
        float repeats = Mathf.Floor(start / tileLength);
        // Ends almost crisply at the door spot; otherwise fades out down the hallway.
        float tail = reachesDestination ? 0.4f : fadeOut;
        for (int i = 0; i < m_Path.Count; i++)
        {
            if (i > 0)
                travelled += Vector2.Distance(m_Path[i - 1], m_Path[i]);
            Vector2 tangent = (m_Path[Mathf.Min(i + 1, last)] - m_Path[Mathf.Max(i - 1, 0)]).normalized;
            var left = new Vector2(-tangent.y, tangent.x);
            float d = m_PathDistances[i];
            float alpha = Mathf.Clamp01((d - start) / fadeIn) * Mathf.Clamp01((end - d) / tail);
            var color = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * Smooth(alpha)));

            Vector2 l = m_Path[i] + left * half, r = m_Path[i] - left * half;
            m_Vertices.Add(new Vector3(l.x, height, l.y));
            m_Vertices.Add(new Vector3(r.x, height, r.y));
            float v = travelled / tileLength - repeats;
            m_Uvs.Add(new Vector2(0f, v));
            m_Uvs.Add(new Vector2(1f, v));
            m_Colors.Add(color);
            m_Colors.Add(color);
            if (i > 0)
            {
                // The shader draws both sides, so the winding doesn't matter.
                int k = i * 2;
                m_Triangles.Add(k - 2); m_Triangles.Add(k); m_Triangles.Add(k - 1);
                m_Triangles.Add(k - 1); m_Triangles.Add(k); m_Triangles.Add(k + 1);
            }
        }
        m_Mesh.SetVertices(m_Vertices);
        m_Mesh.SetUVs(0, m_Uvs);
        m_Mesh.SetColors(m_Colors);
        if (m_Alpha < 1f)
            ApplyAlpha();
        m_Mesh.SetTriangles(m_Triangles, 0);
        m_Mesh.RecalculateBounds();
    }

    static float Smooth(float t) => t * t * (3f - 2f * t);
}
