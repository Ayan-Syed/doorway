"""Tools/databricks/doorway_logs.py on small logs written as PoseLog.cs writes them, with known answers."""
import math
import os
import sys

import pytest

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "databricks")))

import doorway_logs as dl  # noqa: E402

# A walk from door node d113 to Room 121: 4 frames a second for 3 s, walking 1 m/s along x; tracking lost for the
# last second; 4 m off the route from t = 2; one sign read and used, one not; a realign, then arrived.
WALK = ["# Doorway walk from d113 to Room 121, 1.4.0", "b,0.000,90.00,0.000,0.000,1.000,2.000,30.000,2.000"]
for i in range(13):
    t = i * 0.25
    tracking = 0 if t >= 2.0 else 1
    off = 4.0 if t >= 2.0 else 0.5
    WALK.append(f"f,{t:.3f},4,{tracking},0,1.4,0,0,0,0,1,{1 + t + 0.5:.3f},2.000,90.0,{1 + t:.3f},2.000,90.0,"
                f"1.50,0.80,{t:.2f},{off:.2f},0.000")
WALK += ["s,1.100,121,12.00,4.500,0.300,1,0,1.4,0,0.2,0,0.9,4.600,0.400",
         "s,1.600,12l,40.00,9.000,1.200,0,0,1.4,0,0.6,0,0.7,9.100,1.500",
         "e,2.500,realign tracking lost",
         "e,3.000,arrived"]

# An indoor session: snapped at d113, estimates twice a second for 4 s, Localized for the first 3 s then Lost.
# Two checkpoints: at t = 1.0 the estimate was 3-4-5 (5 m) off the door; at t = 3.9, 1 m off.
INDOOR = ["# Doorway indoors, Business Administration I Floor 1, 1.4.0",
          "n,0.000,d113,1.000,2.000,90.00,0.000,0.000,1,5.000,5.000,180.00"]
for i in range(9):
    t = i * 0.5
    state = 2 if t < 3.0 else 3
    x, z = (4.0, 6.0) if t < 3.0 else (10.0, 2.0)
    INDOOR.append(f"r,{t:.3f},{state},{x:.3f},{z:.3f},90.0,1.20,0.70,300,12,2.50")
    INDOOR.append(f"a,{t:.3f},1,0,1.4,0,90.0")
INDOOR += ["c,1.000,d110,1.000,2.000",
           "c,3.900,d121,11.000,2.000",
           "d,1.200,depth,3,0.10,0.20,0.30,0.40,0.50,0.60",
           "s,2.000,121,0,0,12.00,4.500,0.300,0.25,38,1400,4.600",
           "e,3.000,state Lost walls disagree",
           "z,3.100,a row from a newer app"]


def test_walk_header_and_rows():
    log = dl.parse("\n".join(WALK), "/sdcard/walks/20261010-031500.csv")
    assert (log.name, log.kind, log.version) == ("20261010-031500", dl.WALK, "1.4.0")
    assert (log.origin, log.destination) == ("d113", "Room 121")
    assert len(log.rows["f"]) == 13
    f = log.rows["f"][4]
    assert f["t"] == 1.0 and f["x"] == 2.0 and f["rawx"] == 2.5 and f["offroute"] == 0.5
    sign = log.rows["s"][1]
    assert sign["text"] == "12l" and sign["used"] == 0 and sign["dsigma"] == 1.5


def test_walk_summary():
    s = dl.summary(dl.parse("\n".join(WALK), "w"))
    assert s["duration_s"] == pytest.approx(3.0)
    assert s["walked_m"] == pytest.approx(3.0)
    # Frames from t = 0 to 3 in 0.25 s steps: tracking for [0, 2), off the route for [2, 3).
    assert s["tracking_share"] == pytest.approx(2 / 3)
    assert s["off_route_share"] == pytest.approx(1 / 3)
    assert s["raw_gap_median_m"] == pytest.approx(0.5)
    assert s["spread_median_m"] == pytest.approx(1.5)
    assert (s["signs_read"], s["signs_used"], s["realigns"], s["arrived"]) == (2, 1, 1, True)
    assert s["checkpoints"] == 0 and math.isnan(s["checkpoint_error_median_m"])


def test_indoor_rows_by_their_own_columns():
    log = dl.parse("\n".join(INDOOR), "i")
    assert log.kind == dl.INDOORS and log.place == "Business Administration I Floor 1"
    assert log.rows["n"][0]["door"] == "d113" and log.rows["n"][0]["extra"] == 3   # one alternative: x, z, yaw
    assert log.rows["d"][0]["points"] == 3 and log.rows["d"][0]["extra"] == 6
    assert log.rows["s"][0]["focalpixels"] == 1400   # 's' reads as a plate read here, not as a walk's sighting
    assert log.rows["r"][0]["ms"] == 2.5
    assert log.unknown == 1


def test_checkpoint_errors():
    errors = dl.checkpoint_errors(dl.parse("\n".join(INDOOR), "i"))
    assert [e["door"] for e in errors] == ["d110", "d121"]
    assert errors[0]["error_m"] == pytest.approx(5.0) and errors[0]["state"] == "Localized"
    assert errors[1]["error_m"] == pytest.approx(1.0) and errors[1]["state"] == "Lost"


def test_indoor_summary():
    s = dl.summary(dl.parse("\n".join(INDOOR), "i"))
    assert s["localized_share"] == pytest.approx(3 / 4)
    assert s["lost_share"] == pytest.approx(1 / 4)
    assert s["checkpoints"] == 2
    assert s["checkpoint_error_median_m"] == pytest.approx(3.0)
    assert s["checkpoint_error_p75_m"] == pytest.approx(4.0)
    assert s["checkpoint_error_max_m"] == pytest.approx(5.0)
    assert s["tracking_share"] == pytest.approx(1.0)


def test_a_checkpoint_with_no_recent_estimate_has_no_error():
    log = dl.parse("\n".join(INDOOR[:2] + ["r,0.000,2,1,2,0,1,1,300,0,1", "c,9.000,d110,1.000,2.000"]), "i")
    (only,) = dl.checkpoint_errors(log)
    assert math.isnan(only["error_m"]) and only["state"] == ""


def test_not_a_doorway_log():
    with pytest.raises(ValueError):
        dl.parse("walk,t,x\n1,2,3", "other.csv")


def test_sync_reads_adb_devices_and_names_phones():
    import sync
    ready, waiting = sync.parse_devices("List of devices attached\nR5CT1\tdevice\nemulator-5554\toffline\n"
                                        "9B2X\tunauthorized\n\n")
    assert ready == ["R5CT1"] and waiting == [("emulator-5554", "offline"), ("9B2X", "unauthorized")]
    name = sync.phone_name("SM-G991U (Galaxy S21)", "R5CT1")
    assert name.startswith("SM-G991U-Galaxy-S21-") and len(name) == len("SM-G991U-Galaxy-S21-") + 4
    assert sync.phone_name("", "x") .startswith("phone-") and sync.phone_name("A", "1") != sync.phone_name("A", "2")


def test_sync_pulls_keeps_and_uploads_only_what_changed(tmp_path, monkeypatch):
    import types
    import sync
    phone = tmp_path / "phone"
    phone.mkdir()
    (phone / "20261010-031500.csv").write_text("\n".join(WALK) + "\n", encoding="utf-8")

    def fake_adb(path, *args, serial=None):
        if args[0] == "devices":
            return types.SimpleNamespace(returncode=0, stdout="List of devices attached\nR5CT1\tdevice\n", stderr="")
        if args[0] == "shell":
            return types.SimpleNamespace(returncode=0, stdout="Pixel 7\n", stderr="")
        dest = os.path.join(args[2], "walks")   # adb pull <remote> <dir> makes <dir>/walks
        os.makedirs(dest)
        for f in phone.iterdir():
            open(os.path.join(dest, f.name), "w", encoding="utf-8").write(f.read_text(encoding="utf-8"))
        return types.SimpleNamespace(returncode=0, stdout="", stderr="")

    sent = []
    monkeypatch.setattr(sync, "adb", fake_adb)
    monkeypatch.setattr(sync, "ARCHIVE", str(tmp_path / "Walks"))
    monkeypatch.setattr(sync.upload, "send", lambda plan, *a, **k: sent.append(plan))
    args = types.SimpleNamespace(serial=None, results=None, dry_run=False, profile=None,
                                 catalog="workspace", schema="doorway", volume="logs")
    manifest = str(tmp_path / "Walks" / "uploaded.json")
    name = sync.phone_name("Pixel 7", "R5CT1")

    assert sync.sync_once(args, "adb", manifest) == 1
    assert sent[0][0][1] == f"/Volumes/workspace/doorway/logs/walks/{name}/20261010-031500.csv"
    assert (tmp_path / "Walks" / name / "20261010-031500.csv").is_file()
    assert sync.sync_once(args, "adb", manifest) == 0                   # nothing changed: nothing goes up
    (phone / "20261010-031500.csv").write_text("\n".join(WALK + ["e,4.000,arrived"]) + "\n", encoding="utf-8")
    (phone / "20261010-032000.csv").write_text("\n".join(INDOOR) + "\n", encoding="utf-8")
    assert sync.sync_once(args, "adb", manifest) == 2                   # the grown walk and the new session
    (phone / "20261010-031500.csv").unlink()                            # the phone dropped its oldest log...
    assert sync.sync_once(args, "adb", manifest) == 0
    assert (tmp_path / "Walks" / name / "20261010-031500.csv").is_file()   # ...the computer kept it


def test_upload_takes_only_doorway_logs(tmp_path, capsys):
    import upload   # signs in to nothing on a dry run, so it needs no databricks-sdk here
    (tmp_path / "20261010-031500.csv").write_text("\n".join(WALK) + "\n", encoding="utf-8")
    (tmp_path / "20261010-032000.csv").write_text("\n".join(INDOOR) + "\n", encoding="utf-8")
    (tmp_path / "notes.csv").write_text("a,b\n1,2\n", encoding="utf-8")
    found, others = upload.walk_files([str(tmp_path)])
    assert [os.path.basename(p) for p in found] == ["20261010-031500.csv", "20261010-032000.csv"]
    assert [os.path.basename(p) for p, _ in others] == ["notes.csv"]
    assert upload.main([str(tmp_path), "--dry-run", "--schema", "s", "--volume", "v"]) == 0
    assert "/Volumes/workspace/s/v/walks/20261010-031500.csv" in capsys.readouterr().out
