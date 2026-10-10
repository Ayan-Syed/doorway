using System.Collections.Generic;
using UnityEngine;

// Localizer, continued: outdoors with GPS. Which entrance the user is nearest (EntranceGuess), when GPS says they
// just walked in (EntrancePrompt), and, indoors near an exit, when GPS says they've walked out (GoOutdoors).
// GPS indoors is tens of metres off or gone, so walking in shows as a good fix near the building that then falls
// apart; walking out, as a good fix again, clearly outside.
public partial class Localizer
{
    // Walking in: a good fix (under GoodAccuracy) within NearBuilding of the building's outline in the last
    // NearMemory seconds, then CollapseSeconds of a bad one (worse than BadAccuracy) or none.
    const float NearBuilding = 25f, GoodAccuracy = 15f, BadAccuracy = 25f;
    const float CollapseSeconds = 5f, NearMemory = 60f;
    // A fix older than this has stopped coming.
    const float FixFresh = 3f, FixStopped = 5f;
    // Walking out: within ExitNear of an exit's spot (GPS is woken), then a good fix this far outside the outline,
    // more than its own accuracy, for LeaveSeconds.
    const float ExitNear = 4f, OutsideBy = 5f, LeaveSeconds = 3f;
    const float GuessSeconds = 1f;
    const int GpsHistory = 60;

    bool m_PromptDismissed;
    float m_NearAt = -1000f, m_BadSince = -1f, m_OutsideSince = -1f, m_NextGuess;
    readonly Queue<(double time, Vector2 at, float accuracy)> m_Gps = new Queue<(double, Vector2, float)>();

    void UpdateOutdoors()
    {
        if (m_Location == null)
            return;
        float now = Time.unscaledTime;
        bool fix = m_Location.HasFix && m_Location.FixAge < FixStopped;
        Vector2 at = m_Location.Position;
        float accuracy = m_Location.Accuracy;
        if (fix && now >= m_NextGuess)
        {
            m_NextGuess = now + GuessSeconds;
            EntranceGuess = NearestExit(at, out _);
            RememberGps(at, accuracy);
        }
        bool good = fix && accuracy < GoodAccuracy && m_Location.FixAge < FixFresh;
        float outside = good ? OutsideDistance(at) : float.MaxValue;
        if (good && outside < NearBuilding)
            m_NearAt = now;
        if (good && outside > 2f * NearBuilding)
            m_PromptDismissed = false;   // walked away: the next time in may be offered again
        bool bad = !fix || accuracy > BadAccuracy;
        if (bad && now - m_NearAt < NearMemory)
        {
            if (m_BadSince < 0f)
                m_BadSince = now;
            if (now - m_BadSince >= CollapseSeconds && !m_PromptDismissed && EntranceGuess != null)
                EntrancePrompt = true;
        }
        else
        {
            m_BadSince = -1f;
            if (good && outside > ExitNear)
                EntrancePrompt = false;   // a good fix outside again: not in after all
        }
    }

    // Indoors, once a second: near an exit, GPS wakes; a good fix clearly outside for a few seconds, and it's outdoors.
    void UpdateLeaving()
    {
        if (m_Location == null || navigator.Plan == null || Time.unscaledTime < m_NextGuess)
            return;
        m_NextGuess = Time.unscaledTime + GuessSeconds;
        NearestExit(Position, out float distance);
        bool near = State != LocalizerState.Lost && distance <= ExitNear;
        m_Location.Needed = near;
        bool outside = near && m_Location.HasFix && m_Location.FixAge < FixFresh && m_Location.Accuracy < GoodAccuracy &&
                       OutsideDistance(m_Location.Position) > Mathf.Max(OutsideBy, m_Location.Accuracy);
        if (!outside)
        {
            m_OutsideSince = -1f;
            return;
        }
        if (m_OutsideSince < 0f)
            m_OutsideSince = Time.unscaledTime;
        if (Time.unscaledTime - m_OutsideSince >= LeaveSeconds)
        {
            Note("left: GPS outside");
            GoOutdoors();
        }
    }

    // The entrance (a place of kind "exit") whose door spot is nearest a plan point.
    PlanDoor NearestExit(Vector2 at, out float distance)
    {
        PlanDoor nearest = null;
        distance = float.MaxValue;
        foreach (PlanPlace place in navigator.Plan.places)
        {
            if (place.kind != "exit")
                continue;
            foreach (PlanDoor door in place.doors)
            {
                float d = Vector2.Distance(at, navigator.Plan.NodePosition(door.node));
                if (d < distance)
                {
                    distance = d;
                    nearest = door;
                }
            }
        }
        return nearest;
    }

    // Metres from a plan point to the building: the distance field's outdoor grid once it's built, else the
    // nearest door spot (rougher, but GPS is rougher still).
    float OutsideDistance(Vector2 at)
    {
        if (m_Field == null && m_FieldTask != null && m_FieldTask.IsCompleted && !m_FieldTask.IsFaulted)
            m_Field = m_FieldTask.Result;
        if (m_Field != null)
            return m_Field.OutsideDistance(at);
        float best = float.MaxValue;
        foreach (PlanPlace place in navigator.Plan.places)
            foreach (PlanDoor door in place.doors)
                best = Mathf.Min(best, Vector2.Distance(at, navigator.Plan.NodePosition(door.node)));
        return best;
    }

    // The last minute of GPS, written into the walk log when it opens (for tuning the entrance prompt).
    void RememberGps(Vector2 at, float accuracy)
    {
        m_Gps.Enqueue((Now, at, accuracy));
        while (m_Gps.Count > GpsHistory)
            m_Gps.Dequeue();
    }

    void FlushGpsHistory()
    {
        if (m_Log == null)
            return;
        foreach (var (time, at, accuracy) in m_Gps)
        {
            m_Log.Row('g', time);
            LogXZ(at);
            m_Log.Add(accuracy, 1);
            m_Log.EndRow();
        }
        m_Gps.Clear();
    }
}
