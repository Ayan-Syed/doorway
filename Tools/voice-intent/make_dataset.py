"""Builds the voice-intent training data for FunctionGemma 270M from BA1 floor 1's places.

  python make_dataset.py                       data/train.jsonl, data/test.jsonl, data/tools.json
  python make_dataset.py --push USER/REPO      the same, then uploads them to a private dataset repo (for HF Jobs)

Every row is one thing a user says, already labelled with the function calls the app should make, so the labels
are right by construction. The model learns the plan's place ids, so run this again (and retrain) when the plan's
places change. The app checks every id it gets back against the plan, so a made-up id is caught there.

Three functions, the voice side of the NavigationScreen steps:
  set_location(place, near?, door?)   the door the user stands at: Align
  set_destination(place)              where they want to go: Place / Start
  command(action)                     start, repeat, how far, I'm lost, ...
place is a plan id ("107", "wc-m-w"), or a kind when the user didn't say which one ("mens-restroom", "stairs"):
the app then takes the nearest, or asks.

Rows: {"id", "split", "source", "intent", "context", "text", "calls"}. context is what the app last asked, or null.
Test rows use phrasings (templates) that no training row uses, so the score measures new wordings, not recall.
"""
import argparse
import json
import os
import random
import re

HERE = os.path.dirname(os.path.abspath(__file__))
BOX = os.path.normpath(os.path.join(HERE, "..", ".."))
PLAN_JSON = os.path.join(BOX, "Assets", "Navigation", "Data", "ba1-floor1.json")
ALIASES_JSON = os.path.join(HERE, "aliases.json")
DATA_DIR = os.path.join(HERE, "data")

# FunctionGemma's model card: this exact developer turn switches function calling on.
DEVELOPER = "You are a model that can do function calling with the following functions"

GENERIC = ["restroom", "mens-restroom", "womens-restroom", "elevator", "stairs", "exit"]
DOORS = ["north", "south", "east", "west"]
ACTIONS = ["start", "repeat", "next_step", "how_far", "where_am_i", "lost", "end", "mute", "unmute", "help",
           "yes", "no", "unknown"]

PLACE_HELP = ("A place id from the building directory, such as a room number (\"107\", \"113A\"), or one of "
              + ", ".join(GENERIC) + " when the user doesn't say which one.")

TOOLS = [
    {"type": "function", "function": {
        "name": "set_location",
        "description": "The user says where they are: a room, door or landmark they are at, next to, or can see.",
        "parameters": {"type": "object", "properties": {
            "place": {"type": "string", "description": PLACE_HELP + " The place they are standing at."},
            "near": {"type": "string", "description": "Another place they say they are near or can see, if any."},
            "door": {"type": "string", "enum": DOORS, "description": "Which of the place's doors, if they say."},
        }, "required": ["place"]}}},
    {"type": "function", "function": {
        "name": "set_destination",
        "description": "The user says where they want to go.",
        "parameters": {"type": "object", "properties": {
            "place": {"type": "string", "description": PLACE_HELP},
        }, "required": ["place"]}}},
    {"type": "function", "function": {
        "name": "command",
        "description": "Anything else the user asks of the walking guide. unknown: not about finding the way.",
        "parameters": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ACTIONS},
        }, "required": ["action"]}}},
]

# --- How people refer to places -------------------------------------------------------------------------------

GENERIC_ARTICLE = {   # "take me to {g}"
    "restroom": ["a bathroom", "the bathroom", "a restroom", "the restroom", "a toilet", "the washroom"],
    "mens-restroom": ["the men's room", "a men's bathroom", "the men's restroom", "the guys' bathroom"],
    "womens-restroom": ["the women's room", "a women's bathroom", "the ladies' room", "the women's restroom"],
    "elevator": ["an elevator", "the elevator", "the elevators", "a lift"],
    "stairs": ["the stairs", "some stairs", "a staircase", "the stairwell"],
    "exit": ["an exit", "the exit", "a way out", "the doors outside", "an entrance"],
}
GENERIC_NOUN = {      # "where's the nearest {n}"
    "restroom": ["bathroom", "restroom", "toilet", "washroom"],
    "mens-restroom": ["men's room", "men's bathroom", "men's restroom"],
    "womens-restroom": ["women's room", "women's bathroom", "ladies' room"],
    "elevator": ["elevator", "lift"],
    "stairs": ["stairs", "staircase", "stairwell"],
    "exit": ["exit", "way out", "entrance"],
}
GENDER_NOUN = {"m": ["men's restroom", "men's room", "men's bathroom"],
               "w": ["women's restroom", "women's room", "ladies' room", "women's bathroom"]}


def load_plan():
    with open(PLAN_JSON, encoding="utf-8") as f:
        plan = json.load(f)
    with open(ALIASES_JSON, encoding="utf-8") as f:
        aliases = json.load(f)["places"]
    nodes = {n["id"]: (n["x"], n["z"]) for n in plan["nodes"]}
    return plan, aliases, nodes


def gender(place):
    return "m" if place["id"].startswith("wc-m") else "w"


def references(place, aliases):
    """Ways to name one particular place."""
    alias = aliases.get(place["id"], {})
    refs = list(alias.get("names", []))
    kind, name, detail = place["kind"], place["name"], place.get("detail") or ""
    if kind == "room":
        code = name.split(" ")[0]
        refs += [f"room {code}", code, f"room {code}"]   # "room N" twice: it's what most people say
        if detail == "Classroom":
            refs.append(f"classroom {code}")
        elif detail == "Lecture hall":
            refs += [f"lecture hall {code}", f"the {code} lecture hall"]
        elif detail.startswith("Inside "):
            refs.append(f"room {code} inside {detail[7:]}")
    elif kind == "restroom":
        for noun in GENDER_NOUN[gender(place)]:
            for where in alias.get("where", []):
                refs.append(f"the {noun} {where}")
    return refs


def door_word(label):
    """"West door" -> "west"; unlabelled doors -> None."""
    word = (label or "").split(" ")[0].lower()
    return word if word in DOORS else None


# --- Speech-recogniser noise ----------------------------------------------------------------------------------

ONES = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"]
TEENS = ["ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"]
TENS = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"]


def two_digits(n, oh):
    if n < 10:
        return ("oh " if oh else "") + ONES[n]
    if n < 20:
        return TEENS[n - 10]
    return TENS[n // 10] + ("" if n % 10 == 0 else " " + ONES[n % 10])


def spoken_number(match, rng):
    """"107" as a recogniser might write it out: "one oh seven", "one zero seven", "one hundred and seven"."""
    digits, letter = match.group(1), match.group(2)
    n = int(digits)
    hundreds, rest = divmod(n, 100)
    style = rng.randrange(4)
    if style == 0:
        words = " ".join(ONES[int(d)] for d in digits)
    elif style == 1 and rest:
        words = f"{ONES[hundreds]} {two_digits(rest, True)}"
    elif rest:
        words = f"{ONES[hundreds]} hundred {'and ' if style == 3 else ''}{two_digits(rest, False)}"
    else:
        words = f"{ONES[hundreds]} hundred"
    return words + (f" {letter.lower()}" if letter else "")


FILLERS = ["um", "uh", "so", "okay", "hey", "um so", "yeah", "hi"]


def noisy(text, rng):
    """Lower case, no punctuation, sometimes numbers in words, fillers and dropped words: what speech-to-text
    hands the app."""
    t = text.lower()
    if rng.random() < 0.35:
        t = re.sub(r"\b(\d{3})([a-f]?)\b", lambda m: spoken_number(m, rng), t)
    elif rng.random() < 0.3:
        t = re.sub(r"\b(\d{3})([a-f])\b", r"\1 \2", t)          # "113a" -> "113 a"
    if rng.random() < 0.5:
        t = t.replace("-", " " if rng.random() < 0.5 else "")    # "north-west" -> "north west" / "northwest"
    if rng.random() < 0.6:
        t = re.sub(r"[^\w\s']", "", t)
    if rng.random() < 0.3:
        t = t.replace("'", "")
    if rng.random() < 0.15:
        t = rng.choice(FILLERS) + " " + t
    if rng.random() < 0.1:
        t = re.sub(r"\bthe ", "", t, count=1)
    return re.sub(r"\s+", " ", t).strip()


# --- Phrasings. The last HELD_OUT of each list are test-only. --------------------------------------------------

HELD_OUT = 2

DEST = ["take me to {d}", "where is {d}", "how do i get to {d}", "i need to get to {d}", "navigate to {d}",
        "find {d}", "directions to {d}", "i'm looking for {d}", "can you take me to {d}", "show me the way to {d}",
        "get me to {d}", "{d}", "where's {d}", "i need to go to {d}", "guide me to {d}", "which way to {d}",
        "i'm trying to find {d}", "could you help me find {d}"]
DEST_CLASS = ["i have class in {d}", "my class is in {d}", "i've got a lecture in {d}", "i'm late for {d}"]
DEST_NEAREST = ["where's the nearest {n}", "closest {n} please", "is there a {n} nearby", "i need a {n}",
                "find the nearest {n}", "nearest {n}", "where can i find a {n}", "take me to the closest {n}"]
DEST_GENERIC = ["take me to {g}", "i need {g}", "where is {g}", "find me {g}", "i'm looking for {g}",
                "how do i get to {g}"]
LOC = ["i'm at {o}", "i'm standing outside {o}", "i'm next to {o}", "i'm by {o}", "right now i'm at {o}",
       "i'm in front of {o}", "currently at {o}", "i am outside {o}", "i'm near {o}", "i just got to {o}",
       "i'm standing by {o}", "i'm right outside {o}", "i'm over by {o}"]
LOC_NEAR = ["i'm at {o}, next to {o2}", "i'm by {o} and i can see {o2}", "i'm outside {o}, {o2} is right there",
            "i'm standing at {o} near {o2}", "i'm at {o}, close to {o2}", "i can see {o} and {o2}"]
LOC_DOOR = ["i'm outside the {door} door of {o}", "i'm at {o}, the {door} door", "i'm by the {door} door of {o}",
            "at the {door} door of {o}", "i'm standing at {o}'s {door} door"]
LOC_GENERIC = ["i see {g}", "i'm next to {g}", "i'm standing by {g}", "there's {g} right in front of me",
               "i can see {g} from here", "i'm at {g}"]
COMBO = ["i'm at {o}, take me to {d}", "from {o} to {d}", "how do i get from {o} to {d}",
         "i'm by {o} and need to get to {d}", "take me to {d}, i'm at {o}", "i'm next to {o}. where is {d}",
         "starting at {o}, going to {d}", "i'm outside {o}, how do i get to {d}", "get me from {o} to {d}"]
ASK_LOCATION = ["Where are you right now?", "Where are you? Tell me a room number or something you can see.",
                "Which door are you next to?", "I couldn't find you. Where are you?"]
ASK_DESTINATION = ["Where do you want to go?", "Where to?", "Which room are you looking for?"]
ANSWER = ["{p}", "uh {p}", "{p} i think", "it's {p}", "{p} please", "um {p}"]
ALIGN_PROMPT = ["Stand outside the door with your back to it, then say start.",
                "Hold your phone up and say start when you're ready."]
COMMANDS = {
    "start": ["start", "let's go", "i'm ready", "okay go", "begin", "ready", "go ahead and start",
              "start navigation", "ok i'm in position", "start the directions"],
    "repeat": ["repeat that", "say that again", "what did you say", "sorry what", "can you repeat that",
               "again please", "come again", "one more time"],
    "next_step": ["what's next", "which way now", "where do i turn", "what do i do now", "next step",
                  "which way do i go", "where to now", "do i turn here"],
    "how_far": ["how far is it", "how much longer", "are we close", "how long will it take", "how many feet left",
                "am i almost there", "how far away is it", "how much further"],
    "where_am_i": ["where am i", "where am i right now", "what's around me", "what room am i near",
                   "where are we", "what's near me", "where am i on the map"],
    "lost": ["i'm lost", "this isn't right", "the arrows are wrong", "i think i went the wrong way", "realign",
             "the path doesn't match the hallway", "i'm confused", "this doesn't look right", "i got lost"],
    "end": ["stop", "cancel", "never mind", "end navigation", "stop navigating", "forget it", "quit directions",
            "i'm done", "cancel the route"],
    "mute": ["mute", "be quiet", "stop talking", "turn off the voice", "quiet please", "silence",
             "no more talking", "turn the sound off"],
    "unmute": ["unmute", "turn the voice back on", "talk to me", "voice on", "read the directions out loud",
               "turn the sound back on", "speak again"],
    "help": ["help", "what can i say", "what can you do", "how does this work", "what can i ask you",
             "how do i use this", "help me"],
    "yes": ["yes", "yeah", "yep", "correct", "that's right", "sure", "uh huh", "yes that one"],
    "no": ["no", "nope", "that's wrong", "not that one", "no that's not it", "wrong one", "no no"],
    "unknown": ["what's the weather", "tell me a joke", "what time is it", "play some music",
                "who won the game last night", "call mom", "order a pizza", "set a timer for ten minutes",
                "what's two plus two", "send a text to sam"],
}


def split_of(templates, template):
    return "test" if templates.index(template) >= len(templates) - HELD_OUT else "train"


def joined(*splits):
    return "test" if "test" in splits else "train"


def call(name, **arguments):
    return {"name": name, "arguments": {k: v for k, v in arguments.items() if v}}


class Builder:
    def __init__(self, seed):
        self.rng = random.Random(seed)
        self.rows = []
        self.seen = set()

    def add(self, split, intent, text, calls, context=None, clean=False):
        text = text if clean else noisy(text, self.rng)
        key = (context, text)
        if key in self.seen:
            return
        self.seen.add(key)
        self.rows.append({"split": split, "source": "template", "intent": intent, "context": context,
                          "text": text, "calls": calls})

    def pick(self, items):
        return self.rng.choice(items)


def build(seed):
    plan, aliases, nodes = load_plan()
    places = plan["places"]
    b = Builder(seed)
    refs = {p["id"]: references(p, aliases) for p in places}
    # Places someone in the hallway can stand at or see: not the rooms inside other rooms.
    standable = [p for p in places if not (p.get("detail") or "").startswith("Inside ")]

    def door_xy(place):
        return nodes[place["doors"][0]["node"]]

    def neighbours(place, metres=14.0):
        """Places whose door is within metres of this one's: what a user here could also see."""
        x, z = door_xy(place)
        return [q for q in standable if q is not place and
                ((door_xy(q)[0] - x) ** 2 + (door_xy(q)[1] - z) ** 2) ** 0.5 < metres]

    # Where to: every place, several phrasings each.
    for p in places:
        for _ in range(8):
            t = b.pick(DEST)
            b.add(split_of(DEST, t), "destination", t.format(d=b.pick(refs[p["id"]])),
                  [call("set_destination", place=p["id"])])
        if p["kind"] == "room" and p.get("detail") in ("Classroom", "Lecture hall"):
            for _ in range(2):
                t = b.pick(DEST_CLASS)
                b.add(split_of(DEST_CLASS, t), "destination", t.format(d=b.pick(refs[p["id"]])),
                      [call("set_destination", place=p["id"])])

    # Where to, without saying which one: the app takes the nearest.
    for g in GENERIC:
        for _ in range(14):
            t = b.pick(DEST_NEAREST)
            b.add(split_of(DEST_NEAREST, t), "destination", t.format(n=b.pick(GENERIC_NOUN[g])),
                  [call("set_destination", place=g)])
            t = b.pick(DEST_GENERIC)
            b.add(split_of(DEST_GENERIC, t), "destination", t.format(g=b.pick(GENERIC_ARTICLE[g])),
                  [call("set_destination", place=g)])

    # Where I am.
    for p in standable:
        for _ in range(5):
            t = b.pick(LOC)
            b.add(split_of(LOC, t), "location", t.format(o=b.pick(refs[p["id"]])),
                  [call("set_location", place=p["id"])])
        near = neighbours(p)
        for _ in range(3 if near else 0):
            q = b.pick(near)
            t = b.pick(LOC_NEAR)
            b.add(split_of(LOC_NEAR, t), "location",
                  t.format(o=b.pick(refs[p["id"]]), o2=b.pick(refs[q["id"]])),
                  [call("set_location", place=p["id"], near=q["id"])])
        for door in p["doors"]:
            word = door_word(door.get("label"))
            if word:
                for _ in range(2):
                    t = b.pick(LOC_DOOR)
                    b.add(split_of(LOC_DOOR, t), "location", t.format(o=b.pick(refs[p["id"]]), door=word),
                          [call("set_location", place=p["id"], door=word)])
    for g in GENERIC:
        for _ in range(8):
            t = b.pick(LOC_GENERIC)
            b.add(split_of(LOC_GENERIC, t), "location", t.format(g=b.pick(GENERIC_ARTICLE[g])),
                  [call("set_location", place=g)])

    # Both at once. The app reads set_location first, so that's the order.
    for _ in range(500):
        o, d = b.pick(standable), b.pick(places)
        if o is d:
            continue
        t = b.pick(COMBO)
        b.add(split_of(COMBO, t), "both", t.format(o=b.pick(refs[o["id"]]), d=b.pick(refs[d["id"]])),
              [call("set_location", place=o["id"]), call("set_destination", place=d["id"])])
    for _ in range(80):
        o, g = b.pick(standable), b.pick(GENERIC)
        t = b.pick(COMBO)
        b.add(split_of(COMBO, t), "both", t.format(o=b.pick(refs[o["id"]]), d=b.pick(GENERIC_ARTICLE[g])),
              [call("set_location", place=o["id"]), call("set_destination", place=g)])

    # A bare answer means whatever the app just asked.
    for _ in range(260):
        p = b.pick(standable)
        t = b.pick(ANSWER)
        b.add(split_of(ANSWER, t), "location", t.format(p=b.pick(refs[p["id"]])),
              [call("set_location", place=p["id"])], context=b.pick(ASK_LOCATION))
    for _ in range(260):
        p = b.pick(places)
        t = b.pick(ANSWER)
        b.add(split_of(ANSWER, t), "destination", t.format(p=b.pick(refs[p["id"]])),
              [call("set_destination", place=p["id"])], context=b.pick(ASK_DESTINATION))

    # "Which one?" for places that share a kind.
    groups = {}
    for p in standable:
        if p["kind"] == "restroom":
            groups.setdefault(gender(p) + "-restroom", []).append(p)
        elif p["kind"] in ("elevator", "stairs", "exit"):
            groups.setdefault(p["kind"], []).append(p)
    ordinals = ["the first one", "the second one", "the third one"]
    for kind, members in groups.items():
        for _ in range(40):
            options = b.rng.sample(members, min(len(members), b.pick([2, 2, 3])))
            if len(options) < 2:
                continue
            asking_location = b.rng.random() < 0.5
            descriptions = [b.pick(refs[o["id"]]) for o in options]
            question = (("Which one are you at: " if asking_location else "Which one do you want: ")
                        + ", or ".join(descriptions) + "?")
            pick = b.rng.randrange(len(options))
            answer = descriptions[pick] if b.rng.random() < 0.7 else ordinals[pick]
            name = "set_location" if asking_location else "set_destination"
            b.add("train" if b.rng.random() < 0.85 else "test", "choice", answer,
                  [call(name, place=options[pick]["id"])], context=question)

    # Commands, with and without the app having just spoken.
    for action, phrases in COMMANDS.items():
        for t in phrases:
            for _ in range(3):
                b.add(split_of(phrases, t), "command", t, [call("command", action=action)])
            b.add(split_of(phrases, t), "command", t, [call("command", action=action)],
                  context=b.pick(ALIGN_PROMPT if action == "start" else ASK_LOCATION + ASK_DESTINATION))

    for i, row in enumerate(b.rows):
        row["id"] = i
    return b.rows


def write(rows):
    os.makedirs(DATA_DIR, exist_ok=True)
    for split in ("train", "test"):
        with open(os.path.join(DATA_DIR, f"{split}.jsonl"), "w", encoding="utf-8") as f:
            for row in rows:
                if row["split"] == split:
                    f.write(json.dumps(row, ensure_ascii=False) + "\n")
    with open(os.path.join(DATA_DIR, "tools.json"), "w", encoding="utf-8") as f:
        json.dump({"developer": DEVELOPER, "tools": TOOLS}, f, indent=1)


def push(repo):
    from huggingface_hub import HfApi   # only needed here: pip install huggingface_hub

    api = HfApi()
    api.create_repo(repo, repo_type="dataset", private=True, exist_ok=True)
    api.upload_folder(repo_id=repo, repo_type="dataset", folder_path=DATA_DIR, allow_patterns=["*.jsonl", "*.json"])
    handwritten = os.path.join(HERE, "handwritten.jsonl")
    if os.path.exists(handwritten):
        api.upload_file(path_or_fileobj=handwritten, path_in_repo="handwritten.jsonl", repo_id=repo,
                        repo_type="dataset")
    print(f"Pushed to https://huggingface.co/datasets/{repo}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--seed", type=int, default=7)
    parser.add_argument("--push", metavar="USER/REPO", help="upload data/ to this private dataset repo")
    args = parser.parse_args()
    rows = build(args.seed)
    write(rows)
    counts = {}
    for row in rows:
        counts.setdefault(row["split"], {}).setdefault(row["intent"], 0)
        counts[row["split"]][row["intent"]] += 1
    for split, by_intent in sorted(counts.items()):
        print(f"{split}: {sum(by_intent.values())} rows  " + "  ".join(f"{k} {v}" for k, v in sorted(by_intent.items())))
    if args.push:
        push(args.push)


if __name__ == "__main__":
    main()
