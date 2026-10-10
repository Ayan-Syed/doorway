using UnityEngine;
using UnityEngine.UI;
using static UIBuild;

// The map: the big one behind every screen, and the minimap while walking.
public static partial class BuildNavigateScene
{
    // A map in window (which gets the FloorMap): s scales its icons, labels, pin and dot. buildings: the campus's
    // buildings stand up round the floor, named (the big map); without, only the campus's ground (the minimap).
    static FloorMap CreateFloorMap(RectTransform window, float s, bool buildings)
    {
        var map = window.gameObject.AddComponent<FloorMap>();
        map.placeMap = s_PlaceMap;
        bool campus = s_CampusGeometry != null && s_CampusInfo != null;
        if (campus)
        {
            map.campusGeometry = s_CampusGeometry;
            map.campusInfo = s_CampusInfo;
        }

        RectTransform rotator = Child("Rotator", window);
        rotator.sizeDelta = Vector2.zero;
        map.rotator = rotator;
        // Panning and zooming move it without rebuilding anything else's batches.
        RectTransform content = Child("Content", rotator);
        content.gameObject.AddComponent<Canvas>();
        map.content = content;

        // The campus round the floor (OpenStreetMap): its ground under everything, then its buildings in three
        // parts, so the floor and its walls draw between the buildings behind them and those in front, and the
        // floor's own building (it sinks away as the floor comes in) over its walls. Each part a canvas of its
        // own: the ground is big, and the buildings re-mesh as the map turns.
        CampusBuildings behind = null;
        if (campus)
        {
            RectTransform groundRect = Child("Campus Ground", content);
            var ground = groundRect.gameObject.AddComponent<CampusGround>();
            ground.raycastTarget = false;
            groundRect.gameObject.AddComponent<Canvas>();
            map.campusGround = ground;
            if (buildings)
                behind = Buildings("Buildings Behind", content, CampusBuildings.Part.Behind);
        }

        map.basemap = Raw("Basemap", content, s_Basemap, Color.white, null);
        map.highlight = Raw("Highlight", content, null, s_T.destination, s_HighlightMaterial);
        map.routeCasing = Line("Route Casing", content, s_T.routeCasing);
        map.routeLine = Line("Route", content, s_T.primary);

        // Standing up when the map tilts. Last, so they hide the floor behind them.
        var walls = Child("Walls", content).gameObject.AddComponent<WallMesh>();
        walls.walls = s_Walls;
        walls.material = s_WallMaterial;
        walls.color = s_T.wallTop;
        walls.sideColor = s_T.wallSide;
        walls.raycastTarget = false;
        map.walls = walls;

        if (campus && buildings)
        {
            CampusBuildings indoor = Buildings("Floor's Building", content, CampusBuildings.Part.Indoor);
            CampusBuildings front = Buildings("Buildings Front", content, CampusBuildings.Part.Front);
            map.campusBuildings = new[] { behind, indoor, front };
        }

        // Over the map, not turned with it: labels and markers stay upright and the same size.
        RectTransform overlay = Stretch("Overlay", window);
        map.overlay = overlay;

        if (campus && buildings)
        {
            // Building names, under everything else on the map: dark grey with a halo, a line or two.
            RectTransform names = Stretch("Building Names", overlay);
            Text name = TextIn(Fixed("Building Name", names, Center, Vector2.zero, new Vector2(420f, 80f) * s),
                "Building", s_SemiBold, Mathf.RoundToInt(30f * s), s_T.onSurfaceVariant, TextAnchor.MiddleCenter);
            name.horizontalOverflow = HorizontalWrapMode.Overflow;
            name.verticalOverflow = VerticalWrapMode.Overflow;
            name.lineSpacing = 0.92f;
            Halo(name, 2.6f * s);
            name.gameObject.AddComponent<CanvasGroup>();
            map.buildingLabelTemplate = name;
        }

        // Room numbers: small, grey, with a halo, like a map's street names.
        RectTransform numbers = Stretch("Room Numbers", overlay);
        Text number = TextIn(Fixed("Room Number", numbers, Center, Vector2.zero, new Vector2(220f, 44f) * s),
            "000", s_SemiBold, Mathf.RoundToInt(29f * s), s_T.mapLabel, TextAnchor.MiddleCenter);
        number.horizontalOverflow = HorizontalWrapMode.Overflow;
        number.verticalOverflow = VerticalWrapMode.Overflow;
        Halo(number, 2.4f * s);
        number.gameObject.AddComponent<CanvasGroup>();
        map.labelTemplate = number;

        // A place's icon on a disc in its colour, with a white rim. The disc must be the last Image (FloorMap
        // colours it).
        RectTransform icons = Stretch("Place Icons", overlay);
        RectTransform badge = Fixed("Place Icon", icons, Center, Vector2.zero, new Vector2(56f, 56f) * s);
        badge.gameObject.AddComponent<CanvasGroup>();
        Rounded(Stretch("Rim", badge), s_T.surface, 28f * s);
        Rounded(Stretch("Disc", badge, 4f * s, 4f * s, 4f * s, 4f * s), s_T.restroom, 28f * s - 4f * s);
        TextIn(Stretch("Glyph", badge), PlaceStyle.IconRestroom, s_Icons, Mathf.RoundToInt(32f * s), s_T.onPlace,
            TextAnchor.MiddleCenter);
        map.badgeTemplate = badge.gameObject;

        // The selected place: a teardrop with its tip on the place, the name beside its head.
        const float tip = 3.5f / 168f;   // the tip is 3.5 px above the bottom of the 168 px tall Pin.png
        RectTransform pin = Child("Pin", overlay);
        pin.sizeDelta = new Vector2(76f, 106f) * s;
        pin.pivot = new Vector2(0.5f, tip);
        var head = Stretch("Head", pin).gameObject.AddComponent<Image>();
        head.sprite = s_Pin;
        head.color = s_T.destination;
        head.raycastTarget = false;
        RectTransform pinName = Fixed("Name", pin, new Vector2(0.5f, tip), new Vector2(38f * s + 6f * s, 72f * s),
            new Vector2(300f, 46f) * s);
        pinName.pivot = new Vector2(0f, 0.5f);
        Text pinLabel = TextIn(pinName, "", s_SemiBold, Mathf.RoundToInt(34f * s), s_T.destination, TextAnchor.MiddleLeft);
        pinLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
        Halo(pinLabel, 2.6f * s);
        map.pin = pin.gameObject;
        map.pinLabel = pinLabel;

        // The user: a disc for how far off the phone's location may be (FloorMap sizes it), a beam the way they
        // face, and their marker: the blue dot (a white rim and the dot) or an emoji on a white disc.
        RectTransform you = Child("You", overlay);
        you.sizeDelta = Vector2.zero;
        map.puck = you.gameObject;
        RectTransform accuracy = Fixed("Accuracy", you, Center, Vector2.zero, new Vector2(100f, 100f));
        Rounded(accuracy, Alpha(s_T.primary, 0.15f), 50f);
        map.puckAccuracy = accuracy;
        var beam = Fixed("Beam", you, Center, Vector2.zero, new Vector2(230f, 230f) * s).gameObject.AddComponent<Image>();
        beam.sprite = s_Cone;
        beam.color = Alpha(s_T.primary, 0.75f);
        beam.raycastTarget = false;
        map.puckCone = beam.gameObject;
        RectTransform dot = Fixed("Dot", you, Center, Vector2.zero, new Vector2(50f, 50f) * s);
        Card(dot, Color.white, 25f * s, true);
        map.puckFill = Dot("Fill", dot, Center, Vector2.zero, 36f * s, s_T.primary);
        map.puckDot = dot.gameObject;
        RectTransform avatar = Fixed("Avatar", you, Center, Vector2.zero, new Vector2(84f, 84f) * s);
        Card(avatar, Color.white, 42f * s, true);
        var emoji = Fixed("Emoji", avatar, Center, Vector2.zero, new Vector2(60f, 60f) * s).gameObject.AddComponent<Image>();
        emoji.sprite = s_Emoji[0];
        emoji.preserveAspect = true;
        emoji.raycastTarget = false;
        avatar.gameObject.SetActive(false);
        map.puckAvatar = avatar.gameObject;
        map.puckEmoji = emoji;
        // A "?" on the marker's shoulder while the position is lost (FloorMap.TintPuck).
        RectTransform unknown = Fixed("Unknown", you, Center, new Vector2(30f, 30f) * s, new Vector2(40f, 40f) * s);
        Card(unknown, s_T.onSurfaceVariant, 20f * s, true);
        TextIn(Stretch("Mark", unknown), "?", s_Bold, Mathf.RoundToInt(30f * s), Color.white, TextAnchor.MiddleCenter);
        unknown.gameObject.SetActive(false);
        map.puckUnknown = unknown.gameObject;
        return map;
    }

    // One part of the campus's buildings, a canvas of its own, drawn with the walls' material.
    static CampusBuildings Buildings(string name, RectTransform content, CampusBuildings.Part part)
    {
        RectTransform rect = Child(name, content);
        var graphic = rect.gameObject.AddComponent<CampusBuildings>();
        graphic.part = part;
        graphic.material = s_WallMaterial;
        graphic.raycastTarget = false;
        rect.gameObject.AddComponent<Canvas>();
        return graphic;
    }
}
