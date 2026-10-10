using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// A walk's poses, frame by frame, for tuning the position filter afterwards: one CSV per walk in
// <persistentDataPath>/walks, the last KeepFiles kept. On the phone:
//   adb pull /sdcard/Android/data/com.ayansyed.doorway/files/walks
// Lines are formatted by hand into one buffer and written once a second, so a frame costs no garbage and no I/O.
// FilterTests replays these files. Rows, by their first field:
//   b,t,yaw,camx,camz,x,z,endx,endz      the start: Begin's plan yaw, the camera (world x, z), the door spot and the
//                                        destination's door spot (plan x, z)
//   f,t,state,tracking,cx,cy,cz,qx,qy,qz,qw,rawx,rawz,rawh,x,z,h,spread,confidence,progress,offroute,floor
//                                        a frame: ARSessionState, tracking 0/1, the camera's world pose, the plan
//                                        position and heading from Begin's alignment alone (raw) and as guided
//                                        (filtered), the filter's spread and confidence (-1 without walls), metres
//                                        along and off the route, and the floor's world height
//   s,t,text,bearing,range,sigma,used,cx,cy,cz,dx,dy,dz,distance,dsigma
//                                        a sign sighting: as the filter took it (world bearing and range from the
//                                        filter's camera, 1 if used), then as the reader made it
//   r,t,endx,endz                        rerouted (the destination's door may have changed)
//   e,t,what                             arrived, realign <why>
// t is seconds since the walk began (Time.realtimeSinceStartupAsDouble); metres and degrees throughout.
//
// The Localizer's logs (one per indoor session, from the snap to going outdoors) hold the raw inputs of its engine,
// so ReplayEvaluator can run them again exactly (LocalizerEngine, the same code and seed):
//   n,t,door,x,z,yaw,camx,camz,k,(x,z,yaw)...,h,(dt,x,z)...   snap: the door spot, the plan's yaw in the AR world,
//                                        the camera (world x, z), the k other doors it might have been (spot and yaw),
//                                        and the camera's track over the seconds before (dt before the snap, x, z)
//   l,t / o,t                            relocalize asked / gone outdoors
//   a,t,tracking,cx,cy,cz,yaw            each frame: tracking 0/1, the camera's world position and flat yaw
//   d,t,source,n,x,z,...                 a depth scan (depth or points): n world (x, z) points, 2 decimals
//   p,t,n,(cx,cz,dx,dz,nx,nz,length)...  the walls ARCore sees (vertical planes, flattened)
//   s,t,text,camx,camz,bearing,range,sigma,measured,digitPixels,focalPixels,distance   a plate read
//   m,t,compass / k,t,compass,length     the compass while tracking / a step (length 0: the usual)
//   q,t,max                              the particle cap, lowered for the time budget
//   c,t,door,x,z                         a checkpoint: the user says they're at this door (the truth)
// and its outputs, for reading but not replayed:
//   r,t,state,x,z,heading,spread,confidence,particles,walls
//   D,t,source,points,bestMean,hitShare,samples,noDepth,lowConfidence,outOfRange,floor,ceiling,kept,floorDrop,floorFrom
//                                        a scan's fit, and where the depth samples since the last D went (floorFrom:
//                                        d the depth's own floor, n Navigator's)
//   g,t,x,z,accuracy (GPS)               e,t,what (state changes, routes, sources)
public sealed class PoseLog
{
    public const string Folder = "walks";
    const int KeepFiles = 10;
    const float FlushSeconds = 1f;
    static readonly long[] s_Pow10 = { 1, 10, 100, 1000, 10000, 100000 };

    StreamWriter m_Writer;
    readonly StringBuilder m_Line = new StringBuilder(32 * 1024);
    char[] m_Chars = new char[32 * 1024];
    readonly char[] m_Digits = new char[24];
    readonly double m_Start;
    float m_NextFlush;

    PoseLog(StreamWriter writer)
    {
        m_Writer = writer;
        m_Start = Time.realtimeSinceStartupAsDouble;
        m_NextFlush = Time.unscaledTime + FlushSeconds;
    }

    // A new file for a walk from the door node `from` to the place `to`; null (no log) if it can't be written.
    public static PoseLog Begin(string from, string to, float yaw, Vector3 camera, Vector2 start, Vector2 end)
    {
        try
        {
            string folder = Path.Combine(Application.persistentDataPath, Folder);
            Directory.CreateDirectory(folder);
            Prune(folder);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, name + ".csv");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(folder, $"{name}-{n}.csv");
            var log = new PoseLog(new StreamWriter(path, false, new UTF8Encoding(false), 64 * 1024));
            log.m_Line.Append("# Doorway walk from ").Append(from).Append(" to ").Append(to).Append(", ")
                .Append(Application.version).Append('\n');
            log.Row('b');
            log.Number(yaw, 2);
            log.Number(camera.x, 3);
            log.Number(camera.z, 3);
            log.Number(start.x, 3);
            log.Number(start.y, 3);
            log.Number(end.x, 3);
            log.Number(end.y, 3);
            log.m_Line.Append('\n');
            return log;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Navigation] No walk log: {e.Message}");
            return null;
        }
    }

    // A Localizer log: no route, no 'b' row; every row comes through Row, Add and EndRow.
    public static PoseLog Open(string what)
    {
        try
        {
            string folder = Path.Combine(Application.persistentDataPath, Folder);
            Directory.CreateDirectory(folder);
            Prune(folder);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, name + ".csv");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(folder, $"{name}-{n}.csv");
            var log = new PoseLog(new StreamWriter(path, false, new UTF8Encoding(false), 64 * 1024));
            log.m_Line.Append("# Doorway ").Append(what).Append(", ").Append(Application.version).Append('\n');
            return log;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Navigation] No walk log: {e.Message}");
            return null;
        }
    }

    /// <summary>Starts a row of this kind at time (Time.realtimeSinceStartupAsDouble).</summary>
    public void Row(char kind, double time)
    {
        if (m_Writer == null)
            return;
        m_Line.Append(kind);
        Number(time - m_Start, 3);
    }

    public void Add(double value, int decimals)
    {
        if (m_Writer != null)
            Number(value, decimals);
    }

    public void Add(long value)
    {
        if (m_Writer != null)
            Integer(value);
    }

    public void Add(string text)
    {
        if (m_Writer == null)
            return;
        m_Line.Append(',');
        if (text == null)
            return;
        foreach (char c in text)
            m_Line.Append(c == ',' || c == '\n' || c == '\r' ? ' ' : c);
    }

    /// <summary>Ends the row; writes the buffer out once a second.</summary>
    public void EndRow()
    {
        if (m_Writer == null)
            return;
        m_Line.Append('\n');
        if (Time.unscaledTime >= m_NextFlush)
            Flush();
    }

    // Keeps the newest KeepFiles - 1, so with the new one there are KeepFiles. Names sort by time.
    static void Prune(string folder)
    {
        string[] files = Directory.GetFiles(folder, "*.csv");
        Array.Sort(files, StringComparer.Ordinal);
        for (int i = 0; i < files.Length - (KeepFiles - 1); i++)
            File.Delete(files[i]);
    }

    public void Frame(bool tracking, int state, Vector3 camera, Quaternion rotation, Vector2 raw, float rawHeading,
        Vector2 at, float heading, float spread, float confidence, float progress, float offRoute, float floor)
    {
        if (m_Writer == null)
            return;
        Row('f');
        Integer(state);
        Integer(tracking ? 1 : 0);
        Number(camera.x, 3);
        Number(camera.y, 3);
        Number(camera.z, 3);
        Number(rotation.x, 4);
        Number(rotation.y, 4);
        Number(rotation.z, 4);
        Number(rotation.w, 4);
        Number(raw.x, 3);
        Number(raw.y, 3);
        Number(rawHeading, 1);
        Number(at.x, 3);
        Number(at.y, 3);
        Number(heading, 1);
        Number(spread, 2);
        Number(confidence, 2);
        Number(progress, 2);
        Number(offRoute, 2);
        Number(floor, 3);
        m_Line.Append('\n');
        if (Time.unscaledTime >= m_NextFlush)
            Flush();
    }

    public void Sighting(in SignSighting s, float bearing, float range, float sigma, bool used)
    {
        if (m_Writer == null)
            return;
        m_Line.Append('s');
        Number(s.time - m_Start, 3);
        m_Line.Append(',');
        if (s.text != null)
        {
            foreach (char c in s.text)
                m_Line.Append(c == ',' || c == '\n' || c == '\r' ? ' ' : c);
        }
        Number(bearing, 2);
        Number(range, 3);
        Number(sigma, 3);
        Integer(used ? 1 : 0);
        Number(s.cameraPosition.x, 3);
        Number(s.cameraPosition.y, 3);
        Number(s.cameraPosition.z, 3);
        Number(s.direction.x, 4);
        Number(s.direction.y, 4);
        Number(s.direction.z, 4);
        Number(s.distance, 3);
        Number(s.distanceSigma, 3);
        m_Line.Append('\n');
    }

    public void Rerouted(Vector2 end)
    {
        if (m_Writer == null)
            return;
        Row('r');
        Number(end.x, 3);
        Number(end.y, 3);
        m_Line.Append('\n');
    }

    public void Note(string what)
    {
        if (m_Writer == null)
            return;
        Row('e');
        m_Line.Append(',').Append(what).Append('\n');
    }

    public void Flush()
    {
        if (m_Writer == null)
            return;
        m_NextFlush = Time.unscaledTime + FlushSeconds;
        int length = m_Line.Length;
        if (length == 0)
            return;
        try
        {
            if (m_Chars.Length < length)
                m_Chars = new char[Mathf.NextPowerOfTwo(length)];
            m_Line.CopyTo(0, m_Chars, 0, length);
            m_Line.Clear();
            m_Writer.Write(m_Chars, 0, length);
            m_Writer.Flush();
        }
        catch (Exception e)
        {
            // A full disk or a pulled card: stop logging, never stop guiding.
            Debug.LogWarning($"[Navigation] Walk log stopped: {e.Message}");
            Close();
        }
    }

    public void End()
    {
        Flush();
        Close();
    }

    void Close()
    {
        try
        {
            m_Writer?.Dispose();
        }
        catch (Exception)
        {
        }
        m_Writer = null;
        m_Line.Clear();
    }

    // A row's kind and its time.
    void Row(char kind)
    {
        m_Line.Append(kind);
        Number(Time.realtimeSinceStartupAsDouble - m_Start, 3);
    }

    void Integer(long value)
    {
        m_Line.Append(',');
        if (value < 0)
        {
            m_Line.Append('-');
            value = -value;
        }
        Digits(value);
    }

    // value with this many decimals, without the garbage double.ToString makes.
    void Number(double value, int decimals)
    {
        m_Line.Append(',');
        if (double.IsNaN(value) || Math.Abs(value) > 1e12)
        {
            m_Line.Append("nan");
            return;
        }
        long scaled = (long)Math.Round(Math.Abs(value) * s_Pow10[decimals]);
        if (value < 0 && scaled != 0)
            m_Line.Append('-');
        Digits(scaled / s_Pow10[decimals]);
        if (decimals == 0)
            return;
        m_Line.Append('.');
        long fraction = scaled % s_Pow10[decimals];
        for (int k = decimals - 1; k >= 0; k--)
        {
            m_Digits[k] = (char)('0' + fraction % 10);
            fraction /= 10;
        }
        m_Line.Append(m_Digits, 0, decimals);
    }

    void Digits(long value)
    {
        int i = m_Digits.Length;
        do
        {
            m_Digits[--i] = (char)('0' + value % 10);
            value /= 10;
        }
        while (value > 0);
        m_Line.Append(m_Digits, i, m_Digits.Length - i);
    }
}
