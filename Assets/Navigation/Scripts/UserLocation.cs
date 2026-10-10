using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

// Where the phone's GPS puts the user on the floor plan, so the start door picks itself. Indoors GPS is rough (often
// tens of metres) and jumps about, so the last few fixes are averaged, and it only ever suggests: the nearest doors,
// and the user can pick another. Asks for location as the app opens, and again when the user asks for directions if
// the answer was no; starts again when the user comes back with location turned on. It rests (Suspend) while
// nothing needs it, and forgets its fixes whenever it stops, so an old fix never stands in for a new one. For demos
// it can pretend the phone is somewhere.
public class UserLocation : MonoBehaviour
{
    [Tooltip("A fix older than this many seconds doesn't count.")]
    public float maxAge = 120f;
    [Tooltip("Fixes from this many seconds are averaged, the more accurate ones counting more.")]
    public float smoothing = 6f;

    // How far off a pretend fix says it may be.
    const float PretendAccuracy = 5f;
    // At most this many fixes in the average (a count; the time window is smoothing).
    const int MaxRecent = 8;
    // Android's answer to the permission request.
    const int Granted = 1, Denied = 2, DeniedForGood = 3;
    // An answer that never comes (Unity marks one callback unreliable) stops holding others up, and lets the next
    // Retry ask again, after this many seconds of the app in front with no dialog.
    const float AskTimeout = 20f;

    /// <summary>There is a usable position: a pretend one, or a real, fresh fix.</summary>
    public bool HasFix { get; private set; }
    /// <summary>Plan metres.</summary>
    public Vector2 Position { get; private set; }
    /// <summary>Metres the fix may be off by.</summary>
    public float Accuracy { get; private set; }
    /// <summary>No location: the user said no, it's off in the phone's settings, or it broke.</summary>
    public bool Unavailable => m_Off && m_Pretend == null;
    /// <summary>Android's location permission dialog is up (or about to be): another permission request now
    /// would be refused unseen.</summary>
    public bool IsAsking => m_Asking;

    /// <summary>A new fix, the fix lost, or the location turned off or on.</summary>
    public event Action Changed;

    FloorPlan m_Plan;
    bool m_Running, m_Preview, m_Asking, m_DontAsk, m_Off, m_RealFix, m_Suspended;
    float m_AskingFor;
    // Set on Android's thread by the permission callbacks, so it's only ever a number; read in Update.
    volatile int m_Answer;
    Vector2? m_Pretend;
    float m_NextPoll;
    double m_LastTimestamp;
    readonly List<(double time, Vector2 at, float accuracy)> m_Recent = new List<(double, Vector2, float)>();   // oldest first
    Vector2 m_Mean;
    float m_MeanAccuracy;

    public void Begin(FloorPlan plan)
    {
        m_Plan = plan;
        Retry(true);
    }

    // Starts the location service if it isn't running. ask: may show Android's permission dialog.
    public void Retry(bool ask)
    {
        if (m_Running || m_Asking || m_Preview || m_Suspended || m_Plan?.geo == null || !m_Plan.geo.IsSet)
            return;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            if (!ask || m_DontAsk)
            {
                SetOff(true);
                return;
            }
            m_Asking = true;
            m_AskingFor = 0f;
            SetOff(false);
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => m_Answer = Granted;
            callbacks.PermissionDenied += _ => m_Answer = Denied;
            // Unity marks this one unreliable; at worst we ask again and Android itself says no without a dialog.
#pragma warning disable CS0618
            callbacks.PermissionDeniedAndDontAskAgain += _ => m_Answer = DeniedForGood;
#pragma warning restore CS0618
            Permission.RequestUserPermission(Permission.FineLocation, callbacks);
            return;
        }
#endif
        StartService();
    }

    void StartService()
    {
        try
        {
            if (!Input.location.isEnabledByUser)
            {
                SetOff(true);   // switched off in the phone's settings
                return;
            }
            Input.location.Start(5f, 1f);
            m_Running = true;
            SetOff(false);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Navigation] No location: {e.Message}");
            SetOff(true);
        }
    }

    void SetOff(bool off)
    {
        if (off)
            ForgetFixes();
        if (m_Off == off)
            return;
        m_Off = off;
        Changed?.Invoke();
    }

    // Stopped, the service's last fix only gets older: drop it, so nothing picks a start door from it.
    void ForgetFixes()
    {
        m_Recent.Clear();
        m_LastTimestamp = 0;
        if (!m_RealFix)
            return;
        m_RealFix = false;
        Publish();
        if (m_Pretend == null)
            Changed?.Invoke();
    }

    void StopService()
    {
        try
        {
            if (m_Running)
                Input.location.Stop();
        }
        catch (Exception)
        {
        }
        m_Running = false;
        ForgetFixes();
    }

    // Rests the service while nothing uses it (walking, arrived): GPS searching indoors is a steady drain on top of
    // the camera and ARCore. Off again, it starts as it would on coming back to the app.
    public void Suspend(bool on)
    {
        if (m_Suspended == on || m_Preview)
            return;
        m_Suspended = on;
        if (on)
            StopService();
        else
            Retry(false);
    }

    // For demos away from the building: the location is this plan point until Pretend(null).
    public void Pretend(Vector2? at)
    {
        m_Pretend = at;
        Publish();
        Changed?.Invoke();
    }

    // For the editor's preview renders: this fix (or none), and never the real service.
    public void SetPreview(Vector2? at, float accuracy)
    {
        m_Preview = true;
        m_Recent.Clear();
        m_RealFix = at.HasValue;
        if (at.HasValue)
            Add(0, at.Value, accuracy);
        Publish();
    }

    void Update()
    {
        int answer = m_Answer;
        if (answer != 0)
        {
            m_Answer = 0;
            m_Asking = false;
            if (answer == Granted)
            {
                if (!m_Suspended)
                    StartService();
            }
            else
            {
                m_DontAsk |= answer == DeniedForGood;
                SetOff(true);
            }
        }
        else if (m_Asking && Application.isFocused)
        {
            // The dialog takes the focus while it's up, so this only counts time with no dialog showing.
            m_AskingFor += Time.unscaledDeltaTime;
            if (m_AskingFor >= AskTimeout)
                m_Asking = false;
        }

        if (!m_Running || Time.unscaledTime < m_NextPoll)
            return;
        m_NextPoll = Time.unscaledTime + 1f;
        Poll();
    }

    // Once a second: a new fix into the average, or notice the fix going stale, the service failing, or location
    // switched off in the phone's settings (the service itself just goes quiet then).
    void Poll()
    {
        bool fix = false;
        try
        {
            LocationServiceStatus status = Input.location.status;
            if (!Input.location.isEnabledByUser)
            {
                StopService();
                SetOff(true);
                return;
            }
            if (status == LocationServiceStatus.Failed)
            {
                m_Running = false;
                SetOff(true);
            }
            else if (status == LocationServiceStatus.Running)
            {
                LocationInfo info = Input.location.lastData;
                double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                fix = info.timestamp > 0 && now - info.timestamp < maxAge;
                if (fix && info.timestamp != m_LastTimestamp)
                {
                    m_LastTimestamp = info.timestamp;
                    Add(info.timestamp, m_Plan.geo.ToPlan(info.latitude, info.longitude), info.horizontalAccuracy);
                    m_RealFix = true;
                    Publish();
                    if (m_Pretend == null)
                        Changed?.Invoke();
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Navigation] Location failed: {e.Message}");
            m_Running = false;
            SetOff(true);
        }

        // The fix gained or lost: gone stale, or the service stopped running.
        if (m_RealFix != fix)
        {
            m_RealFix = fix;
            Publish();
            if (m_Pretend == null)
                Changed?.Invoke();
        }
    }

    // A fix into the average: the fixes of the last `smoothing` seconds, each counting by 1 / accuracy².
    void Add(double time, Vector2 at, float accuracy)
    {
        // Far off the others: the phone has moved (or the first fix was an old guess). Start again from it.
        if (m_Recent.Count > 0 && (at - m_Mean).magnitude > 2f * (accuracy + m_MeanAccuracy))
            m_Recent.Clear();
        m_Recent.Add((time, at, accuracy));
        while (m_Recent.Count > MaxRecent || time - m_Recent[0].time > smoothing)
            m_Recent.RemoveAt(0);

        Vector2 sum = Vector2.zero;
        float weights = 0f, accuracies = 0f;
        foreach (var (_, point, a) in m_Recent)
        {
            float weight = 1f / Mathf.Max(a * a, 1f);   // inverse variance; floors at 1 m so 0 m can't divide by zero
            sum += point * weight;
            accuracies += a * weight;
            weights += weight;
        }
        m_Mean = sum / weights;
        m_MeanAccuracy = accuracies / weights;          // a weighted mean of the accuracies, not a shrunk error
    }

    void Publish()
    {
        HasFix = m_Pretend != null || m_RealFix;
        Position = m_Pretend ?? m_Mean;
        Accuracy = m_Pretend != null ? PretendAccuracy : m_MeanAccuracy;
    }

    void OnApplicationPause(bool paused)
    {
        try
        {
            if (paused)
                StopService();   // saves battery in the background, and the fix is stale on return
            else
                Retry(false);    // permission may have been given in Settings meanwhile; never shows a dialog
        }
        catch (Exception)
        {
        }
    }

    void OnApplicationFocus(bool focused)
    {
        try
        {
            if (focused)
                Retry(false);   // location may have been switched on from the notification shade
        }
        catch (Exception)
        {
        }
    }

    void OnDestroy()
    {
        try
        {
            if (m_Running)
                Input.location.Stop();
        }
        catch (Exception)
        {
        }
    }
}
