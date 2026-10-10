using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

// Localizer, continued: the sensors. The depth scan (ARCore environment depth, or its feature points without the
// Depth API), the walls ARCore finds, plates read, the compass and steps: each into the engine and the walk log.
public partial class Localizer
{
    // Depth: a grid of about DepthColumns across the depth image (every 3rd or 4th pixel of ARCore's 160-pixel-wide
    // one: the first field walk's every 8th left a median of 7 points a scan); points 0.4 to 6 m from the camera and
    // MinHeight to MaxHeight above the floor (walls, not floor, ceiling or the user's feet); raw depth's confidence
    // (0-255) at least MinDepthConfidence.
    const int DepthColumns = 48;
    const float MinRange = 0.4f, MaxRange = 6f, MinHeight = 0.4f, MaxHeight = 2f;
    const byte MinDepthConfidence = 100;
    const float MinPointConfidence = 0.3f;
    // The camera image's rotation to upright: a quarter turn for the back camera with the app held in portrait (the
    // app is portrait only, and every Android phone's back camera sensor sits that way round).
    const int ImageRotation = 90;
    // Seconds after depth was asked for, before its absence counts (the subsystem takes a moment to start).
    const float DepthStartSeconds = 3f;
    // A wall ARCore hasn't updated for this long isn't used (it may be out of date, or from the far side). Long: ARCore
    // stops updating a wall once it's settled, and at 10 s the first field walk lost most of its walls that way.
    const float WallSeenSeconds = 60f;
    // Digit height samples: the plate's distance from the estimate, while Localized, the nearest sign with that text
    // being the only one within this; a median of the last ones (robust to the odd wrong read).
    const float DigitUnique = 8f;
    const int DigitSamples = 31, DigitMinSamples = 5;
    const float MinDigit = 0.02f, MaxDigit = 0.15f;
    const float MinLevel = 0.3f;

    enum ScanSource { None, Depth, Points }

    DistanceField m_Field;
    AROcclusionManager m_Occlusion;
    ARCameraManager m_CameraManager;
    ScanSource m_Source = ScanSource.None;
    float m_DepthAskedAt = -1f;
    readonly ScanBuffer m_Buffer = new ScanBuffer();
    readonly Vector2[] m_Scan = new Vector2[ScanBuffer.Target];
    readonly List<WallObservation> m_Walls = new List<WallObservation>();
    readonly Dictionary<TrackableId, float> m_WallSeen = new Dictionary<TrackableId, float>();
    float[] m_Depth = new float[0];
    byte[] m_DepthConfidence = new byte[0];

    void StartSensors()
    {
        Transform camera = navigator.arCamera;
        if (camera != null)
        {
            camera.TryGetComponent(out m_Occlusion);
            camera.TryGetComponent(out m_CameraManager);
        }
        ApplySensors(false);
    }

    // Depth on indoors, off outdoors (it costs the GPU and battery); the feature points only as depth's stand-in.
    void ApplySensors(bool indoors)
    {
        if (m_Occlusion != null)
        {
            m_Occlusion.requestedEnvironmentDepthMode = indoors ? EnvironmentDepthMode.Medium : EnvironmentDepthMode.Disabled;
            // Raw depth, so its confidence image comes with it (ARCore smooths only the other).
            m_Occlusion.environmentDepthTemporalSmoothingRequested = false;
        }
        m_DepthAskedAt = indoors ? Time.unscaledTime : -1f;
        if (pointClouds != null)
            pointClouds.enabled = indoors && m_Source == ScanSource.Points;
        if (!indoors)
            m_Buffer.Clear();
    }

    // Which scan source is working: depth if the phone gives it, else feature points. Logged when it changes.
    void ChooseSource(double t)
    {
        ScanSource source = ScanSource.None;
        bool depthStarting = m_DepthAskedAt >= 0f && Time.unscaledTime - m_DepthAskedAt < DepthStartSeconds;
        if (m_Occlusion != null && (m_Occlusion.currentEnvironmentDepthMode != EnvironmentDepthMode.Disabled || depthStarting))
            source = ScanSource.Depth;
        else if (pointClouds != null)
            source = ScanSource.Points;
        if (source == m_Source)
            return;
        m_Source = source;
        if (pointClouds != null)
            pointClouds.enabled = source == ScanSource.Points;
        Note($"source {source.ToString().ToLowerInvariant()}");
    }

    // Each tracked frame: new points into the buffer, five times a second (depth, or feature points: those are the
    // cloud's whole set each time, and taken every frame they'd fill the buffer with copies in half a second).
    void CollectScan(double t)
    {
        ChooseSource(t);
        if (Time.unscaledTime < m_NextMeasure - 0.01f)
            return;
        if (m_Source == ScanSource.Depth)
            CollectDepth(t);
        else if (m_Source == ScanSource.Points)
            CollectPoints(t);
    }

    // ARCore environment depth: a sparse grid of pixels, each unprojected with the depth image's intrinsics (the
    // camera image's, scaled) from the camera's pose now, kept if in range and between floor and ceiling.
    void CollectDepth(double t)
    {
        if (m_Occlusion == null || m_CameraManager == null || !m_CameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            return;
        try
        {
            if (!m_Occlusion.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depth))
                return;
            using (depth)
            {
                int w = depth.width, h = depth.height;
                if (!ReadDepth(depth, w, h))
                    return;
                bool confident = ReadConfidence(w, h);
                Vector2 scale = new Vector2(w / (float)intrinsics.resolution.x, h / (float)intrinsics.resolution.y);
                Vector2 focal = Vector2.Scale(intrinsics.focalLength, scale);
                Vector2 principal = Vector2.Scale(intrinsics.principalPoint, scale);
                Matrix4x4 pose = navigator.arCamera.localToWorldMatrix;
                float cameraY = navigator.arCamera.position.y;
                int stride = Mathf.Max(2, Mathf.RoundToInt(w / (float)DepthColumns));
                // First the points in range, then the floor among them, then the walls above it.
                int found = 0;
                for (int y = stride / 2; y < h; y += stride)
                {
                    for (int x = stride / 2; x < w; x += stride)
                    {
                        m_DepthCounts[0]++;
                        int i = y * w + x;
                        float z = m_Depth[i];
                        if (z <= 0f)
                        {
                            m_DepthCounts[1]++;
                            continue;
                        }
                        if (confident && m_DepthConfidence[i] < MinDepthConfidence)
                        {
                            m_DepthCounts[2]++;
                            continue;
                        }
                        Vector3 ray = SignReader.ImageToCameraDirection(new Vector2(x, y), focal, principal, ImageRotation);
                        Vector3 local = ray * (z / ray.z);   // depth is along the optical axis
                        float range = local.magnitude;
                        if (range < MinRange || range > MaxRange)
                        {
                            m_DepthCounts[3]++;
                            continue;
                        }
                        if (found == m_DepthPoints.Length)
                            Array.Resize(ref m_DepthPoints, found * 2);
                        m_DepthPoints[found++] = pose.MultiplyPoint3x4(local);
                    }
                }
                float floor = DepthFloor(t, cameraY, found);
                for (int k = 0; k < found; k++)
                {
                    Vector3 world = m_DepthPoints[k];
                    float height = world.y - floor;
                    if (height < MinHeight)
                    {
                        m_DepthCounts[4]++;
                    }
                    else if (height > MaxHeight)
                    {
                        m_DepthCounts[5]++;
                    }
                    else
                    {
                        m_DepthCounts[6]++;
                        m_Buffer.Add(t, new Vector2(world.x, world.z));
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Navigation] Depth scan: {e.Message}");
        }
    }

    // The floor's height under the depth points: the most crowded FloorBin-high layer from FloorDropMin to
    // FloorDropMax below the camera, if it holds FloorShare of them (the floor seen ahead, as a phone held to read
    // its screen sees it); else the last one found within FloorKeepSeconds; else Navigator's (a floor plane, or a
    // guess from a typical eye height). The guess can be 30 cm out, and then the floor ahead passes for a wall: in
    // the first field walk most of the points kept were floor a metre or two ahead.
    float DepthFloor(double t, float cameraY, int count)
    {
        Array.Clear(m_FloorBins, 0, m_FloorBins.Length);
        for (int k = 0; k < count; k++)
        {
            int bin = Mathf.FloorToInt((cameraY - m_DepthPoints[k].y - FloorDropMin) / FloorBin);
            if (bin >= 0 && bin < m_FloorBins.Length)
                m_FloorBins[bin]++;
        }
        int best = 0;
        for (int b = 1; b < m_FloorBins.Length; b++)
            if (m_FloorBins[b] > m_FloorBins[best])
                best = b;
        if (count > 0 && m_FloorBins[best] >= Mathf.Max(FloorMinPoints, FloorShare * count))
        {
            float drop = FloorDropMin + (best + 0.5f) * FloorBin;
            m_FloorDrop = m_FloorAt < 0 ? drop : Mathf.Lerp(m_FloorDrop, drop, FloorLearning);
            m_FloorAt = t;
        }
        if (m_FloorAt >= 0 && t - m_FloorAt <= FloorKeepSeconds)
        {
            m_FloorSource = 'd';
            return cameraY - m_FloorDrop;
        }
        m_FloorSource = 'n';
        return navigator.FloorY;
    }

    const float FloorDropMin = 0.8f, FloorDropMax = 2.3f, FloorBin = 0.05f, FloorShare = 0.1f, FloorLearning = 0.3f;
    const int FloorMinPoints = 10;
    const double FloorKeepSeconds = 5.0;
    readonly int[] m_FloorBins = new int[30];   // (FloorDropMax - FloorDropMin) / FloorBin; from 0.8 m: not a table
    Vector3[] m_DepthPoints = new Vector3[1024];
    float m_FloorDrop;
    double m_FloorAt = -1;
    char m_FloorSource = 'n';
    // Depth samples since the last D row: taken, no depth, low confidence, out of range, below MinHeight (floor),
    // above MaxHeight (ceiling), kept.
    readonly int[] m_DepthCounts = new int[7];

    // The depth image into metres (DepthFloat32 metres, or DepthUint16 millimetres).
    bool ReadDepth(XRCpuImage depth, int w, int h)
    {
        bool millimetres = depth.format == XRCpuImage.Format.DepthUint16;
        if (!millimetres && depth.format != XRCpuImage.Format.DepthFloat32)
            return false;
        if (m_Depth.Length != w * h)
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
        return true;
    }

    // Raw depth's confidence, the same size as the depth image; false (use every pixel) if there isn't one.
    bool ReadConfidence(int w, int h)
    {
        if (!m_Occlusion.TryAcquireEnvironmentDepthConfidenceCpuImage(out XRCpuImage image))
            return false;
        using (image)
        {
            if (image.width != w || image.height != h || image.format != XRCpuImage.Format.OneComponent8)
                return false;
            if (m_DepthConfidence.Length != w * h)
                m_DepthConfidence = new byte[w * h];
            XRCpuImage.Plane plane = image.GetPlane(0);
            NativeArray<byte> values = plane.data;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    m_DepthConfidence[y * w + x] = values[y * plane.rowStride + x];
            return true;
        }
    }

    // Feature points (no Depth API): this frame's, with the same range and height limits.
    void CollectPoints(double t)
    {
        if (pointClouds == null)
            return;
        Vector3 camera = navigator.arCamera.position;
        float floor = navigator.FloorY;
        foreach (ARPointCloud cloud in pointClouds.trackables)
        {
            NativeSlice<Vector3>? positions = cloud.positions;
            if (!positions.HasValue)
                continue;
            NativeArray<float>? confidence = cloud.confidenceValues;
            NativeSlice<Vector3> points = positions.Value;
            for (int i = 0; i < points.Length; i++)
            {
                if (confidence.HasValue && i < confidence.Value.Length && confidence.Value[i] < MinPointConfidence)
                    continue;
                Vector3 world = cloud.transform.TransformPoint(points[i]);
                float range = Vector3.Distance(world, camera), height = world.y - floor;
                if (range < MinRange || range > MaxRange || height < MinHeight || height > MaxHeight)
                    continue;
                m_Buffer.Add(t, new Vector2(world.x, world.z));
            }
        }
    }

    // The buffered scan into the engine, logged with its fit.
    void MatchScan(double t)
    {
        int n = m_Buffer.Build(t, m_Scan);
        if (n == 0)
            return;
        string source = m_Source == ScanSource.Depth ? "depth" : "points";
        if (m_Log != null)
        {
            m_Log.Row('d', t);
            m_Log.Add(source);
            m_Log.Add(n);
            for (int j = 0; j < n; j++)
            {
                m_Log.Add(m_Scan[j].x, 2);
                m_Log.Add(m_Scan[j].y, 2);
            }
            m_Log.EndRow();
        }
        m_Watch.Start();   // the engine's own time only (Measure's budget), not the log's
        bool weighed = m_Engine.Scan(t, m_Scan, n);
        m_Watch.Stop();
        if (!weighed || m_Log == null)
            return;
        ScanMatcher matcher = m_Engine.ScanMatcher;
        m_Log.Row('D', t);
        m_Log.Add(source);
        m_Log.Add(matcher.LastPoints);
        m_Log.Add(matcher.LastBestMean, 3);
        m_Log.Add(matcher.LastHitShare, 2);
        // Where the depth samples went since the last row, and the floor the heights were measured from: its drop
        // below the camera, d (from the depth) or n (Navigator's: a floor plane or a guess).
        foreach (int count in m_DepthCounts)
            m_Log.Add(count);
        m_Log.Add(navigator.arCamera.position.y - (m_FloorSource == 'd' ? navigator.arCamera.position.y - m_FloorDrop
            : navigator.FloorY), 2);
        m_Log.Add(m_FloorSource.ToString());
        Array.Clear(m_DepthCounts, 0, m_DepthCounts.Length);
        m_Log.EndRow();
    }

    void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> changes)
    {
        float now = Time.unscaledTime;
        foreach (ARPlane plane in changes.added)
            m_WallSeen[plane.trackableId] = now;
        foreach (ARPlane plane in changes.updated)
            m_WallSeen[plane.trackableId] = now;
    }

    // The walls ARCore has found near the camera, flattened to lines on the floor, into the engine and the log.
    void MatchWalls(double t)
    {
        m_Walls.Clear();
        ARPlaneManager planes = navigator.planeManager;
        if (planes == null)
            return;
        Vector3 camera = navigator.arCamera.position;
        float now = Time.unscaledTime;
        foreach (ARPlane plane in planes.trackables)
        {
            if (plane.alignment != PlaneAlignment.Vertical || plane.trackingState != TrackingState.Tracking ||
                plane.subsumedBy != null)
                continue;
            if (!m_WallSeen.TryGetValue(plane.trackableId, out float seen) || now - seen > WallSeenSeconds)
                continue;
            Vector3 normal = plane.normal;
            if (Mathf.Abs(normal.y) > 0.3f)
                continue;
            var n = new Vector2(normal.x, normal.z).normalized;
            var along = new Vector2(-n.y, n.x);
            var along3 = new Vector3(along.x, 0f, along.y);
            Transform tf = plane.transform;
            float length = 2f * (plane.extents.x * Mathf.Abs(Vector3.Dot(tf.right, along3)) +
                                 plane.extents.y * Mathf.Abs(Vector3.Dot(tf.forward, along3)));
            Vector3 centre = plane.center;
            var wall = new WallObservation { centre = new Vector2(centre.x, centre.z), direction = along, normal = n, length = length };
            if (length < WallMatcher.MinLength ||
                Vector2.Distance(new Vector2(camera.x, camera.z), wall.centre) > WallMatcher.MaxRange + length * 0.5f)
                continue;
            m_Walls.Add(wall);
        }
        if (m_Log != null)
        {
            m_Log.Row('p', t);
            m_Log.Add(m_Walls.Count);
            foreach (WallObservation w in m_Walls)
            {
                LogXZ(w.centre);
                m_Log.Add(w.direction.x, 4);
                m_Log.Add(w.direction.y, 4);
                m_Log.Add(w.normal.x, 4);
                m_Log.Add(w.normal.y, 4);
                m_Log.Add(w.length, 2);
            }
            m_Log.EndRow();
        }
        m_Watch.Start();
        m_Engine.Walls(t, m_Walls);
        m_Watch.Stop();
    }

    // A plate read (SignReader, or Navigator passing one on): flattened to a bearing and range from the camera where
    // the frame was taken, into the engine; and, while Localized, a measurement of the plates' digit height.
    public void AddSighting(SignSighting s)
    {
        if (!Indoors || m_Engine == null || string.IsNullOrEmpty(s.text))
            return;
        Vector3 direction = s.direction.normalized;
        float level = new Vector2(direction.x, direction.z).magnitude;
        if (level < MinLevel || !(s.distance > 0f))
            return;
        var camera = new Vector2(s.cameraPosition.x, s.cameraPosition.z);
        float bearing = PositionFilter.Heading(new Vector2(direction.x, direction.z));
        float range = s.distance * level, sigma = s.distanceSigma * level;
        double t = s.time;
        if (m_Log != null)
        {
            m_Log.Row('s', t);
            m_Log.Add(s.text.Trim());
            LogXZ(camera);
            m_Log.Add(bearing, 2);
            m_Log.Add(range, 3);
            m_Log.Add(sigma, 3);
            m_Log.Add(s.distanceMeasured ? 1 : 0);
            m_Log.Add(s.digitPixels, 1);
            m_Log.Add(s.focalPixels, 1);
            m_Log.Add(s.distance, 3);
            m_Log.EndRow();
        }
        // The plates' digit height: from a measured distance (depth or a wall plane) directly; else from the estimate
        // while Localized. (Measured only, the first field walk never calibrated: 39 of its 43 reads were measured.)
        if (s.distanceMeasured)
            AddDigitSample(s.digitPixels * s.distance / s.focalPixels);
        else if (State == LocalizerState.Localized)
            CalibrateDigits(s, level);
        m_Engine.Sighting(t, s.text, camera, bearing, range, sigma);
        Publish();
    }

    // How tall the plates' digits really are: from the plate's distance as the estimate puts it, while Localized.
    void CalibrateDigits(in SignSighting s, float level)
    {
        if (!(s.digitPixels > 0f) || !(s.focalPixels > 0f) || navigator.Plan?.signs == null)
            return;
        PlanSign nearest = null;
        float best = float.MaxValue, second = float.MaxValue;
        foreach (PlanSign sign in navigator.Plan.signs)
        {
            if (sign == null || !string.Equals(sign.text, s.text.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            float d = Vector2.Distance(sign.Position, Position);
            if (d < best)
            {
                second = best;
                best = d;
                nearest = sign;
            }
            else if (d < second)
            {
                second = d;
            }
        }
        if (nearest == null || second < DigitUnique)
            return;
        // The sign's world position by the estimate, and its distance along the ray from the camera then.
        PositionFilter filter = m_Engine.Filter;
        Vector2 world = filter.Camera + PositionFilter.ToPlan(nearest.Position - filter.Estimate, -filter.Yaw) / filter.Scale;
        float flat = Vector2.Distance(world, new Vector2(s.cameraPosition.x, s.cameraPosition.z));
        float digit = s.digitPixels * (flat / level) / s.focalPixels;
        if (digit < MinDigit || digit > MaxDigit)
            return;
        AddDigitSample(digit);
    }

    // One digit height sample (metres) into the running median that SignReader ranges plates by.
    void AddDigitSample(float digit)
    {
        if (!(digit >= MinDigit && digit <= MaxDigit))
            return;
        m_DigitHeights.Add(digit);
        if (m_DigitHeights.Count > DigitSamples)
            m_DigitHeights.RemoveAt(0);
        if (m_DigitHeights.Count < DigitMinSamples)
            return;
        var sorted = new List<float>(m_DigitHeights);
        sorted.Sort();
        PlateDigitHeight = SignReader.DigitHeight = sorted[sorted.Count / 2];
        PlayerPrefs.SetFloat(DigitHeightKey, PlateDigitHeight);
    }

    // Twice a second while tracking: the compass, for learning its offset to the plan.
    void Compass(double t)
    {
        if (steps == null || float.IsNaN(steps.Heading))
            return;
        m_Log?.Row('m', t);
        m_Log?.Add(steps.Heading, 1);
        m_Log?.EndRow();
        m_Engine.Compass(t, steps.Heading);
    }

    // A step: the engine moves on it only while ARCore can't track.
    void OnStepped()
    {
        if (!Indoors || m_Engine == null)
            return;
        double t = Now;
        m_Log?.Row('k', t);
        m_Log?.Add(steps.Heading, 1);
        m_Log?.Add(0);
        m_Log?.EndRow();
        m_Engine.Step(t, steps.Heading, 0f);
    }
}
