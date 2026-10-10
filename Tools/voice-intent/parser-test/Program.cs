using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// Scores the app's rule parser (Assets/Navigation/Scripts/VoiceParser.cs) on the same rows evaluate.py scores the
// fine-tuned model on, outside Unity: the parser and FloorPlan compile against UnityStubs.cs.
//   dotnet run -c Release                       handwritten.jsonl and data/test.jsonl (run make_dataset.py first)
//   dotnet run -c Release -- some.jsonl ...     other files
// A row is right when where the user is (and which door), where they're going, or the command, all match.
// For "which one?" rows, the option picked is read back as a place.
static class Program
{
    static readonly string Box = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../")).Replace('\\', '/');

    static VoiceQuestion Question(string context)
    {
        if (string.IsNullOrEmpty(context)) return null;
        var q = new VoiceQuestion { text = context };
        string c = context.ToLowerInvariant();
        if (c.StartsWith("which one"))
        {
            q.ask = c.Contains("are you at") ? VoiceAsk.Location : VoiceAsk.Destination;
            string list = context.Substring(context.IndexOf(':') + 1).TrimEnd('?').Trim();
            q.options = list.Split(new[] { ", or ", ", " }, StringSplitOptions.None).Select(o => o.Trim()).ToArray();
        }
        else if (c.Contains("say start")) q.ask = VoiceAsk.Start;
        else if (c.Contains("where do you want") || c.StartsWith("where to") || c.Contains("looking for")) q.ask = VoiceAsk.Destination;
        else q.ask = VoiceAsk.Location;
        return q;
    }

    static void Main(string[] args)
    {
        if (!FloorPlan.Validate(File.ReadAllText(Box + "Assets/Navigation/Data/ba1-floor1.json"), out string error))
            throw new Exception(error);
        FloorPlan plan = FloorPlan.Load(new UnityEngine.TextAsset { text = File.ReadAllText(Box + "Assets/Navigation/Data/ba1-floor1.json"), name = "plan" });
        var parser = new VoiceParser(plan);
        var files = args.Length > 0 ? args : new[] { Box + "Tools/voice-intent/handwritten.jsonl", Box + "Tools/voice-intent/data/test.jsonl" };
        var totals = new SortedDictionary<string, (int hit, int n)>();
        var misses = new List<string>();
        foreach (string file in files)
        foreach (string line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement row = JsonDocument.Parse(line).RootElement;
            string text = row.GetProperty("text").GetString();
            string context = row.TryGetProperty("context", out var cx) && cx.ValueKind == JsonValueKind.String ? cx.GetString() : null;
            string source = file.Contains("handwritten") ? "handwritten" : row.GetProperty("source").GetString();
            string intentName = row.GetProperty("intent").GetString();
            VoiceQuestion q = Question(context);
            VoiceIntent got = parser.Interpret(text, q);
            if (got.choice >= 0 && q?.options != null)
            {   // what the chosen option names
                VoiceIntent named = parser.Interpret(q.options[got.choice], new VoiceQuestion { ask = q.ask });
                if (q.ask == VoiceAsk.Location) got.here = named.here; else got.destination = named.destination;
            }
            string want = "", have = "";
            foreach (JsonElement call in row.GetProperty("calls").EnumerateArray())
            {
                var a = call.GetProperty("arguments");
                string Arg(string k) => a.TryGetProperty(k, out var v) ? v.GetString() : null;
                switch (call.GetProperty("name").GetString())
                {
                    case "set_location": want += $"here={Arg("place")} door={Arg("door")} "; break;
                    case "set_destination": want += $"to={Arg("place")} "; break;
                    default: want += $"do={Arg("action")} "; break;
                }
            }
            if (got.here != null) have += $"here={got.here} door={got.door} ";
            if (got.destination != null) have += $"to={got.destination} ";
            if (got.here == null && got.destination == null && got.action != VoiceAction.None)
                have += $"do={Action(got.action)} ";
            bool right = string.Equals(want.Trim(), have.Trim(), StringComparison.OrdinalIgnoreCase);
            foreach (string key in new[] { "source " + source, "intent " + intentName, "all" })
            {
                totals.TryGetValue(key, out var t);
                totals[key] = (t.hit + (right ? 1 : 0), t.n + 1);
            }
            if (!right) misses.Add($"[{source}] {(context != null ? "(" + context + ") " : "")}{text}\n   want {want}\n   got  {have}");
        }
        foreach (var (key, (hit, n)) in totals) Console.WriteLine($"{key,-24} {hit,5}/{n,-5} {100.0 * hit / n,5:F1}%");
        int show = 40;
        foreach (string m in misses.Where(m => m.StartsWith("[handwritten]")).Concat(misses.Where(m => !m.StartsWith("[handwritten]"))).Take(show))
            Console.WriteLine("\nMISS " + m);
    }

    static string Action(VoiceAction a) => a switch
    {
        VoiceAction.NextStep => "next_step", VoiceAction.HowFar => "how_far", VoiceAction.WhereAmI => "where_am_i",
        _ => a.ToString().ToLowerInvariant(),
    };
}
