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

    // How many rows the last filter left showing.
    public int Shown { get; private set; }

    class Row
    {
        public PlaceListRow view;
        public string kind;
        public string searchText;
        public bool searchOnly;
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
    bool m_Wide = true;   // something typed shows the searchOnly rows too
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
        view.gameObject.SetActive(!searchOnly);

        m_Rows.Add(new Row
        {
            view = view,
            kind = kind,
            searchText = $"{title} {detail} {PlaceStyle.Keywords(kind)}".ToLowerInvariant(),
            searchOnly = searchOnly,
            section = m_Sections.Count - 1,
            shown = !searchOnly,
        });
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
        m_Wide = true;
        Apply(query);
        if (scroll != null)
            scroll.verticalNormalizedPosition = 1f;
    }

    // Filter, but the searchOnly rows stay hidden: a search within the list as it stands (the building's places).
    public void FilterWithin(string query)
    {
        Initialize();
        m_Wide = false;
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

        int shown = 0;
        foreach (Row row in m_Rows)
        {
            bool match = (m_Kind == null || row.kind == m_Kind) && (!row.searchOnly || (m_Wide && words.Length > 0)) && HasAll(row.searchText, words);
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
