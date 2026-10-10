using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// A scrolling list of places (or doors) in sections, narrowed down by a search field as the user types and by
// kind (the filter chips). Rows and section headers are copies of templates built by the scene builder.
// A section can be a dropdown, closed until tapped, and a row can sit inside another, shown under it once its
// "n inside" button is tapped. Typing or a chip opens everything: every match shows, and a nested match brings the
// row it's inside along, so it's clear where it is.
public class PlaceList : MonoBehaviour
{
    // Material Icons: expand_more and expand_less.
    public const string IconMore = "";
    public const string IconLess = "";
    // How much further in a nested row's text starts.
    const float Indent = 56f;

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
        public int parent = -1;  // the row it's inside, or -1
        public int children;     // rows inside it
        public bool expanded;    // its rows inside show (browsing)
        public bool match;       // the last filter matched it
        public bool childMatch;  // ...or a row inside it
    }

    class Section
    {
        public GameObject view;
        public PlaceListSection parts;   // null on a scene built before dropdowns
        public bool dropdown;
        public bool open;
        public bool shown;
        public int total;   // rows in it, not counting nested ones
    }

    readonly List<Row> m_Rows = new List<Row>();
    readonly List<Section> m_Sections = new List<Section>();
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
        foreach (Section section in m_Sections)
            Remove(section.view);
        m_Rows.Clear();
        m_Sections.Clear();
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

    // dropdown: closed until tapped, with how many places are in it (on a scene built before dropdowns, a plain
    // section).
    public void AddSection(string title, bool dropdown = false)
    {
        Initialize();
        GameObject view = Instantiate(sectionTemplate, sectionTemplate.transform.parent, false);
        view.name = title;
        var parts = view.GetComponent<PlaceListSection>();
        Text text = parts != null && parts.title != null ? parts.title : view.GetComponentInChildren<Text>(true);
        if (text != null)
            text.text = title;
        var section = new Section { view = view, parts = parts, shown = true };
        section.dropdown = dropdown && parts != null && parts.button != null;
        section.open = !section.dropdown;
        if (parts != null)
        {
            if (parts.button != null)
                parts.button.enabled = section.dropdown;
            SetActive(parts.count, section.dropdown);
            SetActive(parts.chevron, section.dropdown);
            if (parts.divider != null)
                parts.divider.SetActive(section.dropdown);
            if (section.dropdown)
                parts.button.onClick.AddListener(() =>
                {
                    section.open = !section.open;
                    Apply(m_Query);
                });
        }
        view.SetActive(true);
        m_Sections.Add(section);
    }

    // A row under the last section, and its index. searchOnly: shown only while something is typed (the campus's
    // buildings). Those many rows can come on together as the first letter is typed, so their text is one fixed
    // size, not best fit (which lays the text out again and again to find its size), and a long name is cut short
    // instead. parent: the row (an index from AddRow) it's inside, which then gets an "n inside" button.
    public int AddRow(string kind, string title, string detail, UnityAction onTap, bool searchOnly = false, int parent = -1)
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
        if (view.toggle != null)
            view.toggle.gameObject.SetActive(false);

        int section = m_Sections.Count - 1;
        if (parent < 0 || parent >= m_Rows.Count)
            parent = -1;
        else
        {
            if (view.textColumn != null)
                view.textColumn.offsetMin += new Vector2(Indent, 0f);
            if (view.divider != null)
                view.divider.offsetMin += new Vector2(Indent, 0f);
            AddChild(parent);
        }
        if (section >= 0 && !searchOnly && parent < 0)
            m_Sections[section].total++;   // the rows opening it shows; nested ones are counted on their button
        // Until the next filter: in an open section, not nested.
        bool shown = !searchOnly && parent < 0 && (section < 0 || m_Sections[section].open);
        view.gameObject.SetActive(shown);

        m_Rows.Add(new Row
        {
            view = view,
            kind = kind,
            title = title,
            searchText = $"{title} {detail} {PlaceStyle.Keywords(kind)}".ToLowerInvariant(),
            searchOnly = searchOnly,
            section = section,
            shown = shown,
            parent = parent,
        });
        return m_Rows.Count - 1;
    }

    // A row has one more inside it: its button says how many, and leaves room for itself in the row.
    void AddChild(int index)
    {
        Row row = m_Rows[index];
        row.children++;
        PlaceListRow view = row.view;
        if (view.toggle == null)
            return;
        if (row.children == 1)
        {
            if (view.textColumn != null)
                view.textColumn.offsetMax -= new Vector2(((RectTransform)view.toggle.transform).sizeDelta.x + 24f, 0f);
            view.toggle.onClick.AddListener(() =>
            {
                row.expanded = !row.expanded;
                Apply(m_Query);
            });
        }
        if (view.toggleLabel != null)
            view.toggleLabel.text = $"{row.children} inside";
    }

    static void SetActive(Component part, bool on)
    {
        if (part != null && part.gameObject.activeSelf != on)
            part.gameObject.SetActive(on);
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

    // Nothing typed, no chip, and every dropdown and nested row closed again.
    public void ResetSearch()
    {
        Initialize();
        if (searchField != null)
            searchField.SetTextWithoutNotify("");
        m_Kind = null;
        foreach (Section section in m_Sections)
            section.open = !section.dropdown;
        foreach (Row row in m_Rows)
            row.expanded = false;
        Filter("");
    }

    // Opens the dropdown with this title and, if given, the rooms inside the row with this one (the previews).
    public void Open(string section, string row = null)
    {
        Initialize();
        foreach (Section s in m_Sections)
            if (s.view.name == section)
                s.open = true;
        foreach (Row r in m_Rows)
            if (row != null && r.title == row && r.children > 0)
                r.expanded = true;
        Apply(m_Query);
    }

    void Apply(string query)
    {
        m_Query = query ?? "";
        string[] words = m_Query.ToLowerInvariant().Split(s_Space, StringSplitOptions.RemoveEmptyEntries);

        // Browsing: dropdowns and nested rows as the user left them. Typing or a chip: every match, opened up.
        bool browsing = words.Length == 0 && m_Kind == null;

        // Typing, only the first rows that match: every row shown costs layout and text, and a screenful is plenty.
        int limit = words.Length > 0 && maxSearchRows > 0 ? maxSearchRows : int.MaxValue;
        int matched = 0;
        foreach (Row row in m_Rows)
        {
            row.match = matched < limit && (m_Kind == null || row.kind == m_Kind)
                        && (!row.searchOnly || (m_Wide && words.Length > 0)) && HasAll(row.searchText, words);
            row.childMatch = false;
            if (row.match)
                matched++;
        }
        foreach (Row row in m_Rows)
            if (row.match && row.parent >= 0)
                m_Rows[row.parent].childMatch = true;

        var counts = new int[m_Sections.Count];   // per section: rows matched (browsing: all of them)
        foreach (Row row in m_Rows)
        {
            bool visible;
            if (browsing)
                visible = row.match && (row.section < 0 || m_Sections[row.section].open)
                          && (row.parent < 0 || m_Rows[row.parent].expanded);
            else
                visible = row.match || row.childMatch;
            if (visible && row.searchOnly && !row.fitted)
                Fit(row);
            if (visible != row.shown)
            {
                row.shown = visible;
                row.view.gameObject.SetActive(visible);
            }
            if (row.view.toggle != null && row.children > 0)
            {
                SetActive(row.view.toggle, browsing);   // everything inside that matches already shows
                if (row.view.toggleIcon != null)
                    row.view.toggleIcon.text = row.expanded ? IconLess : IconMore;
            }
            if (row.match && row.section >= 0)
                counts[row.section]++;
        }

        for (int i = 0; i < m_Sections.Count; i++)
            StyleSection(m_Sections[i], counts[i], browsing);

        Shown = matched;
        if (emptyText != null)
        {
            bool empty = matched == 0;
            if (empty)
                emptyText.text = EmptyMessage(m_Query.Trim(), m_Kind);
            if (emptyText.gameObject.activeSelf != empty)
                emptyText.gameObject.SetActive(empty);
        }
    }

    // A section shows while it has a match (a closed dropdown too, so it can be opened). A dropdown's count is the
    // rows opening it shows while browsing, the matches otherwise; it only opens and closes while browsing.
    void StyleSection(Section section, int count, bool browsing)
    {
        bool shown = count > 0;
        if (shown != section.shown)
        {
            section.shown = shown;
            section.view.SetActive(shown);
        }
        if (!section.dropdown)
            return;
        PlaceListSection parts = section.parts;
        bool open = !browsing || section.open;
        parts.button.interactable = browsing;
        if (parts.count != null)
            parts.count.text = (browsing ? section.total : count).ToString();
        SetActive(parts.chevron, browsing);
        if (parts.chevron != null)
            parts.chevron.text = open ? IconLess : IconMore;
        if (parts.divider != null && parts.divider.activeSelf == open)
            parts.divider.SetActive(!open);
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
        foreach (Section section in m_Sections)
            ThemeColors.CopyColors(sectionTemplate.transform, section.view.transform);
    }
}
