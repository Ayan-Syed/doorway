using System;
using UnityEngine;
using UnityEngine.EventSystems;

// A sheet that slides up over the map from the bottom edge, like a map app's place sheet. Drag it by any part
// that isn't a list; let go and it settles at the nearest stop, or at the next one the way it was flung. Put it
// on a RectTransform anchored along the bottom with its pivot at the top: its y position is how much of it
// shows. It's `extra` taller than the highest stop, so an overshoot never shows a gap underneath.
[RequireComponent(typeof(RectTransform))]
public class BottomSheet : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    [Tooltip("Visible heights it settles at, lowest first.")]
    public float[] stops = { 300f };
    public float extra = 400f;
    public bool draggable = true;

    public event Action<int> StopChanged;

    public int Stop { get; private set; }
    public float Height => Rect.anchoredPosition.y;
    public bool IsAtTop => Stop == stops.Length - 1;
    public bool IsDragging { get; private set; }

    const float Settle = 14f;           // 1 - exp(-dt * Settle) of the way each frame
    const float RubberBand = 120f;      // overshoot at which a drag stops moving it
    const float FlickSpeed = 900f;      // canvas units a second
    const float FlingPause = 0.08f;     // seconds without a drag that kill the fling

    RectTransform m_Rect;
    float m_Target;
    bool m_HasTarget;   // until the first MoveTo it stays where the scene builder put it
    float m_Velocity;
    float m_LastDragTime;

    RectTransform Rect => m_Rect != null ? m_Rect : (m_Rect = (RectTransform)transform);

    // Screen pixels to canvas units.
    float Scale
    {
        get
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            return canvas != null ? canvas.rootCanvas.scaleFactor : 1f;
        }
    }

    public void SetStops(float[] heights, int stop, bool instant)
    {
        stops = heights;
        float highest = 0f;
        foreach (float h in heights)
            highest = Mathf.Max(highest, h);
        Vector2 size = Rect.sizeDelta;
        size.y = highest + extra;
        Rect.sizeDelta = size;
        MoveTo(stop, instant);
    }

    // Settles at a stop. StopChanged fires at once (before the slide ends), and only if the stop changed.
    public void MoveTo(int stop, bool instant)
    {
        stop = Mathf.Clamp(stop, 0, stops.Length - 1);
        m_Target = stops[stop];
        m_HasTarget = true;
        if (instant || !Application.isPlaying)
            SetHeight(m_Target);
        if (stop != Stop)
        {
            Stop = stop;
            StopChanged?.Invoke(stop);
        }
    }

    // Slides up from the bottom edge to a stop.
    public void Present(int stop)
    {
        SetHeight(0f);
        MoveTo(stop, !Application.isPlaying);
    }

    void SetHeight(float h)
    {
        Vector2 p = Rect.anchoredPosition;
        p.y = h;
        Rect.anchoredPosition = p;
    }

    void Update()
    {
        float h = Height;
        if (!m_HasTarget || IsDragging || Mathf.Approximately(h, m_Target))
            return;
        float next = Mathf.Lerp(h, m_Target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * Settle));
        SetHeight(Mathf.Abs(next - m_Target) < 0.5f ? m_Target : next);
    }

    // Hidden mid-drag, it never hears the drag end: without this it would think it's still held, and never slide
    // to a stop again (Present would leave it at the bottom edge).
    void OnDisable()
    {
        IsDragging = false;
        m_Velocity = 0f;
    }

    public void OnBeginDrag(PointerEventData e)
    {
        if (!draggable)
            return;
        IsDragging = true;
        m_Velocity = 0f;
    }

    public void OnDrag(PointerEventData e)
    {
        if (!IsDragging)
            return;
        float delta = e.delta.y / Scale;
        float h = Height;
        float low = stops[0], high = stops[stops.Length - 1];

        // Past the ends it moves less and less.
        float give = 1f;
        if (h > high)
            give = Mathf.Clamp01(1f - (h - high) / RubberBand);
        else if (h < low)
            give = Mathf.Clamp01(1f - (low - h) / RubberBand);
        SetHeight(h + delta * give);

        float dt = Time.unscaledDeltaTime;
        m_Velocity = Mathf.Lerp(m_Velocity, delta / Mathf.Max(dt, 0.001f), 0.6f);
        m_LastDragTime = Time.unscaledTime;
    }

    public void OnEndDrag(PointerEventData e)
    {
        if (!IsDragging)
            return;
        IsDragging = false;

        float velocity = Time.unscaledTime - m_LastDragTime > FlingPause ? 0f : m_Velocity;   // a pause kills the fling
        float h = Height;
        int nearest = 0;
        for (int i = 1; i < stops.Length; i++)
            if (Mathf.Abs(stops[i] - h) < Mathf.Abs(stops[nearest] - h))
                nearest = i;

        int stop = nearest;
        // A flick goes on to the next stop that way.
        if (Mathf.Abs(velocity) > FlickSpeed)
        {
            stop = velocity > 0f ? stops.Length - 1 : 0;
            for (int i = 0; i < stops.Length; i++)
                if (velocity > 0f && stops[i] > h + 1f) { stop = i; break; }
            for (int i = stops.Length - 1; i >= 0; i--)
                if (velocity < 0f && stops[i] < h - 1f) { stop = i; break; }
        }
        MoveTo(stop, false);
    }
}
