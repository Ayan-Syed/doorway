"""UCF's campus from OpenStreetMap, in a floor's plan metres (BA1 floor 1's unless --floor), for the app's outdoor map.

Projects the Overpass download with the floor plan's geo fit, sorts it into ground layers (grass, trees,
water, parking, paths, roads), merges and triangulates each layer, and keeps every building's outline,
roof triangles, height and label spot. The floor's building (the plan's "building", OSM's name) is marked indoor.
Run from anywhere: python campus.py [osm/ucf.json] [--floor hec-floor1]
  -> Assets/Navigation/Data/ucf-campus.bytes and ucf-campus.json (written at the end of main(), read by CampusData.cs);
     for another floor, ucf-campus-<floor>.bytes and .json.
Needs Assets/Navigation/Data/<floor>.json (its building and geo block): run the floor pipeline first. The geo block
used is copied into the .json, so the app's data check can tell when the campus needs building again.
ucf-campus.bytes (little-endian): "DWCP", uint16 version (1), uint16 0; int32 chunk count, then per chunk int32
layer, vertex count, index count, int16 x, y decimetres per vertex, uint16 indices; int32 building count, then per
building int32 height (dm), ring count, index count, int32 points per ring, int16 x, y dm, uint16 indices.
"""
import json
import os
import struct
import sys

import mapbox_earcut as earcut
import numpy as np
from shapely.geometry import LineString, Polygon
from shapely.geometry.polygon import orient
from shapely.ops import polygonize, polylabel, unary_union

HERE = os.path.dirname(os.path.abspath(__file__))
BOX = os.path.normpath(os.path.join(HERE, "..", ".."))
OSM = os.path.join(HERE, "osm", "ucf.json")
DATA = os.path.join(BOX, "Assets", "Navigation", "Data")
FLOOR = "ba1-floor1"                               # the floor the app ships unless built for another
BBOX = (28.5880, -81.2120, 28.6130, -81.1860)     # south, west, north, east, as fetched (osm/ucf.overpassql)
EARTH_RADIUS = 6378137.0                           # the same as PlanGeo in FloorPlan.cs

# Ground layers in draw order; the index is the layer number in the .bytes and in Theme.campusLayers.
LAYERS = ["campus", "green", "sport", "wood", "sand", "paved", "water", "path",
          "casing", "major casing", "road", "major road"]
LAYER = {name: i for i, name in enumerate(LAYERS)}
# Full road widths in metres, by highway value.
ROADS = {"motorway": 18, "trunk": 16, "primary": 16, "primary_link": 9, "secondary": 13, "secondary_link": 8,
         "tertiary": 11, "tertiary_link": 7, "unclassified": 8.5, "residential": 8, "living_street": 7,
         "service": 5, "services": 5, "road": 6}
MAJOR = {"motorway", "trunk", "primary", "primary_link", "secondary", "secondary_link"}   # drawn yellow
PATHS = {"footway": 2.4, "path": 2.2, "cycleway": 2.6, "pedestrian": 4.5, "steps": 2.2, "track": 3.0,
         "bridleway": 2.2}
CASING = 1.8          # a road's casing is this many metres wider than the road
GREEN = {"grass", "meadow", "village_green", "recreation_ground", "greenfield", "cemetery", "park", "garden",
         "playground", "dog_park", "common", "grassland", "heath", "golf_course", "orchard", "plant_nursery"}
SPORT = {"pitch", "track", "stadium", "sports_centre"}
WOOD = {"wood", "forest", "scrub", "wetland", "tree_row"}
# Guessed building heights (m) by kind, when OSM has no height or levels.
HEIGHTS = {"university": 14, "college": 14, "dormitory": 13, "apartments": 11, "residential": 8, "house": 7,
           "detached": 7, "terrace": 7, "office": 13, "commercial": 7, "retail": 6, "industrial": 9,
           "parking": 14, "stadium": 20, "roof": 5, "service": 4, "pavilion": 5, "public": 8, "garage": 4}
CHUNK = 60000         # vertices per ground chunk, so indices fit in uint16
GROUND_BUDGET = 60000  # all chunks go into one uGUI mesh (CampusGround), which tops out near 65k vertices
MAGIC, VERSION = b"DWCP", 1


class Geo:
    """lat/lon to plan metres: PlanGeo.ToPlan, vectorised. Keep the two identical."""

    def __init__(self, g):
        self.lat, self.lon, self.angle, self.x, self.z = g["lat"], g["lon"], g["angle"], g["x"], g["z"]

    def __call__(self, lat, lon):
        rad = np.pi / 180
        east = (np.asarray(lon, np.float64) - self.lon) * rad * EARTH_RADIUS * np.cos(self.lat * rad)
        north = (np.asarray(lat, np.float64) - self.lat) * rad * EARTH_RADIUS
        a = self.angle * rad
        return np.stack([east * np.cos(a) - north * np.sin(a) + self.x,
                         east * np.sin(a) + north * np.cos(a) + self.z], axis=-1)

    def points(self, geometry):
        return self(np.array([p["lat"] for p in geometry]), np.array([p["lon"] for p in geometry]))


def polygons(g):
    """A Polygon, MultiPolygon or GeometryCollection as a list of Polygons (anything else dropped)."""
    if g is None or g.is_empty:
        return []
    if isinstance(g, Polygon):
        return [g]
    if hasattr(g, "geoms"):
        return [p for part in g.geoms for p in polygons(part)]
    return []


def area_of(geo, el):
    """An OSM way or multipolygon relation as a polygon in plan metres, or None if it isn't an area."""
    if el["type"] == "way":
        g = el.get("geometry") or []
        if len(g) < 4 or g[0] != g[-1]:
            return None
        poly = Polygon(geo.points(g))
        return poly if poly.is_valid else poly.buffer(0)        # repair self-crossings
    lines = {"outer": [], "inner": []}
    for m in el.get("members", []):
        if m.get("role") in lines and m.get("geometry") and len(m["geometry"]) >= 2:
            lines[m["role"]].append(LineString(geo.points(m["geometry"])))
    if not lines["outer"]:
        return None
    # Unioning the lines first joins rings that OSM splits across several ways.
    outer = unary_union(list(polygonize(unary_union(lines["outer"]))))
    if outer.is_empty:
        return None
    if lines["inner"]:
        outer = outer.difference(unary_union(list(polygonize(unary_union(lines["inner"])))))
    return outer


def triangulate(poly):
    """Roof or ground triangles, every one clockwise with y up (the app draws those as front faces)."""
    poly = orient(poly, 1.0)                                       # exterior anticlockwise, holes clockwise
    rings = [np.asarray(poly.exterior.coords)[:-1]] + [np.asarray(r.coords)[:-1] for r in poly.interiors]
    rings = [r for r in rings if len(r) >= 3]
    if not rings:
        return None, None, None
    pts = np.concatenate(rings)
    ends = np.cumsum([len(r) for r in rings]).astype(np.uint32)    # earcut wants each ring's END index
    tri = earcut.triangulate_float64(pts, ends).astype(np.int64).reshape(-1, 3)
    if len(tri) == 0:
        return None, None, None
    a, b, c = pts[tri[:, 0]], pts[tri[:, 1]], pts[tri[:, 2]]
    cross = (b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (b[:, 1] - a[:, 1]) * (c[:, 0] - a[:, 0])
    flip = cross > 0                                               # anticlockwise -> swap two corners
    tri[flip] = tri[flip][:, [0, 2, 1]]
    return pts, tri, rings


def decimetres(pts):
    q = np.round(np.asarray(pts) * 10)
    assert np.abs(q).max() < 32767, "campus too big for int16 decimetres"
    return q.astype("<i2").tobytes()


def height_of(tags, name):
    try:
        height = float(tags["height"].split()[0])
    except (KeyError, ValueError, IndexError):
        levels = tags.get("building:levels", "")
        if levels.replace(".", "").isdigit() and levels.count(".") <= 1:
            height = float(levels) * 3.6 + 1.5            # 4 levels: 15.9 m
        else:
            kind = "parking" if "Garage" in name else tags["building"]
            height = HEIGHTS.get(kind, 8)
    if "Arena" in name or "Fieldhouse" in name:
        height = max(height, 22)
    return height


def ground_layer(tags):
    """Which ground layer an OSM area goes in, or None. The first rule that matches wins."""
    kinds = {tags[k] for k in ("landuse", "leisure", "natural", "amenity", "water") if k in tags}
    if tags.get("amenity") == "university":
        return LAYER["campus"]
    if tags.get("natural") == "water" or "water" in tags or kinds & {"swimming_pool", "fountain", "basin", "reservoir"}:
        return LAYER["water"]
    if kinds & SPORT:
        return LAYER["sport"]
    if kinds & WOOD:
        return LAYER["wood"]
    if kinds & GREEN:
        return LAYER["green"]
    if tags.get("natural") in ("sand", "beach") or tags.get("landuse") == "construction":
        return LAYER["sand"]
    if tags.get("amenity") == "parking" or tags.get("landuse") == "garages":
        return LAYER["paved"]
    return None


def outputs(floor):
    """(plan JSON, output path without extension) for a floor: BA1's campus keeps its old name."""
    out = "ucf-campus" if floor == FLOOR else f"ucf-campus-{floor}"
    return os.path.join(DATA, f"{floor}.json"), os.path.join(DATA, out)


def main():
    args = sys.argv[1:]
    floor = FLOOR
    if "--floor" in args:
        at = args.index("--floor")
        floor = args[at + 1]
        del args[at:at + 2]
    osm = args[0] if args else OSM
    plan_path, out = outputs(floor)
    for path, how in ((osm, "POST osm/ucf.overpassql to https://overpass-api.de/api/interpreter and save the result there"),
                      (plan_path, f"run Tools/{floor}/make_data.py first: its geo block places the campus")):
        if not os.path.exists(path):
            sys.exit(f"Missing {path}: {how}")
    with open(plan_path, encoding="utf-8") as f:
        plan = json.load(f)
    geo_block, indoor = plan["geo"], plan["building"]
    geo = Geo(geo_block)
    with open(osm, encoding="utf-8") as f:
        elements = json.load(f)["elements"]

    # Everything is clipped 20 m inside the download's edge, so features cut off there don't show ragged ends.
    s, w, n, e = BBOX
    frame = Polygon(geo([s, s, n, n], [w, e, e, w])).buffer(-20, join_style="mitre")

    shapes = [[] for _ in LAYERS]        # areas
    lines = [[] for _ in LAYERS]         # buffered lines, added after the areas
    buildings = []
    for el in elements:
        tags = el.get("tags", {})
        if tags.get("tunnel") in ("yes", "building_passage") or "indoor" in tags or tags.get("layer", "0").startswith("-"):
            continue
        if "building" in tags:
            a = area_of(geo, el)
            if a is not None and not a.is_empty:
                buildings.append((el, a))
            continue                     # buildings never become ground
        geometry = el.get("geometry") or []
        if "highway" in tags:
            kind = tags["highway"]
            if tags.get("area") == "yes":
                a = area_of(geo, el)
                if a is not None:
                    shapes[LAYER["paved"]].append(a)
            elif el["type"] == "way" and len(geometry) >= 2:
                line = LineString(geo.points(geometry))
                if kind in PATHS:
                    lines[LAYER["path"]].append(line.buffer(PATHS[kind] / 2, quad_segs=2))
                elif kind in ROADS:
                    width = ROADS[kind] * (0.8 if tags.get("service") in ("parking_aisle", "driveway") else 1.0)
                    road, casing = ("major road", "major casing") if kind in MAJOR else ("road", "casing")
                    lines[LAYER[road]].append(line.buffer(width / 2, quad_segs=3))
                    lines[LAYER[casing]].append(line.buffer(width / 2 + CASING / 2, quad_segs=3))
            continue
        a = area_of(geo, el)
        if a is None:
            if tags.get("natural") == "tree_row" and el["type"] == "way" and len(geometry) >= 2:
                lines[LAYER["wood"]].append(LineString(geo.points(geometry)).buffer(3, quad_segs=2))     # 6 m wide
            continue
        layer = ground_layer(tags)
        if layer is not None:
            shapes[layer].append(a)

    chunks = []
    for layer in range(len(LAYERS)):
        parts = [g for g in shapes[layer] + lines[layer] if not g.is_empty]
        count = 0
        if parts:
            # Merged, so road junctions and overlapping grass draw once; clipped; simplified to 25 cm.
            g = unary_union(parts).intersection(frame).simplify(0.25)
            verts, tris, base = [], [], 0
            for p in polygons(g):
                if p.area < 3:
                    continue
                pts, tri, _ = triangulate(p)
                if pts is None:
                    continue
                if base + len(pts) > CHUNK and verts:
                    chunks.append((layer, np.concatenate(verts), np.concatenate(tris)))
                    verts, tris, base = [], [], 0
                verts.append(pts)
                tris.append(tri + base)
                base += len(pts)
                count += len(pts)
            if verts:
                chunks.append((layer, np.concatenate(verts), np.concatenate(tris)))
        print(f"{LAYERS[layer]:>13}: {count} vertices")

    ground = sum(len(c[1]) for c in chunks)
    if ground > GROUND_BUDGET:
        sys.exit(f"{ground} ground vertices: over the {GROUND_BUDGET} one uGUI mesh can hold. Simplify more "
                 f"(the 0.25 m in main()) or drop a layer.")
    if any(len(c[1]) > 65535 for c in chunks):
        sys.exit("a ground chunk has more than 65535 vertices: its uint16 indices would wrap")

    records, infos = [], []
    for el, a in buildings:
        tags = el.get("tags", {})
        a = a.intersection(frame).simplify(0.3)
        name = tags.get("name", "")
        for p in polygons(a):
            if p.area < 12:
                continue
            p = orient(p, 1.0)
            _, tri, rings = triangulate(p)
            if tri is None:
                continue
            if sum(len(r) for r in rings) > 65535:
                sys.exit(f"building {name or el['id']} has more than 65535 outline points (uint16 indices)")
            height = height_of(tags, name)
            spot = polylabel(p, 0.5)       # the point deepest inside: better than the centroid for L and U shapes
            infos.append({"name": name, "x": round(spot.x, 2), "y": round(spot.y, 2), "area": round(p.area, 1),
                          "height": round(height, 1), "indoor": name == indoor})
            records.append((height, rings, tri))
            name = ""                      # one label for a building in pieces

    # The middle of campus: the mean of the Gemini Boulevard ring road's points.
    ring = [geo.points(el["geometry"]) for el in elements if el["type"] == "way" and el.get("geometry")
            and el.get("tags", {}).get("name", "").startswith("Gemini Boulevard")]
    middle = np.concatenate(ring).mean(axis=0)
    lo, hi = frame.bounds[:2], frame.bounds[2:]

    with open(out + ".bytes", "wb") as f:
        f.write(struct.pack("<4sHH", MAGIC, VERSION, 0))
        f.write(struct.pack("<i", len(chunks)))
        for layer, verts, tri in chunks:
            assert tri.max() < 65536
            f.write(struct.pack("<iii", layer, len(verts), tri.size))
            f.write(decimetres(verts))
            f.write(tri.astype("<u2").tobytes())
        f.write(struct.pack("<i", len(records)))
        for height, rings, tri in records:
            f.write(struct.pack("<iii", int(round(height * 10)), len(rings), tri.size))
            f.write(np.array([len(r) for r in rings], "<i4").tobytes())
            f.write(decimetres(np.concatenate(rings)))
            f.write(tri.astype("<u2").tobytes())
    info = {"attribution": "(c) OpenStreetMap contributors",
            "middle": [round(float(middle[0]), 1), round(float(middle[1]), 1)],
            "min": [round(lo[0], 1), round(lo[1], 1)], "max": [round(hi[0], 1), round(hi[1], 1)],
            "geo": geo_block, "buildings": infos}
    with open(out + ".json", "w", encoding="utf-8") as f:
        json.dump(info, f, ensure_ascii=False, separators=(",", ":"))
    print(f"{len(chunks)} chunks, {sum(len(c[1]) for c in chunks)} ground vertices; {len(records)} buildings, "
          f"{sum(len(r) for _, rings, _ in records for r in rings)} outline points; "
          f"middle {info['middle']}, extent {info['min']} to {info['max']}")


if __name__ == "__main__":
    main()
