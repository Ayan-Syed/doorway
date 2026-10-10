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
// rotation vector for the heading. Events arrive on a thread of their own and are kept here until Unity collects them
// all with one drain() a frame: a call into C# per event (16 to 66 a second) would cost far more.
public final class DoorwayStepSensor implements SensorEventListener
{
    // What drain() returns: a header, then MAX_SAMPLES at most of (x, y, z, seconds) linear acceleration samples.
    //   [0] steps since the last drain
    //   [1] 1 if there's a new rotation, then [2..5] its quaternion x, y, z, w (device to world: x east, y magnetic
    //       north, z up)
    //   [6...] acceleration less gravity, device axes, m/s^2, and seconds since start()
    public static final int HEADER = 6, SAMPLE = 4;
    // Ten seconds at 50 a second: anything older is dropped (Unity paused, say).
    private static final int MAX_SAMPLES = 512;

    private final SensorManager sensors;
    private final Sensor stepDetector, rotationVector, linearAcceleration;
    private final Handler handler;
    private final float[] samples = new float[MAX_SAMPLES * SAMPLE];
    private final float[] rotation = new float[4];
    private int steps, sampleCount;
    private boolean newRotation;
    private long startedAt = -1;

    public DoorwayStepSensor(Context context)
    {
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

    // Listens: the rotation vector, and the step detector or (useStepDetector false) the linear acceleration. Calling
    // it again switches between the two.
    public synchronized void start(boolean useStepDetector)
    {
        sensors.unregisterListener(this);
        steps = 0;
        sampleCount = 0;
        newRotation = false;
        startedAt = -1;
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

    // Everything since the last drain (see HEADER), and starts afresh; null when nothing has happened.
    public synchronized float[] drain()
    {
        if (steps == 0 && !newRotation && sampleCount == 0)
            return null;
        float[] out = new float[HEADER + sampleCount * SAMPLE];
        out[0] = steps;
        out[1] = newRotation ? 1f : 0f;
        System.arraycopy(rotation, 0, out, 2, 4);
        System.arraycopy(samples, 0, out, HEADER, sampleCount * SAMPLE);
        steps = 0;
        newRotation = false;
        sampleCount = 0;
        return out;
    }

    @Override
    public synchronized void onSensorChanged(SensorEvent event)
    {
        float[] v = event.values;
        switch (event.sensor.getType())
        {
            case Sensor.TYPE_STEP_DETECTOR:
                steps++;
                break;
            case Sensor.TYPE_ROTATION_VECTOR:
                rotation[0] = v[0];
                rotation[1] = v[1];
                rotation[2] = v[2];
                // values[3] (w) is optional on old phones: it follows from the unit length.
                rotation[3] = v.length > 3 ? v[3] : (float) Math.sqrt(Math.max(0.0, 1.0 - v[0] * v[0] - v[1] * v[1] - v[2] * v[2]));
                newRotation = true;
                break;
            case Sensor.TYPE_LINEAR_ACCELERATION:
                if (startedAt < 0)
                    startedAt = event.timestamp;
                if (sampleCount == MAX_SAMPLES)
                {
                    // Full: drop the oldest.
                    System.arraycopy(samples, SAMPLE, samples, 0, (MAX_SAMPLES - 1) * SAMPLE);
                    sampleCount--;
                }
                int at = sampleCount * SAMPLE;
                samples[at] = v[0];
                samples[at + 1] = v[1];
                samples[at + 2] = v[2];
                // Seconds since start, small enough to stay precise as a float for hours.
                samples[at + 3] = (float) ((event.timestamp - startedAt) * 1e-9);
                sampleCount++;
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
