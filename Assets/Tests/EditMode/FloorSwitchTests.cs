using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// Two buildings in one app: the real scene (as for the APK) holds BA1's and HEC's floors, and a tap on the other
// building on the campus map loads its floor in place of the one showing, with the same campus map round both.
public class FloorSwitchTests
{
    const string BA1 = "Business Administration I";
    const string HEC = "L3Harris Engineering Center";

    static NavigationScreen s_Screen;
    NavigationScreen m_Screen;
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
        m_Clock = 100f;
        NavigationScreen.TestClock = m_Clock;
        m_Screen.location.SetPreview(null, 0f);
        m_Screen.TestBegin();
        Frame();
    }

    [TearDown]
    public void BackToBA1()
    {
        m_Screen.SwitchFloor(0);
        m_Screen.TestAppearance(1);   // Light
        LogAssert.NoUnexpectedReceived();
    }

    void Frame(float seconds = 0.1f)
    {
        m_Clock += seconds;
        NavigationScreen.TestClock = m_Clock;
        m_Screen.TestFrame();
    }

    CampusData.Building Building(string name) =>
        m_Screen.map.Campus.buildings.First(b => b.name == name);

    FloorPlan Plan => m_Screen.navigator.Plan;

    void Tap(string building)
    {
        m_Screen.TestTapBuilding(Building(building));
        Frame();
        Frame(1f);
    }

    // The place a label stands in, on the big map.
    PlanPlace PlaceAtLabel(string text)
    {
        PlanLabel label = Plan.labels.First(l => l.text == text);
        return m_Screen.map.PlaceAt(label.Position);
    }

    [Test]
    public void TheAppHoldsBothFloors()
    {
        Assert.AreEqual(new[] { BA1, HEC }, m_Screen.floors.Select(f => f.building).ToArray());
        foreach (FloorAssets floor in m_Screen.floors)
        {
            Assert.IsNotNull(floor.plan);
            Assert.IsNotNull(floor.placeMap);
            Assert.IsNotNull(floor.walls);
            Assert.IsNotNull(floor.walkable);
            Assert.IsNotNull(floor.dayMap);
            Assert.IsNotNull(floor.nightMap);
            Assert.AreEqual(floor.building, FloorPlan.Load(floor.plan).building);
        }
        Assert.AreEqual(0, m_Screen.CurrentFloor, "The app opens on BA1's floor.");
        Assert.IsTrue(Building(BA1).indoor && Building(HEC).indoor, "Both have an indoor map on the campus.");
        Assert.AreEqual(BA1, m_Screen.map.Campus.indoor.name);
    }

    [Test]
    public void TappingHECLoadsItsFloor()
    {
        Tap(HEC);

        Assert.AreEqual(1, m_Screen.CurrentFloor);
        Assert.AreEqual(HEC, Plan.building);
        Assert.AreEqual(HEC, m_Screen.map.Campus.indoor.name, "HEC sinks away as its floor comes in.");
        Assert.AreEqual(HEC, m_Screen.homeTitle.text);
        Assert.AreEqual("Home", m_Screen.StepName);
        // The maps are HEC's: its rooms on the place map, its picture.
        Assert.AreEqual("117", PlaceAtLabel("117")?.id);
        Assert.AreEqual("125", PlaceAtLabel("125")?.id);
        Assert.AreSame(m_Screen.floors[1].dayMap, m_Screen.map.basemap.texture);
        Assert.AreSame(m_Screen.floors[1].dayMap, m_Screen.miniMap.basemap.texture);
        // Routes are HEC's.
        PlanPlace from = Plan.places.First(p => p.id == "117");
        PlanPlace to = Plan.places.First(p => p.id == "125");
        Route route = m_Screen.navigator.PlanRoute(from.doors[0], to);
        Assert.IsNotNull(route);
        Assert.Greater(route.length, 5f);
        Assert.Less(route.length, 80f);
    }

    [Test]
    public void TappingBA1GoesBack()
    {
        Tap(HEC);
        Tap(BA1);

        Assert.AreEqual(0, m_Screen.CurrentFloor);
        Assert.AreEqual(BA1, Plan.building);
        Assert.AreEqual(BA1, m_Screen.map.Campus.indoor.name);
        Assert.AreEqual(BA1, m_Screen.homeTitle.text);
        Assert.AreEqual("121", PlaceAtLabel("121")?.id);
        Assert.AreSame(m_Screen.floors[0].dayMap, m_Screen.map.basemap.texture);
    }

    [Test]
    public void SwitchingLeavesNoOldLabelsBehind()
    {
        Transform labels = m_Screen.map.labelTemplate.transform.parent;
        Transform badges = m_Screen.map.badgeTemplate.transform.parent;
        int labelCount = labels.childCount, badgeCount = badges.childCount;

        Tap(HEC);
        Tap(BA1);
        Tap(HEC);
        Tap(BA1);

        Assert.AreEqual(labelCount, labels.childCount, "room numbers");
        Assert.AreEqual(badgeCount, badges.childCount, "place icons");
    }

    [Test]
    public void TheNightMapIsTheFloorsOwn()
    {
        Tap(HEC);
        m_Screen.TestAppearance(2);   // Dark
        Frame();
        Assert.AreSame(m_Screen.floors[1].nightMap, m_Screen.map.basemap.texture);

        Tap(BA1);
        Assert.AreSame(m_Screen.floors[0].nightMap, m_Screen.map.basemap.texture);
    }

    [Test]
    public void ABuildingWithoutAMapStaysPut()
    {
        CampusData.Building other = m_Screen.map.Campus.buildings
            .First(b => !b.indoor && !string.IsNullOrEmpty(b.name) && FloorMap.Drawable(b.name));
        m_Screen.TestTapBuilding(other);
        Frame();
        Assert.AreEqual(0, m_Screen.CurrentFloor);
        Assert.AreEqual(other.name, m_Screen.homeTitle.text);
        Assert.AreEqual(-1, m_Screen.FloorOf(other));
    }
}
