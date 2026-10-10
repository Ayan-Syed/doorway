"""Draws the filled indoor map of BA1 floor 1 in each colour theme, from the rooms segment.py found:
rooms filled by what they are and outlined, the building outlined heavier, casting a soft shadow on the
ground. The app draws room numbers and icons on top, so they stay upright as the map turns.

Run after graph.py --export and segment.py: python basemap.py [theme ...]
The labels, place grid, UI textures and icons are rebuilt every time, whatever themes are named.
places.bytes (little-endian): "DWPL", uint16 version (1), uint16 width, uint16 height, uint32 FNV-1a hash of the
place ids (graph.place_id_hash), then a byte per 5 x 5 px cell, top row first: the place's index + 1, or 0.
"""
import json
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

import graph
import icons

# Kept in step with Theme.cs. ("ground" is never drawn: the outside is transparent and the app paints
# its own ground or the campus map behind.)
THEMES = {
    "campus": {"ground": (232, 238, 228), "shadow": 0.13, "walkway": (255, 255, 255), "room": (242, 239, 233),
               "teaching": (252, 238, 212), "restroom": (229, 238, 250), "stairs": (236, 231, 245),
               "elevator": (236, 231, 245), "wall": (206, 200, 190), "outline": (150, 143, 133),
               "detail": (186, 180, 170)},
    "paper": {"ground": (234, 226, 209), "shadow": 0.10, "walkway": (251, 247, 238), "room": (243, 236, 221),
              "teaching": (245, 222, 192), "restroom": (221, 231, 224), "stairs": (236, 225, 214),
              "elevator": (236, 225, 214), "wall": (140, 126, 110), "outline": (58, 50, 43),
              "detail": (160, 146, 130)},
    "knight": {"ground": (16, 16, 16), "shadow": 0.0, "walkway": (46, 46, 45), "room": (31, 31, 30),
               "teaching": (50, 45, 33), "restroom": (30, 36, 45), "stairs": (42, 36, 47),
               "elevator": (42, 36, 47), "wall": (16, 16, 16), "outline": (92, 88, 80),
               "detail": (84, 80, 74)},
    # Iris and dusk: the 424874 / DCD6F7 / A6B1E1 / CACFD6 / D6E5E3 palette, by day and by night.
    "iris": {"ground": (214, 229, 227), "shadow": 0.12, "walkway": (255, 255, 255), "room": (238, 240, 246),
             "teaching": (228, 223, 249), "restroom": (221, 235, 233), "stairs": (226, 230, 246),
             "elevator": (226, 230, 246), "wall": (190, 195, 210), "outline": (108, 113, 150),
             "detail": (168, 173, 192)},
    "dusk": {"ground": (21, 23, 44), "shadow": 0.0, "walkway": (54, 58, 96), "room": (36, 39, 70),
             "teaching": (54, 49, 92), "restroom": (33, 52, 62), "stairs": (45, 48, 86),
             "elevator": (45, 48, 86), "wall": (21, 23, 44), "outline": (112, 118, 172),
             "detail": (88, 94, 140)},
}
KINDS = ["outside", "walkway", "room", "teaching", "restroom", "stairs", "elevator"]
WALL_WIDTH, OUTLINE_WIDTH = 4.5, 9.0     # plan pixels (3000 across): 15 cm and 31 cm


def line(boundary, width):
    """Anti-aliased coverage of lines `width` wide along the marked pixels."""
    d = ndimage.distance_transform_edt(~boundary)
    return np.clip(width / 2 + 0.5 - d, 0, 1)[..., None]


def smooth(R, sigma=3.0):
    """Each pixel goes to the region most of its neighbourhood is in: rounds off the stair-step and
    wobble along the edges the segmentation left."""
    best = np.zeros(R.shape, np.float32)
    out = R.copy()
    for i, sl in enumerate(ndimage.find_objects(R), 1):
        if sl is None:
            continue
        sl = tuple(slice(max(s.start - 12, 0), s.stop + 12) for s in sl)     # bbox + 12 px pad
        f = ndimage.gaussian_filter((R[sl] == i).astype(np.float32), sigma)
        better = f > best[sl]
        best[sl][better] = f[better]
        out[sl][better] = i
    return out


def boundaries(R, outside):
    """(change, edge_out): where a pixel's region differs from its left or upper neighbour's, between
    two inside regions, and between the building and the outside. Marked on the second pixel."""
    change = np.zeros(R.shape, bool)
    edge_out = np.zeros(R.shape, bool)
    for a, b, oa, ob, dst in ((R[:, :-1], R[:, 1:], outside[:, :-1], outside[:, 1:], (slice(None), slice(1, None))),
                              (R[:-1, :], R[1:, :], outside[:-1, :], outside[1:, :], (slice(1, None), slice(None)))):
        diff = a != b
        change[dst] |= diff & ~oa & ~ob
        edge_out[dst] |= diff & (oa != ob)
    return change, edge_out


def render(theme, R, kind, dark):
    """The building on a transparent ground, so the campus map shows round it: colours premultiplied by
    coverage while drawing, straightened at the end."""
    t = THEMES[theme]
    palette = np.array([t["ground" if k == "outside" else k] for k in KINDS], np.float32)
    K = kind[R]
    im = palette[K]
    outside = K == 0
    alpha = (~outside).astype(np.float32)
    im[outside] = 0
    if t["shadow"] > 0:
        # Soft shadow of the building, down and to the right.
        shade = ndimage.gaussian_filter(np.roll(~outside, (10, 6), axis=(0, 1)).astype(np.float32), 7)
        alpha[outside] = shade[outside] * t["shadow"] * 1.6
    # Stair treads and elevator crosses from the drawing, faint.
    detail = dark & ((K == 5) | (K == 6))
    im[detail] = im[detail] * 0.3 + np.array(t["detail"], np.float32) * 0.7
    change, edge_out = boundaries(R, outside)
    for mask, width, color in ((change, WALL_WIDTH, t["wall"]), (edge_out, OUTLINE_WIDTH, t["outline"])):
        a = line(mask, width)
        im = im * (1 - a) + np.array(color, np.float32) * a
        alpha = alpha * (1 - a[..., 0]) + a[..., 0]
    rgb = np.where(alpha[..., None] > 1e-4, im / np.maximum(alpha, 1e-4)[..., None], 0)
    rgba = np.concatenate([rgb, alpha[..., None] * 255], axis=2)
    return Image.fromarray(np.clip(rgba + 0.5, 0, 255).astype(np.uint8), "RGBA")


def places_and_labels(R, ids, texts, kinds):
    """Room numbers go where there's most room in each room, biggest rooms first; hallways, stairs, lifts
    and restrooms get none (the app gives the last three an icon). The place map holds, every 5 plan
    pixels, the index + 1 of the place there, or 0: a room by its number, the others by the region
    their door opens into."""
    with open(graph.need(graph.PLAN_JSON, "run graph.py --export first (or make_data.py)"), encoding="utf-8") as f:
        plan = json.load(f)
    index = {p["id"]: n + 1 for n, p in enumerate(plan["places"])}
    if len(index) > 255:
        sys.exit("places.bytes stores place index + 1 as a byte: at most 255 places per floor")
    owner = np.zeros(R.max() + 1, np.uint8)
    region_kind = {}
    for i, text, k in zip(ids, texts, kinds):
        i, text, k = int(i), str(text), str(k)
        region_kind[i] = k
        if text in index:
            owner[i] = index[text]

    labels = []
    objs = ndimage.find_objects(R)
    for i, text, k in zip(ids, texts, kinds):
        i, text, k = int(i), str(text), str(k)
        if not text or k not in ("room", "teaching") or i > len(objs) or objs[i - 1] is None:
            continue
        sl = objs[i - 1]
        m = R[sl] == i
        d = ndimage.distance_transform_edt(np.pad(m, 1))[1:-1, 1:-1]     # the box edge counts as a wall
        y, x = np.unravel_index(np.argmax(d), d.shape)
        lx, lz = graph.to_m(sl[1].start + x, sl[0].start + y)
        labels.append({"text": text, "x": lx, "z": lz, "area": round(m.sum() / graph.PX_PER_M ** 2, 1)})

    # The first region of its kind up to ~4 m in through the door.
    kind_of = np.array([region_kind.get(i, "room") for i in range(R.max() + 1)])
    for pid, _, _, kind, doors in graph.P:
        if kind not in ("restroom", "elevator", "stairs"):
            continue
        for _, (x, y), (dx, dy) in doors:
            for step in range(10, 111, 10):
                r = R[int(round(y + dy * step)), int(round(x + dx * step))]
                if kind_of[r] == kind:
                    owner[r] = owner[r] or index[pid]
                    break
            else:
                print("no room behind", pid)

    labels.sort(key=lambda l: -l["area"])
    plan["labels"] = labels
    with open(graph.PLAN_JSON, "w", encoding="utf-8") as f:
        json.dump(plan, f, ensure_ascii=False, indent=1)

    small = owner[R[2::5, 2::5]]                          # the middle of each 5x5 cell
    h, w = small.shape
    with open(os.path.join(graph.DATA_DIR, "ba1-floor1-places.bytes"), "wb") as f:
        # Header: magic, version, size, and a hash of the place ids in order, since a cell holds an index into them.
        f.write(struct.pack("<4sHHHI", graph.PLACES_MAGIC, graph.FORMAT_VERSION, w, h,
                            graph.place_id_hash([p["id"] for p in plan["places"]])))
        f.write(np.ascontiguousarray(small).tobytes())
    print(len(labels), "labels,", len(set(small[small > 0].tolist())), "places on the place map", (w, h))


def ui_textures():
    S = 4
    os.makedirs(graph.UI_DIR, exist_ok=True)
    # Route ribbon mask, scrolled along v by the route shader: red is the band (see-through in the middle,
    # solid at the edges), green a chevron pointing forward (+v). The shader colours each.
    n = 256 * S
    band = Image.new("L", (n, n), 0)
    d = ImageDraw.Draw(band)
    d.rectangle([0.04 * n, 0, 0.96 * n, n], fill=90)
    d.rectangle([0.03 * n, 0, 0.09 * n, n], fill=255)
    d.rectangle([0.91 * n, 0, 0.97 * n, n], fill=255)
    chevron = Image.new("L", (n, n), 0)
    d = ImageDraw.Draw(chevron)
    pts = [(0.27 * n, 0.64 * n), (0.5 * n, 0.38 * n), (0.73 * n, 0.64 * n)]
    d.line(pts, fill=255, width=int(0.10 * n), joint="curve")
    r = 0.05 * n
    for x, y in (pts[0], pts[-1]):                          # round caps
        d.ellipse([x - r, y - r, x + r, y + r], fill=255)
    zero, full = Image.new("L", (n, n), 0), Image.new("L", (n, n), 255)
    mask = Image.merge("RGBA", (band, chevron, zero, full)).resize((256, 256), Image.LANCZOS)
    mask.save(os.path.join(graph.UI_DIR, "RouteMask.png"))

    # Heading beam for the you-are-here dot: a soft wedge widening upward, fading out.
    n = 256
    yy, xx = np.mgrid[0:n, 0:n]
    dx, dy = xx + 0.5 - n / 2, n / 2 - (yy + 0.5)          # +dy is up
    r = np.hypot(dx, dy) / (n / 2)                          # 0 at the centre, 1 at the edge midpoints
    ang = np.degrees(np.abs(np.arctan2(dx, dy)))           # 0 straight up, 180 straight down
    a = np.clip((32 - ang) / 10, 0, 1) * np.clip(1 - r, 0, 1) ** 1.5 * np.clip(r / 0.15, 0, 1)
    graph.white(a).save(os.path.join(graph.UI_DIR, "Cone.png"))


def main():
    themes = sys.argv[1:] or list(THEMES)
    for theme in themes:
        if theme not in THEMES:
            sys.exit(f"Unknown theme {theme}: pick from {', '.join(THEMES)}")
    with np.load(graph.need(graph.REGIONS, "run segment.py first (or make_data.py)")) as d:
        R, dark = smooth(d["regions"]), d["dark"]
        ids, texts, kinds = d["ids"], d["texts"], d["kinds"]
    kind = np.zeros(R.max() + 1, np.int32)
    for i, k in zip(ids, kinds):
        kind[i] = KINDS.index(str(k)) if str(k) in KINDS else 2
    places_and_labels(R, ids, texts, kinds)
    ui_textures()
    icons.app_icons()
    for theme in themes:
        # Full plan resolution, so it stays sharp zoomed in; the app places it by the plan's extent, not its size.
        render(theme, R, kind, dark).save(os.path.join(graph.DATA_DIR, f"ba1-floor1-map-{theme}.png"), optimize=True)
        print(theme)


if __name__ == "__main__":
    main()
