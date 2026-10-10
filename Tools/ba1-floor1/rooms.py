"""Makes every numbered room on BA1 floor 1 a place you can go to, beyond the hand-placed ones in graph.P.

Doorways are where a room's region meets the walkway with open floor on both sides (segment.py floods
rooms and hallways from their middles, so the floods meet in the door openings). Each doorway gets a door
spot in the hallway just outside it, straight out from the wall (doors.wall_normal measures the wall's
direction; the pixel steps along the opening only give a rough one), joined to the nearest hallway line it
can see. A room with no doorway onto the hallway (an office in a suite) uses the door of the room it opens
off: 'Inside 145'.
Run after segment.py and before graph.py --export: python rooms.py -> rooms.json, check/rooms_check.png
"""
import json
import math
import os
from collections import deque

import numpy as np
from PIL import Image, ImageDraw

import graph
from doors import Ink, wall_normal

OUTSIDE, WALKWAY = 1, 2
MIN_OPENING = 14                   # plan px of open boundary for a doorway (~0.5 m)
OUT_STEP = 20                      # how far into the hallway the door spot goes (~0.7 m)
MAX_LINK = 16 * graph.PX_PER_M     # longest door-to-hallway link (16 m)
# Lift shafts the segmentation merged into the hallway: their doors aren't rooms' doors. (x0, y0, x1, y1)
# The lobby's two lifts (143, 144), and the atrium lift's car in front of 107A, whose side touches 109.
EXCLUDE = [(955, 980, 1030, 1135), (1950, 1420, 2030, 1490)]
# Rooms whose way in the search gets wrong (130A opens off the lounge, not across the wall into 121).
PARENT = {"130A": "130"}


def openings(R, free):
    """Doorway points between regions: {(i, j): [(x, y, dx, dy)]}, a point on i's side, normal into j."""
    found = {}
    for dx, dy in ((1, 0), (0, 1)):
        a, b = R[:R.shape[0] - dy, :R.shape[1] - dx], R[dy:, dx:]
        ok = (a != b) & free[:R.shape[0] - dy, :R.shape[1] - dx] & free[dy:, dx:]
        ys, xs = np.nonzero(ok)
        for y, x in zip(ys.tolist(), xs.tolist()):
            i, j = int(a[y, x]), int(b[y, x])
            found.setdefault((i, j), []).append((x, y, dx, dy))
            found.setdefault((j, i), []).append((x + dx, y + dy, -dx, -dy))
    return found


def excluded(points):
    x = sum(p[0] for p in points) / len(points)
    y = sum(p[1] for p in points) / len(points)
    return any(x0 <= x <= x1 and y0 <= y <= y1 for x0, y0, x1, y1 in EXCLUDE)


def clusters(points):
    """Groups points by flood fill: a point joins a group within 10 px of a member in both x and y."""
    left = list(range(len(points)))
    groups = []
    while left:
        group, todo = [], [left.pop(0)]
        while todo:
            k = todo.pop()
            group.append(points[k])
            x, y = points[k][0], points[k][1]
            near = [m for m in left if abs(points[m][0] - x) <= 10 and abs(points[m][1] - y) <= 10]
            for m in near:
                left.remove(m)
            todo += near
        groups.append(group)
    return groups


def touching(R):
    """k x k counts of the boundary pixel pairs each two regions share (doorway or wall)."""
    k = int(R.max()) + 1
    t = np.zeros((k, k), np.int64)
    for a, b in ((R[:, :-1], R[:, 1:]), (R[:-1, :], R[1:, :])):
        d = a != b
        np.add.at(t, (a[d], b[d]), 1)
    return t + t.T


def clear(a, b, dark):
    return graph.hits(a, b, dark) == 0


def main():
    with np.load(graph.need(graph.REGIONS, "run segment.py first (or make_data.py)")) as d:
        R, wall = d["regions"], d["wall"]
        names = {int(i): (str(t), str(k)) for i, t, k in zip(d["ids"], d["texts"], d["kinds"])}
        ink = Ink(d["dark"])
    dark = graph.plan_gray() < 110         # stricter than segment's 170, so faint marks don't block links
    free = ~wall
    known = {p[0] for p in graph.P}
    region_of = {t: i for i, (t, _) in names.items() if t}

    # Doorways: room i's open boundary with j (a hallway or another room), in clusters.
    doorways = {}
    for (i, j), pts in openings(R, free).items():
        if i <= WALKWAY or j == OUTSIDE:
            continue
        cs = [c for c in clusters(pts) if len(c) >= MIN_OPENING and not excluded(c)]
        if cs:
            doorways.setdefault(i, {})[j] = cs

    nodes, edges = graph.build(auto=False)
    hall = set(graph.N)
    original = set(edges)
    lines = [(a, b) for a, b in edges if a in hall and b in hall]
    new_nodes, new_edges, removed = {}, [], []

    def link(name, xy):
        """Joins a door spot to the nearest hallway line it can see, splitting the line if need be."""
        best, bd = None, math.inf
        for a, b in lines:
            (ax, ay), (bx, by) = nodes[a], nodes[b]
            vx, vy = bx - ax, by - ay
            length = math.hypot(vx, vy)
            t = 0.0 if length == 0 else max(0.0, min(1.0, ((xy[0] - ax) * vx + (xy[1] - ay) * vy) / length ** 2))
            p = (ax + vx * t, ay + vy * t)
            dist = math.hypot(xy[0] - p[0], xy[1] - p[1])
            if dist > MAX_LINK or dist >= bd:
                continue
            if clear(xy, p, dark):
                best, bd = (a, b, t, p, length), dist
        if best is None:
            return False
        a, b, t, p, length = best
        if t * length < 25:
            target = a
        elif (1 - t) * length < 25:
            target = b
        else:
            target = "h_" + name
            nodes[target] = new_nodes[target] = [round(p[0], 1), round(p[1], 1)]
            if (a, b) in original:
                removed.append([a, b])
            else:
                new_edges.remove((a, b))
            lines.remove((a, b))
            lines.extend([(a, target), (target, b)])
            new_edges.extend([(a, target), (target, b)])
            hall.add(target)
        new_edges.append((name, target))
        return True

    # Rooms with their own doorways onto the hallway.
    places, nested, place_doors = [], [], {}
    for i in sorted(names, key=lambda i: names[i][0]):
        text, kind = names[i]
        if not text or kind not in ("room", "teaching") or text in known:
            continue
        ways = doorways.get(i, {}).get(WALKWAY, [])
        doors = []
        for c in sorted(ways, key=len, reverse=True)[:3]:
            cx = sum(p[0] for p in c) / len(c)
            cy = sum(p[1] for p in c) / len(c)
            nx, ny = sum(p[2] for p in c), sum(p[3] for p in c)
            h = math.hypot(nx, ny) or 1.0
            nx, ny = nx / h, ny / h                      # into the hallway, roughly: a ratio of pixel steps
            fitted = wall_normal(ink, cx, cy, -nx, -ny)
            if fitted and fitted[2]:                     # on the plan's 45-degree grid: trust it
                nx, ny = -fitted[0][0], -fitted[0][1]    # straight out of the wall
            step = OUT_STEP
            while step > 4:                              # out into the hallway, but not into a wall
                x, y = int(round(cx + nx * step)), int(round(cy + ny * step))
                if free[y, x] and R[y, x] == WALKWAY:
                    break
                step -= 4
            xy = (round(cx + nx * step, 1), round(cy + ny * step, 1))
            if any(math.hypot(xy[0] - q[0], xy[1] - q[1]) < 40 for _, q, _ in doors):
                continue
            doors.append((f"r{text}_{len(doors)}", xy, (-nx, -ny)))     # toward the room
        linked = []
        for name, xy, dxy in doors:
            nodes[name] = new_nodes[name] = list(xy)
            if link(name, xy):
                linked.append([name, list(xy), list(dxy)])
            else:
                del nodes[name], new_nodes[name]
                print(f"  no hallway in sight of {text} {xy}")
        if linked:
            places.append([text, text, "Room", "room", linked])
            place_doors[i] = linked
        else:
            nested.append(i)

    # Hand-placed places that are rooms here too, so suites inside them can find a way in.
    for pid, _, _, _, ds in graph.P:
        if pid in region_of:
            place_doors[region_of[pid]] = [[n, list(xy), list(d)] for n, xy, d in ds]

    # The rest: the nearest room (through doorways) that has doors.
    touch = touching(R)
    for i in nested:
        text = names[i][0]
        parent = region_of.get(PARENT.get(text))
        if parent not in place_doors:
            parent, seen, todo = None, {i}, deque([i])
            while todo and parent is None:
                for j in doorways.get(todo.popleft(), {}):
                    if j in seen or j <= WALKWAY:
                        continue
                    seen.add(j)
                    if j in place_doors:
                        parent = j
                        break
                    todo.append(j)
        if parent is None:
            # No doorway found (a narrow one, or drawn closed): the neighbour with doors it shares most wall with.
            near = [(touch[i, j], j) for j in place_doors if touch[i, j] > 0]
            parent = max(near)[1] if near else None
        if parent is None:
            print(f"  no way in to {text}")
            continue
        ptext = names[parent][0] or "the next room"
        places.append([text, text, f"Inside {ptext}", "room", place_doors[parent], True])

    with open(graph.ROOMS, "w", encoding="utf-8") as f:
        json.dump({"nodes": new_nodes, "removed": removed, "edges": [list(e) for e in new_edges],
                   "places": places}, f, indent=1)
    print(f"{len(places)} rooms added, {sum(len(p) == 6 for p in places)} inside others; "
          f"{len(new_nodes)} points, {len(new_edges)} links")
    check(R, nodes, edges, removed, new_edges, places)


def check(R, nodes, edges, removed, new_edges, places):
    with Image.open(graph.PLAN) as plan:
        im = plan.convert("RGB")
    d = ImageDraw.Draw(im)
    f = graph.font(20)
    gone = {tuple(e) for e in removed}
    for a, b in [e for e in edges if e not in gone] + new_edges:
        d.line([tuple(nodes[a]), tuple(nodes[b])], fill=(0, 140, 255), width=4)
    for p in places:
        c = (200, 0, 160) if len(p) == 6 else (0, 160, 60)
        for _, (x, y), (dx, dy) in p[4]:
            d.line([(x, y), (x + dx * 40, y + dy * 40)], fill=c, width=3)
            d.ellipse([x - 8, y - 8, x + 8, y + 8], fill=c)
        if len(p) == 5:
            x, y = p[4][0][1]
            d.text((x + 10, y - 24), p[0], fill=(0, 120, 40), font=f)
    os.makedirs(graph.CHECK_DIR, exist_ok=True)
    im.resize((1500, 1222), Image.LANCZOS).save(os.path.join(graph.CHECK_DIR, "rooms_check.png"))


if __name__ == "__main__":
    main()
