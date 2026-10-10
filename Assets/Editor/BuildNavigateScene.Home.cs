using UnityEngine;
using UnityEngine.UI;
using static UIBuild;

// Home: the search bar and chips over the map, the floor button and menu, the building's directory in a sheet,
// and a place's sheet.
public static partial class BuildNavigateScene
{
    static readonly string[] ChipNames = { "Restrooms", "Elevators", "Stairs", "Entrances" };

    static void CreateHome(NavigationScreen screen, RectTransform safeArea)
    {
        CanvasGroup panelGroup = Panel("Home", safeArea);
        screen.homePanel = panelGroup;
        var panel = (RectTransform)panelGroup.transform;

        // The floor shown, as a button under the chips at the right: it opens the floor menu. "Floor 1" is a
        // placeholder; NavigationScreen writes the plan's floor.
        RectTransform floor = Fixed("Floor", panel, Vector2.one, new Vector2(-32f, -300f), new Vector2(0f, 84f));
        floor.pivot = Vector2.one;
        CardButton(floor, s_T.surface, 42f, true, out _);
        HorizontalRow(floor, 4f, TextAnchor.MiddleCenter).padding = new RectOffset(36, 22, 0, 0);
        FitWidth(floor);
        LayoutText(floor, "Floor 1", s_SemiBold, 36, s_T.onSurface);
        LayoutText(floor, IconDropDown, s_Icons, 48, s_T.onSurfaceVariant);
        screen.floorPicker = floor.GetComponent<CanvasGroup>();

        // Restrooms, elevators, stairs, entrances: a row of chips under the search bar that scrolls sideways.
        RectTransform chips = TopBand("Chips", panel, 168f, 116f, 0f, 0f);
        screen.chipBar = chips.gameObject.AddComponent<CanvasGroup>();
        chips.gameObject.AddComponent<RectMask2D>();
        var chipScroll = chips.gameObject.AddComponent<ScrollRect>();
        chipScroll.horizontal = true;
        chipScroll.vertical = false;
        chipScroll.movementType = ScrollRect.MovementType.Elastic;
        RectTransform row = Child("Row", chips);
        row.anchorMin = Vector2.zero;
        row.anchorMax = new Vector2(0f, 1f);
        row.pivot = new Vector2(0f, 0.5f);
        row.sizeDelta = Vector2.zero;
        HorizontalRow(row, 16f, TextAnchor.MiddleLeft).padding = new RectOffset(32, 32, 0, 0);
        FitWidth(row);
        chipScroll.content = row;
        screen.chips = new Button[ChipNames.Length];
        screen.chipBackgrounds = new Image[ChipNames.Length];
        screen.chipLabels = new Text[ChipNames.Length];
        for (int i = 0; i < ChipNames.Length; i++)
        {
            RectTransform chip = Child(ChipNames[i], row);
            Size(chip, 0f, 84f);
            screen.chips[i] = CardButton(chip, s_T.surface, 42f, true, out Image chipSurface);
            screen.chipBackgrounds[i] = chipSurface;
            HorizontalRow(chip, 0f, TextAnchor.MiddleCenter).padding = new RectOffset(36, 36, 0, 0);
            screen.chipLabels[i] = LayoutText(chip, ChipNames[i], s_SemiBold, 36, s_T.onSurface);
        }

        CreateDirectory(screen, panel);
        CreatePlaceSheet(screen, panel);
        // Over the sheets.
        CreateSearch(screen, panel);
        CreateFloorMenu(screen, panel);
        screen.homeList.searchField = screen.searchField;
    }

    static void CreateDirectory(NavigationScreen screen, RectTransform panel)
    {
        screen.homeSheet = Sheet("Directory", panel, out RectTransform sheet);
        // Placeholders: NavigationScreen writes the plan's building and floor.
        screen.homeTitle = TextIn(TopBand("Title", sheet, 48f, 72f, 48f, 48f), "Business Administration I", s_Regular, 56,
            s_T.onSurface, TextAnchor.MiddleLeft);
        BestFit(screen.homeTitle, 42);
        screen.homeSubtitle = TextIn(TopBand("Subtitle", sheet, 120f, 48f, 48f, 48f), "Floor 1", s_Regular, 36,
            s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        screen.homeList = List(sheet, screen.homeSheet, 184f);

        // Into the building (Go inside) or back to the whole floor, riding on the sheet's top edge at the right.
        RectTransform whole = Fixed("Whole Floor", sheet, Vector2.one, new Vector2(-32f, 24f), new Vector2(0f, 84f));
        whole.pivot = new Vector2(1f, 0f);
        CardButton(whole, s_T.surface, 42f, true, out _);
        HorizontalRow(whole, 10f, TextAnchor.MiddleCenter).padding = new RectOffset(28, 36, 0, 0);
        FitWidth(whole);
        screen.wholeFloorIcon = LayoutText(whole, IconWholeFloor, s_Icons, 44, s_T.primary);
        screen.wholeFloorLabel = LayoutText(whole, "Whole floor", s_SemiBold, 36, s_T.primary);
        screen.wholeFloor = whole.GetComponent<CanvasGroup>();

        // My location, above it, where a map app has it.
        RectTransform locate = Fixed("Locate", sheet, Vector2.one, new Vector2(-32f, 132f), new Vector2(112f, 112f));
        locate.pivot = new Vector2(1f, 0f);
        CardButton(locate, s_T.surface, 56f, true, out _);
        TextIn(Stretch("Icon", locate), IconMyLocation, s_Icons, 52, s_T.primary, TextAnchor.MiddleCenter);
        screen.locate = locate.GetComponent<CanvasGroup>();

        // OpenStreetMap's credit, as its licence asks: small, over the map at the sheet's top left.
        RectTransform credit = Fixed("Credit", sheet, new Vector2(0f, 1f), new Vector2(30f, 26f), new Vector2(370f, 40f));
        credit.pivot = Vector2.zero;
        Text creditText = TextIn(credit, "© OpenStreetMap contributors", s_Regular, 25, s_T.onSurfaceVariant,
            TextAnchor.LowerLeft);
        creditText.horizontalOverflow = HorizontalWrapMode.Overflow;
        Halo(creditText, 2.2f);
        var creditGroup = credit.gameObject.AddComponent<CanvasGroup>();
        creditGroup.blocksRaycasts = false;
        screen.attribution = creditGroup;

        // The map's names keep out from under these.
        screen.map.keepClear = new[] { credit, locate, whole };
    }

    static void CreatePlaceSheet(NavigationScreen screen, RectTransform panel)
    {
        screen.placeSheet = Sheet("Place", panel, out RectTransform sheet);
        screen.placeTitle = TextIn(TopBand("Title", sheet, 50f, 80f, 48f, 160f), "Room 121", s_Regular, 60,
            s_T.onSurface, TextAnchor.MiddleLeft);
        BestFit(screen.placeTitle, 44);
        screen.placeSubtitle = TextIn(TopBand("Subtitle", sheet, 132f, 48f, 48f, 160f), "Classroom", s_Regular, 36,
            s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        BestFit(screen.placeSubtitle, 28);
        RectTransform close = Fixed("Close", sheet, Vector2.one, new Vector2(-92f, -92f), new Vector2(88f, 88f));
        screen.placeClose = MakeButton(close, s_T.surfaceVariant, 44f, out _);
        TextIn(Stretch("Icon", close), IconClose, s_Icons, 48, s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
        screen.directionsButton = WideButton(TopBand("Directions", sheet, 212f, 112f, 48f, 48f), "Directions",
            s_T.primary, s_T.onPrimary, out _);
        Divider(sheet, 356f, 0f);
        screen.placeDoors = InfoLine(sheet, 376f, s_T.onSurface);
        screen.placeFloor = InfoLine(sheet, 444f, s_T.onSurfaceVariant);
    }

    static void CreateSearch(NavigationScreen screen, RectTransform panel)
    {
        RectTransform search = TopBand("Search", panel, 24f, 132f, 32f, 32f);
        Card(search, s_T.surface, 66f, true).raycastTarget = true;
        screen.searchCard = search;

        // Search, or back while searching (NavigationScreen swaps the glyph).
        RectTransform lead = Fixed("Lead", search, new Vector2(0f, 0.5f), new Vector2(72f, 0f), new Vector2(96f, 96f));
        screen.searchLead = FlatButton(lead, 48f);
        screen.searchLeadIcon = TextIn(Stretch("Icon", lead), IconSearch, s_Icons, 52, s_T.onSurface, TextAnchor.MiddleCenter);

        RectTransform fieldRect = Stretch("Field", search, 128f, 226f, 0f, 0f);
        var fieldTarget = fieldRect.gameObject.AddComponent<Image>();
        fieldTarget.color = Color.clear;   // the field's hit area
        var field = fieldRect.gameObject.AddComponent<InputField>();
        Text text = TextIn(Stretch("Text", fieldRect), "", s_Regular, 42, s_T.onSurface, TextAnchor.MiddleLeft);
        Text placeholder = TextIn(Stretch("Placeholder", fieldRect), "Search rooms and buildings", s_Regular, 42,
            s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        ConfigureField(field, fieldTarget, text, placeholder);
        screen.searchField = field;

        RectTransform clear = Fixed("Clear", search, new Vector2(1f, 0.5f), new Vector2(-172f, 0f), new Vector2(88f, 88f));
        screen.searchClear = FlatButton(clear, 44f);
        TextIn(Stretch("Icon", clear), IconClose, s_Icons, 48, s_T.onSurfaceVariant, TextAnchor.MiddleCenter);

        // Settings, where a map app has your account.
        RectTransform settings = Fixed("Settings", search, new Vector2(1f, 0.5f), new Vector2(-72f, 0f), new Vector2(96f, 96f));
        screen.settingsButton = FlatButton(settings, 48f);
        TextIn(Stretch("Icon", settings), IconSettings, s_Icons, 52, s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
    }

    // The building's floors, dropped down from the floor button, the one shown ticked. Only floor 1 is mapped.
    // A tap anywhere else closes it. The header and "Floor 1" are placeholders NavigationScreen fills from the plan.
    static void CreateFloorMenu(NavigationScreen screen, RectTransform panel)
    {
        CanvasGroup menu = Panel("Floor Menu", panel);
        screen.floorMenu = menu;
        RectTransform blocker = Stretch("Blocker", menu.transform);
        blocker.gameObject.AddComponent<Image>().color = Color.clear;
        var blockerButton = blocker.gameObject.AddComponent<Button>();
        blockerButton.transition = Selectable.Transition.None;
        blockerButton.navigation = new Navigation { mode = Navigation.Mode.None };
        screen.floorMenuBlocker = blockerButton;

        // 512 = an 84 px header, four 104 px rows and 12 below.
        RectTransform card = Fixed("Card", menu.transform, Vector2.one, new Vector2(-32f, -300f), new Vector2(480f, 512f));
        card.pivot = Vector2.one;
        Card(card, s_T.surface, 36f, true).raycastTarget = true;
        Text building = TextIn(TopBand("Building", card, 20f, 56f, 40f, 32f), "Business Administration I", s_Regular, 30,
            s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
        BestFit(building, 24);

        screen.floorButtons = new Button[4];
        for (int n = 4; n >= 1; n--)
        {
            RectTransform row = TopBand($"Floor {n}", card, 84f + (4 - n) * 104f, 104f, 12f, 12f);
            if (n == 1)
            {
                Button shown = MakeButton(row, s_T.primaryContainer, 26f, out _);
                shown.GetComponent<PressFeedback>().pressedScale = 0.98f;
                TextIn(Stretch("Label", row, 28f, 100f), "Floor 1", s_SemiBold, 38, s_T.primary, TextAnchor.MiddleLeft);
                TextIn(Fixed("Icon", row, new Vector2(1f, 0.5f), new Vector2(-56f, 0f), new Vector2(64f, 64f)),
                    IconDone, s_Icons, 48, s_T.primary, TextAnchor.MiddleCenter);
                screen.floorButtons[0] = shown;
            }
            else
            {
                screen.floorButtons[n - 1] = FlatButton(row, 26f);
                TextIn(Stretch("Label", row, 28f, 140f), $"Floor {n}", s_Regular, 38, s_T.onSurfaceVariant, TextAnchor.MiddleLeft);
                TextIn(Stretch("Soon", row, 28f, 32f), "Soon", s_Regular, 30, s_T.onSurfaceVariant, TextAnchor.MiddleRight);
            }
        }
    }
}
