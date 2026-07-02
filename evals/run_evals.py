#!/usr/bin/env python3
"""Eval harness for rhino-mcp: scores what an AI agent actually built in Rhino.

Workflow (semi-automated by design - the agent half runs in your MCP client):
  1. uv run python evals/run_evals.py list            # see tasks
  2. uv run python evals/run_evals.py prompt <id>     # copy the prompt into Claude/Codex
  3.    ... let the agent model it in Rhino ...
  4. uv run python evals/run_evals.py check <id>      # score the live scene
  5. uv run python evals/run_evals.py reset           # clear the scene for the next task

Requires Rhino 8 + AIBridge running. Uses the same protocol layer as the MCP
server, so scoring is exact (no screenshots, no vibes).

Results are appended to evals/results.jsonl with a timestamp and a `label` you
can set via --label (e.g. the model name) so runs are comparable across models
and across tool/skill changes.
"""
from __future__ import annotations

import argparse
import asyncio
import datetime
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server" / "src"))

from rhino_architect.protocol import get_connection  # noqa: E402

TASKS_DIR = Path(__file__).resolve().parent / "tasks"
RESULTS = Path(__file__).resolve().parent / "results.jsonl"


# ── Bridge helpers ───────────────────────────────────────────────────────

async def call(command: str, params: dict | None = None) -> dict:
    conn = await get_connection()
    resp = await conn.send_command(command, params or {})
    result = dict(resp.result)
    result.setdefault("status", resp.status)
    if resp.message and "message" not in result:
        result["message"] = resp.message
    return result


async def count_on_layer(layer: str) -> int:
    r = await call("query_scene", {"scope": "objects", "filter": {"layer": layer},
                                   "detail": "ids", "limit": 500})
    ids = r.get("ids") or r.get("objects") or []
    if isinstance(ids, dict):  # columnar
        ids = ids.get("ids", [])
    return r.get("count", len(ids))


# ── Assertion engine ─────────────────────────────────────────────────────
# Each assertion: {"type": ..., **params}. Add new types here as tasks need them.

async def a_layer_count(a: dict) -> tuple[bool, str]:
    n = await count_on_layer(a["layer"])
    lo, hi = a.get("min", 0), a.get("max", 10**9)
    return lo <= n <= hi, f"layer '{a['layer']}' has {n} objects (want {lo}..{hi})"


async def a_level_count(a: dict) -> tuple[bool, str]:
    r = await call("get_level_summary")
    levels = r.get("levels") or []
    n = len(levels) if isinstance(levels, list) else r.get("level_count", -1)
    return n == a["equals"], f"{n} levels detected (want {a['equals']})"


async def a_clash_free(a: dict) -> tuple[bool, str]:
    params = {}
    if a.get("layer"):
        params["layer"] = a["layer"]
    r = await call("detect_clashes", params)
    clashes = r.get("clashes") or r.get("pairs") or []
    overlaps = [c for c in clashes if isinstance(c, dict) and c.get("kind") == "overlap"]
    allowed = a.get("max_overlaps", 0)
    return len(overlaps) <= allowed, f"{len(overlaps)} hard overlaps (allowed {allowed})"


async def a_clashes_at_least(a: dict) -> tuple[bool, str]:
    r = await call("detect_clashes", {})
    clashes = r.get("clashes") or r.get("pairs") or []
    n = len(clashes) if isinstance(clashes, list) else r.get("count", 0)
    return n >= a["min"], f"{n} clashes found (want >= {a['min']})"


async def a_gfa_total(a: dict) -> tuple[bool, str]:
    r = await call("report_areas", {"by": "level"})
    total = r.get("total_area") or r.get("total") or 0
    # report may be in mm^2 - normalize to m^2 when it is implausibly large
    if total > 10**7:
        total = total / 10**6
    lo, hi = a["min_m2"], a["max_m2"]
    return lo <= total <= hi, f"GFA {total:.0f} m2 (want {lo}..{hi})"


async def a_scene_bbox_height(a: dict) -> tuple[bool, str]:
    r = await call("query_scene", {"scope": "summary"})
    bbox = r.get("bbox") or r.get("bounding_box") or {}
    mn, mx = bbox.get("min"), bbox.get("max")
    if not (isinstance(mn, list) and isinstance(mx, list)):
        return False, "no scene bbox available"
    h = mx[2] - mn[2]
    lo, hi = a["min_mm"], a["max_mm"]
    return lo <= h <= hi, f"scene height {h:.0f}mm (want {lo}..{hi})"


async def a_no_default_layer_geometry(a: dict) -> tuple[bool, str]:
    n = await count_on_layer(a.get("layer", "Default"))
    return n == 0, f"{n} objects on the default layer (want 0)"


async def a_section_count(a: dict) -> tuple[bool, str]:
    r = await call("list_sections")
    secs = r.get("sections") or r.get("items") or []
    n = len(secs) if isinstance(secs, list) else r.get("count", 0)
    lo, hi = a.get("min", 0), a.get("max", 10**9)
    return lo <= n <= hi, f"{n} sections/plans defined (want {lo}..{hi})"


ASSERTIONS = {
    "layer_count": a_layer_count,
    "level_count": a_level_count,
    "clash_free": a_clash_free,
    "clashes_at_least": a_clashes_at_least,
    "gfa_total": a_gfa_total,
    "scene_bbox_height": a_scene_bbox_height,
    "no_default_layer_geometry": a_no_default_layer_geometry,
    "section_count": a_section_count,
}


# ── Runner ───────────────────────────────────────────────────────────────

def load_tasks() -> dict[str, dict]:
    tasks = {}
    for f in sorted(TASKS_DIR.glob("*.json")):
        t = json.loads(f.read_text(encoding="utf-8"))
        tasks[t["id"]] = t
    return tasks


async def check(task: dict, label: str) -> dict:
    results = []
    passed = 0
    for a in task["assertions"]:
        fn = ASSERTIONS.get(a["type"])
        if fn is None:
            results.append({"type": a["type"], "ok": False, "detail": "unknown assertion type"})
            continue
        try:
            ok, detail = await fn(a)
        except Exception as e:
            ok, detail = False, f"assertion error: {e}"
        results.append({"type": a["type"], "ok": ok, "detail": detail})
        passed += ok
    record = {
        "ts": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "task": task["id"],
        "label": label,
        "score": f"{passed}/{len(results)}",
        "pass": passed == len(results),
        "assertions": results,
    }
    with RESULTS.open("a", encoding="utf-8") as f:
        f.write(json.dumps(record) + "\n")
    return record


async def reset() -> None:
    r = await call("delete_objects", {"object_ids": ["all"]})
    print(f"reset: deleted {r.get('deleted_count', '?')} objects")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["list", "prompt", "check", "check-all", "reset"])
    ap.add_argument("task_id", nargs="?")
    ap.add_argument("--label", default="unlabeled", help="model / configuration name for results.jsonl")
    args = ap.parse_args()
    tasks = load_tasks()

    if args.cmd == "list":
        for t in tasks.values():
            print(f"  {t['id']:<24} {t['title']}  [{len(t['assertions'])} assertions]")
        return
    if args.cmd == "prompt":
        t = tasks[args.task_id]
        print(t["prompt"])
        return
    if args.cmd == "reset":
        asyncio.run(reset())
        return

    async def _run():
        todo = [tasks[args.task_id]] if args.cmd == "check" else list(tasks.values())
        for t in todo:
            rec = await check(t, args.label)
            mark = "PASS" if rec["pass"] else "FAIL"
            print(f"[{mark}] {t['id']} {rec['score']}")
            for a in rec["assertions"]:
                print(f"     {'ok ' if a['ok'] else 'XX '} {a['detail']}")

    asyncio.run(_run())


if __name__ == "__main__":
    main()
