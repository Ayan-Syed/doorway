"""Splits BA1 floor 1 into rooms, hallways and outside, for the filled indoor map.

Seeded watershed on the distance from the walls of the labeled plan: each printed room number seeds
its room, the hand-made walkway graph's links seed the hallways all along their length (so a side
passage, like the one to 148's door, doesn't flood as the room beside it), the image border seeds the
outside.
Door openings are narrow, so the floods from either side meet in them. Closed-off spaces with no
number (closets, shafts) get a seed of their own.
Run from anywhere: python segment.py -> regions.npz, check/segment_check.png
"""
import math
import os

import numpy as np
from PIL import Image
from scipy import ndimage

import graph

# Hallways, the atrium, the lobby and the entrance vestibules: drawn as walkway.
CIRCULATION = {"101", "102", "128", "138", "197", "189", "190", "191", "192", "193", "194", "195", "196", "199"}
RESTROOMS = {"123", "123A", "125", "125A", "140", "141"}
STAIRS = {"127", "129", "120", "118"}
ELEVATORS = {"143", "144", "107A"}
# Rooms people come to: lecture halls, classrooms, the events space.
TEACHING = {"107", "119", "121", "122", "126", "110", "113", "146", "147", "148", "148A"}
OUTSIDE, WALKWAY = 1, 2          # fixed region ids; real rooms start at 3


def kind_of(text):
    for kind, names in (("walkway", CIRCULATION), ("restroom", RESTROOMS), ("stairs", STAIRS),
                        ("elevator", ELEVATORS), ("teaching", TEACHING)):
        if text in names:
            return kind
    return "room"


def ink_without_labels(labels):
    """The drawing's ink with the printed numbers taken out, so they don't act as walls."""
    gray = graph.plan_gray()
    dark = gray < 170
    strokes, _ = graph.label_text(gray, labels)
    dark &= ~strokes
    return dark, gray


def segment():
    labels = graph.load_labels()
    dark, _ = ink_without_labels(labels)
    wall = ndimage.binary_dilation(dark, iterations=1)        # 1 px fatter, so diagonal gaps close
    # The building's footprint: the walls closed over gaps up to ~2.4 m (entrances), holes filled.
    near = ndimage.distance_transform_edt(~dark) <= 35
    footprint = ndimage.binary_fill_holes(ndimage.distance_transform_edt(near) > 35)
    free = ~wall & footprint
    dist = ndimage.distance_transform_edt(free)                # clearance from the walls, px
    h, w = dark.shape

    names = {OUTSIDE: ("outside", "outside"), WALKWAY: ("walkway", "walkway")}
    # Along every hand-made link, not just at its points, so a narrow side passage (148's, off the diagonal hall)
    # floods as hallway rather than as the room beside it. Links out through the entrances are left out.
    nodes, edges = graph.build(auto=False)
    exits = {n for _, _, _, kind, ds in graph.P if kind == "exit" for n, _, _ in ds}
    seeds = []
    for a, b in edges:
        if a in exits or b in exits:
            continue
        (ax, ay), (bx, by) = nodes[a], nodes[b]
        n = max(int(math.hypot(bx - ax, by - ay) / 6), 1)
        seeds += [(ax + (bx - ax) * k / n, ay + (by - ay) * k / n, WALKWAY) for k in range(n + 1)]
    nid = 3
    for lab in labels:
        kind = kind_of(lab["text"])
        if kind == "walkway":
            seeds.append((lab["x"], lab["y"], WALKWAY))
        else:
            names[nid] = (lab["text"], kind)
            seeds.append((lab["x"], lab["y"], nid))
            nid += 1

    L = np.zeros((h, w), np.int32)
    L[~footprint] = OUTSIDE
    r = 5
    oy, ox = np.mgrid[-r:r + 1, -r:r + 1]
    disc = ox * ox + oy * oy <= r * r
    for x, y, i in seeds:                                      # later seeds overwrite earlier ones
        cx, cy = int(round(x)), int(round(y))
        if not (0 <= cx < w and 0 <= cy < h) or not footprint[cy, cx]:
            continue
        ys, xs = oy[disc] + cy, ox[disc] + cx
        ok = (ys >= 0) & (ys < h) & (xs >= 0) & (xs < w)
        ys, xs = ys[ok], xs[ok]
        ok = free[ys, xs]
        L[ys[ok], xs[ok]] = i

    # Closets, shafts: open space (more than ~0.5 m from any wall, so doorways count as closed)
    # that no seed reached gets a region of its own.
    comp, n = ndimage.label(dist > 15)
    seeded = np.zeros(n + 1, bool)
    seeded[np.unique(comp[(L > OUTSIDE) & (comp > 0)])] = True
    sizes = np.bincount(comp.ravel(), minlength=n + 1)
    for c in range(1, n + 1):
        if not seeded[c] and sizes[c] >= 400:
            names[nid] = ("", "room")
            L[comp == c] = nid
            nid += 1

    # Flood from the most open space toward the walls, 1 px of clearance at a time: at each step, a
    # connected patch of space with one region in it becomes that region; one with several is split by
    # whichever is nearest. Rooms fill from their middles, and neighbours meet in the doorways.
    top = int(dist.max())
    for t in range(top, -1, -1):
        comp, n = ndimage.label(free & (dist >= t))           # 4-connected patches with >= t px clearance
        for c, sl in enumerate(ndimage.find_objects(comp), 1):
            if sl is None:
                continue
            m = comp[sl] == c
            sub = L[sl]                                        # a view: writes go into L
            unl = m & (sub == 0)
            if not unl.any():
                continue
            vals = np.unique(sub[m & (sub > 0)])
            if len(vals) == 1:
                sub[unl] = vals[0]                             # one region here: it takes the whole patch
            elif len(vals) > 1:
                lab = np.where(m, sub, 0)                      # several: each pixel goes to the nearest
                _, (iy, ix) = ndimage.distance_transform_edt(lab == 0, return_indices=True)
                sub[unl] = lab[iy[unl], ix[unl]]

    # Walls and anything unreached take the nearest region: walls belong to a neighbour.
    if (L == 0).any():
        _, (iy, ix) = ndimage.distance_transform_edt(L == 0, return_indices=True)
        L = L[iy, ix]

    # Unnamed pieces against the outside are outside too: entrance canopies, porches.
    objs = ndimage.find_objects(L)
    for i in sorted(names):
        if i <= WALKWAY or names[i][0] or i > len(objs) or objs[i - 1] is None:
            continue
        sy, sx = objs[i - 1]
        sl = (slice(max(sy.start - 2, 0), sy.stop + 2), slice(max(sx.start - 2, 0), sx.stop + 2))
        m = L[sl] == i
        if m.sum() >= 20000:
            continue
        ring = ndimage.binary_dilation(m) & ~m
        if (L[sl][ring] == OUTSIDE).mean() >= 0.45:
            L[sl][m] = OUTSIDE
            del names[i]
    return L, wall, dark, names


def check(L, dark, names):
    colors = {"outside": (225, 236, 220), "walkway": (255, 255, 255), "restroom": (150, 180, 240),
              "stairs": (200, 170, 240), "elevator": (240, 170, 200), "teaching": (245, 215, 160)}
    rng = np.random.default_rng(3)
    pal = np.zeros((L.max() + 1, 3), np.uint8)
    for i in range(len(pal)):
        kind = names[i][1] if i in names else "room"
        pal[i] = colors.get(kind, rng.integers(150, 235, 3))
    im = pal[L]
    im[dark] = (40, 40, 40)
    os.makedirs(graph.CHECK_DIR, exist_ok=True)
    Image.fromarray(im).resize((1500, 1222), Image.LANCZOS).save(os.path.join(graph.CHECK_DIR, "segment_check.png"))


def main():
    L, wall, dark, names = segment()
    ids = sorted(names)
    np.savez_compressed(graph.REGIONS, regions=L, wall=wall, dark=dark, ids=np.array(ids, np.int64),
                        texts=np.array([names[i][0] for i in ids]), kinds=np.array([names[i][1] for i in ids]))
    check(L, dark, names)
    print("regions", len(ids), "unnamed", sum(1 for i in ids if i > WALKWAY and not names[i][0]))


if __name__ == "__main__":
    main()
