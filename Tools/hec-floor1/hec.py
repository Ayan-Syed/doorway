"""HEC (L3Harris Engineering Center) floor 1, from Knight Hacks' traced room envelopes: the shared geometry.

The source, FloorPlans/knighthacks-data/HEC-floor1-rooms-knighthacks-traced.json, is vector: the building's
outline and 45 room polygons in the pixels of a 1400 x 800 review drawing. Its envelopes close across doors and
most rooms have no known number (temporary ids U01 to U45). So unlike BA1 there is no scan to read walls and
doors from; everything here is drawn from the polygons:
  - the drawing is fitted to OpenStreetMap's outline of HEC (FIT, redone by python hec.py --fit), which gives its
    scale and where it sits on Earth, and is then put in BA1 floor 1's plan metres (Frame), so the two floors and
    the campus map share one frame and the app switches between the buildings without moving the map;
  - the rooms are drawn onto a raster at BA1's resolution (so basemap.py's line widths mean the same here), and
    the slivers between rooms that are too narrow to walk (tracing gaps) go to the room beside them;
  - the hallways' centre lines become the walkway graph, each room gets one door in the middle of its longest
    wall on a hallway, and the entrances come from onsite.json.
onsite.json holds what only walking the floor can tell: real room numbers for the U ids, and the entrances.
"""
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

HERE = os.path.dirname(os.path.abspath(__file__))
BOX = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(BOX, "Tools", "ba1-floor1"))     # basemap, walls, datacheck, graph

import graph              # noqa: E402  (BA1's: its GEO is the frame both floors share)

SOURCE = os.path.join(BOX, "FloorPlans", "knighthacks-data", "HEC-floor1-rooms-knighthacks-traced.json")
ONSITE = os.path.join(HERE, "onsite.json")
OSM = os.path.join(BOX, "Tools", "campus", "osm", "ucf.json")
OSM_WAY = 154245241                     # "L3Harris Engineering Center", short_name HEC
DATA_DIR = os.path.join(BOX, "Assets", "Navigation", "Data")
CHECK_DIR = os.path.join(HERE, "check")          # check images, gitignored
FLOOR = "hec-floor1"
PLAN_JSON = os.path.join(DATA_DIR, f"{FLOOR}.json")
BUILDING, SHORT_NAME = "L3Harris Engineering Center", "HEC"     # OSM's name: campus.py marks it indoor by it

PX_PER_M = 29.196504          # raster px per metre: BA1's, so basemap.py's wall widths are the same here
MARGIN = 4.0                  # metres of ground round the building on the raster
MINIMAP_WIDTH = 2048          # width of hec-floor1-plan.png, and the basis of image.metersPerPixel
CELL = 0.1                    # metres: the walkway graph and the walkable grid
MIN_HALL = 1.0                # metres: a gap narrower than this between rooms is a tracing gap, not a hallway
NECK = 0.9                    # metres: the widest a narrow neck joining two hallways is opened (a door's width)
EARTH_RADIUS = 6378137.0      # the same as PlanGeo in FloorPlan.cs

# The source drawing fitted to OSM's outline of HEC (python hec.py --fit): drawing px (x right, y down) to metres
# east and north of (lat, lon): east, north = metersPerPixel * rotate(angle) * (x, -y) + (east, north).
FIT = {"lat": 28.6005023, "lon": -81.1976487, "metersPerPixel": 0.0957296, "angle": 358.68266,
       "east": -58.7576, "north": 35.1050}

KINDS = {"LIFT": "elevator", "STAIR": "stairs", "WC": "restroom", "CLASSROOM": "teaching",
         "LECTURE HALL": "teaching"}
DETAIL = {"OFFICE": "Office", "CLASSROOM": "Classroom", "LECTURE HALL": "Lecture hall",
          "MEETING ROOM": "Meeting room", "WORK ROOM": "Work room"}
OUTSIDE, WALKWAY = 1, 2       # fixed region ids; rooms start at 3


def need(path, how):
    if not os.path.exists(path):
        sys.exit(f"Missing {os.path.relpath(path, BOX)}: {how}")
    return path


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def source():
    return load(need(SOURCE, "it is Knight Hacks' traced HEC floor 1 (FloorPlans/knighthacks-data)"))


def onsite():
    return load(need(ONSITE, "it holds the room numbers and entrances checked on site"))


class Frame:
    """Drawing px -> plan metres -> raster px. The plan metres are BA1 floor 1's (graph.GEO), so both floors and the
    one campus map share a frame and the app can show either without moving the map. The raster is axis-aligned
    in that frame (HEC's walls run at an angle across it, as BA1's do) and covers the outline plus MARGIN."""

    def __init__(self, outline):
        self.geo_block = dict(graph.GEO)
        p = self.plan(outline)
        self.left, self.top = p[:, 0].min() - MARGIN, p[:, 1].max() + MARGIN
        self.width = int(math.ceil((p[:, 0].max() + MARGIN - self.left) * PX_PER_M))
        self.height = int(math.ceil((self.top - p[:, 1].min() + MARGIN) * PX_PER_M))
        self.origin = (-self.left * PX_PER_M, self.top * PX_PER_M)    # the raster px of plan (0, 0)

    def plan(self, pts):
        """Drawing px -> plan metres (N x 2): FIT to latitude and longitude, then BA1's PlanGeo.ToPlan."""
        p = np.asarray(pts, float).reshape(-1, 2)
        s, a, rad = FIT["metersPerPixel"], math.radians(FIT["angle"]), math.pi / 180
        x, y = p[:, 0], -p[:, 1]
        east = s * (x * math.cos(a) - y * math.sin(a)) + FIT["east"]
        north = s * (x * math.sin(a) + y * math.cos(a)) + FIT["north"]
        lat = FIT["lat"] + north / (EARTH_RADIUS * rad)
        lon = FIT["lon"] + east / (EARTH_RADIUS * rad * math.cos(FIT["lat"] * rad))
        g = self.geo_block
        e = (lon - g["lon"]) * rad * EARTH_RADIUS * math.cos(g["lat"] * rad)
        n = (lat - g["lat"]) * rad * EARTH_RADIUS
        b = math.radians(g["angle"])
        return np.stack([e * math.cos(b) - n * math.sin(b) + g["x"], e * math.sin(b) + n * math.cos(b) + g["z"]], 1)

    def raster(self, pts):
        """Drawing px -> raster px, as an N x 2 array."""
        return np.asarray([self.to_px(x, z) for x, z in self.plan(pts)])

    def to_m(self, x, y):
        """Raster px -> plan metres, rounded to 1 mm (graph.to_m, for this raster)."""
        return round((x - self.origin[0]) / PX_PER_M, 3), round(-(y - self.origin[1]) / PX_PER_M, 3)

    def to_px(self, x, z):
        return x * PX_PER_M + self.origin[0], -z * PX_PER_M + self.origin[1]

    def geo(self):
        """The plan's geo block: BA1's, since the plan metres are BA1's."""
        return self.geo_block


def fill(shape, polygons):
    """Polygons (raster px) filled with their value, later ones over earlier: [(points, value)] -> int32 array."""
    im = Image.new("I", (shape[1], shape[0]), 0)
    d = ImageDraw.Draw(im)
    for pts, value in polygons:
        d.polygon([tuple(p) for p in pts], fill=int(value))
    return np.asarray(im, np.int32)


def inside_outline(outline, x, y):
    """Even-odd test of a drawing point against the outline."""
    pts = np.asarray(outline, float)
    hit = False
    for (x1, y1), (x2, y2) in zip(pts, np.roll(pts, -1, axis=0)):
        if (y1 > y) != (y2 > y) and x < x1 + (y - y1) * (x2 - x1) / (y2 - y1):
            hit = not hit
    return hit


def rooms(src, site):
    """The rooms inside the outline, in source order: [{label, text, kind, detail, known, points}]. text is the
    room's number when known (the source's, or onsite.json's), else its U id."""
    numbers = site.get("numbers", {})
    out = []
    for r in src["rooms"]:
        pts = np.asarray(r["points"], float)
        cx, cy = pts.mean(axis=0)
        if not inside_outline(src["outline"], cx, cy):
            continue                     # U45, a stair outside the east end
        uid = r["label"].replace("HEC-1-", "")
        number = numbers.get(uid) or (r["label"] if r["label_status"] == "corroborated-room-number"
                                      or r["label_status"] == "number-known-placement-inferred" else "")
        source_kind = r["source_label"].upper()
        out.append({"label": r["label"], "uid": uid, "text": number or uid, "known": bool(number),
                    "inferred": r["label_status"] == "number-known-placement-inferred" and uid not in numbers,
                    "kind": KINDS.get(source_kind, "room"), "source": source_kind,
                    "detail": DETAIL.get(source_kind, ""), "points": pts})
    return out


def regions(src, site):
    """(frame, R, ids, texts, kinds, rooms, dark) like segment.py's regions.npz: R the region of every raster px
    (1 outside, 2 walkway, 3 + i room i); dark the stair treads and lift crosses, drawn faintly by basemap."""
    frame = Frame(src["outline"])
    shape = (frame.height, frame.width)
    rs = rooms(src, site)
    inside = fill(shape, [(frame.raster(src["outline"]), 1)]) > 0
    lab = fill(shape, [(frame.raster(r["points"]), 3 + i) for i, r in enumerate(rs)]) * inside
    gap = inside & (lab == 0)
    # Hallways: the gaps at least MIN_HALL wide (an opening by distances), the largest piece and any big ones.
    r = MIN_HALL / 2 * PX_PER_M
    core = ndimage.distance_transform_edt(gap) > r
    hall = (ndimage.distance_transform_edt(~core) <= r) & gap
    pieces, n = ndimage.label(hall)
    sizes = np.bincount(pieces.ravel())
    sizes[0] = 0
    keep = sizes >= 4 * PX_PER_M ** 2            # 4 m2
    keep[int(np.argmax(sizes))] = True
    hall = reconnect(keep[pieces] & hall, gap)
    # Everything else inside goes to the nearest room: the slivers along walls and between rooms.
    _, (iy, ix) = ndimage.distance_transform_edt(lab == 0, return_indices=True)
    R = lab[iy, ix]
    R[hall] = WALKWAY
    R[~inside] = OUTSIDE
    ids = [OUTSIDE, WALKWAY] + [3 + i for i in range(len(rs))]
    texts = ["", ""] + [r["text"] for r in rs]
    kinds = ["outside", "walkway"] + [r["kind"] for r in rs]
    return frame, R, ids, texts, kinds, rs, details(frame, shape, rs)


def reconnect(hall, gap):
    """Hallway pieces cut off from the largest by a neck narrower than MIN_HALL (a doorway, or the tracing pinching
    a hallway) are joined back along the shortest way through the gaps, NECK wide where the gap allows. Pieces the
    gaps don't reach are dropped."""
    step = CELL * PX_PER_M
    h, w = int(hall.shape[0] / step), int(hall.shape[1] / step)
    ys = np.minimum((np.arange(h) * step).astype(int)[:, None] + np.arange(3)[None, :], hall.shape[0] - 1)
    xs = np.minimum((np.arange(w) * step).astype(int)[:, None] + np.arange(3)[None, :], hall.shape[1] - 1)
    cells = lambda m: m[ys[:, :, None, None], xs[None, None, :, :]].any(axis=(1, 3))   # any of 3 x 3 samples
    open_ = cells(gap)
    pieces, n = ndimage.label(hall)
    sizes = np.bincount(pieces.ravel())
    sizes[0] = 0
    main = int(np.argmax(sizes))
    piece_cells = pieces[np.minimum((np.arange(h) * step + 1).astype(int), hall.shape[0] - 1)[:, None],
                         np.minimum((np.arange(w) * step + 1).astype(int), hall.shape[1] - 1)[None, :]]
    eight = np.ones((3, 3), bool)
    joined = {main}
    paint = Image.new("L", (hall.shape[1], hall.shape[0]), 0)
    d = ImageDraw.Draw(paint)
    while True:
        # Distances out from everything joined so far, through the gaps (a wavefront, one cell a step).
        dist = np.full((h, w), -1, np.int32)
        front = np.isin(piece_cells, list(joined)) & open_
        dist[front] = 0
        k = 0
        while front.any():
            k += 1
            front = ndimage.binary_dilation(front, eight) & open_ & (dist < 0)
            dist[front] = k
        reach = [(int(dist[(piece_cells == p) & (dist >= 0)].min()), p) for p in range(1, n + 1)
                 if p not in joined and sizes[p] and ((piece_cells == p) & (dist >= 0)).any()]
        if not reach:
            break
        _, p = min(reach)
        ys_, xs_ = np.nonzero((piece_cells == p) & (dist >= 0))
        at = int(np.argmin(dist[ys_, xs_]))
        y, x = int(ys_[at]), int(xs_[at])
        path = [(y, x)]
        while dist[y, x] > 0:                          # back down the wavefront to the joined pieces
            y, x = min(((y + dy, x + dx) for dy in (-1, 0, 1) for dx in (-1, 0, 1)
                        if 0 <= y + dy < h and 0 <= x + dx < w and dist[y + dy, x + dx] >= 0),
                       key=lambda c: (dist[c], abs(c[0] - y) + abs(c[1] - x)))
            path.append((y, x))
        r = NECK / 2 * PX_PER_M
        for cy, cx in path:
            px, py = (cx + 0.5) * step, (cy + 0.5) * step
            d.ellipse([px - r, py - r, px + r, py + r], fill=255)
        joined.add(p)
    return np.isin(pieces, list(joined)) | ((np.asarray(paint) > 0) & gap)


def details(frame, shape, rs):
    """Stair treads (every 0.3 m across the flight's long side) and lift crosses, as a mask."""
    import cv2
    im = Image.new("L", (shape[1], shape[0]), 0)
    d = ImageDraw.Draw(im)
    for r in rs:
        if r["kind"] not in ("stairs", "elevator"):
            continue
        box = cv2.boxPoints(cv2.minAreaRect(frame.raster(r["points"]).astype(np.float32)))
        a, b, c, e = box
        if r["kind"] == "elevator":
            d.line([tuple(a), tuple(c)], fill=255, width=3)
            d.line([tuple(b), tuple(e)], fill=255, width=3)
            continue
        if np.linalg.norm(b - a) < np.linalg.norm(e - a):     # a-b the short side: treads parallel to it
            a, b, c, e = b, c, e, a
        long_side = np.linalg.norm(e - a)
        steps = int(long_side / (0.3 * PX_PER_M))
        for k in range(1, steps):
            t = k / steps
            d.line([tuple(a + (e - a) * t), tuple(b + (c - b) * t)], fill=255, width=2)
    return np.asarray(im) > 0


# --- the fit to OSM (python hec.py --fit) ---

def fit():
    """Fits the source outline to OSM's outline of HEC (best overlap over scale, turn and shift) and prints FIT."""
    from scipy.optimize import minimize
    from shapely.geometry import Polygon
    el = next(e for e in load(OSM)["elements"] if e.get("id") == OSM_WAY)
    ll = np.array([(p["lat"], p["lon"]) for p in el["geometry"]])
    lat0, lon0 = round(ll[:, 0].mean(), 7), round(ll[:, 1].mean(), 7)
    rad = math.pi / 180
    target = Polygon(np.stack([(ll[:, 1] - lon0) * rad * EARTH_RADIUS * math.cos(lat0 * rad),
                               (ll[:, 0] - lat0) * rad * EARTH_RADIUS], 1)).buffer(0)
    out = np.asarray(source()["outline"], float)
    q = np.stack([out[:, 0], -out[:, 1]], 1)
    drawn = Polygon(q).buffer(0)
    s0 = math.sqrt(target.area / drawn.area)

    def place(v):
        s, a, e, n = v
        c, si = math.cos(a), math.sin(a)
        return np.stack([q[:, 0] * c - q[:, 1] * si, q[:, 0] * si + q[:, 1] * c], 1) * s + [e, n]

    def cost(v):
        p = Polygon(place(v)).buffer(0)
        return 1 - p.intersection(target).area / p.union(target).area

    best = None
    for a0 in np.radians(np.arange(0, 360, 15)):
        c, si = math.cos(a0), math.sin(a0)
        cen = np.array(drawn.centroid.coords[0])
        e0, n0 = np.array(target.centroid.coords[0]) - s0 * np.array([cen[0] * c - cen[1] * si, cen[0] * si + cen[1] * c])
        r = minimize(cost, [s0, a0, e0, n0], method="Nelder-Mead",
                     options={"xatol": 1e-5, "fatol": 1e-7, "maxiter": 4000})
        if best is None or r.fun < best.fun:
            best = r
    s, a, e, n = best.x
    print(f"overlap {1 - best.fun:.3f}")
    print(f'FIT = {{"lat": {lat0}, "lon": {lon0}, "metersPerPixel": {s:.7f}, "angle": {math.degrees(a) % 360:.5f},\n'
          f'       "east": {e:.4f}, "north": {n:.4f}}}')


if __name__ == "__main__":
    if "--fit" in sys.argv:
        fit()
    else:
        print(__doc__)
