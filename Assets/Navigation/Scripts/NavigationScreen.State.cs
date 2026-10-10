using System.Collections.Generic;
using UnityEngine;

// NavigationScreen's constants and the state it keeps between frames.
public partial class NavigationScreen
{
    const string IconSearch = "";
    const string IconBack = "";
    const string IconWholeFloor = "";

    // Sheet heights, in canvas units.
    const float HomePeek = 232f;
    const float StartPeek = 196f;
    const float PlaceHeight = 560f;
    const float AlignHeight = 540f;
    const float SettingsHeight = 1276f;
    const float KnobTravel = 22f;

    // How far the maps lean back with the 3D map on, in degrees.
    const float MapTilt = 35f;
    const float MiniMapTilt = 45f;

    // Under this many metres, a turn or the destination is "now".
    const float NowDistance = 1.5f;
    // The step after next shows when it comes this soon after the next one.
    const float ThenDistance = 15f;
    // A route shorter than this: "You're already there".
    const float AlreadyThere = 2f;
    // A location fix further than this from every door isn't in the building (or is too far off to use).
    // Also the margin round the floor image when there's no campus map.
    const float MaxGpsDistance = 60f;
    // A fix that may be off by more than this doesn't pick the start: Directions waits for a better one.
    const float MaxStartAccuracy = 50f;
    // How long Directions shows it's finding the phone's location before it says it couldn't.
    const float FindingSeconds = 8f;
    // A fix this far outside the building's outline is outside, not just a little off: start at an entrance.
    const float OutsideMargin = 5f;
    // The accuracy disc never covers more than this much of the floor, nor less than this.
    const float MaxAccuracyShown = 25f;
    const float MinAccuracyShown = 2f;
    // At most this many "Near you" rows.
    const int NearYouRows = 4;
    // Holding the settings gear this long turns the debug overlay on or off.
    const float DebugHoldSeconds = 1.5f;
    // Seconds a snackbar stays before it fades.
    const float SnackbarSeconds = 2.8f;
    // Window for the second tap on End.
    const float EndConfirmSeconds = 3f;
    // Window for the second Back on Home, which leaves the app.
    const float ExitConfirmSeconds = 2f;
    // "Starting AR…" this long, then "Look around slowly…".
    const float StartingARSeconds = 4f;
    // While the appearance is Auto, how often the phone's light or dark setting is read again.
    const float SystemThemeSeconds = 2f;
    // Panels fade in at this much alpha a second; the floor button follows the map in and out as fast.
    const float FadeSpeed = 7f;
    // The launch screen stays at least this long, then fades over LaunchFade while the campus view comes down
    // from LaunchSpan times as wide.
    const float LaunchSeconds = 0.9f;
    const float LaunchFade = 0.4f;
    const float LaunchSpan = 2.2f;
    // Campus buildings added to the search list per frame while the app opens.
    const int BuildingRowsPerFrame = 20;

    const string MarkerKey = "marker";
    const string AppearanceKey = "appearance";
    const string Map3DKey = "map3d";
    const string PretendKey = "pretend";
    const string VoiceKey = "voice";

    // Voice. Turns are announced this far ahead (about 30 ft), and again at the turn.
    const float PrepareDistance = 9f;
    // A turn this far past the last one gets "continue straight for…" as the last one is passed.
    const float LongStraight = 20f;
    // The mic button: its size, and how far above the sheet or card under it (more on Home, over the map credit).
    const float MicSize = 112f;
    const float MicLift = 24f;
    const float MicLiftHome = 84f;
    const int AppearanceAuto = 0;
    const int AppearanceDark = 2;

    // Android's night bits in the UI mode (Configuration.UI_MODE_NIGHT_MASK and UI_MODE_NIGHT_YES).
    const int UiModeNightMask = 0x30;
    const int UiModeNightYes = 0x20;

    // The off-route warning names what the user sees in AR: a band of arrows on the floor.
    public const string OffRouteStatus = "Off the route · head back to the arrows";
    const string TrackingLostStatus = "Tracking lost · point the camera down the hallway";

    // The plan kind behind each chip, in chip order, and its words for the subtitle's count.
    static readonly string[] ChipKinds = { "restroom", "elevator", "stairs", "exit" };
    static readonly string[] KindSingular = { "restroom", "elevator", "stairway", "entrance" };
    static readonly string[] KindPlural = { "restrooms", "elevators", "stairways", "entrances" };

    // The directory's sections, and the start list's after "Near you".
    static readonly (string title, string[] kinds)[] Sections =
    {
        ("Rooms", new[] { "room" }),
        ("Restrooms", new[] { "restroom" }),
        ("Elevators and stairs", new[] { "elevator", "stairs" }),
        ("Entrances", new[] { "exit" }),
    };

    static readonly string[] RomanNumerals = { "I", "II", "III", "IV", "V" };

    Step m_Step = Step.Home;
    bool m_Initialized;
    bool m_Searching;
    string m_SavedQuery = "";      // the query to put back when coming back from a place
    string m_Kind;                 // the chip that's on
    PlanPlace m_Selected;          // the place on the place card
    PlanPlace m_Destination;       // where the route goes
    PlanDoor m_From;               // the door picked to start from
    CampusData.Building m_Building; // another campus building the directory sheet is about (null: the plan's)

    Route m_AnnouncedRoute;        // the last "now" buzz: on which route, and where along it
    float m_AnnouncedAt = float.NaN;
    float m_SnackbarHideTime;
    float m_EndConfirmUntil;
    float m_ExitConfirmUntil;
    float m_StartedTime;           // when Align opened
    float m_SafeHeight;
    int m_SettleFrames = 3;
    bool m_OnCampus;
    bool m_Indoors = true;
    float m_IndoorChrome = 1f;
    float m_ListTop = float.NaN;   // the directory list's top offset under the sheet's search box (FitList)
    bool m_Launching;
    float m_LaunchTime = -1f;
    List<CampusData.Building> m_BuildingList;
    int m_BuildingRows;
    string m_SpinnerFor;
    CanvasGroup[] m_Panels;
    Dictionary<GameObject, Layer> m_Layers;   // the parts shown and hidden by their canvases, by root
    float m_NextThemeCheck;

    int m_Marker;
    int m_Appearance;
    bool m_Map3D = true;
    bool m_Night;
    bool m_Pretend;
    bool m_SettingsOpen;
    bool m_FloorMenuOpen;

    bool m_ToldNoLocation;         // "Location is off" said once this run
    bool m_AutoStart;              // Directions may still pick the start from the phone's location
    float m_FindingSince;
    bool m_ToldWhy;                // said why it didn't pick, for this Directions
    string m_ShortName;            // "BA1"
    readonly List<(PlanPlace place, PlanDoor door)> m_Nearby = new List<(PlanPlace place, PlanDoor door)>();
    Vector2? m_NearbyAt;
    string m_FromTitle;            // the start door's name, for the origin row
    bool m_FromAuto;               // the start was picked from the phone's location: the origin row says how near

    // The conversation (NavigationScreen.Voice). Said when a room asked for isn't on the plan.
    const string NoRoomMessage = "Cannot find valid room in this building";
    IVoiceInterpreter m_Interpreter;
    bool m_Voice = true;           // spoken directions and answers (Settings)
    bool m_Conversing;             // the user has used the mic: the app talks them through lining up
    VoiceQuestion m_Question;      // what was last asked, so a bare answer means the right thing
    readonly List<(PlanPlace place, PlanDoor door)> m_Choices = new List<(PlanPlace place, PlanDoor door)>();
    bool m_ChoicesAreDestinations;
    string m_GoalKind;             // "the nearest restroom", waiting to know where the user is
    PlanPlace m_HerePlace;         // where the user said they are
    PlanDoor m_HereDoor;
    bool m_BeginWhenTracking;      // said "start" before AR was tracking
    int m_Retries;
    string m_LastSaid;             // for "repeat that"
    int m_MicStyle = -1;
    float m_MicTop = -1f;          // the mic button's top above the safe area's bottom, or -1 while it's hidden
    // What guidance last said: on which route, at which turn, and how much of it.
    Route m_SpokenRoute;
    float m_SpokenAt = float.NaN;
    bool m_SaidPrepare;
    bool m_SaidNow;
    bool m_SaidOffRoute;
    bool m_SaidTrackingLost;
    string m_GuideLead;            // said before the first step: "Head toward Room 107." or "New route."

    // What guidance last showed, so a frame that changes nothing builds no strings and dirties no layout.
    string m_DestinationTitle;
    string m_TowardText;
    string m_Side;
    string m_SideText;
    int m_EtaFeet = -1;
    int m_EtaClock = -1;
    float m_NextClock;
    float m_ShownProgress = -1f;
    float m_StatusY = float.NaN;
    int m_EndStyle = -1;

    static readonly Vector3[] s_Corners = new Vector3[4];
}
