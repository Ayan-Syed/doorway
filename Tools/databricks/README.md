# Doorway on Databricks

Doorway's walk logs and positioning evaluation as tables in Databricks: how far off the app was when people said
where they were, how often it was lost, where along the floor it struggles, and how each app version compares.

Databricks only reads what the app already writes. Nothing here changes the app, its navigation or its data, and
the app needs no network: logs come off the phone with `adb`, as they do for `ReplayEvaluator`.

| file | what it does | runs on |
|---|---|---|
| `doorway_logs.py` | reads a walk log (`PoseLog.cs`'s format) into rows; sums up each log; checkpoint errors | anywhere (standard library) |
| `sync.py` | pulls the logs off every phone plugged in and uploads the new ones (once, or `--watch`) | your computer |
| `upload.py` | puts a folder of walk logs and `Builds/Eval/results.csv` in a Databricks volume | your computer |
| `ingest.py` | a Databricks notebook: rebuilds the tables from the volume, with three summary queries | Databricks |

Tests: `Tools/tests/test_databricks.py` (no Databricks needed).

## Setting up the account (once)

1. **A workspace.** Sign up for Databricks Free Edition (free, no card), or use a workspace you already have. Free
   Edition comes with a catalog named `workspace`; the scripts default to it.
2. **Permission to create a schema** in that catalog (the account's owner has it). The scripts make the
   `doorway` schema and its `logs` volume the first time; `--catalog`, `--schema` and `--volume` choose others.
3. **Sign in from your computer.** Install the Databricks CLI, then run `databricks auth login` and give it your
   workspace's URL. That saves a profile `upload.py` uses. (Or set `DATABRICKS_HOST` and `DATABRICKS_TOKEN`.)
4. **The repository in the workspace.** In Databricks, *Workspace → Create → Git folder*, with the repository's URL.
   It's private, so link your GitHub account first: *Settings → Linked accounts → GitHub*. The notebook imports
   `doorway_logs.py` and `Tools/eval/report.py` from the Git folder.

## Each time

Plug the phone in (USB debugging on) and run, from the repository:

```sh
pip install -r Tools/databricks/requirements.txt           # once
python Tools/databricks/sync.py --dry-run                  # what would go up
python Tools/databricks/sync.py                            # pull every phone plugged in, upload what's new
python Tools/databricks/sync.py --watch 30                 # keep at it: plugging a phone in is all it takes
```

`sync.py` keeps every log in `Builds/Walks/<phone>/` (the phone keeps only its last 10) and uploads only new or
changed ones, to `walks/<phone>/` in the volume, so teammates' phones never overwrite each other. `<phone>` is the
model and a short hash of the serial. Add `--profile <name>` if your CLI profile isn't `DEFAULT`, and
`--results Builds/Eval/results.csv` to send ReplayEvaluator's results too. It finds `adb` on the PATH or in Unity's
Android support.

`upload.py walks` does the same for a folder of logs pulled by hand, or sent by a teammate without the workspace.

Then open `Tools/databricks/ingest.py` in the Git folder and *Run all*. Every run rebuilds every table from the
volume, so run it again whenever new logs go up.

## The tables (in `workspace.doorway`)

| table | one row per |
|---|---|
| `logs` | walk or indoor session: duration, distance, tracking, time off the route, Localized and Lost, signs read, checkpoint errors |
| `checkpoint_errors` | "I'm at this door" in an indoor session: metres between the position shown and the door |
| `walk_frames` | guided frame of a walk |
| `indoor_estimates` | Localizer estimate in an indoor session |
| `signs` | room-number sign read |
| `events` | arrived, realign, reroute, state change |
| `eval_results`, `eval_metrics` | `ReplayEvaluator`'s checkpoints, and their summary per config and split (`Tools/eval/report.py`'s numbers) |

A checkpoint's error is the app's own estimate at the moment of the tap, as the user saw it. That's real-world
accuracy, but only where people tapped: `ReplayEvaluator` stays the way to compare settings fairly on the same walks.

The phone keeps its last 10 logs, so pull them after each test session. The logs say where someone walked in a
building and when; keep the volume to the team.
