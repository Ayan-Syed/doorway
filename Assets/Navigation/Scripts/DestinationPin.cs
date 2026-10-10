using UnityEngine;
using UnityEngine.UI;

// Marks the destination's door: a ring on the floor with a pulse spreading out of it, a beam of light that can be
// seen from down the hallway, and a floating name label that turns to face the camera and grows with distance so it
// stays readable. Lives under the plan root; the parts are built by the scene builder.
public class DestinationPin : MonoBehaviour
{
    public Transform arCamera;
    public Transform ring;
    public Transform pulse;
    public Transform beam;
    public Renderer pulseRenderer;
    public RectTransform label;
    public Image labelBackground;
    public Text labelIcon;
    public Text labelText;
    [Tooltip("Corner radius of the label background sprite's 9-slice border, in sprite pixels.")]
    public float labelBackgroundRadius = 64f;
    public Color color = Color.white;
    [Tooltip("Label height from afar, above people's heads, so it can be seen down the hallway.")]
    public float labelHeight = 1.75f;
    [Tooltip("Label height up close, below eye level, so it stays under the instruction card on screen.")]
    public float nearLabelHeight = 1.1f;
    [Tooltip("Metres per canvas unit at the base scale.")]
    public float labelScale = 0.0016f;

    static readonly int ColorId = Shader.PropertyToID("_Color");
    MaterialPropertyBlock m_Block;

    // position: the hallway spot outside the door; doorDirection points from there to the door.
    public void Show(PlanPlace place, Vector2 position, Vector2 doorDirection)
    {
        Vector2 at = position + doorDirection.normalized * 0.5f;   // half a metre on, toward the door
        transform.localPosition = new Vector3(at.x, 0f, at.y);
        labelIcon.text = PlaceStyle.Icon(place.kind);
        labelText.text = PlaceStyle.Title(place);

        // Sized to the text, with fully rounded ends.
        float height = labelText.fontSize * 1.9f;
        float iconWidth = labelIcon.fontSize * 1.1f;
        label.sizeDelta = new Vector2(labelText.preferredWidth + iconWidth + labelText.fontSize * 1.3f, height);
        labelBackground.pixelsPerUnitMultiplier = labelBackgroundRadius / (height * 0.5f);
        gameObject.SetActive(true);
    }

    public void ApplyTheme(Theme theme)
    {
        color = theme.destination;
        SetColor(ring, color);
        SetColor(pulse, color);
        Color beamColor = color;
        beamColor.a = 0.5f;
        SetColor(beam, beamColor);
        if (labelBackground != null)
            labelBackground.color = theme.surface;
        if (labelIcon != null)
            labelIcon.color = theme.destination;
        if (labelText != null)
            labelText.color = theme.onSurface;
    }

    static void SetColor(Transform part, Color c)
    {
        if (part == null || !part.TryGetComponent(out Renderer renderer))
            return;
        Material material = Application.isPlaying ? renderer.material : renderer.sharedMaterial;
        if (material != null)
            material.SetColor(ColorId, c);
    }

    void LateUpdate()
    {
        if (arCamera != null)
            FaceCamera(arCamera.position, Time.time);
    }

    // Public so the scene builder can pose it for preview renders.
    public void FaceCamera(Vector3 cameraPosition, float time)
    {
        // A pulse every 1.6 s: grows from the ring outward and fades. Through a property block, so the material
        // asset isn't touched every frame.
        float t = time / 1.6f % 1f;
        pulse.localScale = Vector3.one * Mathf.Lerp(0.9f, 2.2f, t);
        Color c = color;
        c.a = 0.8f * (1f - t) * (1f - t);
        m_Block ??= new MaterialPropertyBlock();
        m_Block.SetColor(ColorId, c);
        pulseRenderer.SetPropertyBlock(m_Block);

        // The beam turns about the vertical only, so it always shows its full width.
        Vector3 toCamera = cameraPosition - beam.position;
        toCamera.y = 0f;
        if (toCamera.sqrMagnitude > 0.01f)
            beam.rotation = Quaternion.LookRotation(-toCamera, Vector3.up);

        // The label comes down as the camera gets close, bobs gently, and keeps about the same size on screen past
        // a few metres.
        float near = Mathf.InverseLerp(8f, 3f, Mathf.Sqrt(toCamera.sqrMagnitude));   // 0 at 8 m or more, 1 at 3 m or less
        float height = Mathf.Lerp(labelHeight, nearLabelHeight, near * near * (3f - 2f * near));
        label.localPosition = new Vector3(0f, height + 0.05f * Mathf.Sin(time * 2f), 0f);
        float distance = Vector3.Distance(cameraPosition, label.position);
        label.localScale = Vector3.one * labelScale * Mathf.Clamp(distance / 3.5f, 1f, 5f);
        label.rotation = Quaternion.LookRotation(label.position - cameraPosition, Vector3.up);
    }
}
