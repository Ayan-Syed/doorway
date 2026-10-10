package com.ayansyed.doorway;

import android.content.Context;
import android.graphics.Rect;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraManager;
import android.util.Log;
import android.view.Surface;
import android.view.WindowManager;

import com.google.mlkit.vision.common.InputImage;
import com.google.mlkit.vision.text.Text;
import com.google.mlkit.vision.text.TextRecognition;
import com.google.mlkit.vision.text.TextRecognizer;
import com.google.mlkit.vision.text.latin.TextRecognizerOptions;

import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.concurrent.Executor;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

// Reads the words in grey camera frames for SignReader (Unity), with ML Kit's on-device Latin text recogniser. The
// model is bundled in the app (com.google.mlkit:text-recognition, see build.gradle), so nothing is downloaded and it
// works offline. One frame at a time: a frame offered while another is being read is turned away.
public final class DoorwayTextReader
{
    // What Unity implements, as an AndroidJavaProxy. Called on this reader's own thread, never Unity's or the UI's.
    public interface Listener
    {
        // One line per word: "text|left|top|right|bottom" in the input frame's pixels, and an empty line after each
        // line of text. The text itself can hold '|' (a misread 1), so read the four numbers from the end.
        void onText(String packed);

        void onError(String message);
    }

    private static final String TAG = "DoorwayText";

    private final Listener listener;
    private final AtomicBoolean busy = new AtomicBoolean();
    // Results are handed to Unity from here rather than from the UI thread. A daemon, so it never holds the app up.
    private final Executor callbacks = Executors.newSingleThreadExecutor(runnable ->
    {
        Thread thread = new Thread(runnable, TAG);
        thread.setDaemon(true);
        return thread;
    });
    private volatile TextRecognizer recognizer;
    // The NV21 frame ML Kit reads: the grey pixels, then flat colour (0x80) for the chroma. Reused, as only one frame
    // is ever being read, and its chroma never changes.
    private byte[] nv21;

    public DoorwayTextReader(Listener listener)
    {
        this.listener = listener;
        recognizer = TextRecognition.getClient(TextRecognizerOptions.DEFAULT_OPTIONS);
    }

    // Starts reading a width x height grey frame, row 0 at the top as the camera sensor sees it; rotationDegrees turns
    // it clockwise to upright. The pixels are copied before this returns, so the caller can free them straight away.
    // True: exactly one onText or onError follows. False: busy or closed, and nothing follows.
    public boolean process(ByteBuffer luma, int width, int height, int rotationDegrees)
    {
        TextRecognizer reader = recognizer;
        if (reader == null || !busy.compareAndSet(false, true))
            return false;
        try
        {
            int size = width * height;
            int total = size + 2 * ((width + 1) / 2) * ((height + 1) / 2);
            if (nv21 == null || nv21.length != total)
            {
                nv21 = new byte[total];
                Arrays.fill(nv21, size, total, (byte) 0x80);
            }
            ByteBuffer pixels = luma.duplicate();
            pixels.rewind();
            pixels.get(nv21, 0, size);
            InputImage image = InputImage.fromByteArray(nv21, width, height, rotationDegrees, InputImage.IMAGE_FORMAT_NV21);
            reader.process(image).addOnCompleteListener(callbacks, task ->
            {
                // Reported before the next frame is let in, so a result always belongs to the frame Unity sent last.
                try
                {
                    if (recognizer == null)
                        return;   // closed meanwhile
                    if (task.isSuccessful())
                        listener.onText(pack(task.getResult(), width, height, rotationDegrees));
                    else
                        listener.onError(describe(task.getException()));
                }
                finally
                {
                    busy.set(false);
                }
            });
            return true;
        }
        catch (Throwable e)
        {
            busy.set(false);
            listener.onError(describe(e));
            return true;
        }
    }

    // How far to turn the camera's frames clockwise to make them upright on this screen: the back camera sensor's
    // mounting angle less the screen's rotation (Android's usual formula). Most phones' back camera sits at 90.
    @SuppressWarnings("deprecation")
    public static int imageRotation(Context context)
    {
        int sensor = 90;
        try
        {
            CameraManager cameras = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
            for (String id : cameras.getCameraIdList())
            {
                CameraCharacteristics camera = cameras.getCameraCharacteristics(id);
                Integer facing = camera.get(CameraCharacteristics.LENS_FACING);
                Integer orientation = camera.get(CameraCharacteristics.SENSOR_ORIENTATION);
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK && orientation != null)
                {
                    sensor = orientation;
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Log.w(TAG, "No back camera orientation, assuming 90: " + e);
        }

        int screen = 0;
        WindowManager windows = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
        if (windows != null)
        {
            switch (windows.getDefaultDisplay().getRotation())
            {
                case Surface.ROTATION_90: screen = 90; break;
                case Surface.ROTATION_180: screen = 180; break;
                case Surface.ROTATION_270: screen = 270; break;
                default: break;
            }
        }
        return (sensor - screen + 360) % 360;
    }

    // Lets go of the recogniser and its model. A frame still being read is never reported.
    public void close()
    {
        TextRecognizer reader = recognizer;
        recognizer = null;
        if (reader != null)
            reader.close();
    }

    private static String pack(Text text, int width, int height, int rotation)
    {
        StringBuilder packed = new StringBuilder();
        for (Text.TextBlock block : text.getTextBlocks())
        {
            for (Text.Line line : block.getLines())
            {
                for (Text.Element element : line.getElements())
                {
                    Rect box = element.getBoundingBox();
                    if (box == null)
                        continue;
                    // ML Kit's boxes are in the upright picture; Unity wants the frame's own pixels.
                    int[] a = unrotate(box.left, box.top, width, height, rotation);
                    int[] b = unrotate(box.right, box.bottom, width, height, rotation);
                    packed.append(element.getText()).append('|')
                          .append(Math.min(a[0], b[0])).append('|')
                          .append(Math.min(a[1], b[1])).append('|')
                          .append(Math.max(a[0], b[0])).append('|')
                          .append(Math.max(a[1], b[1])).append('\n');
                }
                packed.append('\n');
            }
        }
        return packed.toString();
    }

    // A point of the upright picture back in the width x height input frame it was turned (clockwise) from.
    private static int[] unrotate(int x, int y, int width, int height, int rotation)
    {
        switch (rotation)
        {
            case 90: return new int[] { y, height - x };
            case 180: return new int[] { width - x, height - y };
            case 270: return new int[] { width - y, x };
            default: return new int[] { x, y };
        }
    }

    private static String describe(Throwable e)
    {
        return e == null ? "unknown error" : e.getClass().getSimpleName() + ": " + e.getMessage();
    }
}
