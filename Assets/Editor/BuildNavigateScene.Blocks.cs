using UnityEngine;
using UnityEngine.UI;
using static UIBuild;

// Building blocks: the cards, buttons, rows and fields every screen is made of. Colours come from s_T, the
// current theme. Every Image and Text starts with raycasts off unless a helper says otherwise.
public static partial class BuildNavigateScene
{
    // A rounded surface: cards, sheets, chips. No shadow. edge: a hairline round it in the outline colour, for a
    // card that would otherwise melt into the map under it. Neither part takes part in a layout group on rect.
    // Returns the surface.
    static Image Card(RectTransform rect, Color color, float radius, bool edge)
    {
        if (edge)
        {
            RectTransform outline = Stretch("Edge", rect, -2f, -2f, -2f, -2f);
            outline.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            Rounded(outline, s_T.outline, radius + 2f).fillCenter = false;   // Only the rim: the surface covers the middle.
        }
        RectTransform surface = Stretch("Surface", rect);
        surface.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        return Rounded(surface, color, radius);
    }

    // A card that's a button: chips, the floor picker, Whole floor, Locate.
    static Button CardButton(RectTransform rect, Color color, float radius, bool edge, out Image surface)
    {
        surface = Card(rect, color, radius, edge);
        surface.raycastTarget = true;
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = surface;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = Color.white;
        colors.selectedColor = Color.white;
        colors.disabledColor = Color.white;
        colors.pressedColor = new Color(0.88f, 0.88f, 0.88f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.06f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        rect.gameObject.AddComponent<CanvasGroup>();
        rect.gameObject.AddComponent<PressFeedback>().pressedScale = 0.96f;
        return button;
    }

    // A button with no background of its own: icon buttons, text buttons and list rows. A tint fades in while
    // it's held. radius 0: square corners.
    static Button FlatButton(RectTransform rect, float radius)
    {
        Image layer;
        if (radius > 0f)
        {
            layer = Rounded(rect, Alpha(s_T.onSurface, 0f), radius);
        }
        else
        {
            layer = rect.gameObject.AddComponent<Image>();
            layer.color = Alpha(s_T.onSurface, 0f);
        }
        layer.raycastTarget = true;
        var button = rect.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.targetGraphic = layer;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        var press = rect.gameObject.AddComponent<PressFeedback>();
        press.pressedScale = 1f;
        press.stateLayer = layer;
        press.stateLayerAlpha = 0.1f;
        return button;
    }

    // A full-width button with fully round ends (every caller passes a TopBand, so its height is known).
    static Button WideButton(RectTransform rect, string label, Color color, Color ink, out Text text)
    {
        float height = rect.rect.height;
        Button button = MakeButton(rect, color, height > 0f ? height * 0.5f : 56f, out _);
        button.GetComponent<PressFeedback>().pressedScale = 0.98f;
        text = TextIn(Stretch("Label", rect), label, s_SemiBold, 44, ink, TextAnchor.MiddleCenter);
        return button;
    }

    // A pill as wide as its label, inside a layout row.
    static Button TextPill(RectTransform row, string label, Color color, Color ink, out Image bg, out Text text)
    {
        RectTransform rect = Child(label, row);
        Button button = MakeButton(rect, color, 44f, out bg);
        HorizontalRow(rect, 0f, TextAnchor.MiddleCenter).padding = new RectOffset(36, 36, 0, 0);
        text = LayoutText(rect, label, s_SemiBold, 36, ink);
        return button;
    }

    // Children at their preferred sizes, left to right.
    static HorizontalLayoutGroup HorizontalRow(RectTransform rect, float spacing, TextAnchor alignment)
    {
        var row = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.childAlignment = alignment;
        row.spacing = spacing;
        row.childControlWidth = true;
        row.childControlHeight = true;
        row.childForceExpandWidth = false;
        row.childForceExpandHeight = false;
        return row;
    }

    static void FitWidth(RectTransform rect)
    {
        rect.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    // A fixed size in a layout group, for each dimension above 0.
    static LayoutElement Size(RectTransform rect, float width, float height)
    {
        var element = rect.gameObject.AddComponent<LayoutElement>();
        if (width > 0f)
        {
            element.minWidth = width;
            element.preferredWidth = width;
        }
        if (height > 0f)
        {
            element.minHeight = height;
            element.preferredHeight = height;
        }
        return element;
    }

    // A circle.
    static Image Dot(string name, Transform parent, Vector2 anchor, Vector2 pos, float size, Color color)
    {
        return Rounded(Fixed(name, parent, anchor, pos, new Vector2(size, size)), color, size / 2f);
    }

    // A hairline across a sheet.
    static Image Divider(Transform parent, float top, float left)
    {
        var line = TopBand("Divider", parent, top, 2f, left, 0f).gameObject.AddComponent<Image>();
        line.color = s_T.outline;
        line.raycastTarget = false;
        return line;
    }

    // A map label's halo, so it reads over rooms, walls and the route.
    static TextHalo Halo(Text text, float distance)
    {
        var halo = text.gameObject.AddComponent<TextHalo>();
        halo.color = s_T.mapLabelHalo;
        halo.distance = distance;
        return halo;
    }

    // One screen panel that can fade.
    static CanvasGroup Panel(string name, Transform parent)
    {
        return Stretch(name, parent).gameObject.AddComponent<CanvasGroup>();
    }

    static void ConfigureField(InputField field, Graphic target, Text text, Text placeholder)
    {
        text.supportRichText = false;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.targetGraphic = target;
        field.transition = Selectable.Transition.None;
        field.navigation = new Navigation { mode = Navigation.Mode.None };
        field.lineType = InputField.LineType.SingleLine;
        field.characterLimit = 30;
        field.customCaretColor = true;
        field.caretColor = s_T.primary;
        field.caretWidth = 4;
        field.selectionColor = Alpha(s_T.primary, 0.3f);
        // The keyboard types straight into the field, not into a box above the keyboard.
        field.shouldHideMobileInput = true;
    }

    static RawImage Raw(string name, Transform parent, Texture texture, Color color, Material material)
    {
        var image = Child(name, parent).gameObject.AddComponent<RawImage>();
        image.texture = texture;
        image.color = color;
        image.material = material;
        image.raycastTarget = false;
        return image;
    }

    // A dotted line (a walking route).
    static UILine Line(string name, Transform parent, Color color)
    {
        var line = Child(name, parent).gameObject.AddComponent<UILine>();
        line.color = color;
        line.raycastTarget = false;
        return line;
    }

    // A spinner size across, in the middle of parent. A canvas of its own: it re-meshes every frame it shows,
    // and shouldn't rebuild its parent's batch each time.
    static Spinner MakeSpinner(Transform parent, float size, float thickness, Color color)
    {
        RectTransform rect = Fixed("Spinner", parent, Center, Vector2.zero, new Vector2(size, size));
        rect.gameObject.AddComponent<Canvas>();
        var spinner = rect.gameObject.AddComponent<Spinner>();
        spinner.thickness = thickness;
        spinner.color = color;
        spinner.raycastTarget = false;
        return spinner;
    }

    // A line of detail under a sheet's title.
    static Text InfoLine(RectTransform sheet, float top, Color color)
    {
        Text text = TextIn(TopBand("Info", sheet, top, 60f, 48f, 48f), "", s_Regular, 36, color, TextAnchor.MiddleLeft);
        BestFit(text, 28);
        return text;
    }

    // A setting that's on or off: its name and what it does, and a switch. The whole row is the button.
    // The switch is built in its "on" look; NavigationScreen styles it.
    static Button SwitchRow(RectTransform sheet, string name, float top, string title, string detail,
        out Text titleText, out Text detailText, out Image track, out Image knob)
    {
        RectTransform row = TopBand(name, sheet, top, 120f, 0f, 0f);
        Button button = FlatButton(row, 0f);
        titleText = TextIn(TopBand("Title", row, 14f, 50f, 48f, 200f), title, s_SemiBold, 38, s_T.onSurface,
            TextAnchor.MiddleLeft);
        BestFit(titleText, 28);
        detailText = TextIn(TopBand("Detail", row, 66f, 42f, 48f, 200f), detail, s_Regular, 32, s_T.onSurfaceVariant,
            TextAnchor.MiddleLeft);
        BestFit(detailText, 24);
        RectTransform toggle = Fixed("Switch", row, new Vector2(1f, 0.5f), new Vector2(-100f, 0f), new Vector2(104f, 60f));
        track = Rounded(toggle, s_T.primary, 30f);
        knob = Rounded(Fixed("Knob", toggle, Center, new Vector2(22f, 0f), new Vector2(44f, 44f)), s_T.onPrimary, 22f);
        return button;
    }
}
