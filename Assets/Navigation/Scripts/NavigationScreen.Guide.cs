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

    // How sure guidance may be of the position. The Localizer converging with a wide spread can be metres (even tens
    // of metres) off: then "Turn right · 30 ft" and a bright, running ribbon would be confidently wrong.
    //   Sure     Localized, or converging and narrow: guidance as usual
    //   Unsure   converging and wide: the banner says it's finding the spot, no turn or distance, no turn buzz, the
    //            ribbon faint and still, the minimap's dot haloed
    //   TurnUnsure  sure enough across the corridor but not along it (AlongSpread: ARCore's scale error) with a turn
    //            within that much (plus TurnMargin) ahead, where it decides which corridor "Turn right" means: the
    //            banner says a turn is coming without the distance, no turn buzz, the ribbon as usual. Mid-corridor
    //            the along error doesn't matter and is ignored.
    //   Lost     the banner asks for a room number, no ribbon
    // Into Unsure (or TurnUnsure) past UnsureEnter and out under UnsureLeave, each only once it has held for
    // GuidanceHold, so the banner never flickers. Lost comes and goes at once (the Localizer's own state is steady).
    public enum Guidance { Sure, TurnUnsure, Unsure, Lost }
    const float TurnMargin = 3f;
    const string TurnComing = "Turn coming up · watch for the corridor";
    const float UnsureEnter = 2.5f;
    const float UnsureLeave = 2f;
    const float GuidanceHold = 1f;
    const float UnsureRibbon = 0.3f;
    const string IconFinding = "\ue1b7";   // location_searching
    const string IconLookAround = "\ue412"; // photo_camera
    Guidance m_Guidance;
    float m_GuidancePending = -1f;   // since when the other state has been called for

    // The guidance the position calls for, before the GuidanceHold delay: the rule itself, pure, so the stress
    // evaluator can apply exactly this. toTurn: metres to the next turn (infinity with none before the destination).
    // previous: the guidance shown now, for the hysteresis (each threshold is entered past UnsureEnter and left under
    // UnsureLeave).
    public static Guidance WantedGuidance(LocalizerState state, float spread, float alongSpread, float toTurn,
        Guidance previous)
    {
        if (state == LocalizerState.Lost)
            return Guidance.Lost;
        bool wide = state == LocalizerState.Converging
                    && (previous == Guidance.Unsure ? spread >= UnsureLeave : spread > UnsureEnter);
        if (wide)
            return Guidance.Unsure;
        bool nearTurn = toTurn <= alongSpread + TurnMargin;
        bool stretched = previous == Guidance.TurnUnsure ? alongSpread >= UnsureLeave : alongSpread > UnsureEnter;
        return nearTurn && stretched ? Guidance.TurnUnsure : Guidance.Sure;
    }

    Guidance JudgeGuidance(bool instant, float toTurn)
    {
        Guidance want = WantedGuidance(m_Indoor.state, m_Indoor.spread, m_Indoor.alongSpread, toTurn, m_Guidance);
        if (want == Guidance.Lost)
        {
            m_GuidancePending = -1f;
            return m_Guidance = Guidance.Lost;
        }
        if (want == m_Guidance)
            m_GuidancePending = -1f;
        else if (instant || m_Guidance == Guidance.Lost)
        {
            m_Guidance = want;
            m_GuidancePending = -1f;
        }
        else if (m_GuidancePending < 0f)
            m_GuidancePending = Now;
        else if (Now - m_GuidancePending >= GuidanceHold)
        {
            m_Guidance = want;
            m_GuidancePending = -1f;
        }
        return m_Guidance;
    }

    // The Start button: the user stands with their back to the door, phone up. While AR can't run it does what
    // can be done about that instead (Align).
    void Begin()
    {
        if (FixAR())
            return;
        // Standing at the door, back to it: that's where the position is, for the Localizer from now on too.
        SnapTo(m_From);
        navigator.Begin(m_From, m_Destination);
        if (navigator.Route == null)
            return;
        Haptics.Tick(40);
        m_AnnouncedRoute = null;
        m_EndConfirmUntil = 0f;
        m_BeginWhenTracking = false;
        m_HerePlace = null;   // the user is about to walk away from it
        m_HereDoor = null;
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
        m_Guidance = Guidance.Sure;
        m_GuidancePending = -1f;
        if (!m_StatusWired && statusButton != null)
        {
            m_StatusWired = true;
            statusButton.onClick.AddListener(Realign);
        }
        m_SpokenRoute = null;
        m_SaidOffRoute = m_SaidTrackingLost = false;
        m_GuideLead = $"Head toward {m_DestinationTitle}. ";
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
        if (!Application.isPlaying)
            m_Indoor = ReadIndoor();   // previews: no Update has read it
        Guidance was = m_Guidance;
        Maneuver step = navigator.NextStep(out float distance, out Maneuver then, out float gap);
        bool now = distance < NowDistance;
        bool arriving = step == Maneuver.Arrive;
        float toTurn = arriving || step == Maneuver.Straight ? float.PositiveInfinity : distance;
        Guidance guidance = JudgeGuidance(instant, toTurn);
        // Sure of where the user is: the ribbon, the ETA and the minimap as usual (TurnUnsure only hides the turn's
        // distance and its buzz).
        bool sure = guidance == Guidance.Sure || guidance == Guidance.TurnUnsure;
        bool turnSure = guidance == Guidance.Sure;

        if (navigator.ribbon != null)
            navigator.ribbon.SetGuidance(sure ? 1f : guidance == Guidance.Unsure ? UnsureRibbon : 0f, sure,
                instant || !Application.isPlaying);
        if (!sure)
        {
            // No turn, no distance: only what to do to be found again.
            bool lostSpot = guidance == Guidance.Lost;
            maneuverIcon.text = lostSpot ? IconLookAround : IconFinding;
            maneuverDistance.text = "";
            maneuverTitle.text = lostSpot ? "Point your camera at a room number" : "Finding your exact spot…";
            maneuverDetail.text = lostSpot ? m_TowardText : "Keep the camera up and keep walking";
            if (thenTab.gameObject.activeSelf)
                thenTab.gameObject.SetActive(false);
        }
        else if (!turnSure)
        {
            // Which corridor the turn is in isn't certain: the way it turns, not when.
            maneuverIcon.text = ManeuverIcon(step);
            maneuverDistance.text = "";
            maneuverTitle.text = TurnComing;
            maneuverDetail.text = m_TowardText;
            if (thenTab.gameObject.activeSelf)
                thenTab.gameObject.SetActive(false);
        }
        else
        {
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
        }

        if (!instant)
            SpeakGuide(step, distance, then, gap, arriving, guidance, was);

        // A buzz as each turn comes up, once, and only when sure of it.
        float at = navigator.Progress + distance;
        if (turnSure && now && !arriving && (route != m_AnnouncedRoute || Mathf.Abs(at - m_AnnouncedAt) > 2f))
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
        else if (navigator.IsOffRoute && turnSure)   // "head back to the arrows" only when sure it is off them
            SetStatus(OffRouteStatus, PlaceStyle.Waiting);
        else
            SetStatus(null, default);
        StyleRealign(realign, instant);

        // What's left, and the clock time you'll be there (checked each second, or as the distance changes). Not
        // while unsure: the distance is as unsure as the position.
        float remaining = navigator.Remaining;
        int feet = FeetRounded(remaining);
        float time = Time.unscaledTime;
        if (!sure)
        {
            if (was != guidance || instant)
            {
                etaText.text = "–";
                etaDetail.text = guidance == Guidance.Lost ? "Lost your spot · look for a room number"
                    : "Distance once you're found";
            }
            m_EtaFeet = -1;   // written again as soon as it's sure
        }
        bool moved = feet != m_EtaFeet;
        if (sure && (instant || moved || time >= m_NextClock))
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
        // Haloed as wide as the estimate is unsure, and coloured like the big map's dot.
        miniMap.Follow(route, navigator.Progress, navigator.UserPosition, navigator.UserHeading, instant,
            sure ? 0f : Mathf.Max(m_Indoor.spread, MinAccuracyShown));
        miniMap.TintPuck(sure ? Theme.Current.primary : guidance == Guidance.Lost ? Theme.Current.onSurfaceVariant
            : Theme.Current.warning, guidance == Guidance.Lost);
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
        // Under the Then tab when it hangs below the banner; a Then chip inside the banner leaves it under the banner.
        RectTransform above = thenTab.gameObject.activeSelf && !thenTab.IsChildOf(banner) ? thenTab : banner;
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
        m_GuideLead = "New route. ";   // said with the first step of it
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
        Speak($"You've arrived. {arrivedDetail.text}.");
    }
}
