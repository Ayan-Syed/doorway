"""Doorway's walk logs (Assets/Navigation/Scripts/PoseLog.cs) as plain rows, and what each log says about how the
positioning did. Plain Python and the standard library, so it runs the same on a laptop, in Tools/tests, and on
Databricks (ingest.py imports it); nothing here talks to Databricks.

A log is one CSV per walk or indoor session, pulled off the phone with
  adb pull /sdcard/Android/data/com.ayansyed.doorway/files/walks
Its first line names the kind:
  # Doorway walk from <door node> to <place>, <app version>      Navigator's walk: the guided position each frame
  # Doorway indoors, <building> <floor>, <app version>            the Localizer's session: its inputs and outputs
The two kinds share row letters with different columns ('s', 'r' and 'e'), so rows are read by the log's kind.

parse(text, name) -> Log; summary(log) -> one dict of numbers per log; checkpoint_errors(log) -> how far the
position the app showed was from the door the user said they were at (an indoor log's 'c' rows: the truth).
"""
import math
import os
from dataclasses import dataclass, field

WALK = "walk"
INDOORS = "indoors"

# LocalizerState, in its enum's order (Localizer.Engine.cs).
STATES = ["Outdoors", "Converging", "Localized", "Lost"]

# Columns after the row letter, per kind of log and row (PoseLog.cs's header comment, and the code that writes them).
# A name ending in '$' is text; the rest are numbers ("nan" in the file is NaN). Rows of variable length (depth scans,
# walls, a snap's alternative doors) keep their fixed columns and count the rest.
COLUMNS = {
    WALK: {
        "b": "t yaw camx camz x z endx endz",
        "f": "t state tracking cx cy cz qx qy qz qw rawx rawz rawh x z h spread confidence progress offroute floor",
        "s": "t text$ bearing range sigma used cx cy cz dx dy dz distance dsigma",
        "r": "t endx endz",
        "e": "t what$",
    },
    INDOORS: {
        "n": "t door$ x z yaw camx camz alternatives",
        "l": "t",
        "o": "t",
        "a": "t tracking cx cy cz yaw",
        "d": "t source$ points",
        "p": "t walls",
        "s": "t text$ camx camz bearing range sigma measured digitpixels focalpixels distance",
        "m": "t compass",
        "k": "t compass length",
        "q": "t max",
        "c": "t door$ x z",
        "r": "t state x z heading spread confidence particles walls ms",
        "D": "t source$ points bestmean hitshare ms",
        "g": "t x z accuracy",
        "e": "t what$",
    },
}

# The walk's own guided frames, five a second or so: a gap longer than this is a pause, not walking.
MAX_STEP_SECONDS = 2.0
# Off the route by more than this many metres counts as off it (NavigationScreen's off-route warning starts there).
OFF_ROUTE_M = 3.0
# The position the app showed at a checkpoint: its last estimate at most this many seconds before the tap.
CHECKPOINT_WINDOW_S = 2.0


@dataclass
class Log:
    name: str                     # the file's name without .csv: when it began, yyyyMMdd-HHmmss[-n]
    kind: str                     # WALK or INDOORS
    version: str                  # the app's version
    origin: str = ""              # a walk: the door node it began at
    destination: str = ""         # a walk: the place it went to
    place: str = ""               # an indoor session: the building and floor
    rows: dict = field(default_factory=dict)    # row letter -> list of dicts, in the file's order
    unknown: int = 0              # rows of a letter this parser doesn't know (a newer app)


def _number(text):
    try:
        return float(text)
    except ValueError:
        return math.nan


def _header(first, name):
    line = first.lstrip("#").strip()
    if not line.startswith("Doorway "):
        raise ValueError(f"{name}: not a Doorway log (first line {first!r})")
    body, _, version = line[len("Doorway "):].rpartition(", ")
    if not body:
        body, version = version, ""
    if body.startswith("walk from "):
        origin, _, destination = body[len("walk from "):].partition(" to ")
        return Log(name, WALK, version, origin=origin, destination=destination)
    if body.startswith("indoors"):
        return Log(name, INDOORS, version, place=body[len("indoors"):].lstrip(", ").strip())
    raise ValueError(f"{name}: unknown kind of Doorway log {body!r}")


def parse(text, name="walk"):
    """A log's text as a Log. name: the file's name (a path is fine; the folder and .csv are dropped)."""
    name = os.path.splitext(os.path.basename(name))[0]
    lines = text.splitlines()
    if not lines:
        raise ValueError(f"{name}: empty")
    log = _header(lines[0], name)
    specs = {letter: cols.split() for letter, cols in COLUMNS[log.kind].items()}
    for line in lines[1:]:
        if not line or line.startswith("#"):
            continue
        parts = line.split(",")
        spec = specs.get(parts[0])
        if spec is None:
            log.unknown += 1
            continue
        values = parts[1:]
        row = {}
        for i, column in enumerate(spec):
            raw = values[i] if i < len(values) else ""
            if column.endswith("$"):
                row[column[:-1]] = raw.strip()
            else:
                row[column] = _number(raw) if raw != "" else math.nan
        row["extra"] = max(0, len(values) - len(spec))
        log.rows.setdefault(parts[0], []).append(row)
    return log


def read(path):
    with open(path, encoding="utf-8") as f:
        return parse(f.read(), path)


def _finite(values):
    return [v for v in values if isinstance(v, float) and math.isfinite(v)]


def _percentile(values, q):
    """numpy's default (linear) percentile, q in 0..100; NaN for no values."""
    v = sorted(_finite(values))
    if not v:
        return math.nan
    k = (len(v) - 1) * q / 100.0
    lo, hi = math.floor(k), math.ceil(k)
    return v[lo] + (v[hi] - v[lo]) * (k - lo)


def _timed_share(rows, test):
    """The share of the time (between consecutive rows, pauses left out) that test(row) holds."""
    total = hit = 0.0
    for a, b in zip(rows, rows[1:]):
        dt = b["t"] - a["t"]
        if not math.isfinite(dt) or dt <= 0 or dt > MAX_STEP_SECONDS:
            continue
        total += dt
        if test(a):
            hit += dt
    return hit / total if total > 0 else math.nan


def _events(log, prefix):
    return sum(1 for e in log.rows.get("e", []) if e["what"].startswith(prefix))


def checkpoint_errors(log):
    """An indoor session's checkpoints: per 'c' row, the door, the state and the metres between the position the
    app showed (its last 'r' estimate within CHECKPOINT_WINDOW_S before the tap) and the door's spot. NaN error
    where there was no estimate then. Walk logs have no checkpoints."""
    if log.kind != INDOORS:
        return []
    estimates = log.rows.get("r", [])
    out = []
    for c in log.rows.get("c", []):
        before = [r for r in estimates if r["t"] <= c["t"] and c["t"] - r["t"] <= CHECKPOINT_WINDOW_S]
        r = before[-1] if before else None
        error = math.hypot(r["x"] - c["x"], r["z"] - c["z"]) if r else math.nan
        state = STATES[int(r["state"])] if r and math.isfinite(r["state"]) and 0 <= r["state"] < len(STATES) else ""
        out.append({"log": log.name, "version": log.version, "place": log.place, "t": c["t"], "door": c["door"],
                    "x": c["x"], "z": c["z"], "error_m": error, "state": state,
                    "spread": r["spread"] if r else math.nan})
    return out


def summary(log):
    """One row of numbers about a log, for a table of logs. Keys are the same for both kinds; a number that doesn't
    apply to the kind is NaN (or 0 for counts)."""
    s = {"log": log.name, "kind": log.kind, "version": log.version, "origin": log.origin,
         "destination": log.destination, "place": log.place, "unknown_rows": log.unknown}
    times = [r["t"] for rows in log.rows.values() for r in rows if math.isfinite(r.get("t", math.nan))]
    s["duration_s"] = max(times) - min(times) if times else 0.0
    s["arrived"] = _events(log, "arrived") > 0
    s["realigns"] = _events(log, "realign")
    s["reroutes"] = len(log.rows.get("r", [])) if log.kind == WALK else _events(log, "rerouted")

    nan = math.nan
    s.update(walked_m=nan, tracking_share=nan, off_route_share=nan, spread_median_m=nan, raw_gap_median_m=nan,
             localized_share=nan, lost_share=nan, checkpoints=0, checkpoint_error_median_m=nan,
             checkpoint_error_p75_m=nan, checkpoint_error_max_m=nan, signs_read=0, signs_used=0)

    if log.kind == WALK:
        frames = log.rows.get("f", [])
        walked = 0.0
        for a, b in zip(frames, frames[1:]):
            step = math.hypot(b["x"] - a["x"], b["z"] - a["z"])
            if math.isfinite(step) and 0 < b["t"] - a["t"] <= MAX_STEP_SECONDS:
                walked += step
        s["walked_m"] = walked
        s["tracking_share"] = _timed_share(frames, lambda f: f["tracking"] == 1)
        s["off_route_share"] = _timed_share(frames, lambda f: math.isfinite(f["offroute"]) and f["offroute"] > OFF_ROUTE_M)
        s["spread_median_m"] = _percentile([f["spread"] for f in frames if f["spread"] >= 0], 50)
        # How far the guided position moved from Begin's alignment alone: what the filter (walls, signs) changed.
        s["raw_gap_median_m"] = _percentile([math.hypot(f["x"] - f["rawx"], f["z"] - f["rawz"]) for f in frames], 50)
        signs = log.rows.get("s", [])
        s["signs_read"] = len(signs)
        s["signs_used"] = sum(1 for x in signs if x["used"] == 1)
    else:
        estimates = log.rows.get("r", [])
        s["localized_share"] = _timed_share(estimates, lambda r: r["state"] == STATES.index("Localized"))
        s["lost_share"] = _timed_share(estimates, lambda r: r["state"] == STATES.index("Lost"))
        s["spread_median_m"] = _percentile([r["spread"] for r in estimates], 50)
        frames = log.rows.get("a", [])
        s["tracking_share"] = _timed_share(frames, lambda a: a["tracking"] == 1)
        s["signs_read"] = len(log.rows.get("s", []))
        errors = [c["error_m"] for c in checkpoint_errors(log)]
        s["checkpoints"] = len(errors)
        s["checkpoint_error_median_m"] = _percentile(errors, 50)
        s["checkpoint_error_p75_m"] = _percentile(errors, 75)
        s["checkpoint_error_max_m"] = max(_finite(errors)) if _finite(errors) else math.nan
    return s
