"""The app's icon and logo: an arched doorway with light falling from it across the floor, and a heading
arrow walking in, on a tile shaded corner to corner.

Run alone with python icons.py (basemap.py runs it too) -> Assets/Navigation/UI/Icon-*.png, Logo-*.png
"""
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

import graph

# Per theme, kept in step with Theme.cs: the tile's top-left and bottom-right, the doorway, the arrow, the light.
ICONS = {
    "campus": {"top": (66, 133, 244), "bottom": (19, 84, 184), "door": (255, 255, 255),
               "arrow": (25, 103, 210), "light": (255, 255, 255)},
    "paper": {"top": (219, 96, 58), "bottom": (160, 48, 22), "door": (251, 248, 241),
              "arrow": (191, 64, 34), "light": (251, 248, 241)},
    "knight": {"top": (48, 47, 44), "bottom": (10, 10, 10), "door": (255, 201, 4),
               "arrow": (22, 22, 21), "light": (255, 201, 4)},
}
S = 4                 # drawn this many times bigger, then shrunk, for smooth edges
SQUIRCLE = 5          # the tile's superellipse power: between a rounded square and a circle


def tile(N, c):
    """The opaque tile, shaded from top-left to bottom-right."""
    yy, xx = np.mgrid[0:N, 0:N] / (N - 1)
    t = np.clip((xx + yy) / 2, 0, 1)[..., None]
    rgb = np.array(c["top"], np.float64) * (1 - t) + np.array(c["bottom"], np.float64) * t
    rgba = np.concatenate([rgb, np.full((N, N, 1), 255.0)], axis=2)
    return Image.fromarray(np.clip(rgba, 0, 255).astype(np.uint8), "RGBA")


def squircle(N, size=1.0):
    """A superellipse mask `size` of the picture across, with an edge about a pixel wide."""
    v = (np.arange(N) + 0.5) / N - 0.5
    x, y = np.abs(v)[None, :], np.abs(v)[:, None]
    r = (x ** SQUIRCLE + y ** SQUIRCLE) ** (1 / SQUIRCLE) / (size / 2)
    return Image.fromarray((np.clip((1 - r) * N / 2, 0, 1) * 255).astype(np.uint8), "L")


def mark(N, c, scale):
    """The doorway, its light and the arrow, on transparent."""
    def P(x, y):
        # The mark runs from y -0.20 (the arch's top) to 0.30 (the light's far edge): centred on 0.05.
        return (N / 2 + x * scale * N, N / 2 + (y - 0.05) * scale * N)

    w, shoulder, floor, far = 0.15, -0.05, 0.17, 0.30
    im = Image.new("RGBA", (N, N), (0, 0, 0, 0))

    # Light spill across the floor, widening away from the door and fading out.
    spill = Image.new("L", (N, N), 0)
    ImageDraw.Draw(spill).polygon([P(-w, floor), P(w, floor), P(0.27, far), P(-0.27, far)], fill=255)
    y0, y1 = P(0, floor)[1], P(0, far)[1]
    fade = np.clip((y1 - np.arange(N)) / (y1 - y0), 0, 1) ** 1.4 * 0.45
    a = (np.asarray(spill) * fade[:, None]).astype(np.uint8)
    light = Image.new("RGBA", (N, N), c["light"] + (0,))
    light.putalpha(Image.fromarray(a, "L"))
    im.alpha_composite(light)

    d = ImageDraw.Draw(im)
    d.rectangle([P(-w, shoulder), P(w, floor)], fill=c["door"])
    d.ellipse([P(-w, shoulder - w), P(w, shoulder + w)], fill=c["door"])      # the round arch
    d.polygon([P(0, -0.075), P(0.102, 0.13), P(0, 0.088), P(-0.102, 0.13)], fill=c["arrow"])
    return im


def on_tile(N, c, scale, mask):
    body = tile(N, c)
    body.alpha_composite(mark(N, c, scale))
    body.putalpha(mask)
    return body


def app_icons():
    os.makedirs(graph.UI_DIR, exist_ok=True)
    n = 432
    N = n * S
    for theme, c in ICONS.items():
        out = lambda kind: os.path.join(graph.UI_DIR, f"Icon-{theme}-{kind}.png")
        tile(N, c).resize((n, n), Image.LANCZOS).save(out("back"))
        # Adaptive: the mark inside the middle 66%, which every launcher's mask keeps.
        mark(N, c, 1.2).resize((n, n), Image.LANCZOS).save(out("door"))
        on_tile(N, c, 1.4, squircle(N)).resize((n, n), Image.LANCZOS).save(out("square"))
        circle = Image.new("L", (N, N), 0)
        ImageDraw.Draw(circle).ellipse([0, 0, N - 1, N - 1], fill=255)
        on_tile(N, c, 1.3, circle).resize((n, n), Image.LANCZOS).save(out("round"))

        # The launch logo: the squircle 84% of the picture, a little high, over a soft shadow below it.
        n2, size = 512, 0.84
        N2 = n2 * S
        mask = squircle(N2, size)
        body = on_tile(N2, c, 1.4 * size, mask).resize((n2, n2), Image.LANCZOS)
        shade = mask.resize((n2, n2), Image.LANCZOS).filter(ImageFilter.GaussianBlur(14))
        shadow = Image.new("RGBA", (n2, n2), (0, 0, 0, 0))
        shadow.putalpha(Image.fromarray((np.asarray(shade) * 0.24).astype(np.uint8), "L"))
        logo = Image.new("RGBA", (n2, n2), (0, 0, 0, 0))
        logo.alpha_composite(shadow, dest=(0, 12))
        logo.alpha_composite(body)
        logo.save(os.path.join(graph.UI_DIR, f"Logo-{theme}.png"))
        print(theme, "icons and logo")


if __name__ == "__main__":
    app_icons()
