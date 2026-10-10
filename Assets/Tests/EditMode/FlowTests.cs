using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

// The screen's state machine end to end: the real scene (built as for the APK, laid out as a phone's), frames run by
// hand, and a scripted stand-in for the Localizer (FakeIndoor). Each test starts from the floor's home map.
public class FlowTests
{
    // What the Localizer would say, set by the test; what the screen asked of it, recorded.
    sealed class FakeIndoor : IIndoorSource
    {
        readonly FloorPlan m_Plan;
        public FakeIndoor(FloorPlan plan) => m_Plan = plan;

        public LocalizerState State { get; set; }
        public Vector2 Position { get; set; }
        public float Heading { get; set; }
        public float Spread { get; set; } = 1f;
        public float AlongSpread { get; set; }
        public PlanDoor From;   // where BeginFrom started the walk
        public bool NeedsView { get; set; }
        public bool AskForSign => State == LocalizerState.Lost || AskReason != null;
        public string AskReason { get; set; }
        public bool EntrancePrompt { get; set; }
        public PlanDoor EntranceGuess { get; set; }
        public LocalizerEngine.SnapCheck LastSnapCheck { get; set; }
        public string SnapCheckText { get; set; }
        public event Action<string> SignRead;
        public event Action StateChanged;

        public PlanDoor Snapped;
        public int Dismissed;
        public bool WentOutdoors;

        // As the Localizer does: the position is the door's, not sure yet, and the prompt is answered.
        public void SnapToDoor(PlanDoor door)
        {
            Snapped = door;
            State = LocalizerState.Converging;
            Position = m_Plan.NodePosition(door.node);
            Spread = 4f;
            EntrancePrompt = false;
            LastSnapCheck = LocalizerEngine.SnapCheck.None;
            StateChanged?.Invoke();
        }

        public void DismissEntrancePrompt() => Dismissed++;

        public void GoOutdoors()
        {
            WentOutdoors = true;
            State = LocalizerState.Outdoors;
            StateChanged?.Invoke();
        }

        // From the door nearest the position (Navigator.Preview stands in for the Localizer's own route start).
        public bool BeginFrom(Navigator navigator, PlanPlace to)
        {
            PlanDoor best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (PlanPlace place in m_Plan.places)
            {
                if (place == to)
                    continue;
                foreach (PlanDoor door in place.doors)
                {
                    float distance = Vector2.Distance(Position, m_Plan.NodePosition(door.node));
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = door;
                    }
                }
            }
            if (best == null)
                return false;
            From = best;
            navigator.Preview(best, to, 0f, 0f, false);
            return navigator.Route != null;
        }

        public void Read(string text) => SignRead?.Invoke(text);
    }

    const string Finding = "Finding your exact spot…";
    const string PointAtNumber = "Point your camera at a room number";

    static NavigationScreen s_Screen;
    NavigationScreen m_Screen;
    FakeIndoor m_Fake;
    FloorPlan m_Plan;
    float m_Clock;

    [OneTimeSetUp]
    public void BuildScene()
    {
        s_Screen = BuildNavigateScene.BuildForTests();
        Assert.IsNotNull(s_Screen, "The scene didn't build: see the log.");
    }

    [OneTimeTearDown]
    public void ClearClock() => NavigationScreen.TestClock = null;

    [SetUp]
    public void Begin()
    {
        m_Screen = s_Screen;
        m_Screen.navigator.Load();
        m_Plan = m_Screen.navigator.Plan;
        m_Fake = new FakeIndoor(m_Plan);
        m_Clock = 100f;
        NavigationScreen.TestClock = m_Clock;
        m_Screen.IndoorSource = m_Fake;
        m_Screen.location.SetPreview(null, 0f);
        m_Screen.TestBegin();
        Frame();
    }

    [TearDown]
    public void NothingLogged()
    {
        NavigationScreen.TestTracking = null;
        LogAssert.NoUnexpectedReceived();
    }

    void Frame(float seconds = 0.1f)
    {
        m_Clock += seconds;
        NavigationScreen.TestClock = m_Clock;
        m_Screen.TestFrame();
    }

    PlanPlace Place(string name) => m_Plan.places.Find(p => p.name == name);
    PlanPlace Entrance => m_Plan.places.Find(p => p.kind == "exit" && p.doors.Count > 0);
    string Step => m_Screen.StepName;
    float RibbonAlpha => m_Screen.navigator.ribbon.GuidanceAlpha;
    string Toast => m_Screen.snackbar.alpha > 0f ? m_Screen.snackbarText.text : "";
    static void Tap(Button button) => button.onClick.Invoke();
    static void Tap(CanvasGroup button) => button.GetComponent<Button>().onClick.Invoke();
    static void AreColor(Color expected, Color actual, string what) =>
        Assert.That(((Vector4)(expected - actual)).magnitude, Is.LessThan(0.01f), $"{what}: {actual} is not {expected}");

    void Localized(float spread = 1f)
    {
        m_Fake.State = LocalizerState.Localized;
        m_Fake.Spread = spread;
        Frame();
    }

    // Snapped at an entrance and sure, walking to room 121 with no door picked.
    void WalkTo121()
    {
        m_Fake.Position = m_Plan.NodePosition(Entrance.doors[0].node);
        Localized();
        m_Screen.TestShowPlace(Place("121"));
        Tap(m_Screen.directionsButton);
        Frame();
        Assert.AreEqual("Guide", Step, "Directions while Localized goes straight to walking");
    }

    // Wide enough to be unsure, held long enough to switch.
    void Unsure(float spread = 4f)
    {
        m_Fake.State = LocalizerState.Converging;
        m_Fake.Spread = spread;
        Frame();
        Frame(1.1f);
    }

    [Test]
    public void EntranceToArrivalWhileSure()
    {
        Assert.AreEqual("Home", Step);
        Assert.IsTrue(m_Screen.insideButton.gameObject.activeSelf, "I'm inside shows outdoors");

        PlanPlace entrance = Entrance;
        m_Fake.EntrancePrompt = true;
        m_Fake.EntranceGuess = entrance.doors[0];
        Frame();
        Assert.IsTrue(m_Screen.SnapCardOpen, "the Localizer's prompt opens the snap card");
        StringAssert.Contains(PlaceStyle.Title(entrance), m_Screen.snapTitle.text);

        Tap(m_Screen.snapConfirm);
        Frame();
        Assert.AreEqual(entrance.doors[0], m_Fake.Snapped);
        Assert.IsFalse(m_Screen.SnapCardOpen);
        AreColor(Theme.Current.warning, m_Screen.map.puckFill.color, "converging dot");

        Localized();
        AreColor(Theme.Current.primary, m_Screen.map.puckFill.color, "localized dot");

        m_Screen.TestShowPlace(Place("121"));
        Assert.AreEqual("Place", Step);
        Tap(m_Screen.directionsButton);
        Frame();
        Assert.AreEqual("Guide", Step, "no door picking or lining up while Localized");
        Assert.AreNotEqual(Finding, m_Screen.maneuverTitle.text);
        Assert.AreEqual(1f, RibbonAlpha);

        Unsure();
        Assert.AreEqual(Finding, m_Screen.maneuverTitle.text);
        Assert.AreEqual("", m_Screen.maneuverDistance.text, "no distance while unsure");
        Assert.AreEqual(0.3f, RibbonAlpha, 0.01f, "the ribbon faint while unsure");
        AreColor(Theme.Current.warning, m_Screen.miniMap.puckFill.color, "the minimap's unsure dot");
        Assert.IsFalse(Navigator.SureOfPosition(m_Fake.State, m_Fake.Spread), "no arrival while unsure");

        m_Fake.State = LocalizerState.Localized;
        m_Fake.Spread = 1f;
        Frame();
        Assert.AreEqual(Finding, m_Screen.maneuverTitle.text, "held a moment: no flicker");
        Frame(1.1f);
        Assert.AreNotEqual(Finding, m_Screen.maneuverTitle.text);
        Assert.AreEqual(1f, RibbonAlpha);

        Assert.IsTrue(Navigator.SureOfPosition(m_Fake.State, m_Fake.Spread));
        m_Screen.TestArrive();
        Assert.AreEqual("Arrived", Step);
        Assert.AreEqual("You've arrived", m_Screen.arrivedTitle.text);
    }

    [Test]
    public void ConfirmWaitsForTracking()
    {
        NavigationScreen.TestTracking = false;
        Tap(m_Screen.insideButton);
        Frame();
        Assert.IsTrue(m_Screen.SnapCardOpen);
        Assert.IsFalse(m_Screen.snapConfirm.interactable, "no snap on a stale pose");
        Assert.AreEqual("Starting camera…", m_Screen.snapConfirmLabel.text);
        Assert.IsTrue(m_Screen.snapSpinner.gameObject.activeSelf);
        Tap(m_Screen.snapConfirm);
        Assert.IsNull(m_Fake.Snapped, "Confirm does nothing until AR tracks");
        Assert.IsTrue(m_Screen.SnapCardOpen);

        NavigationScreen.TestTracking = true;
        Frame();
        Assert.IsTrue(m_Screen.snapConfirm.interactable);
        Assert.AreEqual("Confirm", m_Screen.snapConfirmLabel.text);
        Assert.IsFalse(m_Screen.snapSpinner.gameObject.activeSelf);
        Tap(m_Screen.snapConfirm);
        Assert.IsNotNull(m_Fake.Snapped);
        Assert.IsFalse(m_Screen.SnapCardOpen);
    }

    // The walk's progress where the next step is a turn this far ahead (within a metre), or NaN if there's none.
    float ProgressBeforeTurn(float ahead)
    {
        Navigator navigator = m_Screen.navigator;
        float length = navigator.Route.length;
        for (float progress = 0f; progress < length; progress += 0.25f)
        {
            navigator.Preview(m_Fake.From, Place("121"), progress, 0f, false);
            Maneuver step = navigator.NextStep(out float distance, out _, out _);
            if (step != Maneuver.Arrive && step != Maneuver.Straight && Mathf.Abs(distance - ahead) < 1f)
                return progress;
        }
        return float.NaN;
    }

    [Test]
    public void GuidanceRule()
    {
        var g = NavigationScreen.Guidance.Sure;
        Assert.AreEqual(NavigationScreen.Guidance.Lost, NavigationScreen.WantedGuidance(LocalizerState.Lost, 1f, 0f, 50f, g));
        Assert.AreEqual(NavigationScreen.Guidance.Unsure,
            NavigationScreen.WantedGuidance(LocalizerState.Converging, 3f, 0f, 50f, g));
        // Along-route error only near a turn: 4 m of it matters 5 m before a turn, not 20 m before.
        Assert.AreEqual(NavigationScreen.Guidance.TurnUnsure,
            NavigationScreen.WantedGuidance(LocalizerState.Localized, 1f, 4f, 5f, g));
        Assert.AreEqual(NavigationScreen.Guidance.Sure, NavigationScreen.WantedGuidance(LocalizerState.Localized, 1f, 4f, 20f, g));
        Assert.AreEqual(NavigationScreen.Guidance.Sure,
            NavigationScreen.WantedGuidance(LocalizerState.Localized, 1f, 4f, float.PositiveInfinity, g));
        // Hysteresis: 2.2 m isn't enough to enter, but is to stay.
        Assert.AreEqual(NavigationScreen.Guidance.Sure, NavigationScreen.WantedGuidance(LocalizerState.Localized, 1f, 2.2f, 1f, g));
        Assert.AreEqual(NavigationScreen.Guidance.TurnUnsure, NavigationScreen.WantedGuidance(LocalizerState.Localized, 1f, 2.2f, 1f,
            NavigationScreen.Guidance.TurnUnsure));
        Assert.AreEqual(NavigationScreen.Guidance.Sure, NavigationScreen.WantedGuidance(LocalizerState.Localized, 1f, 1.9f, 1f,
            NavigationScreen.Guidance.TurnUnsure));
    }

    [Test]
    public void NearATurnAlongErrorHidesTheDistance()
    {
        WalkTo121();
        float progress = ProgressBeforeTurn(3f);
        Assume.That(progress, Is.Not.NaN, "no turn on the walk to 121");
        m_Fake.AlongSpread = 4f;
        Frame();
        Frame(1.1f);
        Assert.AreEqual("Turn coming up · watch for the corridor", m_Screen.maneuverTitle.text);
        Assert.AreEqual("", m_Screen.maneuverDistance.text, "no distance to a turn it can't place");
        Assert.AreEqual(1f, RibbonAlpha, "the ribbon stays");

        m_Fake.AlongSpread = 1f;
        Frame();
        Frame(1.1f);
        Assert.AreNotEqual("Turn coming up · watch for the corridor", m_Screen.maneuverTitle.text);
        Assert.AreNotEqual("", m_Screen.maneuverDistance.text);
    }

    [Test]
    public void MidCorridorAlongErrorIsIgnored()
    {
        WalkTo121();
        float progress = ProgressBeforeTurn(12f);
        Assume.That(progress, Is.Not.NaN, "no stretch of 12 m before a turn on the walk to 121");
        m_Fake.AlongSpread = 4f;   // 12 m out: past 4 + 3
        Frame();
        Frame(1.1f);
        Assert.AreNotEqual("Turn coming up · watch for the corridor", m_Screen.maneuverTitle.text);
        Assert.AreNotEqual("", m_Screen.maneuverDistance.text, "the turn's distance shows");
        Assert.AreEqual(1f, RibbonAlpha);
    }

    [Test]
    public void WalkingStraightWithThePhoneSwingingSaysNoTurn()
    {
        WalkTo121();
        Navigator nav = m_Screen.navigator;
        Assume.That(nav.Route.turns.Count == 0 || nav.Route.turns[0].distance > 4f, "the first turn is too close to test");
        // Facing 80 degrees off the route (the phone pointed at the room numbers), walking straight along it.
        nav.Preview(m_Fake.From, Place("121"), 0f, 80f, false);
        Route route = nav.Route;
        Vector2 start = route.PointAt(0f), dir = route.DirectionAt(0f);
        int sides = 0, lastSide = 0;
        for (int i = 0; i <= 28; i++)
        {
            float d = i * 0.1f;
            Vector2 p = start + dir * d;
            float swing = i % 2 == 0 ? 80f : -80f;
            nav.arCamera.SetPositionAndRotation(new Vector3(p.x, 1.4f, p.y),
                Quaternion.Euler(0f, Navigator.Heading(dir) + swing, 0f));
            Maneuver step = nav.NextStep(out float distance, out _, out _);
            bool turnNow = distance < 0.01f && step != Maneuver.Arrive && step != Maneuver.Straight;
            if (turnNow)
            {
                int side = step == Maneuver.SlightLeft || step == Maneuver.Left || step == Maneuver.SharpLeft ? 1 : -1;
                if (side != lastSide)
                    sides++;
                lastSide = side;
            }
            if (d >= 1.6f)
                Assert.IsFalse(turnNow, $"walking straight at {d:0.0} m, yet {step} now");
        }
        Assert.LessOrEqual(sides, 1, "the first turn flipped sides");
    }

    [Test]
    public void NoRerouteUnlessSure()
    {
        Assert.IsFalse(Navigator.MayReroute(LocalizerState.Converging, 4f, 0f, 50f), "unsure");
        Assert.IsFalse(Navigator.MayReroute(LocalizerState.Lost, 1f, 0f, 50f), "lost");
        Assert.IsFalse(Navigator.MayReroute(LocalizerState.Localized, 1f, 4f, 3f), "a turn it can't place");
        Assert.IsTrue(Navigator.MayReroute(LocalizerState.Localized, 1f, 4f, 30f), "mid-corridor");
        Assert.IsTrue(Navigator.MayReroute(LocalizerState.Converging, 1.5f, 0f, 50f));
        Assert.IsTrue(Navigator.MayReroute(LocalizerState.Outdoors, 30f, 0f, 1f));
    }

    [Test]
    public void ARerouteOntoTheSameWayIsSilent()
    {
        Navigator nav = m_Screen.navigator;
        PlanPlace to = Place("121"), from = Place("113");
        Route old = nav.PlanRoute(from.doors[0], to);
        Route same = nav.PlanRoute(from.doors[0], to);
        Assert.IsTrue(Navigator.SameStart(old, 0f, same), "the same way on: kept, no toast");
        // A door well away from the old route, so the new one starts somewhere else.
        PlanPlace far = m_Plan.places.Find(p =>
        {
            if (p == to || p.doors.Count == 0)
                return false;
            old.Project(m_Plan.NodePosition(p.doors[0].node), 0f, out float off);
            return off > 6f;
        });
        Assume.That(far, Is.Not.Null);
        Route other = nav.PlanRoute(far.doors[0], to);
        Assert.IsFalse(Navigator.SameStart(old, 0f, other), "a different way: a real reroute");
    }

    [Test]
    public void NotNowIsntAskedAgainUntilReRaised()
    {
        m_Fake.EntrancePrompt = true;
        m_Fake.EntranceGuess = Entrance.doors[0];
        Frame();
        Assert.IsTrue(m_Screen.SnapCardOpen);
        Tap(m_Screen.snapNotNow);
        Assert.AreEqual(1, m_Fake.Dismissed);
        Frame();
        Frame();
        Assert.IsFalse(m_Screen.SnapCardOpen, "not offered again while the same prompt stands");

        m_Fake.EntrancePrompt = false;
        Frame();
        m_Fake.EntrancePrompt = true;
        Frame();
        Assert.IsTrue(m_Screen.SnapCardOpen, "offered again once re-raised");
    }

    [Test]
    public void LostWhileWalkingThenFoundBySign()
    {
        WalkTo121();
        m_Fake.State = LocalizerState.Lost;
        Frame();
        Assert.AreEqual(PointAtNumber, m_Screen.maneuverTitle.text);
        Assert.AreEqual(0f, RibbonAlpha, "no ribbon while lost");
        StringAssert.Contains("room number", m_Screen.statusText.text);

        m_Fake.Read("121");
        Assert.AreEqual("Saw room 121", Toast);
        Localized();
        Assert.AreNotEqual(PointAtNumber, m_Screen.maneuverTitle.text, "guidance back at once");
        Assert.AreEqual(1f, RibbonAlpha);
    }

    [Test]
    public void RoomDoorSnapCorrected()
    {
        Tap(m_Screen.insideButton);
        Assert.IsTrue(m_Screen.SnapCardOpen);
        Tap(m_Screen.snapDoorList);
        Assert.AreEqual("Start", Step);
        Assert.AreEqual("Set your location", m_Screen.destinationText.text);

        PlanPlace room = Place("113");
        m_Screen.TestStartFrom(room, room.doors[0]);
        Assert.IsTrue(m_Screen.SnapCardOpen, "a door picked for where you are opens the snap card");
        StringAssert.Contains("Point at the room number", m_Screen.snapHint.text);
        Tap(m_Screen.snapConfirm);
        Assert.AreEqual(room.doors[0], m_Fake.Snapped);
        Assert.AreEqual("Home", Step);

        m_Fake.LastSnapCheck = LocalizerEngine.SnapCheck.Corrected;
        m_Fake.SnapCheckText = "121";
        Frame();
        Assert.AreEqual("Corrected to Room 121", Toast);
    }

    [Test]
    public void DirectionsUnsureFallsBackToTheDoorList()
    {
        m_Fake.State = LocalizerState.Lost;
        Frame();
        m_Screen.TestShowPlace(Place("121"));
        Tap(m_Screen.directionsButton);
        Assert.AreEqual("Start", Step);
        StringAssert.Contains("pick the door you're at", Toast);

        m_Screen.TestBegin();
        m_Fake.State = LocalizerState.Converging;
        m_Fake.Spread = 6f;
        Frame();
        m_Screen.TestShowPlace(Place("121"));
        Tap(m_Screen.directionsButton);
        Assert.AreEqual("Start", Step);
        StringAssert.Contains("Still finding you", Toast);
    }

    [Test]
    public void BackFromEachStepAndLeavingTheBuilding()
    {
        m_Screen.TestBack();
        Assert.AreEqual("Home", Step);
        Assert.AreEqual("Press back again to exit", Toast);

        PlanPlace room = Place("121"), from = Place("113");
        m_Screen.TestShowPlace(room);
        m_Screen.TestBack();
        Assert.AreEqual("Home", Step);

        m_Screen.TestShowPlace(room);
        Tap(m_Screen.directionsButton);   // outdoors: the door list (no GPS fix to pick one)
        Assert.AreEqual("Start", Step);
        Assert.IsTrue(m_Screen.TestStartFrom(from, from.doors[0]));
        Assert.AreEqual("Align", Step);
        m_Screen.TestBack();
        Assert.AreEqual("Start", Step);
        m_Screen.TestBack();
        Assert.AreEqual("Place", Step);

        m_Screen.TestBegin();
        WalkTo121();
        m_Screen.TestBack();
        Assert.AreEqual("Guide", Step, "the first Back only asks");
        Assert.AreEqual("End?", m_Screen.endLabel.text);
        m_Screen.TestBack();
        Assert.AreEqual("Home", Step);

        Tap(m_Screen.insideButton);
        m_Screen.TestBack();
        Assert.IsFalse(m_Screen.SnapCardOpen, "Back closes the snap card");

        // A GPS fix to fall back on (a preview fix raises no Changed, so it's there before leaving).
        m_Screen.location.SetPreview(m_Plan.NodePosition(Entrance.doors[0].node) + new Vector2(0f, 20f), 8f);
        Localized();
        m_Screen.TestOpenSettings();
        Tap(m_Screen.leftBuildingButton);
        Assert.IsTrue(m_Fake.WentOutdoors);
        Frame();
        Assert.IsTrue(m_Screen.map.puck.activeSelf, "the GPS dot");
        AreColor(Theme.Current.primary, m_Screen.map.puckFill.color, "the GPS dot");
        Assert.IsFalse(m_Screen.map.puckUnknown.activeSelf);
        Assert.IsTrue(m_Screen.insideButton.gameObject.activeSelf, "I'm inside again");
    }

    [Test]
    public void NightModeMidWalkKeepsTheUnsureLook()
    {
        WalkTo121();
        Unsure();
        string day = Theme.Current.name;
        m_Screen.TestAppearance(2);
        Frame();
        Assert.AreNotEqual(day, Theme.Current.name, "the night theme, whatever the designers call it");
        Assert.AreEqual(Finding, m_Screen.maneuverTitle.text);
        AreColor(Theme.Current.warning, m_Screen.miniMap.puckFill.color, "the unsure dot by night");
        Assert.AreEqual(0.3f, RibbonAlpha, 0.01f);
        m_Screen.TestAppearance(1);
    }

    [Test]
    public void ResumingMidWalkAsksToRealign()
    {
        WalkTo121();
        MethodInfo pause = typeof(Navigator).GetMethod("OnApplicationPause", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(pause, "Navigator.OnApplicationPause");
        pause.Invoke(m_Screen.navigator, new object[] { true });
        PausedFor(10.0);   // long enough that the phone may have gone somewhere
        pause.Invoke(m_Screen.navigator, new object[] { false });
        Frame();
        Assert.IsTrue(m_Screen.navigator.ShouldRealign);
        Assert.AreEqual("Re-align at the nearest door", m_Screen.statusText.text);
    }

    [Test]
    public void AShortPauseMidWalkKeepsGuiding()
    {
        WalkTo121();
        MethodInfo pause = typeof(Navigator).GetMethod("OnApplicationPause", BindingFlags.Instance | BindingFlags.NonPublic);
        pause.Invoke(m_Screen.navigator, new object[] { true });   // a permission dialog, say: back at once
        pause.Invoke(m_Screen.navigator, new object[] { false });
        Frame();
        Assert.IsFalse(m_Screen.navigator.ShouldRealign);
    }

    // Moves the pause's start back, as if the app had been away that long.
    void PausedFor(double seconds)
    {
        FieldInfo at = typeof(Navigator).GetField("m_PausedAt", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(at, "Navigator.m_PausedAt");
        at.SetValue(m_Screen.navigator, (double)at.GetValue(m_Screen.navigator) - seconds);
    }
}
