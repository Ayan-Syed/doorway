"""HEC floor 1's walkway graph, doors and entrances, all from the regions hec.py draws (raster px, y down).

  - the graph: the hallways' centre lines (the walkway thinned to a line on 10 cm cells), spurs under SPUR cut
    off, and each line simplified to points no more than SIMPLIFY off it;
  - a door per room that has a hallway beside it: in the middle of the longest stretch of its wall on a hallway,
    with its spot DOOR_OUT out in the hallway (less in a narrow one) facing the wall;
  - the entrances in onsite.json: the nearest point of the outline, the spot just inside on the hallway;
  - shortcuts across open floor where the centre lines go the long way round (a centre line dips into the middle
    of a wide junction, and a route along it would double back);
  - every door spot joined to the graph at the nearest point it can see along a clear line, which splits that link.
"""
import math

import numpy as np
from scipy import ndimage
from shapely.geometry import Point, Polygon

import hec

SPUR = 2.0            # metres: dead ends shorter than this are the thinning's whiskers, not hallways
SIMPLIFY = 0.3        # metres a simplified hallway line may stray from the centre line
DOOR_OUT = 0.8        # metres from the wall to the door spot, at most
SNAP = 0.4            # metres: a door joins the graph at an existing point this close rather than splitting a link
NEAR_LINK = 2.5       # metres: a door spot is also linked to every hallway point this close that it can see
SHORTCUT = 6.0        # metres: hallway points this close, with clear floor between them...
DETOUR = 1.3          # ...whose way round the graph is this many times longer, get a link straight across
CLEARANCE = 0.3       # metres of clear floor a shortcut keeps on each side


def thin(img):
    """Zhang-Suen thinning, vectorised: a one-pixel-wide centre line of a mask."""
    img = img.astype(np.uint8).copy()
    changed = True
    while changed:
        changed = False
        for step in (0, 1):
            P = np.pad(img, 1)
            p2, p3, p4, p5 = P[:-2, 1:-1], P[:-2, 2:], P[1:-1, 2:], P[2:, 2:]
            p6, p7, p8, p9 = P[2:, 1:-1], P[2:, :-2], P[1:-1, :-2], P[:-2, :-2]
            ring = [p2, p3, p4, p5, p6, p7, p8, p9, p2]
            b = sum(p.astype(np.int32) for p in ring[:8])
            a = sum(((ring[k] == 0) & (ring[k + 1] == 1)).astype(np.int32) for k in range(8))
            if step == 0:
                c = ((p2 * p4 * p6) == 0) & ((p4 * p6 * p8) == 0)
            else:
                c = ((p2 * p4 * p8) == 0) & ((p2 * p6 * p8) == 0)
            m = (img == 1) & (b >= 2) & (b <= 6) & (a == 1) & c
            if m.any():
                img[m] = 0
                changed = True
    return img.astype(bool)


def lines(skeleton):
    """The skeleton as a graph: (node cells {id: (row, col)}, edges [(a, b, [cells along it])]).
    Cells link to their 4 neighbours, and diagonally only where no 4-neighbour links the two already
    (m-adjacency), so a line's staircase is a plain chain: every cell has two links but the line's ends (one) and
    junctions (three or more). Touching junction cells are one node; a loop with neither gets a node of its own."""
    cells = {tuple(int(v) for v in c) for c in np.argwhere(skeleton)}

    def links(c):
        y, x = c
        out = [n for n in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)) if n in cells]
        for dy, dx in ((-1, -1), (-1, 1), (1, -1), (1, 1)):
            n = (y + dy, x + dx)
            if n in cells and (y + dy, x) not in cells and (y, x + dx) not in cells:
                out.append(n)
        return out

    adj = {c: links(c) for c in cells}
    special = {c for c, a in adj.items() if len(a) != 2}
    node_of, node_at = {}, {}

    def make_node(group):
        k = len(node_at) + 1
        mid = np.mean(group, axis=0)
        node_at[k] = min(group, key=lambda c: (c[0] - mid[0]) ** 2 + (c[1] - mid[1]) ** 2)
        for c in group:
            node_of[c] = k

    for c in sorted(special):                       # touching junction cells: one node
        if c in node_of:
            continue
        group, todo = [], [c]
        node_of[c] = -1
        while todo:
            d = todo.pop()
            group.append(d)
            for n in adj[d]:
                if n in special and n not in node_of:
                    node_of[n] = -1
                    todo.append(n)
        make_node(group)

    edges, walked = [], set()

    def trace(c):
        for first in adj[c]:
            if first in node_of or first in walked:
                continue
            path, prev, cur = [first], c, first
            walked.add(first)
            while cur not in node_of:
                nxt = [n for n in adj[cur] if n != prev]
                if not nxt:
                    break
                prev, cur = cur, nxt[0]
                if cur in node_of:
                    break
                if cur in walked:              # round a loop back to where it started
                    break
                walked.add(cur)
                path.append(cur)
            if cur in node_of:
                a, b = node_of[c], node_of[cur]
                edges.append((a, b, [node_at[a]] + path + [node_at[b]]))

    for c in sorted(node_of):
        trace(c)
    for c in sorted(cells):                        # loops with no end or junction on them
        if c not in walked and c not in node_of:
            make_node([c])
            trace(c)
    return node_at, [(a, b, p) for a, b, p in edges if a != b or length(p) > 1.0]


def length(path):
    p = np.asarray(path, float)
    return float(np.hypot(*np.diff(p, axis=0).T).sum()) * hec.CELL


def prune(node_at, edges):
    """Cuts dead ends shorter than SPUR (repeatedly), then merges nodes with exactly two links."""
    edges = list(edges)
    while True:
        degree = {}
        for a, b, _ in edges:
            degree[a] = degree.get(a, 0) + 1
            degree[b] = degree.get(b, 0) + 1
        drop = [k for k, (a, b, p) in enumerate(edges) if a != b and length(p) < SPUR
                and (degree[a] == 1) != (degree[b] == 1) and max(degree[a], degree[b]) >= 3]
        if not drop:
            break
        edges = [e for k, e in enumerate(edges) if k not in set(drop)]
    while True:
        degree = {}
        for a, b, _ in edges:
            degree[a] = degree.get(a, 0) + 1
            degree[b] = degree.get(b, 0) + 1
        middle = next((v for v, d in degree.items() if d == 2
                       and sum(1 for a, b, _ in edges if a == v and b == v) == 0), None)
        if middle is None:
            break
        (i, (a1, b1, p1)), (j, (a2, b2, p2)) = [(k, e) for k, e in enumerate(edges) if middle in e[:2]]
        p1 = p1 if b1 == middle else p1[::-1]
        p2 = p2 if a2 == middle else p2[::-1]
        start = a1 if b1 == middle else b1
        end = b2 if a2 == middle else a2
        edges = [e for k, e in enumerate(edges) if k not in (i, j)] + [(start, end, p1 + p2[1:])]
    used = {v for a, b, _ in edges for v in (a, b)}
    return {k: v for k, v in node_at.items() if k in used}, edges


def simplify(path, tolerance):
    """Douglas-Peucker on cells: the indices of the points kept."""
    p = np.asarray(path, float)
    keep = [0, len(p) - 1]
    stack = [(0, len(p) - 1)]
    while stack:
        i, j = stack.pop()
        if j <= i + 1:
            continue
        a, b = p[i], p[j]
        ab = b - a
        L = np.hypot(*ab)
        seg = p[i + 1:j]
        if L < 1e-9:
            d = np.hypot(*(seg - a).T)
        else:
            d = np.abs(ab[0] * (seg[:, 1] - a[1]) - ab[1] * (seg[:, 0] - a[0])) / L
        k = int(np.argmax(d))
        if d[k] > tolerance:
            keep.append(i + 1 + k)
            stack += [(i, i + 1 + k), (i + 1 + k, j)]
    return sorted(set(keep))


class Graph:
    """Hallway points and links in raster px."""

    def __init__(self):
        self.nodes = {}          # id -> (x, y) raster px
        self.edges = []          # (a, b)

    def add(self, name, xy):
        assert name not in self.nodes, name
        self.nodes[name] = (float(xy[0]), float(xy[1]))
        return name


def hallway_graph(frame, R):
    """The walkway graph of the hallways: Graph in raster px, and the walkway cells it was thinned from."""
    width = int(math.ceil(frame.width / hec.PX_PER_M / hec.CELL))
    height = int(math.ceil(frame.height / hec.PX_PER_M / hec.CELL))
    step = hec.CELL * hec.PX_PER_M
    cx = np.clip(((np.arange(width) + 0.5) * step).astype(int), 0, frame.width - 1)
    cy = np.clip(((np.arange(height) + 0.5) * step).astype(int), 0, frame.height - 1)
    hall = R[cy[:, None], cx[None, :]] == hec.WALKWAY
    soft = ndimage.gaussian_filter(hall.astype(np.float32), 2.0) > 0.5     # rounds the corners off...
    narrow = ndimage.distance_transform_edt(hall) <= 3                     # ...but keeps the narrow necks
    node_at, edges = prune(*lines(thin((soft | narrow) & hall)))
    to_px = lambda c: ((c[1] + 0.5) * step, (c[0] + 0.5) * step)
    g = Graph()
    order = sorted(node_at, key=lambda k: node_at[k])
    name = {k: g.add(f"h{n + 1}", to_px(node_at[k])) for n, k in enumerate(order)}
    bends = 0
    for a, b, path in sorted(edges, key=lambda e: (node_at[e[0]], node_at[e[1]], len(e[2]))):
        keep = simplify(path, SIMPLIFY / hec.CELL)
        chain = [name[a]]
        for k in keep[1:-1]:
            bends += 1
            chain.append(g.add(f"b{bends}", to_px(path[k])))
        chain.append(name[b])
        for u, v in zip(chain, chain[1:]):
            if u != v:
                g.edges.append((u, v))
    join_pieces(g, R)
    shortcuts(g, R)
    return g, hall


def join_pieces(g, R):
    """The centre lines can break where a hallway pinches: each piece but the largest is joined to the rest by the
    shortest clear line between their points, or dropped if there is none."""
    import graph as ba1
    while True:
        pieces = sorted(ba1.components(g.nodes, g.edges), key=len)
        if len(pieces) == 1:
            return
        piece, rest = pieces[0], [n for p in pieces[1:] for n in p]
        pairs = sorted((math.hypot(*(np.array(g.nodes[a]) - g.nodes[b])), a, b) for a in piece for b in rest)
        link = next(((a, b) for _, a, b in pairs if clear(R, g.nodes[a], g.nodes[b])), None)
        if link:
            g.edges.append(link)
        else:
            for n in piece:
                del g.nodes[n]
            g.edges = [e for e in g.edges if e[0] not in piece and e[1] not in piece]


def shortcuts(g, R):
    """Links straight across clear floor between hallway points SHORTCUT apart or less whose way round the graph
    is DETOUR times longer, shortest first, each judged against the graph with the ones before it."""
    from scipy.sparse import csr_matrix
    from scipy.sparse.csgraph import shortest_path
    names = list(g.nodes)
    index = {n: i for i, n in enumerate(names)}
    xy = np.array([g.nodes[n] for n in names])
    reach = SHORTCUT * hec.PX_PER_M
    pairs = sorted((float(np.hypot(*(xy[i] - xy[j]))), i, j) for i in range(len(names)) for j in range(i + 1, len(names))
                   if np.hypot(*(xy[i] - xy[j])) <= reach)
    added = 0
    weights = None
    for d, i, j in pairs:
        if weights is None:
            rows = [index[a] for a, b in g.edges] + [index[b] for a, b in g.edges]
            cols = [index[b] for a, b in g.edges] + [index[a] for a, b in g.edges]
            lens = [float(np.hypot(*(xy[r] - xy[c]))) for r, c in zip(rows, cols)]
            weights = shortest_path(csr_matrix((lens, (rows, cols)), shape=(len(names),) * 2), directed=False)
        if weights[i, j] <= DETOUR * d + 0.5 * hec.PX_PER_M:
            continue
        side = (xy[j] - xy[i]) / d
        side = np.array([-side[1], side[0]]) * CLEARANCE * hec.PX_PER_M
        if all(clear(R, xy[i] + o, xy[j] + o) for o in (0 * side, side, -side)):
            g.edges.append((names[i], names[j]))
            added += 1
            weights = None
    return added


def on_hall(R, x, y):
    yi, xi = int(round(y)), int(round(x))
    return 0 <= yi < R.shape[0] and 0 <= xi < R.shape[1] and R[yi, xi] == hec.WALKWAY


def clear(R, p, q):
    """True if the straight line p -> q stays on the walkway (raster px, every 2 px)."""
    n = max(int(math.hypot(q[0] - p[0], q[1] - p[1]) / 2), 1)
    return all(on_hall(R, p[0] + (q[0] - p[0]) * t / n, p[1] + (q[1] - p[1]) * t / n) for t in range(n + 1))


def room_door(R, rid):
    """(wall point, normal out into the hallway, spot) for room rid, raster px, or None if no hallway touches it."""
    m_all = R == rid
    ys, xs = np.nonzero(m_all)
    pad = 6
    y0, y1 = max(ys.min() - pad, 0), min(ys.max() + pad + 1, R.shape[0])
    x0, x1 = max(xs.min() - pad, 0), min(xs.max() + pad + 1, R.shape[1])
    sub = R[y0:y1, x0:x1]
    edge = (sub == rid) & ndimage.binary_dilation(sub == hec.WALKWAY, structure=np.ones((3, 3)))
    if not edge.any():
        return None
    runs, n = ndimage.label(edge, structure=np.ones((3, 3)))
    longest = int(np.argmax(np.bincount(runs.ravel())[1:]) + 1)
    pts = np.argwhere(runs == longest)[:, ::-1].astype(float) + [x0, y0]       # (x, y)
    if len(pts) < 0.5 * hec.PX_PER_M:
        return None                                  # touches a hallway along less than half a metre
    c = pts.mean(axis=0)
    t = np.linalg.svd(pts - c, full_matrices=False)[2][0]
    mid = pts[np.argsort((pts - c) @ t)[len(pts) // 2]]
    near = pts[np.hypot(*(pts - mid).T) < hec.PX_PER_M]
    if len(near) >= 3:
        t = np.linalg.svd(near - near.mean(axis=0), full_matrices=False)[2][0]
    n_ = np.array([-t[1], t[0]])
    probe = mid + n_ * 0.4 * hec.PX_PER_M
    if not on_hall(R, *probe):
        n_ = -n_
    return mid, n_, out_spot(R, mid, n_)


def out_spot(R, wall, n):
    """The door spot: from the wall out along n, DOOR_OUT or halfway across the hallway, whichever is less."""
    step = 0.05 * hec.PX_PER_M
    first, across = None, 0.0
    for k in range(1, int(6 * hec.PX_PER_M / step)):
        p = wall + n * step * k
        if on_hall(R, *p):
            first = k if first is None else first
            across = (k - first + 1) * step
        elif first is not None:
            break
    if first is None:
        return None
    d = first * step + min(DOOR_OUT * hec.PX_PER_M, max(across / 2, step))
    return wall + n * d


def entrance(frame, R, outline, at):
    """(outline point, normal out of the building, spot just inside on the hallway) for an entrance given near
    `at` (drawing px), or None if no hallway is behind that bit of wall."""
    ring = Polygon(frame.raster(outline)).exterior
    p = Point(*frame.raster([at])[0])
    s = ring.project(p)
    o = np.array(ring.interpolate(s).coords[0])
    a = np.array(ring.interpolate(max(s - 10, 0)).coords[0])
    b = np.array(ring.interpolate(min(s + 10, ring.length)).coords[0])
    t = (b - a) / np.linalg.norm(b - a)
    n = np.array([-t[1], t[0]])
    yi, xi = (o + n * 0.5 * hec.PX_PER_M)[::-1].astype(int)
    if R[yi, xi] != hec.OUTSIDE:
        n = -n
    step = 0.05 * hec.PX_PER_M
    for k in range(1, int(5 * hec.PX_PER_M / step)):
        q = o - n * step * k
        if on_hall(R, *q):
            spot = q - n * 0.4 * hec.PX_PER_M
            return o, n, spot if on_hall(R, *spot) else q
    return None


def join(g, R, point_name, spot):
    """Joins a door spot to the graph: to the nearest point on a link it can see, splitting the link there (or
    to an end of it within SNAP). Returns the hallway point it joined, or None."""
    best = []
    for k, (a, b) in enumerate(g.edges):
        pa, pb = np.array(g.nodes[a]), np.array(g.nodes[b])
        ab = pb - pa
        t = 0.0 if ab @ ab < 1e-9 else min(max((spot - pa) @ ab / (ab @ ab), 0.0), 1.0)
        q = pa + ab * t
        best.append((float(np.hypot(*(q - spot))), k, t, q))
    best.sort(key=lambda b: b[0])
    for _, k, t, q in best:
        if not clear(R, spot, q):
            continue
        a, b = g.edges[k]
        L = math.hypot(*(np.array(g.nodes[b]) - g.nodes[a]))
        if t * L < SNAP * hec.PX_PER_M:
            hall = a
        elif (1 - t) * L < SNAP * hec.PX_PER_M:
            hall = b
        else:
            hall = g.add(f"j{sum(1 for n in g.nodes if n.startswith('j')) + 1}", q)
            g.edges[k] = (a, hall)
            g.edges.append((hall, b))
        g.edges.append((point_name, hall))
        # Also every other hallway point close by in plain sight, as BA1's doors join the points either side of
        # them: a route then comes straight in, and doesn't run on to the join and double back.
        for other, xy in list(g.nodes.items()):
            if (other != hall and other != point_name and other[0] in "hbj"
                    and math.hypot(*(np.array(xy) - spot)) < NEAR_LINK * hec.PX_PER_M and clear(R, spot, xy)):
                g.edges.append((point_name, other))
        return hall
    return None
