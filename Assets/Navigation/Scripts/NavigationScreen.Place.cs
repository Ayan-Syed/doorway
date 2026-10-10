using System.Linq;
using UnityEngine;

// Place: a place picked on the map or in the list, its card, and Directions.
public partial class NavigationScreen
{
    void ShowPlace(PlanPlace place)
    {
        Haptics.Tick(15);
        if (m_Step == Step.Home)
            m_SavedQuery = searchField.text;   // Back puts the search back
        if (searchField.isFocused)
            searchField.DeactivateInputField();
        m_Searching = false;
        m_Selected = place;
        m_Destination = null;
        navigator.Stop();
        ShowPlanBuilding();

        string title = PlaceStyle.Title(place);
        searchField.SetTextWithoutNotify(title);   // the search bar shows the place, with a back arrow and an X
        placeTitle.text = title;
        placeSubtitle.text = PlaceStyle.Subtitle(place, Plan.floor);
        placeDoors.text = Doors(place);
        placeFloor.text = $"{Plan.floor} · {Plan.building}";

        Show(Step.Place);
        OpenSheet(placeSheet, 0);
        map.Select(place);
        map.ShowPin(map.Anchor(place), title);
        map.ShowRoute(null);
        SetInsets(BottomOf(searchCard) + 8f, PlaceHeight);
        map.ShowPlace(place, !Application.isPlaying);
    }

    // "2 doors · West door, East door", "1 door", or none.
    static string Doors(PlanPlace place)
    {
        int count = place.doors.Count;
        if (count == 0)
            return "No doors mapped yet";
        string doors = count == 1 ? "1 door" : $"{count} doors";
        string labels = string.Join(", ", place.doors.Where(d => !string.IsNullOrEmpty(d.label)).Select(d => d.label));
        return labels.Length > 0 ? $"{doors} · {labels}" : doors;
    }
}
