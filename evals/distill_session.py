#!/usr/bin/env python3
"""Session distiller: turn a real modelling session into eval and skill material.

Every mutating command is already journaled to the write-ahead log
(%APPDATA%\\AIBridge\\wal\\*.jsonl) as a begin/end pair. That is a complete,
structured record of what an agent actually did in Rhino - what it called, in what
order, what failed, what was slow, and how the scene version progressed. Nothing
extra has to be captured; the sessions you have already run are sitting there.

This turns that record into the two things the project compounds on:

  report  - what the session DID. Command mix, failures grouped by kind, the
            slowest calls, retries and replays. This is the raw material for
            SKILL.md: if the model needed three attempts at the same thing, the
            skill should teach it in one.

  draft   - a runnable eval task JSON, with assertions derived from the LIVE scene
            the session produced (layer counts, per-layer dimensions, watertightness).
            A session that built something worth keeping becomes a regression test
            for it, instead of being thrown away when Rhino closes.

Workflow:
  uv run python evals/distill_session.py list
  uv run python evals/distill_session.py report latest
  uv run python evals/distill_session.py draft latest --id my_task --out evals/tasks/11_my_task.json
  uv run python evals/run_evals.py check my_task --label opus-5

`list` and `report` are pure log analysis and need no Rhino. `draft` reads the live
scene, so Rhino + AIBridge must be running with the session's model still open.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import sys
from collections import Counter
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server" / "src"))

from rhino_architect.protocol import get_connection  # noqa: E402

TASKS_DIR = Path(__file__).resolve().parent / "tasks"

# Commands that only read. A session's *shape* is its mutations; reads are noise
# when you are trying to see what was built.
READ_ONLY = {
    "ping", "query_scene", "list_objects", "get_objects", "list_layers", "get_state",
    "get_scene_diff", "measure_object", "measure_distance", "validate_objects",
    "assert_geometry", "assert_dimensions", "find_unsupported", "section_preview",
    "report_areas", "detect_clashes", "capture_viewport", "capture_inspection_view",
    "thumbnail", "batch_preview", "get_log", "list_commands", "list_operations",
    "get_operation_result", "list_checkpoints", "get_recovery_log", "search_memory",
}


def wal_dir() -> Path:
    base = os.environ.get("APPDATA") or os.path.expanduser("~/.config")
    return Path(base) / "AIBridge" / "wal"


# ── Reading the journal ──────────────────────────────────────────────────


def load_wal(path: Path) -> list[dict]:
    """Parse a WAL file, tolerating a truncated final line (crash mid-write)."""
    entries = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            entries.append(json.loads(line))
        except json.JSONDecodeError:
            # A crash can leave a half-written final record. That is exactly the
            # case this log exists for, so skip it rather than refusing the file.
            continue
    return entries


def pair_operations(entries: list[dict]) -> list[dict]:
    """Fold begin/end records into one operation each.

    An operation with a begin and no end is the interesting case: the session
    died, or the command never returned. Those are kept, flagged unfinished.
    """
    begins: dict[str, dict] = {}
    ops: list[dict] = []
    for e in entries:
        rid = e.get("request_id")
        if not rid:
            continue
        if e.get("phase") == "begin":
            begins[rid] = e
        elif e.get("phase") == "end":
            b = begins.pop(rid, None)
            ops.append(_make_op(b, e))
    for b in begins.values():           # began but never ended
        ops.append(_make_op(b, None))
    ops.sort(key=lambda o: o["ts"] or "")
    return ops


def _parse_ts(raw: str | None) -> datetime | None:
    if not raw:
        return None
    try:
        return datetime.fromisoformat(raw.replace("Z", "+00:00"))
    except ValueError:
        return None


def _make_op(begin: dict | None, end: dict | None) -> dict:
    src = begin or end or {}
    t0 = _parse_ts((begin or {}).get("ts"))
    t1 = _parse_ts((end or {}).get("ts"))
    ms = None
    if t0 and t1:
        ms = round((t1 - t0).total_seconds() * 1000, 1)
    return {
        "request_id": src.get("request_id"),
        "type": src.get("type") or "?",
        "ts": (begin or end or {}).get("ts"),
        "ms": ms,
        "status": (end or {}).get("status") or ("UNFINISHED" if begin else "?"),
        "scene_version": src.get("scene_version"),
        "params": (begin or {}).get("params") or "",
        "finished": end is not None,
    }


# ── report ───────────────────────────────────────────────────────────────


# rab helpers a script called - the single best signal for what the skill should teach.
RAB_CALL = re.compile(r"\brab\.([a-z_][a-z0-9_]*)\s*\(")


def _unescape(params: str) -> str:
    r"""Turn JSON escape sequences back into real whitespace before scanning.

    The WAL journals params as a JSON *string*, so a script's newlines arrive as a
    literal backslash-n. That makes the text read `...)\nrab.slab(`, where "nrab"
    is a single word and the \b in RAB_CALL never matches - every call after the
    first is silently missed. Params are frequently truncated, so json.loads is not
    an option; substituting the three whitespace escapes is enough and cannot fail.
    """
    return params.replace("\\n", "\n").replace("\\r", "\r").replace("\\t", "\t")


def summarize(ops: list[dict]) -> dict:
    mutations = [o for o in ops if o["type"] not in READ_ONLY]
    failures = [o for o in ops if o["status"] not in ("ok", "UNFINISHED", "?")]
    unfinished = [o for o in ops if not o["finished"]]
    timed = [o for o in ops if o["ms"] is not None]

    rab_calls: Counter = Counter()
    truncated = 0
    with_params = 0
    for o in ops:
        p = o["params"] or ""
        if p:
            with_params += 1
            if p.rstrip().endswith("..."):
                truncated += 1
        for m in RAB_CALL.finditer(_unescape(p)):
            rab_calls[m.group(1)] += 1

    # Repeated identical command types back-to-back after a failure are the
    # signature of the model retrying something the skill should have taught.
    retries: Counter = Counter()
    for i, o in enumerate(ops[1:], 1):
        prev = ops[i - 1]
        if prev["status"] not in ("ok", "?") and prev["type"] == o["type"]:
            retries[o["type"]] += 1

    versions = [o["scene_version"] for o in ops if isinstance(o.get("scene_version"), int)]
    return {
        "operations": len(ops),
        "mutations": len(mutations),
        "failures": failures,
        "unfinished": unfinished,
        "by_type": Counter(o["type"] for o in ops),
        "slowest": sorted(timed, key=lambda o: -o["ms"])[:8],
        "total_ms": round(sum(o["ms"] for o in timed), 1),
        "rab_calls": rab_calls,
        "params_truncated": truncated,
        "params_total": with_params,
        "retries": retries,
        "scene_version_from": min(versions) if versions else None,
        "scene_version_to": max(versions) if versions else None,
        "started": ops[0]["ts"] if ops else None,
        "ended": ops[-1]["ts"] if ops else None,
    }


def print_report(path: Path, ops: list[dict]) -> None:
    s = summarize(ops)
    print(f"session   : {path.name}")
    print(f"span      : {s['started']}  ->  {s['ended']}")
    print(f"operations: {s['operations']}  ({s['mutations']} mutating)")
    print(f"busy time : {s['total_ms'] / 1000:.1f}s of command execution")
    if s["scene_version_from"] is not None:
        print(f"scene ver : {s['scene_version_from']} -> {s['scene_version_to']}")
    print()

    print("command mix")
    for t, n in s["by_type"].most_common(12):
        print(f"  {n:4}  {t}")
    print()

    if s["rab_calls"]:
        print("rab helpers used  (what the skill should teach first)")
        for fn, n in s["rab_calls"].most_common(12):
            print(f"  {n:4}  rab.{fn}()")
        print()
    elif s["params_truncated"]:
        # Be explicit rather than printing an empty section. The plugin truncates
        # the params it journals, and for execute_script that budget is spent
        # entirely on the rab bootstrap preamble, so the actual script never
        # reaches the WAL. This is a limit of the source, not of the parser.
        print("rab helpers used  : cannot tell from this log")
        print(f"  {s['params_truncated']} of {s['params_total']} records have truncated params.")
        print("  execute_script bodies are cut off inside the rab bootstrap preamble, so")
        print("  the script itself is never journaled. Widening the WAL params budget (or")
        print("  journaling the post-bootstrap slice) would make this section work.")
        print()

    if s["slowest"]:
        print("slowest calls")
        for o in s["slowest"]:
            print(f"  {o['ms']:9.1f}ms  {o['type']}")
        print()

    if s["failures"]:
        print(f"failures ({len(s['failures'])})  <- skill/tool gaps live here")
        for o in s["failures"][:10]:
            print(f"  {o['ts']}  {o['type']}: {o['status']}")
        print()
    else:
        print("failures  : none\n")

    if s["retries"]:
        print("retried after failure  (one attempt is the goal)")
        for t, n in s["retries"].most_common():
            print(f"  {n:4}x  {t}")
        print()

    if s["unfinished"]:
        print(f"UNFINISHED ({len(s['unfinished'])}) - began with no end record:")
        for o in s["unfinished"][:5]:
            print(f"  {o['ts']}  {o['type']}  request_id={o['request_id']}")
        print("  (a crash, or a command that never returned - recoverable via")
        print("   get_operation_result if the plugin is still up)")
        print()


# ── draft ────────────────────────────────────────────────────────────────


async def call(command: str, params: dict | None = None) -> dict:
    conn = await get_connection()
    resp = await conn.send_command(command, params or {})
    if not resp.ok:
        raise RuntimeError(f"{command} failed: {resp.message}")
    return dict(resp.result)


def _tolerance(scale: float) -> float:
    """1% of the measured scale, in the document's own units.

    Deliberately relative with only a tiny floor. An absolute floor like 1.0 assumes
    millimetres - in a metres document it would allow a whole metre of error on a 3 m
    wall (33%) and the assertion would catch nothing.
    """
    return max(round(abs(scale) * 0.01, 3), 0.001)


async def _measure(layer: str, measure: str) -> float | None:
    try:
        r = await call("assert_dimensions", {
            "targets": [{"selector": f"by_layer:{layer}", "measure": measure}]
        })
    except RuntimeError:
        return None
    rows = r.get("dimensions") or r.get("results") or []
    actual = rows[0].get("actual") if rows else None
    return float(actual) if isinstance(actual, (int, float)) else None


async def build_assertions(min_objects: int) -> list[dict]:
    """Derive assertions from the live scene the session produced.

    Deliberately conservative: only layers that actually hold geometry, exact
    counts (the scene IS the expected answer), and a watertight check on layers
    whose objects are all closed solids. A generated assertion that is wrong is
    worse than one that is missing, because it fails a future correct build.
    """
    layers = await call("list_layers", {})
    entries = layers.get("layers") or []
    assertions: list[dict] = []
    populated = []

    for lay in entries:
        name = lay.get("full_path") or lay.get("name")
        count = lay.get("object_count") or 0
        if not name or count < min_objects:
            continue
        if str(name).lower() == "default":
            continue
        populated.append((name, count))
        assertions.append({
            "type": "layer_count", "layer": name, "min": count, "max": count,
        })

    # Per populated layer: its vertical EXTENT (height) and its POSITION (top_z).
    # Height alone is not enough - measured live, a roof slab dropped from z=8000 to
    # the ground kept its 250 height and passed every drafted assertion. Wrong
    # elevation is the classic parametric slip (a doubled base height, a plane
    # origin off by a storey), and it is exactly what this project exists to catch.
    for name, _count in populated[:6]:
        height = await _measure(name, "height")
        if height is None or height <= 0:
            continue
        assertions.append({
            "type": "dimension", "selector": f"by_layer:{name}",
            "measure": "height", "target": round(height, 3), "tol": _tolerance(height),
        })
        top = await _measure(name, "top_z")
        if top is None:
            continue
        # Elevation can legitimately be 0 or negative, so the tolerance scales with
        # whichever is larger: how high it sits or how tall it is.
        assertions.append({
            "type": "dimension", "selector": f"by_layer:{name}",
            "measure": "top_z", "target": round(top, 3),
            "tol": _tolerance(max(abs(top), height)),
        })

    # Watertightness only where it already holds - see the docstring.
    for name, _count in populated[:6]:
        try:
            r = await call("assert_geometry", {"assertions": [
                {"kind": "watertight", "selector": f"by_layer:{name}"}
            ]})
        except RuntimeError:
            continue
        got = (r.get("assertions") or [{}])[0]
        if got.get("pass"):
            assertions.append({"type": "watertight", "selector": f"by_layer:{name}"})

    assertions.append({"type": "no_default_layer_geometry", "layer": "Default"})
    return assertions


def draft_prompt(ops: list[dict], summary: dict) -> str:
    """Scaffold a prompt from the session, to be edited by a human.

    We do NOT try to reconstruct intent from code - the script text says how, not
    why, and a prompt that encodes the how defeats the point of the eval.
    """
    helpers = ", ".join(f"rab.{fn}()" for fn, _ in summary["rab_calls"].most_common(8))
    return (
        "TODO: describe the building in the language a person would use - the brief, "
        "not the steps. The assertions below were measured from the scene this "
        "session actually produced, so they already encode the expected answer.\n\n"
        f"Session shape: {summary['mutations']} mutating operations, "
        f"{summary['total_ms'] / 1000:.0f}s of command time"
        + (f"; leaned on {helpers}" if helpers else "")
        + ".\n\nWork in phases, one heavy call per turn. Verify as you go."
    )


async def do_draft(path: Path, ops: list[dict], task_id: str, out: Path | None,
                   min_objects: int) -> int:
    summary = summarize(ops)
    print(f"reading the live scene to derive assertions (session: {path.name})...")
    assertions = await build_assertions(min_objects)
    counts = sum(1 for a in assertions if a["type"] == "layer_count")
    if counts == 0:
        print()
        print("No populated layers found in the live scene.")
        print("A draft needs the session's model still OPEN in Rhino - assertions are")
        print("measured from it, not reconstructed from the log. Reopen the .3dm and retry.")
        return 1

    task = {
        "id": task_id,
        "title": f"TODO: title - distilled from {path.name}",
        "prompt": draft_prompt(ops, summary),
        "assertions": assertions,
        "notes": (
            f"Distilled from {path.name} by evals/distill_session.py. "
            f"Assertions measured from the live scene ({counts} populated layers). "
            "REVIEW BEFORE COMMITTING: exact counts and measured dimensions encode this "
            "one build, so loosen any tolerance that a legitimately different-but-correct "
            "solution would fail, and replace the TODO prompt with a real brief."
        ),
    }

    text = json.dumps(task, indent=2) + "\n"
    if out:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(text, encoding="utf-8")
        print(f"wrote {out}")
        print(f"  {len(assertions)} assertions ({counts} layer_count, "
              f"{sum(1 for a in assertions if a['type'] == 'dimension')} dimension, "
              f"{sum(1 for a in assertions if a['type'] == 'watertight')} watertight)")
        print()
        print("Next: edit the prompt and title, then")
        print(f"  uv run python evals/run_evals.py check {task_id} --label <model>")
    else:
        print(text)
    return 0


# ── CLI ──────────────────────────────────────────────────────────────────


def find_sessions() -> list[Path]:
    d = wal_dir()
    if not d.is_dir():
        return []
    return sorted(d.glob("wal_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)


def resolve_session(which: str) -> Path | None:
    sessions = find_sessions()
    if not sessions:
        return None
    if which in ("latest", "", None):
        return sessions[0]
    p = Path(which)
    if p.is_file():
        return p
    for s in sessions:
        if s.name == which or s.stem == which:
            return s
    return None


def cmd_list() -> int:
    sessions = find_sessions()
    if not sessions:
        print(f"No WAL sessions found in {wal_dir()}")
        print("The WAL is written by the plugin as it executes mutating commands -")
        print("run something in Rhino through the bridge first.")
        return 1
    print(f"{'session':34} {'ops':>5} {'mut':>5} {'fail':>5}  span")
    for s in sessions[:20]:
        ops = pair_operations(load_wal(s))
        if not ops:
            print(f"{s.name:34} {'-':>5} {'-':>5} {'-':>5}  (empty)")
            continue
        summary = summarize(ops)
        span = f"{(summary['started'] or '?')[:19]} -> {(summary['ended'] or '?')[11:19]}"
        print(f"{s.name:34} {summary['operations']:5} {summary['mutations']:5} "
              f"{len(summary['failures']):5}  {span}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["list", "report", "draft"])
    ap.add_argument("session", nargs="?", default="latest",
                    help="WAL filename, path, or 'latest' (default)")
    ap.add_argument("--id", default=None, help="task id for draft")
    ap.add_argument("--out", default=None, help="write the drafted task JSON here")
    ap.add_argument("--min-objects", type=int, default=1,
                    help="ignore layers holding fewer than this many objects (default 1)")
    args = ap.parse_args()

    if args.cmd == "list":
        return cmd_list()

    path = resolve_session(args.session)
    if path is None:
        print(f"No such session: {args.session}   (try: distill_session.py list)")
        return 1

    ops = pair_operations(load_wal(path))
    if not ops:
        print(f"{path.name} has no complete operations.")
        return 1

    if args.cmd == "report":
        print_report(path, ops)
        return 0

    task_id = args.id or re.sub(r"[^a-z0-9_]", "_", path.stem.lower())
    out = Path(args.out) if args.out else None
    if out is None and args.id:
        out = TASKS_DIR / f"{task_id}.json"
    return asyncio.run(do_draft(path, ops, task_id, out, args.min_objects))


if __name__ == "__main__":
    sys.exit(main())
