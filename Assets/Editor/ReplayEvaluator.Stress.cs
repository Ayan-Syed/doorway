using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// ReplayEvaluator, continued: stress walks (SyntheticWalks.Stress), scored against the truth they carry (T rows,
// five times a second) rather than at checkpoints. Per tier and configuration:
// - the error while not Lost (median, p75, p95, max): what the user is shown as a position;
// - the share of time Lost (the app says so and asks for a room number);
// - the share of time confident and wrong: Localized with the error over 3 m, the most dangerous outcome;
// - the share of time Localized ("sure"), and the displayed error: the error while Localized, when the UI shows the
//   position with confidence (p95, max);
// - Spread's calibration: the share of time the error is within Spread, which should be about 68%;
// - wall violations, and how long after a plate read while Lost the error falls under 1.5 m.
public static partial class ReplayEvaluator
{
    public sealed class StressWalk
    {
        public string name, info;
        public SyntheticWalks.Tier tier;
        public IList<string> lines;
    }

    public sealed class StressWalkResult
    {
        public string walk, config, info;
        public SyntheticWalks.Tier tier;
        public float p95, max, lostShare, wrongShare, calibration;
        public int samples, violations;
        public double firstWrongAt = double.NaN;
        public readonly List<float> errors = new List<float>();   // while not Lost
        public readonly List<float> localizedErrors = new List<float>();
        // What the user sees (GuidanceSim): walking time, the part with normal guidance and its errors, the walk's
        // length, and each "Finding your exact spot…" spell's length.
        public float walkSeconds, guidedSeconds, walked;
        public readonly List<float> guidedErrors = new List<float>();
        public readonly List<float> interruptions = new List<float>();
        public readonly List<float> recoveries = new List<float>();
        public int lost, wrong, within, judged;
    }

    public sealed class StressSummary
    {
        public string config;
        public SyntheticWalks.Tier tier;
        public int walks, violations;
        public float median, p75, p95, max, lostShare, wrongShare, calibration, recoveryMedian, recoveryMax;
        public float localizedShare, localizedP95, localizedMax;
        public float guidance, guidedP75, guidedP95, guidedMax, interruptionsPer100m, interruptionMedian, interruptionP90;
        public int recoveries, slowRecoveries;
    }

    /// <summary>The guidance the UI would show (NavigationScreen.JudgeGuidance, mirrored here: keep the two alike):
    /// normal guidance unless Lost (at once) or Converging with Spread past UnsureEnter, back under UnsureLeave, each
    /// change held GuidanceHold seconds; out of Lost at once.</summary>
    public sealed class GuidanceSim
    {
        public const float UnsureEnter = 2.5f, UnsureLeave = 2f, GuidanceHold = 1f;
        enum Guidance { Sure, Unsure, Lost }
        Guidance m_Guidance;
        double m_Pending = -1;

        /// <summary>Whether normal guidance shows at time t.</summary>
        public bool Judge(LocalizerState state, float spread, double t)
        {
            if (state == LocalizerState.Lost)
            {
                m_Pending = -1;
                m_Guidance = Guidance.Lost;
                return false;
            }
            bool wide = state == LocalizerState.Converging &&
                        (m_Guidance == Guidance.Unsure ? spread >= UnsureLeave : spread > UnsureEnter);
            Guidance want = wide ? Guidance.Unsure : Guidance.Sure;
            if (want == m_Guidance)
                m_Pending = -1;
            else if (m_Guidance == Guidance.Lost)
            {
                m_Guidance = want;
                m_Pending = -1;
            }
            else if (m_Pending < 0)
                m_Pending = t;
            else if (t - m_Pending >= GuidanceHold)
            {
                m_Guidance = want;
                m_Pending = -1;
            }
            return m_Guidance == Guidance.Sure;
        }
    }

    // Walking: the truth moved more than this between two truth rows (a fifth of a second).
    const float MovingStep = 0.05f;

    /// <summary>The error over which Localized counts as confidently wrong, metres.</summary>
    public const float WrongDistance = 3f;

    public static StressWalkResult ReplayStress(FloorPlan plan, Walkable walkable, DistanceField field, WallMatcher walls,
        LocalizerEngine.Config config, StressWalk walk, int seed = 1)
    {
        var result = new StressWalkResult { walk = walk.name, config = config.name, info = walk.info, tier = walk.tier };
        var engine = new LocalizerEngine(plan, walkable, field, walls, config, seed);
        var feeder = new Feeder(engine);
        double signWhileLost = double.NaN, lastT = double.NaN, unsureSince = double.NaN;
        Vector2 lastTruth = default;
        var guidance = new GuidanceSim();
        foreach (string line in walk.lines)
        {
            bool lost = engine.State == LocalizerState.Lost;
            if (lost && double.IsNaN(signWhileLost) && line.StartsWith("s,", StringComparison.Ordinal))
                signWhileLost = double.Parse(line.Split(',')[1], CultureInfo.InvariantCulture);
            if (!feeder.Feed(line, out string[] c, out double t))
                continue;
            if (c[0] == "a" && engine.State != LocalizerState.Outdoors && walkable != null && !walkable.IsWalkable(engine.Position))
                result.violations++;
            if (c[0] != "T" || engine.State == LocalizerState.Outdoors)
                continue;
            var truth = new Vector2(float.Parse(c[2], CultureInfo.InvariantCulture), float.Parse(c[3], CultureInfo.InvariantCulture));
            float error = Vector2.Distance(engine.Position, truth);
            result.samples++;
            bool sure = guidance.Judge(engine.State, engine.Spread, t);
            if (!double.IsNaN(lastT))
            {
                float step = Vector2.Distance(truth, lastTruth), dt = (float)(t - lastT);
                result.walked += step;
                if (step > MovingStep)
                {
                    result.walkSeconds += dt;
                    if (sure)
                    {
                        result.guidedSeconds += dt;
                        result.guidedErrors.Add(error);
                    }
                }
            }
            if (!sure && double.IsNaN(unsureSince))
                unsureSince = t;
            else if (sure && !double.IsNaN(unsureSince))
            {
                result.interruptions.Add((float)(t - unsureSince));
                unsureSince = double.NaN;
            }
            lastT = t;
            lastTruth = truth;
            if (engine.State == LocalizerState.Lost)
            {
                result.lost++;
                continue;
            }
            result.errors.Add(error);
            result.judged++;
            if (error <= Mathf.Max(engine.Spread, 0.1f))
                result.within++;
            if (engine.State == LocalizerState.Localized)
                result.localizedErrors.Add(error);
            if (engine.State == LocalizerState.Localized && error > WrongDistance)
            {
                result.wrong++;
                if (double.IsNaN(result.firstWrongAt))
                    result.firstWrongAt = t;
            }
            if (!double.IsNaN(signWhileLost) && error < 1.5f)
            {
                result.recoveries.Add((float)(t - signWhileLost));
                signWhileLost = double.NaN;
            }
        }
        if (!double.IsNaN(unsureSince))
            result.interruptions.Add((float)(lastT - unsureSince));
        result.errors.Sort();
        result.p95 = Percentile(result.errors, 0.95f);
        result.max = result.errors.Count > 0 ? result.errors[result.errors.Count - 1] : 0f;
        result.lostShare = result.samples > 0 ? result.lost / (float)result.samples : 0f;
        result.wrongShare = result.samples > 0 ? result.wrong / (float)result.samples : 0f;
        result.calibration = result.judged > 0 ? result.within / (float)result.judged : float.NaN;
        return result;
    }

    public static List<StressSummary> SummarizeStress(IEnumerable<StressWalkResult> results)
    {
        var summaries = new List<StressSummary>();
        foreach (var group in results.GroupBy(r => (r.config, r.tier)).OrderBy(g => g.Key.tier).ThenBy(g => g.Key.config))
        {
            var errors = group.SelectMany(r => r.errors).OrderBy(e => e).ToList();
            var recoveries = group.SelectMany(r => r.recoveries).OrderBy(e => e).ToList();
            var localized = group.SelectMany(r => r.localizedErrors).OrderBy(e => e).ToList();
            var guided = group.SelectMany(r => r.guidedErrors).OrderBy(e => e).ToList();
            var spells = group.SelectMany(r => r.interruptions).OrderBy(e => e).ToList();
            float walkSeconds = group.Sum(r => r.walkSeconds), walked = group.Sum(r => r.walked);
            int samples = group.Sum(r => r.samples), judged = group.Sum(r => r.judged);
            summaries.Add(new StressSummary
            {
                config = group.Key.config,
                tier = group.Key.tier,
                walks = group.Count(),
                median = Percentile(errors, 0.5f),
                p75 = Percentile(errors, 0.75f),
                p95 = Percentile(errors, 0.95f),
                max = errors.Count > 0 ? errors[errors.Count - 1] : float.NaN,
                lostShare = samples > 0 ? group.Sum(r => r.lost) / (float)samples : float.NaN,
                wrongShare = samples > 0 ? group.Sum(r => r.wrong) / (float)samples : float.NaN,
                calibration = judged > 0 ? group.Sum(r => r.within) / (float)judged : float.NaN,
                violations = group.Sum(r => r.violations),
                recoveries = recoveries.Count,
                recoveryMedian = Percentile(recoveries, 0.5f),
                recoveryMax = recoveries.Count > 0 ? recoveries[recoveries.Count - 1] : float.NaN,
                slowRecoveries = recoveries.Count(r => r > 5f),
                localizedShare = samples > 0 ? localized.Count / (float)samples : float.NaN,
                localizedP95 = Percentile(localized, 0.95f),
                localizedMax = localized.Count > 0 ? localized[localized.Count - 1] : float.NaN,
                guidance = walkSeconds > 0f ? group.Sum(r => r.guidedSeconds) / walkSeconds : float.NaN,
                guidedP75 = Percentile(guided, 0.75f),
                guidedP95 = Percentile(guided, 0.95f),
                guidedMax = guided.Count > 0 ? guided[guided.Count - 1] : float.NaN,
                interruptionsPer100m = walked > 0f ? 100f * spells.Count / walked : float.NaN,
                interruptionMedian = Percentile(spells, 0.5f),
                interruptionP90 = Percentile(spells, 0.9f),
            });
        }
        return summaries;
    }

    public static string StressTable(IEnumerable<StressSummary> summaries)
    {
        var text = new StringBuilder();
        text.AppendLine("tier     config         walks median  p75    p95    max    %lost  %wrong  calib  viol  %sure dispP95 dispMax recov(med/max/slow)");
        foreach (StressSummary s in summaries)
        {
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-8} {1,-14} {2,5} {3,6:F2} {4,6:F2} {5,6:F2} {6,6:F2} {7,6:F1} {8,6:F2} {9,6:F2} {10,5} {11,5:F1} {12,7:F2} {13,7:F2} {14,5:F1}/{15:F1}/{16}",
                s.tier, s.config, s.walks, s.median, s.p75, s.p95, s.max, 100f * s.lostShare, 100f * s.wrongShare,
                s.calibration, s.violations, 100f * s.localizedShare, s.localizedP95, s.localizedMax, s.recoveryMedian,
                s.recoveryMax, s.slowRecoveries));
        }
        return text.ToString();
    }

    /// <summary>Every walk through every evaluated configuration, in parallel (a wall matcher per thread, from
    /// newWalls: it keeps scratch state); writes folder/results.csv and summary.txt, and returns the summaries.</summary>
    public static List<StressSummary> RunStress(FloorPlan plan, Walkable walkable, DistanceField field,
        Func<WallMatcher> newWalls, IList<StressWalk> walks, string folder, out List<StressWalkResult> results)
    {
        var bag = new System.Collections.Concurrent.ConcurrentBag<StressWalkResult>();
        var jobs = LocalizerEngine.Config.Evaluated().SelectMany(c => walks.Select(w => (c, w))).ToList();
        using (var matchers = new ThreadLocal<WallMatcher>(newWalls))
            Parallel.ForEach(jobs, job => bag.Add(ReplayStress(plan, walkable, field, matchers.Value, job.c, job.w)));
        results = bag.OrderBy(r => r.walk).ThenBy(r => r.config).ToList();
        List<StressSummary> summaries = SummarizeStress(results);
        WriteStress(folder, results, summaries);
        return summaries;
    }

    public static void WriteStress(string folder, List<StressWalkResult> results, List<StressSummary> summaries)
    {
        Directory.CreateDirectory(folder);
        var csv = new StringBuilder("walk,tier,config,p95_m,max_m,lost_share,wrong_share,calibration,violations,info\n");
        foreach (StressWalkResult r in results)
            csv.Append(r.walk).Append(',').Append(r.tier).Append(',').Append(r.config).Append(',').Append(S(r.p95, 2))
                .Append(',').Append(S(r.max, 2)).Append(',').Append(S(r.lostShare, 3)).Append(',').Append(S(r.wrongShare, 3))
                .Append(',').Append(S(r.calibration, 3)).Append(',').Append(r.violations).Append(',').Append(r.info).Append('\n');
        File.WriteAllText(Path.Combine(folder, "results.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(folder, "summary.txt"), StressTable(summaries));
    }
}
