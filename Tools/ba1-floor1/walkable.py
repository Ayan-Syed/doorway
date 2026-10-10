"""Where a person can stand on BA1 floor 1, as a 10 cm grid, for the app's position filter and for rerouting
(Walkable.cs): hallways, room interiors and the doorways between them.

From segment.py's wall mask, not its regions (which hand every wall pixel to a neighbour):
  1. the treads drawn inside stairs and the crosses inside lift shafts stop being walls;
  2. close the walls by CLOSE px: fills the hollow middles of the double-line walls and shuts the drawing's
     slits narrower than ~0.7 m, which are gaps between line ends, not doors;
  3. cut every door in ba1-floor1.json (and every exit) back in, 0.9 m wide, from its door spot through the
     wall, and keep the walkway graph's own links open (0.4 m wide), so every route is on walkable ground;
  4. sample it onto 10 cm cells: a cell is walkable only if all of it is.
Then checks it, and fails on: a door that doesn't reach its room (or the vestibule it opens into) within its
straight-line distance + 1 m, or an opening narrower than DOOR_SIZED between a room and anywhere else that isn't
a door in the plan. Door-sized openings the plan has no door for are kept (doors the graph doesn't route
through) and counted. Stairs and lifts aren't walked into: they lead off the floor.

Run after graph.py --export (it needs the doors): python walkable.py -> Assets/Navigation/Data/ba1-floor1-walkable.bytes
The file (little-endian, Walkable.cs): "DWWK", uint16 version (1), uint16 width, uint16 height, float32 left,
float32 top, float32 cell (metres), then width x height bytes, top row first: 1 walkable, 0 not.
"""
import heapq
import json
import math
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

import doors
import graph

CELL = 0.1            # metres
CLOSE = 10            # px: walls closed over gaps up to about twice this (~0.7 m)
DOOR_WIDTH = 0.9      # metres cut through the wall at each door
THROUGH = 0.8         # metres past the door's wall line the cut goes, into the room
LINK_WIDTH = 0.4      # metres kept open along every walkway link
SLACK = 1.0           # metres a door's walk to its room may exceed the straight line
# Metres of walkable cells: an opening at least this wide may be a door the plan doesn't route through. A cell is
# walkable only if all of it is, which trims about a cell off each side, so a 0.65 m gap in the drawing (CLOSE
# shuts anything narrower) is about 0.45 m of cells.
DOOR_SIZED = 0.4
NEAR_DOOR = 1.0       # metres: an opening this close to a cut door is that door
OUT_PATH = os.path.join(graph.DATA_DIR, "ba1-floor1-walkable.bytes")
OUTSIDE, WALKWAY = 1, 2


def disk(r):
    y, x = np.mgrid[-r:r + 1, -r:r + 1]
    return x * x + y * y <= r * r


def door_cuts(plan, nodes_px, R, ink):
    """[(node, place, spot px, direction px, polygon px)] for every door node once, exits included."""
    cuts, seen = [], set()
    half = DOOR_WIDTH / 2 * graph.PX_PER_M
    for p in plan["places"]:
        for d in p["doors"]:
            if d["node"] in seen:
                continue
            seen.add(d["node"])
            x, y = nodes_px[d["node"]]
            dx, dy = d["dx"], -d["dz"]
            where = doors.hit(R, ink, x, y, dx, dy)
            reach = math.hypot(where[0] - x, where[1] - y) if where else graph.PX_PER_M
            reach += THROUGH * graph.PX_PER_M
            px, py = -dy, dx
            poly = [(x + px * half, y + py * half), (x - px * half, y - py * half),
                    (x + dx * reach - px * half, y + dy * reach - py * half),
                    (x + dx * reach + px * half, y + dy * reach + py * half)]
            cuts.append((d["node"], p, (x, y), (dx, dy), poly))
    return cuts


def to_cells(mask, left, top, width, height):
    """A cell is walkable only if all 3 x 3 sample points in it are (and they're on the plan)."""
    offs = (np.arange(3) + 0.5) / 3
    fx = (left + (np.arange(width)[:, None] + offs[None, :]) * CELL) * graph.PX_PER_M + graph.ORIGIN[0]
    fy = -(top - (np.arange(height)[:, None] + offs[None, :]) * CELL) * graph.PX_PER_M + graph.ORIGIN[1]
    h, w = mask.shape
    px = np.clip(np.round(fx).astype(int), 0, w - 1)
    py = np.clip(np.round(fy).astype(int), 0, h - 1)
    on_x = ((fx >= 0) & (fx < w)).all(1)
    on_y = ((fy >= 0) & (fy < h)).all(1)
    return mask[py[:, :, None, None], px[None, None, :, :]].all(axis=(1, 3)) & on_y[:, None] & on_x[None, :]


def cell_of(x_px, y_px, left, top):
    """(row, column) of the cell holding a plan pixel."""
    xm = (x_px - graph.ORIGIN[0]) / graph.PX_PER_M
    zm = -(y_px - graph.ORIGIN[1]) / graph.PX_PER_M
    return int((top - zm) // CELL), int((xm - left) // CELL)


def nearest_open(grid, cell, radius):
    """The walkable cell nearest to cell within radius cells, or None."""
    y, x = cell
    best = None
    for yy in range(max(y - radius, 0), min(y + radius + 1, grid.shape[0])):
        for xx in range(max(x - radius, 0), min(x + radius + 1, grid.shape[1])):
            d = (yy - y) ** 2 + (xx - x) ** 2
            if grid[yy, xx] and d <= radius * radius and (best is None or d < best[0]):
                best = (d, (yy, xx))
    return best[1] if best else None


def walk(grid, start, target, limit):
    """Walking distance (metres, 8-connected) from start to the nearest target cell, or None past limit."""
    h, w = grid.shape
    best = {start: 0.0}
    todo = [(0.0, start)]
    steps = [(dy, dx, math.hypot(dy, dx) * CELL) for dy in (-1, 0, 1) for dx in (-1, 0, 1) if dy or dx]
    while todo:
        d, (y, x) = heapq.heappop(todo)
        if d > best.get((y, x), math.inf) or d > limit:
            continue
        if target[y, x]:
            return d
        for dy, dx, c in steps:
            yy, xx = y + dy, x + dx
            if 0 <= yy < h and 0 <= xx < w and grid[yy, xx] and d + c < best.get((yy, xx), math.inf):
                best[(yy, xx)] = d + c
                heapq.heappush(todo, (d + c, (yy, xx)))
    return None


def first_room(R, x, y, dx, dy):
    """The first region other than the walkway a door's line enters within 4 m, or None."""
    h, w = R.shape
    for s in range(1, int(4 * graph.PX_PER_M)):
        xi, yi = int(round(x + dx * s)), int(round(y + dy * s))
        if not (0 <= xi < w and 0 <= yi < h):
            return None
        if R[yi, xi] not in (WALKWAY, OUTSIDE):
            return int(R[yi, xi])
    return None


def build(R, wall, names, plan, nodes_px, ink):
    """(free px mask, cut px mask, cuts) after steps 1-3."""
    # 1. Stairs' treads and lifts' crosses: ink well inside those rooms (6 px or more from their edge).
    inner = np.zeros(R.shape, bool)
    for i, (_, kind) in names.items():
        if kind in ("stairs", "elevator"):
            inner |= ndimage.binary_erosion(R == i, iterations=6)
    # 2. Walls closed.
    blocked = ndimage.binary_closing(wall & ~inner, structure=disk(CLOSE))
    # 3. Doors cut back in, and the graph's links kept open.
    cuts = door_cuts(plan, nodes_px, R, ink)
    image = Image.new("L", (R.shape[1], R.shape[0]), 0)
    draw = ImageDraw.Draw(image)
    for *_, poly in cuts:
        draw.polygon(poly, fill=255)
    cut = np.asarray(image) > 0
    links = Image.new("L", image.size, 0)
    draw = ImageDraw.Draw(links)
    width = max(int(round(LINK_WIDTH * graph.PX_PER_M)), 1)
    for e in plan["edges"]:
        draw.line([nodes_px[e["a"]], nodes_px[e["b"]]], fill=255, width=width)
    for x, y in nodes_px.values():
        draw.ellipse([x - width / 2, y - width / 2, x + width / 2, y + width / 2], fill=255)
    free = (~(R == OUTSIDE) & ~blocked) | cut | (np.asarray(links) > 0)
    return free, cut, cuts


def check_doors(grid, region, cuts, R, names, region_of, left, top):
    """Problems: doors whose room isn't reached within the straight line + SLACK. And notes."""
    problems, notes, checked = [], [], 0
    for node, p, (x, y), (dx, dy), _ in cuts:
        kind = p["kind"]
        if kind in ("exit", "stairs", "elevator"):
            continue
        # The room by its number (a restroom's, behind its door), or whatever room the door opens into first:
        # a vestibule, a restroom's lobby.
        text = p["id"] if kind == "room" else doors.number_behind(R, names, x, y, dx, dy, kind)
        targets = {region_of.get(text), first_room(R, x, y, dx, dy)}
        targets.discard(None)
        if not targets:
            notes.append(f"door {node} of {p['id']}: no room of its own on the plan to walk into, not checked")
            continue
        target = np.isin(region, list(targets)) & grid
        if not target.any():
            problems.append(f"the room behind {node} ({p['id']}) has no walkable cell")
            continue
        start = nearest_open(grid, cell_of(x, y, left, top), 5)
        if start is None:
            problems.append(f"door spot {node} of {p['id']} has no walkable cell within 0.5 m")
            continue
        ty, tx = np.nonzero(target)
        straight = float(np.min(np.hypot(ty - start[0], tx - start[1]))) * CELL
        walked = walk(grid, start, target, straight + SLACK + 0.5)
        checked += 1
        if walked is None or walked > straight + SLACK:
            problems.append(f"door {node} of {p['id']}: room not reached within {straight:.1f} + {SLACK:.0f} m"
                            + ("" if walked is None else f" (walked {walked:.1f} m)"))
    return problems, notes, checked


def check_openings(grid, region, cut_cells, names, left, top):
    """(problems, at doors, door-sized): every place a room's walkable cells meet another region's."""
    room_ids = [i for i, (_, k) in names.items() if k not in ("walkway", "outside")]
    is_room = np.isin(region, room_ids)
    boundary = np.zeros(grid.shape, bool)
    for dy, dx in ((0, 1), (1, 0)):
        h, w = grid.shape[0] - dy, grid.shape[1] - dx
        a, b = region[:h, :w], region[dy:, dx:]
        edge = (grid[:h, :w] & grid[dy:, dx:] & (a != b) & (is_room[:h, :w] | is_room[dy:, dx:])
                & (a != OUTSIDE) & (b != OUTSIDE))
        boundary[:h, :w] |= edge
        boundary[dy:, dx:] |= edge
    near_door = ndimage.distance_transform_edt(~cut_cells) * CELL <= NEAR_DOOR
    groups, _ = ndimage.label(boundary, structure=np.ones((3, 3)))
    problems, at_doors, sized = [], 0, 0
    for g, sl in enumerate(ndimage.find_objects(groups), 1):
        m = groups[sl] == g
        if (near_door[sl] & m).any():
            at_doors += 1
            continue
        ys, xs = np.nonzero(m)
        span = (max(np.ptp(ys), np.ptp(xs)) + 1) * CELL           # along the opening (across it is 2 cells)
        if span >= DOOR_SIZED:
            sized += 1
            continue
        where = (round(left + (xs.mean() + sl[1].start + 0.5) * CELL, 1),
                 round(top - (ys.mean() + sl[0].start + 0.5) * CELL, 1))
        pair = sorted({names.get(int(v), ("?", ""))[0] or f"#{int(v)}" for v in region[sl][m]})
        problems.append(f"a {span:.1f} m opening between {' and '.join(pair)} at {where} that isn't a door")
    return problems, at_doors, sized


def main():
    with np.load(graph.need(graph.REGIONS, "run segment.py first (or make_data.py)")) as d:
        R, wall, dark = d["regions"], d["wall"], d["dark"]
        names = {int(i): (str(t), str(k)) for i, t, k in zip(d["ids"], d["texts"], d["kinds"])}
    with open(graph.need(graph.PLAN_JSON, "run graph.py --export first (or make_data.py)"), encoding="utf-8") as f:
        plan = json.load(f)
    ink = doors.Ink(dark)
    nodes_px = {n["id"]: (n["x"] * graph.PX_PER_M + graph.ORIGIN[0], -n["z"] * graph.PX_PER_M + graph.ORIGIN[1])
                for n in plan["nodes"]}
    region_of = {t: i for i, (t, k) in names.items() if t}

    free, cut, cuts = build(R, wall, names, plan, nodes_px, ink)

    # 4. Onto 10 cm cells over the plan image's extent.
    left, top = plan["image"]["left"], plan["image"]["top"]
    h_px, w_px = R.shape
    width = math.ceil(w_px / graph.PX_PER_M / CELL)
    height = math.ceil(h_px / graph.PX_PER_M / CELL)
    grid = to_cells(free, left, top, width, height)
    cx = np.clip(np.round((left + (np.arange(width) + 0.5) * CELL) * graph.PX_PER_M + graph.ORIGIN[0]).astype(int),
                 0, w_px - 1)
    cy = np.clip(np.round(-(top - (np.arange(height) + 0.5) * CELL) * graph.PX_PER_M + graph.ORIGIN[1]).astype(int),
                 0, h_px - 1)
    region = R[cy[:, None], cx[None, :]]
    cut_cells = cut[cy[:, None], cx[None, :]]

    problems, notes, checked = check_doors(grid, region, cuts, R, names, region_of, left, top)
    more, at_doors, sized = check_openings(grid, region, cut_cells, names, left, top)
    problems += more

    lab, _ = ndimage.label(grid, structure=np.ones((3, 3)))
    sizes = np.bincount(lab.ravel())
    main_id = int(np.argmax(sizes[1:]) + 1)
    area = grid.sum() * CELL * CELL
    rooms = [i for i, (_, k) in names.items() if k not in ("walkway", "outside")]
    cut_off = sorted({names[i][0] or f"#{i}" for i in rooms
                      if ((region == i) & grid).any() and not (lab[(region == i) & grid] == main_id).any()})
    off_graph = [n for n, (x, y) in nodes_px.items() if not grid[cell_of(x, y, left, top)]]

    # A failing mask goes to check/ to look at, never over the app's.
    os.makedirs(graph.CHECK_DIR, exist_ok=True)
    path = os.path.join(graph.CHECK_DIR, "walkable-failed.bytes") if problems else OUT_PATH
    with open(path, "wb") as f:
        f.write(struct.pack("<4sHHHfff", graph.WALKABLE_MAGIC, graph.FORMAT_VERSION, width, height, left, top, CELL))
        f.write(grid.astype(np.uint8).tobytes())
    Image.fromarray((grid * 255).astype(np.uint8)).save(os.path.join(graph.CHECK_DIR, "walkable_check.png"))
    print(f"walkable: {width} x {height} cells of {CELL * 100:.0f} cm, {area:.0f} m2 walkable, "
          f"{sizes[main_id] * CELL * CELL / area:.1%} of it in one piece; {len(cuts)} doors cut, "
          f"{checked} checked; openings from rooms: {at_doors} at doors, {sized} door-sized with no door in the plan")
    print(f"  rooms sealed in the drawing (no opening): {', '.join(cut_off) or 'none'}")
    if off_graph:
        problems.append(f"graph nodes on unwalkable cells: {', '.join(off_graph)}")
    for note in notes:
        print("  " + note)
    if problems:
        sys.exit("walkable.py found problems:\n  " + "\n  ".join(problems))


if __name__ == "__main__":
    main()
