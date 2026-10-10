"""Readers for the app's data files and the checks they must pass, shared by make_data.py and Tools/tests.

Each reader checks the file's header (magic, version) and size the way the app's C# readers do, and raises
ValueError naming the file and the problem. problems() runs every cross-file check and returns what's wrong.
Run alone: python datacheck.py (exit 1 on any problem).
"""
import collections
import json
import math
import os
import struct
import sys

import numpy as np

import graph

PLACES = os.path.join(graph.DATA_DIR, "ba1-floor1-places.bytes")
WALLS = os.path.join(graph.DATA_DIR, "ba1-floor1-walls.bytes")
WALKABLE = os.path.join(graph.DATA_DIR, "ba1-floor1-walkable.bytes")
CAMPUS_BYTES = os.path.join(graph.DATA_DIR, "ucf-campus.bytes")
CAMPUS_JSON = os.path.join(graph.DATA_DIR, "ucf-campus.json")
GRID_TOLERANCE = 5.0      # degrees a door may be off the plan's 45-degree wall grid


class Reader:
    """Little-endian reads that never run past the end."""

    def __init__(self, data, name):
        self.data, self.name, self.at = data, name, 0

    def fail(self, why):
        raise ValueError(f"{self.name}: {why}")

    def take(self, fmt):
        size = struct.calcsize(fmt)
        if self.at + size > len(self.data):
            self.fail(f"ends early, at byte {len(self.data)} (needed {self.at + size})")
        values = struct.unpack_from(fmt, self.data, self.at)
        self.at += size
        return values

    def array(self, dtype, count):
        dtype = np.dtype(dtype)
        if count < 0 or self.at + dtype.itemsize * count > len(self.data):
            self.fail(f"ends early: {count} values of {dtype.itemsize} bytes don't fit")
        a = np.frombuffer(self.data, dtype, count, self.at)
        self.at += dtype.itemsize * count
        return a

    def header(self, magic):
        found, version = self.take("<4sH")
        if found != magic:
            self.fail(f"not a {magic.decode()} file (starts {found!r})")
        if version != graph.FORMAT_VERSION:
            self.fail(f"version {version}, expected {graph.FORMAT_VERSION}")

    def end(self):
        if self.at != len(self.data):
            self.fail(f"{len(self.data) - self.at} bytes left over after the data")


def read(path):
    with open(path, "rb") as f:
        return f.read()


def read_places(data, name="ba1-floor1-places.bytes"):
    """(width, height, place-id hash, cells as a height x width uint8 array)."""
    r = Reader(data, name)
    r.header(graph.PLACES_MAGIC)
    w, h, digest = r.take("<HHI")
    cells = r.array("u1", w * h).reshape(h, w)
    r.end()
    return w, h, digest, cells


def read_walls(data, name="ba1-floor1-walls.bytes"):
    """(ring lengths, points in plan cm as N x 2, triangle indices)."""
    r = Reader(data, name)
    r.header(graph.WALLS_MAGIC)
    r.take("<H")
    count, ring_count, index_count = r.take("<iii")
    rings = r.array("<i4", ring_count)
    points = r.array("<i2", 2 * count).reshape(-1, 2)
    indices = r.array("<i4", index_count)
    r.end()
    if rings.sum() != count:
        r.fail(f"rings hold {rings.sum()} points, the header says {count}")
    if index_count % 3 or (index_count and (indices.min() < 0 or indices.max() >= count)):
        r.fail("triangle indices out of range or not in threes")
    return rings, points, indices


def read_campus(data, name="ucf-campus.bytes"):
    """(chunks [(layer, points dm N x 2, indices)], buildings [(height dm, ring lengths, points dm, indices)])."""
    r = Reader(data, name)
    r.header(graph.CAMPUS_MAGIC)
    r.take("<H")
    chunks = []
    for _ in range(r.take("<i")[0]):
        layer, count, index_count = r.take("<iii")
        points = r.array("<i2", 2 * count).reshape(-1, 2)
        indices = r.array("<u2", index_count)
        if index_count and indices.max() >= count:
            r.fail(f"a ground chunk's index {indices.max()} is past its {count} points")
        chunks.append((layer, points, indices))
    buildings = []
    for _ in range(r.take("<i")[0]):
        height, ring_count, index_count = r.take("<iii")
        rings = r.array("<i4", ring_count)
        points = r.array("<i2", 2 * int(rings.sum())).reshape(-1, 2)
        indices = r.array("<u2", index_count)
        if index_count and indices.max() >= rings.sum():
            r.fail("a building's roof index is past its points")
        buildings.append((height, rings, points, indices))
    r.end()
    return chunks, buildings


def read_walkable(data, name="ba1-floor1-walkable.bytes"):
    """(left, top, cell, grid as height x width bool)."""
    r = Reader(data, name)
    r.header(graph.WALKABLE_MAGIC)
    w, h, left, top, cell = r.take("<HHfff")
    if not w or not h or not cell > 0:
        r.fail(f"bad grid {w} x {h}, cell {cell}")
    grid = r.array("u1", w * h).reshape(h, w) != 0
    r.end()
    return left, top, cell, grid


def walkable_at(walk, x, z):
    left, top, cell, grid = walk
    i, j = math.floor((x - left) / cell), math.floor((top - z) / cell)
    return 0 <= j < grid.shape[0] and 0 <= i < grid.shape[1] and bool(grid[j, i])


def off_grid(dx, dz):
    """Degrees a direction is off the nearest multiple of 45."""
    a = math.degrees(math.atan2(dz, dx)) % 45.0
    return min(a, 45.0 - a)


def plan_problems(plan, grid=True):
    """What FloorPlan.Validate rejects, and the pipeline's own rules (doors on the wall grid, signs on doors).
    grid=False drops the wall-grid rule, for a floor whose walls curve (HEC)."""
    out = []
    ids = [n["id"] for n in plan["nodes"]]
    if len(set(ids)) != len(ids):
        out.append("node ids repeat")
    known = set(ids)
    for e in plan["edges"]:
        if e["a"] not in known or e["b"] not in known:
            out.append(f"edge {e['a']}-{e['b']} names an unknown node")
        elif e["a"] == e["b"]:
            out.append(f"edge {e['a']} joins a node to itself")
    if len(graph.components({n: None for n in ids}, [(e["a"], e["b"]) for e in plan["edges"]])) != 1:
        out.append("the walkway graph is in pieces")
    place_ids = [p["id"] for p in plan["places"]]
    if len(set(place_ids)) != len(place_ids):
        out.append("place ids repeat")
    for p in plan["places"]:
        if not p["doors"]:
            out.append(f"place {p['id']} has no doors")
        for d in p["doors"]:
            if d["node"] not in known:
                out.append(f"door {d['node']} of {p['id']} is no node")
            if abs(math.hypot(d["dx"], d["dz"]) - 1) > 0.01:
                out.append(f"door {d['node']} of {p['id']} doesn't face a unit direction")
            elif grid and off_grid(d["dx"], d["dz"]) > GRID_TOLERANCE:
                out.append(f"door {d['node']} of {p['id']} is {off_grid(d['dx'], d['dz']):.1f} deg off the wall grid")
    for label in plan["labels"]:
        if not label.get("area", 0) > 0:
            out.append(f"label {label['text']} has no area: run basemap.py after graph.py --export")
    doors_of = {p["id"]: {d["node"] for d in p["doors"]} for p in plan["places"]}
    for s in plan.get("signs", []):
        if s["place"] not in doors_of or s["door"] not in doors_of[s["place"]]:
            out.append(f"sign {s['text']} is on {s['door']}, not a door of {s['place']}")
        if abs(math.hypot(s["nx"], s["nz"]) - 1) > 0.01:
            out.append(f"sign {s['text']} at {s['door']} has a normal that isn't unit length")
    image, geo = plan.get("image") or {}, plan.get("geo") or {}
    if not (image.get("width", 0) > 0 and image.get("height", 0) > 0 and image.get("metersPerPixel", 0) > 0):
        out.append("the image block is missing or empty")
    if not (geo.get("lat") or geo.get("lon")) or abs(geo.get("lat", 0)) > 90 or abs(geo.get("lon", 0)) > 180:
        out.append("the geo block is missing or out of range")
    return out


def problems(floor="ba1-floor1", campus="ucf-campus", grid=True):
    """Every check, across every file of a floor (Data/<floor>.json, -places, -walls, -walkable and its campus
    map). Empty when the data is sound."""
    data = lambda name: os.path.join(graph.DATA_DIR, name)
    with open(data(f"{floor}.json"), encoding="utf-8") as f:
        plan = json.load(f)
    out = plan_problems(plan, grid)
    try:
        w, h, digest, cells = read_places(read(data(f"{floor}-places.bytes")))
        if digest != graph.place_id_hash([p["id"] for p in plan["places"]]):
            out.append("places.bytes was made for a different place list: run basemap.py")
        if cells.max() > len(plan["places"]):
            out.append("places.bytes names a place the JSON doesn't have")
    except (OSError, ValueError) as e:
        out.append(str(e))
    try:
        read_walls(read(data(f"{floor}-walls.bytes")))
    except (OSError, ValueError) as e:
        out.append(str(e))
    try:
        walk = read_walkable(read(data(f"{floor}-walkable.bytes")))
        off = [n["id"] for n in plan["nodes"] if not walkable_at(walk, n["x"], n["z"])]
        if off:
            out.append(f"walkway nodes on unwalkable cells: {', '.join(off)}")
    except (OSError, ValueError) as e:
        out.append(str(e))
    try:
        chunks, buildings = read_campus(read(data(f"{campus}.bytes")))
        with open(data(f"{campus}.json"), encoding="utf-8") as f:
            info = json.load(f)
        if len(buildings) != len(info["buildings"]):
            out.append(f"{campus}: {len(buildings)} buildings but {len(info['buildings'])} names")
        if sum(len(c[1]) for c in chunks) > 60000:
            out.append(f"{campus}: over 60000 ground vertices, more than one uGUI mesh holds")
        if info.get("geo") != plan["geo"]:
            out.append(f"{campus} was built with a different geo fit than {floor}.json: run make_data.py --campus")
        if plan["building"] not in [b["name"] for b in info["buildings"] if b.get("indoor")]:
            out.append(f"{campus} doesn't mark {plan['building']} as a building with an indoor map: run make_data.py --campus")
    except (OSError, ValueError, KeyError) as e:
        out.append(f"{campus}: {e}")
    return out


def summary(floor="ba1-floor1"):
    with open(os.path.join(graph.DATA_DIR, f"{floor}.json"), encoding="utf-8") as f:
        plan = json.load(f)
    kinds = collections.Counter(p["kind"] for p in plan["places"])
    doors = sum(len(p["doors"]) for p in plan["places"])
    return (f"{floor}.json: {len(plan['nodes'])} nodes, {len(plan['edges'])} edges, {len(plan['places'])} places "
            f"({', '.join(f'{v} {k}' for k, v in kinds.most_common())}), {doors} doors, {len(plan['labels'])} labels, "
            f"{len(plan.get('signs', []))} signs")


if __name__ == "__main__":
    print(summary())
    found = problems()
    if found:
        sys.exit("Data problems:\n  " + "\n  ".join(found))
    print("data OK")
