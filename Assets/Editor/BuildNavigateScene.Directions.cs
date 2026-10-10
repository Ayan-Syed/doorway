using UnityEngine;
using UnityEngine.UI;
using static UIBuild;

// Directions: where you start (the Start sheet), lining up at that door (the Line Up sheet and the face chip),
// and the from/to card over them.
public static partial class BuildNavigateScene
{
    static void CreateDirections(NavigationScreen screen, RectTransform safeArea)
    {
        CanvasGroup panelGroup = Panel("Directions", safeArea);
        screen.routePanel = panelGroup;
        var panel = (RectTransform)panelGroup.transform;

        // Where are you? (When the phone's location can't say.)
        screen.startSheet = Sheet("Start", panel, out RectTransform start);
        TextIn(TopBand("Title", start, 44f, 66f, 48f, 48f), "Where are you?", s_Regular, 52, s_T.onSurface, TextAnchor.MiddleLeft);
        Text hint = TextIn(TopBand("Subtitle", start, 112f, 46f, 48f, 48f), "Pick the door you're at, or tap it on the map",
            s_Regular, 34, s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        BestFit(hint, 26);
        screen.startHint = hint.gameObject;
        // In its place while Directions finds the phone's location.
        RectTransform finding = TopBand("Finding", start, 112f, 46f, 48f, 48f);
        RectTransform findingSpinner = (RectTransform)MakeSpinner(finding, 40f, 5f, s_T.primary).transform;
        findingSpinner.anchorMin = new Vector2(0f, 0.5f);
        findingSpinner.anchorMax = new Vector2(0f, 0.5f);
        findingSpinner.anchoredPosition = new Vector2(20f, 0f);
        TextIn(Stretch("Text", finding, 60f), "Finding where you are…", s_Regular, 34, s_T.onSurfaceVariant,
            TextAnchor.MiddleLeft);
        finding.gameObject.SetActive(false);
        screen.findingRow = finding.gameObject;
        screen.startList = List(start, screen.startSheet, 176f);

        // Line up and go; other doors nearby if the guess is wrong.
        screen.alignSheet = Sheet("Line Up", panel, out RectTransform align);
        RectTransform time = TopBand("Time", align, 44f, 80f, 48f, 48f);
        HorizontalRow(time, 16f, TextAnchor.MiddleLeft);
        screen.alignTime = LayoutText(time, "3 min", s_SemiBold, 58, s_T.good);
        screen.alignDistance = LayoutText(time, "(240 ft)", s_Regular, 58, s_T.onSurfaceVariant);
        screen.alignHint = TextIn(TopBand("Hint", align, 128f, 100f, 48f, 48f), "", s_Regular, 34, s_T.onSurfaceVariant,
            TextAnchor.UpperLeft);
        screen.alignHint.lineSpacing = 1.1f;
        TextIn(TopBand("Not Here", align, 236f, 48f, 48f, 48f), "Not here? Start from", s_Regular, 32,
            s_T.onSurfaceVariant, TextAnchor.MiddleLeft);

        RectTransform nearby = TopBand("Nearby", align, 292f, 84f, 48f, 32f);
        HorizontalRow(nearby, 16f, TextAnchor.MiddleLeft).childForceExpandHeight = true;
        screen.nearbyChips = new Button[3];
        screen.nearbyLabels = new Text[3];
        for (int i = 0; i < 3; i++)
        {
            RectTransform chip = Child($"Door {i + 1}", nearby);
            // The three share the width.
            var share = chip.gameObject.AddComponent<LayoutElement>();
            share.minWidth = 0f;
            share.preferredWidth = 0f;
            share.flexibleWidth = 1f;
            screen.nearbyChips[i] = MakeButton(chip, s_T.surfaceVariant, 42f, out _);
            Text label = TextIn(Stretch("Label", chip, 16f, 16f), "Door 113", s_SemiBold, 32, s_T.onSurface, TextAnchor.MiddleCenter);
            BestFit(label, 22);
            screen.nearbyLabels[i] = label;
        }
        RectTransform more = Child("All Doors", nearby);
        Size(more, 190f, 0f);
        screen.moreButton = FlatButton(more, 42f);
        TextIn(Stretch("Label", more), "All doors", s_SemiBold, 32, s_T.primary, TextAnchor.MiddleCenter);

        RectTransform begin = TopBand("Start", align, 400f, 112f, 48f, 48f);
        screen.beginButton = WideButton(begin, "Start", s_T.primary, s_T.onPrimary, out Text beginLabel);
        screen.beginLabel = beginLabel;
        // Beside the label while AR gets going ("Starting AR…"); NavigationScreen places it.
        screen.beginSpinner = MakeSpinner(begin, 46f, 5.5f, s_T.onPrimary);

        // Under the card while lining up: which way is up on the map.
        RectTransform facing = Fixed("Facing", panel, new Vector2(0.5f, 1f), new Vector2(0f, -296f), new Vector2(0f, 76f));
        facing.pivot = new Vector2(0.5f, 1f);
        Card(facing, s_T.surface, 38f, true);
        HorizontalRow(facing, 12f, TextAnchor.MiddleCenter).padding = new RectOffset(28, 36, 0, 0);
        FitWidth(facing);
        LayoutText(facing, IconArrowUp, s_Icons, 40, s_T.primary);
        LayoutText(facing, "The way you'll face", s_SemiBold, 34, s_T.onSurface);
        screen.faceChip = facing.gameObject.AddComponent<CanvasGroup>();

        CreateRouteCard(screen, panel);
        // Typing in the start field filters the door list.
        screen.startList.searchField = screen.originField;
    }

    // From and to, like a map app's directions card: it runs up under the status bar, so y0 is where the safe
    // area starts in it.
    static void CreateRouteCard(NavigationScreen screen, RectTransform panel)
    {
        const float y0 = 200f, fromY = 276f, toY = 396f, x = 136f;
        RectTransform card = TopBand("Route", panel, -y0, 472f, 0f, 0f);
        Card(card, s_T.surface, 40f, true).raycastTarget = true;
        screen.routeCard = card;

        RectTransform back = Fixed("Back", card, new Vector2(0f, 1f), new Vector2(72f, -fromY), new Vector2(96f, 96f));
        screen.routeBack = FlatButton(back, 48f);
        TextIn(Stretch("Icon", back), IconBack, s_Icons, 52, s_T.onSurface, TextAnchor.MiddleCenter);

        // A ring for the start, dots, and a pin for the place.
        Image ring = Dot("From", card, new Vector2(0f, 1f), new Vector2(x, -fromY), 28f, s_T.onSurfaceVariant);
        Dot("Hole", ring.transform, Center, Vector2.zero, 14f, s_T.surface);
        for (int i = 0; i < 3; i++)
            Dot("Dot", card, new Vector2(0f, 1f), new Vector2(x, -(fromY + 36f + i * 24f)), 8f, s_T.onSurfaceVariant);
        TextIn(Fixed("To", card, new Vector2(0f, 1f), new Vector2(x, -toY), new Vector2(52f, 52f)),
            PlaceStyle.IconPlace, s_Icons, 46, s_T.destination, TextAnchor.MiddleCenter);

        // The start: a search field for the door, then the door picked.
        RectTransform fieldRect = TopBand("From Field", card, 224f, 104f, 176f, 40f);
        Image fieldBackground = Rounded(fieldRect, s_T.surfaceVariant, 24f);
        fieldBackground.raycastTarget = true;
        var field = fieldRect.gameObject.AddComponent<InputField>();
        Text fieldText = TextIn(Stretch("Text", fieldRect, 32f, 32f), "", s_Regular, 40, s_T.onSurface, TextAnchor.MiddleLeft);
        Text placeholder = TextIn(Stretch("Placeholder", fieldRect, 32f, 32f), "Choose your starting door", s_Regular, 40,
            s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        ConfigureField(field, fieldBackground, fieldText, placeholder);
        screen.originField = field;

        // Two siblings are named "From" (the ring and this) and two "To" (the icon and the destination button).
        screen.originButton = RouteButton(card, "From", 224f, out Text originText);
        screen.originText = originText;
        screen.originButton.gameObject.SetActive(false);
        screen.destinationButton = RouteButton(card, "To", 344f, out Text destinationText);
        screen.destinationText = destinationText;
    }

    static Button RouteButton(RectTransform card, string name, float top, out Text text)
    {
        RectTransform rect = TopBand(name, card, top, 104f, 176f, 40f);
        Button button = MakeButton(rect, s_T.surfaceVariant, 24f, out _);
        button.GetComponent<PressFeedback>().pressedScale = 0.98f;
        text = TextIn(Stretch("Text", rect, 32f, 32f), "", s_Regular, 40, s_T.onSurface, TextAnchor.MiddleLeft);
        BestFit(text, 30);
        return button;
    }
}
