using System;
using UnityEngine;
using UnityEngine.UI;

// Guide (walking) and Arrived.
public partial class NavigationScreen
{
    // When the plan may no longer match the hallway (tracking lost for a while, back from the background, or the
    // walls lost the user): the status pill says this, and tapping it re-aligns.
    const string RealignStatus = "Re-align at the nearest door";

    [Header("Guidance: re-align")]
    [Tooltip("On the status pill: re-aligns while it asks the user to.")]
    public Button statusButton;
    [Tooltip("The Re-align pill's background and label: in the accent colour while re-aligning is asked for.")]
    public Image realignBackground;
    public Text realignLabel;

    bool m_StatusWired;
    int m_RealignStyle = -1;

    // The Start button: the user stands with their back to the door, phone up. While AR can't run it does what
    // can be done about that instead (Align).
    void Begin()
    {
        if (FixAR())
            return;
        // Standing at the door, back to it: that's where the position is, for the Localizer from now on too.
        if (localizer != null && Application.isPlaying)
            localizer.SnapToDoor(m_From);
        navigator.Begin(m_From, m_Destination);
        if (navigator.Route == null)
            return;
        Haptics.Tick(40);
        m_AnnouncedRoute = null;
        m_EndConfirmUntil = 0f;
        ShowGuide();
    }

    void ShowGuide()
    {
        Show(Step.Guide);
        m_DestinationTitle = PlaceStyle.Title(m_Destination);
        m_TowardText = $"Toward {m_DestinationTitle}";
        m_Side = null;
        m_EtaFeet = -1;
        m_EtaClock = -1;
        m_ShownProgress = -1f;
        m_StatusY = float.NaN;
        m_EndStyle = -1;
        m_RealignStyle = -1;
        if (!m_StatusWired && statusButton != null)
        {
            m_StatusWired = true;
            statusButton.onClick.AddListener(Realign);
        }
        miniMap.Select(m_Destination);
        miniMap.ShowPin(miniMap.Anchor(m_Destination), null);
        UpdateGuide(true);
    }

    // Every frame on Guide. instant: the first frame, which may not buzz and must write everything.
    void UpdateGuide(bool instant)
    {
        if (!navigator.IsGuiding || navigator.Route == null)
            return;
        Route route = navigator.Route;
        Maneuver step = navigator.NextStep(out float distance, out Maneuver then, out float gap);
        bool now = distance < NowDistance;
        bool arriving = step == Maneuver.Arrive;

        maneuverIcon.text = ManeuverIcon(step);
        maneuverDistance.text = now ? "Now" : Feet(distance);
        if (arriving)
        {
            string side = navigator.DoorSide();
            if (side != m_Side)
            {
                m_Side = side;
                m_SideText = Capitalized(side);
            }
            maneuverTitle.text = m_DestinationTitle;
            maneuverDetail.text = m_SideText;
        }
        else
        {
            maneuverTitle.text = ManeuverText(step);
            maneuverDetail.text = m_TowardText;
        }
        bool showThen = !arriving && gap < ThenDistance;
        if (thenTab.gameObject.activeSelf != showThen)
            thenTab.gameObject.SetActive(showThen);
        if (showThen)
            thenIcon.text = ManeuverIcon(then);

        // A buzz as each turn comes up, once.
        float at = navigator.Progress + distance;
        if (now && !arriving && (route != m_AnnouncedRoute || Mathf.Abs(at - m_AnnouncedAt) > 2f))
        {
            m_AnnouncedRoute = route;
            m_AnnouncedAt = at;
            if (!instant)
                Haptics.Tick(60);
        }

        // Each with hysteresis in Navigator: a blink of lost tracking or a step across the warning line says nothing.
        // Indoors, what the Localizer needs comes first: lost, a room number (or a door: the pill takes the tap);
        // short of a view, the phone held up.
        bool lost = m_Indoor.askForSign;
        bool realign = navigator.ShouldRealign || lost;
        if (lost)
            SetStatus("Point at a room number, or tap to pick a door", PlaceStyle.Bad);
        else if (m_Indoor.needsView)
            SetStatus(NeedsViewMessage, PlaceStyle.Waiting);
        else if (realign)
            SetStatus(RealignStatus, PlaceStyle.Bad);
        else if (navigator.TrackingLost)
            SetStatus(TrackingLostStatus, PlaceStyle.Bad);
        else if (navigator.IsOffRoute)
            SetStatus(OffRouteStatus, PlaceStyle.Waiting);
        else
            SetStatus(null, default);
        StyleRealign(realign, instant);

        // What's left, and the clock time you'll be there (checked each second, or as the distance changes).
        float remaining = navigator.Remaining;
        int feet = FeetRounded(remaining);
        float time = Time.unscaledTime;
        bool moved = feet != m_EtaFeet;
        if (instant || moved || time >= m_NextClock)
        {
            m_NextClock = time + 1f;
            DateTime arrive = DateTime.Now.AddSeconds(remaining / Navigator.WalkingSpeed);
            int clock = arrive.Hour * 60 + arrive.Minute;
            if (instant || moved || clock != m_EtaClock)
            {
                m_EtaFeet = feet;
                m_EtaClock = clock;
                etaText.text = Minutes(remaining);
                etaDetail.text = $"{Feet(remaining)} · arrive {arrive:h:mm}";
            }
        }

        float walked = route.length > 0f ? Mathf.Clamp01(navigator.Progress / route.length) : 0f;
        if (instant || Mathf.Abs(walked - m_ShownProgress) > 0.002f)
        {
            m_ShownProgress = walked;
            progressFill.anchorMax = new Vector2(walked, 1f);
        }

        // Heading-up round the user; the walked part of the route is cut off.
        miniMap.Follow(route, navigator.Progress, navigator.UserPosition, navigator.UserHeading, instant);
        StyleEnd(false);
    }

    // The pill under the banner (or under the Then tab): a message and its colour, or null to hide it.
    void SetStatus(string message, Color color)
    {
        bool shown = message != null;
        if (statusPill.gameObject.activeSelf != shown)
        {
            statusPill.gameObject.SetActive(shown);
            m_StatusY = float.NaN;
        }
        if (!shown)
            return;
        statusText.text = message;
        statusDot.color = color;
        RectTransform above = thenTab.gameObject.activeSelf ? thenTab : banner;
        float y = -(BottomOf(above) + 20f);
        if (y != m_StatusY)
        {
            m_StatusY = y;
            statusPill.anchoredPosition = new Vector2(statusPill.anchoredPosition.x, y);
        }
    }

    // Asking to re-align: the pill takes taps, the Re-align button turns the accent colour, and one buzz. The
    // colours are checked every frame (a compare), since a day/night switch repaints the button its usual way.
    void StyleRealign(bool asking, bool instant)
    {
        Theme theme = Theme.Current;
        if (realignBackground != null)
        {
            Color background = asking ? theme.primary : theme.surfaceVariant;
            if (realignBackground.color != background)
                realignBackground.color = background;
        }
        if (realignLabel != null)
        {
            Color ink = asking ? theme.onPrimary : theme.onSurface;
            if (realignLabel.color != ink)
                realignLabel.color = ink;
        }
        int style = asking ? 1 : 0;
        if (style == m_RealignStyle)
            return;
        bool styled = m_RealignStyle >= 0;
        m_RealignStyle = style;
        if (statusButton != null)
            statusButton.interactable = asking;
        if (asking && styled && !instant)
            Haptics.Tick(60);
    }

    // Two taps, so a stray touch never ends the walk.
    void OnEndTapped()
    {
        if (Time.unscaledTime < m_EndConfirmUntil)
        {
            m_EndConfirmUntil = 0f;
            ResetHome(false);
            return;
        }
        m_EndConfirmUntil = Time.unscaledTime + EndConfirmSeconds;
        StyleEnd(true);
        Haptics.Tick();
    }

    // "End?" in red while a second tap ends the walk, else "End".
    void StyleEnd(bool force)
    {
        int style = Time.unscaledTime < m_EndConfirmUntil ? 1 : 0;
        if (style == m_EndStyle && !force)
            return;
        m_EndStyle = style;
        Theme theme = Theme.Current;
        endBackground.color = style == 1 ? theme.error : theme.surfaceVariant;
        endLabel.color = style == 1 ? Color.white : theme.onSurface;
        endLabel.text = style == 1 ? "End?" : "End";
    }

    // Starts again from a door, for when the arrows no longer match the hallway.
    void Realign()
    {
        navigator.Stop();
        ShowStart(m_Destination, false);
        Snack("Go to the nearest door and pick it");
    }

    void OnRerouted()
    {
        Snack("New route from here");
        Haptics.Tick(40);
    }

    void OnArrived()
    {
        PlanPlace destination = m_Destination;
        arrivedTitle.text = "You've arrived";
        arrivedDetail.text = $"{PlaceStyle.Title(destination)} is {navigator.DoorSide()}";
        Vector2 at = navigator.UserPosition;
        float heading = navigator.UserHeading;
        if (Application.isPlaying)
            navigator.Stop();   // the walk is over, and AR goes off with the next Show
        Show(Step.Arrived);
        // With the camera off, the map is behind the card: the place, and the user where the walk ended.
        map.Select(destination);
        map.ShowPin(map.Anchor(destination), PlaceStyle.Title(destination));
        map.ShowRoute(null);
        map.ShowPuck(at, heading, 0f, true);
        SetInsets(24f, TopOf(arrivedCard) + 8f);
        map.ShowPlace(destination, !Application.isPlaying);
        Haptics.Tick(150);
    }
}
