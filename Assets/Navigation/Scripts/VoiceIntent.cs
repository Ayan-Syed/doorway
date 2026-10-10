using System.Collections.Generic;
using System.Text.RegularExpressions;

// What the user meant, in the same terms as the voice-intent model's function calls (Tools/voice-intent):
//   set_location(place, near?, door?)  -> here, near, door
//   set_destination(place)             -> destination
//   command(action)                    -> action
// place is a plan place id ("107", "wc-m-w"), or a kind (VoiceKinds) when the user didn't say which one.
// Interpreters: VoiceParser (rules, always there) and FunctionGemmaFormat (the fine-tuned model's answer).
public enum VoiceAction
{
    None, Start, Repeat, NextStep, HowFar, WhereAmI, Lost, End, Mute, Unmute, Help, Yes, No, Unknown,
}

// What the app last asked, so a bare answer ("one forty six") means the right thing.
public enum VoiceAsk { Nothing, Location, Destination, Start }

public class VoiceQuestion
{
    public VoiceAsk ask;
    /// <summary>The question as spoken.</summary>
    public string text;
    /// <summary>"Which one?": the choices as spoken, in order. Null for an open question.</summary>
    public string[] options;
}

public class VoiceIntent
{
    public string here;
    public string near;
    /// <summary>north, south, east or west: which of here's doors.</summary>
    public string door;
    public string destination;
    /// <summary>The option picked when the question offered some, or -1.</summary>
    public int choice = -1;
    public VoiceAction action;

    public bool Understood => here != null || destination != null || choice >= 0 ||
                              action != VoiceAction.None && action != VoiceAction.Unknown;
}

public interface IVoiceInterpreter
{
    VoiceIntent Interpret(string heard, VoiceQuestion question);
}

// The kinds a place argument may be instead of an id.
public static class VoiceKinds
{
    public const string Restroom = "restroom";
    public const string MensRestroom = "mens-restroom";
    public const string WomensRestroom = "womens-restroom";
    public const string Elevator = "elevator";
    public const string Stairs = "stairs";
    public const string Exit = "exit";

    public static readonly string[] All = { Restroom, MensRestroom, WomensRestroom, Elevator, Stairs, Exit };

    public static bool IsKind(string place) => System.Array.IndexOf(All, place) >= 0;

    public static bool Matches(PlanPlace place, string kind)
    {
        switch (kind)
        {
            case MensRestroom: return place.kind == "restroom" && (place.name ?? "").StartsWith("Men");
            case WomensRestroom: return place.kind == "restroom" && (place.name ?? "").StartsWith("Women");
            default: return place.kind == kind;
        }
    }

    // "a restroom", "a men's restroom", "an elevator"…: for questions.
    public static string Spoken(string kind)
    {
        switch (kind)
        {
            case MensRestroom: return "men's restroom";
            case WomensRestroom: return "women's restroom";
            case Stairs: return "stairway";
            case Exit: return "entrance";
            default: return kind;
        }
    }
}

// The fine-tuned FunctionGemma's answer, as an intent:
//   <start_function_call>call:set_location{door:<escape>west<escape>,place:<escape>107<escape>}<end_function_call>
// The model runs elsewhere (a llama.cpp or LiteRT runtime); this only reads what it wrote.
public static class FunctionGemmaFormat
{
    static readonly Regex Call = new Regex(@"<start_function_call>call:(\w+)\{(.*?)\}<end_function_call>",
        RegexOptions.Singleline);
    static readonly Regex Argument = new Regex(@"(\w+):(?:<escape>(.*?)<escape>|([^,}]*))", RegexOptions.Singleline);

    static readonly Dictionary<string, VoiceAction> Actions = new Dictionary<string, VoiceAction>
    {
        { "start", VoiceAction.Start }, { "repeat", VoiceAction.Repeat }, { "next_step", VoiceAction.NextStep },
        { "how_far", VoiceAction.HowFar }, { "where_am_i", VoiceAction.WhereAmI }, { "lost", VoiceAction.Lost },
        { "end", VoiceAction.End }, { "mute", VoiceAction.Mute }, { "unmute", VoiceAction.Unmute },
        { "help", VoiceAction.Help }, { "yes", VoiceAction.Yes }, { "no", VoiceAction.No },
        { "unknown", VoiceAction.Unknown },
    };

    /// <summary>Ids are checked against the plan: one the model made up comes back null.</summary>
    public static VoiceIntent Parse(string output, FloorPlan plan)
    {
        var intent = new VoiceIntent();
        foreach (Match call in Call.Matches(output ?? ""))
        {
            var arguments = new Dictionary<string, string>();
            foreach (Match argument in Argument.Matches(call.Groups[2].Value))
            {
                string value = argument.Groups[2].Success ? argument.Groups[2].Value : argument.Groups[3].Value;
                arguments[argument.Groups[1].Value] = value.Trim();
            }
            arguments.TryGetValue("place", out string place);
            place = Known(place, plan);
            switch (call.Groups[1].Value)
            {
                case "set_location":
                    intent.here = place;
                    intent.near = arguments.TryGetValue("near", out string near) ? Known(near, plan) : null;
                    intent.door = arguments.TryGetValue("door", out string door) ? door.ToLowerInvariant() : null;
                    break;
                case "set_destination":
                    intent.destination = place;
                    break;
                case "command":
                    if (arguments.TryGetValue("action", out string action) &&
                        Actions.TryGetValue(action, out VoiceAction a))
                        intent.action = a;
                    break;
            }
        }
        return intent;
    }

    // The plan's id for it (any case), a kind, or null.
    static string Known(string place, FloorPlan plan)
    {
        if (string.IsNullOrEmpty(place))
            return null;
        if (VoiceKinds.IsKind(place))
            return place;
        PlanPlace found = plan.places.Find(p => string.Equals(p.id, place, System.StringComparison.OrdinalIgnoreCase));
        return found?.id;
    }
}
