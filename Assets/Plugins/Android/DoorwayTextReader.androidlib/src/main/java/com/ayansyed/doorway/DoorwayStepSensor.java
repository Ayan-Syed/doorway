package com.ayansyed.doorway;

import android.content.Context;
import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;

// Steps and which way the phone faces, for StepCounter (Unity). The phone's own step detector when allowed (it needs
// ACTIVITY_RECOGNITION from Android 10), else the linear acceleration for StepCounter to find steps in; and the
// rotation vector for the heading. Sensor events arrive on a thread of their own, never Unity's or the UI's.
public final class DoorwayStepSensor implements SensorEventListener
{
    // What Unity implements, as an AndroidJavaProxy.
    public interface Listener
    {
        void onStep();

        // The rotation vector as a unit quaternion: device to world (x east, y magnetic north, z up).
        void onRotation(float x, float y, float z, float w);

        // Only without the step detector: acceleration less gravity, device axes, m/s^2, and the event's time (ns).
        void onAcceleration(float x, float y, float z, long timestamp);
    }

    private final SensorManager sensors;
    private final Listener listener;
    private final Sensor stepDetector, rotationVector, linearAcceleration;
    private final Handler handler;

    public DoorwayStepSensor(Context context, Listener listener)
    {
        this.listener = listener;
        sensors = (SensorManager) context.getSystemService(Context.SENSOR_SERVICE);
        stepDetector = sensors.getDefaultSensor(Sensor.TYPE_STEP_DETECTOR);
        rotationVector = sensors.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR);
        linearAcceleration = sensors.getDefaultSensor(Sensor.TYPE_LINEAR_ACCELERATION);
        HandlerThread thread = new HandlerThread("DoorwaySteps");
        thread.start();
        handler = new Handler(thread.getLooper());
    }

    public static int sdk()
    {
        return Build.VERSION.SDK_INT;
    }

    public boolean hasStepDetector()
    {
        return stepDetector != null;
    }

    public boolean hasRotationVector()
    {
        return rotationVector != null;
    }

    public boolean hasLinearAcceleration()
    {
        return linearAcceleration != null;
    }

    // Listens: the rotation vector, and the step detector or (useStepDetector false) the linear acceleration.
    public synchronized void start(boolean useStepDetector)
    {
        sensors.unregisterListener(this);
        if (rotationVector != null)
            sensors.registerListener(this, rotationVector, SensorManager.SENSOR_DELAY_UI, handler);
        if (useStepDetector && stepDetector != null)
            sensors.registerListener(this, stepDetector, SensorManager.SENSOR_DELAY_NORMAL, handler);
        else if (linearAcceleration != null)
            sensors.registerListener(this, linearAcceleration, SensorManager.SENSOR_DELAY_GAME, handler);
    }

    public synchronized void stop()
    {
        sensors.unregisterListener(this);
    }

    @Override
    public void onSensorChanged(SensorEvent event)
    {
        float[] v = event.values;
        switch (event.sensor.getType())
        {
            case Sensor.TYPE_STEP_DETECTOR:
                listener.onStep();
                break;
            case Sensor.TYPE_ROTATION_VECTOR:
                // values[3] (w) is optional on old phones: it follows from the unit length.
                float w = v.length > 3 ? v[3] : (float) Math.sqrt(Math.max(0.0, 1.0 - v[0] * v[0] - v[1] * v[1] - v[2] * v[2]));
                listener.onRotation(v[0], v[1], v[2], w);
                break;
            case Sensor.TYPE_LINEAR_ACCELERATION:
                listener.onAcceleration(v[0], v[1], v[2], event.timestamp);
                break;
            default:
                break;
        }
    }

    @Override
    public void onAccuracyChanged(Sensor sensor, int accuracy)
    {
    }
}
