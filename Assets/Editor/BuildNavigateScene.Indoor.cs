using UnityEngine;
using UnityEngine.UI;
using static UIBuild;

// Indoor positioning's screens: "I'm inside" on the map, the snap card (back to the door, phone up, Confirm), the
// banner that asks for a view or a room number, and the team's debug overlay. NavigationScreen.Indoor runs them.
public static partial class BuildNavigateScene
{
    // On the map while outdoors, under the chips at the left (the floor button is at the right): opens the snap
    // card at the nearest entrance.
    static void CreateInsideButton(NavigationScreen screen, RectTransform panel)
    {
        RectTransform inside = Fixed("Inside", panel, new Vector2(0f, 1f), new Vector2(32f, -300f), new Vector2(0f, 84f));
        inside.pivot = new Vector2(0f, 1f);
        CardButton(inside, s_T.surface, 42f, true, out _);
        HorizontalRow(inside, 10f, TextAnchor.MiddleCenter).padding = new RectOffset(28, 36, 0, 0);
        FitWidth(inside);
        LayoutText(inside, PlaceStyle.IconDoor, s_Icons, 44, s_T.primary);
        LayoutText(inside, "I'm inside", s_SemiBold, 36, s_T.primary);
        screen.insideButton = inside.GetComponent<CanvasGroup>();
        // The map's names keep out from under it, like the other buttons over the map.
        var keep = new RectTransform[screen.map.keepClear.Length + 1];
        screen.map.keepClear.CopyTo(keep, 0);
        keep[keep.Length - 1] = inside;
        screen.map.keepClear = keep;
    }

    // Over a dimmed map (it takes no taps while this is up): which door you're at, Confirm, the other entrances
    // nearest it, a room door instead, Not now. NavigationScreen fills in the door.
    static void CreateSnapCard(NavigationScreen screen, RectTransform safeArea)
    {
        CanvasGroup panelGroup = Panel("Snap", safeArea);
        screen.snapPanel = panelGroup;
        var panel = (RectTransform)panelGroup.transform;
        RectTransform scrim = Stretch("Scrim", panel, -200f, -200f, -300f, -300f);
        var dim = scrim.gameObject.AddComponent<Image>();
        dim.color = new Color(0f, 0f, 0f, 0.25f);
        dim.raycastTarget = true;

        // It reaches under the gesture bar, like the arrived card.
        RectTransform card = Child("Card", panel);
        card.anchorMin = Vector2.zero;
        card.anchorMax = new Vector2(1f, 0f);
        card.offsetMin = new Vector2(0f, -200f);
        card.offsetMax = new Vector2(0f, 700f);
        Card(card, s_T.surface, 56f, true).raycastTarget = true;
        screen.snapCard = card;

        screen.snapTitle = TextIn(TopBand("Title", card, 48f, 72f, 48f, 48f), "Just came in the North entrance?",
            s_Regular, 52, s_T.onSurface, TextAnchor.MiddleLeft);
        BestFit(screen.snapTitle, 38);
        screen.snapHint = TextIn(TopBand("Hint", card, 128f, 92f, 48f, 48f),
            "Stand just inside the doors, back to them, phone up", s_Regular, 36, s_T.onSurfaceVariant, TextAnchor.UpperLeft);
        screen.snapHint.lineSpacing = 1.1f;
        screen.snapConfirm = WideButton(TopBand("Confirm", card, 232f, 120f, 48f, 48f), "Confirm", s_T.primary,
            s_T.onPrimary, out _);

        screen.snapChipsLabel = TextIn(TopBand("Others Label", card, 380f, 48f, 48f, 48f), "Another entrance", s_Regular,
            32, s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        RectTransform others = TopBand("Others", card, 436f, 88f, 48f, 48f);
        HorizontalRow(others, 16f, TextAnchor.MiddleLeft).childForceExpandHeight = true;
        const int chips = 3;
        screen.snapChips = new Button[chips];
        screen.snapChipLabels = new Text[chips];
        for (int i = 0; i < chips; i++)
        {
            RectTransform chip = Child($"Other {i + 1}", others);
            // The three share the width.
            var share = chip.gameObject.AddComponent<LayoutElement>();
            share.minWidth = 0f;
            share.preferredWidth = 0f;
            share.flexibleWidth = 1f;
            screen.snapChips[i] = MakeButton(chip, s_T.surfaceVariant, 44f, out _);
            Text label = TextIn(Stretch("Label", chip, 16f, 16f), "South-east entrance", s_SemiBold, 32, s_T.onSurface,
                TextAnchor.MiddleCenter);
            BestFit(label, 22);
            screen.snapChipLabels[i] = label;
        }

        RectTransform doors = TopBand("Door List", card, 560f, 96f, 24f, 320f);
        screen.snapDoorList = FlatButton(doors, 48f);
        TextIn(Stretch("Label", doors, 24f, 24f), "Start at a room door instead", s_SemiBold, 34, s_T.primary,
            TextAnchor.MiddleLeft);
        RectTransform notNow = TopBand("Not Now", card, 560f, 96f, 780f, 24f);
        screen.snapNotNow = FlatButton(notNow, 48f);
        TextIn(Stretch("Label", notNow), "Not now", s_SemiBold, 34, s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
    }

    // Above the sheet (NavigationScreen places it): "Hold your phone up…" or "Point your camera at any room
    // number", the second with "Stand at a door instead".
    static void CreatePositionBanner(NavigationScreen screen, RectTransform safeArea)
    {
        RectTransform banner = Fixed("Position", safeArea, new Vector2(0.5f, 0f), new Vector2(0f, 300f),
            new Vector2(1016f, 196f));
        banner.pivot = new Vector2(0.5f, 0f);
        Card(banner, s_T.surface, 28f, true).raycastTarget = true;
        screen.positionBanner = banner.gameObject.AddComponent<CanvasGroup>();
        screen.positionBannerIcon = TextIn(Fixed("Icon", banner, new Vector2(0f, 1f), new Vector2(64f, -56f),
            new Vector2(72f, 72f)), "\ue412", s_Icons, 48, s_T.warning, TextAnchor.MiddleCenter);
        screen.positionBannerText = TextIn(TopBand("Text", banner, 18f, 76f, 116f, 32f),
            "Point your camera at any room number", s_SemiBold, 34, s_T.onSurface, TextAnchor.MiddleLeft);
        BestFit(screen.positionBannerText, 26);
        RectTransform door = Fixed("Door", banner, new Vector2(1f, 0f), new Vector2(-24f, 16f), new Vector2(440f, 72f));
        door.pivot = new Vector2(1f, 0f);
        screen.positionBannerButton = FlatButton(door, 36f);
        TextIn(Stretch("Label", door, 24f, 24f), "Stand at a door instead", s_SemiBold, 32, s_T.primary,
            TextAnchor.MiddleRight);
    }

    // The team's overlay, over every screen: the numbers top left (they take no taps), and Checkpoint at the
    // right, where a thumb reaches, opening the six doors nearest the estimate as big chips above it.
    static void CreateDebugOverlay(NavigationScreen screen, RectTransform safeArea)
    {
        CanvasGroup panelGroup = Panel("Debug", safeArea);
        var panel = (RectTransform)panelGroup.transform;
        var overlay = panel.gameObject.AddComponent<DebugOverlay>();
        screen.debugOverlay = overlay;
        overlay.map = screen.map;
        overlay.navigator = screen.navigator;

        RectTransform box = Fixed("Lines", panel, new Vector2(0f, 1f), new Vector2(24f, -420f), new Vector2(660f, 230f));
        box.pivot = new Vector2(0f, 1f);
        Rounded(box, new Color(0f, 0f, 0f, 0.55f), 20f);
        overlay.lines = TextIn(Stretch("Text", box, 20f, 20f, 14f, 14f), "Outdoors", s_Regular, 26, Color.white,
            TextAnchor.UpperLeft);
        overlay.lines.horizontalOverflow = HorizontalWrapMode.Overflow;

        RectTransform checkpoint = Fixed("Checkpoint", panel, new Vector2(1f, 0.5f), new Vector2(-24f, -160f),
            new Vector2(280f, 120f));
        checkpoint.pivot = new Vector2(1f, 0.5f);
        overlay.checkpointButton = MakeButton(checkpoint, new Color(0f, 0f, 0f, 0.7f), 60f, out _);
        TextIn(Stretch("Label", checkpoint), "Checkpoint", s_SemiBold, 36, Color.white, TextAnchor.MiddleCenter);

        // Two rows of three, the width of the screen.
        RectTransform row = Child("Doors", panel);
        row.anchorMin = new Vector2(0f, 0.5f);
        row.anchorMax = new Vector2(1f, 0.5f);
        row.pivot = new Vector2(0.5f, 0f);
        row.offsetMin = new Vector2(24f, -80f);
        row.offsetMax = new Vector2(-24f, -80f + 2f * 128f + 3f * 16f);
        Rounded(row, new Color(0f, 0f, 0f, 0.7f), 32f).raycastTarget = true;
        var grid = row.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(320f, 128f);
        grid.spacing = new Vector2(16f, 16f);
        grid.padding = new RectOffset(16, 16, 16, 16);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 3;
        grid.childAlignment = TextAnchor.UpperCenter;
        const int doors = 6;
        overlay.checkpointChips = new Button[doors];
        overlay.checkpointLabels = new Text[doors];
        for (int i = 0; i < doors; i++)
        {
            RectTransform chip = Child($"Door {i + 1}", row);
            overlay.checkpointChips[i] = MakeButton(chip, s_T.surface, 28f, out _);
            Text label = TextIn(Stretch("Label", chip, 14f, 14f), "Room 121 · West door", s_SemiBold, 32, s_T.onSurface,
                TextAnchor.MiddleCenter);
            BestFit(label, 22);
            overlay.checkpointLabels[i] = label;
        }
        overlay.checkpointRow = row.gameObject;
    }
}
