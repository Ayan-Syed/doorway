using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Indoors: GPS can't place the phone inside the building, so positioning changes over at the door. Outside, the map's
// dot is GPS. Coming in, the user "snaps" to the door (back to it, phone up, Confirm), and from then on the Localizer
// keeps the position from the camera, the walls and the room numbers it reads, and the dot is its estimate:
//   Converging  amber, with a halo as wide as the estimate is unsure
//   Localized   the usual colour; Directions goes straight to walking from here
//   Lost        grey with a "?", and a banner asking for a room number (or a door to start from)
// The snap card is the way in: offered by the Localizer when it thinks the phone just came through an entrance, or
// opened by hand ("I'm inside", or a room door from the door list).
public partial class NavigationScreen
{
    [Header("Indoor positioning")]
    [Tooltip("Keeps the position indoors. None: GPS only, every walk starts at a door.")]
    public Localizer localizer;
    [Tooltip("On the map while outdoors: opens the snap card at the nearest entrance.")]
    public CanvasGroup insideButton;
    [Tooltip("The snap card: \"Just came in the North entrance?\", Confirm, other entrances, a room door, Not now.")]
    public CanvasGroup snapPanel;
    public RectTransform snapCard;
    public Text snapTitle;
    public Text snapHint;
    public Button snapConfirm;
    [Tooltip("The other entrances (or doors), nearest first.")]
    public Button[] snapChips;
    public Text[] snapChipLabels;
    public Text snapChipsLabel;
    public Button snapDoorList;
    public Button snapNotNow;
    [Tooltip("Over the map, above the sheet: what the Localizer needs from the user to keep the position.")]
    public CanvasGroup positionBanner;
    public Text positionBannerText;
    public Text positionBannerIcon;
    public Button positionBannerButton;
    [Tooltip("In Settings: back to GPS.")]
    public Button leftBuildingButton;
    public Text leftBuildingDetail;
    [Tooltip("Particles, numbers and checkpoints for the team (hold the settings gear).")]
    public DebugOverlay debugOverlay;

    const string NeedsViewMessage = "Hold your phone up to update your location";
    const string AskForSignMessage = "Point your camera at any room number";
    const string IconCamera = "";      // photo_camera
    const string IconSign = "";        // visibility
    const float BannerHeight = 112f;
    const float BannerWithButton = 196f;
    // The dot is re-laid out only when it has moved or turned this much, or its halo changed this much.
    const float DotMove = 0.05f;
    const float DotTurn = 1f;
    const float DotSpread = 0.1f;
    const string DebugKey = "debug";

    // What the Localizer says, read once a frame (or made up by a preview hook).
    struct Indoor
    {
        public LocalizerState state;
        public Vector2 position;
        public float heading, spread;
        public bool needsView, askForSign, entrancePrompt;
        public PlanDoor entranceGuess;
    }

    Indoor m_Indoor;
    bool m_PreviewingIndoor;
    Indoor m_PreviewIndoor;

    bool m_SnapOpen;
    PlanDoor m_SnapDoor;
    bool m_SnapEntrance;           // offering entrances (else room doors)
    bool m_SnapFromPrompt;         // the Localizer asked: Not now tells it so
    bool m_PromptHandled;          // answered: not offered again until the Localizer stops asking
    readonly List<(PlanPlace place, PlanDoor door)> m_SnapOthers = new List<(PlanPlace place, PlanDoor door)>();
    bool m_PickingDoor;            // the door list is for where you are, not for a walk

    Vector2 m_DotAt = new Vector2(float.NaN, float.NaN);
    float m_DotHeading, m_DotSpread;
    LocalizerState m_DotState;

    float m_GearSince = -1f;       // when the press on the settings gear began (-1: none)
    bool m_SwallowSettingsTap;     // the long press opened the overlay: the tap that ends it doesn't open Settings
    bool m_Debug;

    bool Positioning => localizer != null || m_PreviewingIndoor;

    Indoor ReadIndoor()
    {
        if (m_PreviewingIndoor)
            return m_PreviewIndoor;
        if (localizer == null)
            return default;   // Outdoors
        return new Indoor
        {
            state = localizer.State,
            position = localizer.Position,
            heading = localizer.Heading,
            spread = localizer.Spread,
            needsView = localizer.NeedsView,
            askForSign = localizer.AskForSign,
            entrancePrompt = localizer.EntrancePrompt,
            entranceGuess = localizer.EntranceGuess,
        };
    }

    void WireIndoor()
    {
        if (localizer == null)
            localizer = FindAnyObjectByType<Localizer>();
        if (localizer != null)
        {
            localizer.SignRead += OnSignRead;
            localizer.StateChanged += OnIndoorStateChanged;
        }
        if (insideButton != null)
            insideButton.GetComponent<Button>().onClick.AddListener(OnInside);
        if (snapConfirm != null)
            snapConfirm.onClick.AddListener(ConfirmSnap);
        for (int i = 0; snapChips != null && i < snapChips.Length; i++)
        {
            int chip = i;
            snapChips[i].onClick.AddListener(() =>
            {
                if (chip < m_SnapOthers.Count)
                    OpenSnapCard(m_SnapOthers[chip].place, m_SnapOthers[chip].door, m_SnapEntrance, m_SnapFromPrompt);
            });
        }
        if (snapDoorList != null)
            snapDoorList.onClick.AddListener(() =>
            {
                CloseSnapCard(true);
                ShowDoorPicker();
            });
        if (snapNotNow != null)
            snapNotNow.onClick.AddListener(() => CloseSnapCard(true));
        if (positionBannerButton != null)
            positionBannerButton.onClick.AddListener(ShowDoorPicker);
        if (leftBuildingButton != null)
            leftBuildingButton.onClick.AddListener(LeftBuilding);
        if (snapPanel != null)
            snapPanel.gameObject.SetActive(false);
        if (positionBanner != null)
            positionBanner.gameObject.SetActive(false);
        if (debugOverlay != null)
        {
            debugOverlay.Toast = Snack;
            m_Debug = Application.isPlaying && PlayerPrefs.GetInt(DebugKey, 0) == 1;
            debugOverlay.Show(m_Debug);
        }
    }

    void UnwireIndoor()
    {
        if (localizer == null)
            return;
        localizer.SignRead -= OnSignRead;
        localizer.StateChanged -= OnIndoorStateChanged;
    }

    // Every frame: the snap card when the Localizer offers it, the dot, the banner, "I'm inside".
    void UpdateIndoor()
    {
        Indoor was = m_Indoor;
        m_Indoor = ReadIndoor();
        bool inside = m_Indoor.state != LocalizerState.Outdoors;

        if (!m_Indoor.entrancePrompt)
            m_PromptHandled = false;
        else if (!m_PromptHandled && !m_SnapOpen && !m_SettingsOpen && m_Step <= Step.Place && m_Indoor.entranceGuess != null)
        {
            PlanPlace entrance = PlaceOf(m_Indoor.entranceGuess);
            if (entrance != null)
                OpenSnapCard(entrance, m_Indoor.entranceGuess, true, true);
        }

        if (m_Step <= Step.Start
            && (inside != (was.state != LocalizerState.Outdoors) || m_Indoor.state != m_DotState || DotMoved()))
            UpdateGpsPuck();
        map.TintPuck(PuckColor(), m_Indoor.state == LocalizerState.Lost);   // again after a day/night switch
        UpdateBanner();
        if (insideButton != null)
        {
            bool shown = Positioning && !inside && m_Step == Step.Home && !m_Searching;
            if (insideButton.gameObject.activeSelf != shown)
                insideButton.gameObject.SetActive(shown);
        }
        UpdateGearHold();
    }

    bool DotMoved()
    {
        if (m_Indoor.state == LocalizerState.Outdoors)
            return false;
        return float.IsNaN(m_DotAt.x) || (m_Indoor.position - m_DotAt).sqrMagnitude > DotMove * DotMove
            || Mathf.Abs(Mathf.DeltaAngle(m_Indoor.heading, m_DotHeading)) > DotTurn
            || Mathf.Abs(m_Indoor.spread - m_DotSpread) > DotSpread;
    }

    // The map's dot indoors: the Localizer's estimate, its halo the estimate's spread, coloured by how sure it is.
    // False outdoors (the dot is GPS's then).
    bool ShowIndoorDot()
    {
        if (m_Indoor.state == LocalizerState.Outdoors)
        {
            map.TintPuck(Theme.Current.primary, false);
            return false;
        }
        m_DotAt = m_Indoor.position;
        m_DotHeading = m_Indoor.heading;
        m_DotSpread = m_Indoor.spread;
        m_DotState = m_Indoor.state;
        Color color = PuckColor();
        map.ShowPuck(m_Indoor.position, m_Indoor.heading, Mathf.Max(m_Indoor.spread, MinAccuracyShown),
            m_Indoor.state != LocalizerState.Lost);
        map.TintPuck(color, m_Indoor.state == LocalizerState.Lost);
        return true;
    }

    // The dot's colour: the usual one outdoors and when sure, amber while converging, grey when lost.
    Color PuckColor()
    {
        Theme theme = Theme.Current;
        switch (m_Indoor.state)
        {
            case LocalizerState.Converging: return theme.warning;
            case LocalizerState.Lost: return theme.onSurfaceVariant;
            default: return theme.primary;
        }
    }

    // Over the map (above the sheet) or under the turn banner while walking: what keeps the position going.
    void UpdateBanner()
    {
        if (positionBanner == null)
            return;
        bool ask = m_Indoor.askForSign;
        bool look = m_Indoor.needsView && !ask;
        bool shown = (ask || look) && !m_SnapOpen && !m_SettingsOpen && m_Step <= Step.Place;
        if (positionBanner.gameObject.activeSelf != shown)
            positionBanner.gameObject.SetActive(shown);
        if (!shown)
            return;
        positionBannerText.text = ask ? AskForSignMessage : NeedsViewMessage;
        positionBannerIcon.text = ask ? IconSign : IconCamera;
        if (positionBannerButton.gameObject.activeSelf != ask)
            positionBannerButton.gameObject.SetActive(ask);
        var rect = (RectTransform)positionBanner.transform;
        var size = new Vector2(rect.sizeDelta.x, ask ? BannerWithButton : BannerHeight);
        if (rect.sizeDelta != size)
            rect.sizeDelta = size;
        float y = Mathf.Min(BottomUI(), safeArea.rect.height * 0.6f) + 16f;
        if (rect.anchoredPosition.y != y)
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, y);
    }

    // How much of the screen's bottom is taken: by the step's sheet or card, or Settings.
    float BottomUI()
    {
        if (settingsPanel.gameObject.activeSelf)
            return settingsSheet.Height;
        if (m_SnapOpen && snapCard != null)
            return TopOf(snapCard);
        switch (m_Step)
        {
            case Step.Home: return homeSheet.Height;
            case Step.Place: return placeSheet.Height;
            case Step.Start: return startSheet.Height;
            case Step.Align: return alignSheet.Height;
            case Step.Guide: return TopOf(miniMapFrame);
            default: return TopOf(arrivedCard);
        }
    }

    // The banner's height, for the snackbar to sit above it.
    float BannerTaken => positionBanner != null && positionBanner.gameObject.activeSelf
        ? ((RectTransform)positionBanner.transform).sizeDelta.y + 16f : 0f;

    // The snap card for a door: entrance, "Just came in the North entrance?" with the other entrances to choose
    // from; else "At Room 113's door?" with the doors round it. fromPrompt: the Localizer offered it.
    void OpenSnapCard(PlanPlace place, PlanDoor door, bool entrance, bool fromPrompt)
    {
        if (snapPanel == null || place == null || door == null)
            return;
        Haptics.Tick(15);
        m_SnapOpen = true;
        m_SnapDoor = door;
        m_SnapEntrance = entrance;
        m_SnapFromPrompt = fromPrompt;
        SetFloorMenu(false);
        if (searchField.isFocused)
            searchField.DeactivateInputField();

        string title = PlaceStyle.Title(place);
        snapTitle.text = entrance ? $"Just came in the {title}?" : $"At {PlaceStyle.DoorTitle(place, door)}?";
        snapHint.text = entrance ? "Stand just inside the doors, back to them, phone up"
            : "Stand in the doorway, back to the door, phone up";
        snapChipsLabel.text = entrance ? "Another entrance" : "Another door";

        // The others nearest this one: entrances, or any doors.
        m_SnapOthers.Clear();
        Vector2 at = Plan.NodePosition(door.node);
        var others = new List<(PlanPlace place, PlanDoor door, float distance)>();
        foreach (PlanPlace other in Plan.places)
        {
            if (entrance && other.kind != "exit")
                continue;
            foreach (PlanDoor otherDoor in other.doors)
                if (otherDoor != door)
                    others.Add((other, otherDoor, Vector2.Distance(at, Plan.NodePosition(otherDoor.node))));
        }
        others.Sort((a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < others.Count && m_SnapOthers.Count < snapChips.Length; i++)
            m_SnapOthers.Add((others[i].place, others[i].door));
        for (int i = 0; i < snapChips.Length; i++)
        {
            bool shown = i < m_SnapOthers.Count;
            if (snapChips[i].gameObject.activeSelf != shown)
                snapChips[i].gameObject.SetActive(shown);
            if (shown)
                snapChipLabels[i].text = entrance ? PlaceStyle.Title(m_SnapOthers[i].place)
                    : PlaceStyle.DoorTitle(m_SnapOthers[i].place, m_SnapOthers[i].door);
        }
        if (snapDoorList != null && snapDoorList.gameObject.activeSelf != entrance)
            snapDoorList.gameObject.SetActive(entrance);   // from the door list it would only go back to it

        if (!snapPanel.gameObject.activeSelf)
        {
            snapPanel.alpha = Application.isPlaying ? 0f : 1f;
            snapPanel.gameObject.SetActive(true);
        }
        // The door on the map, the way the user will face up.
        if (m_Step <= Step.Start)
        {
            map.Select(place.kind == "exit" ? null : place);
            map.ShowPin(at, null);
            SetInsets(BottomOf(m_Step == Step.Start ? routeCard : searchCard) + 8f, TopOf(snapCard) + 8f);
            map.Overview(null, at, Navigator.Heading(-door.Direction), !Application.isPlaying);
        }
    }

    // answered: the user said no (Not now, a door instead), which the Localizer is told if it asked.
    void CloseSnapCard(bool answered)
    {
        if (!m_SnapOpen)
            return;
        m_SnapOpen = false;
        if (snapPanel != null)
            snapPanel.gameObject.SetActive(false);
        if (m_SnapFromPrompt)
        {
            m_PromptHandled = true;   // not offered again until the Localizer stops asking and asks again
            if (answered && localizer != null)
                localizer.DismissEntrancePrompt();
        }
        // The map back as the step had it.
        if (m_Step == Step.Home)
        {
            map.HidePin();
            UpdateGpsPuck();
            FrameAll(false);
        }
        else if (m_Step == Step.Place && m_Selected != null)
        {
            map.Select(m_Selected);
            map.ShowPin(map.Anchor(m_Selected), PlaceStyle.Title(m_Selected));
            SetInsets(BottomOf(searchCard) + 8f, PlaceHeight);
            map.ShowPlace(m_Selected, false);
        }
        else if (m_Step == Step.Start)
        {
            map.HidePin();
            UpdateGpsPuck();
            SetInsets(BottomOf(routeCard) + 8f, startSheet.stops[1]);
            map.ShowAll(false);
        }
    }

    // Confirm: the position is this door, facing away from it. Back to the floor's map with the dot on the door.
    void ConfirmSnap()
    {
        PlanDoor door = m_SnapDoor;
        PlanPlace place = PlaceOf(door);
        if (door == null)
            return;
        bool picking = m_PickingDoor;
        if (localizer != null)
            localizer.SnapToDoor(door);
        CloseSnapCard(false);
        Haptics.Tick(40);
        if (picking || m_Step != Step.Home)
            ResetHome(false);
        Snack(place == null ? "Got you" : place.kind == "exit" ? $"You're at the {PlaceStyle.Title(place)}"
            : $"You're at {PlaceStyle.DoorTitle(place, door)}");
        UpdateGpsPuck();
    }

    // "I'm inside": the snap card at the entrance nearest the phone's location (the first if there's no fix).
    void OnInside()
    {
        Vector2? at = location != null && location.HasFix ? location.Position : (Vector2?)null;
        PlanPlace best = null;
        PlanDoor bestDoor = null;
        float bestDistance = float.PositiveInfinity;
        foreach (PlanPlace place in Plan.places)
        {
            if (place.kind != "exit")
                continue;
            foreach (PlanDoor door in place.doors)
            {
                float distance = at.HasValue ? Vector2.Distance(at.Value, Plan.NodePosition(door.node)) : 0f;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = place;
                    bestDoor = door;
                }
            }
        }
        if (best == null)
        {
            ShowDoorPicker();
            return;
        }
        OpenSnapCard(best, bestDoor, true, false);
    }

    // The door list, to say where you are (not to start a walk): a door picked opens the snap card for it.
    void ShowDoorPicker()
    {
        CloseSnapCard(true);
        ShowStart(null, false);
        m_PickingDoor = true;
        destinationText.text = "Set your location";
    }

    // A door picked in the door list or on the map while on Start: lining up for a walk, or (picking where you
    // are) the snap card.
    bool StartFrom(PlanPlace place, PlanDoor door, Vector2? near)
    {
        if (m_PickingDoor)
        {
            OpenSnapCard(place, door, place.kind == "exit", false);
            return true;
        }
        return ShowAlign(place, door, false, near);
    }

    // Directions from the place card. Indoors and sure of the position: walking from here at once. Otherwise as
    // before: outdoors the start is picked from GPS (an entrance), and lost or still converging, the user picks the
    // door they're at, which snaps the position there when the walk starts.
    void OnDirections()
    {
        PlanPlace place = m_Selected;
        if (place == null)
            return;
        if (m_Indoor.state == LocalizerState.Localized && localizer != null && Application.isPlaying)
        {
            m_Destination = m_Selected = place;
            if (navigator.BeginFrom(place))
            {
                Haptics.Tick(40);
                m_AnnouncedRoute = null;
                m_EndConfirmUntil = 0f;
                ShowGuide();
                return;
            }
            Snack("Couldn't find a way from here · pick the door you're at");
            ShowStart(place, false);
            return;
        }
        if (m_Indoor.state == LocalizerState.Outdoors)
        {
            ShowStart(place, true);
            return;
        }
        ShowStart(place, false);
        Snack(m_Indoor.state == LocalizerState.Lost ? "Not sure where you are · pick the door you're at"
            : "Still finding you · pick the door you're at");
    }

    // "I've left the building": GPS again.
    void LeftBuilding()
    {
        if (m_Indoor.state == LocalizerState.Outdoors)
            return;
        if (localizer != null)
            localizer.GoOutdoors();
        CloseSettings();
        Haptics.Tick(15);
        Snack("Outdoors · the map follows GPS again");
    }

    void StyleLeftBuilding()
    {
        if (leftBuildingButton == null)
            return;
        bool inside = m_Indoor.state != LocalizerState.Outdoors;
        SetInteractable(leftBuildingButton, inside);
        if (leftBuildingDetail != null)
            leftBuildingDetail.text = inside ? "Back to GPS for the campus map" : "You're outdoors: the map uses GPS";
    }

    void OnSignRead(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        Snack(char.IsDigit(text[0]) ? $"Saw room {text}" : $"Saw {text}");
        if (debugOverlay != null)
            debugOverlay.SawSign(text);
    }

    void OnIndoorStateChanged()
    {
        m_Indoor = ReadIndoor();
        UpdateGpsPuck();
        UpdateBanner();
        StyleLeftBuilding();
    }

    // Holding the settings gear for DebugHoldSeconds turns the debug overlay on or off (and remembers it).
    void UpdateGearHold()
    {
        Pointer pointer = Pointer.current;
        if (pointer == null || settingsButton == null || debugOverlay == null)
            return;
        if (pointer.press.wasPressedThisFrame)
        {
            m_SwallowSettingsTap = false;
            bool onGear = m_Step <= Step.Place && IsShown(homePanel) && RectTransformUtility.RectangleContainsScreenPoint(
                (RectTransform)settingsButton.transform, pointer.position.ReadValue(), uiCamera);
            m_GearSince = onGear ? Time.unscaledTime : -1f;
        }
        if (!pointer.press.isPressed)
        {
            m_GearSince = -1f;
            return;
        }
        if (m_GearSince < 0f || Time.unscaledTime - m_GearSince < DebugHoldSeconds)
            return;
        m_GearSince = -1f;
        m_SwallowSettingsTap = true;
        SetDebug(!m_Debug);
        Haptics.Tick(60);
        Snack(m_Debug ? "Debug overlay on" : "Debug overlay off");
    }

    void SetDebug(bool on)
    {
        m_Debug = on;
        if (Application.isPlaying)
        {
            PlayerPrefs.SetInt(DebugKey, on ? 1 : 0);
            PlayerPrefs.Save();
        }
        if (debugOverlay != null)
            debugOverlay.Show(on);
    }

    // The place a door belongs to.
    PlanPlace PlaceOf(PlanDoor door)
    {
        if (door == null)
            return null;
        foreach (PlanPlace place in Plan.places)
            if (place.doors.Contains(door))
                return place;
        return null;
    }
}
