"""Door directions, door positions and room-number signs, measured on the drawing.

A door's direction is what the app lines the whole plan up with (Navigator.AlignTo), so it is measured, not
typed: the wall's own orientation round the door (the ink's gradient, averaged over orientations within 40
degrees of the rough direction, so the side walls don't count), snapped onto the plan's 45-degree wall grid when
it is within SNAP degrees of it. All in plan pixels (y down) unless a name says metres.
"""
import math
import re

import numpy as np
from scipy import ndimage

import graph

SNAP = 15.0           # degrees: a measured normal this close to a multiple of 45 is snapped onto it
WINDOW = 40           # px round the door that the wall's orientation is measured over (~1.4 m)
SIGMA = 18.0          # px, the weight's fall-off inside the window
REACH = 150           # px a door spot may be from its door (~5 m)
SIGN_OFFSET = 0.75    # metres from the door's middle to its plate along the wall (half a door, a hand's width more)
ROOM_NUMBER = re.compile(r"^\d{3}[A-Z]?$")
WALKWAY, OUTSIDE = 2, 1


class Ink:
    """The drawing's walls, ready for measuring: the dark mask and its smoothed gradient."""

    def __init__(self, dark):
        self.dark = dark
        g = ndimage.gaussian_filter(dark.astype(np.float32), 1.5)
        self.gx = ndimage.sobel(g, 1)
        self.gy = ndimage.sobel(g, 0)
        self.clearance = ndimage.distance_transform_edt(~dark)      # px to the nearest ink

    def wall_at(self, x, y):
        h, w = self.dark.shape
        xi, yi = int(round(x)), int(round(y))
        return 0 <= xi < w and 0 <= yi < h and bool(self.dark[yi, xi])


def snap(angle):
    """(angle, snapped): a direction in degrees moved onto the nearest multiple of 45 if within SNAP."""
    k = round(angle / 45.0) * 45.0
    if abs(angle - k) <= SNAP:
        return k % 360.0, True
    return angle % 360.0, False


def hit(R, ink, x, y, dx, dy):
    """Where the line from a door spot toward its door first meets a wall or leaves the spot's own region:
    (x, y) in px, or None within REACH."""
    h, w = R.shape
    start = R[int(round(y)), int(round(x))]
    for s in range(1, REACH):
        xx, yy = x + dx * s, y + dy * s
        xi, yi = int(round(xx)), int(round(yy))
        if not (0 <= xi < w and 0 <= yi < h):
            return None
        if ink.wall_at(xx, yy) or R[yi, xi] != start:
            return xx, yy
    return None


def wall_normal(ink, x, y, dx, dy):
    """The normal of the wall at (x, y) on the side (dx, dy) points to, snapped: ((nx, ny), measured degrees,
    snapped). None where there's no ink nearby to measure."""
    h, w = ink.dark.shape
    x0, x1 = max(int(x) - WINDOW, 0), min(int(x) + WINDOW + 1, w)
    y0, y1 = max(int(y) - WINDOW, 0), min(int(y) + WINDOW + 1, h)
    gx, gy = ink.gx[y0:y1, x0:x1], ink.gy[y0:y1, x0:x1]
    yy, xx = np.mgrid[y0:y1, x0:x1]
    r2 = (xx - x) ** 2 + (yy - y) ** 2
    weight = np.hypot(gx, gy) * np.exp(-r2 / (2 * SIGMA ** 2)) * (r2 <= WINDOW ** 2)
    phi = np.arctan2(gy, gx)
    rough = math.atan2(dy, dx)
    # Orientations, not directions: a wall's two faces have opposite gradients, so compare and average doubled.
    diff = np.angle(np.exp(2j * (phi - rough))) / 2
    weight = weight * (np.abs(diff) < math.radians(40))
    if weight.sum() < 1e-3:
        return None
    mean = np.angle(np.sum(weight * np.exp(2j * phi))) / 2
    nx, ny = math.cos(mean), math.sin(mean)
    if nx * dx + ny * dy < 0:
        nx, ny = -nx, -ny
    measured = math.degrees(math.atan2(ny, nx)) % 360.0
    angle, snapped = snap(measured)
    a = math.radians(angle)
    return (math.cos(a), math.sin(a)), measured, snapped


def fit_direction(R, ink, x, y, dx, dy):
    """A door spot's direction toward its door, measured on the wall the rough direction (dx, dy) points at.
    Returns ((dx, dy), the wall point it was measured at or None, measured degrees or None, snapped).
    Where there's no wall to measure, or what's in front is off the grid (a desk drawn askew in an open
    reception, say), the rough direction stays, snapped onto the grid if it's near it."""
    h = math.hypot(dx, dy) or 1.0
    dx, dy = dx / h, dy / h
    where = hit(R, ink, x, y, dx, dy)
    fitted = wall_normal(ink, where[0], where[1], dx, dy) if where else None
    if fitted is None or not fitted[2]:
        angle, _ = snap(math.degrees(math.atan2(dy, dx)) % 360.0)
        a = math.radians(angle)
        rough = (math.cos(a), math.sin(a)) if abs(math.cos(a) * dx + math.sin(a) * dy - 1) < 0.05 else (dx, dy)
        return rough, where, fitted[1] if fitted else None, False
    n, measured, snapped = fitted
    # Measured again along the new direction, so the wall point is the one the spot really faces.
    again = hit(R, ink, x, y, n[0], n[1])
    return n, again or where, measured, snapped


def number_behind(R, names, x, y, dx, dy, kind):
    """The printed number of the first region of this kind through a door (restrooms, stairs, lifts), or None."""
    h, w = R.shape
    for step in range(5, 121, 5):
        xi, yi = int(round(x + dx * step)), int(round(y + dy * step))
        if not (0 <= xi < w and 0 <= yi < h):
            return None
        text, k = names.get(int(R[yi, xi]), ("", ""))
        if k == kind and ROOM_NUMBER.match(text):
            return text
    return None


def sign_spot(R, ink, door_point, n):
    """The plate beside a door: on the hallway face of the wall, SIGN_OFFSET along it, on whichever side of the
    door the wall carries on with hallway in front of it (the right, facing the door, if both). (x, y) in px."""
    nx, ny = n
    right = (-ny, nx)                  # facing (nx, ny) on the page (y down), this is the right hand
    reach = SIGN_OFFSET * graph.PX_PER_M
    front = 0.5 * graph.PX_PER_M       # where someone reading it stands, with 0.3 m clear round them
    clear = 0.3 * graph.PX_PER_M
    h, w = R.shape
    for side in (1, -1):
        tx, ty = right[0] * side, right[1] * side
        px, py = door_point[0] + tx * reach, door_point[1] + ty * reach
        # Find the wall here (within ~0.5 m either way across it), then step back out of it to its hallway face.
        for s in range(-15, 16):
            if ink.wall_at(px + nx * s, py + ny * s):
                s0 = s
                while ink.wall_at(px + nx * s0, py + ny * s0) and s0 > -40:
                    s0 -= 1
                fx, fy = px + nx * s0, py + ny * s0
                ax, ay = int(round(fx - nx * front)), int(round(fy - ny * front))
                if 0 <= ax < w and 0 <= ay < h and R[ay, ax] == WALKWAY and ink.clearance[ay, ax] >= clear:
                    return fx, fy
                break
    return door_point


def signs_for(places, nodes, R, names, ink):
    """One plate per door of every place with a printed number of its own (not the suites' borrowed doors, not
    exits). Rooms use their own number; restrooms, stairs and lifts the number of the room behind the door."""
    out = []
    for pid, _, detail, kind, doors in places:
        if kind == "exit" or detail.startswith("Inside"):
            continue
        for node, _, (dx, dy) in doors:
            x, y = nodes[node]
            if kind == "room":
                text = pid if ROOM_NUMBER.match(pid) else None
            else:
                text = number_behind(R, names, x, y, dx, dy, kind)
            if not text:
                continue
            where = hit(R, ink, x, y, dx, dy)
            if where is None:
                continue
            sx, sy = sign_spot(R, ink, where, (dx, dy))
            mx, mz = graph.to_m(sx, sy)
            out.append({"text": text, "x": mx, "z": mz, "nx": round(-dx, 4), "nz": round(dy, 4),
                        "door": node, "place": pid})
    return out
