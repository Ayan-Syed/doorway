using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

// Understands what the user said, from rules and the plan's own words: room numbers (spoken any way: "one oh
// seven", "one hundred and seven"), kinds (bathroom, lift, stairs, doors), men's or women's, and the words of a
// place's name and detail ("the lobby", "the café", "toward Engineering"). Always available, offline and instant;
// the fine-tuned model (FunctionGemmaFormat) can take over the wordings these rules miss.
//
// A sentence is split at cue words into what's about where the user is ("I'm at", "I can see", "from") and where
// they want to go ("take me to", "where is", "I need"). Anything with no cue answers the last question asked.
// Within the "where I am" part, a second landmark ("by the stairs", "and I can see 121") becomes near: the
// screen uses it to tell apart places that share a description.
public class VoiceParser : IVoiceInterpreter
{
    class Entry
    {
        public PlanPlace place;
        public string code;                  // "107", "113A"; null for places without a number
        public string gender;                // "m" or "w" for restrooms
        public readonly HashSet<string> words = new HashSet<string>();
        public readonly HashSet<string> numbers = new HashSet<string>();   // in the detail: "by 123"
    }

    enum Role { None, Here, Go }

    readonly FloorPlan m_Plan;
    readonly List<Entry> m_Entries = new List<Entry>();
    readonly HashSet<string> m_Codes = new HashSet<string>();

    // A place scores at least this to count as mentioned.
    const int MinScore = 3;

    static readonly string[][] HereCues = Cues("i am", "we are", "i see", "i can see", "there is", "there are",
        "from", "starting at", "start from", "currently at", "standing", "just got out of", "got out of",
        "came out of", "walked in", "my location is");
    static readonly string[][] GoCues = Cues("take me to", "bring me to", "get me to", "go to", "get to", "head to",
        "navigate to", "directions to", "direction to", "route to", "way to", "where is", "where are",
        "where can i find", "find", "looking for", "need", "want", "show me", "guide me to", "lead me to",
        "class in", "class is in", "lecture in", "meeting in", "late for", "going to");
    // Words that start another landmark inside "where I am".
    static readonly HashSet<string> Connectors = new HashSet<string>
        { "and", "near", "by", "next", "beside", "opposite", "across", "close", "with", "see", "behind" };

    static readonly Dictionary<string, string[]> KindWords = new Dictionary<string, string[]>
    {
        { "restroom", new[] { "restroom", "restrooms", "bathroom", "bathrooms", "toilet", "toilets", "washroom",
                              "wc", "loo", "lavatory", "pee" } },
        { "elevator", new[] { "elevator", "elevators", "lift", "lifts" } },
        { "stairs", new[] { "stairs", "stair", "stairway", "stairwell", "staircase", "steps" } },
        { "exit", new[] { "exit", "exits", "entrance", "entrances", "doors" } },
    };
    static readonly HashSet<string> Men = new HashSet<string> { "men", "mens", "man", "male", "guys", "gents", "boys" };
    static readonly HashSet<string> Women = new HashSet<string>
        { "women", "womens", "woman", "ladies", "lady", "female", "girls" };
    static readonly HashSet<string> Compass = new HashSet<string> { "north", "south", "east", "west" };
    static readonly HashSet<string> Stop = new HashSet<string>
    {
        "the", "a", "an", "to", "of", "in", "at", "on", "by", "for", "me", "my", "i", "am", "is", "are", "it", "its",
        "room", "rooms", "please", "uh", "um", "uhh", "so", "okay", "ok", "hey", "and", "this", "that", "there", "here",
        "right", "just", "some", "think", "go", "get", "where", "how", "do", "can", "you", "near", "next", "with",
        "from", "need", "want", "find", "one", "now", "front", "side", "door", "wing", "inside", "former", "be",
        "we", "was", "were", "what", "which", "whichever", "will", "would", "could", "like", "see", "out", "up",
    };
    static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>
    {
        { "coffee", "cafe" }, { "cafeteria", "cafe" }, { "ii", "2" }, { "engineering", "engineering" },
    };

    static readonly Dictionary<string, int> Ones = new Dictionary<string, int>
    {
        { "zero", 0 }, { "oh", 0 }, { "one", 1 }, { "two", 2 }, { "three", 3 }, { "four", 4 }, { "five", 5 },
        { "six", 6 }, { "seven", 7 }, { "eight", 8 }, { "nine", 9 },
    };
    static readonly Dictionary<string, int> Teens = new Dictionary<string, int>
    {
        { "ten", 10 }, { "eleven", 11 }, { "twelve", 12 }, { "thirteen", 13 }, { "fourteen", 14 }, { "fifteen", 15 },
        { "sixteen", 16 }, { "seventeen", 17 }, { "eighteen", 18 }, { "nineteen", 19 },
    };
    static readonly Dictionary<string, int> Tens = new Dictionary<string, int>
    {
        { "twenty", 20 }, { "thirty", 30 }, { "forty", 40 }, { "fifty", 50 }, { "sixty", 60 }, { "seventy", 70 },
        { "eighty", 80 }, { "ninety", 90 },
    };
    static readonly Dictionary<string, int> Ordinals = new Dictionary<string, int>
    {
        { "first", 0 }, { "1st", 0 }, { "second", 1 }, { "2nd", 1 }, { "third", 2 }, { "3rd", 2 }, { "fourth", 3 },
    };

    // Commands, matched as whole words in order: earlier ones win ("stop talking" is Mute before "stop" is End).
    static readonly (VoiceAction action, string[] phrases)[] Commands =
    {
        (VoiceAction.Unmute, new[] { "unmute", "voice on", "talk to me", "sound on", "speak again", "out loud",
                                     "voice back on", "sound back on", "read the directions" }),
        (VoiceAction.Mute, new[] { "mute", "be quiet", "no more talking", "stop talking", "shut up", "quiet", "silence", "voice off",
                                   "turn off the voice", "sound off" }),
        (VoiceAction.WhereAmI, new[] { "where am i", "where are we", "what is around", "near me", "what room am i" }),
        (VoiceAction.Lost, new[] { "lost", "wrong way", "realign", "re align", "arrows are", "not match",
                                   "confused", "not right", "not look right", "into a wall" }),
        (VoiceAction.Repeat, new[] { "repeat", "say that again", "say again", "come again", "what did you say",
                                     "not catch", "sorry what", "one more time", "pardon" }),
        (VoiceAction.HowFar, new[] { "how far", "how long", "how much longer", "how much further", "how many",
                                     "almost there", "are we close", "am i close", "minutes", "eta" }),
        (VoiceAction.NextStep, new[] { "what is next", "which way", "where do i turn", "what do i do", "next step",
                                       "where to now", "do i turn", "supposed to turn", "where now" }),
        (VoiceAction.End, new[] { "cancel", "never mind", "nevermind", "end navigation", "stop navigating",
                                  "forget it", "quit", "i am done", "changed my mind" }),
        (VoiceAction.Help, new[] { "help", "what can i say", "what can you do", "how does this work",
                                   "how do i use" }),
        (VoiceAction.Start, new[] { "start", "begin", "let us go", "lets go", "i am ready", "go ahead", "ready",
                                    "i am in position" }),
    };
    static readonly HashSet<string> Yes = new HashSet<string>
        { "yes", "yeah", "yep", "yup", "correct", "sure", "right", "affirmative", "uh huh" };
    static readonly HashSet<string> No = new HashSet<string> { "no", "nope", "wrong", "nah" };

    public VoiceParser(FloorPlan plan)
    {
        m_Plan = plan;
        foreach (PlanPlace place in plan.places)
        {
            var entry = new Entry { place = place };
            string name = place.name ?? "";
            Match code = Regex.Match(name, @"^(\d+[A-Za-z]?)\b");
            if (code.Success)
            {
                entry.code = code.Groups[1].Value.ToUpperInvariant();
                m_Codes.Add(entry.code);
            }
            if (place.kind == "restroom")
                entry.gender = name.StartsWith("Men") ? "m" : name.StartsWith("Women") ? "w" : null;
            // The place's own kind words score as the kind, not as words: else "Elevator" in one elevator's name
            // would make "an elevator" mean that one. Other kinds' words stay ("Lobby, by the elevators").
            string idNumbers = Regex.Replace(place.id, @"\D+", " ");
            foreach (string word in Words(name + " " + (place.detail ?? "") + " " + idNumbers))
            {
                if (char.IsDigit(word[0]))
                    entry.numbers.Add(word.ToUpperInvariant());
                else if (!Stop.Contains(word) && !Men.Contains(word) && !Women.Contains(word) &&
                         !OwnKindWord(place.kind, word))
                {
                    entry.words.Add(word);
                    entry.words.Add(word.EndsWith("s") ? word.Substring(0, word.Length - 1) : word + "s");   // "offices"
                }
            }
            if (entry.code != null)
                entry.numbers.Remove(entry.code);
            m_Entries.Add(entry);
        }
    }

    static bool OwnKindWord(string kind, string word) =>
        KindWords.TryGetValue(kind, out string[] words) && Array.IndexOf(words, word) >= 0;

    public VoiceIntent Interpret(string heard, VoiceQuestion question)
    {
        var intent = new VoiceIntent();
        List<string> raw = Words(heard, false);
        if (raw.Count == 0)
            return intent;

        if (question?.options != null)
        {
            intent.choice = Choose(raw, question.options);
            if (intent.choice >= 0)
                return intent;
        }

        List<string> words = SpokenNumbers(raw);
        bool missed = false;   // asked for a place ("take me to…", a room number) that isn't on the plan
        foreach (var (role, segment) in Segments(words))
        {
            Role assigned = role;
            if (assigned == Role.None && DoorNamed(segment) != null)
                assigned = Role.Here;   // "at the west door of 109"
            if (assigned == Role.None)
                assigned = question == null || question.ask == VoiceAsk.Nothing || question.ask == VoiceAsk.Destination
                    ? Role.Go : Role.Here;
            bool found;
            if (assigned == Role.Go)
            {
                if (intent.destination == null)
                    intent.destination = Best(segment);
                found = intent.destination != null;
            }
            else
            {
                if (intent.here == null)
                    ReadHere(segment, intent);
                found = intent.here != null;
            }
            // After a cue ("take me to the gym") any words count; without one, only a number ("room 999").
            if (!found && (role != Role.None ? segment.Exists(w => !Stop.Contains(w)) : HasNumber(segment)))
                missed = true;
        }
        if (intent.here != null || intent.destination != null)
            return intent;

        intent.action = Command(raw);
        // "I'm lost" has a cue too, and is a command: only what no command explains is a place that isn't here.
        intent.unknownPlace = missed && intent.action == VoiceAction.Unknown;
        return intent;
    }

    static bool HasNumber(List<string> words) => words.Exists(w => char.IsDigit(w[0]));

    // "Where I am": the place, a second landmark if one is named, and which door.
    void ReadHere(List<string> segment, VoiceIntent intent)
    {
        intent.door = DoorNamed(segment);
        var mentions = new List<List<string>> { new List<string>() };
        foreach (string word in segment)
        {
            if (Connectors.Contains(word))
            {
                if (mentions[mentions.Count - 1].Count > 0)
                    mentions.Add(new List<string>());
            }
            else
                mentions[mentions.Count - 1].Add(word);
        }
        string first = null, second = null;
        foreach (List<string> mention in mentions)
        {
            string found = Best(mention);
            if (found == null)
                continue;
            if (first == null)
                first = found;
            else if (second == null && found != first)
                second = found;
        }
        intent.here = first;
        intent.near = second;
        if (first == null || !VoiceKinds.IsKind(first))
            return;
        // A kind, then: "the men's room by the elevators" may name one outright, read as a whole;
        // "the stairs by 117" is the stairs nearest 117.
        string whole = Best(segment);
        if (whole != null && !VoiceKinds.IsKind(whole) && VoiceKinds.Matches(Place(whole), first))
            intent.here = whole;
        else if (second != null && !VoiceKinds.IsKind(second))
            intent.here = NearestOfKind(first, Place(second)) ?? first;
        if (intent.here == intent.near)
            intent.near = null;
    }

    // "west door", "north side": which door, or null.
    static string DoorNamed(List<string> words)
    {
        for (int i = 0; i + 1 < words.Count; i++)
        {
            if (Compass.Contains(words[i]) && (words[i + 1] == "door" || words[i + 1] == "side"))
                return words[i];
        }
        return null;
    }

    PlanPlace Place(string id) => m_Plan.places.Find(p => p.id == id);

    // The place of this kind whose door is nearest one of the landmark's doors.
    string NearestOfKind(string kind, PlanPlace landmark)
    {
        string best = null;
        float shortest = float.PositiveInfinity;
        string code = m_Entries.Find(e => e.place == landmark)?.code;
        foreach (Entry entry in m_Entries)
        {
            if (!VoiceKinds.Matches(entry.place, kind))
                continue;
            if (code != null && entry.numbers.Contains(code))
                return entry.place.id;   // "Stairs 118: North-east, by 117"
            foreach (PlanDoor a in entry.place.doors)
            foreach (PlanDoor b in landmark.doors)
            {
                float d = Distance(m_Plan.NodePosition(a.node), m_Plan.NodePosition(b.node));
                if (d < shortest)
                {
                    shortest = d;
                    best = entry.place.id;
                }
            }
        }
        return best;
    }

    static float Distance(UnityEngine.Vector2 a, UnityEngine.Vector2 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    // The place these words name: an id if one place scores best, a kind if several of one kind tie, else null.
    string Best(List<string> words)
    {
        if (words.Count == 0)
            return null;
        List<string> codes = Codes(words);
        string gender = null;
        foreach (string w in words)
        {
            if (Men.Contains(w))
                gender = "m";
            else if (Women.Contains(w))
                gender = "w";
        }

        int best = MinScore - 1;
        var winners = new List<Entry>();
        foreach (Entry entry in m_Entries)
        {
            int score = Score(entry, words, codes, gender);
            if (score > best)
            {
                best = score;
                winners.Clear();
            }
            if (score == best)
                winners.Add(entry);
        }
        if (winners.Count == 0)
            return null;
        if (winners.Count == 1)
            return winners[0].place.id;
        string kind = winners[0].place.kind;
        if (kind == "room" || winners.Exists(e => e.place.kind != kind))
            return winners[0].place.id;   // rooms are named by number; a tie between kinds is a guess either way
        if (kind == "restroom" && gender != null)
            return gender == "m" ? VoiceKinds.MensRestroom : VoiceKinds.WomensRestroom;
        return kind;
    }

    static int Score(Entry entry, List<string> words, List<string> codes, string gender)
    {
        int score = 0;
        int named = entry.code == null ? -1 : codes.IndexOf(entry.code);
        if (named == 0)
            score += 10;
        else if (named > 0)
            score += 4;   // a number after the first one qualifies it: "117B inside 117"
        bool kind = KindWords.TryGetValue(entry.place.kind, out string[] kindWords) &&
                    words.Exists(w => Array.IndexOf(kindWords, w) >= 0) ||
                    entry.gender != null && gender != null;   // "the men's room", "the ladies'"
        if (kind)
            score += 3;
        if (entry.gender != null && gender != null)
            score += entry.gender == gender ? 3 : -6;
        foreach (string w in words)
        {
            if (entry.words.Contains(w))
                score += 2;
        }
        // "the elevator in front of 107", "the entrance by 126": a number in the detail, with the kind right,
        // outweighs the room it names.
        if (kind)
            foreach (string c in codes)
                if (entry.numbers.Contains(c))
                    score += 8;
        return score;
    }

    // Room numbers in the words, in order: "113" then "a" is 113A when the plan has a 113A (and then not 113).
    List<string> Codes(List<string> words)
    {
        var codes = new List<string>();
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            if (!Regex.IsMatch(w, @"^\d+[a-z]?$"))
                continue;
            if (i + 1 < words.Count && words[i + 1].Length == 1 && char.IsLetter(words[i + 1][0]) &&
                m_Codes.Contains((w + words[i + 1]).ToUpperInvariant()))
            {
                codes.Add((w + words[i + 1]).ToUpperInvariant());
                i++;
                continue;
            }
            codes.Add(w.ToUpperInvariant());
        }
        return codes;
    }

    // Splits at the cues; each piece is about where the user is, where they're going, or neither. "to" after
    // a "from" starts the going part ("from 113 to 107").
    static List<(Role role, List<string> words)> Segments(List<string> words)
    {
        var segments = new List<(Role role, List<string> words)> { (Role.None, new List<string>()) };
        bool from = false;
        for (int i = 0; i < words.Count;)
        {
            int length = Matches(words, i, HereCues);
            Role role = Role.Here;
            if (length == 0)
            {
                length = Matches(words, i, GoCues);
                role = Role.Go;
            }
            if (length == 0 && from && words[i] == "to")
                length = 1;
            if (length > 0)
            {
                if (role == Role.Here && words[i] == "from")
                    from = true;
                segments.Add((role, new List<string>()));
                i += length;
                continue;
            }
            segments[segments.Count - 1].words.Add(words[i]);
            i++;
        }
        segments.RemoveAll(s => s.words.Count == 0);
        return segments;
    }

    // The length of the longest cue starting at words[i], or 0.
    static int Matches(List<string> words, int i, string[][] cues)
    {
        int longest = 0;
        foreach (string[] cue in cues)
        {
            if (cue.Length <= longest || i + cue.Length > words.Count)
                continue;
            bool match = true;
            for (int k = 0; k < cue.Length && match; k++)
                match = words[i + k] == cue[k];
            if (match)
                longest = cue.Length;
        }
        return longest;
    }

    // "the second one", "the lobby", "whichever is in the atrium": the option, or -1.
    static int Choose(List<string> words, string[] options)
    {
        foreach (string w in words)
        {
            if (Ordinals.TryGetValue(w, out int index) && index < options.Length)
                return index;
            if (w == "last")
                return options.Length - 1;
        }
        int best = -1, bestScore = 0;
        bool tie = false;
        for (int i = 0; i < options.Length; i++)
        {
            int score = 0;
            foreach (string w in Words(options[i]))
            {
                if (!Stop.Contains(w) && words.Contains(w))
                    score++;
            }
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
                tie = false;
            }
            else if (score == bestScore && score > 0)
                tie = true;
        }
        return tie ? -1 : best;
    }

    static VoiceAction Command(List<string> words)
    {
        string text = " " + string.Join(" ", words) + " ";
        foreach (var (action, phrases) in Commands)
        {
            foreach (string phrase in phrases)
            {
                if (text.Contains(" " + phrase + " "))
                    return action;
            }
        }
        // Short answers only: "go" or "stop" inside a sentence isn't a command.
        if (words.Count <= 3)
        {
            if (words.Contains("go"))
                return VoiceAction.Start;
            if (words.Contains("stop") || words.Contains("end"))
                return VoiceAction.End;
            if (words.Exists(Yes.Contains) || text.Contains(" uh huh "))
                return VoiceAction.Yes;
            if (words.Exists(No.Contains))
                return VoiceAction.No;
        }
        return VoiceAction.Unknown;
    }

    // --- Words ---------------------------------------------------------------------------------------------

    // Lower case, no accents or punctuation, contractions spelt out ("i'm" -> "i am", "men's" -> "men"),
    // "ba2" -> "ba 2", compass pairs joined ("north west" -> "northwest"), number words as digits (numbers true).
    public static List<string> Words(string text, bool numbers = true)
    {
        var s = new StringBuilder((text ?? "").ToLowerInvariant());
        s.Replace('’', '\'').Replace('é', 'e').Replace('è', 'e').Replace('ê', 'e').Replace('á', 'a').Replace('à', 'a')
         .Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u').Replace('ñ', 'n').Replace('-', ' ');
        string t = s.ToString();
        t = t.Replace("n't", " not").Replace("'m", " am").Replace("'re", " are").Replace("'ll", " will")
             .Replace("'ve", " have").Replace("'d", " would");
        t = Regex.Replace(t, @"\blet's\b", "let us");
        t = Regex.Replace(t, @"\b(what|where|it|that|there|here|who|how)'s\b", "$1 is");
        t = t.Replace("'s", "").Replace("s'", "s").Replace("'", "");
        t = Regex.Replace(t, @"([a-z]{2,})(\d)", "$1 $2");
        t = Regex.Replace(t, @"(\d)([a-z]{2,})", "$1 $2");
        t = Regex.Replace(t, @"[^a-z0-9 ]", " ");
        t = Regex.Replace(t, @"\bim\b", "i am");
        t = Regex.Replace(t, @"\b(does|do|did|is|are|was|ca|wo)nt\b", "$1 not");   // speech-to-text drops apostrophes
        t = Regex.Replace(t, @"\b(\d+)s\b", "$1");
        t = Regex.Replace(t, @"\b(get out|way out|leave (the|this) building)\b", "go to exit");

        var words = new List<string>();
        foreach (string w in t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string word = Synonyms.TryGetValue(w, out string synonym) ? synonym : w;
            int last = words.Count - 1;
            if (last >= 0 && (words[last] == "north" || words[last] == "south") && (word == "east" || word == "west"))
                words[last] += word;
            else
                words.Add(word);
        }
        return numbers ? SpokenNumbers(words) : words;
    }

    static bool IsNumberWord(string w) => Ones.ContainsKey(w) || Teens.ContainsKey(w) || Tens.ContainsKey(w) ||
                                          w == "hundred";

    // Runs of number words as digits: "one oh seven" and "one hundred and seven" -> "107", "one thirteen" -> "113",
    // "one thirty five" -> "135". "oh" alone is just "oh"; "a" counts before "hundred", "and" after it.
    static List<string> SpokenNumbers(List<string> words)
    {
        var output = new List<string>();
        for (int i = 0; i < words.Count;)
        {
            int end = i;
            while (end < words.Count && (IsNumberWord(words[end]) ||
                   words[end] == "a" && end + 1 < words.Count && words[end + 1] == "hundred" ||
                   words[end] == "and" && end > i && words[end - 1] == "hundred" && end + 1 < words.Count &&
                   IsNumberWord(words[end + 1])))
                end++;
            bool onlyOh = end > i && words.GetRange(i, end - i).TrueForAll(w => w == "oh");
            if (end == i || onlyOh)
            {
                output.Add(words[i]);
                i++;
                continue;
            }
            output.Add(NumberValue(words.GetRange(i, end - i)));
            i = end;
        }
        return output;
    }

    static string NumberValue(List<string> run)
    {
        int hundred = run.IndexOf("hundred");
        if (hundred >= 0)
        {
            int lead = hundred > 0 && Ones.TryGetValue(run[hundred - 1], out int h) ? h : 1;
            List<string> rest = run.GetRange(hundred + 1, run.Count - hundred - 1);
            rest.RemoveAll(w => w == "and");
            return (lead * 100 + SmallValue(rest)).ToString();
        }
        var digits = new StringBuilder();
        for (int k = 0; k < run.Count; k++)
        {
            string w = run[k];
            if (Ones.TryGetValue(w, out int one))
                digits.Append(one);
            else if (Teens.TryGetValue(w, out int teen))
                digits.Append(teen);
            else if (Tens.TryGetValue(w, out int ten))
            {
                if (k + 1 < run.Count && Ones.TryGetValue(run[k + 1], out int unit) && unit > 0 && run[k + 1] != "oh")
                {
                    digits.Append(ten + unit);
                    k++;
                }
                else
                    digits.Append(ten);
            }
        }
        return digits.ToString();
    }

    // Up to 99 from words: "thirty five", "thirteen", "seven".
    static int SmallValue(List<string> words)
    {
        int value = 0;
        foreach (string w in words)
        {
            if (Tens.TryGetValue(w, out int ten))
                value += ten;
            else if (Teens.TryGetValue(w, out int teen))
                value += teen;
            else if (Ones.TryGetValue(w, out int one))
                value += one;
        }
        return value;
    }

    static string[][] Cues(params string[] cues)
    {
        var split = new string[cues.Length][];
        for (int i = 0; i < cues.Length; i++)
            split[i] = cues[i].Split(' ');
        return split;
    }
}
