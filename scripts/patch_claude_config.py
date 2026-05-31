"""
RhinoAIBridge — Claude Desktop config patcher
by tanishqb (https://github.com/tanishqb/rhino-ai-bridge)

Usage: python patch_claude_config.py <server_directory>
Called automatically by INSTALL.bat. Safe to run multiple times.
"""

import json
import os
import sys
import shutil
from datetime import datetime

def main():
    if len(sys.argv) < 2:
        print("Usage: patch_claude_config.py <server_directory>")
        sys.exit(1)

    server_dir = sys.argv[1].strip().rstrip("\\").rstrip("/")
    config_path = os.path.join(os.environ.get("APPDATA", ""), "Claude", "claude_desktop_config.json")

    # ── Locate config ─────────────────────────────────────────────────────────
    if not os.path.exists(config_path):
        config_dir = os.path.dirname(config_path)
        if not os.path.exists(config_dir):
            print(f"Claude Desktop not found at: {config_dir}")
            print("Install Claude Desktop from https://claude.ai/download, then re-run INSTALL.bat")
            sys.exit(2)
        # Create a fresh config
        cfg = {}
    else:
        # ── Back up existing config ───────────────────────────────────────────
        backup = config_path + f".backup_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
        shutil.copy2(config_path, backup)
        print(f"  Backed up existing config to: {backup}")

        try:
            with open(config_path, "r", encoding="utf-8") as f:
                cfg = json.load(f)
        except json.JSONDecodeError:
            print("  WARNING: Existing config was invalid JSON — starting fresh.")
            cfg = {}

    # ── Patch mcpServers entry ────────────────────────────────────────────────
    cfg.setdefault("mcpServers", {})

    # Resolve uv path: prefer the one in PATH, fall back to common install locations
    uv_cmd = "uv"  # uv is on PATH after install

    cfg["mcpServers"]["rhino-architect"] = {
        "command": uv_cmd,
        "args": [
            "--directory",
            server_dir,
            "run",
            "--frozen",
            "rhino-architect",
        ],
    }

    # ── Write back ────────────────────────────────────────────────────────────
    with open(config_path, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)

    print(f"  Claude Desktop configured: {config_path}")
    print(f"  MCP server path: {server_dir}")
    print("  Restart Claude Desktop to pick up the new connection.")


if __name__ == "__main__":
    main()
