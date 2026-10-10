# Databricks notebook source
# MAGIC %md
# MAGIC # Doorway walks
# MAGIC Rebuilds the Doorway tables from the files in the logs volume (upload.py puts them there):
# MAGIC
# MAGIC | table | one row per |
# MAGIC |---|---|
# MAGIC | `logs` | walk or indoor session: how long, how far, tracking, off the route, Localized and Lost, signs, checkpoint errors |
# MAGIC | `checkpoint_errors` | time the user said "I'm at this door" in an indoor session: metres from the position the app showed |
# MAGIC | `walk_frames` | guided frame of a walk (Navigator) |
# MAGIC | `indoor_estimates` | Localizer estimate of an indoor session |
# MAGIC | `signs` | room-number sign read |
# MAGIC | `events` | arrived, realign, reroute, state change |
# MAGIC | `eval_results`, `eval_metrics` | ReplayEvaluator's checkpoints (Builds/Eval/results.csv), and their ISO/IEC 18305 summary per config and split, as Tools/eval/report.py computes it |
# MAGIC | `problem_squares` | 2 m square of a floor the app's estimates fell in: seconds there, share Lost and Converging, spread |
# MAGIC | `problem_doors` | door with checkpoints: how far off the app was there (median, p75, max), and how often confidently wrong |
# MAGIC
# MAGIC and a map per floor of both, over the floor's picture, in the `maps` volume (not `logs`: the job runs when
# MAGIC files land in `logs`, so writing there would set it off again).
# MAGIC
# MAGIC Every run rebuilds every table from the volume, so it can run again at any time. It reads only what the app
# MAGIC already writes, and changes nothing in the app or its data.
# MAGIC
# MAGIC Run it from a Git folder of the repository, so `doorway_logs.py` (here) and `Tools/eval/report.py` import.

# COMMAND ----------

dbutils.widgets.text("catalog", "workspace", "Catalog")
dbutils.widgets.text("schema", "doorway", "Schema")
dbutils.widgets.text("volume", "logs", "Volume")
catalog = dbutils.widgets.get("catalog")
schema = dbutils.widgets.get("schema")
volume = dbutils.widgets.get("volume")

spark.sql(f"CREATE SCHEMA IF NOT EXISTS `{catalog}`.`{schema}`")
spark.sql(f"CREATE VOLUME IF NOT EXISTS `{catalog}`.`{schema}`.`{volume}`")
root = f"/Volumes/{catalog}/{schema}/{volume}"
print(f"Reading {root}/walks and {root}/eval; writing {catalog}.{schema}.*")

# COMMAND ----------

import glob
import math
import os
import sys

import pandas as pd

import importlib

here = os.getcwd()   # the notebook's folder, in a Git folder
for folder in (here, os.path.normpath(os.path.join(here, "..", "eval"))):
    if folder not in sys.path:
        sys.path.insert(0, folder)
import doorway_logs as dl  # noqa: E402
import problem_map as pm  # noqa: E402
import report  # noqa: E402

# A session keeps modules it has imported: after a pull, Run all would use the old code without these. problem_map
# after doorway_logs, so it picks up the new one.
dl = importlib.reload(dl)
pm = importlib.reload(pm)
report = importlib.reload(report)
print(f"doorway_logs from {dl.__file__}")

logs, skipped = [], []
for path in sorted(glob.glob(f"{root}/walks/**/*.csv", recursive=True)):
    try:
        logs.append(dl.read(path))
    except (ValueError, UnicodeDecodeError) as e:
        skipped.append((os.path.basename(path), str(e)))
print(f"{len(logs)} logs: {sum(l.kind == dl.WALK for l in logs)} walks, {sum(l.kind == dl.INDOORS for l in logs)} indoor sessions")
for name, why in skipped:
    print(f"  skipped {name}: {why}")

# COMMAND ----------

def save(rows, table):
    """Replaces the table with these rows (dicts). NaN stays NaN; no rows leaves the table as it was."""
    name = f"`{catalog}`.`{schema}`.`{table}`"
    if not rows:
        print(f"{table}: no rows, left as it was")
        return
    frame = pd.DataFrame(rows)
    spark.createDataFrame(frame).write.mode("overwrite").option("overwriteSchema", "true").saveAsTable(name)
    print(f"{table}: {len(frame)} rows")


def rows_of(kind, letter, extra=None):
    out = []
    for log in logs:
        if log.kind != kind:
            continue
        for row in log.rows.get(letter, []):
            out.append({"log": log.name, "version": log.version, **(extra(log) if extra else {}), **row})
    return out


save([dl.summary(l) for l in logs], "logs")
save([c for l in logs for c in dl.checkpoint_errors(l)], "checkpoint_errors")
save(rows_of(dl.WALK, "f", lambda l: {"origin": l.origin, "destination": l.destination}), "walk_frames")
save(rows_of(dl.INDOORS, "r", lambda l: {"place": l.place}), "indoor_estimates")
save([{"kind": k, **row} for k in (dl.WALK, dl.INDOORS) for row in rows_of(k, "s")], "signs")
save([{"kind": k, **row} for k in (dl.WALK, dl.INDOORS) for row in rows_of(k, "e")], "events")

# COMMAND ----------

results_path = f"{root}/eval/results.csv"
if os.path.exists(results_path):
    results = report.read_results(results_path)
    save(results, "eval_results")
    groups = {}
    for r in results:
        groups.setdefault((r["config"], r["split"]), []).append(r["error_m"])
    save([{"config": c, "split": s, **report.metrics(e)} for (c, s), e in sorted(groups.items())], "eval_metrics")
else:
    print(f"No {results_path}: eval tables left as they were")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Where it goes wrong: a map per floor
# MAGIC Red squares: the share of the time the app was Lost while its estimate was there (where to look, not proof:
# MAGIC a lost estimate can be anywhere). Circles: the doors people said they were at, labelled with how far off the
# MAGIC app was there (75th percentile), red over 3 m.

# COMMAND ----------

import base64
import re
import shutil
import tempfile

place_squares, place_doors = pm.squares(logs), pm.doors(logs)
save(place_squares, "problem_squares")
save(place_doors, "problem_doors")

spark.sql(f"CREATE VOLUME IF NOT EXISTS `{catalog}`.`{schema}`.`maps`")
maps_root = f"/Volumes/{catalog}/{schema}/maps"
floor_plans = pm.plans(os.path.normpath(os.path.join(here, "..", "..", "Assets", "Navigation", "Data")))
for place in sorted({s["place"] for s in place_squares} | {d["place"] for d in place_doors}):
    plan = floor_plans.get(place)
    if plan is None:
        print(f"No floor plan named {place!r} in Assets/Navigation/Data: no map for it")
        continue
    name = re.sub(r"[^A-Za-z0-9]+", "-", place).strip("-").lower() + ".png"
    with tempfile.TemporaryDirectory() as tmp:
        drawn = pm.draw(place, plan, [s for s in place_squares if s["place"] == place],
                        [d for d in place_doors if d["place"] == place], os.path.join(tmp, name))
        shutil.copyfile(drawn, f"{maps_root}/{name}")
        with open(drawn, "rb") as f:
            displayHTML(f'<img src="data:image/png;base64,{base64.b64encode(f.read()).decode()}" style="max-width:100%">')
    print(f"{place}: {maps_root}/{name}")

# COMMAND ----------

# MAGIC %md
# MAGIC ## The squares where it was lost longest

# COMMAND ----------

display(spark.sql(f"""
  SELECT place, round(x, 1) AS x, round(z, 1) AS z, round(seconds) AS seconds,
         round(lost_share, 2) AS lost_share, round(converging_share, 2) AS converging_share,
         round(spread_median_m, 2) AS spread_m, sessions
  FROM `{catalog}`.`{schema}`.`problem_squares`
  ORDER BY lost_share * seconds DESC LIMIT 20
"""))

# COMMAND ----------

# MAGIC %md
# MAGIC ## Real walks: how far off the app was when people said where they were
# MAGIC The 75th percentile is the headline figure (IPIN's), as in Tools/eval/report.py.

# COMMAND ----------

display(spark.sql(f"""
  SELECT version,
         count(*) AS checkpoints,
         round(percentile(error_m, 0.5), 2) AS median_m,
         round(percentile(error_m, 0.75), 2) AS p75_m,
         round(percentile(error_m, 0.95), 2) AS p95_m,
         round(max(error_m), 2) AS max_m,
         round(avg(CASE WHEN state = 'Localized' AND error_m > 3 THEN 1.0 ELSE 0.0 END), 3) AS confident_and_wrong
  FROM `{catalog}`.`{schema}`.`checkpoint_errors`
  WHERE NOT isnan(error_m)
  GROUP BY version ORDER BY version
"""))

# COMMAND ----------

# MAGIC %md
# MAGIC ## Where people get lost: the doors with the worst checkpoint errors

# COMMAND ----------

display(spark.sql(f"""
  SELECT door, count(*) AS checkpoints, round(percentile(error_m, 0.75), 2) AS p75_m, round(max(error_m), 2) AS max_m
  FROM `{catalog}`.`{schema}`.`checkpoint_errors`
  WHERE NOT isnan(error_m)
  GROUP BY door HAVING count(*) >= 2 ORDER BY p75_m DESC LIMIT 20
"""))

# COMMAND ----------

# MAGIC %md
# MAGIC ## Walks: arrivals, realigns, time off the route and time tracking

# COMMAND ----------

display(spark.sql(f"""
  SELECT version, kind, count(*) AS logs,
         round(avg(CAST(arrived AS INT)), 2) AS arrived_share,
         round(avg(realigns), 2) AS realigns_per_log,
         round(avg(off_route_share), 3) AS off_route_share,
         round(avg(tracking_share), 3) AS tracking_share,
         round(avg(lost_share), 3) AS lost_share
  FROM `{catalog}`.`{schema}`.`logs`
  GROUP BY version, kind ORDER BY version, kind
"""))
