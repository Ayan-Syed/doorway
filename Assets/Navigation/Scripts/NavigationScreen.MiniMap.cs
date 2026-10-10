using UnityEngine;

// The minimap while walking: a tap makes it big (the screen's width, up to under the banner) or small again, and a
// pinch or the mouse wheel zooms it; it keeps following the user either way. Each walk starts small, at its usual
// zoom. The frame's size eases there; FloorMap lays itself out for its new size every frame it follows.
public partial class NavigationScreen
{
    // Its small size (the scene builder's), and how far the big one keeps from the screen's sides and the banner.
    const float MiniMapSmall = 380f;
    const float MiniMapSide = 32f;
    const float MiniMapUnderBanner = 24f;
    // Material Icons: fullscreen, fullscreen_exit.
    public const string IconMiniMapGrow = "";
    const string IconMiniMapShrink = "";

    bool m_MiniMapBig;
    float m_MiniMapZoom = float.NaN;   // the minimap's usual zoom, as built

    void InitMiniMap()
    {
        if (miniMap == null || miniMapFrame == null)
            return;
        m_MiniMapZoom = miniMap.followZoom;
        miniMap.zoomGestures = true;
        miniMap.Tapped += () =>
        {
            m_MiniMapBig = !m_MiniMapBig;
            Haptics.Tick(15);
            StyleMiniMapIcon();
        };
    }

    // A new walk: small, at the usual zoom.
    void ResetMiniMap()
    {
        if (miniMap == null || miniMapFrame == null)
            return;
        m_MiniMapBig = false;
        if (!float.IsNaN(m_MiniMapZoom))
            miniMap.followZoom = m_MiniMapZoom;
        StyleMiniMapIcon();
        SizeMiniMap(true);
    }

    void StyleMiniMapIcon()
    {
        if (miniMapIcon != null)
            miniMapIcon.text = m_MiniMapBig ? IconMiniMapShrink : IconMiniMapGrow;
    }

    // Eases the frame toward its size: small, or as wide as the screen and up to just under the banner.
    void SizeMiniMap(bool instant)
    {
        if (miniMapFrame == null)
            return;
        Vector2 target = new Vector2(MiniMapSmall, MiniMapSmall);
        if (m_MiniMapBig)
        {
            float bottom = TopOf(miniMapFrame) - miniMapFrame.rect.height;   // where its bottom edge sits
            float room = safeArea.rect.height - BottomOf(banner) - MiniMapUnderBanner - bottom;
            target = new Vector2(safeArea.rect.width - 2f * MiniMapSide, Mathf.Max(MiniMapSmall, room));
        }
        Vector2 size = miniMapFrame.sizeDelta;
        if ((size - target).sqrMagnitude < 0.25f)
            return;
        miniMapFrame.sizeDelta = instant || !Application.isPlaying ? target
            : Vector2.Lerp(size, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f));
    }
}
