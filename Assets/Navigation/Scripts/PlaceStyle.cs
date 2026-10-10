using UnityEngine;

// How each kind of place looks in lists, on the map and on the destination label. Colours come from the theme.
public static class PlaceStyle
{
    public static Color Accent => Theme.Current.primary;
    public static Color AccentInk => Theme.Current.onPrimary;
    public static Color Destination => Theme.Current.destination;
    public static Color DestinationInk => Theme.Current.onDestination;
    public static Color Good => Theme.Current.good;
    public static Color Waiting => Theme.Current.warning;
    public static Color Bad => Theme.Current.error;

    // Material Icons glyphs.
    public const string IconRoom = "";      // meeting_room
    public const string IconRestroom = "";  // wc
    public const string IconElevator = "";  // elevator
    public const string IconStairs = "";    // stairs
    public const string IconExit = "";      // exit_to_app
    public const string IconPlace = "";     // place
    public const string IconDoor = "";      // door_back (door_front is U+EFFD)
    public const string IconBuilding = "";  // domain

    // "Room 107" for a bare room number; any other name as it is ("Men's restroom", "148 The Exchange").
    public static string Title(PlanPlace place)
    {
        string name = place.name ?? "";
        if (name.Length > 0 && char.IsDigit(name[0]) && name.IndexOf(' ') < 0)
            return "Room " + name;
        return name;
    }

    // The place, and which of its doors when it has more than one ("Room 107 · West door").
    public static string DoorTitle(PlanPlace place, PlanDoor door)
    {
        if (door == null || string.IsNullOrEmpty(door.label))
            return Title(place);
        return $"{Title(place)} · {door.label}";
    }

    // Extra words a search finds this kind of place by.
    public static string Keywords(string kind)
    {
        switch (kind)
        {
            case "restroom": return "restroom bathroom toilet wc men women";
            case "elevator": return "elevator lift";
            case "stairs": return "stairs stairway staircase";
            case "exit": return "exit entrance door outside";
            case "building": return "building hall";
            default: return "room class";
        }
    }

    public static string Icon(string kind)
    {
        switch (kind)
        {
            case "room": return IconRoom;
            case "restroom": return IconRestroom;
            case "elevator": return IconElevator;
            case "stairs": return IconStairs;
            case "exit": return IconExit;
            case "building": return IconBuilding;
            default: return IconPlace;
        }
    }

    public static Color Color(string kind)
    {
        Theme t = Theme.Current;
        switch (kind)
        {
            case "room": return t.room;
            case "restroom": return t.restroom;
            case "elevator": return t.elevator;
            case "stairs": return t.stairs;
            case "exit": return t.exit;
            case "building": return t.onSurfaceVariant;
            default: return t.primary;
        }
    }

    // "Classroom · Floor 1", or the floor alone when there's nothing to say.
    public static string Subtitle(PlanPlace place, string floor)
    {
        return string.IsNullOrEmpty(place.detail) ? floor : $"{place.detail} · {floor}";
    }

    // Where to stand for a door start.
    public static string StandHint(string kind)
    {
        switch (kind)
        {
            case "restroom": return "Stand right outside the restroom door with your back to it, facing the hallway.";
            case "elevator": return "Stand right in front of the elevator with your back to its doors.";
            case "stairs": return "Stand at the foot of the stairs with your back to them.";
            case "exit": return "Stand just inside the doors with your back to them, facing into the building.";
            default: return "Stand right outside the door with your back to it, facing the hallway.";
        }
    }
}
