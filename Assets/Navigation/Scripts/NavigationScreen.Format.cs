using System.Collections.Generic;
using UnityEngine;

// Distances, times and turns, in words.
public partial class NavigationScreen
{
    // The strings are kept: guidance asks for them every frame.
    static readonly Dictionary<int, string> s_Feet = new Dictionary<int, string>();
    static readonly Dictionary<int, string> s_Minutes = new Dictionary<int, string>();

    // Feet, rounded the way a person would say them: to 5 under 50 ft (never under 5), else to 10.
    static int FeetRounded(float meters)
    {
        float feet = meters * 3.28084f;
        return feet < 50f ? Mathf.Max(5, Mathf.RoundToInt(feet / 5f) * 5) : Mathf.RoundToInt(feet / 10f) * 10;
    }

    static string Feet(float meters)
    {
        int feet = FeetRounded(meters);
        if (!s_Feet.TryGetValue(feet, out string text))
            s_Feet[feet] = text = $"{feet} ft";
        return text;
    }

    // Never "0 min".
    static string Minutes(float meters)
    {
        int minutes = Mathf.Max(1, Mathf.RoundToInt(meters / Navigator.WalkingSpeed / 60f));
        if (!s_Minutes.TryGetValue(minutes, out string text))
            s_Minutes[minutes] = text = $"{minutes} min";
        return text;
    }

    // No "Turn now": at a turn the distance slot says "Now" next to the turn's own words.
    static string ManeuverText(Maneuver maneuver)
    {
        switch (maneuver)
        {
            case Maneuver.SlightLeft: return "Bear left";
            case Maneuver.Left: return "Turn left";
            case Maneuver.SharpLeft: return "Sharp left";
            case Maneuver.SlightRight: return "Bear right";
            case Maneuver.Right: return "Turn right";
            case Maneuver.SharpRight: return "Sharp right";
            case Maneuver.TurnAround: return "Turn around";
            default: return "Go straight";
        }
    }

    // Material Icons.
    static string ManeuverIcon(Maneuver maneuver)
    {
        switch (maneuver)
        {
            case Maneuver.SlightLeft: return "";
            case Maneuver.Left: return "";
            case Maneuver.SharpLeft: return "";
            case Maneuver.SlightRight: return "";
            case Maneuver.Right: return "";
            case Maneuver.SharpRight: return "";
            case Maneuver.TurnAround: return "";
            case Maneuver.Arrive: return PlaceStyle.IconPlace;
            default: return "";
        }
    }

    // "on your left" -> "On your left".
    static string Capitalized(string text)
    {
        return string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
