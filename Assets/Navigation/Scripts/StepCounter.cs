using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

// Counts the user's steps and says which way the phone faces, for dead reckoning between fixes. Steps come from the
// phone's own step detector (Android's TYPE_STEP_DETECTOR, which needs the ACTIVITY_RECOGNITION permission from
// Android 10); refused, or with no such sensor, from peaks in the up-and-down acceleration instead. The heading is
// from the rotation vector (gyroscope, accelerometer and compass fused). Sensors run only while `running` is on and
// the app is in front. Java (DoorwayStepSensor) keeps the events on its own thread and Update collects them in one
// call a frame. Does nothing in the editor.
public class StepCounter : MonoBehaviour
{
    [Tooltip("Listen to the sensors while on. Off, they're unregistered.")]
    public bool running;

    /// <summary>The phone's own step detector is counting (allowed, and the phone has one). False with the fallback.</summary>
    public bool Available { get; private set; }
    /// <summary>Steps are counted from acceleration peaks instead: the permission was refused or there's no detector.</summary>
    public bool Fallback { get; private set; }
    /// <summary>Steps counted since the app started.</summary>
    public int Steps { get; private set; }
    /// <summary>Degrees clockwise from magnetic north that the phone faces: where its top edge points with the phone
    /// flat, where the back camera looks with it upright. NaN until the rotation vector has reported.</summary>
    public float Heading { get; private set; } = float.NaN;

    /// <summary>One step, on the main thread.</summary>
    public event Action Stepped;

    /// <summary>The heading (degrees clockwise from north, 0 to 360) of a phone with this rotation vector, a unit
    /// quaternion turning device axes to world axes (x east, y north, z up). The top edge (device y) and the back
    /// camera (device -z) are added, flattened to the floor: each alone fails as the phone stands up or lies flat.</summary>
    public static float HeadingFromRotation(Quaternion q)
    {
        // Columns of Android's rotation matrix (SensorManager.getRotationMatrixFromVector): device y and z in the world.
        float yEast = 2f * (q.x * q.y - q.z * q.w), yNorth = 1f - 2f * (q.x * q.x + q.z * q.z);
        float zEast = 2f * (q.x * q.z + q.y * q.w), zNorth = 2f * (q.y * q.z - q.x * q.w);
        float east = yEast - zEast, north = yNorth - zNorth;
        if (east * east + north * north < 1e-8f)
            return float.NaN;
        float degrees = Mathf.Atan2(east, north) * Mathf.Rad2Deg;
        return degrees < 0f ? degrees + 360f : degrees;
    }

    /// <summary>The upward part of an acceleration in device axes, for a phone with this rotation vector.</summary>
    public static float VerticalAcceleration(Vector3 acceleration, Quaternion q)
    {
        // The world's up in device axes: the rotation matrix's bottom row.
        var up = new Vector3(2f * (q.x * q.z - q.y * q.w), 2f * (q.y * q.z + q.x * q.w), 1f - 2f * (q.x * q.x + q.y * q.y));
        return Vector3.Dot(acceleration, up);
    }

    /// <summary>Steps from the upward acceleration (gravity removed): a step is a rise above High after a fall below
    /// Low, at least MinInterval after the last one. Lightly smoothed first, as the sensor is noisy.</summary>
    public sealed class PeakDetector
    {
        /// <summary>m/s²: walking lifts the body by 1.5 to 4 m/s² each step; a phone in a still hand stays well below.</summary>
        public const float High = 1.2f, Low = 0.2f;
        /// <summary>Seconds: nobody walks faster than about 3.3 steps a second.</summary>
        public const float MinInterval = 0.3f;
        // Exponential smoothing at about 50 samples a second.
        const float Smoothing = 0.4f;

        float m_Value;
        bool m_Armed = true, m_Started;
        double m_LastStep = double.NegativeInfinity;

        /// <summary>One sample: time in seconds, the upward acceleration. True when it completes a step.</summary>
        public bool Add(double time, float upward)
        {
            m_Value = m_Started ? m_Value + Smoothing * (upward - m_Value) : upward;
            m_Started = true;
            if (m_Value < Low)
                m_Armed = true;
            if (!m_Armed || m_Value <= High || time - m_LastStep < MinInterval)
                return false;
            m_Armed = false;
            m_LastStep = time;
            return true;
        }
    }

    void Step()
    {
        Steps++;
        Stepped?.Invoke();
    }

    /// <summary>Asks for the activity-recognition permission now, at a calm moment (the UI calls it on the Align
    /// screen and as the snap card opens), so the dialog never comes up mid-guidance. Does nothing once it has been
    /// granted or refused this run of the app, or while the dialog is up; a dismissed dialog can be asked again. A
    /// StepCounter that starts running after this never shows the dialog itself.</summary>
    public static void RequestEarly()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            TimeOutAsk();
            int answer = s_Answer;
            if (answer == Asking || answer == Granted || answer == Denied)
                return;
            if (Allowed())
                s_Answer = Granted;
            else
                Ask();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Navigation] Steps: couldn't ask for activity recognition ({e.Message}).");
        }
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    const string SensorClass = "com.ayansyed.doorway.DoorwayStepSensor";
    const string ActivityRecognition = "android.permission.ACTIVITY_RECOGNITION";
    // The step detector needs the permission from Android 10 (API 29).
    const int PermissionSdk = 29;
    // A dialog with no answer after this many seconds (covered by another app's, say) counts as dismissed.
    const float AskTimeout = 20f;
    // Never asked this run; dialog up; and the answers. Dismissed (or timed out) may be asked again; Denied isn't.
    const int Idle = 0, Asking = 1, Granted = 2, Denied = 3, Dismissed = 4;
    // DoorwayStepSensor.drain(): its header, and the floats per acceleration sample.
    const int Header = 6, Sample = 4;

    // Shared by RequestEarly and the counter. Set on Android's thread by the permission callbacks, so only a number.
    static volatile int s_Answer = Idle;
    static float s_AskedAt;
    static int s_Sdk = -1;

    PeakDetector m_Peaks = new PeakDetector();
    AndroidJavaObject m_Sensor;
    bool m_WasRunning, m_Broken, m_Listening, m_Decided, m_UseDetector;
    int m_SeenAnswer = -1;
    Quaternion m_Rotation = Quaternion.identity;

    static bool Allowed()
    {
        if (s_Sdk < 0)
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                s_Sdk = version.GetStatic<int>("SDK_INT");
        return s_Sdk < PermissionSdk || Permission.HasUserAuthorizedPermission(ActivityRecognition);
    }

    static void Ask()
    {
        s_Answer = Asking;
        s_AskedAt = Time.realtimeSinceStartup;
        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => s_Answer = Granted;
        callbacks.PermissionDenied += _ => s_Answer = Denied;
#pragma warning disable CS0618
        callbacks.PermissionDeniedAndDontAskAgain += _ => s_Answer = Denied;
#pragma warning restore CS0618
        callbacks.PermissionRequestDismissed += _ => s_Answer = Dismissed;
        Permission.RequestUserPermission(ActivityRecognition, callbacks);
    }

    static void TimeOutAsk()
    {
        if (s_Answer == Asking && Time.realtimeSinceStartup - s_AskedAt > AskTimeout)
            s_Answer = Dismissed;
    }

    void Update()
    {
        if (m_Listening)
            Drain();

        TimeOutAsk();
        if (running != m_WasRunning)
        {
            m_WasRunning = running;
            if (running)
                Begin();
            else
                Listen(false);
        }
        // An answer that came in while running (or a later grant, from RequestEarly): decide again.
        if (running && m_Sensor != null && s_Answer != m_SeenAnswer)
            Resolve(false);
    }

    void Begin()
    {
        if (m_Broken)
            return;
        try
        {
            if (m_Sensor == null)
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    m_Sensor = new AndroidJavaObject(SensorClass, activity);
            }
            if (m_Decided && s_Answer == m_SeenAnswer)
                Listen(true);
            else
                Resolve(true);
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    // From the permission's state: the detector, the fallback, or wait for the dialog. mayAsk: only the first start,
    // and only if nothing was asked this run.
    void Resolve(bool mayAsk)
    {
        try
        {
            int answer = s_Answer;
            m_SeenAnswer = answer;
            if (answer == Asking)
                return;   // the dialog is up: decide when it answers
            if (answer == Granted)
                Decide(true);
            else if (answer == Denied || answer == Dismissed)
                Decide(false);
            else if (Allowed())
            {
                s_Answer = m_SeenAnswer = Granted;
                Decide(true);
            }
            else if (mayAsk)
            {
                Ask();
                m_SeenAnswer = Asking;
            }
            else
                Decide(false);
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    // Detector or fallback. Again on a later grant: the counting switches over and the step count carries on.
    void Decide(bool allowed)
    {
        if (m_Broken || m_Sensor == null)
            return;
        bool detector = allowed && m_Sensor.Call<bool>("hasStepDetector");
        if (m_Decided && detector == m_UseDetector)
            return;
        m_Decided = true;
        m_UseDetector = detector;
        Available = detector;
        Fallback = !detector && m_Sensor.Call<bool>("hasLinearAcceleration");
        if (!m_Sensor.Call<bool>("hasRotationVector"))
            Debug.LogWarning("[Navigation] Steps: no rotation vector sensor, so no heading.");
        Debug.Log($"[Navigation] Steps: {(Available ? "step detector" : Fallback ? "acceleration peaks" : "no way to count")}" +
                  $"{(allowed ? "" : " (activity recognition not granted)")}.");
        if (m_Listening)
        {
            m_Listening = false;
            Listen(true);   // start() again switches the sensors
        }
        else if (running)
            Listen(true);
    }

    void Listen(bool on)
    {
        if (m_Sensor == null || m_Broken || on == m_Listening || (on && !m_Decided))
            return;
        try
        {
            if (on)
            {
                // start() restarts the samples' clock, so the detector starts afresh too.
                m_Peaks = new PeakDetector();
                m_Sensor.Call("start", m_UseDetector);
            }
            else
                m_Sensor.Call("stop");
            m_Listening = on;
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    // Off for good, said once.
    void Fail(string why)
    {
        if (m_Broken)
            return;
        m_Broken = true;
        Available = Fallback = false;
        Debug.LogWarning($"[Navigation] Steps off: {why}");
    }

    // Everything the sensors said since last frame, in one call (DoorwayStepSensor.drain).
    void Drain()
    {
        float[] data;
        try
        {
            data = m_Sensor.Call<float[]>("drain");
        }
        catch (Exception e)
        {
            Fail(e.Message);
            return;
        }
        if (data == null || data.Length < Header)
            return;
        if (data[1] > 0.5f)
        {
            m_Rotation = new Quaternion(data[2], data[3], data[4], data[5]);
            Heading = HeadingFromRotation(m_Rotation);
        }
        int steps = m_UseDetector ? Mathf.RoundToInt(data[0]) : 0;
        if (!m_UseDetector)
        {
            for (int i = Header; i + Sample <= data.Length; i += Sample)
            {
                var acceleration = new Vector3(data[i], data[i + 1], data[i + 2]);
                if (m_Peaks.Add(data[i + 3], VerticalAcceleration(acceleration, m_Rotation)))
                    steps++;
            }
        }
        for (int i = 0; i < steps; i++)
            Step();
    }

    // Sensors stop with the app in the background, and start again on return.
    void OnApplicationPause(bool paused)
    {
        if (paused)
            Listen(false);
        else if (running)
            Listen(true);
    }

    void OnDestroy()
    {
        Listen(false);
        m_Sensor?.Dispose();
        m_Sensor = null;
    }
#endif
}
