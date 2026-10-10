using System.Linq;
using UnityEngine;

// For the editor's preview renders (BuildNavigateScene.RenderPreviews): each hook puts one screen up, still.
public partial class NavigationScreen
{
    // Home with the directory sheet at a stop, optionally searching or with a chip on.
    public void PreviewHome(int stop, string search, string kind)
    {
        PreviewBegin();
        UpdateStops();
        ResetHome(false);
        homeSheet.MoveTo(stop, true);
        if (kind != null)
            SetKind(kind, true);
        if (!string.IsNullOrEmpty(search))
        {
            AddBuildingRows(int.MaxValue);
            searchField.SetTextWithoutNotify(search);
            homeList.Filter(search);
            SetSearching(true);
        }
        UpdateIndoors(true);
        UpdateHomeChrome();
    }

    // Home out over the campus, under the launch screen or not.
    public void PreviewCampus(bool launch)
    {
        PreviewBegin();
        ResetHome(true);
        if (launchScreen != null)
        {
            launchScreen.gameObject.SetActive(launch);
            launchScreen.alpha = 1f;
            if (launchLogo != null)
                launchLogo.localScale = Vector3.one;
        }
    }

    // Zooming in from the campus to the floor's building, span metres across it: its roof sinking away and the
    // floor coming in.
    public void PreviewApproach(float span)
    {
        PreviewCampus(false);
        CampusData.Building building = map.Campus?.indoor;
        if (building == null)
            return;
        HomeInsets(homeSheet.Stop);
        map.FramePoints(new[] { building.bounds.center }, 0f, span, true);
        UpdateIndoors(true);
        UpdateHomeChrome();
    }

    // A campus building tapped, by its exact name.
    public void PreviewBuilding(string name)
    {
        PreviewCampus(false);
        CampusData.Building building = map.Campus?.buildings.FirstOrDefault(b => b.name == name);
        if (building != null)
            OnBuildingTapped(building);
        else
            Debug.LogWarning($"[Navigation] No campus building named {name}.");
    }

    public void PreviewFloorMenu()
    {
        PreviewHome(0, null, null);
        SetFloorMenu(true);
    }

    public void PreviewSettings()
    {
        PreviewHome(0, null, null);
        OpenSettings();
    }

    public void PreviewPlace(PlanPlace place)
    {
        PreviewBegin();
        ResetHome(false);
        ShowPlace(place);
    }

    public void PreviewStart(PlanPlace destination)
    {
        PreviewBegin();
        ResetHome(false);
        ShowStart(destination, false);
    }

    // Lining up at a door picked by hand, with AR tracking or still starting.
    public void PreviewAlign(PlanPlace destination, PlanPlace from, PlanDoor door, bool tracking)
    {
        PreviewBegin();
        ShowStart(destination, false);
        ShowAlign(from, door, false, null);
        PreviewTracking(tracking);
    }

    // Directions from the place card, the start picked from the phone's location (see PreviewLocation).
    public void PreviewDirections(PlanPlace destination, bool tracking)
    {
        PreviewBegin();
        ShowPlace(destination);
        ShowStart(destination, true);
        PreviewTracking(tracking);
    }

    void PreviewTracking(bool tracking)
    {
        SetInteractable(beginButton, tracking);
        beginLabel.text = tracking ? "Start" : "Starting AR…";
        ShowBeginSpinner(!tracking);
        if (beginSpinner != null)
            beginSpinner.SetTime(0.45f);   // a still frame
    }

    // Directions still finding the phone's location: the fix is taken away for the picture, then put back.
    public void PreviewFinding(PlanPlace destination)
    {
        PreviewBegin();
        if (location == null)
        {
            PreviewStart(destination);
            return;
        }
        bool hadFix = location.HasFix;
        Vector2 at = location.Position;
        float accuracy = location.Accuracy;
        location.SetPreview(null, 0f);
        ShowStart(destination, true);
        foreach (Spinner spinner in findingRow.GetComponentsInChildren<Spinner>(true))
            spinner.SetTime(0.45f);
        m_AutoStart = false;   // the fix coming back mustn't pick the start under the picture
        location.SetPreview(hadFix ? at : (Vector2?)null, accuracy);
    }

    // A location fix at a plan point.
    public void PreviewLocation(Vector2? at, float accuracy)
    {
        PreviewBegin();
        if (location == null)
            return;
        location.SetPreview(at, accuracy);
        UpdateGpsPuck();
    }

    public void PreviewMarker(int marker)
    {
        PreviewBegin();
        SetMarker(marker, false);
    }

    // Auto, Light or Dark (0, 1, 2).
    public void PreviewAppearance(int mode)
    {
        PreviewBegin();
        m_Appearance = mode;
        ApplyAppearance(true);
    }

    // Walking, after navigator.Preview has put the user somewhere along the route.
    public void PreviewGuide(PlanPlace destination, string status)
    {
        PreviewBegin();
        m_Destination = m_Selected = destination;
        m_AnnouncedRoute = null;
        m_EndConfirmUntil = 0f;
        ShowGuide();
        // No AR session in the editor, so the tracking warning would always show.
        SetStatus(status, PlaceStyle.Waiting);
    }

    public void PreviewArrived(PlanPlace destination)
    {
        PreviewBegin();
        m_Destination = m_Selected = destination;
        OnArrived();
    }

    // null hides it.
    public void PreviewSnackbar(string message)
    {
        PreviewBegin();
        if (message == null)
            snackbar.alpha = 0f;
        else
            Snack(message);
    }

    // Every hook starts here: the screen set up once, and the sheets fitted to the preview's safe area.
    // Indoors, the Localizer's estimate made up: on the floor's map with the dot at a plan point, coloured and
    // haloed for the state, and the banner that state asks for.
    public void PreviewIndoor(LocalizerState state, Vector2 at, float heading, float spread, bool needsView)
    {
        PreviewBegin();
        m_PreviewingIndoor = true;
        m_PreviewIndoor = new Indoor
        {
            state = state,
            position = at,
            heading = heading,
            spread = spread,
            needsView = needsView,
            askForSign = state == LocalizerState.Lost,
        };
        m_Indoor = m_PreviewIndoor;
        ResetHome(false);
        HomeInsets(homeSheet.Stop);
        map.FramePoints(new[] { at }, 0f, 36f, true);
        UpdateGpsPuck();
        map.TintPuck(PuckColor(), state == LocalizerState.Lost);
        UpdateBanner();
    }

    // Walking with the Localizer unsure (converging, this wide) or lost: after navigator.Preview, like PreviewGuide.
    public void PreviewGuideIndoor(PlanPlace destination, LocalizerState state, float spread)
    {
        PreviewBegin();
        m_PreviewingIndoor = true;
        m_PreviewIndoor = new Indoor
        {
            state = state,
            position = navigator.UserPosition,
            heading = navigator.UserHeading,
            spread = spread,
            askForSign = state == LocalizerState.Lost,
        };
        m_Indoor = m_PreviewIndoor;
        m_Destination = m_Selected = destination;
        m_AnnouncedRoute = null;
        m_EndConfirmUntil = 0f;
        ShowGuide();
    }

    // The snap card the Localizer offers coming in through an entrance (the phone still outdoors).
    public void PreviewEntrance(PlanPlace entrance)
    {
        PreviewBegin();
        ResetHome(false);
        if (entrance != null && entrance.doors.Count > 0)
            OpenSnapCard(entrance, entrance.doors[0], true, true);
    }

    // The debug overlay over the floor's map, a cloud of particles round the estimate, the door chips open.
    public void PreviewDebug(Vector2 at, float heading, float spread)
    {
        PreviewIndoor(LocalizerState.Converging, at, heading, spread, false);
        if (debugOverlay != null)
            debugOverlay.Preview(LocalizerState.Converging, at, spread, 0.64f, true);
    }

    // Every hook starts here: the screen set up once, the sheets fitted to the preview's safe area, and anything an
    // indoor preview left (the made-up estimate, the snap card, the debug overlay) gone.
    void PreviewBegin()
    {
        Initialize();
        if (Mathf.Abs(safeArea.rect.height - m_SafeHeight) > 1f)
            UpdateStops();
        m_PreviewingIndoor = false;
        m_Indoor = default;
        CloseSnapCard(false);
        if (debugOverlay != null)
            debugOverlay.Show(false);
        if (positionBanner != null)
            positionBanner.gameObject.SetActive(false);
        map.TintPuck(Theme.Current.primary, false);
    }
}
