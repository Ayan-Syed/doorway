using UnityEngine;
using UnityEngine.UI;
using static UIBuild;

// Bottom sheets and the place lists inside them. A sheet is anchored along the bottom with its pivot at the top,
// so its y position is how much of it shows; BottomSheet moves and resizes it at runtime.
public static partial class BuildNavigateScene
{
    // A sheet along the bottom: a card with a drag handle, the full width. Drags anywhere on it move it. A canvas
    // of its own, so sliding it moves one canvas instead of re-batching everything over the map, and a CanvasGroup
    // NavigationScreen stops its taps with while it's hidden.
    static BottomSheet Sheet(string name, Transform parent, out RectTransform rect)
    {
        rect = Child(name, parent);
        OwnCanvas(rect);
        rect.gameObject.AddComponent<CanvasGroup>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(0f, 1000f);   // a starting value: BottomSheet sizes it to its stops
        rect.anchoredPosition = new Vector2(0f, 300f);
        Card(rect, s_T.surface, SheetCorner, true).raycastTarget = true;   // so drags on empty parts reach the sheet
        Rounded(Fixed("Handle", rect, new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(84f, 10f)), s_T.outline, 5f);
        var sheet = rect.gameObject.AddComponent<BottomSheet>();
        // extra taller than the highest stop, so an overshoot never shows a gap underneath. The list below ends
        // the same distance above the sheet's bottom: change one, change both.
        sheet.extra = 400f;
        return sheet;
    }

    // A list of places filling a sheet below top, to the bottom of the screen when the sheet is all the way up.
    static PlaceList List(RectTransform sheetRect, BottomSheet sheet, float top)
    {
        RectTransform viewport = Stretch("List", sheetRect, 0f, 0f, top, sheet.extra);
        // A canvas of its own: scrolling moves its rows and nothing else's.
        OwnCanvas(viewport);
        var hit = viewport.gameObject.AddComponent<Image>();
        hit.color = Color.clear;   // Clear, but catches drags between rows so they scroll the list.
        hit.raycastTarget = true;
        viewport.gameObject.AddComponent<RectMask2D>();
        var scroll = viewport.gameObject.AddComponent<SheetScrollRect>();
        scroll.sheet = sheet;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Elastic;
        scroll.scrollSensitivity = 40f;

        RectTransform content = Child("Content", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = Vector2.one;
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = Vector2.zero;
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 120);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = content;

        var list = viewport.gameObject.AddComponent<PlaceList>();
        list.scroll = scroll;
        list.content = content;
        list.sectionTemplate = SectionTemplate(content);
        list.rowTemplate = RowTemplate(content);

        // Filled in by PlaceList when nothing matches.
        Text empty = TextIn(TopBand("Nothing Found", viewport, 60f, 60f, 48f, 48f), "", s_Regular, 36,
            s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
        empty.gameObject.SetActive(false);
        list.emptyText = empty;
        return list;
    }

    // A section header; PlaceList copies it and sets the title.
    // For a dropdown (PlaceList.AddSection), the whole header is the button, with the count and a chevron at the
    // right and a hairline under it while closed; a plain section keeps those off.
    static GameObject SectionTemplate(RectTransform content)
    {
        RectTransform section = Child("Section", content);
        Size(section, 0f, 88f);
        var parts = section.gameObject.AddComponent<PlaceListSection>();
        parts.button = FlatButton(section, 0f);
        parts.button.enabled = false;
        parts.title = TextIn(Stretch("Title", section, 48f, 200f, 0f, 14f), "Rooms", s_SemiBold, 34, s_T.onSurfaceVariant,
            TextAnchor.LowerLeft);
        parts.count = TextIn(Stretch("Count", section, 48f, 112f, 0f, 14f), "7", s_Regular, 32, s_T.onSurfaceVariant,
            TextAnchor.LowerRight);
        parts.chevron = TextIn(Fixed("Chevron", section, new Vector2(1f, 0f), new Vector2(-72f, 36f), new Vector2(56f, 56f)),
            PlaceList.IconMore, s_Icons, 48, s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
        parts.divider = Hairline(section).gameObject;
        parts.count.gameObject.SetActive(false);
        parts.chevron.gameObject.SetActive(false);
        parts.divider.SetActive(false);
        return section.gameObject;
    }

    // A 2 px line along a list item's bottom, from the text's left edge.
    static RectTransform Hairline(RectTransform parent)
    {
        RectTransform divider = Child("Divider", parent);
        divider.anchorMin = Vector2.zero;
        divider.anchorMax = new Vector2(1f, 0f);
        divider.pivot = new Vector2(0.5f, 0f);
        divider.offsetMin = new Vector2(48f, 0f);
        divider.offsetMax = new Vector2(0f, 2f);
        var line = divider.gameObject.AddComponent<Image>();
        line.color = s_T.outline;
        line.raycastTarget = false;
        return divider;
    }

    // A place: its name, what it is under it (the name alone, centred, when there's nothing to say), and a
    // hairline under them. The whole row is the button.
    static PlaceListRow RowTemplate(RectTransform content)
    {
        RectTransform rect = Child("Row", content);
        Size(rect, 0f, 140f);
        var row = rect.gameObject.AddComponent<PlaceListRow>();
        row.button = FlatButton(rect, 0f);

        RectTransform text = Stretch("Text", rect, 48f, 48f);
        var column = text.gameObject.AddComponent<VerticalLayoutGroup>();
        column.childAlignment = TextAnchor.MiddleLeft;
        column.spacing = 2f;
        column.childControlWidth = true;
        column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;

        RectTransform title = Child("Title", text);
        row.title = TextIn(title, "Room 107", s_SemiBold, 42, s_T.onSurface, TextAnchor.MiddleLeft);
        Size(title, 0f, 58f);
        BestFit(row.title, 32);

        RectTransform detail = Child("Detail", text);
        row.detail = TextIn(detail, "Lecture hall", s_Regular, 34, s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        Size(detail, 0f, 44f);
        BestFit(row.detail, 26);
        row.textColumn = text;

        row.divider = Hairline(rect);

        // "6 inside" and a chevron at the right, on a row with rooms inside it (PlaceList shows it and makes room).
        RectTransform toggle = Fixed("Toggle", rect, new Vector2(1f, 0.5f), new Vector2(-160f, 0f), new Vector2(224f, 80f));
        row.toggle = MakeButton(toggle, s_T.surfaceVariant, SmallCorner, out _);
        row.toggleLabel = TextIn(Stretch("Label", toggle, 16f, 64f), "6 inside", s_SemiBold, 32, s_T.onSurfaceVariant,
            TextAnchor.MiddleRight);
        row.toggleIcon = TextIn(Fixed("Icon", toggle, new Vector2(1f, 0.5f), new Vector2(-38f, 0f), new Vector2(48f, 48f)),
            PlaceList.IconMore, s_Icons, 44, s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
        toggle.gameObject.SetActive(false);
        return row;
    }
}
