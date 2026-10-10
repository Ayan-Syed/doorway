using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// A spinner for waiting, the way Material draws one: an arc with round ends that grows and shrinks as it
// turns, its edges softened by a pixel. Drawn in the graphic's colour, inside its rect.
[RequireComponent(typeof(CanvasRenderer))]
public class Spinner : MaskableGraphic
{
    public float thickness = 8f;
    [Tooltip("Seconds for the arc to grow and shrink again.")]
    public float period = 1.4f;
    [Tooltip("Degrees a second the whole arc turns.")]
    public float turnSpeed = 220f;

    const float MinSweep = 16f;
    const float MaxSweep = 270f;
    const int CapSteps = 8;        // points on each round end
    const float Feather = 1.2f;    // px of soft edge

    static readonly List<Vector2> s_Points = new List<Vector2>();
    static readonly List<Vector2> s_Normals = new List<Vector2>();

    float m_Time;

    // For the preview renders: a fixed moment of the spin.
    public void SetTime(float seconds)
    {
        m_Time = seconds;
        SetVerticesDirty();
    }

    void Update()
    {
        m_Time += Time.unscaledDeltaTime;
        SetVerticesDirty();
    }

    // 0 degrees is the top; angles grow clockwise.
    static Vector2 Direction(float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
    }

    // Smoothstep, like Material's easing.
    static float Ease(float x) => x * x * (3f - 2f * x);

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect rect = rectTransform.rect;
        float half = thickness * 0.5f;
        float radius = Mathf.Min(rect.width, rect.height) * 0.5f - half - Feather;   // the band's centre line
        if (radius <= 0f)
            return;
        Vector2 middle = rect.center;

        // The head runs ahead in the first half of the cycle and the tail catches up in the second; each cycle
        // starts where the last left off.
        float cycle = m_Time / period;
        int count = Mathf.FloorToInt(cycle);
        float t = cycle - count;
        float grow = MaxSweep - MinSweep;
        float head = t < 0.5f ? Ease(t * 2f) * grow : grow;
        float tail = t < 0.5f ? 0f : Ease(t * 2f - 1f) * grow;
        float from = count * grow + m_Time * turnSpeed + tail;
        float to = count * grow + m_Time * turnSpeed + head + MinSweep;
        int steps = Mathf.Max(2, Mathf.CeilToInt((to - from) / 6f));   // a point every 6 degrees or less

        // The outline: the outer edge, the head's round end, the inner edge back, the tail's round end.
        s_Points.Clear();
        s_Normals.Clear();
        for (int i = 0; i <= steps; i++)
        {
            Vector2 u = Direction(Mathf.Lerp(from, to, (float)i / steps));
            s_Points.Add(middle + u * (radius + half));
            s_Normals.Add(u);
        }
        RoundEnd(middle, radius, half, to, 1f);
        int inner = steps + CapSteps;
        for (int i = steps; i >= 0; i--)
        {
            Vector2 u = Direction(Mathf.Lerp(from, to, (float)i / steps));
            s_Points.Add(middle + u * (radius - half));
            s_Normals.Add(-u);
        }
        RoundEnd(middle, radius, half, from, -1f);
        int n = s_Points.Count;   // 2 * steps + 16

        Color32 solid = color;
        Color32 clear = solid;
        clear.a = 0;
        for (int i = 0; i < n; i++)
            vh.AddVert(s_Points[i], solid, Vector4.zero);
        for (int i = 0; i < n; i++)
            vh.AddVert(s_Points[i] + s_Normals[i] * Feather, clear, Vector4.zero);

        // The band between the edges.
        for (int i = 0; i < steps; i++)
        {
            int c = inner + steps - i - 1, d = inner + steps - i;
            vh.AddTriangle(i, i + 1, c);
            vh.AddTriangle(i, c, d);
        }

        // Each round end is a fan from its centre on the band's middle line.
        int headCentre = vh.currentVertCount;
        vh.AddVert(middle + Direction(to) * radius, solid, Vector4.zero);
        for (int i = steps; i < inner; i++)
            vh.AddTriangle(headCentre, i, i + 1);
        int tailCentre = vh.currentVertCount;
        vh.AddVert(middle + Direction(from) * radius, solid, Vector4.zero);
        for (int i = inner + steps; i < n; i++)
            vh.AddTriangle(tailCentre, i, (i + 1) % n);

        // The soft rim: a strip between every outline point and its feathered copy, all the way round.
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            vh.AddTriangle(i, j, n + j);
            vh.AddTriangle(i, n + j, n + i);
        }
    }

    // A half circle of radius `half` round the band's end at `angle`, without its first and last points (the
    // edges already have them). forward +1 for the head (it bulges clockwise), -1 for the tail.
    static void RoundEnd(Vector2 middle, float radius, float half, float angle, float forward)
    {
        Vector2 u = Direction(angle);
        Vector2 centre = middle + u * radius;
        Vector2 along = new Vector2(u.y, -u.x) * forward;   // clockwise along the arc, times forward
        for (int k = 1; k < CapSteps; k++)
        {
            float a = Mathf.PI * k / CapSteps;
            Vector2 normal = u * (forward * Mathf.Cos(a)) + along * Mathf.Sin(a);
            s_Points.Add(centre + normal * half);
            s_Normals.Add(normal);
        }
    }
}
