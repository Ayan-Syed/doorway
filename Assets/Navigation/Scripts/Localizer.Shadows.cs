using System;
using System.Collections.Generic;
using UnityEngine;

// The snap's other doors as shadow hypotheses: a sequential likelihood-ratio test (Wald 1945), as multi-hypothesis
// tracking keeps rival tracks (Reid 1979). At a snap, the other doors whose surroundings look most like the snapped
// one's (MaxShadows of them) each get a small filter of their own, fed the same motion, scans, steps and plates; each
// hypothesis keeps the log of the likelihood of everything seen so far under it (scans' marginals, damped by
// ScanLogShare, plates' marginals, and the share of the cloud the walk didn't put through a wall), the snapped door
// starting SnapPriorLog ahead. The snap isn't sure until it leads every shadow by DecideLog (about 20 to 1) after
// ShadowWalk metres of walking with the camera up; a shadow that leads it by as much takes its place. (The main cloud
// keeps its own small share at the other doors too: in the stress walks, shadows alone, without it, were wrong more.) Still undecided after AmbiguousWalk metres: AskReason "AmbiguousStart" (a plate decides it).
// A room door's own plate read close by soon after the snap confirms it; a nearby room's plate corrects it to that
// room's door (LastSnapCheck).
public sealed partial class LocalizerEngine
{
    public enum SnapCheck { None, Confirmed, Corrected }

    sealed class Shadow
    {
        public PositionFilter filter;
        public Vector2 spot;
        public float yaw;
        public string text;   // the room's plate beside this door (null for an entrance)
        public double log;    // scans' and plates' log likelihood (the filter keeps the motion's)
    }

    const int MaxShadows = 3, ShadowParticles = 80;
    const float DecideLog = 3f, ShadowWalk = 5f, AmbiguousWalk = 25f;
    // The snapped door starts SnapPriorLog ahead of each shadow (the user picked it: wrong about one time in ten). A
    // shadow whose estimate comes within MergeDistance of the snap's agrees with it on where the user is: dropped.
    const float SnapPriorLog = 1.5f, MergeDistance = 2f;
    // Scans weighed every half metre overlap (their two seconds of points come round again) and their errors are
    // alike from one to the next (clutter, the plan's own errors): each counts ScanLogShare in the test.
    const float ScanLogShare = 0.1f;
    // The snap's plate check: within CheckSeconds of a snap at a room door, a plate read within CheckRange.
    // The plate fits a cloud if Σ weight × likelihood is at least SeenShare.
    const float CheckSeconds = 10f, CheckRange = 3f, SeenShare = 0.05f;
    // Surroundings compared over a SimilarityReach-metre square round the camera, every SimilarityStep metres.
    const float SimilarityReach = 5f, SimilarityStep = 1f;

    readonly List<Shadow> m_Shadows = new List<Shadow>();
    readonly List<Shadow> m_Others = new List<Shadow>();   // every other door offered (for the plate check)
    double m_MainLog;
    float m_ShadowWalked;
    Vector2 m_ShadowFrom;
    string m_SnapText;
    readonly DistanceField m_Field;

    /// <summary>Why the user is asked for a room number besides being Lost: "AmbiguousStart" when the walk hasn't told
    /// the snapped door from a look-alike one after AmbiguousWalk metres. Null otherwise.</summary>
    public string AskReason { get; private set; }
    /// <summary>What a plate read beside the snapped room door said: confirmed it, or corrected it (to SnapCheckText).</summary>
    public SnapCheck LastSnapCheck { get; private set; }
    public string SnapCheckText { get; private set; }
    /// <summary>The log likelihood ratio of the snap over each shadow, and metres walked camera-up (for diagnostics).</summary>
    public string ShadowState => m_Shadows.Count == 0 ? "-" :
        $"{m_ShadowWalked:F0}m " + string.Join(" ", m_Shadows.ConvertAll(sh => (MainLog - LogOf(sh)).ToString("F1")));
    /// <summary>Shadow hypotheses still running.</summary>
    public int Shadows => m_Shadows.Count;

    void StartShadows(Vector2 spot, Vector2 camera, IReadOnlyList<Vector2> alternatives, IReadOnlyList<float> yaws)
    {
        DropShadows();
        m_Others.Clear();
        LastSnapCheck = SnapCheck.None;
        SnapCheckText = null;
        m_SnapText = DoorText(spot);
        m_MainLog = 0.0;
        m_Filter.MotionLog = 0.0;
        m_ShadowWalked = 0f;
        m_ShadowFrom = camera;
        int others = alternatives?.Count ?? 0;
        var ranked = new List<(float score, int k)>();
        for (int k = 0; k < others; k++)
        {
            m_Others.Add(new Shadow { spot = alternatives[k], yaw = yaws[k], text = DoorText(alternatives[k]) });
            ranked.Add((Dissimilarity(spot, m_SnapYaw, alternatives[k], yaws[k]), k));
        }
        ranked.Sort((a, b) => a.score.CompareTo(b.score));
        for (int r = 0; r < ranked.Count && r < MaxShadows; r++)
        {
            Shadow s = m_Others[ranked[r].k];
            s.filter = new PositionFilter(m_Walkable, ShadowParticles, 101 + r, ShadowParticles);
            s.filter.Reset(s.spot, camera, s.yaw, SnapSpread, SnapHeadingSpread, SnapHeadingLimit, true);
            // The same make-up as the snap's own cloud (a share a few metres on), so neither is favoured by its shape.
            s.filter.SeedNear(0, Mathf.RoundToInt(ShadowParticles * NearShare), s.spot, s.yaw, NearFrom, NearReach,
                SnapHeadingSpread);
            s.filter.MotionLog = 0.0;
            s.log = 0.0;
            m_Shadows.Add(s);
        }
    }

    // How unlike the snapped door's surroundings another door's are, as the camera would see them: the mean
    // difference of the distance to the nearest wall at the same world offsets, under each door's yaw (capped at 2 m).
    float Dissimilarity(Vector2 spot, float yaw, Vector2 other, float otherYaw)
    {
        if (m_Field == null)
            return 0f;
        float sum = 0f;
        int n = 0;
        for (float x = -SimilarityReach; x <= SimilarityReach; x += SimilarityStep)
        {
            for (float z = -SimilarityReach; z <= SimilarityReach; z += SimilarityStep)
            {
                var offset = new Vector2(x, z);
                Vector2 a = spot + PositionFilter.ToPlan(offset, yaw), b = other + PositionFilter.ToPlan(offset, otherYaw);
                float da = Mathf.Min(m_Field.Centimetres(a.x, a.y), 200), db = Mathf.Min(m_Field.Centimetres(b.x, b.y), 200);
                sum += Mathf.Abs(da - db);
                n++;
            }
        }
        return n > 0 ? sum / n : 0f;
    }

    // The room plate beside the door at spot (null for an entrance, or a door without one).
    string DoorText(Vector2 spot)
    {
        if (m_Plan?.places == null)
            return null;
        foreach (PlanPlace place in m_Plan.places)
        {
            if (place.kind == "exit")
                continue;
            foreach (PlanDoor door in place.doors)
            {
                if ((m_Plan.NodePosition(door.node) - spot).sqrMagnitude > 0.01f)
                    continue;
                foreach (PlanSign sign in m_Plan.signs)
                    if (sign.door == door.node && !string.IsNullOrEmpty(sign.text))
                        return sign.text.Trim();
                return null;
            }
        }
        return null;
    }

    void DropShadows()
    {
        m_Shadows.Clear();
        AskReason = null;
    }

    void FollowShadows(Vector2 camera, float cameraYaw, bool tracking)
    {
        foreach (Shadow s in m_Shadows)
            s.filter.Follow(camera, cameraYaw, tracking);
    }

    void StepShadows(float heading, float length, float sigma)
    {
        foreach (Shadow s in m_Shadows)
            s.filter.StepBlindWorld(heading, length, StepLengthSigma, sigma);
    }

    // Before the main cloud weighs a scan (so the matcher's statistics are the main cloud's after).
    void ScanShadows(Vector2[] points, int count, float strength)
    {
        foreach (Shadow s in m_Shadows)
            if (m_Scan.Weigh(s.filter, points, count, strength))
                s.log += ScanLogShare * m_Scan.LastLogMarginal;
    }

    // After the main cloud weighed a scan.
    void ScannedMain()
    {
        if (m_Shadows.Count == 0)
            return;
        m_MainLog += ScanLogShare * m_Scan.LastLogMarginal;
        m_ShadowWalked += Vector2.Distance(m_Camera, m_ShadowFrom);
        m_ShadowFrom = m_Camera;
    }

    // A plate: each shadow weighs it robustly, never starting again from it; the main cloud's log takes its marginal.
    void PlateShadows(float bearing, float range, float sigma, List<Vector2> at, List<Vector2> normals)
    {
        if (m_Shadows.Count == 0)
            return;
        m_MainLog += Math.Log(PlateOutlier + m_Filter.LastSignTotal);
        foreach (Shadow s in m_Shadows)
        {
            s.filter.SeedOnContradiction = false;
            s.filter.PlateOutlier = PlateOutlier;
            s.filter.AddSignObservation(bearing, range, sigma, at, normals);
            s.filter.SeedOnContradiction = null;
            s.filter.PlateOutlier = 0f;
            s.log += Math.Log(PlateOutlier + s.filter.LastSignTotal);
        }
    }

    double MainLog => SnapPriorLog + m_MainLog + m_Filter.MotionLog;
    static double LogOf(Shadow s) => s.log + s.filter.MotionLog;

    // The test, after each update: drop the shadows the snap leads by DecideLog, swap in one that leads it by as much.
    void DecideShadows()
    {
        if (m_Shadows.Count == 0)
            return;
        m_Shadows.RemoveAll(s => (s.filter.Estimate - m_Filter.Estimate).sqrMagnitude < MergeDistance * MergeDistance);
        if (m_Shadows.Count == 0)
        {
            AskReason = null;
            return;
        }
        if (m_ShadowWalked < ShadowWalk)
            return;
        Shadow best = null;
        foreach (Shadow s in m_Shadows)
            if (LogOf(s) - MainLog >= DecideLog && (best == null || LogOf(s) > LogOf(best)))
                best = s;
        if (best != null)
            SwapIn(best);
        m_Shadows.RemoveAll(s => MainLog - LogOf(s) >= DecideLog);
        if (m_Shadows.Count == 0)
            AskReason = null;
        else if (m_ShadowWalked >= AmbiguousWalk)
            AskReason = "AmbiguousStart";
    }

    // The shadow's door was the right one: its cloud becomes the main one, and the snapped door's the shadow.
    void SwapIn(Shadow s)
    {
        var main = new PositionFilter(m_Walkable, ShadowParticles, 97, ShadowParticles);
        main.CopyFrom(m_Filter);
        double mainLog = m_MainLog;
        m_Filter.CopyFrom(s.filter);
        // The prior goes with the hypothesis, not the slot: the old snap keeps it.
        m_MainLog = s.log - SnapPriorLog;
        s.filter = main;
        s.log = mainLog + SnapPriorLog;
        (s.spot, m_SnapSpot) = (m_SnapSpot, s.spot);
        (s.yaw, m_SnapYaw) = (m_SnapYaw, s.yaw);
        (s.text, m_SnapText) = (m_SnapText, s.text);
        m_HadEstimate = false;
        m_SureSince = -1;
        StateChanged?.Invoke(State, "other door");
    }

    // The plate check at a room-door snap, after the plate was weighed by every hypothesis: the snapped room's own
    // plate, close by, that the snap's cloud could have seen and no shadow could confirms it; a nearby room's plate that it couldn't have
    // seen but that room's door's shadow could moves the snap to that door. (A plate is only evidence of a door if it
    // fits the pose at that door: with their back to the door the user sees the doors across the hallway, not theirs.)
    bool CheckSnap(double t, string key, float worldRange)
    {
        if (m_SnapText == null || LastSnapCheck != SnapCheck.None || t - m_SnapTime > CheckSeconds ||
            worldRange > CheckRange || State == LocalizerState.Lost)
            return false;
        if (key == m_SnapText && m_Filter.LastSignTotal >= SeenShare &&
            m_Shadows.TrueForAll(o => o.filter.LastSignTotal < SeenShare))
        {
            LastSnapCheck = SnapCheck.Confirmed;
            DropShadows();
            m_Evidence += EvidenceNeeded;
            return true;
        }
        Shadow other = m_Shadows.Find(o => o.text == key);
        if (other == null || m_Filter.LastSignTotal >= SeenShare || other.filter.LastSignTotal < SeenShare)
            return false;
        SwapIn(other);
        LastSnapCheck = SnapCheck.Corrected;
        SnapCheckText = key;
        DropShadows();
        return true;
    }
}
