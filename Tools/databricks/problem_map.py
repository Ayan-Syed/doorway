"""Where on the floor the positioning goes wrong, from indoor sessions' logs (doorway_logs): the floor cut into
squares, each with how long the app spent there Lost, still Converging, or Localized and how unsure it was; and every
door someone said they were at, with how far off the app was there. Then both drawn over the floor's own picture.

  squares(logs)        -> rows: place, the square's middle, seconds, the share of them Lost / Converging, spread
  doors(logs)          -> rows: place, door, checkpoints, error median / p75 / max, how often Localized and > 3 m off
  plans(data_dir)      -> place -> the floor's picture and where it sits, from Assets/Navigation/Data/*.json
  draw(plan, squares, doors, path)   -> a PNG (needs matplotlib)

A square's figures come from where the app *said* the user was. That's right while it's Localized; while it's Lost
the estimate can be anywhere, so a red square says "the app was lost while it thought you were here", which is
where to look, not proof of where the trouble is. The doors are the truth: there the user said where they were.
Plain Python but for draw, so Tools/tests runs it without Databricks; ingest.py runs it on Databricks.
"""
import glob
import json
import math
import os

import doorway_logs as dl

SQUARE_M = 2.0            # the squares' size: a hallway's width, about
MIN_SECONDS = 3.0         # a square needs this long of estimates to be drawn: less is a walk past
WRONG_M = 3.0             # Localized and off by more than this: confident and wrong (as ReplayEvaluator counts it)


def _timed(rows):
    """(row, seconds until the next row) for each row but the last, pauses (dl.MAX_STEP_SECONDS) left out."""
    for a, b in zip(rows, rows[1:]):
        dt = b["t"] - a["t"]
        if math.isfinite(dt) and 0 < dt <= dl.MAX_STEP_SECONDS:
            yield a, dt


def squares(logs, size=SQUARE_M):
    """Per (place, square) the indoor sessions' estimates fell in: seconds there, the share of them Lost and
    Converging, the median spread while Localized, and how many sessions passed. Squares under MIN_SECONDS out."""
    acc = {}
    lost, converging, localized = (dl.STATES.index(s) for s in ("Lost", "Converging", "Localized"))
    for log in logs:
        if log.kind != dl.INDOORS:
            continue
        for r, dt in _timed(log.rows.get("r", [])):
            if not (math.isfinite(r["x"]) and math.isfinite(r["z"])):
                continue
            key = (log.place, math.floor(r["x"] / size), math.floor(r["z"] / size))
            a = acc.setdefault(key, {"seconds": 0.0, "lost": 0.0, "converging": 0.0, "spreads": [], "logs": set()})
            a["seconds"] += dt
            a["lost"] += dt if r["state"] == lost else 0.0
            a["converging"] += dt if r["state"] == converging else 0.0
            if r["state"] == localized and math.isfinite(r["spread"]):
                a["spreads"].append(r["spread"])
            a["logs"].add(log.name)
    out = []
    for (place, i, j), a in sorted(acc.items()):
        if a["seconds"] < MIN_SECONDS:
            continue
        out.append({"place": place, "x": (i + 0.5) * size, "z": (j + 0.5) * size, "size_m": size,
                    "seconds": a["seconds"], "lost_share": a["lost"] / a["seconds"],
                    "converging_share": a["converging"] / a["seconds"],
                    "spread_median_m": dl._percentile(a["spreads"], 50), "sessions": len(a["logs"])})
    return out


def doors(logs):
    """Per (place, door) with checkpoints: how many, where, the error's median, p75 and max, and how many were
    confident and wrong (Localized, more than WRONG_M off). Worst p75 first."""
    acc = {}
    for log in logs:
        for c in dl.checkpoint_errors(log):
            acc.setdefault((c["place"], c["door"]), []).append(c)
    out = []
    for (place, door), cs in acc.items():
        errors = [c["error_m"] for c in cs]
        finite = dl._finite(errors)
        out.append({"place": place, "door": door, "x": cs[0]["x"], "z": cs[0]["z"], "checkpoints": len(cs),
                    "error_median_m": dl._percentile(errors, 50), "error_p75_m": dl._percentile(errors, 75),
                    "error_max_m": max(finite) if finite else math.nan,
                    "confident_wrong": sum(1 for c in cs if c["state"] == "Localized" and c["error_m"] > WRONG_M)})
    return sorted(out, key=lambda d: -d["error_p75_m"] if math.isfinite(d["error_p75_m"]) else math.inf)


def plans(data_dir):
    """place ("Business Administration I Floor 1", as indoor logs name it) -> the floor's picture (the app's
    light map if it's there, else the plan) and where it sits: left, top, width_m, height_m (plan metres)."""
    out = {}
    for path in sorted(glob.glob(os.path.join(data_dir, "*.json"))):
        try:
            with open(path, encoding="utf-8") as f:
                plan = json.load(f)
        except (OSError, ValueError):
            continue
        image = plan.get("image") if isinstance(plan, dict) else None
        if not image or not plan.get("building"):
            continue
        stem = os.path.splitext(path)[0]
        picture = next((p for p in (f"{stem}-map-iris.png", f"{stem}-plan.png") if os.path.isfile(p)), None)
        mpp = image["metersPerPixel"]
        out[f"{plan['building']} {plan.get('floor', '')}".strip()] = {
            "picture": picture, "left": image["left"], "top": image["top"],
            "width_m": image["width"] * mpp, "height_m": image["height"] * mpp}
    return out


def draw(place, plan, place_squares, place_doors, path):
    """The floor's picture, its squares tinted by the share of time Lost (clear to red), and its doors as circles
    sized and labelled by their p75 error; to path as a PNG. Returns path."""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.patches import Rectangle

    left, top = plan["left"], plan["top"]
    right, bottom = left + plan["width_m"], top - plan["height_m"]
    fig, ax = plt.subplots(figsize=(12, 12 * plan["height_m"] / plan["width_m"] + 0.8), dpi=110)
    if plan.get("picture"):
        ax.imshow(plt.imread(plan["picture"]), extent=(left, right, bottom, top), zorder=0)
    cmap = plt.get_cmap("Reds")
    for s in place_squares:
        half = s["size_m"] / 2
        ax.add_patch(Rectangle((s["x"] - half, s["z"] - half), s["size_m"], s["size_m"], zorder=1, linewidth=0,
                               facecolor=cmap(0.25 + 0.75 * s["lost_share"]), alpha=0.15 + 0.6 * s["lost_share"]))
    for d in place_doors:
        error = d["error_p75_m"]
        if not math.isfinite(error):
            continue
        colour = "#b3364a" if error > WRONG_M else "#424874"
        ax.scatter([d["x"]], [d["z"]], s=60 + 40 * error, facecolor="white", edgecolor=colour, linewidth=2.5, zorder=3)
        ax.annotate(f"{d['door']}  {error:.1f} m" + (f"  ×{d['checkpoints']}" if d["checkpoints"] > 1 else ""),
                    (d["x"], d["z"]), xytext=(8, 6), textcoords="offset points", fontsize=9, color=colour,
                    fontweight="bold", zorder=4,
                    bbox={"boxstyle": "round,pad=0.2", "facecolor": "white", "edgecolor": "none", "alpha": 0.85})
    ax.set_xlim(left, right)
    ax.set_ylim(bottom, top)
    ax.set_aspect("equal")
    ax.set_xticks([])
    ax.set_yticks([])
    seconds = sum(s["seconds"] for s in place_squares)
    ax.set_title(f"{place}: where the app was lost (red squares, {SQUARE_M:g} m) and how far off it was at each "
                 f"checkpoint (p75)\n{len(place_doors)} doors, {seconds / 60:.0f} min of estimates", fontsize=11, loc="left")
    fig.tight_layout()
    fig.savefig(path)
    plt.close(fig)
    return path
