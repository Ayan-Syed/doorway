"""BA1 floor 1 walkway graph, in pixels of FloorPlans/BA1-floor1-knighthacks-labeled.png (3000x2444).

Run from anywhere: python graph.py [--export] [--hand]
  -> check/graph_overlay.png (check), wall-crossing report,
     and with --export the Unity JSON (door directions measured on the drawing, room-number signs),
     the minimap and the pin textures.
--hand checks the hand-made graph alone (no rooms.json needed).
After --export always run basemap.py, walls.py and walkable.py: basemap adds the label areas and rebuilds the
place grid for the new place order (make_data.py runs everything in order).
"""
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

import doors

# Paths, all relative to this script so the folder works on any machine.
HERE = os.path.dirname(os.path.abspath(__file__))
BOX = os.path.normpath(os.path.join(HERE, "..", ".."))
PLAN = os.path.join(BOX, "FloorPlans", "BA1-floor1-knighthacks-labeled.png")
DATA_DIR = os.path.join(BOX, "Assets", "Navigation", "Data")
UI_DIR = os.path.join(BOX, "Assets", "Navigation", "UI")
FONT = os.path.join(BOX, "Assets", "Navigation", "Fonts", "GoogleSans-400.ttf")
CHECK_DIR = os.path.join(HERE, "check")          # check images, gitignored
LABELS = os.path.join(HERE, "ba1f1_labels.json")
REGIONS = os.path.join(HERE, "regions.npz")
ROOMS = os.path.join(HERE, "rooms.json")
PLAN_JSON = os.path.join(DATA_DIR, "ba1-floor1.json")

PX_PER_M = 29.196504          # plan px per metre (fitted to UCF's outline of BA1)
ORIGIN = (1500.0, 1222.0)     # plan px that becomes map (0, 0)
MINIMAP_WIDTH = 2048          # width of ba1-floor1-plan.png, and the basis of image.metersPerPixel

# Where the plan sits on Earth, fitted to UCF's campus map outline of BA1 (docs/geo/ucf-locations.json id 45):
# east/north metres from (lat, lon), turned by angle (degrees anticlockwise) and moved by (x, z), are plan metres.
GEO = {"lat": 28.6010684, "lon": -81.1991664, "angle": 315.371, "x": -2.272, "z": 7.066}


# The .bytes files start with a 4-letter magic and a uint16 version, so the app can tell a stale or foreign
# file from a broken one (CampusData, WallMesh, FloorMap.ReadPlaceGrid and Walkable check them).
PLACES_MAGIC, WALLS_MAGIC, CAMPUS_MAGIC, WALKABLE_MAGIC = b"DWPL", b"DWWL", b"DWCP", b"DWWK"
FORMAT_VERSION = 1


def place_id_hash(ids):
    """FNV-1a (32-bit) of the place ids, one per line. places.bytes carries it, so the app can tell a place grid
    made for a different place list or order (FloorPlan.PlaceIdHash computes the same)."""
    h = 0x811C9DC5
    for b in "\n".join(ids).encode("utf-8"):
        h = ((h ^ b) * 0x01000193) & 0xFFFFFFFF
    return h


def need(path, how):
    """Fails loudly on a missing input instead of carrying on with half the data."""
    if not os.path.exists(path):
        sys.exit(f"Missing {os.path.relpath(path, BOX)}: {how}")
    return path


def font(size):
    return ImageFont.truetype(need(FONT, "the check images draw with the app's font"), size)


# Hallway points, plan px (y down).
N = {
    # West wing: north entrance (189), the lobby (138), the diagonal hall (138) to The Exchange.
    "w_189": (1000, 330), "w_189s": (1000, 560), "w_680": (1010, 690),
    "l1": (1120, 760), "l2": (1120, 870), "l3": (1125, 1065), "l4": (1135, 1290), "l5": (1050, 1410),
    "d1": (980, 1460), "d2": (870, 1580), "d3": (760, 1700), "d4": (640, 1810),
    # The side hall west off the lobby, by 133 and 135.
    "k1": (900, 668), "k2": (770, 668),
    # Hall 102 east-west, lobby to atrium.
    "c1": (1300, 1290), "c2": (1500, 1290), "c3": (1690, 1290),
    # Atrium wing: hall 102 north-south, atrium (128) ring, hall 101 north-south and east-west.
    "a1": (1670, 480), "a2": (1670, 700), "a3": (1700, 800), "a4": (1790, 900),
    "t_n": (1990, 940), "t_ne": (2200, 900), "t_w1": (1775, 1105), "t_w2": (1775, 1225),
    "t_sw": (1760, 1365), "t_s": (1990, 1385), "t_se": (2220, 1365), "t_e0": (2216, 1288), "t_e1": (2205, 1060),
    "b1": (2300, 790), "b2": (2300, 600), "b3": (2300, 470),
    "e0": (2285, 1288), "e1": (2373, 1295), "e2": (2550, 1288), "e3": (2690, 1288),
    # South stubs to 107's doors.
    "s1": (1735, 1460), "s2": (1735, 1600), "u1": (2240, 1460), "u2": (2240, 1600),
    # Corridor 197, behind the diagonal hall: in through the doorway by 146 (g197), down its middle.
    "g197": (904, 1663), "c197a": (1150, 1560), "c197b": (1060, 1640), "c197c": (970, 1725),
    "c197d": (870, 1805), "c197e": (790, 1890), "c197f": (750, 1990),
}

# Hallway edges, undirected.
E = [
    ("w_189", "w_189s"), ("w_189s", "w_680"), ("w_680", "l1"), ("l1", "l2"), ("l2", "l3"), ("l3", "l4"),
    ("l4", "l5"), ("l5", "d1"), ("d1", "d2"), ("d2", "d3"), ("d3", "d4"),
    ("w_680", "k1"), ("k1", "k2"),
    ("l4", "c1"), ("c1", "c2"), ("c2", "c3"), ("c3", "t_sw"),
    ("a1", "a2"), ("a2", "a3"), ("a3", "a4"), ("a4", "t_n"), ("t_n", "t_ne"), ("t_ne", "b1"),
    ("a4", "t_w1"), ("t_w1", "t_w2"), ("t_w2", "t_sw"), ("t_sw", "t_s"), ("t_s", "t_se"), ("t_se", "t_e0"),
    ("t_e0", "t_e1"), ("t_e1", "t_ne"),
    ("b1", "b2"), ("b2", "b3"),
    ("t_e0", "e0"), ("e0", "e1"), ("e1", "e2"), ("e2", "e3"),
    ("t_sw", "s1"), ("s1", "s2"), ("t_se", "u1"), ("u1", "u2"),
    ("d2", "g197"), ("d3", "g197"), ("g197", "c197c"),
    ("c197a", "c197b"), ("c197b", "c197c"), ("c197c", "c197d"), ("c197d", "c197e"), ("c197e", "c197f"),
]


def D(dx, dy):
    """A direction, normalised."""
    h = math.hypot(dx, dy)
    return (dx / h, dy / h)


# Places: (id, name, detail, kind, [(door point, (x, y), direction toward the door)]).
# The door point is a spot in the hallway just outside the door.
P = [
    ("107", "107", "Lecture hall", "room", [("p107w", (1745, 1612), D(1, 0)), ("p107e", (2228, 1612), D(-1, 0))]),
    ("119", "119", "Lecture hall", "room", [("p119w", (1732, 722), D(1, 0)), ("p119e", (2240, 722), D(-1, 0))]),
    ("121", "121", "Classroom", "room", [("p121n", (1668, 520), D(-1, 0)), ("p121s", (1668, 680), D(-1, 0))]),
    ("122", "122", "Classroom", "room", [("p122", (1700, 800), D(-0.59, 0.8))]),
    ("126", "126", "Classroom", "room", [("p126", (1640, 1290), D(-0.45, 0.9))]),
    ("110", "110", "Classroom", "room", [("p110", (2373, 1295), D(0.7, 0.7))]),
    ("113", "113", "Classroom", "room", [("p113", (2550, 1288), D(0, -1))]),
    ("115", "115", "Room", "room", [("p115", (2222, 1095), D(1, 0))]),
    ("116", "116", "Room", "room", [("p116", (2222, 1015), D(1, 0))]),
    ("117", "117", "Former Java City café", "room", [("p117", (2300, 490), D(1, 0))]),
    ("130", "130", "Lounge by the north lobby", "room", [("p130", (1190, 745), D(0, -1))]),
    ("145", "145", "Office suite", "room", [("p145", (1075, 1300), D(-1, 0))]),
    ("146", "146", "Classroom", "room", [("p146", (843, 1607), D(-0.745, -0.667))]),
    ("147", "147", "Classroom", "room", [("p147", (705, 1712), D(-0.85, -0.53))]),
    ("148", "148 The Exchange", "Events space", "room", [("p148", (565, 1880), D(-0.7, 0.7))]),
    ("wc-m-a", "Men's restroom", "Atrium wing, by 123", "restroom", [("pma", (1752, 1105), D(-1, 0))]),
    ("wc-w-a", "Women's restroom", "Atrium wing, by 125", "restroom", [("pwa", (1752, 1225), D(-1, 0))]),
    ("wc-m-w", "Men's restroom", "Lobby, by the elevators", "restroom", [("pmw", (1065, 790), D(-1, 0))]),
    ("wc-w-w", "Women's restroom", "Lobby, by the elevators", "restroom", [("pww", (1065, 935), D(-1, 0))]),
    ("elev-a", "Elevator", "Atrium, in front of 107", "elevator", [("pea", (1950, 1395), D(0, 1))]),
    ("elev-w", "Elevators", "Lobby 138", "elevator", [("pew", (1072, 1065), D(-1, 0))]),
    ("stair-127", "Main stairs", "Atrium", "stairs", [("ps127", (1990, 1350), D(0, -1))]),
    ("stair-129", "Stairs 129", "Lobby 138", "stairs", [("ps129", (1178, 845), D(1, 0))]),
    ("stair-120", "Stairs 120", "North-west, by 121", "stairs", [("ps120", (1655, 440), D(-1, 0))]),
    ("stair-118", "Stairs 118", "North-east, by 117", "stairs", [("ps118", (2318, 420), D(1, 0))]),
    ("exit-n", "North entrance", "Through 189", "exit", [("xn", (945, 215), D(0, -1))]),
    ("exit-nw", "North-west entrance", "Atrium wing, by 119", "exit", [("xnw", (1728, 410), D(0, -1))]),
    ("exit-ne", "North-east entrance", "Atrium wing, by 119", "exit", [("xne", (2262, 410), D(0, -1))]),
    ("exit-sw", "South-west entrance", "Atrium wing, by 126", "exit", [("xsw", (1610, 1665), D(-0.7, 0.7))]),
    ("exit-se", "South-east entrance", "Atrium wing, by 110", "exit", [("xse", (2360, 1665), D(0.7, 0.7))]),
    ("exit-e", "East exit", "Toward Engineering", "exit", [("xe", (2735, 1288), D(1, 0))]),
    ("exit-ba2", "Exit to BA II", "South end of lobby 138", "exit", [("xba2", (1190, 1485), D(0, 1))]),
]

# Which hallway points each door point joins. Two links let a route enter the door spot from
# either side along the hall, so it doesn't double back to a single node; a door spot that sits on a
# hallway point (p110 on e1, p113 on e2) joins that point too, or a route overshoots and turns back.
LINKS = {
    "p107w": ["s2"], "p107e": ["u2"], "p119w": ["a2", "a3"], "p119e": ["b1", "t_ne"],
    "p121n": ["a1", "a2"], "p121s": ["a2", "a1"], "p122": ["a3"], "p126": ["c2", "c3"],
    "p110": ["e0", "e2", "e1"], "p113": ["e1", "e3", "e2"], "p115": ["t_e1", "t_e0"], "p116": ["t_e1", "t_ne"],
    "p117": ["b2", "b3"], "p130": ["l1"], "p145": ["l4", "l3"], "p146": ["d2", "d3"],
    "p147": ["d3", "d4"], "p148": ["d4"], "pma": ["t_w1"], "pwa": ["t_w2"],
    "pmw": ["l1", "l2"], "pww": ["l2", "l3"], "pea": ["t_s", "t_sw"], "pew": ["l3"],
    "ps127": ["t_s"], "ps129": ["l2"], "ps120": ["a1"], "ps118": ["b3"],
    "xn": ["w_189"], "xnw": ["a1"], "xne": ["b3"], "xsw": ["s2"],
    "xse": ["u2"], "xe": ["e3"], "xba2": ["l4", "l5"],
}

# Names for the doors of places with more than one. The r... keys are door names rooms.py makes,
# so check them after any change there (main() warns about keys that match no door).
DOOR_LABELS = {
    "p107w": "West door", "p107e": "East door", "p119w": "West door", "p119e": "East door",
    "p121n": "North door", "p121s": "South door",
    "r139_0": "South door", "r139_1": "North door",
    "r149_1": "West door", "r149_2": "East door",
}

# Overlay colours by place kind.
KIND_COLORS = {"room": (0, 170, 80), "restroom": (200, 0, 200), "elevator": (230, 120, 0),
               "stairs": (150, 90, 0), "exit": (220, 0, 0)}


def auto_rooms():
    """The doors rooms.py found. Missing is an error: without it the export has 32 places, not 71."""
    with open(need(ROOMS, "run rooms.py first (or make_data.py)"), encoding="utf-8") as f:
        return json.load(f)


def places(auto=True):
    """P, plus every rooms.json place in the same shape (its 'inside' flag dropped)."""
    out = list(P)
    if auto:
        for p in auto_rooms()["places"]:
            pid, name, detail, kind, doors = p[:5]
            out.append((pid, name, detail, kind, [(n, tuple(xy), tuple(d)) for n, xy, d in doors]))
    return out


def build(auto=True):
    """(nodes, edges): hallway points, door points, their links, and with auto the rooms.json doors."""
    nodes = dict(N)
    edges = list(E)
    for _, _, _, _, doors in P:
        for point, xy, _ in doors:
            assert point not in nodes, point
            nodes[point] = xy
    for point, joins in LINKS.items():
        for hall in joins:
            assert point in nodes and hall in nodes, (point, hall)
            edges.append((point, hall))
    if auto:
        rooms = auto_rooms()
        for name, xy in rooms["nodes"].items():
            assert name not in nodes, name
            nodes[name] = tuple(xy)
        removed = {tuple(e) for e in rooms["removed"]}
        edges = [e for e in edges if e not in removed]
        edges += [tuple(e) for e in rooms["edges"]]
    for a, b in edges:
        assert a in nodes and b in nodes, (a, b)
    return nodes, edges


def plan_gray():
    with Image.open(need(PLAN, "the labeled plan PNG belongs in FloorPlans/")) as im:
        return np.asarray(im.convert("L"))


def hits(p, q, dark):
    """Dark pixels on the line p -> q, ignoring 10 px around each end (doors, labels next to points)."""
    length = math.hypot(q[0] - p[0], q[1] - p[1])
    steps = max(int(length), 1)
    count = 0
    for i in range(steps + 1):
        t = i / steps
        if t * length < 10 or (1 - t) * length < 10:
            continue
        x = int(round(p[0] + (q[0] - p[0]) * t))
        y = int(round(p[1] + (q[1] - p[1]) * t))
        count += bool(dark[y, x])
    return count


def wall_hits(nodes, edges, dark):
    """Dark pixels crossed by each link, ignoring 10 px around each end (doors, labels next to points)."""
    out = []
    for a, b in edges:
        h = hits(nodes[a], nodes[b], dark)
        if h > 0:
            out.append((a, b, h))
    return out


def components(nodes, edges):
    """Connected groups of the graph (union-find with path halving); there should be exactly 1."""
    parent = {n: n for n in nodes}

    def find(n):
        while parent[n] != n:
            parent[n] = parent[parent[n]]
            n = parent[n]
        return n

    for a, b in edges:
        parent[find(a)] = find(b)
    groups = {}
    for n in nodes:
        groups.setdefault(find(n), []).append(n)
    return list(groups.values())


def overlay(nodes, edges, out, crop=None):
    """The check image: the graph drawn on the plan, door points coloured by place kind."""
    with Image.open(PLAN) as plan:
        im = plan.convert("RGB")
    d = ImageDraw.Draw(im)
    f = font(18)
    for a, b in edges:
        d.line([nodes[a], nodes[b]], fill=(0, 140, 255), width=5)
    doors = {}
    for pid, _, _, kind, ds in P:
        for point, xy, (dx, dy) in ds:
            doors[point] = (pid, kind, dx, dy)
    for n, (x, y) in nodes.items():
        if n in doors:
            pid, kind, dx, dy = doors[n]
            c = KIND_COLORS[kind]
            d.line([(x, y), (x + dx * 45, y + dy * 45)], fill=c, width=4)
            d.ellipse([x - 9, y - 9, x + 9, y + 9], fill=c)
            d.text((x + 10, y - 26), pid, fill=c, font=f)
        else:
            d.ellipse([x - 6, y - 6, x + 6, y + 6], fill=(0, 90, 200))
            d.text((x + 8, y + 4), n, fill=(0, 90, 200), font=f)
    if crop:
        im = im.crop(crop)
    im.save(out)
    return im


def to_m(x, y):
    """Plan px to plan metres: x right, z toward the plan's top, rounded to 1 mm."""
    return round((x - ORIGIN[0]) / PX_PER_M, 3), round(-(y - ORIGIN[1]) / PX_PER_M, 3)


def label_text(gray, labels):
    """(strokes, boxes): the printed label text's own strokes (components of gray < 200 lying wholly
    inside a label's box), and every label box. segment.py and minimap() erase the strokes."""
    from scipy import ndimage
    comp, _ = ndimage.label(gray < 200, structure=np.ones((3, 3)))
    objs = ndimage.find_objects(comp)
    h, w = gray.shape
    strokes = np.zeros(gray.shape, bool)
    boxes = np.zeros(gray.shape, bool)
    for lab in labels:
        hw, hh = 9 * len(lab["text"]) + 8, 16
        x0, x1 = max(int(lab["x"] - hw), 0), min(int(lab["x"] + hw) + 1, w)
        y0, y1 = max(int(lab["y"] - hh), 0), min(int(lab["y"] + hh) + 1, h)
        boxes[y0:y1, x0:x1] = True
        for i in np.unique(comp[y0:y1, x0:x1]):
            if i == 0:
                continue
            sy, sx = objs[i - 1]
            if sy.start >= y0 and sy.stop <= y1 and sx.start >= x0 and sx.stop <= x1:
                strokes[sy, sx] |= comp[sy, sx] == i
    return strokes, boxes & (comp == 0)


def load_labels():
    with open(need(LABELS, "run labels.py first (or make_data.py)"), encoding="utf-8") as f:
        return json.load(f)


def fitted_places(nodes):
    """places(), each door's direction measured on the drawing (doors.fit_direction), and the room-number signs.
    Prints the doors with no on-grid wall in front to measure (those keep their drawn direction)."""
    with np.load(need(REGIONS, "run segment.py first (or make_data.py)")) as d:
        R, dark = d["regions"], d["dark"]
        names = {int(i): (str(t), str(k)) for i, t, k in zip(d["ids"], d["texts"], d["kinds"])}
    ink = doors.Ink(dark)
    out, fitted = [], {}
    for pid, name, detail, kind, ds in places():
        new = []
        for n, xy, (dx, dy) in ds:
            if n not in fitted:
                fitted[n] = doors.fit_direction(R, ink, nodes[n][0], nodes[n][1], dx, dy)
                if fitted[n][2] is not None and not fitted[n][3]:
                    print(f"  door {n} of {pid}: what's in front is at {fitted[n][2]:.1f} deg, off the 45-degree "
                          f"grid; kept as drawn")
                elif fitted[n][2] is None:
                    print(f"  door {n} of {pid}: no wall in front to measure, kept as drawn")
            new.append((n, xy, fitted[n][0]))
        out.append((pid, name, detail, kind, new))
    return out, doors.signs_for(out, nodes, R, names, ink)


def export(nodes, edges):
    """Writes ba1-floor1.json (what Unity loads) and the minimap. basemap.py then replaces the labels."""
    os.makedirs(DATA_DIR, exist_ok=True)
    measured, signs = fitted_places(nodes)
    out_places = []
    for pid, name, detail, kind, ds in measured:
        out_places.append({"id": pid, "name": name, "detail": detail, "kind": kind, "doors": [
            {"node": n, "label": DOOR_LABELS.get(n, ""), "dx": round(dx, 4), "dz": round(-dy, 4)}
            for n, _, (dx, dy) in ds]})
    labels = load_labels()
    # First-pass labels: whole room numbers at the printed spot. basemap.py swaps in labels with areas.
    first = []
    for lab in labels:
        if lab["text"].isdigit():
            x, z = to_m(lab["x"], lab["y"])
            first.append({"text": lab["text"], "x": x, "z": z})
    with Image.open(PLAN) as im:
        w, h = im.size
    left, top = to_m(0, 0)
    height = round(h * MINIMAP_WIDTH / w)
    plan = {
        "building": "Business Administration I",
        "floor": "Floor 1",
        "nodes": [{"id": n, "x": to_m(*xy)[0], "z": to_m(*xy)[1]} for n, xy in nodes.items()],
        "edges": [{"a": a, "b": b} for a, b in edges],
        "places": out_places,
        "labels": first,
        "signs": signs,
        "image": {"left": left, "top": top, "metersPerPixel": round(w / PX_PER_M / MINIMAP_WIDTH, 6),
                  "width": MINIMAP_WIDTH, "height": height},
        "geo": GEO,
    }
    with open(PLAN_JSON, "w", encoding="utf-8") as f:
        json.dump(plan, f, ensure_ascii=False, indent=1)
    minimap(labels, (MINIMAP_WIDTH, height))
    print("exported", len(plan["nodes"]), "nodes", len(plan["edges"]), "edges",
          len(out_places), "places", len(first), "labels", len(signs), "signs")


def minimap(all_labels, size):
    """Walls as white lines whose alpha is the plan's darkness, with the printed room numbers removed
    (the app draws its own, kept upright while the map turns)."""
    from scipy import ndimage
    gray = plan_gray().astype(np.float32)
    ink = np.clip((235 - gray) / 175, 0, 1)
    # The text's own strokes, and the light anti-aliasing around them that belongs to no stroke.
    strokes, loose = label_text(gray, all_labels)
    ink[strokes | loose] = 0
    ink = ndimage.maximum_filter(ink, size=3)      # 1 px thicker, so the lines survive the downscale
    alpha = Image.fromarray((ink * 255).astype(np.uint8), "L").resize(size, Image.LANCZOS)
    im = Image.new("RGBA", size, (255, 255, 255, 0))
    im.putalpha(alpha)
    im.save(os.path.join(DATA_DIR, "ba1-floor1-plan.png"))


def finish(im, size, color):
    """Shrinks a supersampled picture. Fully transparent pixels take the main color, so filtering never
    pulls in a dark fringe."""
    im = im.resize(size, Image.LANCZOS)
    a = np.asarray(im).copy()
    a[a[..., 3] == 0, :3] = color
    return Image.fromarray(a, "RGBA")


def white(alpha):
    """A white RGBA picture with this alpha (0..1, truncated to 8 bits)."""
    a = (np.clip(alpha, 0, 1) * 255).astype(np.uint8)
    rgba = np.full(a.shape + (4,), 255, np.uint8)
    rgba[..., 3] = a
    return Image.fromarray(rgba, "RGBA")


def textures():
    """The destination pin's textures. (Cone.png is basemap.py's; the old chevron and glow are gone.)"""
    from PIL import ImageFilter
    S = 4
    os.makedirs(UI_DIR, exist_ok=True)
    # Ring for the destination marker on the floor, with a soft glow.
    n = 256 * S
    c, r0, r1 = n / 2, 0.40 * n, 0.47 * n
    ring = Image.new("L", (n, n), 0)
    d = ImageDraw.Draw(ring)
    d.ellipse([c - r1, c - r1, c + r1, c + r1], fill=255)
    d.ellipse([c - r0, c - r0, c + r0, c + r0], fill=0)
    glow = ring.filter(ImageFilter.GaussianBlur(0.04 * n))
    a = np.maximum(np.asarray(ring) / 255.0, np.asarray(glow) / 255.0 * 0.8)
    finish(white(a), (256, 256), (255, 255, 255)).save(os.path.join(UI_DIR, "Ring.png"))
    # Vertical light beam: bright at the floor, fading upward, soft at the sides.
    yy, xx = np.mgrid[0:256, 0:64]
    u, v = (xx + 0.5) / 64, 1 - (yy + 0.5) / 256        # v = 0 at the bottom
    white(np.exp(-((u - 0.5) / 0.2) ** 2) * (1 - v) ** 1.6).save(os.path.join(UI_DIR, "Beam.png"))
    print("textures written")


def main():
    hand = "--hand" in sys.argv
    nodes, edges = build(auto=not hand)
    dark = plan_gray() < 110
    groups = components(nodes, edges)
    print("points", len(nodes), "links", len(edges), "groups", len(groups))
    for a, b, h in wall_hits(nodes, edges, dark):
        print(f"  crosses dark: {a} - {b}: {h} px")
    if not hand:
        missing = [k for k in DOOR_LABELS if k not in nodes]
        if missing:
            print("  DOOR_LABELS match no door:", ", ".join(missing))
    os.makedirs(CHECK_DIR, exist_ok=True)
    im = overlay(nodes, edges, os.path.join(CHECK_DIR, "graph_overlay.png"))
    im.resize((2000, 1629), Image.LANCZOS).save(os.path.join(CHECK_DIR, "graph_overlay_small.png"))
    if "--export" in sys.argv:
        if hand:
            sys.exit("--export needs rooms.json: drop --hand")
        if len(groups) != 1:
            sys.exit(f"The graph is in {len(groups)} pieces: fix it before exporting")
        export(nodes, edges)
        textures()
        if "--pipeline" not in sys.argv:
            print("\nWARNING: ba1-floor1.json now has labels without areas and a place order the place grid may not"
                  "\nmatch. Run basemap.py, walls.py and walkable.py next (or make_data.py, which runs everything).")


if __name__ == "__main__":
    main()
