using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Shows that a tap landed, even in bright light: the button shrinks slightly while it is held, and a state
// layer (a tint over the whole button, the way flat list rows and icon buttons show a press) fades in. A press
// that turns into a scroll lets go, so a list row doesn't stay lit while the list moves under the finger.
public class PressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public float pressedScale = 0.95f;
    [Tooltip("Optional: faded in to stateLayerAlpha while held.")]
    public Graphic stateLayer;
    public float stateLayerAlpha = 0.12f;

    const float ScrollSlop = 6f;       // canvas units the button may move under the finger before it lets go
    const float ScaleSpeed = 1.5f;     // scale units a second: 1 to 0.95 in about 0.033 s
    const float LayerSpeed = 2f;       // alpha a second

    bool m_Held;
    Vector3 m_PressedAt;

    public void OnPointerDown(PointerEventData e)
    {
        var selectable = GetComponent<Selectable>();
        m_Held = selectable == null || selectable.IsInteractable();
        m_PressedAt = transform.position;
    }

    public void OnPointerUp(PointerEventData e) => m_Held = false;

    public void OnPointerExit(PointerEventData e) => m_Held = false;

    void OnDisable()
    {
        m_Held = false;
        transform.localScale = Vector3.one;
        SetLayerAlpha(0f);
    }

    void Update()
    {
        float layer = stateLayer != null ? stateLayer.color.a : 0f;
        // At rest: touching the transform would make the canvas rebuild every frame.
        if (!m_Held && transform.localScale.x == 1f && layer == 0f)
            return;

        // Scrolled away under the finger.
        if (m_Held && (transform.position - m_PressedAt).magnitude > ScrollSlop * transform.lossyScale.x)
            m_Held = false;

        float dt = Time.unscaledDeltaTime;
        float scale = Mathf.MoveTowards(transform.localScale.x, m_Held ? pressedScale : 1f, dt * ScaleSpeed);
        transform.localScale = new Vector3(scale, scale, 1f);

        if (stateLayer != null)
            SetLayerAlpha(Mathf.MoveTowards(layer, m_Held ? stateLayerAlpha : 0f, dt * LayerSpeed));
    }

    void SetLayerAlpha(float alpha)
    {
        if (stateLayer == null || stateLayer.color.a == alpha)
            return;
        Color c = stateLayer.color;
        c.a = alpha;
        stateLayer.color = c;
    }
}
