"""The walls of BA1 floor 1 as outlines, for the app to stand up off the floor when the map is tilted.
They are the lines basemap.py draws between rooms (and round the building, thicker), so they sit on
those lines. If you change basemap's line widths or smoothing, re-run this.

Run after segment.py: python walls.py -> Assets/Navigation/Data/ba1-floor1-walls.bytes
The file (little-endian): int32 point count, ring count, index count; int32 points per ring; then each
point as int16 x, z in plan centimetres (x right, z up), rings one after another, each outer ring
followed by its holes; then int32 triangle indices into all the points. The app stands a side up along
each ring and puts the tops on them.
"""
import os
import struct

import cv2
import mapbox_earcut as earcut
import numpy as np
from scipy import ndimage

import basemap
import graph

SIMPLIFY = 1.2        # plan px the outlines may stray to save points
MIN_AREA = 40         # plan px; smaller specks are dropped


def boundaries(R, outside):
    """(inner, outer): pixels whose region differs from the left or upper neighbour's, between two inside
    regions (inner) or between the building and the outside (outer). Marked on the right or lower pixel."""
    out = outside[R]
    inner = np.zeros(R.shape, bool)
    outer = np.zeros(R.shape, bool)
    for a, b, oa, ob, dst in ((R[:, :-1], R[:, 1:], out[:, :-1], out[:, 1:], (slice(None), slice(1, None))),
                              (R[:-1, :], R[1:, :], out[:-1, :], out[1:, :], (slice(1, None), slice(None)))):
        diff = a != b
        edge = diff & (oa != ob)
        outer[dst] |= edge
        inner[dst] |= diff & ~edge
    return inner, outer


def main():
    with np.load(graph.need(graph.REGIONS, "run segment.py first (or make_data.py)")) as d:
        R = basemap.smooth(d["regions"])          # the same smoothing as the pictures
        outside = np.zeros(R.max() + 1, bool)
        for i, k in zip(d["ids"], d["kinds"]):
            outside[int(i)] = str(k) == "outside"
    inner, outer = boundaries(R, outside)
    # The wall bands as solid pixels, at the widths basemap.py draws its lines.
    solid = ((ndimage.distance_transform_edt(~inner) <= basemap.WALL_WIDTH / 2)
             | (ndimage.distance_transform_edt(~outer) <= basemap.OUTLINE_WIDTH / 2))
    contours, hierarchy = cv2.findContours(solid.astype(np.uint8), cv2.RETR_CCOMP, cv2.CHAIN_APPROX_NONE)
    hierarchy = hierarchy[0]                       # [next, previous, first child, parent]
    simple = [cv2.approxPolyDP(c, SIMPLIFY, True).reshape(-1, 2) for c in contours]

    def keep(k):
        return len(simple[k]) >= 3 and cv2.contourArea(simple[k]) >= MIN_AREA

    rings, tris, base = [], [], 0
    for k in range(len(contours)):
        if hierarchy[k][3] != -1 or not keep(k):
            continue
        group = [simple[k]]
        child = hierarchy[k][2]
        while child != -1:                         # its holes
            if keep(child):
                group.append(simple[child])
            child = hierarchy[child][0]
        pts = np.concatenate(group).astype(np.float32)
        ends = np.cumsum([len(g) for g in group]).astype(np.uint32)
        tris.append(earcut.triangulate_float32(pts, ends).astype(np.int64) + base)
        rings += group
        base += len(pts)

    pts = np.concatenate(rings).astype(np.float64)
    cm = np.empty_like(pts)
    cm[:, 0] = (pts[:, 0] - graph.ORIGIN[0]) / graph.PX_PER_M * 100
    cm[:, 1] = -(pts[:, 1] - graph.ORIGIN[1]) / graph.PX_PER_M * 100
    cm = np.round(cm)
    assert np.abs(cm).max() < 32767, "floor too big for int16 centimetres"
    index = np.concatenate(tris)
    with open(os.path.join(graph.DATA_DIR, "ba1-floor1-walls.bytes"), "wb") as f:
        f.write(struct.pack("<iii", len(pts), len(rings), len(index)))
        f.write(np.array([len(r) for r in rings], "<i4").tobytes())
        f.write(cm.astype("<i2").tobytes())
        f.write(index.astype("<i4").tobytes())
    print(len(rings), "outlines,", len(pts), "points,", len(index) // 3, "triangles")


if __name__ == "__main__":
    main()
