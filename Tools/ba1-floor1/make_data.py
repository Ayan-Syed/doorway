"""Builds all of BA1 floor 1's app data in the right order, then checks the result.

  python make_data.py            labels -> segment -> rooms -> graph --export -> basemap -> walls -> walkable
  python make_data.py --campus   the same, then Tools/campus/campus.py (it reads the new JSON's geo)

Order matters: graph.py --export writes labels without areas and a new place order, so basemap.py must
run after it, or the labels lose their areas and places.bytes points at the wrong places; walkable.py cuts
the exported doors into the walls. Stops at the first step that fails, then runs every check in datacheck.py
(the same ones the app's build runs).
"""
import os
import subprocess
import sys

import datacheck
import graph

STEPS = [
    ("labels.py", []),
    ("segment.py", []),
    ("rooms.py", []),
    ("graph.py", ["--export", "--pipeline"]),
    ("basemap.py", []),
    ("walls.py", []),
    ("walkable.py", []),
]
CAMPUS = os.path.join(graph.BOX, "Tools", "campus", "campus.py")


def run(script, args):
    path = script if os.path.isabs(script) else os.path.join(graph.HERE, script)
    print(f"== {os.path.basename(path)} {' '.join(args)}".rstrip(), flush=True)
    result = subprocess.run([sys.executable, path] + args, cwd=os.path.dirname(path))
    if result.returncode != 0:
        sys.exit(f"{os.path.basename(path)} failed (exit {result.returncode}); stopping.")


def verify():
    print(datacheck.summary())
    problems = datacheck.problems()
    if problems:
        sys.exit("Data problems:\n  " + "\n  ".join(problems))


def main():
    for script, args in STEPS:
        run(script, args)
    if "--campus" in sys.argv:
        run(CAMPUS, [])
    verify()
    print("done")


if __name__ == "__main__":
    main()
