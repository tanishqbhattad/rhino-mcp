"""
RhinoAIBridge — Codex config patcher
by tanishqb (https://github.com/tanishqbhattad/rhino-mcp)

Writes the rhino_architect MCP entry to %USERPROFILE%\.codex\config.toml
so that OpenAI Codex picks up the server automatically.

Usage: python patch_codex_config.py <server_directory>
Called automatically by INSTALL.bat option [4]. Safe to run multiple times.
"""

import os
import re
import sys
import shutil
from datetime import datetime


def _toml_escape(s: str) -> str:
    """Escape backslashes and quotes for a TOML basic string."""
    return s.replace("\\", "\\\\").replace('"', '\\"')


def _build_entry(server_dir: str, safe_mode: bool = True) -> str:
    """Return the TOML block for the rhino_architect MCP server."""
    sd = _toml_escape(server_dir)
    safe = "1" if safe_mode else "0"
    return (
        '\n[mcp_servers.rhino_architect]\n'
        'command = "uv"\n'
        'args = [\n'
        '  "--directory",\n'
        '  "' + sd + '",\n'
        '  "run",\n'
        '  "rhino-architect"\n'
        ']\n'
        'startup_timeout_sec = 20\n'
        'tool_timeout_sec = 120\n'
        'enabled = true\n'
        '\n'
        '[mcp_servers.rhino_architect.env]\n'
        'RHINO_HOST = "127.0.0.1"\n'
        'RHINO_PORT = "9544"\n'
        'RHINO_SAFE_MODE = "' + safe + '"\n'
    )


def _remove_existing_entry(content: str) -> str:
    """Strip any existing [mcp_servers.rhino_architect*] sections from TOML content.

    The old regex used [^\\[]* which broke on TOML array literals like
    args = ["--directory", ...] because it treated the '[' in the value as
    a section header.  The fix: match until the next line that starts with
    '[' (a real section header) instead of stopping at any '[' character.
    """
    pattern = re.compile(
        r'\n?\[mcp_servers\.rhino_architect(?:\.env)?\]'
        r'(?:\n(?!\[).*)*',
        re.MULTILINE,
    )
    return pattern.sub('', content)


def main():
    if len(sys.argv) < 2:
        print("Usage: patch_codex_config.py <server_directory>")
        sys.exit(1)

    server_dir = sys.argv[1].strip().rstrip("\\").rstrip("/")
    safe_mode = "--safe" in sys.argv

    codex_dir = os.path.join(os.environ.get("USERPROFILE", os.path.expanduser("~")), ".codex")
    config_path = os.path.join(codex_dir, "config.toml")

    os.makedirs(codex_dir, exist_ok=True)

    if os.path.exists(config_path):
        backup = config_path + f".backup_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
        shutil.copy2(config_path, backup)
        print(f"  Backed up existing config to: {backup}")
        with open(config_path, "r", encoding="utf-8") as f:
            content = f.read()
    else:
        content = ""

    content = _remove_existing_entry(content)
    content = content.rstrip() + "\n" + _build_entry(server_dir, safe_mode)

    with open(config_path, "w", encoding="utf-8") as f:
        f.write(content)

    print(f"  Codex config written: {config_path}")
    print(f"  MCP server path: {server_dir}")
    print(f"  RHINO_SAFE_MODE: {'1 (safe)' if safe_mode else '0 (developer)'}")
    print()
    print("  Verify with:  codex mcp list")
    print("  Then in Rhino run AIBridge, and ask Codex:  ping Rhino")


if __name__ == "__main__":
    main()
