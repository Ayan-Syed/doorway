using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// The floor plan as an indoor map, the way a phone map app draws one: filled rooms, room numbers, icons for
// restrooms, elevators, stairs and entrances, the selected place outlined with a pin on it, the walking route as
// dots, and the user's dot. Room numbers and icons stay upright and the same size whatever the zoom or turn, and
// only show where there's room for them.
//
// The camera is orthographic, so a tilt just squashes the floor's up and down by its cosine, which is all ToWindow
// and ToPlan need to know.
//
// Browse (drag to pan, pinch or scroll to zoom, tap a place) unless `interactive` is off. Follow keeps the user in
// the middle, heading-up (the minimap). Overview fits a route, turned so a given direction points up.
//
// Zoomed out, you see the campus with its buildings standing up and named. Zooming in to the floor's building, its
// roof sinks away and the floor fades in (indoorFrom to indoorTo). ShowCampus is the view the app opens on.
//
// The parts: FloorMap.Places (the place map and the highlight), FloorMap.Campus (buildings and their names),
// FloorMap.Views (framing, route, pin and puck), FloorMap.Labels (Layout: who shows where), FloorMap.Gestures.
public partial class FloorMap : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IBeginDragHandler,
    IDragHandler, IEndDragHandler, IPointerClickHandler, IScrollHandler
{
    public RectTransform rotator;
    public RectTransform content;
    public RectTransform overlay;
    public RawImage basemap;
    public RawImage highlight;
    public UILine routeCasing;
    public UILine routeLine;
    [Tooltip("Which place each part of the floor belongs to (Data/*-places.bytes).")]
    public TextAsset placeMap;
    [Tooltip("A room number, copied for each room. Has a CanvasGroup.")]
    public Text labelTemplate;
    [Tooltip("A place icon: a rim Image, a disc Image under it and a glyph Text. Has a CanvasGroup.")]
    public GameObject badgeTemplate;
    [Tooltip("The selected place's pin, its tip at the pivot.")]
    public GameObject pin;
    public Text pinLabel;
    public GameObject puck;
    public GameObject puckCone;
    [Tooltip("The user's marker: the blue dot, or an emoji on a disc (SetMarker).")]
    public GameObject puckDot;
    public GameObject puckAvatar;
    public Image puckEmoji;
    [Tooltip("How far off the position may be: a disc on the floor, 100 x 100 at scale 1.")]
    public RectTransform puckAccuracy;
    [Tooltip("The blue dot's fill: TintPuck colours it (and the disc) by how sure the position is.")]
    public Image puckFill;
    [Tooltip("A \"?\" badge on the marker, while the position is lost.")]
    public GameObject puckUnknown;
    [Tooltip("The walls standing up off the floor, shown while the map is tilted.")]
    public WallMesh walls;
    [Tooltip("The campus round the building (Data/ucf-campus.bytes and .json); none for just the floor.")]
    public TextAsset campusGeometry;
    public TextAsset campusInfo;
    public CampusGround campusGround;
    [Tooltip("The campus's buildings: those behind the floor's building, the building itself, and those in front.")]
    public CampusBuildings[] campusBuildings = new CampusBuildings[0];
    [Tooltip("A building's name, copied for each named building. Has a CanvasGroup.")]
    public Text buildingLabelTemplate;
    [Tooltip("Things over the map (buttons, the map's credit) that labels keep clear of while they show.")]
    public RectTransform[] keepClear = new RectTransform[0];

    [Tooltip("Canvas units per plan metre at zoom 1.")]
    public float pixelsPerMeter = 10f;
    // 0.6 suits the minimap. The big map needs 0.04 to zoom out over the whole campus (ShowCampus at 900 m is
    // zoom 0.12, the launch view about 0.055), so the scene builder sets that on it.
    public float minZoom = 0.6f;
    public float maxZoom = 6f;
    public float followZoom = 1.6f;
    [Tooltip("Following, the user sits this much of the window's height below its middle, so more shows ahead.")]
    public float followAhead = 0f;
    [Tooltip("Route dots: their size, their outline's size and the gap between them, on screen.")]
    public float routeWidth = 17f;
    public float casingWidth = 25f;
    public float dotSpacing = 31f;
    public bool interactive = true;
    [Tooltip("A room number shows once its room is this many times the number's size on screen.")]
    public float labelRoom = 2.5f;
    [Tooltip("Zooming in from this zoom to this one, the building's roof sinks away and its floor shows.")]
    public float indoorFrom = 0.32f;
    public float indoorTo = 0.5f;

    // Strips of the window covered by other UI (the search bar, the bottom sheet): framing keeps clear of them.
    [NonSerialized] public float topInset;
    [NonSerialized] public float bottomInset;

    const float Margin = 48f;
    // How much of the campus ShowCampus fits across the window, in metres.
    public const float CampusSpan = 900f;
    // A building's name shows once the building's this wide on screen (or it's the floor's building).
    const float BuildingLabelRoom = 44f;

    // A tap on the map: the place there (null for a hallway or a room that isn't a place) and the plan point.
    public event Action<PlanPlace, Vector2> PlaceTapped;
    // A tap on one of the campus's buildings, outside the floor.
    public event Action<CampusData.Building> BuildingTapped;

    public FloorPlan Plan => m_Plan;
    public float Rotation => m_Rotation;
    public CampusData Campus => m_Campus;
    public float Zoom => m_Zoom;
    // Where the map looks, and whether it's on its way somewhere it was sent (not a finger's drag or fling).
    public Vector2 Center => m_Center;
    public bool IsMoving => m_Animating;

    FloorPlan m_Plan;
    CampusData m_Campus;

    Vector2 m_Center, m_TargetCenter;
    float m_Zoom = 1f, m_TargetZoom = 1f;
    float m_Rotation, m_TargetRotation;
    float m_Tilt, m_TargetTilt;
    bool m_Animating, m_Following, m_Dirty;
    float m_Indoor = -1f;   // so the first Layout always calls ShowFloor
    float m_LinesZoom = -1f;
    string m_Kind;

    Route m_Route;
    float m_RouteFrom = float.NegativeInfinity;

    static readonly Vector3[] s_Corners = new Vector3[4];

    RectTransform Window => (RectTransform)transform;

    static RectTransform RectOf(GameObject target) => (RectTransform)target.transform;

    // The plan image's extent in plan metres; y points up and image.top is the top edge.
    Vector2 PlanSize => new Vector2(m_Plan.image.width, m_Plan.image.height) * m_Plan.image.metersPerPixel;
    Rect PlanRect
    {
        get
        {
            Vector2 size = PlanSize;
            return new Rect(m_Plan.image.left, m_Plan.image.top - size.y, size.x, size.y);
        }
    }

    // Builds everything for a floor. Markers aren't pooled: the app loads one plan.
    public void SetPlan(FloorPlan plan)
    {
        if (m_Plan == plan)
            return;
        m_Plan = plan;
        if (plan == null)
            return;

        float ppm = pixelsPerMeter;
        Vector2 size = PlanSize;
        foreach (RawImage image in new[] { basemap, highlight })
        {
            if (image == null)
                continue;
            RectTransform rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(plan.image.left, plan.image.top) * ppm;
            rect.sizeDelta = size * ppm;
        }
        if (highlight != null)
            highlight.gameObject.SetActive(false);

        if (walls != null)
        {
            // Its origin at plan (0, 0), and its rect over the floor, so it's never culled as off screen.
            RectTransform rect = walls.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(-plan.image.left / size.x, (size.y - plan.image.top) / size.y);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size * ppm;
            walls.pixelsPerMeter = ppm;
            walls.SetVerticesDirty();
        }

        ReadPlaceMap();
        FindPlaces();
        MakeRoomNumbers();
        MakeBadges();
        SetCampus();

        m_Center = m_TargetCenter = PlanRect.center;
        m_Dirty = true;
    }

    void MakeRoomNumbers()
    {
        if (labelTemplate == null)
            return;
        labelTemplate.gameObject.SetActive(false);
        foreach (PlanLabel label in m_Plan.labels)
        {
            PlanPlace place = PlaceAt(label.Position);
            // Restrooms, stairs and elevators have their icon instead.
            if (place != null && place.kind != "room")
                continue;

            // A named place shows its name.
            string text = label.text;
            if (place != null && place.name != null && place.name.StartsWith(label.text + " ", StringComparison.Ordinal))
                text = place.name.Substring(label.text.Length + 1);

            Text copy = Instantiate(labelTemplate, labelTemplate.transform.parent, false);
            copy.name = text;
            copy.text = text;
            Marker marker = AddMarker(copy.rectTransform, label.Position);
            marker.size = new Vector2(copy.preferredWidth + 8f, copy.fontSize * 1.25f);
            marker.area = label.area;
            marker.place = place;
            m_Labels.Add(marker);
            copy.gameObject.SetActive(false);   // until Fade shows it
        }
        // Claim priority: biggest rooms first.
        m_Labels = m_Labels.OrderByDescending(m => m.area).ToList();
    }

    void MakeBadges()
    {
        if (badgeTemplate == null)
            return;
        badgeTemplate.SetActive(false);
        foreach (PlanPlace place in m_Plan.places)
        {
            if (place.kind == "room")
                continue;
            // One per door: a restroom with two doors gets two.
            foreach (PlanDoor door in place.doors)
            {
                var copy = (RectTransform)Instantiate(badgeTemplate, badgeTemplate.transform.parent, false).transform;
                copy.name = place.name;
                StyleBadge(copy, place.kind);
                copy.GetComponentInChildren<Text>(true).text = PlaceStyle.Icon(place.kind);
                Marker marker = AddMarker(copy, BadgeSpot(door));
                marker.size = copy.sizeDelta;
                marker.place = place;
                m_Badges.Add(marker);
                copy.gameObject.SetActive(false);   // until Fade shows it
            }
        }
    }

    // The disc is the badge's last Image.
    static void StyleBadge(RectTransform badge, string kind)
    {
        Image[] images = badge.GetComponentsInChildren<Image>(true);
        if (images.Length > 0)
            images[images.Length - 1].color = PlaceStyle.Color(kind);
    }

    // After a day/night switch: room numbers and icons take their templates' new colours.
    public void Restyle()
    {
        foreach (Marker label in m_Labels)
            ThemeColors.CopyColors(labelTemplate, label.rect);
        foreach (Marker badge in m_Badges)
        {
            ThemeColors.CopyColors(badgeTemplate.transform, badge.rect);
            StyleBadge(badge.rect, badge.place.kind);   // CopyColors gave it the template's restroom colour
        }
        if (campusGround != null)
            campusGround.Restyle();
        foreach (CampusBuildings buildings in campusBuildings)
            if (buildings != null)
                buildings.Restyle();
        foreach (Marker label in m_BuildingLabels)
            if (label.rect != null)
                StyleBuildingLabel(label);
    }

    void Update()
    {
        if (m_Plan == null)
            return;
        float dt = Time.unscaledDeltaTime;

        // A fling slides on and slows down.
        if (m_Pointers.Count == 0 && m_Velocity.sqrMagnitude > 1f)
        {
            Pan(m_Velocity * dt);
            m_Velocity *= Mathf.Exp(-dt * 4.5f);
            if (m_Velocity.sqrMagnitude < 400f)
                m_Velocity = Vector2.zero;
        }

        if (m_Animating)
        {
            float k = 1f - Mathf.Exp(-dt * 9f);
            m_Center = Vector2.Lerp(m_Center, m_TargetCenter, k);
            // In log space, so zooming in and out feel equally fast.
            m_Zoom = Mathf.Exp(Mathf.Lerp(Mathf.Log(m_Zoom), Mathf.Log(m_TargetZoom), k));
            m_Rotation = Mathf.LerpAngle(m_Rotation, m_TargetRotation, k);
            if ((m_Center - m_TargetCenter).sqrMagnitude < 0.0004f && Mathf.Abs(m_Zoom - m_TargetZoom) < 0.002f
                && Mathf.Abs(Mathf.DeltaAngle(m_Rotation, m_TargetRotation)) < 0.05f)
            {
                m_Center = m_TargetCenter;
                m_Zoom = m_TargetZoom;
                m_Rotation = m_TargetRotation;
                m_Animating = false;
            }
            m_Dirty = true;
        }

        if (m_Tilt != m_TargetTilt)
        {
            m_Tilt = Mathf.Lerp(m_Tilt, m_TargetTilt, 1f - Mathf.Exp(-dt * 8f));
            if (Mathf.Abs(m_Tilt - m_TargetTilt) < 0.05f)
                m_Tilt = m_TargetTilt;
            m_Dirty = true;
        }

        if (m_Dirty)
            Layout();
    }

    void LateUpdate()
    {
        float step = Time.unscaledDeltaTime * 7f;
        Fade(m_Labels, step);
        Fade(m_Badges, step);
        Fade(m_BuildingLabels, step);
    }

    void OnDestroy()
    {
        if (m_HighlightTexture == null)
            return;
        if (Application.isPlaying)
            Destroy(m_HighlightTexture);
        else
            DestroyImmediate(m_HighlightTexture);
    }

    // Turns a plan vector counterclockwise by `degrees` (how the map turns on screen).
    static Vector2 Turn(Vector2 p, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(p.x * c - p.y * s, p.x * s + p.y * c);
    }

    // How much a tilt shortens the floor's up and down on screen.
    static float Squash(float tilt) => Mathf.Cos(tilt * Mathf.Deg2Rad);

    Vector2 Unsquash(Vector2 v) => new Vector2(v.x, v.y / Squash(m_Tilt));

    // A plan point to window units from the window's centre.
    public Vector2 ToWindow(Vector2 p)
    {
        Vector2 v = Turn(p - m_Center, m_Rotation) * (pixelsPerMeter * m_Zoom);
        return new Vector2(v.x, v.y * Squash(m_Tilt));
    }

    // A window point (from its centre) to the plan point drawn there.
    public Vector2 ToPlan(Vector2 w) => m_Center + Turn(Unsquash(w), -m_Rotation) / (pixelsPerMeter * m_Zoom);
}
