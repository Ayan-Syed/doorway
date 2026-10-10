"""HEC floor 1's shipped data (Tools/hec-floor1): the same file checks as BA1's, and every route walked."""
import json
import os

import pytest

import datacheck
import graph
import routing

FLOOR, CAMPUS = "hec-floor1", "ucf-campus"
NEAR_END = 3.5       # metres from a route's start or end


@pytest.fixture(scope="module")
def hec_plan():
    with open(os.path.join(graph.DATA_DIR, f"{FLOOR}.json"), encoding="utf-8") as f:
        return json.load(f)


def test_every_file_reads_and_agrees():
    assert datacheck.problems(FLOOR, CAMPUS, grid=False) == []


def test_names_the_building_as_osm_does(hec_plan):
    assert (hec_plan["building"], hec_plan["shortName"], hec_plan["floor"]) == \
        ("L3Harris Engineering Center", "HEC", "Floor 1")


def test_has_entrances_and_the_known_rooms(hec_plan):
    ids = {p["id"] for p in hec_plan["places"]}
    assert {"101", "117", "118", "119", "125"} <= ids
    assert sum(p["kind"] == "exit" for p in hec_plan["places"]) >= 3
    # Plates only for numbers known for sure: 125's placement is inferred, the U ids are temporary.
    assert {s["text"] for s in hec_plan["signs"]} == {"101", "117", "118", "119"}


def test_every_place_is_reachable_from_every_door(hec_plan):
    assert [(s, p["id"]) for s, p, r in routing.all_routes(hec_plan) if r is None] == []


def test_no_route_turns_around_on_the_way(hec_plan):
    """Door spots join the graph where they meet a link, so no route runs past a door and comes back. Turning round
    near either end is right in the west offices' nook, where doors are a step apart or set back at a sharp angle."""
    found = sorted({(s, p["id"]) for s, p, r in routing.all_routes(hec_plan) for at, a in r.turns
                    if routing.classify(a) == "TurnAround" and NEAR_END < at < r.length - NEAR_END})
    assert found == []
