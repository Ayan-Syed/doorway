"""Puts Doorway's walk logs (and ReplayEvaluator's results, if given) in a Databricks volume, for ingest.py.

  python Tools/databricks/upload.py <walks folder or files...> [--results Builds/Eval/results.csv]
      [--catalog workspace] [--schema doorway] [--volume logs] [--profile DEFAULT] [--dry-run]

The walks folder is what adb pulls off the phone:
  adb pull /sdcard/Android/data/com.ayansyed.doorway/files/walks
Only files whose first line says they're a Doorway log go up, to <volume>/walks/<name>.csv; a file already there is
replaced (names are the walk's start time, so the same walk has the same name). The schema and volume are made if
they're missing. Signs in with the Databricks CLI's profile (databricks auth login), or DATABRICKS_HOST and
DATABRICKS_TOKEN. --dry-run lists what would go up and signs in to nothing.

Needs databricks-sdk (Tools/databricks/requirements.txt); the rest of Tools doesn't.
"""
import argparse
import glob
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import doorway_logs as dl  # noqa: E402


def walk_files(paths):
    """The Doorway logs among these files and folders (folders: their .csv files, not recursively), and the rest."""
    found, others = [], []
    for path in paths:
        candidates = sorted(glob.glob(os.path.join(path, "*.csv"))) if os.path.isdir(path) else [path]
        for candidate in candidates:
            try:
                with open(candidate, encoding="utf-8") as f:
                    dl.parse(f.readline(), candidate)   # the header says what it is
                found.append(candidate)
            except (OSError, ValueError, UnicodeDecodeError) as e:
                others.append((candidate, str(e)))
    return found, others


def ensure_volume(client, catalog, schema, volume):
    from databricks.sdk.errors import AlreadyExists, NotFound, ResourceAlreadyExists
    from databricks.sdk.service.catalog import VolumeType
    try:
        client.volumes.read(f"{catalog}.{schema}.{volume}")
        return
    except NotFound:
        pass
    try:
        client.schemas.create(name=schema, catalog_name=catalog,
                              comment="Doorway's walk logs and positioning evaluation (Tools/databricks)")
    except (AlreadyExists, ResourceAlreadyExists):
        pass
    client.volumes.create(catalog_name=catalog, schema_name=schema, name=volume, volume_type=VolumeType.MANAGED,
                          comment="Doorway logs: walks/ from the phone, eval/ from ReplayEvaluator")
    print(f"Made the volume {catalog}.{schema}.{volume}")


def cli_path():
    """The Databricks CLI, for signing in with its profile: on the PATH, or where winget puts it (a terminal opened
    before it was installed doesn't have it on the PATH yet). None if neither."""
    found = shutil.which("databricks")
    if found:
        return found
    packages = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Packages")
    matches = sorted(glob.glob(os.path.join(packages, "Databricks.DatabricksCLI_*", "databricks.exe")))
    return matches[-1] if matches else None


def send(plan, catalog, schema, volume, profile=None):
    """Uploads each (local file, volume path) in plan, making the schema and volume first if they're missing."""
    from databricks.sdk import WorkspaceClient
    from databricks.sdk.core import Config
    options = {"profile": profile} if profile else {}
    cli = cli_path()
    if cli:
        options["databricks_cli_path"] = cli   # Config takes it; WorkspaceClient itself doesn't
    client = WorkspaceClient(config=Config(**options))
    ensure_volume(client, catalog, schema, volume)
    for local, remote in plan:
        with open(local, "rb") as f:
            client.files.upload(remote, f, overwrite=True)
        print(f"{local} -> {remote}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("paths", nargs="+", help="walk log files, or folders of them")
    parser.add_argument("--results", help="ReplayEvaluator's results.csv, to <volume>/eval/results.csv")
    parser.add_argument("--catalog", default="workspace")
    parser.add_argument("--schema", default="doorway")
    parser.add_argument("--volume", default="logs")
    parser.add_argument("--profile", help="a Databricks CLI profile (default: the CLI's own default)")
    parser.add_argument("--dry-run", action="store_true", help="list what would go up, and upload nothing")
    args = parser.parse_args(argv)

    walks, others = walk_files(args.paths)
    for path, why in others:
        print(f"Not uploading {path}: {why}")
    if args.results and not os.path.isfile(args.results):
        sys.exit(f"No {args.results}")
    root = f"/Volumes/{args.catalog}/{args.schema}/{args.volume}"
    plan = [(path, f"{root}/walks/{os.path.basename(path)}") for path in walks]
    if args.results:
        plan.append((args.results, f"{root}/eval/results.csv"))
    if not plan:
        sys.exit("Nothing to upload")

    if args.dry_run:
        for local, remote in plan:
            print(f"{local} -> {remote}")
        print(f"{len(plan)} files (dry run: nothing uploaded)")
        return 0

    send(plan, args.catalog, args.schema, args.volume, args.profile)
    print(f"Uploaded {len(plan)} files. Run Tools/databricks/ingest.py in the workspace to rebuild the tables.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
