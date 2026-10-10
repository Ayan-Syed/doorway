using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Voice: the mic button, a conversation that does what taps do, and spoken guidance while walking.
//
// What the user says (VoiceInput) is read by an interpreter (VoiceParser, or the fine-tuned model) into where
// they are, where they want to go, or a command. Those become the same steps a tap makes: ShowStart for a
// destination, ShowAlign for the door they stand at, Begin for "start". Where the user is comes from what they
// name, or describe: a second landmark ("by the stairs") or the phone's location tells apart places that share a
// description, and when nothing does, the app asks which one. A destination given as a kind ("the nearest
// restroom") is the one the shortest walk away.
// After a question the app listens again by itself, once it has finished speaking, so a whole route can be set
// up hands-free: "take me to 107" / "where are you?" / "the north entrance" / "...say start" / "start".
// Spoken directions (a setting, on by default) read out each turn as it comes up and at the turn, arriving,
// a new route, and being off the route.
public partial class NavigationScreen
{
    void InitializeVoice()
    {
        m_Interpreter = new VoiceParser(Plan);
        bool available = voiceInput != null && (!Application.isPlaying || voiceInput.Available);
        if (micButton != null)
        {
            micButton.gameObject.SetActive(available);
            micButton.GetComponent<Button>().onClick.AddListener(OnMic);
        }
        if (voiceInput != null)
        {
            voiceInput.Heard += OnHeard;
            voiceInput.Partial += OnPartial;
            voiceInput.Failed += OnHeardNothing;
        }
        if (voiceToggle != null)
            voiceToggle.onClick.AddListener(() =>
            {
                SetVoice(!m_Voice, true);
                if (!m_Voice && voiceOutput != null)
                    voiceOutput.Stop();
            });
    }

    void ReleaseVoice()
    {
        if (voiceInput == null)
            return;
        voiceInput.Heard -= OnHeard;
        voiceInput.Partial -= OnPartial;
        voiceInput.Failed -= OnHeardNothing;
    }

    // Back to nothing asked and nothing known: a new start from Home.
    void ResetVoice()
    {
        m_Question = null;
        m_Choices.Clear();
        m_GoalKind = null;
        m_HerePlace = null;
        m_HereDoor = null;
        m_BeginWhenTracking = false;
        m_ListenAfterSpeech = false;
        m_Retries = 0;
    }

    void OnMic()
    {
        if (voiceInput == null)
            return;
        Haptics.Tick(15);
        if (voiceInput.IsListening)
        {
            voiceInput.Stop();   // done talking: use what was heard
            return;
        }
        m_Conversing = true;
        m_ListenAfterSpeech = false;
        if (voiceOutput != null)
            voiceOutput.Stop();
        Listen();
    }

    void Listen()
    {
        snackbarText.text = m_Question != null ? m_Question.text : "Listening…";
        snackbar.alpha = 1f;
        PlaceSnackbar();
        voiceInput.Listen();
    }

    void OnPartial(string words)
    {
        snackbarText.text = $"“{words}”";
    }

    // The first alternative that makes sense, else the best one.
    void OnHeard(IReadOnlyList<string> alternatives)
    {
        string said = alternatives[0];
        VoiceIntent intent = null;
        foreach (string alternative in alternatives)
        {
            VoiceIntent candidate = m_Interpreter.Interpret(alternative, m_Question);
            if (candidate.Understood)
            {
                intent = candidate;
                said = alternative;
                break;
            }
        }
        if (intent == null)
            intent = m_Interpreter.Interpret(said, m_Question);
        Snack($"“{said}”");
        Handle(intent);
    }

    // Nothing heard: ask again once if there was a question, else just say why.
    void OnHeardNothing(string why)
    {
        if (m_Question != null && m_Retries < 1)
        {
            m_Retries++;
            Ask(m_Question.ask, $"{why}. {m_Question.text}", m_Question.options);
            return;
        }
        m_Retries = 0;
        m_Question = null;
        Snack(why);
    }

    void Handle(VoiceIntent intent)
    {
        VoiceQuestion question = m_Question;
        m_Question = null;

        // "Which one?" answered: by its place in the list, or by naming one of them.
        if (question?.options != null)
        {
            int pick = intent.choice;
            if (pick < 0)
                pick = m_Choices.FindIndex(c => c.place.id == intent.here || c.place.id == intent.destination);
            if (pick >= 0)
            {
                var (place, door) = m_Choices[pick];
                bool destination = m_ChoicesAreDestinations;
                m_Choices.Clear();
                m_Retries = 0;
                if (destination)
                    GoTo(place);
                else if (Locate(place.id, null, door?.label))
                    AfterLocated();
                return;
            }
        }
        m_Retries = 0;

        // Walking somewhere new: start from the door nearest where the user is now.
        if (m_Step == Step.Guide && intent.here == null && intent.destination != null)
        {
            var nearest = DoorsNear(navigator.UserPosition);
            if (nearest.Count > 0)
            {
                m_HerePlace = nearest[0].place;
                m_HereDoor = nearest[0].door;
            }
        }

        if (intent.here != null && !Locate(intent.here, intent.near, intent.door))
            return;   // it asked which one, or said it doesn't know the place

        if (intent.destination != null)
        {
            PlanPlace goal = Destination(intent.destination);
            if (goal != null)
                GoTo(goal);
            else if (VoiceKinds.IsKind(intent.destination))
            {
                m_GoalKind = intent.destination;
                Ask(VoiceAsk.Location, $"Sure. To find the nearest {VoiceKinds.Spoken(intent.destination)}, " +
                                       "where are you now? Tell me a room number or something you can see.");
            }
            return;
        }
        if (intent.here != null)
        {
            AfterLocated();
            return;
        }
        Act(intent.action, question);
    }

    // The user said where they are; m_HerePlace and m_HereDoor hold it. On to the destination, or ask for one.
    void AfterLocated()
    {
        if (m_GoalKind != null)
        {
            PlanPlace goal = Destination(m_GoalKind);
            m_GoalKind = null;
            if (goal != null)
            {
                GoTo(goal);
                return;
            }
        }
        if (m_Destination != null && m_Step >= Step.Start)
        {
            GoTo(m_Destination);
            return;
        }
        Ask(VoiceAsk.Destination,
            $"Got it, you're at {PlaceStyle.DoorTitle(m_HerePlace, m_HereDoor)}. Where do you want to go?");
    }

    // Directions to goal: from the door the user named, or Directions' usual start (the phone's location, or ask).
    void GoTo(PlanPlace goal)
    {
        if (m_HerePlace != null)
        {
            Route route = m_HerePlace == goal ? null : navigator.PlanRoute(m_HereDoor, goal);
            if (m_HerePlace == goal || route != null && route.length < AlreadyThere)
            {
                Reply($"You're already at {PlaceStyle.Title(goal)}.");
                return;
            }
            if (route == null)
            {
                Reply($"I couldn't find a way from {PlaceStyle.Title(m_HerePlace)} to {PlaceStyle.Title(goal)}.");
                return;
            }
            ShowStart(goal, false);
            ShowAlign(m_HerePlace, m_HereDoor, false, null);   // OnVoiceAlign says what to do
            return;
        }
        ShowStart(goal, true);
        if (m_Step == Step.Start)   // the phone's location didn't pick the start (yet)
            Ask(VoiceAsk.Location, $"Okay, {PlaceStyle.Title(goal)}. Where are you now? " +
                                   "Tell me a room number or something you can see.");
    }

    // ShowAlign calls this: on the line-up step, say where to stand and listen for "start".
    void OnVoiceAlign(PlanPlace place, PlanDoor door, bool auto)
    {
        if (!m_Conversing || !Application.isPlaying)
            return;
        if (voiceInput != null && voiceInput.IsListening)
            voiceInput.Cancel();   // the phone's location answered first
        m_HerePlace = place;
        m_HereDoor = door;
        string from = PlaceStyle.DoorTitle(place, door);
        string lead = auto ? $"You look to be near {from}. " : $"Starting from {from}. ";
        Ask(VoiceAsk.Start, lead + PlaceStyle.StandHint(place.kind) + " Then hold your phone up and say start.");
    }

    // Sets m_HerePlace and m_HereDoor from what the user said, or asks which one (and returns false).
    bool Locate(string id, string nearId, string doorWord)
    {
        List<PlanPlace> candidates = Candidates(id);
        if (candidates.Count == 0)
        {
            Reply("Sorry, I don't know that place.");
            return false;
        }
        List<PlanPlace> near = nearId == null ? null : Candidates(nearId);
        bool gps = GpsUsable();

        PlanPlace place = candidates[0];
        if (candidates.Count > 1)
        {
            if (near != null && near.Count > 0)
                place = Closest(candidates, p => Distance(p, near));
            else if (gps)
                place = Closest(candidates, p => Distance(p, location.Position));
            else
            {
                AskWhich(candidates, false, id);
                return false;
            }
        }

        PlanDoor door = place.doors[0];
        if (place.doors.Count > 1)
        {
            PlanDoor named = doorWord == null ? null : place.doors.Find(d =>
                (d.label ?? "").ToLowerInvariant().StartsWith(doorWord.ToLowerInvariant()));
            if (named != null)
                door = named;
            else if (near != null && near.Count > 0)
            {
                var self = new List<PlanPlace> { place };
                door = ClosestDoor(place, Position(Closest(near, n => Distance(n, self))));
            }
            else if (gps)
                door = ClosestDoor(place, location.Position);
            else
            {
                m_Choices.Clear();
                foreach (PlanDoor d in place.doors)
                    m_Choices.Add((place, d));
                m_ChoicesAreDestinations = false;
                string[] labels = m_Choices.ConvertAll(c => c.door.label).ToArray();
                Ask(VoiceAsk.Location, $"Which door of {PlaceStyle.Title(place)}: {Spoken(labels)}?", labels);
                return false;
            }
        }
        m_HerePlace = place;
        m_HereDoor = door;
        return true;
    }

    // "Which one are you at: Atrium wing by 123, or Lobby by the elevators?" More than three: an open question.
    void AskWhich(List<PlanPlace> places, bool destinations, string kind)
    {
        string what = VoiceKinds.IsKind(kind) ? VoiceKinds.Spoken(kind) : "one";
        if (places.Count > 3)
        {
            Ask(destinations ? VoiceAsk.Destination : VoiceAsk.Location,
                $"Which {what}? Tell me a room number or sign near it.");
            return;
        }
        m_Choices.Clear();
        foreach (PlanPlace p in places)
            m_Choices.Add((p, null));
        m_ChoicesAreDestinations = destinations;
        // Places of one kind share a title, so the detail tells them apart.
        string[] options = places.ConvertAll(p => string.IsNullOrEmpty(p.detail)
            ? PlaceStyle.Title(p) : p.detail.Replace(",", "")).ToArray();
        Ask(destinations ? VoiceAsk.Destination : VoiceAsk.Location,
            $"Which {what} {(destinations ? "do you want" : "are you at")}: {Spoken(options)}?", options);
    }

    // A destination id as a place; a kind as the one the shortest walk (or, without a start, the shortest
    // distance) away. Null for a kind when there's nothing to measure from.
    PlanPlace Destination(string id)
    {
        if (!VoiceKinds.IsKind(id))
            return Plan.places.Find(p => p.id == id);
        List<PlanPlace> candidates = Candidates(id);
        if (candidates.Count == 0)
            return null;
        if (m_HerePlace != null)
        {
            PlanPlace best = null;
            float shortest = float.PositiveInfinity;
            foreach (PlanPlace p in candidates)
            {
                Route route = p == m_HerePlace ? null : navigator.PlanRoute(m_HereDoor, p);
                if (route != null && route.length < shortest)
                {
                    shortest = route.length;
                    best = p;
                }
            }
            return best;
        }
        if (GpsUsable())
            return Closest(candidates, p => Distance(p, location.Position));
        return null;
    }

    void Act(VoiceAction action, VoiceQuestion question)
    {
        switch (action)
        {
            case VoiceAction.Start:
            case VoiceAction.Yes:
                if (m_Step == Step.Align)
                {
                    if (navigator.IsTracking)
                        Begin();
                    else
                    {
                        m_BeginWhenTracking = true;
                        Reply("One moment, I'm still finding the floor. Move the phone around slowly.");
                    }
                }
                else if (m_Step == Step.Guide)
                    Reply(StepSentence());
                else
                    Help();
                break;
            case VoiceAction.No:
                if (m_Step == Step.Align)
                    Ask(VoiceAsk.Location, "Okay. Where are you, then?");
                else
                    Reply("Okay.");
                break;
            case VoiceAction.Repeat:
                Reply(m_LastSaid ?? "I haven't said anything yet.");
                break;
            case VoiceAction.NextStep:
                if (m_Step == Step.Guide)
                    Reply(StepSentence());
                else if (m_Step == Step.Align && m_HerePlace != null)
                    OnVoiceAlign(m_HerePlace, m_HereDoor, false);
                else
                    Help();
                break;
            case VoiceAction.HowFar:
                if (m_Step == Step.Guide)
                    Reply($"{Minutes(navigator.Remaining)} to go, about {SpokenFeet(navigator.Remaining)}.");
                else if (m_Step == Step.Align && m_From != null && m_Destination != null)
                {
                    Route route = navigator.PlanRoute(m_From, m_Destination);
                    if (route != null)
                        Reply($"It's {Minutes(route.length)}, about {SpokenFeet(route.length)}.");
                }
                else
                    Ask(VoiceAsk.Destination, "Where do you want to go?");
                break;
            case VoiceAction.WhereAmI:
                WhereAmI();
                break;
            case VoiceAction.Lost:
                if (m_Step == Step.Guide)
                {
                    Realign();
                    Ask(VoiceAsk.Location,
                        "Okay, let's line up again. Go to the nearest door and tell me its room number.");
                }
                else
                    Ask(VoiceAsk.Location, "Tell me a room number or sign you can see, and I'll find you.");
                break;
            case VoiceAction.End:
                if (m_Step >= Step.Start && m_Step != Step.Arrived)
                {
                    ResetHome(false);
                    Reply("Okay, stopped.");
                }
                else
                    Reply("Okay.");
                break;
            case VoiceAction.Mute:
                Reply("Okay, I'll be quiet.");
                SetVoice(false, true);
                break;
            case VoiceAction.Unmute:
                SetVoice(true, true);
                Reply("Voice directions are on.");
                break;
            case VoiceAction.Help:
                Help();
                break;
            default:
                if (question != null && m_Retries < 1)
                {
                    m_Retries++;
                    Ask(question.ask, "Sorry, I didn't get that. " + question.text, question.options);
                }
                else
                    Reply($"Sorry, I can only help you find your way around {m_ShortName}. " +
                          $"Try: take me to {ExampleRoom()}.");
                break;
        }
    }

    void WhereAmI()
    {
        Vector2? at = m_Step == Step.Guide ? navigator.UserPosition : GpsUsable() ? location.Position : (Vector2?)null;
        if (at == null)
        {
            Ask(VoiceAsk.Location, "I can't tell where you are yet. Tell me a room number or sign you can see.");
            return;
        }
        bool outside = m_Step != Step.Guide && Outside(at.Value, location.Accuracy);   // off Guide, at is the GPS fix
        PlanPlace nearest = null;
        float best = float.PositiveInfinity;
        foreach (PlanPlace p in Plan.places)
        {
            if (Inside(p) || outside && p.kind != "exit")
                continue;
            float d = Distance(p, at.Value);
            if (d < best)
            {
                best = d;
                nearest = p;
            }
        }
        if (nearest == null)
            return;
        string detail = string.IsNullOrEmpty(nearest.detail) || nearest.kind == "room" ? "" : $", {nearest.detail}";
        Reply(outside
            ? $"You're outside {m_ShortName}, nearest the {PlaceStyle.Title(nearest)}."
            : $"You're near {PlaceStyle.Title(nearest)}{detail}.");
    }

    void Help()
    {
        PlanPlace exit = Plan.places.Find(p => p.kind == "exit");
        Reply($"You can say things like: take me to {ExampleRoom()}. " +
              (exit != null ? $"I'm at the {PlaceStyle.Title(exit)}. " : "") +
              "Where's the nearest restroom? Or, while walking: what's next, or how far.");
    }

    string ExampleRoom()
    {
        PlanPlace room = Plan.places.Find(p => p.kind == "room" && !Inside(p));
        return room != null ? PlaceStyle.Title(room) : "a room";
    }

    // --- Speaking ------------------------------------------------------------------------------------------

    // An answer: shown, and spoken unless the voice is off.
    void Reply(string text)
    {
        m_LastSaid = text;
        Snack(text);
        if (m_Voice && voiceOutput != null)
            voiceOutput.Say(text);
    }

    // A question: answered, then the microphone opens once it's said (when the user has been talking).
    void Ask(VoiceAsk ask, string text, string[] options = null)
    {
        m_Question = new VoiceQuestion { ask = ask, text = text, options = options };
        Reply(text);
        m_ListenAfterSpeech = m_Conversing && voiceInput != null;
    }

    // Guidance: spoken only, the banner shows it. Never over the user talking.
    void Speak(string text)
    {
        if (!m_Voice || voiceOutput == null || !Application.isPlaying)
            return;
        m_LastSaid = text;
        if (voiceInput == null || !voiceInput.IsListening)
            voiceOutput.Say(text);
    }

    // Every frame: the mic button, the listening snackbar, and listening again after a question.
    void UpdateVoice()
    {
        if (voiceInput == null || micButton == null)
            return;
        bool listening = voiceInput.IsListening;
        if (listening)
        {
            snackbar.alpha = 1f;
            m_SnackbarHideTime = Time.unscaledTime + 0.6f;
        }
        else if (m_ListenAfterSpeech && (voiceOutput == null || !voiceOutput.IsSpeaking))
        {
            m_ListenAfterSpeech = false;
            Listen();
            listening = true;
        }
        if (m_Step == Step.Align && m_BeginWhenTracking && navigator.IsTracking)
        {
            m_BeginWhenTracking = false;
            Begin();
        }
        PlaceMic();
        StyleMic(listening);
        if (micRing != null)
        {
            float target = listening ? 1.12f + 0.4f * voiceInput.Level : 1f;
            float scale = Mathf.MoveTowards(micRing.localScale.x, target, Time.unscaledDeltaTime * 3f);
            if (scale != micRing.localScale.x)
                micRing.localScale = new Vector3(scale, scale, 1f);
        }
    }

    // Lower left, just above whatever is along the bottom (and the position banner); out of the way while a sheet
    // is pulled up or Settings is open. On Home it sits over the map credit.
    void PlaceMic()
    {
        float bottom = BottomUI();
        bool shown = micButton.gameObject.activeSelf && !m_SettingsOpen && !m_FloorMenuOpen &&
                     bottom < safeArea.rect.height * 0.6f;
        float y = bottom + (m_Step == Step.Home ? MicLiftHome : MicLift) + BannerTaken;
        SetAlpha(micButton, shown ? 1f : 0f);
        m_MicTop = shown ? y + MicSize : -1f;
        var rect = (RectTransform)micButton.transform;
        if (rect.anchoredPosition.y != y)
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, y);
    }

    void StyleMic(bool listening)
    {
        int style = listening ? 1 : 0;
        if (style == m_MicStyle)
            return;
        m_MicStyle = style;
        Theme theme = Theme.Current;
        micBackground.color = listening ? theme.primary : theme.surface;
        micIcon.color = listening ? theme.onPrimary : theme.primary;
    }

    // --- Guidance ------------------------------------------------------------------------------------------

    // From UpdateGuide, with what it worked out: each turn in PrepareDistance and at the turn, a long straight
    // after a turn, the destination coming up, and leaving the route or losing tracking. Like the banner and the
    // buzz, no turn is said unless the position is sure; unsure or lost, it says what the banner asks instead.
    void SpeakGuide(Maneuver step, float distance, Maneuver then, float gap, bool arriving, Guidance guidance,
        Guidance was)
    {
        if (!m_Voice || voiceOutput == null || !Application.isPlaying)
            return;

        if (guidance != was)
        {
            if (guidance == Guidance.Lost)
                Speak("I've lost your spot. Point your camera at a room number.");
            else if (guidance == Guidance.Unsure)
                Speak("Finding your exact spot. Keep the camera up and keep walking.");
            else
                m_SpokenRoute = null;   // sure again: say the next step afresh
        }
        if (guidance != Guidance.Sure)
            return;

        // The navigator's own signals, with its hysteresis: a blink of either says nothing.
        bool off = navigator.IsOffRoute;
        if (off && !m_SaidOffRoute)
            Speak("You're off the route. Head back to the arrows.");
        m_SaidOffRoute = off;
        bool lost = navigator.TrackingLost;
        if (lost && !m_SaidTrackingLost)
            Speak("Tracking lost. Point the camera down the hallway.");
        m_SaidTrackingLost = lost;
        if (off || lost)
            return;

        Route route = navigator.Route;
        float at = navigator.Progress + distance;
        if (route != m_SpokenRoute || Mathf.Abs(at - m_SpokenAt) > 2f)
        {
            bool first = m_GuideLead != null;
            m_SpokenRoute = route;
            m_SpokenAt = at;
            m_SaidPrepare = distance <= PrepareDistance && first;
            m_SaidNow = distance < NowDistance && first;
            if (first)
            {
                Speak(m_GuideLead + StepSentence());
                m_GuideLead = null;
                return;
            }
            if (!arriving && distance > LongStraight)
                Speak($"Continue straight for {SpokenFeet(distance)}.");
        }

        if (arriving)
        {
            if (!m_SaidPrepare && distance <= PrepareDistance)
            {
                m_SaidPrepare = true;
                Speak($"{m_DestinationTitle} is coming up, {navigator.DoorSide()}.");
            }
            return;
        }
        if (!m_SaidNow && distance < NowDistance)
        {
            m_SaidNow = m_SaidPrepare = true;
            string next = gap < ThenDistance ? ", then " + (then == Maneuver.Arrive
                ? $"{m_DestinationTitle} is {navigator.DoorSide()}" : Lower(ManeuverText(then))) : "";
            Speak($"{ManeuverText(step)} now{next}.");
        }
        else if (!m_SaidPrepare && distance <= PrepareDistance)
        {
            m_SaidPrepare = true;
            Speak($"In {SpokenFeet(distance)}, {Lower(ManeuverText(step))}.");
        }
    }

    // The next thing to do, as a sentence.
    string StepSentence()
    {
        Maneuver step = navigator.NextStep(out float distance, out _, out _);
        if (step == Maneuver.Arrive)
            return distance < NowDistance
                ? $"{m_DestinationTitle} is {navigator.DoorSide()}."
                : $"Go {SpokenFeet(distance)}, and {m_DestinationTitle} will be {navigator.DoorSide()}.";
        return distance < NowDistance
            ? $"{ManeuverText(step)} now."
            : $"In {SpokenFeet(distance)}, {Lower(ManeuverText(step))}.";
    }

    // --- Places --------------------------------------------------------------------------------------------

    // An id's place, or every place of a kind someone could be standing at.
    List<PlanPlace> Candidates(string id)
    {
        if (VoiceKinds.IsKind(id))
            return Plan.places.FindAll(p => VoiceKinds.Matches(p, id) && !Inside(p) && p.doors.Count > 0);
        PlanPlace place = Plan.places.Find(p => p.id == id);
        return place != null && place.doors.Count > 0 ? new List<PlanPlace> { place } : new List<PlanPlace>();
    }

    // Rooms inside other rooms: not where someone in the hallway stands.
    static bool Inside(PlanPlace place) => (place.detail ?? "").StartsWith("Inside ");

    // The phone's location is good enough to choose by, and at the building.
    bool GpsUsable()
    {
        if (location == null || !location.HasFix || location.Accuracy > MaxStartAccuracy)
            return false;
        foreach (PlanPlace p in Plan.places)
            if (Distance(p, location.Position) < MaxGpsDistance)
                return true;
        return false;
    }

    Vector2 Position(PlanPlace place) => Plan.NodePosition(place.doors[0].node);

    // From the nearest of the place's doors.
    float Distance(PlanPlace place, Vector2 at)
    {
        float best = float.PositiveInfinity;
        foreach (PlanDoor door in place.doors)
            best = Mathf.Min(best, Vector2.Distance(Plan.NodePosition(door.node), at));
        return best;
    }

    // Door to door, to the nearest of others (but not to itself).
    float Distance(PlanPlace place, List<PlanPlace> others)
    {
        float best = float.PositiveInfinity;
        foreach (PlanPlace other in others)
        {
            if (other == place)
                continue;
            foreach (PlanDoor door in other.doors)
                best = Mathf.Min(best, Distance(place, Plan.NodePosition(door.node)));
        }
        return best;
    }

    PlanDoor ClosestDoor(PlanPlace place, Vector2 at)
    {
        PlanDoor best = place.doors[0];
        foreach (PlanDoor door in place.doors)
            if (Vector2.Distance(Plan.NodePosition(door.node), at) < Vector2.Distance(Plan.NodePosition(best.node), at))
                best = door;
        return best;
    }

    static PlanPlace Closest(List<PlanPlace> places, System.Func<PlanPlace, float> distance)
    {
        PlanPlace best = null;
        float shortest = float.PositiveInfinity;
        foreach (PlanPlace p in places)
        {
            float d = distance(p);
            if (d < shortest)
            {
                shortest = d;
                best = p;
            }
        }
        return best ?? places[0];
    }

    // --- Words ---------------------------------------------------------------------------------------------

    // "A, B, or C".
    static string Spoken(string[] options)
    {
        if (options.Length < 2)
            return options.Length == 1 ? options[0] : "";
        return string.Join(", ", options, 0, options.Length - 1) + ", or " + options[options.Length - 1];
    }

    // Feet as said aloud, rounded as the banner rounds them.
    static string SpokenFeet(float meters) => $"{FeetRounded(meters)} feet";

    static string Lower(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);
}
