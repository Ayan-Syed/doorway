using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Concurrent;
using UnityEngine.Android;
using UnityEngine.Scripting;
#endif

// Counts the user's steps and says which way the phone faces, for dead reckoning between fixes. Steps come from the
// phone's own step detector (Android's TYPE_STEP_DETECTOR, which needs the ACTIVITY_RECOGNITION permission from
// Android 10); refused, or with no such sensor, from peaks in the up-and-down acceleration instead. The heading is
// from the rotation vector (gyroscope, accelerometer and compass fused). Sensors run only while `running` is on and
// the app is in front. Java (DoorwayStepSensor) reports on its own thread into a queue that Update empties. Does
// nothing in the editor.
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

#if UNITY_ANDROID && !UNITY_EDITOR
    const string SensorClass = "com.ayansyed.doorway.DoorwayStepSensor";
    const string ActivityRecognition = "android.permission.ACTIVITY_RECOGNITION";
    // The step detector needs the permission from Android 10 (API 29).
    const int PermissionSdk = 29;
    const int Idle = -1, Asking = 0, Granted = 1, Denied = 2;

    readonly ConcurrentQueue<Reading> m_Readings = new ConcurrentQueue<Reading>();
    readonly PeakDetector m_Peaks = new PeakDetector();
    AndroidJavaObject m_Sensor;
    Listener m_Listener;
    bool m_WasRunning, m_Broken, m_Listening, m_Decided, m_UseDetector;
    // Set on Android's thread by the permission callbacks, so it's only ever a number; read in Update.
    volatile int m_Answer = Idle;
    Quaternion m_Rotation = Quaternion.identity;

    void Update()
    {
        while (m_Readings.TryDequeue(out Reading reading))
            Handle(reading);

        int answer = m_Answer;
        if (answer == Granted || answer == Denied)
        {
            m_Answer = Idle;
            Decide(answer == Granted);
        }

        if (running != m_WasRunning)
        {
            m_WasRunning = running;
            if (running)
                Begin();
            else
                Listen(false);
        }
    }

    void Begin()
    {
        if (m_Broken)
            return;
        try
        {
            if (m_Sensor == null)
            {
                m_Listener = new Listener(m_Readings);
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    m_Sensor = new AndroidJavaObject(SensorClass, activity, m_Listener);
            }
            if (m_Decided)
            {
                Listen(true);
                return;
            }
            if (m_Answer == Asking)
                return;   // the dialog is up
            int sdk;
            using (var sensorClass = new AndroidJavaClass(SensorClass))
                sdk = sensorClass.CallStatic<int>("sdk");
            if (sdk < PermissionSdk || Permission.HasUserAuthorizedPermission(ActivityRecognition))
            {
                Decide(true);
                return;
            }
            m_Answer = Asking;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => m_Answer = Granted;
            callbacks.PermissionDenied += _ => m_Answer = Denied;
#pragma warning disable CS0618
            callbacks.PermissionDeniedAndDontAskAgain += _ => m_Answer = Denied;
#pragma warning restore CS0618
            Permission.RequestUserPermission(ActivityRecognition, callbacks);
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    // Detector or fallback, once per run of the app: asked once, the answer stands.
    void Decide(bool allowed)
    {
        if (m_Broken || m_Sensor == null)
            return;
        try
        {
            m_Decided = true;
            m_UseDetector = allowed && m_Sensor.Call<bool>("hasStepDetector");
            Available = m_UseDetector;
            Fallback = !m_UseDetector && m_Sensor.Call<bool>("hasLinearAcceleration");
            if (!m_Sensor.Call<bool>("hasRotationVector"))
                Debug.LogWarning("[Navigation] Steps: no rotation vector sensor, so no heading.");
            Debug.Log($"[Navigation] Steps: {(Available ? "step detector" : Fallback ? "acceleration peaks" : "no way to count")}" +
                      $"{(allowed ? "" : " (activity recognition refused)")}.");
            if (running)
                Listen(true);
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    void Listen(bool on)
    {
        if (m_Sensor == null || m_Broken || on == m_Listening || (on && !m_Decided))
            return;
        try
        {
            if (on)
                m_Sensor.Call("start", m_UseDetector);
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

    void Handle(Reading reading)
    {
        switch (reading.kind)
        {
            case Kind.Step:
                if (m_UseDetector)
                    Step();
                break;
            case Kind.Rotation:
                m_Rotation = reading.rotation;
                Heading = HeadingFromRotation(reading.rotation);
                break;
            case Kind.Acceleration:
                if (!m_UseDetector && m_Peaks.Add(reading.time, VerticalAcceleration(reading.acceleration, m_Rotation)))
                    Step();
                break;
        }
    }

    void Step()
    {
        Steps++;
        Stepped?.Invoke();
    }

    // Sensors stop with the app in the background (and the queue with them), and start again on return.
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

    enum Kind { Step, Rotation, Acceleration }

    readonly struct Reading
    {
        public readonly Kind kind;
        public readonly Quaternion rotation;
        public readonly Vector3 acceleration;
        public readonly double time;

        public Reading(Kind kind, Quaternion rotation, Vector3 acceleration, double time)
        {
            this.kind = kind;
            this.rotation = rotation;
            this.acceleration = acceleration;
            this.time = time;
        }
    }

    // DoorwayStepSensor.Listener, implemented here (the names are Java's). Called on the sensors' own thread, so it
    // only queues.
    sealed class Listener : AndroidJavaProxy
    {
        readonly ConcurrentQueue<Reading> m_Readings;

        public Listener(ConcurrentQueue<Reading> readings) : base(SensorClass + "$Listener")
        {
            m_Readings = readings;
        }

        [Preserve]
        public void onStep()
        {
            m_Readings.Enqueue(new Reading(Kind.Step, default, default, 0));
        }

        [Preserve]
        public void onRotation(float x, float y, float z, float w)
        {
            m_Readings.Enqueue(new Reading(Kind.Rotation, new Quaternion(x, y, z, w), default, 0));
        }

        [Preserve]
        public void onAcceleration(float x, float y, float z, long timestamp)
        {
            m_Readings.Enqueue(new Reading(Kind.Acceleration, default, new Vector3(x, y, z), timestamp * 1e-9));
        }
    }
#endif
}
