"""The user's markers besides the dot (Settings > Your marker): a diamond, a four-point spark, a hexagon and a
shield, in the iris palette (424874 indigo, A6B1E1 periwinkle, DCD6F7 lavender). Each has a white rim and a soft
shadow, so it stands out on the light map and the dark one alike; the app draws them as they are, by day and night.

Run alone with python markers.py (basemap.py runs it too) -> Assets/Navigation/UI/Markers/marker-*.png
"""
import math
import os

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

import graph

INDIGO = (66, 72, 116)
DEEP = (42, 46, 82)
PERIWINKLE = (166, 177, 225)
LAVENDER = (220, 214, 247)
WHITE = (255, 255, 255)

N = 1024              # drawn this big, then shrunk to SIZE, for smooth edges
SIZE = 256
RIM = 0.07            # the white rim, in half-widths
ROUND = 0.03          # corners rounded by this much

MARKERS = ["diamond", "spark", "hex", "shield"]


def mask(points):
    """A polygon given in -1..1 (y down), filled, its corners rounded off: a bool array N x N."""
    im = Image.new("L", (N, N), 0)
    ImageDraw.Draw(im).polygon([((x + 1) * N / 2, (y + 1) * N / 2) for x, y in points], fill=255)
    m = np.asarray(im) > 127
    r = ROUND * N / 2
    eroded = ndimage.distance_transform_edt(m) > r          # in by r, then out by r: the corners come out round
    return ndimage.distance_transform_edt(~eroded) <= r


def ring(r, sides):
    """Points on a regular polygon of `sides` corners, radius r, the first straight up."""
    return [(r * math.sin(2 * math.pi * k / sides), -r * math.cos(2 * math.pi * k / sides)) for k in range(sides)]


def star(outer, inner, points=4):
    out = []
    for k in range(points * 2):
        r = outer if k % 2 == 0 else inner
        a = math.pi * k / points
        out.append((r * math.sin(a), -r * math.cos(a)))
    return out


def shield(scale=1.0):
    """A heater shield: straight top and sides, curving to a point at the bottom."""
    top, side, waist = -0.74 * scale, 0.68 * scale, -0.08 * scale
    pts = [(-side, top), (side, top), (side, waist)]
    for i in range(1, 17):                       # quadratic curve from the right side to the point
        t = i / 16
        x = (1 - t) ** 2 * side + 2 * (1 - t) * t * side * 0.55 + t * t * 0
        y = (1 - t) ** 2 * waist + 2 * (1 - t) * t * 0.62 * scale + t * t * 0.86 * scale
        pts.append((x, y))
    return pts + [(-x, y) for x, y in reversed(pts[3:-1])] + [(-side, waist)]


def gradient(top, bottom):
    """top-left to bottom-right, as the app icon's tile."""
    yy, xx = np.mgrid[0:N, 0:N] / (N - 1)
    t = np.clip((xx + yy) / 2, 0, 1)[..., None]
    return np.array(top, float) * (1 - t) + np.array(bottom, float) * t


def compose(body, paint):
    """The marker: a soft indigo shadow, the white rim round the body, and the body painted by `paint`
    (rgb N x N x 3 and the body mask -> rgb)."""
    outside = ndimage.distance_transform_edt(~body)
    rim = outside <= RIM * N / 2
    rgba = np.zeros((N, N, 4))
    shadow = ndimage.gaussian_filter(np.roll(rim.astype(float), int(0.035 * N), axis=0), 0.03 * N) * 0.32
    rgba[..., :3] = DEEP
    rgba[..., 3] = shadow
    rgba[rim, :3] = WHITE
    rgba[rim, 3] = 1.0
    rgb = paint(np.zeros((N, N, 3)) + WHITE, body)
    rgba[body, :3] = rgb[body]
    rgba[..., 3] *= 255
    im = Image.fromarray(np.clip(rgba, 0, 255).astype(np.uint8), "RGBA")
    return im.resize((SIZE, SIZE), Image.LANCZOS)


def inset(m, width):
    """The part of mask m more than `width` (half-widths) in from its edge."""
    return ndimage.distance_transform_edt(m) > width * N / 2


def diamond():
    body = mask([(0, -0.82), (0.82, 0), (0, 0.82), (-0.82, 0)])
    gem = mask([(0, -0.36), (0.36, 0), (0, 0.36), (-0.36, 0)])

    def paint(rgb, b):
        rgb[:] = gradient(PERIWINKLE, INDIGO)
        rgb[gem] = LAVENDER
        return rgb
    return compose(body, paint)


def spark():
    body = mask(star(0.88, 0.3))
    core = inset(body, 0.09)
    dot = mask(ring(0.13, 24))

    def paint(rgb, b):
        rgb[:] = INDIGO
        rgb[core] = LAVENDER
        rgb[dot] = INDIGO
        return rgb
    return compose(body, paint)


def hexagon():
    body = mask(ring(0.84, 6))
    inner = mask(ring(0.44, 6))

    def paint(rgb, b):
        rgb[:] = gradient(INDIGO, DEEP)
        rgb[inner] = PERIWINKLE
        return rgb
    return compose(body, paint)


def crest():
    body = mask(shield())
    chevron = mask([(-0.46, -0.36), (0, 0.06), (0.46, -0.36), (0.46, -0.12), (0, 0.3), (-0.46, -0.12)]) & inset(body, 0.1)

    def paint(rgb, b):
        rgb[:] = gradient(INDIGO, DEEP)
        rgb[chevron] = LAVENDER
        return rgb
    return compose(body, paint)


def marker_sprites():
    folder = os.path.join(graph.UI_DIR, "Markers")
    os.makedirs(folder, exist_ok=True)
    for name, draw in zip(MARKERS, (diamond, spark, hexagon, crest)):
        draw().save(os.path.join(folder, f"marker-{name}.png"), optimize=True)
        print("marker", name)


if __name__ == "__main__":
    marker_sprites()
