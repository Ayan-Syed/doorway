"""Builds all of HEC floor 1's app data from Knight Hacks' traced rooms (hec.py) and onsite.json, then checks it.

  python make_data.py            -> Assets/Navigation/Data/hec-floor1.json, -plan.png, -map-<theme>.png,
                                    -places.bytes, -walls.bytes, -walkable.bytes; check images in check/
  python make_data.py --campus   the same, then Tools/campus/campus.py --floor hec-floor1 (ucf-campus-hec-floor1.*)

Same formats as BA1's (Tools/ba1-floor1 documents each file), drawn the same way: basemap.py's themes and line
widths, walls.py's outlines, walkable.py's door cuts and 10 cm cells. What differs is where the rooms, doors and
hallways come from: polygons, not a scan, so the doors are inferred (one per room, in the middle of its longest
wall on a hallway) and the room numbers are onsite.json's or the temporary U ids.
Stops at the first problem; then runs datacheck.py's checks on these files (doors aren't held to BA1's 45-degree
wall grid: HEC's walls curve).
"""
import json
import math
import os
import struct
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage

import hec
import walkway
import basemap            # Tools/ba1-floor1, put on the path by hec
import datacheck
import graph as ba1
import walkable as ba1_walkable
import walls

CAMPUS = "ucf-campus-hec-floor1"
CAMPUS_SCRIPT = os.path.join(hec.BOX, "Tools", "campus", "campus.py")
DOOR_WIDTH = 0.9      # metres cut through the wall at each door (walkable.py's)
THROUGH = 0.8         # metres past the wall the cut goes
LINK_WIDTH = 0.4      # metres kept open along every walkway link
SLACK = 1.0           # metres a door's walk to its room may exceed the straight line
SIGN_ASIDE = 0.6      # metres from the middle of a door to the number plate beside it
PLACE_KIND = {"room": "room", "teaching": "room", "restroom": "restroom", "stairs": "stairs", "elevator": "elevator"}
PLACE_NAME = {"restroom": "Restroom", "stairs": "Stairs", "elevator": "Elevator"}


def data(name):
    return os.path.join(hec.DATA_DIR, name)


def build():
    """Everything the files are made from: a dict of the regions, the graph and the places."""
    site, src = hec.onsite(), hec.source()
    frame, R, ids, texts, kinds, rs, dark = hec.regions(src, site)
    g, _ = walkway.hallway_graph(frame, R)
    problems, notes = [], []

    # Places: (id, name, detail, kind, region, uid, [(node, spot, wall point, toward the door)]), all in raster px.
    places = []
    for i, r in enumerate(rs):
        if r["source"] == "SERVICE":
            continue                     # closets, plant rooms: drawn, not navigated to
        door = walkway.room_door(R, 3 + i)
        if door is None or door[2] is None:
            notes.append(f"{r['text']} ({r['source'].lower()}) has no hallway beside it: not a place")
            continue
        wall, out, spot = door
        kind = PLACE_KIND[r["kind"]]
        if kind == "room":
            pid = name = r["text"]
            detail = r["detail"]         # plain: the directory groups by it ("Classroom" -> "Classrooms")
        else:
            pid, name, detail = f"{kind}-{r['uid'].lower()}", PLACE_NAME[kind], ""
        places.append([pid, name, detail, kind, 3 + i, r, [(f"d-{pid}", spot, wall, -out)]])
    rooms_only = [p for p in places if p[3] == "room"]
    for p in places:
        if p[3] != "room":               # "By 117": the nearest room's name
            spot = p[6][0][1]
            near = min(rooms_only, key=lambda q: np.hypot(*(q[6][0][1] - spot)))
            p[2] = f"By {near[1]}"
    for e in site["entrances"]:
        found = walkway.entrance(frame, R, src["outline"], e["at"])
        if found is None:
            problems.append(f"entrance {e['id']}: no hallway behind the wall near {e['at']}")
            continue
        o, out, spot = found
        places.append([e["id"], e["name"], e["detail"], "exit", None, None, [(f"x-{e['id']}", spot, o, out)]])

    for p in places:
        for node, spot, _, _ in p[6]:
            g.add(node, spot)
            if walkway.join(g, R, node, spot) is None:
                problems.append(f"door {node} of {p[0]} can't see the walkway graph along a clear line")
    if problems:
        sys.exit("hec-floor1 problems:\n  " + "\n  ".join(problems))
    for n in notes:
        print("  " + n)
    return {"frame": frame, "R": R, "ids": ids, "texts": texts, "kinds": kinds, "rooms": rs, "dark": dark,
            "graph": g, "places": places}


def plan_json(b):
    """The plan (FloorPlan.cs): nodes, edges, places, labels, signs, image and geo."""
    frame, R, g = b["frame"], b["R"], b["graph"]
    m = lambda xy: frame.to_m(*xy)
    unit = lambda v: (round(float(v[0]) / float(np.hypot(*v)), 4), round(-float(v[1]) / float(np.hypot(*v)), 4))
    places, signs, labels = [], [], []
    for pid, name, detail, kind, region, room, doors in b["places"]:
        places.append({"id": pid, "name": name, "detail": detail, "kind": kind,
                       "doors": [{"node": node, "label": "", "dx": unit(d)[0], "dz": unit(d)[1]}
                                 for node, _, _, d in doors]})
        if kind == "room":
            # The number where there's most room in the room, and its area (basemap.places_and_labels).
            ys, xs = np.nonzero(R == region)
            sl = (slice(ys.min(), ys.max() + 1), slice(xs.min(), xs.max() + 1))
            inside = R[sl] == region
            depth = ndimage.distance_transform_edt(np.pad(inside, 1))[1:-1, 1:-1]
            y, x = np.unravel_index(np.argmax(depth), depth.shape)
            lx, lz = m((sl[1].start + x, sl[0].start + y))
            labels.append({"text": name, "x": lx, "z": lz, "area": round(inside.sum() / hec.PX_PER_M ** 2, 1)})
            # A number plate beside each door of a room whose number is known for sure.
            if room["known"] and not room["inferred"]:
                for node, _, wall, d in doors:
                    out = -d / np.hypot(*d)
                    aside = np.array([out[1], -out[0]])
                    sx, sz = m(wall + out * 0.05 * hec.PX_PER_M + aside * SIGN_ASIDE * hec.PX_PER_M)
                    nx, nz = unit(out)
                    signs.append({"text": name, "x": sx, "z": sz, "nx": nx, "nz": nz, "door": node, "place": pid})
    labels.sort(key=lambda l: -l["area"])
    left, top = frame.to_m(0, 0)
    return {
        "building": hec.BUILDING,
        "shortName": hec.SHORT_NAME,
        "floor": "Floor 1",
        "nodes": [{"id": n, "x": m(xy)[0], "z": m(xy)[1]} for n, xy in g.nodes.items()],
        "edges": [{"a": a, "b": b_} for a, b_ in g.edges],
        "places": places,
        "labels": labels,
        "signs": signs,
        "image": {"left": left, "top": top, "metersPerPixel": round(frame.width / hec.PX_PER_M / hec.MINIMAP_WIDTH, 6),
                  "width": hec.MINIMAP_WIDTH, "height": round(frame.height * hec.MINIMAP_WIDTH / frame.width)},
        "geo": frame.geo(),
    }


def outside_flags(b):
    flags = np.zeros(max(b["ids"]) + 1, bool)
    flags[hec.OUTSIDE] = True
    return flags


def pictures(b, Rs, plan):
    """The minimap (walls as white lines over transparency) and the filled map in every theme."""
    inner, outer = walls.boundaries(Rs, outside_flags(b))
    alpha = np.maximum(basemap.line(inner, basemap.WALL_WIDTH)[..., 0], basemap.line(outer, basemap.OUTLINE_WIDTH)[..., 0])
    size = (plan["image"]["width"], plan["image"]["height"])
    a = Image.fromarray((alpha * 255).astype(np.uint8), "L").resize(size, Image.LANCZOS)
    minimap = Image.new("RGBA", size, (255, 255, 255, 0))
    minimap.putalpha(a)
    minimap.save(data(f"{hec.FLOOR}-plan.png"))
    kind = np.zeros(max(b["ids"]) + 1, np.int32)
    for i, k in zip(b["ids"], b["kinds"]):
        kind[i] = basemap.KINDS.index(k)
    for theme in basemap.THEMES:
        basemap.render(theme, Rs, kind, b["dark"]).save(data(f"{hec.FLOOR}-map-{theme}.png"), optimize=True)
        print("  map", theme)


def place_grid(b, Rs, plan):
    """places.bytes: every 5 raster px, the index + 1 of the place there (its own region), or 0."""
    owner = np.zeros(max(b["ids"]) + 1, np.uint8)
    for n, p in enumerate(b["places"]):
        if p[4] is not None:
            owner[p[4]] = n + 1
    if len(plan["places"]) > 255:
        sys.exit("places.bytes stores place index + 1 as a byte: at most 255 places per floor")
    small = owner[Rs[2::5, 2::5]]
    h, w = small.shape
    with open(data(f"{hec.FLOOR}-places.bytes"), "wb") as f:
        f.write(struct.pack("<4sHHHI", ba1.PLACES_MAGIC, ba1.FORMAT_VERSION, w, h,
                            ba1.place_id_hash([p["id"] for p in plan["places"]])))
        f.write(np.ascontiguousarray(small).tobytes())


def walkable_grid(b, Rs, plan):
    """walkable.bytes (walkable.py's format): inside the building and off the walls basemap.py draws, the doors cut
    through and the graph's links kept open, on 10 cm cells a cell walkable only if all of it is. Then checks it."""
    frame, g = b["frame"], b["graph"]
    inner, outer = walls.boundaries(Rs, outside_flags(b))
    solid = ((ndimage.distance_transform_edt(~inner) <= basemap.WALL_WIDTH / 2)
             | (ndimage.distance_transform_edt(~outer) <= basemap.OUTLINE_WIDTH / 2))
    cut_im = Image.new("L", (frame.width, frame.height), 0)
    d = ImageDraw.Draw(cut_im)
    half = DOOR_WIDTH / 2 * hec.PX_PER_M
    for p in b["places"]:
        for _, spot, wall, toward in p[6]:
            t = toward / np.hypot(*toward)
            reach = np.hypot(*(wall - spot)) + THROUGH * hec.PX_PER_M
            side = np.array([-t[1], t[0]]) * half
            d.polygon([tuple(spot + side), tuple(spot - side), tuple(spot + t * reach - side), tuple(spot + t * reach + side)],
                      fill=255)
    cut = np.asarray(cut_im) > 0
    link_im = Image.new("L", cut_im.size, 0)
    d = ImageDraw.Draw(link_im)
    width = max(int(round(LINK_WIDTH * hec.PX_PER_M)), 1)
    for a, b_ in g.edges:
        d.line([g.nodes[a], g.nodes[b_]], fill=255, width=width)
    for x, y in g.nodes.values():
        d.ellipse([x - width / 2, y - width / 2, x + width / 2, y + width / 2], fill=255)
    free = ((Rs != hec.OUTSIDE) & ~solid) | cut | (np.asarray(link_im) > 0)

    left, top = plan["image"]["left"], plan["image"]["top"]
    width_c = math.ceil(frame.width / hec.PX_PER_M / hec.CELL)
    height_c = math.ceil(frame.height / hec.PX_PER_M / hec.CELL)
    offs = (np.arange(3) + 0.5) / 3
    fx = (left + (np.arange(width_c)[:, None] + offs[None, :]) * hec.CELL) * hec.PX_PER_M + frame.origin[0]
    fy = -(top - (np.arange(height_c)[:, None] + offs[None, :]) * hec.CELL) * hec.PX_PER_M + frame.origin[1]
    px = np.clip(np.round(fx).astype(int), 0, frame.width - 1)
    py = np.clip(np.round(fy).astype(int), 0, frame.height - 1)
    grid = free[py[:, :, None, None], px[None, None, :, :]].all(axis=(1, 3))
    region = Rs[py[:, 1][:, None], px[:, 1][None, :]]

    cell_of = lambda x, y: (int((top - (-(y - frame.origin[1]) / hec.PX_PER_M)) // hec.CELL),
                            int(((x - frame.origin[0]) / hec.PX_PER_M - left) // hec.CELL))
    problems = []
    off = [n for n, (x, y) in g.nodes.items() if not grid[cell_of(x, y)]]
    if off:
        problems.append(f"graph points on cells nobody can stand on: {', '.join(off)}")
    checked = 0
    for p in b["places"]:
        if p[4] is None or p[3] in ("stairs", "elevator"):
            continue
        target = (region == p[4]) & grid
        for node, spot, _, _ in p[6]:
            start = ba1_walkable.nearest_open(grid, cell_of(*spot), 5)
            if start is None or not target.any():
                problems.append(f"door {node} of {p[0]}: no walkable cell at its spot or in its room")
                continue
            ty, tx = np.nonzero(target)
            straight = float(np.min(np.hypot(ty - start[0], tx - start[1]))) * hec.CELL
            walked = ba1_walkable.walk(grid, start, target, straight + SLACK + 0.5)
            checked += 1
            if walked is None or walked > straight + SLACK:
                problems.append(f"door {node} of {p[0]}: room not reached within {straight:.1f} + {SLACK:.0f} m")
    lab, _ = ndimage.label(grid, structure=np.ones((3, 3)))
    pieces = {int(lab[cell_of(x, y)]) for x, y in g.nodes.values()}
    if len(pieces) != 1:
        problems.append(f"the graph's points stand on {len(pieces)} separate walkable pieces")

    os.makedirs(hec.CHECK_DIR, exist_ok=True)
    Image.fromarray((grid * 255).astype(np.uint8)).save(os.path.join(hec.CHECK_DIR, "walkable.png"))
    path = os.path.join(hec.CHECK_DIR, "walkable-failed.bytes") if problems else data(f"{hec.FLOOR}-walkable.bytes")
    with open(path, "wb") as f:
        f.write(struct.pack("<4sHHHfff", ba1.WALKABLE_MAGIC, ba1.FORMAT_VERSION, width_c, height_c, left, top, hec.CELL))
        f.write(grid.astype(np.uint8).tobytes())
    print(f"  walkable: {width_c} x {height_c} cells, {grid.sum() * hec.CELL ** 2:.0f} m2; {checked} doors walked into")
    if problems:
        sys.exit("walkable problems:\n  " + "\n  ".join(problems))


def check_images(b):
    """check/rooms.png: every traced room's number or U id, to read the real numbers off the doors on site;
    check/graph.png: the walkway graph, doors (green), entrances (red) and number plates (purple) on the map."""
    frame, R, g = b["frame"], b["R"], b["graph"]
    with Image.open(data(f"{hec.FLOOR}-map-paper.png")) as im:
        base = Image.new("RGBA", im.size, (255, 255, 255, 255))
        base.alpha_composite(im.convert("RGBA"))
    font = ImageFont.truetype(ba1.FONT, 34)
    rooms = base.copy()
    d = ImageDraw.Draw(rooms)
    for i, r in enumerate(b["rooms"]):
        ys, xs = np.nonzero(R == 3 + i)
        if len(xs):
            text = r["text"] if r["known"] else f"{r['uid']}?"
            d.text((xs.mean(), ys.mean()), text, fill=(0, 0, 0), font=font, anchor="mm")
    rooms.convert("RGB").save(os.path.join(hec.CHECK_DIR, "rooms.png"))
    d = ImageDraw.Draw(base)
    for a, b_ in g.edges:
        d.line([g.nodes[a], g.nodes[b_]], fill=(0, 120, 255), width=6)
    for n, (x, y) in g.nodes.items():
        d.ellipse([x - 7, y - 7, x + 7, y + 7], fill=(0, 70, 200))
    for p in b["places"]:
        color = (220, 0, 0) if p[3] == "exit" else (0, 160, 60)
        for node, spot, wall, _ in p[6]:
            d.line([tuple(spot), tuple(wall)], fill=color, width=6)
            d.ellipse([spot[0] - 11, spot[1] - 11, spot[0] + 11, spot[1] + 11], fill=color)
            d.text((spot[0] + 14, spot[1] - 30), p[1], fill=color, font=font)
    base.convert("RGB").save(os.path.join(hec.CHECK_DIR, "graph.png"))


def main():
    b = build()
    plan = plan_json(b)
    with open(hec.PLAN_JSON, "w", encoding="utf-8", newline="\n") as f:
        json.dump(plan, f, ensure_ascii=False, indent=1)
    print(f"  {hec.FLOOR}.json: {len(plan['nodes'])} nodes, {len(plan['edges'])} edges, {len(plan['places'])} places, "
          f"{len(plan['labels'])} labels, {len(plan['signs'])} signs; geo {plan['geo']}")
    Rs = basemap.smooth(b["R"])
    pictures(b, Rs, plan)
    place_grid(b, Rs, plan)
    walls.write(Rs, outside_flags(b), data(f"{hec.FLOOR}-walls.bytes"), b["frame"].origin, hec.PX_PER_M)
    walkable_grid(b, Rs, plan)
    check_images(b)
    if "--campus" in sys.argv:
        result = subprocess.run([sys.executable, CAMPUS_SCRIPT, "--floor", hec.FLOOR], cwd=os.path.dirname(CAMPUS_SCRIPT))
        if result.returncode != 0:
            sys.exit(f"campus.py failed (exit {result.returncode})")
    print(datacheck.summary(hec.FLOOR))
    problems = datacheck.problems(hec.FLOOR, CAMPUS, grid=False)
    if problems:
        sys.exit("Data problems:\n  " + "\n  ".join(problems))
    print("done")


if __name__ == "__main__":
    main()
