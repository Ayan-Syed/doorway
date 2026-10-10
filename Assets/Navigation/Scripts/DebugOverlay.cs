using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

// For the team on site: what the Localizer is thinking, over every screen. Its particles on the map (small dots,
// blue for unlikely to red for likely), a few lines of numbers, and Checkpoint: the six doors nearest the estimate as
// big chips, one tap to record "I'm at this door" as ground truth. Held on the settings gear to turn on or off.
public class DebugOverlay : MonoBehaviour
{
    public Localizer localizer;
    public Navigator navigator;
    [Tooltip("The big map: the particles are drawn in its content, under its labels.")]
    public FloorMap map;
    public Text lines;
    public Button checkpointButton;
    [Tooltip("The door chips, shown by the Checkpoint button.")]
    public GameObject checkpointRow;
    public Button[] checkpointChips;
    public Text[] checkpointLabels;

    // NavigationScreen's snackbar.
    public Action<string> Toast;

    // The numbers change four times a second: fast enough to read, slow enough not to build strings every frame.
    const float LinesSeconds = 0.25f;
    // A particle's size on screen, in canvas units.
    public const float ParticleSize = 7f;

    DebugParticles m_Particles;
    bool m_Wired;
    string m_Sign;
    float m_SignAt = -1f;
    float m_Fps = 60f;
    float m_NextLines;
    readonly List<(PlanPlace place, PlanDoor door)> m_Doors = new List<(PlanPlace place, PlanDoor door)>();
    readonly StringBuilder m_Text = new StringBuilder();

    // For the editor's previews: made-up particles and numbers.
    bool m_Previewing;
    LocalizerState m_PreviewState;
    Vector2 m_PreviewAt;
    float m_PreviewSpread, m_PreviewConfidence;
    readonly List<(Vector2 at, float weight)> m_PreviewParticles = new List<(Vector2 at, float weight)>();

    public void Show(bool on)
    {
        Wire();
        if (gameObject.activeSelf != on)
            gameObject.SetActive(on);
        if (m_Particles != null && m_Particles.gameObject.activeSelf != on)
            m_Particles.gameObject.SetActive(on);
        if (!on)
        {
            CloseCheckpoints();
            m_Previewing = false;
        }
        m_NextLines = 0f;
    }

    // True if the door chips were open (Back closes them first).
    public bool CloseCheckpoints()
    {
        if (checkpointRow == null || !checkpointRow.activeSelf)
            return false;
        checkpointRow.SetActive(false);
        return true;
    }

    public void SawSign(string text)
    {
        m_Sign = text;
        m_SignAt = Time.unscaledTime;
    }

    void Wire()
    {
        if (m_Wired)
            return;
        m_Wired = true;
        if (checkpointButton != null)
            checkpointButton.onClick.AddListener(ToggleCheckpoints);
        for (int i = 0; checkpointChips != null && i < checkpointChips.Length; i++)
        {
            int chip = i;
            checkpointChips[i].onClick.AddListener(() => Checkpoint(chip));
        }
        if (checkpointRow != null)
            checkpointRow.SetActive(false);
        if (map != null && map.content != null)
        {
            var go = new GameObject("Debug Particles", typeof(RectTransform));
            go.layer = map.content.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(map.content, false);
            rect.sizeDelta = Vector2.zero;
            m_Particles = go.AddComponent<DebugParticles>();
            m_Particles.raycastTarget = false;
            m_Particles.overlay = this;
            go.SetActive(gameObject.activeSelf);
        }
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f)
            m_Fps = Mathf.Lerp(m_Fps, 1f / dt, 0.1f);
        if (m_Particles != null)
            m_Particles.SetVerticesDirty();   // the particles move every frame
        if (Time.unscaledTime >= m_NextLines)
        {
            m_NextLines = Time.unscaledTime + LinesSeconds;
            WriteLines();
        }
    }

    LocalizerState State => m_Previewing ? m_PreviewState : localizer != null ? localizer.State : LocalizerState.Outdoors;
    Vector2 Position => m_Previewing ? m_PreviewAt : localizer != null ? localizer.Position : Vector2.zero;

    void WriteLines()
    {
        if (lines == null)
            return;
        float spread = m_Previewing ? m_PreviewSpread : localizer != null ? localizer.Spread : 0f;
        float confidence = m_Previewing ? m_PreviewConfidence : localizer != null ? localizer.Confidence : 0f;
        int walls = localizer != null && !m_Previewing ? localizer.WallsMatched : 12;
        float digits = localizer != null && !m_Previewing ? localizer.PlateDigitHeight : 0.05f;
        bool tracking = navigator != null && navigator.IsTracking || m_Previewing;
        m_Text.Clear();
        m_Text.Append(State).Append(" · confidence ").Append(confidence.ToString("0.00"))
              .Append(" · spread ").Append(spread.ToString("0.0")).Append(" m\n");
        m_Text.Append("walls matched ").Append(walls).Append(" · particles ").Append(ParticleTotal()).Append('\n');
        if (m_Sign == null)
            m_Text.Append("sign —\n");
        else
            m_Text.Append("sign ").Append(m_Sign).Append(" · ")
                  .Append(Mathf.RoundToInt(Time.unscaledTime - m_SignAt)).Append(" s ago\n");
        m_Text.Append("plate digits ").Append((digits * 100f).ToString("0.0")).Append(" cm\n");
        m_Text.Append("tracking ").Append(tracking ? "yes" : "no").Append(" · ").Append(Mathf.RoundToInt(m_Fps)).Append(" fps");
        lines.text = m_Text.ToString();
    }

    public int ParticleTotal() => m_Previewing ? m_PreviewParticles.Count : localizer != null ? localizer.ParticleCount : 0;

    public void Particle(int i, out Vector2 at, out float weight)
    {
        if (m_Previewing)
        {
            (at, weight) = m_PreviewParticles[i];
            return;
        }
        localizer.Particle(i, out at, out weight);
    }

    // The six doors nearest the estimate, as chips; open or shut.
    void ToggleCheckpoints()
    {
        if (CloseCheckpoints() || checkpointRow == null || navigator == null || navigator.Plan == null)
            return;
        Vector2 at = Position;
        var doors = new List<(PlanPlace place, PlanDoor door, float distance)>();
        foreach (PlanPlace place in navigator.Plan.places)
            foreach (PlanDoor door in place.doors)
                doors.Add((place, door, Vector2.Distance(at, navigator.Plan.NodePosition(door.node))));
        doors.Sort((a, b) => a.distance.CompareTo(b.distance));
        m_Doors.Clear();
        for (int i = 0; i < doors.Count && m_Doors.Count < checkpointChips.Length; i++)
            m_Doors.Add((doors[i].place, doors[i].door));
        for (int i = 0; i < checkpointChips.Length; i++)
        {
            bool shown = i < m_Doors.Count;
            checkpointChips[i].gameObject.SetActive(shown);
            if (shown)
                checkpointLabels[i].text = PlaceStyle.DoorTitle(m_Doors[i].place, m_Doors[i].door);
        }
        checkpointRow.SetActive(true);
        Haptics.Tick(15);
    }

    void Checkpoint(int chip)
    {
        if (chip >= m_Doors.Count)
            return;
        var (place, door) = m_Doors[chip];
        if (localizer != null && Application.isPlaying)
            localizer.Checkpoint(door);
        CloseCheckpoints();
        Haptics.Tick(40);
        Toast?.Invoke($"Checkpoint {place.name}");
    }

    // A still frame for the previews: a cloud of count particles round at, as wide as spread, and the numbers.
    public void Preview(LocalizerState state, Vector2 at, float spread, float confidence, bool checkpoints)
    {
        Show(true);
        m_Previewing = true;
        m_PreviewState = state;
        m_PreviewAt = at;
        m_PreviewSpread = spread;
        m_PreviewConfidence = confidence;
        m_PreviewParticles.Clear();
        var random = new System.Random(7);
        for (int i = 0; i < 300; i++)
        {
            // Roughly normal: the sum of three uniforms.
            float x = (float)(random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * spread;
            float y = (float)(random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * spread;
            float weight = Mathf.Exp(-(x * x + y * y) / Mathf.Max(spread * spread, 0.01f));
            m_PreviewParticles.Add((at + new Vector2(x, y), weight));
        }
        m_Sign = "121";
        m_SignAt = Time.unscaledTime - 4f;
        m_Fps = 58f;
        WriteLines();
        if (m_Particles != null)
            m_Particles.SetVerticesDirty();
        if (checkpoints)
        {
            CloseCheckpoints();
            ToggleCheckpoints();
        }
    }
}

// The particles, in the map's content (plan metres x pixels per metre), each a small square kept one size on screen.
// Made by DebugOverlay at runtime, so it needs no file of its own.
public class DebugParticles : MaskableGraphic
{
    public DebugOverlay overlay;

    static readonly Color Unlikely = new Color(0.2f, 0.45f, 1f, 0.55f);
    static readonly Color Likely = new Color(1f, 0.2f, 0.15f, 0.8f);

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        FloorMap map = overlay != null ? overlay.map : null;
        if (map == null)
            return;
        int count = overlay.ParticleTotal();
        if (count == 0)
            return;
        // Coloured against the likeliest one.
        float most = 0f;
        for (int i = 0; i < count; i++)
        {
            overlay.Particle(i, out _, out float weight);
            most = Mathf.Max(most, weight);
        }
        float ppm = map.pixelsPerMeter;
        float half = DebugOverlay.ParticleSize * 0.5f / Mathf.Max(map.Zoom, 0.01f);
        for (int i = 0; i < count && vh.currentVertCount < 60000; i++)
        {
            overlay.Particle(i, out Vector2 at, out float weight);
            Color32 color = Color.Lerp(Unlikely, Likely, most > 0f ? weight / most : 0f);
            var p = new Vector3(at.x * ppm, at.y * ppm, 0f);
            int v = vh.currentVertCount;
            vh.AddVert(p + new Vector3(-half, -half), color, Vector4.zero);
            vh.AddVert(p + new Vector3(-half, half), color, Vector4.zero);
            vh.AddVert(p + new Vector3(half, half), color, Vector4.zero);
            vh.AddVert(p + new Vector3(half, -half), color, Vector4.zero);
            vh.AddTriangle(v, v + 1, v + 2);
            vh.AddTriangle(v + 2, v + 3, v);
        }
    }
}
