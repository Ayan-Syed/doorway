"""The positioning evaluation as one page: Builds/Eval/results.csv (ReplayEvaluator's rows) -> Builds/Eval/report.html.

  python Tools/eval/report.py [results.csv] [report.html]

results.csv has one row per checkpoint: walk, split (tune or test), config, checkpoint, time_s, error_m, state.
The page: a methods note, metrics per config and split (n, median, 75th percentile in bold, 95th, max, mean, with
seeded bootstrap 95% intervals for the median and the 75th percentile), the error CDF per config on the test split,
the ablation (each signal removed against "full"), and every walk's error over time. Inline SVG, no scripts, no
external files. A plain-text summary goes to stdout too.
"""
import csv
import html
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
BOX = os.path.normpath(os.path.join(HERE, "..", ".."))
RESULTS = os.path.join(BOX, "Builds", "Eval", "results.csv")
BOOTSTRAP = 2000
SEED = 18305
FULL = "full"
COLUMNS = ("walk", "split", "config", "checkpoint", "time_s", "error_m", "state")
PALETTE = ["#2563eb", "#d97706", "#059669", "#dc2626", "#7c3aed", "#0891b2", "#65a30d", "#db2777"]


def read_results(path):
    """The rows, error_m and time_s as floats (NaN where the filter gave no position)."""
    with open(path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        missing = [c for c in COLUMNS if c not in (reader.fieldnames or [])]
        if missing:
            sys.exit(f"{path}: no column {', '.join(missing)}")
        rows = list(reader)
    for r in rows:
        for key in ("error_m", "time_s"):
            try:
                r[key] = float(r[key])
            except (TypeError, ValueError):
                r[key] = math.nan
    return rows


def metrics(errors):
    """ISO/IEC 18305 / IPIN-style summary of point errors (metres): n, median, p75, p95, max, mean."""
    e = np.asarray([x for x in errors if math.isfinite(x)], float)
    if len(e) == 0:
        return {"n": 0, "median": math.nan, "p75": math.nan, "p95": math.nan, "max": math.nan, "mean": math.nan}
    return {"n": len(e), "median": float(np.percentile(e, 50)), "p75": float(np.percentile(e, 75)),
            "p95": float(np.percentile(e, 95)), "max": float(e.max()), "mean": float(e.mean())}


def bootstrap(errors, q, resamples=BOOTSTRAP, seed=SEED):
    """Percentile-bootstrap 95% interval of the q-th percentile: (low, high). Seeded, so the page is repeatable."""
    e = np.asarray([x for x in errors if math.isfinite(x)], float)
    if len(e) == 0:
        return math.nan, math.nan
    rng = np.random.default_rng(seed)
    stats = np.percentile(e[rng.integers(0, len(e), (resamples, len(e)))], q, axis=1)
    return float(np.percentile(stats, 2.5)), float(np.percentile(stats, 97.5))


def table(rows):
    """{(config, split): metrics + ci_median, ci_p75, lost}."""
    groups = {}
    for r in rows:
        groups.setdefault((r["config"], r["split"]), []).append(r)
    out = {}
    for key, rs in sorted(groups.items()):
        errors = [r["error_m"] for r in rs]
        m = metrics(errors)
        m["lost"] = sum(1 for x in errors if not math.isfinite(x))
        m["ci_median"] = bootstrap(errors, 50)
        m["ci_p75"] = bootstrap(errors, 75)
        out[key] = m
    return out


def ablation(stats, split):
    """[(config, p75 - full's p75)] for every config but full on this split; empty without a full."""
    full = stats.get((FULL, split))
    if not full:
        return []
    return [(c, m["p75"] - full["p75"]) for (c, s), m in stats.items() if s == split and c != FULL]


def configs_of(rows):
    seen = []
    for r in rows:
        if r["config"] not in seen:
            seen.append(r["config"])
    return sorted(seen, key=lambda c: (c != FULL, c))


# Charts: plain SVG, axes in metres and seconds.

def _scale(lo, hi, a, b):
    span = (hi - lo) or 1.0
    return lambda v: a + (v - lo) / span * (b - a)


def _axes(w, h, pad, xmax, ymax, xlabel, ylabel, yfmt="{:g}"):
    parts = [f'<line x1="{pad}" y1="{h - pad}" x2="{w - 10}" y2="{h - pad}" class="axis"/>',
             f'<line x1="{pad}" y1="10" x2="{pad}" y2="{h - pad}" class="axis"/>']
    for k in range(5):
        xv, yv = xmax * k / 4, ymax * k / 4
        x = pad + (w - 10 - pad) * k / 4
        y = h - pad - (h - pad - 10) * k / 4
        parts.append(f'<text x="{x:.1f}" y="{h - pad + 14}" class="tick" text-anchor="middle">{xv:.3g}</text>')
        parts.append(f'<text x="{pad - 4}" y="{y + 4:.1f}" class="tick" text-anchor="end">{yfmt.format(yv)}</text>')
        parts.append(f'<line x1="{pad}" y1="{y:.1f}" x2="{w - 10}" y2="{y:.1f}" class="grid"/>')
    parts.append(f'<text x="{(pad + w) / 2}" y="{h - 4}" class="label" text-anchor="middle">{xlabel}</text>')
    parts.append(f'<text x="12" y="{h / 2}" class="label" text-anchor="middle" '
                 f'transform="rotate(-90 12 {h / 2})">{ylabel}</text>')
    return parts


def _legend(names, x, y):
    return [f'<rect x="{x}" y="{y + 16 * i - 8}" width="10" height="10" fill="{PALETTE[i % len(PALETTE)]}"/>'
            f'<text x="{x + 14}" y="{y + 16 * i + 1}" class="tick">{html.escape(n)}</text>'
            for i, n in enumerate(names)]


def svg_cdf(series, w=640, h=320, pad=46):
    """series: [(name, errors)] -> the empirical CDF of each."""
    finite = {n: np.sort([x for x in e if math.isfinite(x)]) for n, e in series}
    top = max([float(v[-1]) for v in finite.values() if len(v)] + [1.0])
    sx, sy = _scale(0, top, pad, w - 10), _scale(0, 1, h - pad, 10)
    parts = _axes(w, h, pad, top, 1.0, "error (m)", "fraction of checkpoints", "{:.2f}")
    for i, (name, _) in enumerate(series):
        e = finite[name]
        if not len(e):
            continue
        pts = [(0.0, 0.0)]
        for k, v in enumerate(e):
            pts += [(float(v), k / len(e)), (float(v), (k + 1) / len(e))]
        path = " ".join(f"{sx(x):.1f},{sy(y):.1f}" for x, y in pts)
        parts.append(f'<polyline points="{path}" fill="none" stroke="{PALETTE[i % len(PALETTE)]}" stroke-width="2"/>')
    parts += _legend([n for n, _ in series], w - 170, 24)
    return f'<svg viewBox="0 0 {w} {h}" role="img">{"".join(parts)}</svg>'


def svg_bars(bars, w=640, pad=46):
    """bars: [(name, delta metres)] -> horizontal bars from zero, worse (positive) to the right."""
    if not bars:
        return "<p>No ablation: there is no \"full\" config on this split.</p>"
    h = 30 + 26 * len(bars)
    lo, hi = min(0.0, *(d for _, d in bars)), max(0.0, *(d for _, d in bars))
    left = 150
    sx = _scale(lo, hi if hi > lo else lo + 1, left, w - 60)
    parts = [f'<line x1="{sx(0):.1f}" y1="6" x2="{sx(0):.1f}" y2="{h - 6}" class="axis"/>']
    for i, (name, d) in enumerate(sorted(bars, key=lambda b: -b[1])):
        y = 14 + 26 * i
        x0, x1 = sorted((sx(0), sx(d)))
        color = "#dc2626" if d > 0 else "#059669"
        parts.append(f'<text x="{left - 6}" y="{y + 12}" class="tick" text-anchor="end">{html.escape(name)}</text>')
        parts.append(f'<rect x="{x0:.1f}" y="{y}" width="{max(x1 - x0, 1):.1f}" height="16" fill="{color}"/>')
        parts.append(f'<text x="{x1 + 4:.1f}" y="{y + 12}" class="tick">{d:+.2f} m</text>')
    return f'<svg viewBox="0 0 {w} {h}" role="img">{"".join(parts)}</svg>'


def svg_walk(series, w=640, h=220, pad=46):
    """series: [(config, [(time s, error m)])] for one walk -> error over time, one line per config."""
    pts = [p for _, s in series for p in s if math.isfinite(p[0]) and math.isfinite(p[1])]
    tmax = max([p[0] for p in pts] + [1.0])
    emax = max([p[1] for p in pts] + [1.0])
    sx, sy = _scale(0, tmax, pad, w - 10), _scale(0, emax, h - pad, 10)
    parts = _axes(w, h, pad, tmax, emax, "time (s)", "error (m)")
    for i, (name, s) in enumerate(series):
        good = sorted(p for p in s if math.isfinite(p[0]) and math.isfinite(p[1]))
        if good:
            path = " ".join(f"{sx(t):.1f},{sy(e):.1f}" for t, e in good)
            parts.append(f'<polyline points="{path}" fill="none" stroke="{PALETTE[i % len(PALETTE)]}" '
                         f'stroke-width="1.6"/>')
    parts += _legend([n for n, _ in series], w - 170, 24)
    return f'<svg viewBox="0 0 {w} {h}" role="img">{"".join(parts)}</svg>'


def fmt(v):
    return "–" if not math.isfinite(v) else f"{v:.2f}"


def render(rows):
    stats = table(rows)
    configs = configs_of(rows)
    splits = sorted({r["split"] for r in rows}, key=lambda s: (s != "test", s))
    main = "test" if "test" in splits else splits[0]
    walks = sorted({(r["split"], r["walk"]) for r in rows}, key=lambda w: (w[0] != main, w))

    body = ["<h1>Positioning evaluation</h1>",
            "<p class=note><b>Method.</b> Each row is a surveyed checkpoint on a recorded walk; the error is the "
            "horizontal distance in metres between the filter's estimate and the checkpoint, as in ISO/IEC "
            "18305:2016 and the IPIN competitions. Results are replays of on-phone logs through the same filter "
            "code the app runs, one config at a time. Walks are split before tuning: <i>tune</i> walks set the "
            "parameters, <i>test</i> walks are only scored. Metrics: n, median, the 75th percentile (IPIN's "
            f"headline figure, in bold), 95th, max and mean; bracketed ranges are percentile-bootstrap 95% "
            f"intervals ({BOOTSTRAP} resamples, seed {SEED}). Checkpoints with no estimate count as lost and are "
            "left out of the error figures.</p>",
            "<h2>Metrics</h2><table><tr><th>config</th><th>split</th><th>n</th><th>lost</th><th>median</th>"
            "<th>75th</th><th>95th</th><th>max</th><th>mean</th></tr>"]
    for split in splits:
        for c in configs:
            m = stats.get((c, split))
            if not m:
                continue
            body.append(
                f"<tr><td>{html.escape(c)}</td><td>{html.escape(split)}</td><td>{m['n']}</td><td>{m['lost']}</td>"
                f"<td>{fmt(m['median'])} <span class=ci>[{fmt(m['ci_median'][0])}, {fmt(m['ci_median'][1])}]</span></td>"
                f"<td><b>{fmt(m['p75'])}</b> <span class=ci>[{fmt(m['ci_p75'][0])}, {fmt(m['ci_p75'][1])}]</span></td>"
                f"<td>{fmt(m['p95'])}</td><td>{fmt(m['max'])}</td><td>{fmt(m['mean'])}</td></tr>")
    body.append("</table><p class=note>Errors in metres.</p>")

    body.append(f"<h2>Error CDF ({html.escape(main)} split)</h2>")
    body.append(svg_cdf([(c, [r["error_m"] for r in rows if r["config"] == c and r["split"] == main])
                         for c in configs]))
    body.append(f"<h2>Ablation ({html.escape(main)} split)</h2><p class=note>Change in the 75th-percentile error "
                "against \"full\" when each signal is taken out; right (red) is worse.</p>")
    body.append(svg_bars(ablation(stats, main)))
    body.append("<h2>Per walk</h2>")
    for split, walk in walks:
        series = [(c, [(r["time_s"], r["error_m"]) for r in rows
                       if r["walk"] == walk and r["split"] == split and r["config"] == c]) for c in configs]
        series = [s for s in series if s[1]]
        body.append(f"<h3>{html.escape(walk)} <small>({html.escape(split)})</small></h3>{svg_walk(series)}")

    style = ("body{font:14px/1.45 system-ui,sans-serif;margin:24px auto;max-width:720px;padding:0 16px;"
             "color:#1f2937;background:#fff}table{border-collapse:collapse;width:100%;font-size:13px}"
             "th,td{border-bottom:1px solid #e5e7eb;padding:4px 6px;text-align:right}th:first-child,"
             "td:first-child,th:nth-child(2),td:nth-child(2){text-align:left}.ci{color:#6b7280;font-size:11px}"
             ".note{color:#4b5563}svg{width:100%;height:auto}.axis{stroke:#6b7280}.grid{stroke:#e5e7eb}"
             ".tick{font-size:10px;fill:#4b5563}.label{font-size:11px;fill:#374151}"
             "@media (prefers-color-scheme:dark){body{background:#111827;color:#e5e7eb}.note,.ci{color:#9ca3af}"
             "th,td{border-color:#374151}.tick,.label{fill:#d1d5db}.grid{stroke:#374151}}")
    return (f"<!doctype html><html><head><meta charset=utf-8><meta name=viewport "
            f"content='width=device-width,initial-scale=1'><title>Positioning evaluation</title><style>{style}"
            f"</style></head><body>{''.join(body)}</body></html>"), stats


def summary_text(stats):
    lines = [f"{'config':<20}{'split':<7}{'n':>5}{'median':>9}{'p75':>9}{'p95':>9}{'max':>9}{'mean':>9}"
             "   p75 95% CI"]
    for (c, s), m in stats.items():
        lines.append(f"{c:<20}{s:<7}{m['n']:>5}{fmt(m['median']):>9}{fmt(m['p75']):>9}{fmt(m['p95']):>9}"
                     f"{fmt(m['max']):>9}{fmt(m['mean']):>9}   [{fmt(m['ci_p75'][0])}, {fmt(m['ci_p75'][1])}]")
    return "\n".join(lines)


def main(argv):
    src = argv[1] if len(argv) > 1 else RESULTS
    out = argv[2] if len(argv) > 2 else os.path.join(os.path.dirname(src), "report.html")
    if not os.path.exists(src):
        sys.exit(f"Missing {src}: run the ReplayEvaluator first")
    rows = read_results(src)
    page, stats = render(rows)
    with open(out, "w", encoding="utf-8") as f:
        f.write(page)
    print(summary_text(stats))
    print(f"wrote {out}")


if __name__ == "__main__":
    main(sys.argv)
