using System;
using UnityEngine;

// Guide (walking) and Arrived.
public partial class NavigationScreen
{
    // The Start button: the user stands with their back to the door, phone up.
    void Begin()
    {
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

        if (!instant)
            SpeakGuide(step, distance, then, gap, arriving);

        // A buzz as each turn comes up, once.
        float at = navigator.Progress + distance;
        if (now && !arriving && (route != m_AnnouncedRoute || Mathf.Abs(at - m_AnnouncedAt) > 2f))
        {
            m_AnnouncedRoute = route;
            m_AnnouncedAt = at;
            if (!instant)
                Haptics.Tick(60);
        }

        if (!navigator.IsTracking)
            SetStatus(TrackingLostStatus, PlaceStyle.Bad);
        else if (navigator.OffRoute > Navigator.OffRouteWarning)
            SetStatus(OffRouteStatus, PlaceStyle.Waiting);
        else
            SetStatus(null, default);

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
