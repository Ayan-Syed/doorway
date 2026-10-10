"""Shared fixtures for the data tests: python -m pytest Tools/tests (from the repo root, after make_data.py)."""
import json
import os
import sys

import numpy as np
import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "ba1-floor1")))
sys.path.insert(0, HERE)

import graph  # noqa: E402


@pytest.fixture(scope="session")
def plan():
    with open(graph.PLAN_JSON, encoding="utf-8") as f:
        return json.load(f)


@pytest.fixture(scope="session")
def regions():
    """(R, wall, dark, names) from regions.npz, or from segment.py in memory when it isn't there (it's gitignored)."""
    if os.path.exists(graph.REGIONS):
        with np.load(graph.REGIONS) as d:
            names = {int(i): (str(t), str(k)) for i, t, k in zip(d["ids"], d["texts"], d["kinds"])}
            return d["regions"], d["wall"], d["dark"], names
    import segment
    R, wall, dark, names = segment.segment()
    return R, wall, dark, names
