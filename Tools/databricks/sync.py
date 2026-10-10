"""Pulls Doorway's walk logs off every phone plugged in and uploads the new ones to Databricks, in one go.

  python Tools/databricks/sync.py [--profile NAME] [--watch SECONDS] [--dry-run]
      [--serial SERIAL] [--results Builds/Eval/results.csv] [--catalog workspace] [--schema doorway] [--volume logs]

For each phone adb sees (USB debugging on, this computer allowed):
1. adb pull its walks folder (/sdcard/Android/data/com.ayansyed.doorway/files/walks) and keep every log in
   Builds/Walks/<phone>/: the phone keeps only its last 10, the computer keeps them all;
2. upload the logs that are new or changed since the last sync to <volume>/walks/<phone>/ (a log still being
   written changes, so it goes up again whole). Builds/Walks/uploaded.json remembers what went up, and where to.
<phone> is the phone's model and four characters of a hash of its serial ("Pixel-7-3fa2"): a name per phone, so
two teammates' phones never overwrite each other's logs, without the serial itself.

--watch SECONDS runs again every SECONDS until Ctrl+C, so plugging a phone in is all it takes; without it, once.
--dry-run pulls and lists what would go up, and uploads nothing. Then run Tools/databricks/ingest.py in the
workspace (or let its job run it). Nothing here changes the app or the phone: it only reads the phone's logs.

Needs adb (Android platform-tools; Unity's Android support has one) and databricks-sdk (requirements.txt).
"""
import argparse
import contextlib
import glob
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import upload  # noqa: E402

BOX = os.path.normpath(os.path.join(HERE, "..", ".."))
ARCHIVE = os.path.join(BOX, "Builds", "Walks")
PACKAGE = "com.ayansyed.doorway"
REMOTE = f"/sdcard/Android/data/{PACKAGE}/files/walks"   # PoseLog.Folder under the app's files
RESULTS_KEY = "eval/results.csv"                          # --results' entry in the manifest


def find_adb():
    """adb on the PATH, else where the Android SDK and Unity's Android support keep it; None if nowhere."""
    found = shutil.which("adb")
    if found:
        return found
    local = os.environ.get("LOCALAPPDATA", "")
    candidates = [os.path.join(local, "Android", "Sdk", "platform-tools", "adb.exe")]
    candidates += sorted(glob.glob(os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "Unity", "Hub",
                                                "Editor", "*", "Editor", "Data", "PlaybackEngines", "AndroidPlayer",
                                                "SDK", "platform-tools", "adb.exe")), reverse=True)
    return next((c for c in candidates if os.path.isfile(c)), None)


def parse_devices(text):
    """`adb devices`' output -> (ready serials, [(serial, state)] of the rest: unauthorized, offline...)."""
    ready, waiting = [], []
    for line in text.splitlines()[1:]:
        parts = line.split()
        if len(parts) >= 2:
            (ready if parts[1] == "device" else waiting).append((parts[0], parts[1]))
    return [s for s, _ in ready], waiting


def phone_name(model, serial):
    """A folder name per phone: its model, made safe for a path, and 4 hex of a hash of its serial."""
    safe = re.sub(r"[^A-Za-z0-9]+", "-", model or "").strip("-") or "phone"
    return f"{safe}-{hashlib.sha256(serial.encode('utf-8')).hexdigest()[:4]}"


def file_hash(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def load_manifest(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_manifest(path, manifest):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
    os.replace(tmp, path)


def to_upload(paths, manifest, remote_of):
    """The logs among paths whose content or destination differs from what the manifest says went up."""
    out = []
    for path in paths:
        entry = manifest.get(os.path.relpath(path, ARCHIVE).replace(os.sep, "/"))
        if not entry or entry.get("sha256") != file_hash(path) or entry.get("remote") != remote_of(path):
            out.append(path)
    return out


def adb(adb_path, *args, serial=None):
    command = [adb_path] + (["-s", serial] if serial else []) + list(args)
    return subprocess.run(command, capture_output=True, text=True, timeout=120)


def pull(adb_path, serial, folder):
    """The phone's logs into folder (new and changed ones replace what's there); the Doorway logs now in folder."""
    os.makedirs(folder, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        result = adb(adb_path, "pull", REMOTE, tmp, serial=serial)
        if result.returncode != 0:
            message = (result.stderr or result.stdout).strip().splitlines()
            print(f"  nothing pulled: {message[-1] if message else 'adb pull failed'}")
        for src in glob.glob(os.path.join(tmp, "**", "*.csv"), recursive=True):
            shutil.copyfile(src, os.path.join(folder, os.path.basename(src)))
    found, others = upload.walk_files([folder])
    for path, why in others:
        print(f"  not a Doorway log, left out: {os.path.basename(path)} ({why})")
    return found


def sync_once(args, adb_path, manifest_path):
    serials, waiting = parse_devices(adb(adb_path, "devices").stdout)
    for serial, state in waiting:
        print(f"Phone {serial} is {state}: unlock it and allow USB debugging for this computer.")
    if args.serial:
        serials = [s for s in serials if s == args.serial]
    if not serials:
        print("No phone connected.")
        return 0

    root = f"/Volumes/{args.catalog}/{args.schema}/{args.volume}"
    manifest = load_manifest(manifest_path)
    plan = []
    for serial in serials:
        model = adb(adb_path, "shell", "getprop", "ro.product.model", serial=serial).stdout.strip()
        name = phone_name(model, serial)
        print(f"{name}: pulling {REMOTE}")
        logs = pull(adb_path, serial, os.path.join(ARCHIVE, name))
        remote_of = lambda p, name=name: f"{root}/walks/{name}/{os.path.basename(p)}"
        new = to_upload(logs, manifest, remote_of)
        print(f"  {len(logs)} logs kept in Builds/Walks/{name}, {len(new)} new or changed")
        plan += [(p, remote_of(p)) for p in new]
    if args.results:
        if not os.path.isfile(args.results):
            sys.exit(f"No {args.results}")
        entry = manifest.get(RESULTS_KEY, {})
        remote = f"{root}/eval/results.csv"
        if entry.get("sha256") != file_hash(args.results) or entry.get("remote") != remote:
            plan.append((args.results, remote))

    if not plan:
        print("Nothing new to upload.")
        return 0
    if args.dry_run:
        for local, remote in plan:
            print(f"  would upload {os.path.relpath(local, BOX)} -> {remote}")
        print(f"{len(plan)} files (dry run: nothing uploaded)")
        return len(plan)

    upload.send(plan, args.catalog, args.schema, args.volume, args.profile)
    for local, remote in plan:
        key = RESULTS_KEY if remote.endswith("/eval/results.csv") else os.path.relpath(local, ARCHIVE).replace(os.sep, "/")
        manifest[key] = {"sha256": file_hash(local), "remote": remote, "uploaded": time.strftime("%Y-%m-%d %H:%M:%S")}
    save_manifest(manifest_path, manifest)
    print(f"Uploaded {len(plan)} files. Run Tools/databricks/ingest.py in the workspace to rebuild the tables.")
    return len(plan)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--profile", help="the Databricks CLI profile to sign in with (default: DEFAULT)")
    parser.add_argument("--watch", type=float, metavar="SECONDS", help="run again every SECONDS until Ctrl+C")
    parser.add_argument("--dry-run", action="store_true", help="pull and list what would go up; upload nothing")
    parser.add_argument("--serial", help="only this phone (as `adb devices` names it)")
    parser.add_argument("--results", help="ReplayEvaluator's results.csv, to <volume>/eval/results.csv as well")
    parser.add_argument("--catalog", default="workspace")
    parser.add_argument("--schema", default="doorway")
    parser.add_argument("--volume", default="logs")
    args = parser.parse_args(argv)

    adb_path = find_adb()
    if not adb_path:
        sys.exit("No adb: install Android platform-tools, or Unity's Android support, which has one.")
    manifest_path = os.path.join(ARCHIVE, "uploaded.json")
    if not args.watch:
        sync_once(args, adb_path, manifest_path)
        return 0
    print(f"Watching for phones every {args.watch:g} s (Ctrl+C to stop)")
    last = None
    try:
        while True:
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                try:
                    sync_once(args, adb_path, manifest_path)
                except Exception as e:   # a dropped connection or a phone pulled out mid-sync: try next time
                    print(f"Sync failed: {e}")
            # Only what changed: "No phone connected." once, not every few seconds.
            if out.getvalue() != last:
                print(time.strftime("[%H:%M:%S]"), out.getvalue(), end="", flush=True)
                last = out.getvalue()
            time.sleep(args.watch)
    except KeyboardInterrupt:
        print("Stopped.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
