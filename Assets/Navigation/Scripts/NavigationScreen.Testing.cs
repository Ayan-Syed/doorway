using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("Doorway.Tests.EditMode")]

// For the flow tests (Tests/EditMode/FlowTests.cs): the real screen, driven a frame at a time in the editor, with
// a clock they move themselves (the editor's Time doesn't move between their frames) and the taps that aren't
// buttons. IndoorSource stands in for the Localizer.
public partial class NavigationScreen
{
    // Null: the real clock.
    internal static float? TestClock;
    // Null: AR's own tracking (as if tracking, in the editor).
    internal static bool? TestTracking;
    internal void TestFrameSnapConfirm() => UpdateSnapConfirm();

    static float Now => TestClock ?? Time.unscaledTime;

    internal string StepName => m_Step.ToString();
    internal bool SnapCardOpen => m_SnapOpen;

    // Set up as the app opens, but on the floor's map rather than over the campus.
    internal void TestBegin()
    {
        PreviewBegin();
        m_AwaitingCheck = false;
        m_CheckUntil = 0f;
        m_Appearance = 1;   // Light, whatever the last test left
        ApplyAppearance(true);
        ResetHome(false);
        snackbar.alpha = 0f;
        snackbarText.text = "";
    }

    internal void TestFrame() => Update();
    internal void TestShowPlace(PlanPlace place) => ShowPlace(place);
    internal void TestBack() => Back();
    internal bool TestStartFrom(PlanPlace place, PlanDoor door) => StartFrom(place, door, null);
    internal void TestArrive() => OnArrived();
    internal void TestOpenSettings() => OpenSettings();
    internal void TestAppearance(int mode) => SetAppearance(mode);
    internal void TestTapBuilding(CampusData.Building building) => OnBuildingTapped(building);
}
