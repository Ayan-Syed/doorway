"""Every door-to-place route of the shipped plan, walked with the port of Router.cs (routing.py)."""
import math

import pytest

import routing

# Turn-arounds that are right: the destination's door is a step behind the start's (doors side by side or facing
# across a hall). Anything else that reads "turn around" is a graph link that overshoots.
TURN_AROUND_OK = {
    ("pma", "124"), ("pwa", "124"), ("r124_0", "wc-m-a"), ("r124_0", "wc-w-a"),
    ("r136_0", "137"), ("r137_0", "136"), ("r137_0", "136A"),
    ("r139_0", "142"), ("r139_0", "136F"), ("r142_0", "139"),
}


@pytest.fixture(scope="module")
def routes(plan):
    return list(routing.all_routes(plan))


def test_every_place_is_reachable_from_every_door(routes):
    assert [(s, p["id"]) for s, p, r in routes if r is None] == []


def test_no_turn_around_but_the_known_ones(routes):
    found = {(s, p["id"]) for s, p, r in routes for _, a in r.turns if routing.classify(a) == "TurnAround"}
    assert found - TURN_AROUND_OK == set()


def test_turns_are_real_headings(routes):
    assert all(abs(a) <= 180 for _, _, r in routes for _, a in r.turns)


@pytest.mark.parametrize("start,place,length,turns", [
    # 110 to 113 along hall 101: the door spots sit on the hall's points, so it's straight (it used to be 12.1 m
    # with a U-turn, when p110 and p113 weren't joined to the points they sit on).
    ("p110", "113", 6.1, []),
    ("p113", "110", 6.1, []),
    # Into the atrium lift: one turn, and no turn-around 1.4 m before it any more (pea joins t_sw as well as t_s).
    ("p107w", "elev-a", 15.2, ["Right"]),
    # 113 to 121 across the atrium (the old app's preview route: 47.9 m, four turns).
    ("p113", "121", 47.9, ["Right", "Left", "SlightRight", "SlightRight"]),
    # 121's south door to the stairs by its north door: straight up the hall, no stepping back first.
    ("p121s", "stair-120", 8.2, []),
])
def test_golden_routes(plan, start, place, length, turns):
    router = routing.Router(plan)
    route = router.find_route(start, next(p for p in plan["places"] if p["id"] == place))
    assert route.length == pytest.approx(length, abs=0.15)
    assert [routing.classify(a) for _, a in route.turns] == turns


def test_two_close_bends_read_as_their_net_turn():
    """Two 105-degree left bends 1 m apart turn 210 degrees left, which is 150 degrees right: a sharp right."""
    pts = [(0.0, 0.0), (5.0, 0.0)]
    a = math.radians(105)
    pts.append((pts[-1][0] + math.cos(a), pts[-1][1] + math.sin(a)))
    b = math.radians(210)
    pts.append((pts[-1][0] + 5 * math.cos(b), pts[-1][1] + 5 * math.sin(b)))
    import numpy as np
    route = routing.Route([np.array(p) for p in pts], None, None)
    assert [routing.classify(t) for _, t in route.turns] == ["SharpRight"]


def test_a_jog_is_not_a_turn():
    import numpy as np
    pts = [np.array(p) for p in ((0.0, 0.0), (10.0, 0.0), (13.0, 1.5), (25.0, 1.5))]
    assert routing.Route(pts, None, None).turns == []
