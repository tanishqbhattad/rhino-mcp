# Regressions for the v4.14 field report.
#
# The C# fixes (A1 layer counts, A2 by_layer descendants, A3 report_areas) need a live
# Rhino, so they are covered by evals/tasks/10_gothic_cathedral.json rather than here.
# What IS unit-testable is the contract those fixes rely on: selector shapes, the rab
# helpers' IronPython-2 safety, and the schemas that make any of it discoverable.

from __future__ import annotations

import ast
import re
from pathlib import Path

import pytest

SRC = Path(__file__).resolve().parent.parent / "src" / "rhino_architect"
PLUGIN = Path(__file__).resolve().parent.parent.parent / "plugin"


def _cs(name: str) -> str:
    return (PLUGIN / name).read_text(encoding="utf-8", errors="replace")


# --- A1: counts must be keyed by layer INDEX, never by name ------------------

def test_listlayers_uses_index_keyed_counts():
    """Keying by name reported 0 for nested layers and collided on duplicate leaves."""
    src = _cs("CommandHandler.cs")
    start = src.index("JObject ListLayers")
    body = src[start:start + 3000]
    assert "CountsByLayerIndex" in body, "ListLayers must use the index-keyed counts"
    assert "counts.TryGetValue(l.Name" not in body, "name-keyed lookup reintroduced"
    for field in ("full_path", "parent", "depth", "subtree_count"):
        assert f'["{field}"]' in body, f"ListLayers should expose {field}"


def test_snapshot_exposes_counts_by_index():
    src = _cs("SceneSnapshot.cs")
    assert "public Dictionary<int, int> CountsByLayerIndex()" in src


# --- A2: by_layer must include descendants -----------------------------------

def test_by_layer_includes_descendants_and_has_exact_escape_hatch():
    src = _cs("CommandHandler.cs")
    assert "by_layer_exact:" in src, "the exact-match escape hatch is missing"
    snap = _cs("SceneSnapshot.cs")
    sig = "public List<ObjectMeta> ByLayerName(string layerName, bool includeDescendants = true)"
    assert sig in snap, "ByLayerName must default to including descendants"
    body = snap[snap.index(sig):]
    body = body[: body.index("public ", 10)]
    assert "StartsWith(prefix" in body, "descendants are matched by full-path prefix"


# --- A6: rab must not use the default (hidden-skipping) enumerator -----------

def test_rab_counts_hidden_objects():
    src = (SRC / "rab.py").read_text(encoding="utf-8")
    assert "ObjectEnumeratorSettings" in src
    assert "HiddenObjects" in src
    # The bare iteration that under-reported by 21 objects must be gone.
    assert "for o in sc.doc.Objects:" not in src, "bare enumerator skips hidden objects"


# --- A7: the YZ-plane opening workaround must keep its fallback --------------

def test_wall_profile_has_planar_fallback():
    src = (SRC / "rab.py").read_text(encoding="utf-8")
    fn = src[src.index("def wall_profile("):]
    fn = fn[: fn.index("\ndef ")]
    assert "AddInnerProfile" in fn, "should still try the cheap Extrusion path"
    assert "CreatePlanarBreps" in fn, "must fall back when AddInnerProfile refuses"


# --- rab stays IronPython 2 safe ---------------------------------------------

def test_rab_is_ironpython2_safe():
    src = (SRC / "rab.py").read_text(encoding="utf-8")
    tree = ast.parse(src)
    assert not [n for n in ast.walk(tree) if isinstance(n, ast.JoinedStr)], "f-string found"
    bad = [(i + 1) for i, line in enumerate(src.splitlines())
           for ch in line if ord(ch) > 127]
    assert not bad, f"non-ASCII source breaks IronPython 2 import: lines {bad[:5]}"


# --- Schemas stay self-describing --------------------------------------------

@pytest.mark.parametrize("tool", [
    "assert_dimensions", "capture_elevations", "get_operation_result", "report_areas",
])
def test_new_tools_are_registered_and_documented(tool):
    src = (SRC / "server.py").read_text(encoding="utf-8")
    assert f'name="{tool}"' in src, f"{tool} is not registered as a tool"


def test_slow_calls_are_timed_even_without_timing_flag():
    src = (SRC / "server.py").read_text(encoding="utf-8")
    assert "_SLOW_CALL_MS" in src
    assert "slow_call" in src


def test_eval_tasks_are_valid_and_use_known_assertions():
    import json

    evals_dir = Path(__file__).resolve().parent.parent.parent / "evals"
    runner = (evals_dir / "run_evals.py").read_text(encoding="utf-8")
    known = set(re.findall(r'^\s*"(\w+)":\s*a_\w+,', runner, re.MULTILINE))
    assert known, "could not parse the ASSERTIONS table"
    for f in sorted((evals_dir / "tasks").glob("*.json")):
        task = json.loads(f.read_text(encoding="utf-8"))
        assert task["id"] and task["prompt"] and task["assertions"]
        for a in task["assertions"]:
            assert a["type"] in known, f"{f.name}: unknown assertion type {a['type']}"
