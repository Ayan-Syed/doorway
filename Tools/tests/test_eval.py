"""Tools/eval/report.py on a synthetic results.csv with known answers."""
import csv
import math
import os
import sys

import numpy as np
import pytest

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "eval")))

import report  # noqa: E402

# full on test: errors 1..10 m, so median 5.5, p75 7.75, p95 9.55 (numpy's linear interpolation), mean 5.5.
FULL_TEST = [float(e) for e in range(1, 11)]
# no-signs on test: every error 2 m worse.
NO_SIGNS_TEST = [e + 2 for e in FULL_TEST]


def write_csv(path):
    rows = []
    for config, errors in (("full", FULL_TEST), ("no-signs", NO_SIGNS_TEST)):
        for k, e in enumerate(errors):
            rows.append({"walk": f"w{k % 2}", "split": "test", "config": config, "checkpoint": k,
                         "time_s": 10.0 * k, "error_m": e, "state": "tracking"})
    rows.append({"walk": "w9", "split": "tune", "config": "full", "checkpoint": 0, "time_s": 0.0,
                 "error_m": 3.0, "state": "tracking"})
    rows.append({"walk": "w9", "split": "tune", "config": "full", "checkpoint": 1, "time_s": 5.0,
                 "error_m": "", "state": "lost"})
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=report.COLUMNS)
        w.writeheader()
        w.writerows(rows)


@pytest.fixture
def results(tmp_path):
    path = tmp_path / "results.csv"
    write_csv(path)
    return path


def test_metrics_known_values():
    m = report.metrics(FULL_TEST)
    assert m["n"] == 10
    assert m["median"] == pytest.approx(5.5)
    assert m["p75"] == pytest.approx(7.75)
    assert m["p95"] == pytest.approx(9.55)
    assert m["max"] == 10 and m["mean"] == pytest.approx(5.5)
    assert report.metrics([math.nan])["n"] == 0


def test_bootstrap_is_seeded_and_brackets_the_statistic():
    lo, hi = report.bootstrap(FULL_TEST, 75)
    assert (lo, hi) == report.bootstrap(FULL_TEST, 75)           # same seed, same interval
    assert 1 <= lo <= 7.75 <= hi <= 10
    # Every resample of a constant sample is that constant.
    assert report.bootstrap([4.0] * 20, 50) == (4.0, 4.0)
    # The same percentile-bootstrap by hand.
    rng = np.random.default_rng(report.SEED)
    e = np.array(FULL_TEST)
    stats = np.percentile(e[rng.integers(0, 10, (report.BOOTSTRAP, 10))], 75, axis=1)
    assert lo == pytest.approx(np.percentile(stats, 2.5)) and hi == pytest.approx(np.percentile(stats, 97.5))


def test_table_ablation_and_lost(results):
    rows = report.read_results(results)
    stats = report.table(rows)
    assert stats[("full", "test")]["p75"] == pytest.approx(7.75)
    assert stats[("no-signs", "test")]["p75"] == pytest.approx(9.75)
    assert stats[("full", "tune")]["n"] == 1 and stats[("full", "tune")]["lost"] == 1
    assert report.ablation(stats, "test") == [("no-signs", pytest.approx(2.0))]


def test_report_page(results, tmp_path, capsys):
    out = tmp_path / "report.html"
    report.main(["report.py", str(results), str(out)])
    page = out.read_text(encoding="utf-8")
    assert page.startswith("<!doctype html>") and page.count("<svg") == 1 + 1 + 3   # CDF, ablation, 3 walks
    assert "<b>7.75</b>" in page and "ISO/IEC 18305" in page and "+2.00 m" in page
    assert "<script" not in page and "http" not in page.replace("http-equiv", "")
    printed = capsys.readouterr().out
    assert "full" in printed and "7.75" in printed
