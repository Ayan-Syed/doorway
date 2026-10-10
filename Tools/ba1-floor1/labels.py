"""Room numbers of BA1 floor 1 in plan pixels, read off Knight Hacks' labeled SVG.

The PNG is the SVG's viewBox drawn exactly 3000 px across, so an SVG point maps to the
PNG by subtracting the viewBox corner and scaling by PNG width / viewBox width.
Run from anywhere: python labels.py -> ba1f1_labels.json (one-off; the file is committed).
"""
import json
import os
import re
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
BOX = os.path.normpath(os.path.join(HERE, "..", ".."))
SVG = os.path.join(BOX, "FloorPlans", "BA1-floor1-knighthacks-labeled.svg")
PNG = os.path.join(BOX, "FloorPlans", "BA1-floor1-knighthacks-labeled.png")
OUT = os.path.join(HERE, "ba1f1_labels.json")

TEXT = re.compile(r'<text x="([\d.]+)" y="([\d.]+)"[^>]*font-size="([\d.]+)"[^>]*>([^<]*)</text>')


def main():
    for path in (SVG, PNG):
        if not os.path.exists(path):
            sys.exit(f"labels.py: missing input {path}")
    with open(SVG, encoding="utf-8") as f:
        svg = f.read()
    vb = [float(v) for v in re.search(r'viewBox="([^"]+)"', svg).group(1).split()]
    with Image.open(PNG) as im:
        k = im.width / vb[2]                      # 5.609993 px per SVG unit
    labels = []
    for m in TEXT.finditer(svg):
        x, y, fs, t = float(m.group(1)), float(m.group(2)), float(m.group(3)), m.group(4)
        # SVG y is the text baseline; 0.35 x font size up is roughly the middle of the digits
        labels.append({"text": t, "x": round((x - vb[0]) * k, 1), "y": round((y - fs * 0.35 - vb[1]) * k, 1)})
    if not labels:
        sys.exit(f"labels.py: no <text> labels found in {SVG}")
    with open(OUT, "w") as f:
        json.dump(labels, f, indent=0)
    print(len(labels), "labels; px per svg unit", round(k, 4))


if __name__ == "__main__":
    main()
