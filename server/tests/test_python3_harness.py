# Tests for the execute_python3 harness.
#
# `rhinocode script` returns before the script finishes and relays neither its output
# nor its exit status (measured on Rhino 8.18: a script that raised came back as exit
# code 0, so execute_python3 reported "ok" for crashed scripts). The harness fixes that
# by capturing everything into a result file. It is plain CPython, so it is tested here
# by running it with this interpreter - no Rhino required.

from __future__ import annotations

import asyncio
import json
import subprocess
import sys
import time

import pytest

from rhino_architect.server import (
    _PY3_OUTPUT_CAP,
    _await_result_file,
    _cap_output,
    _py3_harness,
)


def run_harness(tmp_path, code: str) -> dict:
    user = tmp_path / "script.py"
    result = tmp_path / "result.json"
    runner = tmp_path / "run.py"
    user.write_text(code, encoding="utf-8")
    runner.write_text(_py3_harness(str(user), str(result)), encoding="utf-8")
    subprocess.run([sys.executable, str(runner)], check=True, timeout=30)
    return json.loads(result.read_text(encoding="utf-8"))


def test_print_output_is_captured(tmp_path):
    rec = run_harness(tmp_path, "print('hello', 6 * 7)\n")
    assert rec["ok"] is True
    assert rec["stdout"] == "hello 42\n"


def test_stderr_is_captured_separately(tmp_path):
    rec = run_harness(tmp_path, "import sys\nsys.stderr.write('warn\\n')\nprint('out')\n")
    assert rec["stdout"] == "out\n"
    assert rec["stderr"] == "warn\n"


def test_an_exception_is_a_failure_not_a_success(tmp_path):
    """The core bug: a raising script must never be reported as ok."""
    rec = run_harness(tmp_path, "print('partial')\nraise ValueError('BOOM')\n")
    assert rec["ok"] is False
    assert rec["stdout"] == "partial\n"           # output before the raise survives
    assert "ValueError: BOOM" in rec["traceback"]


def test_traceback_starts_in_the_users_file_not_the_harness(tmp_path):
    rec = run_harness(tmp_path, "def f(v):\n    return 1 / v\n\nf(0)\n")
    tb = rec["traceback"]
    assert "script.py\", line 4" in tb and "script.py\", line 2" in tb
    assert "run.py" not in tb, "harness frame leaked into the user's traceback"


def test_syntax_error_is_reported(tmp_path):
    rec = run_harness(tmp_path, "def broken(:\n    pass\n")
    assert rec["ok"] is False
    assert "SyntaxError" in rec["traceback"]


@pytest.mark.parametrize("code, ok", [
    ("import sys\nsys.exit(0)\n", True),
    ("import sys\nsys.exit()\n", True),
    ("import sys\nsys.exit(3)\n", False),
])
def test_sys_exit_codes(tmp_path, code, ok):
    rec = run_harness(tmp_path, code)
    assert rec["ok"] is ok


def test_script_runs_as_main(tmp_path):
    rec = run_harness(tmp_path, "if __name__ == '__main__':\n    print('main')\n")
    assert rec["stdout"] == "main\n"


def test_harness_restores_stdout(tmp_path):
    """Rhino's script server is long-lived; a leaked redirect swallows later output."""
    user = tmp_path / "script.py"
    user.write_text("raise RuntimeError('x')\n", encoding="utf-8")
    harness = _py3_harness(str(user), str(tmp_path / "result.json"))
    before = (sys.stdout, sys.stderr)
    exec(compile(harness, "run.py", "exec"), {"__name__": "__harness__"})
    assert (sys.stdout, sys.stderr) == before


def test_paths_with_quotes_and_spaces_are_embedded_safely(tmp_path):
    d = tmp_path / "it's a dir"
    d.mkdir()
    rec = run_harness(d, "print('ok')\n")
    assert rec["stdout"] == "ok\n"


def test_await_result_file_gives_up_at_the_deadline(tmp_path):
    t0 = time.monotonic()
    got = asyncio.run(_await_result_file(str(tmp_path / "never.json"), t0 + 0.3, poll=0.05))
    assert got is None
    assert time.monotonic() - t0 < 2.0


def test_await_result_file_picks_up_a_late_result(tmp_path):
    path = tmp_path / "result.json"

    async def scenario():
        async def write_later():
            await asyncio.sleep(0.2)
            path.write_text(json.dumps({"ok": True, "stdout": "late"}), encoding="utf-8")
        writer = asyncio.create_task(write_later())
        got = await _await_result_file(str(path), time.monotonic() + 5, poll=0.05)
        await writer
        return got

    assert asyncio.run(scenario())["stdout"] == "late"


def test_output_is_capped():
    text, cut = _cap_output("x" * (_PY3_OUTPUT_CAP + 10))
    assert cut is True
    assert text.endswith("[truncated 10 chars]")
    assert _cap_output("short") == ("short", False)
