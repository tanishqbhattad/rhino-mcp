# Tests for evals/distill_session.py - the session distiller.
#
# All of these run against synthetic WAL content. The distiller's value is that it
# reads logs real sessions already leave behind, so the parsing has to survive what
# those logs actually contain: truncated params, crash-truncated final lines, and
# operations that began but never ended.

from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

EVALS = Path(__file__).resolve().parent.parent.parent / "evals"
sys.path.insert(0, str(EVALS))

distill = pytest.importorskip("distill_session")


def _wal(tmp_path: Path, records: list[dict], name: str = "wal_test.jsonl") -> Path:
    p = tmp_path / name
    p.write_text("\n".join(json.dumps(r) for r in records) + "\n", encoding="utf-8")
    return p


def _op(rid: str, typ: str, t0: str, t1: str | None, status: str = "ok",
        params: str = "", version: int = 1) -> list[dict]:
    begin = {"ts": t0, "phase": "begin", "request_id": rid, "type": typ,
             "scene_version": version, "params": params}
    if t1 is None:
        return [begin]
    return [begin, {"ts": t1, "phase": "end", "request_id": rid, "type": typ,
                    "scene_version": version, "status": status}]


def test_pairs_begin_and_end_into_one_operation_with_duration(tmp_path):
    recs = _op("a", "execute_script",
               "2026-09-22T10:00:00.000000Z", "2026-09-22T10:00:02.500000Z")
    ops = distill.pair_operations(distill.load_wal(_wal(tmp_path, recs)))
    assert len(ops) == 1
    assert ops[0]["type"] == "execute_script"
    assert ops[0]["ms"] == pytest.approx(2500.0, abs=1.0)
    assert ops[0]["finished"] is True


def test_operation_that_began_but_never_ended_is_kept_and_flagged(tmp_path):
    """A crash mid-command is the case the WAL exists for - it must not vanish."""
    recs = _op("a", "execute_script", "2026-09-22T10:00:00Z", "2026-09-22T10:00:01Z")
    recs += _op("b", "boolean_operation", "2026-09-22T10:00:02Z", None)
    ops = distill.pair_operations(distill.load_wal(_wal(tmp_path, recs)))
    assert len(ops) == 2
    unfinished = [o for o in ops if not o["finished"]]
    assert len(unfinished) == 1
    assert unfinished[0]["type"] == "boolean_operation"
    assert unfinished[0]["status"] == "UNFINISHED"
    assert distill.summarize(ops)["unfinished"] == unfinished


def test_truncated_final_line_is_skipped_not_fatal(tmp_path):
    """A crash can leave half a JSON record. Refusing the file would be perverse."""
    p = tmp_path / "wal_trunc.jsonl"
    good = _op("a", "create_object", "2026-09-22T10:00:00Z", "2026-09-22T10:00:01Z")
    text = "\n".join(json.dumps(r) for r in good) + "\n"
    text += '{"ts":"2026-09-22T10:00:02Z","phase":"begin","req'   # cut mid-write
    p.write_text(text, encoding="utf-8")

    ops = distill.pair_operations(distill.load_wal(p))
    assert len(ops) == 1
    assert ops[0]["type"] == "create_object"


def test_failures_and_retries_are_surfaced(tmp_path):
    """Repeat of a just-failed command type is the signature of a skill gap."""
    recs = _op("a", "execute_script", "2026-09-22T10:00:00Z",
               "2026-09-22T10:00:01Z", status="error")
    recs += _op("b", "execute_script", "2026-09-22T10:00:02Z", "2026-09-22T10:00:03Z")
    s = distill.summarize(distill.pair_operations(distill.load_wal(_wal(tmp_path, recs))))
    assert len(s["failures"]) == 1
    assert s["retries"]["execute_script"] == 1


def test_reads_are_not_counted_as_mutations(tmp_path):
    recs = _op("a", "query_scene", "2026-09-22T10:00:00Z", "2026-09-22T10:00:01Z")
    recs += _op("b", "create_object", "2026-09-22T10:00:02Z", "2026-09-22T10:00:03Z")
    s = distill.summarize(distill.pair_operations(distill.load_wal(_wal(tmp_path, recs))))
    assert s["operations"] == 2
    assert s["mutations"] == 1


def test_rab_helpers_are_mined_from_untruncated_params(tmp_path):
    params = json.dumps({"code": "rab.wall(a, b)\nrab.slab(pts)\nrab.wall(c, d)"})
    recs = _op("a", "execute_script", "2026-09-22T10:00:00Z",
               "2026-09-22T10:00:01Z", params=params)
    s = distill.summarize(distill.pair_operations(distill.load_wal(_wal(tmp_path, recs))))
    assert s["rab_calls"]["wall"] == 2
    assert s["rab_calls"]["slab"] == 1
    assert s["params_truncated"] == 0


def test_truncated_params_are_reported_rather_than_silently_empty(tmp_path):
    """The plugin truncates journaled params, so rab mining often CANNOT work.

    An empty 'rab helpers' section would read as 'this session used none', which is
    false. summarize must expose the truncation so the report can say so.
    """
    recs = _op("a", "execute_script", "2026-09-22T10:00:00Z", "2026-09-22T10:00:01Z",
               params='{"code":"import sys as _rabsys, os as _rabos\\n_rabdir = ...')
    s = distill.summarize(distill.pair_operations(distill.load_wal(_wal(tmp_path, recs))))
    assert s["rab_calls"] == {}
    assert s["params_truncated"] == 1
    assert s["params_total"] == 1


def test_scene_version_progression_is_tracked(tmp_path):
    recs = _op("a", "create_object", "2026-09-22T10:00:00Z", "2026-09-22T10:00:01Z",
               version=5)
    recs += _op("b", "create_object", "2026-09-22T10:00:02Z", "2026-09-22T10:00:03Z",
                version=91)
    s = distill.summarize(distill.pair_operations(distill.load_wal(_wal(tmp_path, recs))))
    assert s["scene_version_from"] == 5
    assert s["scene_version_to"] == 91


def test_report_renders_without_raising(tmp_path, capsys):
    recs = _op("a", "execute_script", "2026-09-22T10:00:00Z", "2026-09-22T10:00:04Z")
    recs += _op("b", "set_camera", "2026-09-22T10:00:05Z", "2026-09-22T10:00:05.100000Z")
    path = _wal(tmp_path, recs)
    distill.print_report(path, distill.pair_operations(distill.load_wal(path)))
    out = capsys.readouterr().out
    assert "command mix" in out
    assert "execute_script" in out
    assert "failures  : none" in out


def test_empty_wal_yields_no_operations(tmp_path):
    p = tmp_path / "wal_empty.jsonl"
    p.write_text("", encoding="utf-8")
    assert distill.pair_operations(distill.load_wal(p)) == []


# ── draft tolerances ─────────────────────────────────────────────────────


@pytest.mark.parametrize("scale", [3000.0, 3.0, 0.3])
def test_tolerance_is_one_percent_in_any_unit_system(scale):
    """An absolute floor like 1.0 assumes millimetres: in a metres document it
    allowed a whole metre of error on a 3 m wall (33%), catching nothing."""
    assert distill._tolerance(scale) == pytest.approx(scale * 0.01, abs=1e-3)


def test_tolerance_never_collapses_to_zero():
    assert distill._tolerance(0.0) > 0


def test_draft_asserts_elevation_not_just_extent():
    """Measured live: with height alone, a roof dropped from z=8000 to the ground
    kept its height and passed every drafted assertion. top_z must be drafted too."""
    import inspect
    src = inspect.getsource(distill.build_assertions)
    assert '"top_z"' in src and '"height"' in src
