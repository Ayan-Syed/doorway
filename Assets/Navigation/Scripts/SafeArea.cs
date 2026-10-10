using UnityEngine;

// Fits this RectTransform to the part of the screen not covered by the camera hole, rounded corners, or system
// bars. Put it on a full-screen object directly under the root canvas (Screen Space - Overlay or - Camera:
// the anchors are fractions of the screen either way).
[RequireComponent(typeof(RectTransform))]
public class SafeArea : MonoBehaviour
{
    Rect m_Area;
    Vector2Int m_Screen;

    // Every frame, but it writes only on a change, so rotation and inset changes are picked up.
    void Update()
    {
        Rect area = Screen.safeArea;
        var screen = new Vector2Int(Screen.width, Screen.height);
        if ((area == m_Area && screen == m_Screen) || screen.x == 0 || screen.y == 0)
            return;
        m_Area = area;
        m_Screen = screen;

        var rect = (RectTransform)transform;
        rect.anchorMin = new Vector2(area.xMin / screen.x, area.yMin / screen.y);
        rect.anchorMax = new Vector2(area.xMax / screen.x, area.yMax / screen.y);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
