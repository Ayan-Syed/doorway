using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// A scrolling list of places (or doors) in sections, narrowed down by a search field as the user types and by
// kind (the filter chips). Rows and section headers are copies of templates built by the scene builder.
public class PlaceList : MonoBehaviour
{
    public ScrollRect scroll;
    public RectTransform content;
    public PlaceListRow rowTemplate;
    [Tooltip("A section header, with its Text somewhere inside.")]
    public GameObject sectionTemplate;
    [Tooltip("Optional: typing in it filters the list.")]
    public InputField searchField;
    public Text emptyText;
    [Tooltip("While something is typed, at most this many rows show (0: no limit).")]
    public int maxSearchRows = 30;

    // How many rows the last filter left showing.
    public int Shown { get; private set; }

    class Row
    {
        public PlaceListRow view;
        public string kind;
        public string title;
        public string searchText;
        public bool searchOnly;
        public bool fitted;      // a search-only row's title, cut to the row's width the first time it shows
        public int section;
        public bool shown;
    }

    readonly List<Row> m_Rows = new List<Row>();
    readonly List<GameObject> m_Sections = new List<GameObject>();
    readonly List<bool> m_SectionShown = new List<bool>();
    readonly List<bool> m_SectionHas = new List<bool>();
    static readonly char[] s_Space = { ' ' };

    string m_Kind;
    string m_Query = "";
    bool m_Initialized;

    void Awake() => Initialize();

    void Initialize()
    {
        if (m_Initialized)
            return;
        m_Initialized = true;
        if (rowTemplate != null)
            rowTemplate.gameObject.SetActive(false);
        if (sectionTemplate != null)
            sectionTemplate.SetActive(false);
        if (searchField != null)
            searchField.onValueChanged.AddListener(Filter);
    }

    public void Clear()
    {
        Initialize();
        foreach (Row row in m_Rows)
            Remove(row.view.gameObject);
        foreach (GameObject section in m_Sections)
            Remove(section);
        m_Rows.Clear();
        m_Sections.Clear();
        m_SectionShown.Clear();
        Shown = 0;
    }

    static void Remove(GameObject item)
    {
        // Hidden at once, so the layout skips it before it's gone.
        item.SetActive(false);
        if (Application.isPlaying)
            Destroy(item);
        else
            DestroyImmediate(item);
    }

    public void AddSection(string title)
    {
        Initialize();
        GameObject section = Instantiate(sectionTemplate, sectionTemplate.transform.parent, false);
        section.name = title;
        Text text = section.GetComponentInChildren<Text>(true);
        if (text != null)
            text.text = title;
        section.SetActive(true);
        m_Sections.Add(section);
        m_SectionShown.Add(true);
    }

    // A row under the last section. searchOnly: shown only while something is typed (the campus's buildings).
    // Those many rows can come on together as the first letter is typed, so their text is one fixed size, not
    // best fit (which lays the text out again and again to find its size), and a long name is cut short instead.
    public void AddRow(string kind, string title, string detail, UnityAction onTap, bool searchOnly = false)
    {
        Initialize();
        PlaceListRow view = Instantiate(rowTemplate, rowTemplate.transform.parent, false);
        view.name = title;
        view.title.text = title;
        bool hasDetail = !string.IsNullOrEmpty(detail);
        view.detail.text = hasDetail ? detail : "";
        view.detail.gameObject.SetActive(hasDetail);
        if (onTap != null)
            view.button.onClick.AddListener(onTap);
        if (searchOnly)
        {
            FixedSize(view.title);
            FixedSize(view.detail);
        }
        view.gameObject.SetActive(!searchOnly);

        m_Rows.Add(new Row
        {
            view = view,
            kind = kind,
            title = title,
            searchText = $"{title} {detail} {PlaceStyle.Keywords(kind)}".ToLowerInvariant(),
            searchOnly = searchOnly,
            section = m_Sections.Count - 1,
            shown = !searchOnly,
        });
    }

    // One line at the template's size. It may overflow its box: Google Sans's lines are taller than their size,
    // and a line that may not overflow draws nothing (the scene builder does the same for every fixed text).
    static void FixedSize(Text text)
    {
        text.resizeTextForBestFit = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
    }

    // A title cut with "…" to the width its row gives it: measured with the text's own layout, once, the first
    // time the row shows (the list's width is only known once it's laid out).
    void Fit(Row row)
    {
        Text text = row.view.title;
        var column = text.rectTransform.parent as RectTransform;
        float width = content == null ? 0f
            : content.rect.width - (column != null ? column.offsetMin.x - column.offsetMax.x : 0f) - 4f;
        if (width <= 0f)
            return;   // not laid out yet: try again next time it shows
        row.fitted = true;
        TextGenerationSettings settings = text.GetGenerationSettings(Vector2.zero);
        TextGenerator generator = text.cachedTextGeneratorForLayout;
        string value = row.title;
        float full = generator.GetPreferredWidth(value, settings) / text.pixelsPerUnit;
        if (full <= width)
            return;
        // Cut in proportion, back to a word if there's one, and check; a few tries at most.
        for (int tries = 0; tries < 4 && value.Length > 1; tries++)
        {
            int keep = Mathf.Clamp(Mathf.FloorToInt(value.Length * width / full) - 1, 1, value.Length - 1);
            int space = value.LastIndexOf(' ', keep);
            value = value.Substring(0, space > keep / 2 ? space : keep).TrimEnd(' ', ',', '-', '·');
            full = generator.GetPreferredWidth(value + "…", settings) / text.pixelsPerUnit;
            if (full <= width)
                break;
        }
        text.text = value + "…";
    }

    // Only places of this kind (a filter chip), or every kind for null.
    public void SetKind(string kind)
    {
        Initialize();
        m_Kind = kind;
        Apply(m_Query);
    }

    // Shows the rows of the chosen kind that contain every typed word, and the sections that still have rows.
    public void Filter(string query)
    {
        Initialize();
        Apply(query);
        if (scroll != null)
            scroll.verticalNormalizedPosition = 1f;
    }

    // Filters again (rows were added) without scrolling.
    public void Refresh()
    {
        Initialize();
        Apply(m_Query);
    }

    public void ResetSearch()
    {
        Initialize();
        if (searchField != null)
            searchField.SetTextWithoutNotify("");
        m_Kind = null;
        Filter("");
    }

    void Apply(string query)
    {
        m_Query = query ?? "";
        string[] words = m_Query.ToLowerInvariant().Split(s_Space, StringSplitOptions.RemoveEmptyEntries);

        m_SectionHas.Clear();
        for (int i = 0; i < m_Sections.Count; i++)
            m_SectionHas.Add(false);

        // Typing, only the first rows that match: every row shown costs layout and text, and a screenful is plenty.
        int limit = words.Length > 0 && maxSearchRows > 0 ? maxSearchRows : int.MaxValue;
        int shown = 0;
        foreach (Row row in m_Rows)
        {
            bool match = shown < limit && (m_Kind == null || row.kind == m_Kind) && (!row.searchOnly || words.Length > 0)
                         && HasAll(row.searchText, words);
            if (match && row.searchOnly && !row.fitted)
                Fit(row);
            if (match != row.shown)
            {
                row.shown = match;
                row.view.gameObject.SetActive(match);
            }
            if (!match)
                continue;
            shown++;
            if (row.section >= 0)
                m_SectionHas[row.section] = true;
        }

        for (int i = 0; i < m_Sections.Count; i++)
            if (m_SectionHas[i] != m_SectionShown[i])
            {
                m_SectionShown[i] = m_SectionHas[i];
                m_Sections[i].SetActive(m_SectionHas[i]);
            }

        Shown = shown;
        if (emptyText != null)
        {
            bool empty = shown == 0;
            if (empty)
                emptyText.text = EmptyMessage(m_Query.Trim(), m_Kind);
            if (emptyText.gameObject.activeSelf != empty)
                emptyText.gameObject.SetActive(empty);
        }
    }

    static bool HasAll(string text, string[] words)
    {
        foreach (string word in words)
            if (!text.Contains(word))   // ordinal
                return false;
        return true;
    }

    // What an empty list says. v7 read "No places match “”" when a chip alone left no rows; with nothing typed
    // it now says the floor has none of that kind.
    static string EmptyMessage(string query, string kind)
    {
        string what = kind == null ? "places" : Plural(kind);
        return query.Length > 0 ? $"No {what} match “{query}”" : $"No {what} on this floor";
    }

    // The words NavigationScreen counts the chips' kinds in.
    static string Plural(string kind)
    {
        switch (kind)
        {
            case "room": return "rooms";
            case "restroom": return "restrooms";
            case "elevator": return "elevators";
            case "stairs": return "stairways";
            case "exit": return "entrances";
            case "building": return "buildings";
            default: return "places";
        }
    }

    // After a day/night switch: the copies take their templates' new colours.
    public void Restyle()
    {
        foreach (Row row in m_Rows)
            ThemeColors.CopyColors(rowTemplate, row.view);
        foreach (GameObject section in m_Sections)
            ThemeColors.CopyColors(sectionTemplate.transform, section.transform);
    }
}
