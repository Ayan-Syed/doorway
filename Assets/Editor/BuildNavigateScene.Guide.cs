using UnityEngine;
using UnityEngine.UI;
using static UIBuild;

// Walking: the turn banner over the camera, the status pill, what's left, and the minimap. Then Arrived.
public static partial class BuildNavigateScene
{
    static void CreateGuide(NavigationScreen screen, RectTransform safeArea)
    {
        CanvasGroup panelGroup = Panel("Guidance", safeArea);
        screen.guidePanel = panelGroup;
        var panel = (RectTransform)panelGroup.transform;
        const float bannerTop = 16f, bannerHeight = 228f;

        // The next step, on a card of deep indigo glass with a shadow: the arrow on a tile in the ribbon's colour (the
        // line on the floor and the arrow read as one), what to do and toward what beside it, how far at the top
        // right, and the step after next as a chip at the bottom right.
        RectTransform banner = TopBand("Banner", panel, bannerTop, bannerHeight, 24f, 24f);
        Shadow(banner, Lift);
        Card(banner, s_T.banner, Corner, false);
        screen.banner = banner;
        RectTransform tile = Fixed("Tile", banner, new Vector2(0f, 0.5f), new Vector2(24f + 90f, 0f), new Vector2(180f, 180f));
        Rounded(tile, s_T.Ribbon, 28f);
        screen.maneuverIcon = TextIn(Stretch("Arrow", tile), IconStraight, s_Icons, 112, Alpha(s_T.banner, 1f),
            TextAnchor.MiddleCenter);
        const float textLeft = 24f + 180f + 32f, textRight = 224f;
        screen.maneuverTitle = TextIn(TopBand("Title", banner, 34f, 72f, textLeft, textRight), "Go straight", s_SemiBold, 54,
            s_T.onBanner, TextAnchor.MiddleLeft);
        BestFit(screen.maneuverTitle, 30);
        screen.maneuverDetail = TextIn(TopBand("Detail", banner, 108f, 52f, textLeft, textRight), "", s_Regular, 36,
            Alpha(s_T.onBanner, 0.75f), TextAnchor.MiddleLeft);
        BestFit(screen.maneuverDetail, 26);
        // How far, in the ribbon's colour on a dark banner; on a light one that's too pale to read, so the accent.
        bool lightBanner = s_T.banner.grayscale > 0.5f;
        screen.maneuverDistance = TextIn(Fixed("Distance", banner, Vector2.one, new Vector2(-40f - 90f, -70f), new Vector2(180f, 64f)),
            "30 ft", s_SemiBold, 46, lightBanner ? s_T.primary : s_T.Ribbon, TextAnchor.MiddleRight);

        RectTransform then = Fixed("Then", banner, new Vector2(1f, 0f), new Vector2(-40f - 84f, 50f), new Vector2(168f, 60f));
        Card(then, s_T.bannerDark, SmallCorner, false);
        screen.thenTab = then;
        HorizontalRow(then, 8f, TextAnchor.MiddleCenter);
        LayoutText(then, "Then", s_SemiBold, 30, Alpha(s_T.onBanner, 0.85f));
        screen.thenIcon = LayoutText(then, IconTurnLeft, s_Icons, 40, s_T.onBanner);

        // Off the route, tracking lost, re-align: a pill under the banner (NavigationScreen places it). It's a button
        // that only takes taps while it asks the user to re-align.
        RectTransform status = Fixed("Status", panel, new Vector2(0.5f, 1f), new Vector2(0f, -300f), new Vector2(0f, 84f));
        status.pivot = new Vector2(0.5f, 1f);
        Image statusSurface = Card(status, s_T.surface, 42f, true);
        statusSurface.raycastTarget = true;
        var statusButton = status.gameObject.AddComponent<Button>();
        statusButton.transition = Selectable.Transition.None;
        statusButton.targetGraphic = statusSurface;
        statusButton.navigation = new Navigation { mode = Navigation.Mode.None };
        statusButton.interactable = false;
        status.gameObject.AddComponent<PressFeedback>().pressedScale = 0.97f;
        screen.statusButton = statusButton;
        HorizontalRow(status, 16f, TextAnchor.MiddleCenter).padding = new RectOffset(32, 40, 0, 0);
        FitWidth(status);
        screen.statusPill = status;
        RectTransform statusDot = Child("Dot", status);
        Size(statusDot, 24f, 24f);
        screen.statusDot = Rounded(statusDot, s_T.warning, 12f);
        screen.statusText = LayoutText(status, "", s_SemiBold, 34, s_T.onSurface);

        // What's left: how far along, the time and distance, line up again, end. It reaches under the gesture bar.
        RectTransform trip = Child("Trip", panel);
        trip.anchorMin = Vector2.zero;
        trip.anchorMax = new Vector2(1f, 0f);
        trip.offsetMin = new Vector2(0f, -200f);
        trip.offsetMax = new Vector2(0f, 230f);
        Card(trip, s_T.surface, 56f, true).raycastTarget = true;
        RectTransform progress = TopBand("Progress", trip, 36f, 8f, 48f, 48f);
        Rounded(progress, s_T.surfaceVariant, 4f);
        RectTransform walked = Child("Walked", progress);
        walked.anchorMin = Vector2.zero;
        walked.anchorMax = new Vector2(0.3f, 1f);   // NavigationScreen moves anchorMax.x
        walked.offsetMin = Vector2.zero;
        walked.offsetMax = Vector2.zero;
        Rounded(walked, s_T.primary, 4f);
        screen.progressFill = walked;
        screen.etaText = TextIn(TopBand("Time", trip, 62f, 76f, 48f, 440f), "4 min", s_SemiBold, 60, s_T.good,
            TextAnchor.MiddleLeft);
        screen.etaDetail = TextIn(TopBand("Detail", trip, 140f, 48f, 48f, 440f), "", s_Regular, 34, s_T.onSurfaceVariant,
            TextAnchor.MiddleLeft);
        BestFit(screen.etaDetail, 24);

        RectTransform actions = Fixed("Actions", trip, Vector2.one, new Vector2(-48f, -122f), new Vector2(0f, 88f));
        actions.pivot = new Vector2(1f, 0.5f);
        HorizontalRow(actions, 16f, TextAnchor.MiddleRight).childForceExpandHeight = true;
        FitWidth(actions);
        screen.realignButton = TextPill(actions, "Re-align", s_T.surfaceVariant, s_T.onSurface, out Image realignBackground,
            out Text realignLabel);
        // In the accent colour while the status pill asks for it.
        screen.realignBackground = realignBackground;
        screen.realignLabel = realignLabel;
        screen.endButton = TextPill(actions, "End", s_T.surfaceVariant, s_T.onSurface, out Image endBackground, out Text endLabel);
        screen.endBackground = endBackground;
        screen.endLabel = endLabel;
        // Room for "End?" without the row jumping.
        screen.endButton.gameObject.AddComponent<LayoutElement>().minWidth = 150f;

        // The minimap: heading-up, the walked part of the route cut off. A canvas of its own, like the big map.
        RectTransform mini = Fixed("Minimap", panel, new Vector2(1f, 0f), new Vector2(-32f, 262f), new Vector2(380f, 380f));
        mini.pivot = new Vector2(1f, 0f);
        // Its own raycaster: it takes taps (bigger or smaller) and pinches (zoom), NavigationScreen.MiniMap.
        OwnCanvas(mini);
        Card(mini, s_T.surface, 46f, true);
        screen.miniMapFrame = mini;
        RectTransform window = Stretch("Window", mini, 8f, 8f, 8f, 8f);
        Rounded(window, s_T.mapGround, 38f).raycastTarget = true;   // FloorMap's touches land on it
        window.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        // Tap to make it big: an icon in the top left corner says so (it takes no taps itself).
        RectTransform grow = Fixed("Grow", mini, new Vector2(0f, 1f), new Vector2(46f, -46f), new Vector2(56f, 56f));
        Rounded(grow, Alpha(s_T.surface, 0.9f), 28f);
        screen.miniMapIcon = TextIn(Stretch("Icon", grow), NavigationScreen.IconMiniMapGrow, s_Icons, 40,
            s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
        FloorMap miniMap = CreateFloorMap(window, 0.8f, false);
        miniMap.interactive = false;
        miniMap.routeWidth = 14f;
        miniMap.casingWidth = 21f;
        miniMap.dotSpacing = 26f;
        miniMap.followZoom = 1.8f;
        miniMap.followAhead = 0.22f;
        screen.miniMap = miniMap;
    }

    static void CreateArrived(NavigationScreen screen, RectTransform safeArea)
    {
        CanvasGroup panelGroup = Panel("Arrived", safeArea);
        screen.arrivedPanel = panelGroup;
        RectTransform card = Child("Card", panelGroup.transform);
        card.anchorMin = Vector2.zero;
        card.anchorMax = new Vector2(1f, 0f);
        card.offsetMin = new Vector2(0f, -200f);
        card.offsetMax = new Vector2(0f, 420f);
        Card(card, s_T.surface, 56f, true).raycastTarget = true;
        screen.arrivedCard = card;
        TextIn(Fixed("Icon", card, new Vector2(0f, 1f), new Vector2(96f, -100f), new Vector2(104f, 104f)),
            IconCheck, s_Icons, 96, s_T.good, TextAnchor.MiddleCenter);
        screen.arrivedTitle = TextIn(TopBand("Title", card, 50f, 72f, 168f, 48f), "You've arrived", s_Regular, 58,
            s_T.onSurface, TextAnchor.MiddleLeft);
        screen.arrivedDetail = TextIn(TopBand("Detail", card, 124f, 52f, 168f, 48f), "", s_Regular, 38,
            s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        BestFit(screen.arrivedDetail, 28);
        screen.doneButton = WideButton(TopBand("Done", card, 236f, 116f, 48f, 48f), "Done", s_T.primary, s_T.onPrimary, out _);
    }
}
