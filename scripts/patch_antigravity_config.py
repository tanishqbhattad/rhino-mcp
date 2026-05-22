"""
RhinoAIBridge — Gemini Antigravity config patcher
by tanishqb (https://github.com/tanishqbhattad/rhino-mcp)

Writes the rhino-architect MCP entry to %USERPROFILE%\.gemini\antigravity\mcp_config.json
so that Gemini Antigravity picks up the server automatically.

Usage: python patch_antigravity_config.py <server_directory>
Called automatically by INSTALL.bat. Safe to run multiple times.
"""

import json
import os
import sys
import shutil
from datetime import datetime


def main():
    if len(sys.argv) < 2:
        print("Usage: patch_antigravity_config.py <server_directory>")
        sys.exit(1)

    server_dir = sys.argv[1].strip().rstrip("\\").rstrip("/")
    home = os.environ.get("USERPROFILE", os.path.expanduser("~"))
    config_dir = os.path.join(home, ".gemini", "antigravity")
    config_path = os.path.join(config_dir, "mcp_config.json")

    # Ensure directory exists
    os.makedirs(config_dir, exist_ok=True)

    # Read existing config (if any)
    if os.path.exists(config_path):
        backup = config_path + f".backup_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
        shutil.copy2(config_path, backup)
        print(f"  Backed up existing config to: {backup}")
        try:
            with open(config_path, "r", encoding="utf-8") as f:
                cfg = json.load(f)
        except json.JSONDecodeError:
            print("  WARNING: Existing config was invalid JSON - starting fresh.")
            cfg = {}
    else:
        cfg = {}

    # Patch mcpServers entry
    cfg.setdefault("mcpServers", {})
    cfg["mcpServers"]["rhino-architect"] = {
        "command": "uv",
        "args": [
            "--directory",
            server_dir,
            "run",
            "rhino-architect",
        ],
    }

    # Write back
    with open(config_path, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)

    print(f"  Antigravity configured: {config_path}")
    print(f"  MCP server path: {server_dir}")
    print("  Restart Antigravity to pick up the new connection.")


if __name__ == "__main__":
    main()
