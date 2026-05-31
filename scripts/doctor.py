"""
RhinoAIBridge — Doctor / health-check script
by tanishqb (https://github.com/tanishqbhattad/rhino-mcp)

Checks every layer of the installation and reports what is OK / broken.

Usage: python doctor.py [--fix]
  --fix   Attempt automatic repairs where possible.

Exit codes: 0 = all OK, 1 = warnings, 2 = critical failures
"""

import json
import os
import re
import shutil
import socket
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent        # repo root
SERVER_DIR = ROOT / "server"
SCRIPTS_DIR = ROOT / "scripts"
PLUGIN_DIR = Path(os.environ.get("APPDATA", "")) / "McNeel" / "Rhinoceros" / "8.0" / "Plug-ins" / "RhinoAIBridge"
CLAUDE_CONFIG = Path(os.environ.get("APPDATA", "")) / "Claude" / "claude_desktop_config.json"
CODEX_CONFIG = Path(os.environ.get("USERPROFILE", Path.home())) / ".codex" / "config.toml"

PASS = "\033[92m✓\033[0m"
FAIL = "\033[91m✗\033[0m"
WARN = "\033[93m!\033[0m"

warnings = 0
failures = 0


def ok(msg):    print(f"  {PASS}  {msg}")
def fail(msg):  global failures;  failures += 1;  print(f"  {FAIL}  {msg}")
def warn(msg):  global warnings;  warnings += 1;  print(f"  {WARN}  {msg}")


def section(title):
    print(f"\n{'─'*60}")
    print(f"  {title}")
    print(f"{'─'*60}")


# ── 1. Plugin file ─────────────────────────────────────────────────────────────
section("Rhino Plugin")
rhp = PLUGIN_DIR / "RhinoAIBridge.rhp"
if rhp.exists():
    size_kb = rhp.stat().st_size // 1024
    ok(f"Plugin installed: {rhp}  ({size_kb} KB)")
    if size_kb < 100:
        warn("Plugin seems small — may be an old or corrupt build. Run INSTALL.bat again.")
else:
    fail(f"Plugin NOT found at: {rhp}")
    print(f"         Run INSTALL.bat to install it.")

# ── 2. Server dependencies ─────────────────────────────────────────────────────
section("Python MCP Server")

uv = shutil.which("uv")
if uv:
    ok(f"uv found: {uv}")
else:
    fail("uv not found in PATH. Install from https://docs.astral.sh/uv/")

pyproject = SERVER_DIR / "pyproject.toml"
if pyproject.exists():
    ok(f"pyproject.toml: {pyproject}")
else:
    fail(f"Server directory not found: {SERVER_DIR}")

if uv:
    try:
        r = subprocess.run(
            ["uv", "--directory", str(SERVER_DIR), "run", "--frozen", "python", "-c",
             "import rhino_architect; print('ok')"],
            capture_output=True, text=True, timeout=15
        )
        if r.returncode == 0 and "ok" in r.stdout:
            ok("rhino_architect package imports successfully")
        else:
            fail(f"rhino_architect import failed:\n{r.stderr.strip()[:300]}")
    except Exception as e:
        fail(f"Could not test import: {e}")

    # Check optional tracing deps
    for pkg, pip_name in [("fitz", "pymupdf"), ("cv2", "opencv-python"), ("numpy", "numpy")]:
        try:
            r = subprocess.run(
                ["uv", "--directory", str(SERVER_DIR), "run", "--frozen", "python", "-c", f"import {pkg}; print('ok')"],
                capture_output=True, text=True, timeout=10
            )
            if r.returncode == 0:
                ok(f"  Optional dep '{pkg}' available (PDF tracing)")
            else:
                warn(f"  Optional dep '{pkg}' not installed — PDF tracing disabled. Run: uv add {pip_name}")
        except Exception:
            warn(f"  Could not check '{pkg}'")

# ── 3. Rhino TCP bridge ─────────────────────────────────────────────────────────
section("Rhino TCP Bridge (port 9544)")
try:
    s = socket.create_connection(("127.0.0.1", 9544), timeout=2)
    s.close()
    ok("Port 9544 is open — Rhino AIBridge is running")
except (ConnectionRefusedError, OSError):
    warn("Port 9544 not responding — Rhino is not running or AIBridge not started")
    print("         In Rhino command line type:  AIBridge")

# ── 4. Claude Desktop ───────────────────────────────────────────────────────────
section("Claude Desktop")
if CLAUDE_CONFIG.exists():
    try:
        with open(CLAUDE_CONFIG, encoding="utf-8") as f:
            cfg = json.load(f)
        if "rhino-architect" in cfg.get("mcpServers", {}):
            entry = cfg["mcpServers"]["rhino-architect"]
            ok(f"rhino-architect configured in Claude Desktop")
            # Check path still valid
            args = entry.get("args", [])
            if "--directory" in args:
                sd = args[args.index("--directory") + 1]
                if Path(sd).exists():
                    ok(f"  Server path valid: {sd}")
                else:
                    fail(f"  Server path in config not found: {sd}  — re-run INSTALL.bat")
        else:
            warn("rhino-architect NOT in Claude Desktop config")
            print("         Run INSTALL.bat (option 3) or:")
            print(f"         python {SCRIPTS_DIR / 'patch_claude_config.py'} \"{SERVER_DIR}\"")
    except Exception as e:
        fail(f"Could not read Claude config: {e}")
else:
    warn("Claude Desktop config not found (Claude Desktop may not be installed)")

# ── 5. Codex ────────────────────────────────────────────────────────────────────
section("OpenAI Codex")
if CODEX_CONFIG.exists():
    content = CODEX_CONFIG.read_text(encoding="utf-8")
    if "rhino_architect" in content:
        ok("rhino_architect entry found in ~/.codex/config.toml")
        # Extract and verify server path
        m = re.search(r'"--directory",\s*"([^"]+)"', content)
        if m:
            sd = m.group(1).replace("\\\\", "\\")
            if Path(sd).exists():
                ok(f"  Server path valid: {sd}")
            else:
                fail(f"  Server path not found: {sd}  — re-run INSTALL.bat option [4]")
        # Check enabled = true
        if "enabled = true" in content:
            ok("  Server enabled = true")
        else:
            warn("  enabled not set to true in config")
    else:
        warn("rhino_architect NOT in ~/.codex/config.toml")
        print("         Run INSTALL.bat option [4] or:")
        print(f"         python {SCRIPTS_DIR / 'patch_codex_config.py'} \"{SERVER_DIR}\"")
else:
    warn("~/.codex/config.toml not found (Codex not configured)")
    print("         Run INSTALL.bat option [4] to configure Codex")

# Check codex CLI availability
codex_cli = shutil.which("codex")
if codex_cli:
    ok(f"codex CLI found: {codex_cli}")
    try:
        r = subprocess.run(["codex", "mcp", "list"], capture_output=True, text=True, timeout=5)
        if "rhino_architect" in r.stdout:
            ok("  codex mcp list shows rhino_architect")
        else:
            warn("  rhino_architect not visible in 'codex mcp list' output")
    except Exception:
        warn("  Could not run 'codex mcp list'")
else:
    warn("codex CLI not in PATH (optional — only needed for CLI usage)")

# ── Summary ─────────────────────────────────────────────────────────────────────
print(f"\n{'═'*60}")
if failures == 0 and warnings == 0:
    print(f"  {PASS}  All checks passed — RhinoAIBridge is healthy!")
elif failures == 0:
    print(f"  {WARN}  {warnings} warning(s), 0 failures — mostly OK")
else:
    print(f"  {FAIL}  {failures} failure(s), {warnings} warning(s) — action required")
print(f"{'═'*60}\n")

sys.exit(2 if failures > 0 else (1 if warnings > 0 else 0))
