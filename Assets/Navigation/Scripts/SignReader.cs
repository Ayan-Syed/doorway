using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Concurrent;
using Unity.Collections;
using UnityEngine.Rendering;
using UnityEngine.Scripting;
#endif

// Reads room-number plates ("121", "107A") in the AR camera's frames while the user looks around, and reports each
// as a SignSighting: a ray from where the camera was when the frame was taken, and a distance from how tall the
// digits look. The distance is measured where the phone can: ARCore depth at the text (when the Localizer has depth
// on and the phone has it; this only reads it), else a detected wall along the ray, else estimated from the digits'
// size. The reading is
// Google ML Kit's on-device Latin text recogniser with its model bundled in the app
// (Plugins/Android/DoorwayTextReader.androidlib), so it works offline. A few times a second one grey frame is
// converted off the main thread and handed to Java, one frame at a time. A plate only counts when it's on the plan
// and read in two of the last three frames, so one misread frame can't move the user. Does nothing in the editor,
// and quietly nothing on a phone where the reader won't start.
public class SignReader : MonoBehaviour
{
    public ARCameraManager cameraManager;
    public Camera arCamera;
    [Tooltip("Optional: ARCore depth at the text, the best distance. Only read here: the Localizer turns depth on and off.")]
    public AROcclusionManager occlusionManager;
    [Tooltip("Optional: vertical planes (walls) to measure the distance against when there's no depth.")]
    public ARPlaneManager planeManager;
    [Tooltip("Read plates while on. Off, it costs nothing.")]
    public bool reading;
    [Tooltip("Frames read a second, at most.")]
    public float rate = 3f;
    [Tooltip("Log each sighting, and every 10 s how long reading takes (logcat tag Unity).")]
    public bool logSightings = true;

    /// <summary>The plates' digit height before anything is measured, metres: an estimate.</summary>
    public const float PlateDigitHeight = 0.05f;
    /// <summary>How tall the plates' digits are, metres, for distances from the digits' size. The Localizer can set it
    /// once it has measured the real plates (from sightings with a measured distance).</summary>
    public static float DigitHeight = PlateDigitHeight;

    // One standard deviation of a distance by source: depth is good to about 10 cm plus 5%; a wall plane to about
    // 15 cm; the digits' size to 30%, as their height is a guess and the boxes are loose.
    const float DepthSigma = 0.1f, DepthSigmaShare = 0.05f, PlaneSigma = 0.15f, DigitSigmaShare = 0.3f;
    // A measured distance this many times off the digits' estimate (either way) hit something else: the digits win.
    const float MeasuredAgreement = 3f;
    // Depth pixels either side of the text's centre in the patch whose median is taken (5 x 5).
    const int DepthPatch = 2;
    // Wider frames are shrunk to this before reading: plenty for a plate across a hallway, and quicker to read.
    const int MaxReadWidth = 1280;
    // The biggest CPU image to ask the camera for, in the sensor's own (landscape) orientation.
    const int MaxCameraWidth = 1920, MaxCameraHeight = 1080;

    /// <summary>A plate on the plan, read in two of the last three frames read. Raised on the main thread.</summary>
    public event Action<SignSighting> Sighted;

    // The plan's plate texts, normalised, to the plan's own spelling.
    readonly Dictionary<string, string> m_Texts = new Dictionary<string, string>();
    readonly Votes m_Votes = new Votes();
    readonly List<Word> m_Words = new List<Word>();
    readonly Dictionary<string, Rect> m_Plates = new Dictionary<string, Rect>();

    /// <summary>A camera frame as it was when captured: where the camera was, and how its image maps to rays.</summary>
    public struct Frame
    {
        /// <summary>Time.realtimeSinceStartupAsDouble at capture.</summary>
        public double time;
        /// <summary>The AR camera's world pose at capture.</summary>
        public Vector3 position;
        public Quaternion rotation;
        /// <summary>The AR camera's projection at capture: only for the screen point in the log.</summary>
        public Matrix4x4 projection;
        /// <summary>Focal length and principal point, in pixels of the image that was read.</summary>
        public Vector2 focal, principal;
        /// <summary>Clockwise degrees that turn the image upright on the screen: 90 on most phones held upright.</summary>
        public int imageRotation;
        /// <summary>The read image's size in pixels: places the text in the depth image.</summary>
        public Vector2Int imageSize;
        /// <summary>ARCore depth for the same frame, metres along the optical axis, row 0 at the top as in the camera
        /// image (0 where unknown); null without depth.</summary>
        public float[] depth;
        public int depthWidth, depthHeight;
    }

    /// <summary>A word the text reader found: its text, its box in the image's pixels (y down), and its line.</summary>
    public struct Word
    {
        public string text;
        public Rect box;
        public int line;
    }

    /// <summary>The plates that can count: the plan's sign texts (PlanSign.text). Nothing counts until this is set.</summary>
    public void SetTexts(IEnumerable<string> validTexts)
    {
        m_Texts.Clear();
        m_Votes.Clear();
        if (validTexts == null)
            return;
        foreach (string text in validTexts)
        {
            string key = Normalise(text);
            if (IsPlateText(key))
                m_Texts[key] = text;
        }
    }

    /// <summary>One frame's words from the text reader: each plate on the plan gets a vote, and those in two of the
    /// last three frames become sightings. Called with each result on the phone; public for the tests.</summary>
    public void Read(string packed, Frame frame)
    {
        m_Words.Clear();
        ParseWords(packed, m_Words);
        m_Plates.Clear();
        FindPlates(m_Words, m_Texts.Keys, m_Plates);
        m_Votes.Add(m_Plates.Keys);
        foreach (KeyValuePair<string, Rect> plate in m_Plates)
        {
            if (m_Votes.Confirmed(plate.Key))
                Emit(plate.Key, plate.Value, frame);
        }
    }

    void Emit(string key, Rect box, Frame frame)
    {
        Vector3 local = ImageToCameraDirection(box.center, frame.focal, frame.principal, frame.imageRotation);
        float digits = Distance(box, frame.focal, frame.imageRotation, local);
        if (!(digits > 0f) || float.IsInfinity(digits))
            return;
        Vector3 direction = (frame.rotation * local).normalized;
        var sighting = new SignSighting
        {
            text = m_Texts[key],
            time = frame.time,
            cameraPosition = frame.position,
            direction = direction,
            distance = digits,
            distanceSigma = DigitSigmaShare * digits,
            digitPixels = TextPixels(box, frame.imageRotation),
            focalPixels = FocalPixels(frame.focal, frame.imageRotation),
        };
        string source = "digits";
        float depth = 0f;
        if (frame.imageSize.x > 0 && frame.imageSize.y > 0)
        {
            var at = new Vector2(box.center.x / frame.imageSize.x, box.center.y / frame.imageSize.y);
            depth = DepthToRange(DepthAt(frame.depth, frame.depthWidth, frame.depthHeight, at), local);
        }
        if (Agrees(depth, digits))
        {
            sighting.distance = depth;
            sighting.distanceSigma = DepthSigma + DepthSigmaShare * depth;
            sighting.distanceMeasured = true;
            source = "depth";
        }
        else
        {
            float wall = PlaneRange(new Ray(frame.position, direction));
            if (Agrees(wall, digits))
            {
                sighting.distance = wall;
                sighting.distanceSigma = PlaneSigma;
                sighting.distanceMeasured = true;
                source = "wall";
            }
        }
        if (logSightings)
        {
            Vector2 at = CameraDirectionToViewport(local, frame.projection);
            double age = (Time.realtimeSinceStartupAsDouble - frame.time) * 1000.0;
            Debug.Log($"[Navigation] Sign {sighting.text}: {sighting.distance:0.00} m by {source} (digits say {digits:0.00} m, " +
                      $"{sighting.digitPixels:0} px tall), screen ({at.x:0.00}, {at.y:0.00}), {age:0} ms after capture.");
        }
        Sighted?.Invoke(sighting);
    }

    static bool Agrees(float measured, float digits)
    {
        return measured > 0f && measured < digits * MeasuredAgreement && measured > digits / MeasuredAgreement;
    }

    // Metres along the ray to the nearest tracked vertical plane it crosses inside the plane's outline; 0 if none.
    float PlaneRange(Ray ray)
    {
        if (planeManager == null)
            return 0f;
        float best = 0f;
        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane.trackingState != TrackingState.Tracking || !plane.alignment.IsVertical())
                continue;
            if (!plane.infinitePlane.Raycast(ray, out float enter) || enter <= 0f || (best > 0f && enter >= best))
                continue;
            // Plane space: the plane's local x and z.
            Vector3 local = plane.transform.InverseTransformPoint(ray.GetPoint(enter));
            if (InsidePolygon(plane.boundary, new Vector2(local.x, local.z)))
                best = enter;
        }
        return best;
    }

    static bool InsidePolygon(Unity.Collections.NativeArray<Vector2> polygon, Vector2 p)
    {
        if (!polygon.IsCreated || polygon.Length < 3)
            return false;
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 a = polygon[i], b = polygon[j];
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>The median depth, metres, of the 5 x 5 patch of a depth image around a point given as a share of the
    /// image's width and height (y down); unknown (0) pixels are left out. 0 if there's no depth there.</summary>
    public static float DepthAt(float[] depth, int width, int height, Vector2 normalised)
    {
        if (depth == null || width <= 0 || height <= 0 || depth.Length < width * height)
            return 0f;
        int cx = Mathf.FloorToInt(normalised.x * width), cy = Mathf.FloorToInt(normalised.y * height);
        if (cx < 0 || cy < 0 || cx >= width || cy >= height)
            return 0f;
        var patch = new List<float>((2 * DepthPatch + 1) * (2 * DepthPatch + 1));
        for (int y = Mathf.Max(0, cy - DepthPatch); y <= Mathf.Min(height - 1, cy + DepthPatch); y++)
        {
            for (int x = Mathf.Max(0, cx - DepthPatch); x <= Mathf.Min(width - 1, cx + DepthPatch); x++)
            {
                float d = depth[y * width + x];
                if (d > 0f && !float.IsInfinity(d))
                    patch.Add(d);
            }
        }
        if (patch.Count == 0)
            return 0f;
        patch.Sort();
        int mid = patch.Count / 2;
        return patch.Count % 2 == 1 ? patch[mid] : 0.5f * (patch[mid - 1] + patch[mid]);
    }

    /// <summary>Range along a ray from a depth, which is measured along the camera's optical axis: off-centre the
    /// plate is further. cameraDirection is the unit ray in camera space. 0 when there's no depth.</summary>
    public static float DepthToRange(float depth, Vector3 cameraDirection)
    {
        return depth > 0f && cameraDirection.z > 0f ? depth / cameraDirection.z : 0f;
    }

    /// <summary>Uppercase letters and digits only, with O read as 0 and I, l or | as 1 within a run of digits
    /// ("1O7a" is "107A", but "ROOM" stays "ROOM").</summary>
    public static string Normalise(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        var kept = new char[raw.Length];
        int n = 0;
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '|')
                kept[n++] = c;
        }
        // A run of digits and look-alikes is a number if it holds at least one real digit.
        for (int start = 0; start < n;)
        {
            if (!DigitLike(kept[start]))
            {
                start++;
                continue;
            }
            int end = start;
            bool digit = false;
            while (end < n && DigitLike(kept[end]))
                digit |= char.IsDigit(kept[end++]);
            for (int i = start; digit && i < end; i++)
                kept[i] = kept[i] == 'O' || kept[i] == 'o' ? '0' : kept[i] == 'I' || kept[i] == 'l' || kept[i] == '|' ? '1' : kept[i];
            start = end;
        }
        return new string(kept, 0, n).ToUpperInvariant();
    }

    static bool DigitLike(char c)
    {
        return (c >= '0' && c <= '9') || c == 'O' || c == 'o' || c == 'I' || c == 'l' || c == '|';
    }

    /// <summary>A room number as the plates print it: three digits and maybe one capital ("121", "107A").</summary>
    public static bool IsPlateText(string text)
    {
        if (text == null || (text.Length != 3 && text.Length != 4))
            return false;
        for (int i = 0; i < 3; i++)
        {
            if (text[i] < '0' || text[i] > '9')
                return false;
        }
        return text.Length == 3 || (text[3] >= 'A' && text[3] <= 'Z');
    }

    /// <summary>Reads DoorwayTextReader's result: a "text|left|top|right|bottom" line per word, and an empty line
    /// after each line of text. Malformed lines are skipped.</summary>
    public static void ParseWords(string packed, List<Word> words)
    {
        if (string.IsNullOrEmpty(packed))
            return;
        int line = 0;
        foreach (string row in packed.Split('\n'))
        {
            if (row.Length == 0)
            {
                line++;
                continue;
            }
            // The four numbers from the end: the text itself may hold '|'.
            int bottom = row.LastIndexOf('|');
            int right = bottom > 0 ? row.LastIndexOf('|', bottom - 1) : -1;
            int top = right > 0 ? row.LastIndexOf('|', right - 1) : -1;
            int left = top > 0 ? row.LastIndexOf('|', top - 1) : -1;
            if (left < 0 || !Number(row, left, top, out int x0) || !Number(row, top, right, out int y0) ||
                !Number(row, right, bottom, out int x1) || !Number(row, bottom, row.Length, out int y1))
                continue;
            words.Add(new Word { text = row.Substring(0, left), box = Rect.MinMaxRect(x0, y0, x1, y1), line = line });
        }
    }

    // The integer between two separators.
    static bool Number(string row, int before, int after, out int value)
    {
        return int.TryParse(row.Substring(before + 1, after - before - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The plates among one frame's words: normalised, plate-shaped and on the plan. A number with a lone
    /// letter after it on the same line ("107" "A") is read together when the plan has that plate. Each text once,
    /// with its biggest box (the nearest plate).</summary>
    public static void FindPlates(List<Word> words, ICollection<string> valid, Dictionary<string, Rect> plates)
    {
        for (int i = 0; i < words.Count; i++)
        {
            string text = Normalise(words[i].text);
            Rect box = words[i].box;
            if (text.Length == 3 && i + 1 < words.Count && words[i + 1].line == words[i].line)
            {
                string joined = text + Normalise(words[i + 1].text);
                if (joined.Length == 4 && IsPlateText(joined) && valid.Contains(joined))
                {
                    Rect next = words[i + 1].box;
                    text = joined;
                    box = Rect.MinMaxRect(Mathf.Min(box.xMin, next.xMin), Mathf.Min(box.yMin, next.yMin),
                                          Mathf.Max(box.xMax, next.xMax), Mathf.Max(box.yMax, next.yMax));
                    i++;
                }
            }
            if (!IsPlateText(text) || !valid.Contains(text))
                continue;
            if (plates.TryGetValue(text, out Rect seen) && seen.width * seen.height >= box.width * box.height)
                continue;
            plates[text] = box;
        }
    }

    /// <summary>The unit direction, in Unity camera space (x right, y up, z forward on the screen), through a pixel of
    /// the camera image. The image is in the sensor's own orientation, row 0 at the top; imageRotation turns it
    /// clockwise to upright on the screen. Straight from the image's intrinsics rather than through the screen, which
    /// shows a cropped picture of a different size.</summary>
    public static Vector3 ImageToCameraDirection(Vector2 pixel, Vector2 focal, Vector2 principal, int imageRotation)
    {
        // Image right and down, in focal lengths.
        float a = (pixel.x - principal.x) / focal.x;
        float b = (pixel.y - principal.y) / focal.y;
        // Turned clockwise to upright: right and down on the screen. A quarter turn takes the image's top to the right.
        float right, down;
        switch (Quarter(imageRotation))
        {
            case 90: right = -b; down = a; break;
            case 180: right = -a; down = -b; break;
            case 270: right = b; down = -a; break;
            default: right = a; down = b; break;
        }
        return new Vector3(right, -down, 1f).normalized;
    }

    /// <summary>Where a camera-space direction lands on the screen, as a viewport point (0,0 bottom left), for a
    /// camera with this projection: the inverse of Camera.ViewportPointToRay.</summary>
    public static Vector2 CameraDirectionToViewport(Vector3 direction, Matrix4x4 projection)
    {
        // Unity's view space looks down -z.
        Vector4 clip = projection * new Vector4(direction.x, direction.y, -direction.z, 1f);
        return new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
    }

    /// <summary>Where a pixel of the camera image shows on the screen, as a viewport point.</summary>
    public static Vector2 ImageToViewport(Vector2 pixel, Vector2 focal, Vector2 principal, int imageRotation, Matrix4x4 projection)
    {
        return CameraDirectionToViewport(ImageToCameraDirection(pixel, focal, principal, imageRotation), projection);
    }

    /// <summary>How tall upright text in this box is, in image pixels: with the image a quarter turn from upright,
    /// that's the box's width.</summary>
    public static float TextPixels(Rect box, int imageRotation)
    {
        return Quarter(imageRotation) % 180 == 90 ? box.width : box.height;
    }

    /// <summary>Metres along the ray to a plate whose digits fill this box: the pinhole depth (focal length times
    /// DigitHeight over the digits' height in pixels, both along the text's upright axis), out along the ray,
    /// which is longer off-centre. Zero for an empty box.</summary>
    public static float Distance(Rect box, Vector2 focal, int imageRotation, Vector3 cameraDirection)
    {
        float pixels = TextPixels(box, imageRotation);
        if (pixels <= 0f)
            return 0f;
        return DepthToRange(FocalPixels(focal, imageRotation) * DigitHeight / pixels, cameraDirection);
    }

    /// <summary>The focal length along the upright text's height, in image pixels.</summary>
    public static float FocalPixels(Vector2 focal, int imageRotation)
    {
        return Quarter(imageRotation) % 180 == 90 ? focal.x : focal.y;
    }

    /// <summary>The size a camera image is read at: at most MaxReadWidth wide, same shape, both sides even (NV21).</summary>
    public static Vector2Int ReadSize(Vector2Int image)
    {
        float scale = image.x > MaxReadWidth ? (float)MaxReadWidth / image.x : 1f;
        return new Vector2Int(Mathf.RoundToInt(image.x * scale) & ~1, Mathf.RoundToInt(image.y * scale) & ~1);
    }

    static int Quarter(int degrees)
    {
        return ((degrees % 360) + 360) % 360;
    }

    // The camera configuration with the most CPU-image pixels within 1920 x 1080, keeping the current frame rate
    // and depth sensor use where there's a choice; -1 if none fits.
    static int ChooseConfiguration(IList<XRCameraConfiguration> configurations, XRCameraConfiguration? current)
    {
        int best = -1, bestMatch = -1;
        long bestPixels = 0;
        for (int i = 0; i < configurations.Count; i++)
        {
            XRCameraConfiguration c = configurations[i];
            if (Mathf.Max(c.width, c.height) > MaxCameraWidth || Mathf.Min(c.width, c.height) > MaxCameraHeight)
                continue;
            long pixels = (long)c.width * c.height;
            int match = current == null ? 0 :
                (c.framerate == current.Value.framerate ? 2 : 0) + (c.depthSensorSupported == current.Value.depthSensorSupported ? 1 : 0);
            if (pixels > bestPixels || (pixels == bestPixels && match > bestMatch))
            {
                best = i;
                bestPixels = pixels;
                bestMatch = match;
            }
        }
        return best;
    }

    /// <summary>The plate texts of the last three frames read: a text counts once it's in two of them.</summary>
    public sealed class Votes
    {
        readonly HashSet<string>[] m_Frames = { new HashSet<string>(), new HashSet<string>(), new HashSet<string>() };
        int m_Next;

        public void Clear()
        {
            foreach (HashSet<string> frame in m_Frames)
                frame.Clear();
        }

        /// <summary>One frame read, the oldest of the three forgotten. A frame with no plates counts too.</summary>
        public void Add(IEnumerable<string> texts)
        {
            HashSet<string> frame = m_Frames[m_Next];
            frame.Clear();
            foreach (string text in texts)
                frame.Add(text);
            m_Next = (m_Next + 1) % m_Frames.Length;
        }

        public bool Confirmed(string text)
        {
            int seen = 0;
            foreach (HashSet<string> frame in m_Frames)
            {
                if (frame.Contains(text))
                    seen++;
            }
            return seen >= 2;
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    const string ReaderClass = "com.ayansyed.doorway.DoorwayTextReader";
    // A frame not back from Java after this many seconds is given up on.
    const float GiveUpAfter = 5f;
    // Errors in a row before the reader stops for good: one bad frame is nothing, every frame failing is.
    const int MaxErrors = 5;
    // Seconds between the timing lines in the log.
    const float StatsEvery = 10f;

    // Filled on Java's thread by the Listener, emptied in Update.
    readonly ConcurrentQueue<Result> m_Results = new ConcurrentQueue<Result>();
    readonly jvalue[] m_Args = new jvalue[4];
    readonly List<XRCameraConfiguration> m_Configurations = new List<XRCameraConfiguration>();
    AndroidJavaObject m_Reader;
    Listener m_Listener;
    IntPtr m_Process;
    int m_ImageRotation, m_Errors;
    bool m_WasReading, m_Broken, m_ConfigurationChosen, m_Converting, m_InFlight, m_PosePending, m_ShapeWarned, m_DepthShapeWarned, m_DepthSupportLogged;
    float[] m_Depth;
    XRCpuImage.AsyncConversion m_Conversion;
    // The frame being converted or read: there's only ever one.
    Frame m_Frame;
    Vector2Int m_ReadSize;
    double m_LastTimestamp;
    float m_NextCapture, m_SentAt;
    // For the timing line: frames read and their capture-to-words time; Update's own time on the main thread.
    int m_StatFrames, m_StatUpdates;
    double m_StatReadMs, m_StatMainMs, m_StatMainMaxMs;
    float m_StatSince;

    void OnEnable()
    {
        Camera.onPreRender += OnCameraRender;
        RenderPipelineManager.beginCameraRendering += OnPipelineCameraRender;
    }

    void OnDisable()
    {
        Camera.onPreRender -= OnCameraRender;
        RenderPipelineManager.beginCameraRendering -= OnPipelineCameraRender;
    }

    void Update()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (m_Results.TryDequeue(out Result result))
            Handle(result);

        if (reading != m_WasReading)
        {
            m_WasReading = reading;
            if (reading)
                Begin();
            else
                End();
        }
        if (!reading || m_Broken)
            return;

        if (m_Converting)
            PollConversion();
        else if (m_InFlight)
        {
            if (Time.unscaledTime - m_SentAt > GiveUpAfter)
                m_InFlight = false;   // lost: Handle ignores it if it turns up after all
        }
        else if (Time.unscaledTime >= m_NextCapture)
            Capture();
        Measure(start);
    }

    void Begin()
    {
        m_Votes.Clear();
        m_StatSince = Time.unscaledTime;
        if (m_Reader != null || m_Broken)
            return;
        try
        {
            m_Listener = new Listener(m_Results);
            m_Reader = new AndroidJavaObject(ReaderClass, m_Listener);
            m_Process = AndroidJNIHelper.GetMethodID(m_Reader.GetRawClass(), "process", "(Ljava/nio/ByteBuffer;III)Z", false);
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var readerClass = new AndroidJavaClass(ReaderClass))
                m_ImageRotation = readerClass.CallStatic<int>("imageRotation", activity);
        }
        catch (Exception e)
        {
            // No plugin in the build, or no ML Kit in it.
            Fail(e.Message);
        }
    }

    void End()
    {
        m_Votes.Clear();
        m_PosePending = false;
        if (m_Converting)
        {
            m_Conversion.Dispose();
            m_Converting = false;
        }
    }

    // Off for good, said once.
    void Fail(string why)
    {
        if (m_Broken)
            return;
        m_Broken = true;
        Debug.LogWarning($"[Navigation] Sign reader off: {why}");
        End();
    }

    void Handle(Result result)
    {
        if (!m_InFlight)
            return;   // given up on already
        m_InFlight = false;
        if (!reading || m_Broken)
            return;
        if (!result.ok)
        {
            if (++m_Errors >= MaxErrors)
                Fail(result.text);
            return;
        }
        m_Errors = 0;
        m_StatFrames++;
        m_StatReadMs += (Time.realtimeSinceStartupAsDouble - m_Frame.time) * 1000.0;
        Read(result.text, m_Frame);
    }

    // Takes the newest camera image and starts converting it to grey off the main thread, noting where the camera was.
    void Capture()
    {
        m_NextCapture = Time.unscaledTime + 1f / Mathf.Max(rate, 0.1f);
        if (cameraManager == null || arCamera == null || ARSession.state != ARSessionState.SessionTracking)
            return;
        if (!m_ConfigurationChosen)
            ChooseCameraConfiguration();
        if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics) || !cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            return;
        using (image)
        {
            // The same frame again would vote twice; the camera can be slower than this when it struggles.
            if (image.timestamp == m_LastTimestamp || !image.FormatSupported(TextureFormat.R8))
                return;
            if (!SameShape(intrinsics.resolution, image.dimensions))
            {
                if (!m_ShapeWarned)
                    Debug.LogWarning($"[Navigation] Sign reader: intrinsics for {intrinsics.resolution}, image {image.dimensions}; skipping frames.");
                m_ShapeWarned = true;
                return;
            }
            m_LastTimestamp = image.timestamp;
            m_ReadSize = ReadSize(image.dimensions);
            m_Conversion = image.ConvertAsync(new XRCpuImage.ConversionParams(image, TextureFormat.R8) { outputDimensions = m_ReadSize });
            m_Converting = true;
        }
        var scale = new Vector2((float)m_ReadSize.x / intrinsics.resolution.x, (float)m_ReadSize.y / intrinsics.resolution.y);
        m_Frame = new Frame
        {
            time = Time.realtimeSinceStartupAsDouble,
            focal = Vector2.Scale(intrinsics.focalLength, scale),
            principal = Vector2.Scale(intrinsics.principalPoint, scale),
            imageRotation = m_ImageRotation,
        };
        LogDepthSupport();
        CopyDepth();
        TakePose();
        m_PosePending = true;
    }

    // Once, for the log: whether this phone can give depth at all (the Note 10 has no depth camera, so it would be
    // ARCore's depth from motion). Known once the occlusion subsystem has started.
    void LogDepthSupport()
    {
        if (m_DepthSupportLogged || occlusionManager == null)
            return;
        Supported support = occlusionManager.descriptor?.environmentDepthImageSupported ?? Supported.Unknown;
        if (support == Supported.Unknown && occlusionManager.descriptor == null && ARSession.state != ARSessionState.SessionTracking)
            return;
        m_DepthSupportLogged = true;
        Debug.Log($"[Navigation] Sign reader: environment depth {support} on this phone.");
    }

    // Keeps this frame's depth (a small image, 160 x 90 or so) until the words come back and say where to look.
    void CopyDepth()
    {
        m_Frame.imageSize = m_ReadSize;
        m_Frame.depth = null;
        try
        {
            if (occlusionManager == null || !occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depth))
                return;
            using (depth)
            {
                bool millimetres = depth.format == XRCpuImage.Format.DepthUint16;
                if (!millimetres && depth.format != XRCpuImage.Format.DepthFloat32)
                    return;
                // Placed by share of width and height, so it must frame the same view as the camera image.
                if (!SameShape(depth.dimensions, m_ReadSize))
                {
                    if (!m_DepthShapeWarned)
                        Debug.LogWarning($"[Navigation] Sign reader: depth image {depth.dimensions} isn't the camera image's shape; not using depth.");
                    m_DepthShapeWarned = true;
                    return;
                }
                int w = depth.width, h = depth.height;
                if (m_Depth == null || m_Depth.Length != w * h)
                    m_Depth = new float[w * h];
                XRCpuImage.Plane plane = depth.GetPlane(0);
                if (millimetres)
                {
                    NativeArray<ushort> values = plane.data.Reinterpret<ushort>(1);
                    int stride = plane.rowStride / 2;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            m_Depth[y * w + x] = values[y * stride + x] * 0.001f;
                }
                else
                {
                    NativeArray<float> values = plane.data.Reinterpret<float>(1);
                    int stride = plane.rowStride / 4;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            m_Depth[y * w + x] = values[y * stride + x];
                }
                m_Frame.depth = m_Depth;
                m_Frame.depthWidth = w;
                m_Frame.depthHeight = h;
            }
        }
        catch (Exception e)
        {
            if (!m_DepthShapeWarned)
                Debug.LogWarning($"[Navigation] Sign reader: no depth ({e.Message}).");
            m_DepthShapeWarned = true;
        }
    }

    static bool SameShape(Vector2Int a, Vector2Int b)
    {
        return a.x > 0 && a.y > 0 && b.x > 0 && b.y > 0 && Mathf.Abs((float)a.x / a.y - (float)b.x / b.y) < 0.01f;
    }

    void TakePose()
    {
        Transform pose = arCamera.transform;
        m_Frame.position = pose.position;
        m_Frame.rotation = pose.rotation;
        m_Frame.projection = arCamera.projectionMatrix;
    }

    // The tracked pose driver moves the camera once more just before rendering, to the pose of the frame on screen,
    // which is the one just captured; in Update the camera can still have the previous frame's pose.
    void OnCameraRender(Camera camera)
    {
        if (m_PosePending && camera == arCamera)
        {
            TakePose();
            m_PosePending = false;
        }
    }

    void OnPipelineCameraRender(ScriptableRenderContext context, Camera camera)
    {
        OnCameraRender(camera);
    }

    void PollConversion()
    {
        XRCpuImage.AsyncConversionStatus status = m_Conversion.status;
        if (!status.IsDone())
            return;
        m_Converting = false;
        m_PosePending = false;
        try
        {
            if (status == XRCpuImage.AsyncConversionStatus.Ready)
                Send(m_Conversion.GetData<byte>());
        }
        finally
        {
            m_Conversion.Dispose();
        }
    }

    // Hands the grey frame to Java without a copy on this side: Java copies it out of the direct buffer before
    // process returns, so the conversion can be disposed straight after.
    void Send(NativeArray<byte> pixels)
    {
        IntPtr buffer = AndroidJNI.NewDirectByteBuffer(pixels);
        if (buffer == IntPtr.Zero)
            return;
        try
        {
            m_Args[0] = new jvalue { l = buffer };
            m_Args[1] = new jvalue { i = m_ReadSize.x };
            m_Args[2] = new jvalue { i = m_ReadSize.y };
            m_Args[3] = new jvalue { i = m_Frame.imageRotation };
            bool taken = AndroidJNI.CallBooleanMethod(m_Reader.GetRawObject(), m_Process, m_Args);
            IntPtr thrown = AndroidJNI.ExceptionOccurred();
            if (thrown != IntPtr.Zero)
            {
                AndroidJNI.ExceptionClear();
                AndroidJNI.DeleteLocalRef(thrown);
                Fail("the text reader threw");
                return;
            }
            if (taken)
            {
                m_InFlight = true;
                m_SentAt = Time.unscaledTime;
            }
        }
        finally
        {
            AndroidJNI.DeleteLocalRef(buffer);
        }
    }

    // Once per session: the camera's biggest CPU image up to 1920 x 1080 (ARCore's default is 640 x 480, too small
    // for a plate down the hallway). Only once, as changing it can restart the camera.
    void ChooseCameraConfiguration()
    {
        try
        {
            m_Configurations.Clear();
            using (NativeArray<XRCameraConfiguration> all = cameraManager.GetConfigurations(Allocator.Temp))
                m_Configurations.AddRange(all);
            if (m_Configurations.Count == 0)
                return;   // not known until the camera runs: next time
            m_ConfigurationChosen = true;
            XRCameraConfiguration? current = cameraManager.currentConfiguration;
            int best = ChooseConfiguration(m_Configurations, current);
            if (best >= 0 && m_Configurations[best] != current)
            {
                cameraManager.currentConfiguration = m_Configurations[best];
                Debug.Log($"[Navigation] Sign reader: camera image {m_Configurations[best].resolution}, was {current?.resolution}.");
            }
        }
        catch (Exception e)
        {
            m_ConfigurationChosen = true;
            Debug.LogWarning($"[Navigation] Sign reader: keeping the camera's image size ({e.Message}).");
        }
    }

    void Measure(long start)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        m_StatUpdates++;
        m_StatMainMs += ms;
        m_StatMainMaxMs = Math.Max(m_StatMainMaxMs, ms);
        float elapsed = Time.unscaledTime - m_StatSince;
        if (elapsed < StatsEvery)
            return;
        if (logSightings)
            Debug.Log($"[Navigation] Sign reader: {m_StatFrames} frames read in {elapsed:0} s, " +
                      $"{(m_StatFrames > 0 ? m_StatReadMs / m_StatFrames : 0):0} ms from capture to words; Update " +
                      $"{m_StatMainMs / m_StatUpdates:0.00} ms on average, {m_StatMainMaxMs:0.00} ms at most.");
        m_StatFrames = m_StatUpdates = 0;
        m_StatReadMs = m_StatMainMs = m_StatMainMaxMs = 0;
        m_StatSince = Time.unscaledTime;
    }

    void OnDestroy()
    {
        if (m_Converting)
        {
            m_Conversion.Dispose();
            m_Converting = false;
        }
        if (m_Reader != null)
        {
            try
            {
                m_Reader.Call("close");
            }
            catch (Exception)
            {
            }
            m_Reader.Dispose();
            m_Reader = null;
        }
    }

    readonly struct Result
    {
        public readonly bool ok;
        public readonly string text;

        public Result(bool ok, string text)
        {
            this.ok = ok;
            this.text = text;
        }
    }

    // DoorwayTextReader.Listener, implemented here (the names are Java's). Java calls it on its own thread, so it
    // only queues; Update does the rest.
    sealed class Listener : AndroidJavaProxy
    {
        readonly ConcurrentQueue<Result> m_Results;

        public Listener(ConcurrentQueue<Result> results) : base(ReaderClass + "$Listener")
        {
            m_Results = results;
        }

        [Preserve]
        public void onText(string packed)
        {
            m_Results.Enqueue(new Result(true, packed));
        }

        [Preserve]
        public void onError(string message)
        {
            m_Results.Enqueue(new Result(false, message));
        }
    }
#endif
}
