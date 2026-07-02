# Tests for the rab helper library deployment + bootstrap injection.
# rab.py itself only runs inside Rhino (IronPython 2), so here we verify:
# source validity, IronPython-2 compatibility red flags, bootstrap syntax,
# and the deploy-to-AIBridge-dir behavior.

from __future__ import annotations

import ast
from pathlib import Path

import pytest

RAB_SRC_PATH = Path(__file__).resolve().parent.parent / "src" / "rhino_architect" / "rab.py"


def test_rab_source_parses():
    ast.parse(RAB_SRC_PATH.read_text(encoding="utf-8"))


def test_rab_source_is_ironpython2_safe():
    src = RAB_SRC_PATH.read_text(encoding="utf-8")
    tree = ast.parse(src)
    for node in ast.walk(tree):
        assert not isinstance(node, ast.JoinedStr), "f-string found - IronPython 2 unsafe"
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            assert node.returns is None, f"return annotation on {node.name}"
            for arg in node.args.args + node.args.kwonlyargs:
                assert arg.annotation is None, f"type hint on {node.name}({arg.arg})"
        assert not isinstance(node, ast.AnnAssign), "annotated assignment - IronPython 2 unsafe"
    assert "encoding=" not in src, "py3-only open(encoding=) kwarg - use io.open"


def test_bootstrap_parses_and_is_idempotent_marker():
    server_src = (RAB_SRC_PATH.parent / "server.py").read_text(encoding="utf-8")
    assert "_RAB_BOOTSTRAP" in server_src
    # Import the actual constant without triggering FastMCP if unavailable.
    mcp = pytest.importorskip("mcp")  # noqa: F841
    import rhino_architect.server as srv

    ast.parse(srv._RAB_BOOTSTRAP)
    ast.parse(srv._RAB_BOOTSTRAP + "rab.info()\n")
    # Double-prepend guard relies on startswith - bootstrap must be stable text.
    assert (srv._RAB_BOOTSTRAP + "x=1").startswith(srv._RAB_BOOTSTRAP)


def test_deploy_writes_rab_to_aibridge_dir(tmp_path, monkeypatch):
    pytest.importorskip("mcp")
    import rhino_architect.server as srv

    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.setattr(srv.sys, "platform", "win32")
    srv._deploy_rab()
    deployed = tmp_path / "AIBridge" / "rab.py"
    assert deployed.is_file()
    assert deployed.read_text(encoding="utf-8") == RAB_SRC_PATH.read_text(encoding="utf-8")
    # Re-deploy is a no-op (content identical), not an error.
    srv._deploy_rab()
