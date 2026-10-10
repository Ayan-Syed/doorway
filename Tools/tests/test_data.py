"""The shipped data's invariants: what FloorPlan.Validate and the app's readers need, and the pipeline's own rules."""
import collections
import math
import re

import numpy as np
import pytest

import datacheck
import doors
import graph
import walkable

EXIT_KIND = "exit"


def test_counts(plan):
    kinds = collections.Counter(p["kind"] for p in plan["places"])
    assert (len(plan["nodes"]), len(plan["edges"]), len(plan["places"])) == (102, 122, 71)
    assert kinds == {"room": 54, "exit": 7, "restroom": 4, "stairs": 4, "elevator": 2}
    assert sum(len(p["doors"]) for p in plan["places"]) == 79
    assert len(plan["labels"]) == 54
    assert len(plan["signs"]) == 40


def test_plan_rules(plan):
    assert datacheck.plan_problems(plan) == []


def test_every_file_reads_and_agrees():
    assert datacheck.problems() == []


def test_place_grid_is_for_these_places(plan):
    _, _, digest, cells = datacheck.read_places(datacheck.read(datacheck.PLACES))
    assert digest == graph.place_id_hash([p["id"] for p in plan["places"]])
    assert cells.max() <= len(plan["places"])
    swapped = [p["id"] for p in plan["places"]]
    swapped[0], swapped[1] = swapped[1], swapped[0]
    assert graph.place_id_hash(swapped) != digest


def test_door_directions_follow_the_walls(plan, regions):
    """Every door faces a multiple of 45 degrees, and where the wall in front can be measured and is on that
    grid, the door faces straight into it (within 5 degrees)."""
    R, _, dark, _ = regions
    ink = doors.Ink(dark)
    nodes = {n["id"]: (n["x"] * graph.PX_PER_M + graph.ORIGIN[0], -n["z"] * graph.PX_PER_M + graph.ORIGIN[1])
             for n in plan["nodes"]}
    bad = []
    for p in plan["places"]:
        for d in p["doors"]:
            if datacheck.off_grid(d["dx"], d["dz"]) > 5:
                bad.append(f"{d['node']} of {p['id']} is off the 45-degree grid")
                continue
            x, y = nodes[d["node"]]
            (fx, fy), _, measured, snapped = doors.fit_direction(R, ink, x, y, d["dx"], -d["dz"])
            if snapped:
                off = math.degrees(math.acos(max(-1.0, min(1.0, fx * d["dx"] - fy * d["dz"]))))
                if off > 5:
                    bad.append(f"{d['node']} of {p['id']} is {off:.0f} deg from its wall's normal")
    assert bad == []


def test_links_stay_on_the_walkway(plan, regions):
    """No link crosses a wall or a room: sampled every pixel, 10 px clear of its ends (door spots sit by doors).
    Links out through the entrances end outside, so those are left out."""
    R = regions[0]
    exits = {d["node"] for p in plan["places"] if p["kind"] == EXIT_KIND for d in p["doors"]}
    nodes = {n["id"]: (n["x"] * graph.PX_PER_M + graph.ORIGIN[0], -n["z"] * graph.PX_PER_M + graph.ORIGIN[1])
             for n in plan["nodes"]}
    crossing = []
    for e in plan["edges"]:
        if e["a"] in exits or e["b"] in exits:
            continue
        (x0, y0), (x1, y1) = nodes[e["a"]], nodes[e["b"]]
        length = math.hypot(x1 - x0, y1 - y0)
        for i in range(int(length) + 1):
            t = i / max(int(length), 1)
            if t * length < 10 or (1 - t) * length < 10:
                continue
            if R[int(round(y0 + (y1 - y0) * t)), int(round(x0 + (x1 - x0) * t))] != 2:
                crossing.append(f"{e['a']}-{e['b']}")
                break
    assert crossing == []


def test_signs(plan):
    nodes = {n["id"]: (n["x"], n["z"]) for n in plan["nodes"]}
    places = {p["id"]: p for p in plan["places"]}
    walk = datacheck.read_walkable(datacheck.read(datacheck.WALKABLE))
    for s in plan["signs"]:
        assert re.match(r"^\d{3}[A-Z]?$", s["text"]), s
        door = next(d for d in places[s["place"]]["doors"] if d["node"] == s["door"])
        # The plate faces the hallway: straight back out of the door.
        assert abs(s["nx"] + door["dx"]) < 1e-3 and abs(s["nz"] + door["dz"]) < 1e-3, s
        # Beside its door: a step or two from the door spot (3.7 m at most, where 145's open reception starts).
        assert math.dist((s["x"], s["z"]), nodes[s["door"]]) < 4.0, s
        # Someone can stand in front of it.
        assert datacheck.walkable_at(walk, s["x"] + s["nx"] * 0.4, s["z"] + s["nz"] * 0.4), s
    texts = {s["text"] for s in plan["signs"]}
    # Every room with its own door, and the restrooms', stairs' and lift's printed numbers.
    own = {p["id"] for p in plan["places"] if p["kind"] == "room" and not p["detail"].startswith("Inside")}
    assert own <= texts
    assert {"123", "125", "140", "141", "118", "120", "127", "129", "107A"} <= texts


def test_walkable_grid(plan, regions):
    R, wall, dark, names = regions
    left, top, cell, grid = datacheck.read_walkable(datacheck.read(datacheck.WALKABLE))
    assert cell == pytest.approx(0.1)
    assert (left, top) == pytest.approx((plan["image"]["left"], plan["image"]["top"]))
    ink = doors.Ink(dark)
    nodes_px = {n["id"]: (n["x"] * graph.PX_PER_M + graph.ORIGIN[0], -n["z"] * graph.PX_PER_M + graph.ORIGIN[1])
                for n in plan["nodes"]}
    free, cut, cuts = walkable.build(R, wall, names, plan, nodes_px, ink)
    rebuilt = walkable.to_cells(free, left, top, grid.shape[1], grid.shape[0])
    assert (rebuilt == grid).all(), "the shipped grid isn't what walkable.py makes from this plan"
    h_px, w_px = R.shape
    cx = np.clip(np.round((left + (np.arange(grid.shape[1]) + 0.5) * cell) * graph.PX_PER_M
                          + graph.ORIGIN[0]).astype(int), 0, w_px - 1)
    cy = np.clip(np.round(-(top - (np.arange(grid.shape[0]) + 0.5) * cell) * graph.PX_PER_M
                          + graph.ORIGIN[1]).astype(int), 0, h_px - 1)
    region = R[cy[:, None], cx[None, :]]
    region_of = {t: i for i, (t, k) in names.items() if t}
    problems, _, checked = walkable.check_doors(grid, region, cuts, R, names, region_of, left, top)
    assert problems == [] and checked >= 30
    problems, at_doors, _ = walkable.check_openings(grid, region, cut[cy[:, None], cx[None, :]], names, left, top)
    assert problems == [] and at_doors >= 30


@pytest.mark.parametrize("reader,path", [
    (datacheck.read_places, datacheck.PLACES),
    (datacheck.read_walls, datacheck.WALLS),
    (datacheck.read_walkable, datacheck.WALKABLE),
    (datacheck.read_campus, datacheck.CAMPUS_BYTES),
])
def test_broken_files_are_refused(reader, path):
    data = datacheck.read(path)
    reader(data)                                         # the real one reads
    for broken, why in ((data[:-1], "ends early"), (data[:7], "ends early"), (data + b"\0", "left over"),
                        (b"XXXX" + data[4:], "not a"), (data[:4] + b"\x09\x00" + data[6:], "version")):
        with pytest.raises(ValueError, match=why):
            reader(broken)


def test_campus(plan):
    import json
    chunks, buildings = datacheck.read_campus(datacheck.read(datacheck.CAMPUS_BYTES))
    assert sum(len(c[1]) for c in chunks) <= 60000
    with open(datacheck.CAMPUS_JSON, encoding="utf-8") as f:
        info = json.load(f)
    assert info["geo"] == plan["geo"]
    assert len(buildings) == len(info["buildings"])
    # Every building with a floor plan (all in BA1's plan metres) is marked indoor.
    assert sorted(b["name"] for b in info["buildings"] if b["indoor"]) == [plan["building"], "L3Harris Engineering Center"]
