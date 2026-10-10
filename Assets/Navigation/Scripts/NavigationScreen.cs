using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// The app's screens, laid out like a phone map app's:
//   Home     the map (out over the campus as the app opens, in on the floor after), a search bar, filter chips,
//            and the building's directory in a bottom sheet
//   Place    a place picked on the map or in the list: its card, and Directions
//   Start    "Where are you?" Directions picks the door nearest the phone's location (an entrance if it's outside),
//            waiting a few seconds for a fix if it has to. Otherwise the user picks a door in the list or taps the map
//   Align    the route on the map, turned the way the user will face, other doors nearby, and Start once AR tracks
//   Guide    the next turn, a minimap and what's left; the route and the destination are drawn in AR
//   Arrived  "You've arrived"
// Over them: Settings (your marker on the map, light or dark, the 3D map, a pretend location for demos, spoken
// directions), the floor menu, and the mic button: everything above can be done by talking (NavigationScreen.Voice). The navigation itself is Navigator's and the map is FloorMap's; the scene builder lays everything
// out and wires these fields. The rest of the class is in the NavigationScreen.*.cs files, one per screen.
public partial class NavigationScreen : MonoBehaviour
{
    // In this order: steps are compared with < and >. Step <= Align means the map shows; Step >= Start, AR is on.
    enum Step { Home, Place, Start, Align, Guide, Arrived }

    public Navigator navigator;
    [Tooltip("The day colour theme the scene was built with (Theme.All).")]
    public string themeName = "iris";
    [Tooltip("The night colour theme (ThemeColors holds its colours).")]
    public string nightThemeName = "dusk";
    public RectTransform safeArea;
    public FloorMap map;
    [Tooltip("Draws the UI: over the map's ground while the map fills the screen, over the camera feed after.")]
    public Camera uiCamera;
    public UserLocation location;
    public ThemeColors themeColors;

    [Header("Home")]
    public CanvasGroup homePanel;
    public RectTransform searchCard;
    public InputField searchField;
    public Button searchLead;
    public Text searchLeadIcon;
    public Button searchClear;
    public Button settingsButton;
    public CanvasGroup chipBar;
    [Tooltip("Restrooms, elevators, stairs, entrances.")]
    public Button[] chips;
    public Image[] chipBackgrounds;
    public Text[] chipLabels;
    [Tooltip("The floor button, which opens the floor menu.")]
    public CanvasGroup floorPicker;
    [Tooltip("The floor button's label: the plan's floor. Found under floorPicker if not set.")]
    public Text floorLabel;
    public CanvasGroup floorMenu;
    [Tooltip("The floor menu's header: the plan's building. Found as Card/Building under floorMenu if not set.")]
    public Text floorMenuTitle;
    [Tooltip("Fills the screen behind the floor menu: a tap anywhere else closes it.")]
    public Button floorMenuBlocker;
    [Tooltip("The floor menu's rows, Floor 1 first.")]
    public Button[] floorButtons;
    public CanvasGroup wholeFloor;
    [Tooltip("The whole-floor button's label and icon: Go inside while the map's out over the campus.")]
    public Text wholeFloorLabel;
    public Text wholeFloorIcon;
    [Tooltip("Centres the map on the phone's location.")]
    public CanvasGroup locate;
    [Tooltip("The map data's credit, over the map at the sheet's top left.")]
    public CanvasGroup attribution;
    public BottomSheet homeSheet;
    public Text homeTitle;
    public Text homeSubtitle;
    [Tooltip("The search box in the directory sheet, over the list: it searches the building's places only.")]
    public CanvasGroup roomSearch;
    public InputField roomSearchField;
    public Button roomSearchClear;
    public PlaceList homeList;

    [Header("Place")]
    public BottomSheet placeSheet;
    public Text placeTitle;
    public Text placeSubtitle;
    public Text placeDoors;
    public Text placeFloor;
    public Button directionsButton;
    public Button placeClose;

    [Header("Directions")]
    public CanvasGroup routePanel;
    public RectTransform routeCard;
    public Button routeBack;
    public InputField originField;
    public Button originButton;
    public Text originText;
    public Button destinationButton;
    public Text destinationText;
    public CanvasGroup faceChip;
    public BottomSheet startSheet;
    public PlaceList startList;
    [Tooltip("Under the Start sheet's title: what to do. findingRow takes its place while the phone's location is found.")]
    public GameObject startHint;
    public GameObject findingRow;
    public BottomSheet alignSheet;
    public Text alignTime;
    public Text alignDistance;
    public Text alignHint;
    [Tooltip("Other doors near you, to start from one of them instead.")]
    public Button[] nearbyChips;
    public Text[] nearbyLabels;
    [Tooltip("All the doors, in a list.")]
    public Button moreButton;
    public Button beginButton;
    public Text beginLabel;

    [Header("Guidance")]
    public CanvasGroup guidePanel;
    public RectTransform banner;
    public Text maneuverIcon;
    public Text maneuverDistance;
    public Text maneuverTitle;
    public Text maneuverDetail;
    public RectTransform thenTab;
    public Text thenIcon;
    public RectTransform statusPill;
    public Image statusDot;
    public Text statusText;
    public RectTransform miniMapFrame;
    public FloorMap miniMap;
    [Tooltip("The minimap's corner icon: tap to make it big, or small again (NavigationScreen.MiniMap).")]
    public Text miniMapIcon;
    public Text etaText;
    public Text etaDetail;
    [Tooltip("How much of the route is walked: its anchorMax.x goes from 0 to 1.")]
    public RectTransform progressFill;
    public Button endButton;
    public Image endBackground;
    public Text endLabel;
    public Button realignButton;

    [Header("Arrived")]
    public CanvasGroup arrivedPanel;
    public RectTransform arrivedCard;
    public Text arrivedTitle;
    public Text arrivedDetail;
    public Button doneButton;

    [Header("Settings")]
    public CanvasGroup settingsPanel;
    [Tooltip("The dimmed map behind the sheet: a tap closes it.")]
    public CanvasGroup settingsScrim;
    [Tooltip("Stops: closed (0) and open.")]
    public BottomSheet settingsSheet;
    public Button settingsDone;
    [Tooltip("The blue dot first, then one cell per marker sprite.")]
    public Button[] markerCells;
    public Image[] markerBackgrounds;
    public Sprite[] markerSprites;
    [Tooltip("Auto, Light, Dark.")]
    public Button[] appearanceButtons;
    public Image[] appearanceBackgrounds;
    public Text[] appearanceLabels;
    public Button tiltToggle;
    public Image switchTrack;
    public Image switchKnob;
    [Tooltip("Pretend the phone is at the building's entrance, for demos away from it.")]
    public Button pretendToggle;
    public Text pretendTitle;
    public Text pretendDetail;
    public Image pretendTrack;
    public Image pretendKnob;

    [Tooltip("Spoken directions: read turns aloud and answer when spoken to.")]
    public Button voiceToggle;
    public Image voiceTrack;
    public Image voiceKnob;

    [Header("Voice")]
    public VoiceInput voiceInput;
    public VoiceOutput voiceOutput;
    [Tooltip("Talk to the app: lower left, over the map or the camera.")]
    public CanvasGroup micButton;
    public Image micBackground;
    public Text micIcon;
    [Tooltip("Swells with the voice while listening.")]
    public RectTransform micRing;

    [Header("Snackbar")]
    public CanvasGroup snackbar;
    public Text snackbarText;

    [Header("Launch")]
    [Tooltip("The logo and a spinner over everything while the app opens.")]
    public CanvasGroup launchScreen;
    public RectTransform launchLogo;
    [Tooltip("In the Start button while AR gets going.")]
    public Spinner beginSpinner;

    FloorPlan Plan => navigator.Plan;
}
