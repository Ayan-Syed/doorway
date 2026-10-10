"""A line-for-line Python port of Assets/Navigation/Scripts/Router.cs (Dijkstra, Douglas-Peucker, the turn
list), so the tests can walk every door-to-place route of the shipped plan. Keep it in step with Router.cs."""
import math

import numpy as np

CLUSTER_GAP = 3.0
JOG_GAP = 5.0
MINIMUM_TURN = 25.0
SLIGHT_TURN = 55.0


def closest_on_segment(p, a, b):
    ab = b - a
    if ab @ ab < 1e-6:
        return a
    return a + ab * min(max((p - a) @ ab / (ab @ ab), 0.0), 1.0)


def signed_angle(u, v):
    """Unity's Vector2.SignedAngle: degrees, + anticlockwise, 0 if either is (nearly) zero."""
    if np.linalg.norm(u) < 1e-7 or np.linalg.norm(v) < 1e-7:
        return 0.0
    return math.degrees(math.atan2(u[0] * v[1] - u[1] * v[0], u @ v))


def classify(angle):
    a = abs(angle)
    if a < 25:
        return "Straight"
    if a > 155:
        return "TurnAround"
    side = "Left" if angle > 0 else "Right"
    if a < 55:
        return "Slight" + side
    if a < 125:
        return side
    return "Sharp" + side


class Router:
    def __init__(self, plan):
        self.plan = plan
        self.ids = [n["id"] for n in plan["nodes"]]
        self.index = {n: i for i, n in enumerate(self.ids)}
        self.pos = [np.array([n["x"], n["z"]], float) for n in plan["nodes"]]
        self.links = [[] for _ in self.ids]
        for e in plan["edges"]:
            a, b = self.index[e["a"]], self.index[e["b"]]
            self.links[a].append(b)
            self.links[b].append(a)

    def find_route(self, from_node, place):
        cost = [math.inf] * len(self.pos)
        cost[self.index[from_node]] = 0.0
        return self.search(cost, None, place)

    def search(self, cost, lead, place):
        n = len(self.pos)
        previous, done = [-1] * n, [False] * n
        while True:
            current = -1
            for i in range(n):
                if not done[i] and cost[i] != math.inf and (current < 0 or cost[i] < cost[current]):
                    current = i
            if current < 0:
                break
            done[current] = True
            for nxt in self.links[current]:
                c = cost[current] + float(np.linalg.norm(self.pos[current] - self.pos[nxt]))
                if c < cost[nxt]:
                    cost[nxt], previous[nxt] = c, current
        end, door = -1, None
        for d in place["doors"]:
            i = self.index.get(d["node"], -1)
            if i >= 0 and (end < 0 or cost[i] < cost[end]):
                end, door = i, d
        if end < 0 or cost[end] == math.inf:
            return None
        path, i = [], end
        while i >= 0:
            path.append(self.pos[i])
            i = previous[i]
        if lead:
            for p in reversed(lead):
                if np.linalg.norm(path[-1] - p) > 0.05:
                    path.append(p)
        path.reverse()
        return Route(simplify(path, 0.5), place, door)


def simplify(points, tolerance):
    if len(points) < 3:
        return points
    last = len(points) - 1
    keep = [False] * len(points)
    keep[0] = keep[last] = True
    ranges = [(0, last)]
    while ranges:
        first, end = ranges.pop()
        farthest, index = 0.0, -1
        for i in range(first + 1, end):
            d = float(np.linalg.norm(points[i] - closest_on_segment(points[i], points[first], points[end])))
            if d > farthest:
                farthest, index = d, i
        if index >= 0 and farthest > tolerance:
            keep[index] = True
            ranges.append((first, index))
            ranges.append((index, end))
    return [p for p, k in zip(points, keep) if k]


class Route:
    def __init__(self, points, place, door):
        if len(points) == 1:
            points = points + [points[0]]
        self.points, self.place, self.door = points, place, door
        self.distances = [0.0]
        for i in range(1, len(points)):
            self.distances.append(self.distances[-1] + float(np.linalg.norm(points[i] - points[i - 1])))
        self.length = self.distances[-1]
        self.turns = self.find_turns()

    def find_turns(self):
        pts, dist = self.points, self.distances
        found, i = [], 1
        while i < len(pts) - 1:
            j = i + 1
            while j < len(pts) - 1 and dist[j] - dist[j - 1] < CLUSTER_GAP:
                j += 1
            angle = signed_angle(pts[i] - pts[i - 1], pts[j] - pts[j - 1])
            if abs(angle) >= MINIMUM_TURN:
                found.append((dist[i], angle))
            i = j
        turns, k = [], 0
        while k < len(found):
            if (k + 1 < len(found) and found[k + 1][0] - found[k][0] < JOG_GAP and found[k][1] * found[k + 1][1] < 0
                    and abs(found[k][1]) < SLIGHT_TURN and abs(found[k + 1][1]) < SLIGHT_TURN
                    and abs(found[k][1] + found[k + 1][1]) < MINIMUM_TURN):
                k += 2
                continue
            turns.append(found[k])
            k += 1
        return turns


def all_routes(plan):
    """(start node, place, Route or None) for every door spot to every place it isn't a door of."""
    router = Router(plan)
    starts = sorted({d["node"] for p in plan["places"] for d in p["doors"]})
    for s in starts:
        for p in plan["places"]:
            if any(d["node"] == s for d in p["doors"]):
                continue
            yield s, p, router.find_route(s, p)
