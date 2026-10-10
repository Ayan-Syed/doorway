using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

// Re-runs walk logs (PoseLog's input rows) through LocalizerEngine, with the signals switched off one at a time and
// the raw ARCore baseline, and scores every checkpoint: the user tapped "I'm at this door" (or, in a synthetic walk,
// the truth at each bend). Deterministic: a fixed seed and only the logged times.
//
// Metrics follow ISO/IEC 18305:2016 and the IPIN competition's practice: the error's median, 75th percentile (IPIN's
// headline figure), 95th percentile and maximum, points on its CDF, time to first Localized after the snap, the
// share of time Localized, wall violations (an estimate on unwalkable floor: must be 0) and relocalizations (times
// the state went Lost). Walks are split by their place in name order: odd places tune, even places test; tune on
// the one, report the other.
public static partial class ReplayEvaluator
{
    public sealed class Walk
    {
        public string name;
        public IList<string> lines;
    }

    public sealed class CheckpointResult
    {
        public string walk, config, split, checkpoint, state;
        public double time;
        public float error;
    }

    public sealed class Summary
    {
        public string config, split;
        public int walks, checkpoints, violations, relocalizations;
        public float median, p75, p95, max, firstLocalized, localizedShare;
        public float[] cdf;
    }

    /// <summary>The CDF's points: the share of checkpoints within each of these errors, metres.</summary>
    public static readonly float[] CdfPoints = { 0.5f, 1f, 2f, 3f, 5f };

    // Runs every config over every walk. Results and summaries come back; written as CSV if folder is set.
    public static List<Summary> Run(FloorPlan plan, Walkable walkable, DistanceField field, WallMatcher walls,
        IList<Walk> walks, string folder, out List<CheckpointResult> results)
    {
        results = new List<CheckpointResult>();
        var summaries = new List<Summary>();
        var ordered = walks.OrderBy(w => w.name, StringComparer.Ordinal).ToList();
        foreach (LocalizerEngine.Config config in LocalizerEngine.Config.Evaluated())
        {
            var perWalk = new List<(string split, WalkStats stats)>();
            for (int i = 0; i < ordered.Count; i++)
            {
                string split = i % 2 == 1 ? "tune" : "test";
                WalkStats stats = Replay(plan, walkable, field, walls, config, ordered[i], split, results);
                perWalk.Add((split, stats));
            }
            foreach (string split in new[] { "tune", "test", "all" })
            {
                var chosen = perWalk.Where(w => split == "all" || w.split == split).Select(w => w.stats).ToList();
                if (chosen.Count > 0)
                    summaries.Add(Summarize(config.name, split, chosen));
            }
        }
        if (!string.IsNullOrEmpty(folder))
            Write(folder, results, summaries);
        return summaries;
    }

    sealed class WalkStats
    {
        public readonly List<float> errors = new List<float>();
        public float firstLocalized = float.NaN, localized, tracked;
        public int violations, relocalizations;
    }

    static WalkStats Replay(FloorPlan plan, Walkable walkable, DistanceField field, WallMatcher walls,
        LocalizerEngine.Config config, Walk walk, string split, List<CheckpointResult> results)
    {
        var stats = new WalkStats();
        var engine = new LocalizerEngine(plan, walkable, field, walls, config, 1);
        var feeder = new Feeder(engine);
        double snapAt = double.NaN, last = double.NaN;
        foreach (string line in walk.lines)
        {
            if (!feeder.Feed(line, out string[] c, out double t))
                continue;
            switch (c[0])
            {
                case "n":
                    snapAt = t;
                    break;
                case "a":
                    if (engine.State != LocalizerState.Outdoors)
                    {
                        if (walkable != null && !walkable.IsWalkable(engine.Position))
                            stats.violations++;
                        if (!double.IsNaN(last))
                        {
                            float dt = (float)(t - last);
                            stats.tracked += dt;
                            if (engine.State == LocalizerState.Localized)
                                stats.localized += dt;
                        }
                        if (engine.State == LocalizerState.Localized && float.IsNaN(stats.firstLocalized) && !double.IsNaN(snapAt))
                            stats.firstLocalized = (float)(t - snapAt);
                        last = t;
                    }
                    break;
                case "c":   // c,t,door,x,z
                    if (engine.State == LocalizerState.Outdoors)
                        break;
                    float error = Vector2.Distance(engine.Position, V(c, 3));
                    stats.errors.Add(error);
                    results.Add(new CheckpointResult
                    {
                        walk = walk.name, config = engine.Settings.name, split = split, checkpoint = c[2], time = t,
                        error = error, state = engine.State.ToString(),
                    });
                    break;
            }
        }
        stats.relocalizations = engine.Relocalizations;
        return stats;
    }

    // Feeds a walk log's input rows to an engine, one line at a time: what the phone did, in the same order.
    public sealed class Feeder
    {
        readonly LocalizerEngine m_Engine;
        readonly Vector2[] m_Scan = new Vector2[ScanMatcher.MaxPoints + ScanBuffer.Target];
        readonly List<WallObservation> m_Planes = new List<WallObservation>();
        readonly List<Vector2> m_Alternatives = new List<Vector2>();
        readonly List<float> m_AlternativeYaws = new List<float>();
        readonly List<Vector3> m_History = new List<Vector3>();

        public Feeder(LocalizerEngine engine) => m_Engine = engine;

        // One line: false for comments and anything without a time; c and t out for the caller's own rows.
        public bool Feed(string line, out string[] c, out double t)
        {
            c = null;
            t = 0;
            if (line.Length < 2 || line[0] == '#')
                return false;
            c = line.Split(',');
            if (c.Length < 2 || !double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                return false;
            switch (c[0])
            {
                case "n":   // n,t,door,x,z,yaw,camx,camz[,k,(x,z,yaw)...[,h,(dt,x,z)...]]
                {
                    m_Alternatives.Clear();
                    m_AlternativeYaws.Clear();
                    int k = c.Length > 8 ? int.Parse(c[8], CultureInfo.InvariantCulture) : 0;
                    for (int j = 0; j < k; j++)
                    {
                        m_Alternatives.Add(V(c, 9 + 3 * j));
                        m_AlternativeYaws.Add(F(c[11 + 3 * j]));
                    }
                    // [,h,(dt,x,z)*h]: the camera's track before the snap (dt seconds before it, negative).
                    m_History.Clear();
                    int at = 9 + 3 * k;
                    int h = c.Length > at ? int.Parse(c[at], CultureInfo.InvariantCulture) : 0;
                    for (int j = 0; j < h; j++)
                        m_History.Add(new Vector3(F(c[at + 2 + 3 * j]), F(c[at + 3 + 3 * j]), F(c[at + 1 + 3 * j])));
                    m_Engine.Snap(t, V(c, 3), F(c[5]), V(c, 6), m_Alternatives, m_AlternativeYaws, m_History);
                    break;
                }
                case "l":
                    m_Engine.Relocalize(t);
                    break;
                case "o":
                    m_Engine.Outdoors(t);
                    break;
                case "a":   // a,t,tracking,cx,cy,cz,yaw
                    m_Engine.Pose(t, new Vector2(F(c[3]), F(c[5])), F(c[6]), c[2] == "1");
                    break;
                case "d":   // d,t,source,n,x,z,...
                {
                    int n = Mathf.Min(int.Parse(c[3], CultureInfo.InvariantCulture), m_Scan.Length);
                    for (int j = 0; j < n; j++)
                        m_Scan[j] = V(c, 4 + 2 * j);
                    m_Engine.Scan(t, m_Scan, n);
                    break;
                }
                case "p":   // p,t,n,(cx,cz,dx,dz,nx,nz,length)...
                {
                    m_Planes.Clear();
                    int n = int.Parse(c[2], CultureInfo.InvariantCulture);
                    for (int j = 0; j < n; j++)
                    {
                        int at = 3 + 7 * j;
                        m_Planes.Add(new WallObservation
                        {
                            centre = V(c, at), direction = V(c, at + 2), normal = V(c, at + 4), length = F(c[at + 6]),
                        });
                    }
                    m_Engine.Walls(t, m_Planes);
                    break;
                }
                case "s":   // s,t,text,camx,camz,bearing,range,sigma,measured,...
                    m_Engine.Sighting(t, c[2], V(c, 3), F(c[5]), F(c[6]), F(c[7]));
                    break;
                case "m":   // m,t,compass
                    m_Engine.Compass(t, F(c[2]));
                    break;
                case "k":   // k,t,compass,length
                    m_Engine.Step(t, F(c[2]), F(c[3]));
                    break;
                case "q":   // q,t,max
                    m_Engine.LimitParticles(int.Parse(c[2], CultureInfo.InvariantCulture));
                    break;
            }
            return true;
        }
    }

    static Summary Summarize(string config, string split, List<WalkStats> walks)
    {
        var errors = walks.SelectMany(w => w.errors).OrderBy(e => e).ToList();
        var firsts = walks.Select(w => w.firstLocalized).Where(f => !float.IsNaN(f)).OrderBy(f => f).ToList();
        float tracked = walks.Sum(w => w.tracked);
        return new Summary
        {
            config = config,
            split = split,
            walks = walks.Count,
            checkpoints = errors.Count,
            median = Percentile(errors, 0.5f),
            p75 = Percentile(errors, 0.75f),
            p95 = Percentile(errors, 0.95f),
            max = errors.Count > 0 ? errors[errors.Count - 1] : float.NaN,
            cdf = CdfPoints.Select(x => errors.Count > 0 ? errors.Count(e => e <= x) / (float)errors.Count : float.NaN).ToArray(),
            firstLocalized = firsts.Count > 0 ? Percentile(firsts, 0.5f) : float.NaN,
            localizedShare = tracked > 0f ? walks.Sum(w => w.localized) / tracked : float.NaN,
            violations = walks.Sum(w => w.violations),
            relocalizations = walks.Sum(w => w.relocalizations),
        };
    }

    // Linear interpolation between order statistics.
    public static float Percentile(List<float> sorted, float q)
    {
        if (sorted.Count == 0)
            return float.NaN;
        float at = q * (sorted.Count - 1);
        int lo = Mathf.FloorToInt(at), hi = Mathf.Min(lo + 1, sorted.Count - 1);
        return Mathf.Lerp(sorted[lo], sorted[hi], at - lo);
    }

    static void Write(string folder, List<CheckpointResult> results, List<Summary> summaries)
    {
        Directory.CreateDirectory(folder);
        var csv = new StringBuilder("walk,split,config,checkpoint,time,error_m,state\n");
        foreach (CheckpointResult r in results)
            csv.Append(r.walk).Append(',').Append(r.split).Append(',').Append(r.config).Append(',').Append(r.checkpoint)
                .Append(',').Append(S(r.time, 3)).Append(',').Append(S(r.error, 3)).Append(',').Append(r.state).Append('\n');
        File.WriteAllText(Path.Combine(folder, "results.csv"), csv.ToString());

        var summary = new StringBuilder("config,split,walks,checkpoints,median_m,p75_m,p95_m,max_m");
        foreach (float x in CdfPoints)
            summary.Append(",cdf_").Append(S(x, 1)).Append("m");
        summary.Append(",first_localized_s,localized_share,wall_violations,relocalizations\n");
        foreach (Summary s in summaries)
        {
            summary.Append(s.config).Append(',').Append(s.split).Append(',').Append(s.walks).Append(',').Append(s.checkpoints)
                .Append(',').Append(S(s.median, 2)).Append(',').Append(S(s.p75, 2)).Append(',').Append(S(s.p95, 2))
                .Append(',').Append(S(s.max, 2));
            foreach (float v in s.cdf)
                summary.Append(',').Append(S(v, 3));
            summary.Append(',').Append(S(s.firstLocalized, 1)).Append(',').Append(S(s.localizedShare, 3)).Append(',')
                .Append(s.violations).Append(',').Append(s.relocalizations).Append('\n');
        }
        File.WriteAllText(Path.Combine(folder, "summary.csv"), summary.ToString());
    }

    /// <summary>The summaries as a fixed-width table, for logs and reports.</summary>
    public static string Table(IEnumerable<Summary> summaries)
    {
        var text = new StringBuilder();
        text.AppendLine("config         split walks  n    median  p75    p95    max    <1m   <2m   TTFL  %loc  viol reloc");
        foreach (Summary s in summaries)
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-14} {1,-5} {2,5} {3,4} {4,6:F2} {5,6:F2} {6,6:F2} {7,6:F2} {8,5:F2} {9,5:F2} {10,5:F1} {11,5:F2} {12,4} {13,5}",
                s.config, s.split, s.walks, s.checkpoints, s.median, s.p75, s.p95, s.max, s.cdf[1], s.cdf[2],
                s.firstLocalized, s.localizedShare, s.violations, s.relocalizations));
        return text.ToString();
    }

    static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    static Vector2 V(string[] c, int at) => new Vector2(F(c[at]), F(c[at + 1]));
    static string S(double v, int decimals) => double.IsNaN(v) ? "" : v.ToString("F" + decimals, CultureInfo.InvariantCulture);
}
