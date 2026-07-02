#!/usr/bin/env python3
"""
rhino-architect doctor -- diagnose your RhinoAIBridge installation.

Usage:
    uv run rhino-architect-doctor
    python -m rhino_architect.doctor
"""
from __future__ import annotations
import json
import os
import shutil
import socket
import struct
import subprocess
import sys
from pathlib import Path

PLUGIN_PORT = int(os.environ.get("RHINO_PORT", "9544"))
PLUGIN_HOST = os.environ.get("RHINO_HOST", "127.0.0.1")
CLAUDE_CONFIG = Path(os.environ.get("APPDATA", "")) / "Claude" / "claude_desktop_config.json"
RHINO_PLUGIN_DIR = Path(os.environ.get("APPDATA", "")) / "McNeel" / "Rhinoceros" / "8.0" / "Plug-ins" / "RhinoAIBridge"

GREEN  = "\033[92m"
YELLOW = "\033[93m"
RED    = "\033[91m"
RESET  = "\033[0m"
BOLD   = "\033[1m"

def ok(msg):   print(f"  {GREEN}v{RESET}  {msg}")
def warn(msg): print(f"  {YELLOW}!{RESET}  {msg}")
def fail(msg): print(f"  {RED}x{RESET}  {msg}")
def info(msg): print(f"     {msg}")

def check_python():
    print(f"\n{BOLD}[1] Python{RESET}")
    v = sys.version_info
    if v >= (3, 10):
        ok(f"Python {v.major}.{v.minor}.{v.micro}")
    else:
        fail(f"Python {v.major}.{v.minor}.{v.micro} -- need 3.10+")
        info("Install from https://python.org")

def check_uv():
    print(f"\n{BOLD}[2] uv package manager{RESET}")
    uv = shutil.which("uv")
    if uv:
        result = subprocess.run(["uv", "--version"], capture_output=True, text=True)
        ok(f"uv found: {result.stdout.strip()}")
    else:
        fail("uv not found")
        info('Install: powershell -c "irm https://astral.sh/uv/install.ps1 | iex"')

def check_deps():
    print(f"\n{BOLD}[3] Python dependencies{RESET}")
    try:
        import mcp
        ok(f"mcp {mcp.__version__ if hasattr(mcp, '__version__') else 'installed'}")
    except ImportError:
        fail("mcp not installed -- run: uv sync")
    try:
        import orjson  # noqa: F401 - availability probe
        ok("orjson installed")
    except ImportError:
        fail("orjson not installed -- run: uv sync")
    try:
        import pydantic
        ok(f"pydantic {pydantic.VERSION}")
    except ImportError:
        fail("pydantic not installed -- run: uv sync")

def check_port():
    print(f"\n{BOLD}[4] Port {PLUGIN_PORT} (Rhino plugin){RESET}")
    try:
        with socket.create_connection((PLUGIN_HOST, PLUGIN_PORT), timeout=2.0) as s:
            import gzip
            s.settimeout(5.0)

            def exchange(command):
                payload = json.dumps(command).encode("utf-8")
                s.sendall(struct.pack(">I", len(payload)) + payload)
                flag = s.recv(1)
                (length,) = struct.unpack(">I", s.recv(4))
                data = b""
                while len(data) < length:
                    chunk = s.recv(length - len(data))
                    if not chunk: break
                    data += chunk
                if flag and flag[0] == 0x01:
                    data = gzip.decompress(data)
                return json.loads(data)

            from .protocol import _read_auth_token
            token = _read_auth_token()
            if token:
                auth = exchange({"type": "auth", "token": token})
                if auth.get("status") != "ok" and auth.get("error_code") == "AUTH_REQUIRED":
                    raise RuntimeError("local authentication failed; restart AIBridge in Rhino")
            pong = exchange({"type": "ping", "params": {}})
            build = pong.get("build_hash", "?")
            units = pong.get("unit_system", pong.get("units", "?"))
            ok(f"Rhino plugin reachable -- build:{build}, units:{units}")
    except ConnectionRefusedError:
        fail(f"Port {PLUGIN_PORT} refused -- Rhino is not running or AIBridge is not started")
        info("1. Open Rhino 8")
        info("2. Type in command line:  AIBridge")
    except socket.timeout:
        fail(f"Port {PLUGIN_PORT} timed out")
        info("Rhino may be busy -- try again in a few seconds")
    except Exception as e:
        fail(f"Connection error: {e}")

def check_plugin_files():
    print(f"\n{BOLD}[5] Rhino plugin files{RESET}")
    if RHINO_PLUGIN_DIR.exists():
        rhp = RHINO_PLUGIN_DIR / "RhinoAIBridge.rhp"
        if rhp.exists():
            size_kb = rhp.stat().st_size // 1024
            ok(f"RhinoAIBridge.rhp found ({size_kb} KB)")
        else:
            fail("RhinoAIBridge.rhp not found -- run INSTALL.bat")
        for dll in ["Newtonsoft.Json.dll"]:
            if (RHINO_PLUGIN_DIR / dll).exists():
                ok(f"{dll} found")
            else:
                warn(f"{dll} missing -- run INSTALL.bat")
    else:
        fail(f"Plugin directory not found: {RHINO_PLUGIN_DIR}")
        info("Run INSTALL.bat to install the plugin")

def check_claude_config():
    print(f"\n{BOLD}[6] Claude Desktop configuration{RESET}")
    if not CLAUDE_CONFIG.exists():
        warn("claude_desktop_config.json not found")
        info("Claude Desktop may not be installed, or run INSTALL.bat to configure it")
        return
    try:
        cfg = json.loads(CLAUDE_CONFIG.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        fail(f"claude_desktop_config.json is invalid JSON: {e}")
        info(f"Delete {CLAUDE_CONFIG} and run INSTALL.bat again")
        return

    servers = cfg.get("mcpServers", {})
    if "rhino-architect" in servers:
        entry = servers["rhino-architect"]
        args = entry.get("args", [])
        # Find the --directory arg
        server_dir = None
        for i, a in enumerate(args):
            if a == "--directory" and i + 1 < len(args):
                server_dir = Path(args[i + 1])
        if server_dir:
            if server_dir.exists():
                ok(f"rhino-architect configured -> {server_dir}")
                venv = server_dir / ".venv"
                if venv.exists():
                    ok(".venv exists (uv sync was run)")
                else:
                    warn(".venv missing -- run: uv sync in the server directory")
                    info(f'cd "{server_dir}" && uv sync')
            else:
                fail(f"Server directory not found: {server_dir}")
                info("Run INSTALL.bat again from the correct location")
        else:
            warn("rhino-architect entry found but --directory arg missing")
    else:
        fail("rhino-architect not configured in Claude Desktop")
        info("Run INSTALL.bat to configure Claude Desktop")
        info(f"Or manually add to {CLAUDE_CONFIG}")

def main():
    print(f"\n{BOLD}{'='*54}{RESET}")
    print(f"{BOLD}  RhinoAIBridge Doctor -- installation diagnostics{RESET}")
    print(f"{BOLD}{'='*54}{RESET}")

    check_python()
    check_uv()
    check_deps()
    check_port()
    check_plugin_files()
    check_claude_config()

    print(f"\n{BOLD}{'='*54}{RESET}")
    print("  Run this after any install issue to get exact fix steps.")
    print("  Docs: https://github.com/tanishqbhattad/rhino-mcp")
    print(f"{BOLD}{'='*54}{RESET}\n")

if __name__ == "__main__":
    main()
