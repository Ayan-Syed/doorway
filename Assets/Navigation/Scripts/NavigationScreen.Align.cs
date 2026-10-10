using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;

// Align ("line up"): the route on the map turned the way the user will face, other doors nearby, and Start once
// AR is tracking.
public partial class NavigationScreen
{
    // auto: the door was picked from the phone's location. near: where to look for other doors to offer (null:
    // round this door). False, with a word why, if there's no route from there.
    bool ShowAlign(PlanPlace place, PlanDoor door, bool auto, Vector2? near)
    {
        Route route = place == m_Destination ? null : navigator.PlanRoute(door, m_Destination);
        if (place == m_Destination || route != null && route.length < AlreadyThere)
        {
            Snack("You're already there");
            Haptics.Tick(15);
            return false;
        }
        if (route == null)
        {
            Snack("Couldn't find a way from there");
            return false;
        }
        Haptics.Tick(15);
        m_From = door;

        if (originField.isFocused)
            originField.DeactivateInputField();
        originField.gameObject.SetActive(false);
        originButton.gameObject.SetActive(true);
        m_FromTitle = PlaceStyle.DoorTitle(place, door);
        m_FromAuto = auto && location != null && location.HasFix;
        originText.text = m_FromAuto ? OriginNear() : m_FromTitle;

        alignTime.text = Minutes(route.length);
        alignDistance.text = $"({Feet(route.length)})";
        alignHint.text = $"{PlaceStyle.StandHint(place.kind)} Then hold your phone up and tap Start.";
        m_NearbyAt = near;
        FillNearby(near ?? Plan.NodePosition(door.node), door);

        Show(Step.Align);
        OpenSheet(alignSheet, 0);
        // Turned so the way the user will face, away from the door, is up. door.Direction points from the hallway
        // spot toward the door, so the user's back to the door faces the other way.
        SetInsets(BottomOf(faceChip) + 8f, AlignHeight);
        map.Overview(route, Plan.NodePosition(door.node), Navigator.Heading(-door.Direction), !Application.isPlaying);
        m_StartedTime = Time.unscaledTime;
        UpdateAlign();
        return true;
    }

    // "Near Room 113 · ±20 ft": the accuracy clamped as the map's disc is.
    string OriginNear()
    {
        return $"Near {m_FromTitle} · ±{Feet(ShownAccuracy())}";
    }

    // "Not here?": the doors nearest a point, other than the one picked; entrances if it's outside.
    void FillNearby(Vector2 at, PlanDoor current)
    {
        m_Nearby.Clear();
        bool outside = Outside(at);
        foreach (var (place, door, _) in DoorsNear(at))
        {
            if (m_Nearby.Count == nearbyChips.Length)
                break;
            if (door == current || outside && place.kind != "exit")
                continue;
            m_Nearby.Add((place, door));
        }
        for (int i = 0; i < nearbyChips.Length; i++)
        {
            bool shown = i < m_Nearby.Count;
            if (nearbyChips[i].gameObject.activeSelf != shown)
                nearbyChips[i].gameObject.SetActive(shown);
            if (shown)
                nearbyLabels[i].text = PlaceStyle.DoorTitle(m_Nearby[i].place, m_Nearby[i].door);
        }
    }

    // Every frame on Align: Start works only while AR tracks; until then it says what AR is doing.
    void UpdateAlign()
    {
        bool tracking = navigator.IsTracking;
        SetInteractable(beginButton, tracking);
        beginLabel.text = tracking ? "Start" : WaitingForAR();
        ShowBeginSpinner(!tracking && ARSession.state != ARSessionState.Unsupported);
    }

    string WaitingForAR()
    {
        switch (ARSession.state)
        {
            case ARSessionState.Unsupported:
                return "AR isn't supported here";
            case ARSessionState.NeedsInstall:
            case ARSessionState.Installing:
                return "Installing AR services…";
            default:
                return Time.unscaledTime - m_StartedTime < StartingARSeconds ? "Starting AR…" : "Look around slowly…";
        }
    }

    // Turning just left of the label while AR gets going. Placed only when the label changes, so its width
    // (a layout calculation) isn't read every frame.
    void ShowBeginSpinner(bool on)
    {
        if (beginSpinner == null)
            return;
        if (beginSpinner.gameObject.activeSelf != on)
            beginSpinner.gameObject.SetActive(on);
        if (!on || beginLabel.text == m_SpinnerFor)
            return;
        m_SpinnerFor = beginLabel.text;
        var rect = (RectTransform)beginSpinner.transform;
        float x = -beginLabel.preferredWidth / 2f - rect.rect.width / 2f - 20f;
        rect.anchoredPosition = new Vector2(x, rect.anchoredPosition.y);
    }

    // Dimmed and untappable together, so a greyed-out button never looks like it ignored a tap.
    static void SetInteractable(Button button, bool on)
    {
        if (button.interactable != on)
            button.interactable = on;
        float alpha = on ? 1f : 0.45f;
        if (button.TryGetComponent(out CanvasGroup group) && group.alpha != alpha)
            group.alpha = alpha;
    }
}
