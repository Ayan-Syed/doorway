"""Builds all of BA1 floor 1's app data in the right order, then checks the result.

  python make_data.py            labels -> segment -> rooms -> graph --export -> basemap -> walls
  python make_data.py --campus   the same, then Tools/campus/campus.py (it reads the new JSON's geo)

Order matters: graph.py --export writes labels without areas and a new place order, so basemap.py must
run after it, or the labels lose their areas and places.bytes points at the wrong places.
Stops at the first step that fails.
"""
import collections
import json
import os
import struct
import subprocess
import sys

import graph

STEPS = [
    ("labels.py", []),
    ("segment.py", []),
    ("rooms.py", []),
    ("graph.py", ["--export"]),
    ("basemap.py", []),
    ("walls.py", []),
]
CAMPUS = os.path.join(graph.BOX, "Tools", "campus", "campus.py")


def run(script, args):
    path = script if os.path.isabs(script) else os.path.join(graph.HERE, script)
    print(f"== {os.path.basename(path)} {' '.join(args)}".rstrip(), flush=True)
    result = subprocess.run([sys.executable, path] + args, cwd=os.path.dirname(path))
    if result.returncode != 0:
        sys.exit(f"{os.path.basename(path)} failed (exit {result.returncode}); stopping.")


def verify():
    """The plan's own consistency: every reference resolves, labels have areas, the grids fit."""
    with open(graph.PLAN_JSON, encoding="utf-8") as f:
        plan = json.load(f)
    ids = {n["id"] for n in plan["nodes"]}
    problems = [f"edge {e['a']}-{e['b']}" for e in plan["edges"] if e["a"] not in ids or e["b"] not in ids]
    problems += [f"door {d['node']} of {p['id']}" for p in plan["places"] for d in p["doors"] if d["node"] not in ids]
    problems += [f"label {l['text']} has no area" for l in plan["labels"] if "area" not in l]
    with open(os.path.join(graph.DATA_DIR, "ba1-floor1-places.bytes"), "rb") as f:
        raw = f.read()
    w, h = struct.unpack("<HH", raw[:4])
    if len(raw) != 4 + w * h:
        problems.append(f"places.bytes is {len(raw)} bytes, not {4 + w * h}")
    if max(raw[4:]) > len(plan["places"]):
        problems.append("places.bytes names a place the JSON doesn't have")
    groups = graph.components({n["id"]: None for n in plan["nodes"]}, [(e["a"], e["b"]) for e in plan["edges"]])
    if len(groups) != 1:
        problems.append(f"the walkway graph is in {len(groups)} pieces")
    kinds = collections.Counter(p["kind"] for p in plan["places"])
    doors = sum(len(p["doors"]) for p in plan["places"])
    print(f"ba1-floor1.json: {len(plan['nodes'])} nodes, {len(plan['edges'])} edges, {len(plan['places'])} places "
          f"({', '.join(f'{v} {k}' for k, v in kinds.most_common())}), {doors} doors, {len(plan['labels'])} labels")
    if problems:
        sys.exit("Data problems:\n  " + "\n  ".join(problems))


def main():
    for script, args in STEPS:
        run(script, args)
    verify()
    if "--campus" in sys.argv:
        run(CAMPUS, [])
    print("done")


if __name__ == "__main__":
    main()
