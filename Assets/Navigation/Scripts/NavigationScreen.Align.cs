using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;

// Align ("line up"): the route on the map turned the way the user will face, other doors nearby, and Start once
// AR is tracking. When AR can't run (no ARCore, no camera permission, AR services not installed) the Start button
// says why, and does what can be done about it.
public partial class NavigationScreen
{
    // What stands between the user and AR, on the Align screen.
    enum ARBlock { None, Unsupported, NoCamera, NotInstalled }

    const string UnsupportedLabel = "AR isn't supported on this phone";
    const string UnsupportedHint = "This phone can't run Google's AR, so the way can't be drawn on the floor. " +
                                   "The map still shows the route.";
    const string NoCameraLabel = "Open settings";
    const string NoCameraHint = "Camera access is off. Doorway draws the way on the floor through the camera: " +
                                "turn on Camera under Permissions.";
    const string NotInstalledLabel = "Install AR services";
    const string NotInstalledHint = "Doorway needs Google Play Services for AR to draw the way on the floor.";

    string m_AlignHintText;   // what to do at this door, while nothing's blocking AR

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
        m_AlignHintText = $"{PlaceStyle.StandHint(place.kind)} Then hold your phone up and tap Start.";
        alignHint.text = m_AlignHintText;
        m_NearbyAt = near;
        // A point from the phone's location is as rough as the fix; a door's spot is exact.
        float accuracy = near.HasValue && location != null && location.HasFix ? location.Accuracy : 0f;
        FillNearby(near ?? Plan.NodePosition(door.node), accuracy, door);

        Show(Step.Align);
        OpenSheet(alignSheet, 0);
        // Turned so the way the user will face, away from the door, is up. door.Direction points from the hallway
        // spot toward the door, so the user's back to the door faces the other way.
        SetInsets(BottomOf(faceChip) + 8f, AlignHeight);
        map.Overview(route, Plan.NodePosition(door.node), Navigator.Heading(-door.Direction), !Application.isPlaying);
        m_StartedTime = Time.unscaledTime;
        UpdateAlign();
        OnVoiceAlign(place, door, auto);
        return true;
    }

    // "Near Room 113 · ±20 ft": the accuracy clamped as the map's disc is.
    string OriginNear()
    {
        return $"Near {m_FromTitle} · ±{Feet(ShownAccuracy())}";
    }

    // "Not here?": the doors nearest a point, other than the one picked; entrances if it's outside.
    void FillNearby(Vector2 at, float accuracy, PlanDoor current)
    {
        m_Nearby.Clear();
        bool outside = Outside(at, accuracy);
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

    // Every frame on Align: Start works only while AR tracks; until then it says what AR is doing, and if AR
    // can't start, why, and what to tap.
    void UpdateAlign()
    {
        if (Application.isPlaying && location != null)
            navigator.HoldAR = location.IsAsking;
        ARBlock block = BlockedAR();
        bool ready = block == ARBlock.None && navigator.IsTracking;
        bool fix = block == ARBlock.NoCamera || block == ARBlock.NotInstalled;
        SetInteractable(beginButton, ready || fix);
        beginLabel.text = ready ? "Start" : block != ARBlock.None ? BlockedLabel(block) : WaitingForAR();
        alignHint.text = block != ARBlock.None ? BlockedHint(block) : m_AlignHintText ?? alignHint.text;
        ShowBeginSpinner(!ready && block == ARBlock.None);
    }

    ARBlock BlockedAR()
    {
        if (!Application.isPlaying)
            return ARBlock.None;
        if (ARSession.state == ARSessionState.Unsupported)
            return ARBlock.Unsupported;
        if (navigator.CameraDenied)
            return ARBlock.NoCamera;
        return navigator.InstallDeclined ? ARBlock.NotInstalled : ARBlock.None;
    }

    static string BlockedLabel(ARBlock block)
    {
        switch (block)
        {
            case ARBlock.Unsupported: return UnsupportedLabel;
            case ARBlock.NoCamera: return NoCameraLabel;
            default: return NotInstalledLabel;
        }
    }

    static string BlockedHint(ARBlock block)
    {
        switch (block)
        {
            case ARBlock.Unsupported: return UnsupportedHint;
            case ARBlock.NoCamera: return NoCameraHint;
            default: return NotInstalledHint;
        }
    }

    // The Start button while AR is blocked: what can be done about it. True if it was blocked.
    bool FixAR()
    {
        switch (BlockedAR())
        {
            case ARBlock.NoCamera:
                OpenAppSettings();
                return true;
            case ARBlock.NotInstalled:
                navigator.InstallAR();
                return true;
            case ARBlock.Unsupported:
                return true;
            default:
                return false;
        }
    }

    string WaitingForAR()
    {
        switch (ARSession.state)
        {
            case ARSessionState.Unsupported:
                return UnsupportedLabel;
            case ARSessionState.NeedsInstall:
            case ARSessionState.Installing:
                return "Installing AR services…";
            default:
                return Time.unscaledTime - m_StartedTime < StartingARSeconds ? "Starting AR…" : "Look around slowly…";
        }
    }

    // Android's page for this app (Permissions is one tap in). Coming back, Navigator starts AR again once the
    // camera is allowed.
    static void OpenAppSettings()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var uris = new AndroidJavaClass("android.net.Uri"))
            using (AndroidJavaObject uri = uris.CallStatic<AndroidJavaObject>("parse", "package:" + Application.identifier))
            using (var intent = new AndroidJavaObject("android.content.Intent",
                       "android.settings.APPLICATION_DETAILS_SETTINGS", uri))
            {
                intent.Call<AndroidJavaObject>("addFlags", 0x10000000).Dispose();   // FLAG_ACTIVITY_NEW_TASK
                activity.Call("startActivity", intent);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Navigation] Couldn't open the app's settings: {e.Message}");
        }
#endif
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
