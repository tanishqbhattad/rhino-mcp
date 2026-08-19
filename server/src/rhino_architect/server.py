# RhinoAIBridge MCP Server
# by tanishqbhattad | https://github.com/tanishqbhattad/rhino-mcp

"""Rhino AI Bridge MCP Server.

Key properties:
  - Lean responses (dicts -> FastMCP -> orjson on wire)
  - scene_version etag surfaced on every response (cache key for the model)
  - Atomic batches + reference resolution ($1.object_ids[0] chaining)
  - Architect intelligence layer (massing, floors, core, facade, schedules)
  - Protocol 5 transport (multiplexed, idempotent retries, cancel, binary frames)
  - Tool profiles: RHINO_TOOLS=lean|standard|full controls how many tools are
    exposed to the MCP client. Pruned tools remain callable via `batch`.

The plugin still understands the full v3/v4 command vocabulary so older flows and
direct-batch sub-ops keep working. The MCP-exposed surface here is the curated subset
that maps cleanly to how architects work.

The single source of truth for the version is pyproject.toml (package metadata).
"""
from __future__ import annotations

import asyncio
import base64
import importlib.metadata
import importlib.util
import logging
import os
import orjson
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Any, Optional

from mcp.server.fastmcp import FastMCP, Image
from pydantic import BaseModel, ConfigDict, Field

from rhino_architect.protocol import (
    RhinoCommandError,
    RhinoConnectionError,
    get_connection,
)

logging.basicConfig(level=logging.INFO, stream=sys.stderr)
logger = logging.getLogger("rhino_ai_bridge")

# Single-sourced version: pyproject.toml -> installed package metadata.
try:
    _VERSION = importlib.metadata.version("rhino-architect")
except importlib.metadata.PackageNotFoundError:  # running from a raw checkout
    _VERSION = "0.0.0-dev"


# Safe mode --------------------------------------------------
# Optional Python-side defense: set RHINO_SAFE_MODE=1 to block destructive commands.
# The Rhino plugin independently enforces the Safe / Standard / Developer mode chosen in Rhino.
# Safe, trusted (default), or developer - controlled by env var.
_SAFE_MODE = os.environ.get("RHINO_SAFE_MODE", "").strip().lower() in ("1", "true", "yes")
_TRUSTED_MODE = not _SAFE_MODE  # default

_SAFE_MODE_BLOCKED = {
    "execute_script", "run_python", "execute_python3", "run_command",
    "delete_objects", "boolean_operation",
}

def _check_safe_mode(tool_name: str) -> dict | None:
    """Return an error dict if safe mode blocks this tool, else None."""
    if _SAFE_MODE and tool_name in _SAFE_MODE_BLOCKED:
        return {
            "status": "error",
            "error_code": "COMMAND_BLOCKED_BY_SAFE_MODE",
            "message": f"Tool '{tool_name}' is blocked in safe mode. "
                       f"Set RHINO_SAFE_MODE=0 or unset the variable to allow it.",
            "safe_mode": True,
            "blocked_tools": sorted(_SAFE_MODE_BLOCKED),
        }
    return None
# Phase 7: Heartbeat watchdog - auto-exit when Rhino closes.
# Runs in a daemon thread (not asyncio) so it works regardless of the MCP event loop.
# Checks every 10s whether the Rhino TCP port is still accepting connections.
# After 2 consecutive failures (~20s), exits the process so the MCP client knows
# the server is gone and can restart it when Rhino reopens.
_HEARTBEAT_INTERVAL = 10   # seconds between checks
_HEARTBEAT_MAX_FAILS = 2   # consecutive failures before exit
_HEARTBEAT_STARTUP_MAX_WAIT = int(os.environ.get("RHINO_HEARTBEAT_STARTUP_MAX_WAIT", "120"))

def _rhino_heartbeat_loop():
    """Background thread: status-only monitor of the Rhino TCP port.

    IMPORTANT: this never terminates the MCP server. Claude Desktop launches the
    server at app startup (usually before Rhino is open) and marks the connector
    as "failed" if the process exits. The protocol layer auto-reconnects when
    Rhino reappears, so tool calls just return a connection error while Rhino is
    down and start working once it is back. We only log reachability changes.
    """
    import time
    host = os.environ.get("RHINO_HOST", "127.0.0.1")
    port = int(os.environ.get("RHINO_PORT", "9544"))
    logger.info("Heartbeat: monitoring Rhino on %s:%d (status only, no auto-exit).", host, port)
    was_reachable = None
    while True:
        try:
            with socket.create_connection((host, port), timeout=3):
                pass
            reachable = True
        except OSError:
            reachable = False
        if reachable != was_reachable:
            logger.info(
                "Heartbeat: Rhino %s.",
                "reachable" if reachable else "not reachable (server staying alive)",
            )
            was_reachable = reachable
        time.sleep(_HEARTBEAT_INTERVAL)

_heartbeat_thread = threading.Thread(target=_rhino_heartbeat_loop, daemon=True)
_heartbeat_thread.start()


# rab helper library --------------------------------------------------
# A small IronPython-2-compatible helper module (rab.py) is deployed next to the
# auth token and auto-imported into every execute_script call, so the model can
# write `rab.wall(...)` instead of 50 lines of rhinoscriptsyntax boilerplate.
# Disable with RHINO_RAB=0.
_RAB_ENABLED = os.environ.get("RHINO_RAB", "1").strip().lower() not in ("0", "false", "no")


def _aibridge_dir() -> Path:
    """Same per-user directory the auth token lives in (mirrors protocol.py)."""
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    else:
        base = str(Path.home() / ".config")
    return Path(base) / "AIBridge"


def _deploy_rab() -> None:
    """Copy the packaged rab.py into the AIBridge dir so IronPython can import it."""
    try:
        src = Path(__file__).with_name("rab.py").read_text(encoding="utf-8")
        dst = _aibridge_dir() / "rab.py"
        dst.parent.mkdir(parents=True, exist_ok=True)
        if not dst.exists() or dst.read_text(encoding="utf-8") != src:
            dst.write_text(src, encoding="utf-8")
            logger.info("rab helper library deployed to %s", dst)
    except Exception as e:
        logger.warning("Could not deploy rab.py: %s", e)


if _RAB_ENABLED:
    _deploy_rab()

# Prepended to every execute_script payload (after the plugin's own rs/sc preamble).
# reload() keeps the in-Rhino copy fresh when the server ships a newer rab.py.
_RAB_BOOTSTRAP = (
    "import sys as _rabsys, os as _rabos\n"
    "_rabdir = _rabos.path.join(_rabos.environ.get('LOCALAPPDATA') or _rabos.path.expanduser('~/.config'), 'AIBridge')\n"
    "if _rabdir not in _rabsys.path:\n"
    "    _rabsys.path.insert(0, _rabdir)\n"
    "try:\n"
    "    import rab\n"
    "    rab = reload(rab)\n"
    "except Exception as _raberr:\n"
    "    rab = None\n"
    "    print('[rab] helper import failed: %s' % _raberr)\n"
)

mcp = FastMCP("RhinoAIBridge")


# Helpers --------------------------------------------------
async def _exec(command: str, params: dict[str, Any]) -> dict:
    conn = await get_connection()
    resp = await conn.send_command(command, params)
    if not resp.ok:
        raise RhinoCommandError(resp.message, resp.result)
    return resp.result


# Opt-in per-call timing: RHINO_TIMING=1 adds elapsed_ms to every response.
# Off by default to keep responses token-lean.
_TIMING = os.environ.get("RHINO_TIMING", "").strip().lower() in ("1", "true", "yes")


async def _exec_simple(command: str, params: dict[str, Any]) -> dict:
    """Execute a command and return the raw result dict.

    Returns dict, not str: FastMCP serializes once on the way out.
    Phase 2: surfaces scene_version on every response so the model can use it as
    an etag for caching scene queries between turns.
    """
    blocked = _check_safe_mode(command)
    if blocked:
        return blocked
    t0 = time.perf_counter() if _TIMING else 0.0
    try:
        conn = await get_connection()
        resp = await conn.send_command(command, params)
        if not resp.ok:
            return {"status": "error", "message": resp.message, **(resp.result or {})}
        result = dict(resp.result) if resp.result else {}
        _reattach_binary_image(result)
        result.setdefault("status", "ok")
        if resp.scene_version is not None and "scene_version" not in result:
            result["scene_version"] = resp.scene_version
        if _TIMING:
            result["elapsed_ms"] = round((time.perf_counter() - t0) * 1000, 1)
        return result
    except RhinoConnectionError as e:
        return {
            "status": "error",
            "error_code": "RHINO_NOT_CONNECTED",
            "message": str(e),
            "recoverable": True,
            "retry_hint": "Open Rhino, run AIBridge, then retry.",
        }
    except RhinoCommandError as e:
        return {
            "status": "error",
            "error_code": "COMMAND_FAILED",
            "message": str(e),
            "recoverable": True,
            "retry_hint": "Check Rhino command diagnostics and simplify the inputs.",
        }


def _reattach_binary_image(result: dict) -> dict:
    """Re-encode protocol-5 binary image frames as base64, recursively.

    Raw bytes arrive out-of-band (flag 0x02) to avoid base64 inflation on the wire.
    They MUST be converted before the dict reaches FastMCP, which can only serialize
    JSON types. Batches need the recursive walk: protocol.send_batch shortcuts a
    single non-atomic command to a direct send, so an image can surface either at the
    top level or nested inside results[].
    """
    if not isinstance(result, dict):
        return result
    raw = result.pop("_image_raw", None)
    if raw is not None and "image_base64" not in result:
        result["image_base64"] = base64.b64encode(raw).decode("ascii")
        result.pop("image_binary", None)
        result.pop("image_bytes_length", None)
    subs = result.get("results")
    if isinstance(subs, list):
        for s in subs:
            _reattach_binary_image(s)
    return result


def _as_mcp_image(result: dict, key: str = "image_base64", default_format: str = "png") -> Image | dict:
    """Convert a plugin base64 image response into real MCP image content.

    Error dictionaries pass through unchanged so clients still see useful diagnostics.
    """
    if result.get("status") != "ok":
        return result
    b64 = result.get(key) or result.get("thumbnail_base64")
    if not b64:
        return result
    fmt = str(result.get("format") or default_format).lower()
    if fmt == "jpg":
        fmt = "jpeg"
    return Image(data=base64.b64decode(b64), format=fmt)


def _image_with_metadata(result: dict, key: str = "image_base64", default_format: str = "png") -> list[Any] | dict:
    """Return McNeel-style content: JSON metadata first, image second."""
    if result.get("status") != "ok":
        return result
    b64 = result.get(key) or result.get("thumbnail_base64")
    if not b64:
        return result
    fmt = str(result.get("format") or default_format).lower()
    if fmt == "jpg":
        fmt = "jpeg"
    meta = dict(result)
    meta.pop("image_base64", None)
    meta.pop("thumbnail_base64", None)
    meta["content"] = {"image": {"format": fmt, "bytes": result.get("bytes")}}
    return [meta, Image(data=base64.b64decode(b64), format=fmt)]


def _named_image_content(name: str, result: dict, key: str = "image_base64", default_format: str = "png") -> list[Any]:
    """Return [metadata, Image] for one named capture, or [error metadata] if capture failed."""
    if result.get("status") != "ok":
        err = dict(result)
        err["capture_name"] = name
        return [err]
    b64 = result.get(key) or result.get("thumbnail_base64")
    if not b64:
        meta = dict(result)
        meta["capture_name"] = name
        meta["content"] = {"image": None}
        return [meta]
    fmt = str(result.get("format") or default_format).lower()
    if fmt == "jpg":
        fmt = "jpeg"
    meta = dict(result)
    meta.pop("image_base64", None)
    meta.pop("thumbnail_base64", None)
    meta["capture_name"] = name
    meta["content"] = {"image": {"format": fmt, "bytes": result.get("bytes")}}
    return [meta, Image(data=base64.b64decode(b64), format=fmt)]


def _compare_base64_images(before_b64: str | None, after_b64: str | None) -> dict[str, Any]:
    """Best-effort image comparison using OpenCV when available."""
    if not before_b64 or not after_b64:
        return {"status": "unavailable", "message": "Missing before/after image data."}
    try:
        import cv2  # type: ignore
        import numpy as np  # type: ignore

        before = cv2.imdecode(np.frombuffer(base64.b64decode(before_b64), np.uint8), cv2.IMREAD_COLOR)
        after = cv2.imdecode(np.frombuffer(base64.b64decode(after_b64), np.uint8), cv2.IMREAD_COLOR)
        if before is None or after is None:
            return {"status": "unavailable", "message": "Could not decode one or both images."}
        if before.shape != after.shape:
            after = cv2.resize(after, (before.shape[1], before.shape[0]))
        diff = cv2.absdiff(before, after)
        gray = cv2.cvtColor(diff, cv2.COLOR_BGR2GRAY)
        mean_delta = float(gray.mean())
        changed_ratio = float((gray > 18).mean())
        _, png = cv2.imencode(".png", cv2.applyColorMap(gray, cv2.COLORMAP_INFERNO))
        return {
            "status": "ok",
            "mean_pixel_delta": round(mean_delta, 3),
            "changed_ratio": round(changed_ratio, 4),
            "diff_base64": base64.b64encode(png.tobytes()).decode("ascii"),
            "format": "png",
            "interpretation": "changed_ratio is the approximate fraction of pixels that changed visibly.",
        }
    except Exception as e:
        return {
            "status": "unavailable",
            "message": f"Image comparison requires numpy/opencv in the MCP environment: {e}",
        }


def _find_rhinocode() -> str | None:
    """Locate Rhino 8's rhinocode CLI."""
    found = shutil.which("rhinocode")
    if found:
        return found
    candidates = [
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Rhino 8" / "System" / "rhinocode.exe",
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Rhino 8" / "System" / "rhinocode",
    ]
    for candidate in candidates:
        if candidate.exists():
            return str(candidate)
    return None


async def _run_process(args: list[str], timeout_seconds: int) -> dict[str, Any]:
    """Run a subprocess without blocking the MCP event loop."""
    def _run() -> subprocess.CompletedProcess[str]:
        return subprocess.run(args, capture_output=True, text=True, timeout=timeout_seconds)

    try:
        proc = await asyncio.to_thread(_run)
        return {
            "returncode": proc.returncode,
            "stdout": proc.stdout.strip(),
            "stderr": proc.stderr.strip(),
        }
    except subprocess.TimeoutExpired:
        return {
            "returncode": -1,
            "stdout": "",
            "stderr": f"Timed out after {timeout_seconds}s",
        }


async def _developer_mode_required() -> tuple[dict | None, dict]:
    """Block code execution unless the Rhino plugin is currently in Developer mode.

    Returns (error_or_None, ping_data) so callers can reuse the health probe
    instead of paying for a second ping round-trip.
    """
    blocked = _check_safe_mode("execute_python3")
    if blocked:
        return blocked, {}
    ping = await _exec_simple("ping", {})
    if ping.get("status") != "ok":
        return ping, ping
    mode = str(ping.get("mode") or "").lower()
    if mode != "developer":
        return {
            "status": "error",
            "error_code": "MODE_BLOCKED",
            "message": "execute_python3 requires Developer mode in Rhino because it can run arbitrary code.",
            "current_mode": mode or "unknown",
            "retry_hint": "Run AIBridge in Rhino and choose Developer mode, then retry.",
        }, ping
    return None, ping


def _parse_rhino_version(raw: Any) -> tuple[int, int] | None:
    """Parse Rhino version strings like '8.9.24194.18121' into (8, 9)."""
    try:
        parts = str(raw or "").split(".")
        if len(parts) < 2:
            return None
        return int(parts[0]), int(parts[1])
    except (TypeError, ValueError):
        return None


async def _exec_batch(
    commands: list[dict[str, Any]],
    atomic: bool = True,
    stop_on_error: Optional[bool] = None,
) -> dict:
    """Phase 3 - execute a batch with optional atomic semantics."""
    for i, command in enumerate(commands):
        tool_name = command.get("type", "") if isinstance(command, dict) else ""
        blocked = _check_safe_mode(tool_name)
        if blocked:
            blocked["batch_index"] = i
            blocked["op_index"] = i + 1
            return blocked
        # Script sub-ops get the same rab bootstrap as the standalone tool.
        if _RAB_ENABLED and tool_name in ("execute_script", "run_python"):
            sub = command.get("params")
            if isinstance(sub, dict):
                code = sub.get("code") or sub.get("script")
                if isinstance(code, str) and not code.startswith(_RAB_BOOTSTRAP):
                    sub.pop("script", None)
                    sub["code"] = _RAB_BOOTSTRAP + code
    try:
        conn = await get_connection()
        resp = await conn.send_batch(commands, atomic=atomic, stop_on_error=stop_on_error)
        result = dict(resp.result) if resp.result else {}
        # A single non-atomic sub-command is sent as a direct command, so an
        # image-returning op (capture, section_preview) can bring back a binary
        # frame here too. Without this the raw bytes reach FastMCP and blow up
        # serialization with "invalid utf-8 sequence".
        _reattach_binary_image(result)
        result.setdefault("status", resp.status)
        if resp.message:
            result.setdefault("message", resp.message)
        if resp.scene_version is not None and "scene_version" not in result:
            result["scene_version"] = resp.scene_version
        return result
    except RhinoConnectionError as e:
        return {"status": "error", "error_code": "RHINO_NOT_CONNECTED", "message": str(e), "recoverable": True}
    except RhinoCommandError as e:
        return {"status": "error", "error_code": "COMMAND_FAILED", "message": str(e), "recoverable": True}


# Tool annotation hints for the MCP client.
RO = {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False}
WR = {"readOnlyHint": False, "destructiveHint": False, "idempotentHint": False, "openWorldHint": False}
WI = {"readOnlyHint": False, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False}
DE = {"readOnlyHint": False, "destructiveHint": True, "idempotentHint": True, "openWorldHint": False}


# Input Models --------------------------------------------------
class Empty(BaseModel):
    model_config = ConfigDict(extra="forbid")


class QuerySceneInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    scope: str = "objects"  # objects | layers | summary | scene
    filter: dict[str, Any] = Field(default_factory=dict)
    detail: str = "summary"  # ids | summary | full
    limit: int = Field(default=80, ge=1, le=500)
    format: str = Field(
        default="rows",
        description="'rows' (objects as dicts) or 'columnar' (parallel arrays: ids/names/layers/types/bboxes - 40-60% fewer tokens on large listings).",
    )


class CreateObjectInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    type: str = Field(
        default="box",
        description=(
            "What to create. Architectural: wall, slab/floor, column, opening/window/door, roof, "
            "massing/building_mass, core. Primitives: point, line, polyline, circle, arc, ellipse, "
            "curve, box, sphere, cone, cylinder, surface."
        ),
    )
    params: dict[str, Any] = Field(
        default_factory=dict,
        description=(
            "Type-specific parameters. ALL EXAMPLES BELOW ARE IN MILLIMETRES - values are raw "
            "model units, never auto-converted. Check ping.unit_system first and scale "
            "accordingly (a metres document wants 6.0, not 6000). Examples: "
            "box {origin:[0,0,0], size_x:6000, size_y:6000, size_z:3000}; "
            "wall {start_point:[0,0,0], end_point:[6000,0,0], height:3000, thickness:200}; "
            "massing {footprint:[[0,0,0],[30000,0,0],[30000,18000,0],[0,18000,0]], levels:4, level_height:3600} "
            "or level_heights:[4200,3600,3600,3600] for variable floors (total = massing height); "
            "core {boundary:[[9000,6000,0],[15000,6000,0],[15000,12000,0],[9000,12000,0]], height:14400}."
        ),
    )
    layer: Optional[str] = None
    name: Optional[str] = None
    color: Optional[list[int]] = None
    measure: bool = False
    translation: Optional[list[float]] = None
    rotation: Optional[list[float]] = None
    scale: Optional[Any] = None


class TransformObjectsInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_ids: list[str] = Field(..., min_length=1)
    operations: list[dict[str, Any]] = Field(
        default_factory=list,
        description=(
            "Optional sequence. Each op has type move/rotate/scale/mirror/array/align_to_grid. "
            "Example: [{type:'move', translation:[3000,0,0]}, {type:'array', count_x:4, spacing_x:8000}]."
        ),
    )
    copy_objects: bool = Field(False, alias="copy")
    translation: Optional[list[float]] = None
    angle_degrees: Optional[float] = None
    center: Optional[list[float]] = None
    axis: Optional[list[float]] = None
    scale_factor: Optional[float] = None
    base_point: Optional[list[float]] = None
    mirror_plane_start: Optional[list[float]] = None
    mirror_plane_end: Optional[list[float]] = None
    count_x: Optional[int] = None
    count_y: Optional[int] = None
    spacing_x: Optional[float] = None
    spacing_y: Optional[float] = None


class ModifyObjectInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: Optional[str] = None
    object_id: Optional[str] = None
    name: Optional[str] = None
    new_name: Optional[str] = None
    new_color: Optional[list[int]] = None
    new_layer: Optional[str] = None
    visible: Optional[bool] = None
    translation: Optional[list[float]] = None
    rotation: Optional[list[float]] = None
    scale: Optional[Any] = None


class BatchSubCommand(BaseModel):
    """One sub-command inside a batch. The plugin routes on `type`; `params` is passed verbatim.

    `type` must be one of the plugin command names - same names as the MCP tools
    (create_object, derive_floors_from_mass, create_core, transform_objects, modify_object,
    delete_objects, query_scene, setup_arch_layers, batch_layer_visibility, execute_script,
    undo, ...) plus any legacy commands listed in rhino://capabilities.

    `params` is the argument dict exactly as you'd pass to the corresponding standalone tool,
    EXCEPT that any string value may start with a `$N` reference to resolve to a prior result:
        "$1"                -> whole result dict of op 1
        "$1.object_ids[0]"  -> first GUID from op 1
        "$2.mass_id"        -> mass_id field from op 2
        "$3.bounding_box.min" -> nested path
    """
    model_config = ConfigDict(extra="forbid")
    type: str = Field(
        ...,
        description=(
            "Plugin command name. Same names as the MCP tools: create_object, "
            "derive_floors_from_mass, create_core, transform_objects, modify_object, "
            "delete_objects, query_scene, report_areas, place_openings_on_facade, "
            "align_to_grid, setup_arch_layers, batch_layer_visibility, create_layer, "
            "capture_viewport, set_view, set_display_mode, select_objects, get_cross_section, "
            "boolean_operation, execute_script, undo. Legacy commands also accepted."
        ),
    )
    params: dict[str, Any] = Field(
        default_factory=dict,
        description=(
            "Arguments for the command - same shape as calling the tool standalone. "
            "Any string value may be a $N reference to a prior op's result, e.g. "
            "'$1.object_ids[0]' or '$2.mass_id'."
        ),
    )


class BatchCommandInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    commands: list[BatchSubCommand] = Field(
        ...,
        min_length=1,
        description=(
            "Ordered list of sub-commands. Each has a `type` (the plugin command name) "
            "and a `params` dict. Reference earlier results with $N paths in param values."
        ),
    )
    atomic: bool = True
    stop_on_error: Optional[bool] = None


class DeriveFloorsFromMassInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    mass_id: str
    level_heights: list[float] = Field(default_factory=list)
    levels: Optional[int] = None
    level_height: float = Field(default=3000, gt=0)
    slab_thickness: float = Field(default=250, gt=0)
    start_z: Optional[float] = None
    layer: Optional[str] = "Slab"


class CreateCoreInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    boundary: list[list[float]] = Field(..., min_length=3)
    height: float = Field(default=3000, gt=0)
    z_level: Optional[float] = None
    wall_thickness: float = Field(default=200, gt=0)
    walls: list[dict[str, Any]] = Field(default_factory=list)
    modules: list[dict[str, Any]] = Field(default_factory=list)
    punch_through: list[str] = Field(default_factory=list)
    wall_layer: Optional[str] = "Core::Walls"
    shaft_layer: Optional[str] = "Core::Shafts"


class PlaceOpeningsInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    wall_ids: list[str] = Field(..., min_length=1)
    rhythm: float = Field(default=3000, gt=0)
    sill: float = 900
    head: float = 2400
    width: float = Field(default=1200, gt=0)
    height: Optional[float] = None
    margin: Optional[float] = None
    layer: Optional[str] = "Opening"


class AlignGridInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_ids: list[str] = Field(..., min_length=1)
    grid_spacing: float = Field(default=1000, gt=0)
    snap_z: bool = False


class ReportAreasInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    by: str = "layer"  # layer | level | name
    level_height: float = Field(default=3000, gt=0)


class LayerInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str
    color: Optional[list[int]] = None
    visible: bool = True
    locked: bool = False
    parent: Optional[str] = None


class SetupLayersInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    prefix: str = ""


class BatchLayerVisInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    show: list[str] = Field(default_factory=list)
    hide: list[str] = Field(default_factory=list)
    isolate: Optional[str] = None


class ObjectIdInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_id: str = Field(..., min_length=1)


class ObjectIdsInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_ids: list[str] = Field(..., min_length=1)


class MeasureDistInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    point_a: list[float]
    point_b: list[float]


class CheckIntInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_id_a: str
    object_id_b: str


class ValidateInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_ids: list[str] = Field(default_factory=list)
    layer: Optional[str] = Field(None, description="Validate only this layer.")
    name_pattern: Optional[str] = Field(None, description="Validate only objects whose name matches (trailing * allowed).")
    since_version: Optional[int] = Field(None, description="Validate only objects added/modified since this tracker version (from get_tracker_version). The usual case: check what you just built.")
    expect_shells: bool = Field(False, description="True when open geometry is intentional (single-surface roofs, vault webs, glazing). Open breps are then reported separately instead of as issues.")
    max_checks: int = Field(500, ge=1, le=20000, description="Cap on objects inspected per call.")


class AssertGeometryInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    assertions: list[dict[str, Any]] = Field(
        ...,
        min_length=1,
        description=(
            "Post-conditions to check. Each is {kind, selector, ...}. Kinds:\n"
            "  bbox      - {selector, z_max/z_min/x_max/x_min/y_max/y_min, tol} union bbox of the selection\n"
            "  envelope  - {selector, box:[[minx,miny,minz],[maxx,maxy,maxz]], tol} everything must fit inside\n"
            "  count     - {selector, expect} or {selector, min, max}\n"
            "  count_delta - {since_version, expect} added minus deleted since that tracker version\n"
            "  watertight- {selector} every match must be a closed valid solid\n"
            "  supported - {selector, max_gap} nothing may float\n"
            "Selectors: 'all', 'by_layer:Name', 'by_name:Prefix', 'last_created', 'selected', or GUIDs."
        ),
    )


class FindUnsupportedInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    selector: list[str] = Field(default_factory=lambda: ["all"], description="Scope: 'all', 'by_layer:Name', 'by_name:Prefix', 'last_created', or GUIDs.")
    max_gap: Optional[float] = Field(None, description="Largest acceptable gap under an object, in model units. Default ~10x document tolerance.")


class SectionPreviewInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    axis: str = Field("x", description="Cut plane normal: 'x', 'y' or 'z'.")
    station: Optional[float] = Field(None, description="Coordinate along the axis to cut at. Defaults to the scene centre.")
    width: int = 900
    height: int = 700
    display_mode: Optional[str] = Field(None, description="Display mode for the cut view (e.g. 'Technical', 'Shaded').")
    format: str = "auto"
    quality: int = Field(80, ge=1, le=100)
    as_json: bool = Field(False, description="Return base64 JSON instead of MCP image content.")


class WriteModuleInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str = Field(..., description="Module name without .py (letters, digits, underscore). Import it as `import <name>`.")
    source: str = Field(..., description="Full Python source. IronPython 2 syntax if you will use it from execute_script.")
    overwrite: bool = Field(True, description="Replace an existing module of the same name.")


class ModuleNameInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str


class DetectClashesInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_ids: list[str] = Field(default_factory=list, description="Scope to these objects (GUIDs or selectors like 'by_layer:Name'); empty = every solid in the scene.")
    layer: Optional[str] = Field(default=None, description="Restrict the scope to objects on this layer.")
    tolerance: Optional[float] = Field(default=None, description="Intersection tolerance in model units; defaults to the document absolute tolerance.")
    max_checks: int = Field(default=1500, ge=1, le=20000, description="Cap on Brep-Brep narrow-phase tests after the RTree broad phase.")
    include_touching: bool = Field(default=True, description="Include surface-touching contacts, not just hard solid interpenetrations.")
    solid_overlap: bool = Field(default=True, description="Classify hard interpenetration vs touch via boolean-intersection volume (a bit slower).")


class SemanticSelectInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    type: Optional[str] = Field(default=None, description="Element type: wall, slab, column, core, facade, opening (windows/doors), stair, massing, or 'all'.")
    level: Optional[int] = Field(default=None, description="Level index to filter to (from analyze_architecture / get_level_summary).")
    orientation: Optional[str] = Field(default=None, description="Facing direction: N, NE, E, SE, S, SW, W, NW (or words like 'south'). +Y is treated as North.")
    select: bool = Field(default=True, description="Also select the matching objects in Rhino.")
    clear_selection: bool = Field(default=True, description="Clear the current selection first (when select=True).")


class DeleteInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_ids: list[str] = Field(..., description="GUIDs to delete, or selectors: 'all', 'by_layer:Layer', 'by_name:Pattern', 'selected'.")


class CameraViewInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    location: Optional[list[float]] = Field(None, description="Temporary camera position [x, y, z].")
    target: Optional[list[float]] = Field(None, description="Temporary camera target [x, y, z].")
    direction: Optional[list[float]] = Field(None, description="Camera look direction. Use with target + distance.")
    distance: Optional[float] = Field(None, description="Distance from target when direction is supplied.")
    projection: Optional[str] = Field(None, description="'perspective' | 'parallel'. Defaults to current.")
    lens_length: Optional[float] = Field(None, description="Lens focal length in mm. 50=normal, 24=wide, 135=tele.")
    box_min: Optional[list[float]] = Field(None, description="Bounding box min [x,y,z] to frame.")
    box_max: Optional[list[float]] = Field(None, description="Bounding box max [x,y,z] to frame.")


class CaptureInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    width: int = 800
    height: int = 600
    max_bytes: int = 800000
    format: str = "auto"   # "auto" | "png" | "jpeg"
    quality: int = Field(default=80, ge=1, le=100)
    restore_state: bool = Field(default=True, description="Restore viewport camera and display mode after capture. Default True - the AI can inspect the model from any angle without disrupting the user's current view.")
    view: Optional[str | CameraViewInput] = Field(
        default=None,
        description=(
            "Temporarily switch to this named view before capturing "
            "(Top, Front, Right, Perspective, etc.). For camera overrides, pass "
            "{location:[x,y,z], target:[x,y,z], projection:'parallel|perspective'} "
            "or use capture_inspection_view."
        ),
    )
    display_mode: Optional[str] = Field(default=None, description="Temporarily switch to this display mode before capturing (Wireframe, Shaded, Rendered, Arctic, etc.). Restored if restore_state=True.")
    annotate: bool = Field(default=False, description="Include structured capture annotations: selected labels, bounding boxes, and layer colors.")
    annotation_scope: str = Field(default="selected", description="'selected' or 'visible'. Visible is capped by max_annotations.")
    max_annotations: int = Field(default=20, ge=0, le=200)
    as_json: bool = Field(default=False, description="Return the full JSON payload (base64 + metadata) instead of MCP image content. For clients that cannot render image blocks.")


class InspectionCaptureInput(CaptureInput):
    model_config = ConfigDict(extra="forbid")
    location: Optional[list[float]] = Field(None, description="Temporary camera position [x, y, z].")
    target: Optional[list[float]] = Field(None, description="Temporary camera target [x, y, z].")
    direction: Optional[list[float]] = Field(None, description="Camera look direction. Use with target + distance.")
    distance: Optional[float] = Field(None, description="Distance from target when direction is supplied.")
    projection: Optional[str] = Field(None, description="'perspective' | 'parallel'. Defaults to current.")
    lens_length: Optional[float] = Field(None, description="Lens focal length in mm. 50=normal, 24=wide, 135=tele.")
    box_min: Optional[list[float]] = Field(None, description="Bounding box min [x,y,z] to frame.")
    box_max: Optional[list[float]] = Field(None, description="Bounding box max [x,y,z] to frame.")


class ReviewCaptureInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    views: list[str] = Field(
        default_factory=lambda: ["hero", "plan", "front", "right", "detail"],
        description="Review views to capture. Options: hero, plan, front, right, left, back, detail.",
    )
    width: int = 1100
    height: int = 800
    max_bytes: int = 850000
    format: str = "auto"
    quality: int = Field(default=78, ge=1, le=100)
    display_mode: Optional[str] = Field(default=None, description="Temporary display mode for all captures.")
    include_annotations: bool = True
    annotation_scope: str = Field(default="selected", description="'selected' or 'visible'.")
    max_annotations: int = Field(default=25, ge=0, le=200)
    target: Optional[list[float]] = Field(None, description="Optional target point for hero/detail cameras.")
    distance: Optional[float] = Field(None, description="Optional camera distance for hero view.")
    box_min: Optional[list[float]] = Field(None, description="Optional bbox min for detail framing.")
    box_max: Optional[list[float]] = Field(None, description="Optional bbox max for detail framing.")


class BeforeAfterInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    commands: list[BatchSubCommand]
    atomic: bool = True
    stop_on_error: Optional[bool] = None
    capture: CaptureInput = Field(default_factory=lambda: CaptureInput(width=1000, height=720, max_bytes=850000, annotate=True))
    include_diff_image: bool = True


class ViewInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    view_name: str


class DisplayInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    mode: str


class SelectInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_ids: list[str]
    clear_selection: bool = True


class CrossSectionInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_id: str
    z_height: float
    layer: Optional[str] = None
    name: Optional[str] = None


class SectionProfileInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_id: str
    z_height: float = 0.0
    samples: int = Field(80, ge=8, le=300)


class SilhouetteInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_ids: Optional[list[str]] = None
    view: str = Field("front", description="top | front | right | left. Returns cheap SVG/polyline feedback.")


class LoftInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    curve_ids: list[str]
    loft_type: int = Field(0, description="0 Normal, 1 Loose, 2 Tight, 3 Straight.")
    closed: bool = False
    layer: Optional[str] = None
    name: Optional[str] = None
    measure: bool = False


class Sweep1Input(BaseModel):
    model_config = ConfigDict(extra="forbid")
    rail_id: str
    profile_ids: list[str]
    layer: Optional[str] = None
    name: Optional[str] = None


class Sweep2Input(BaseModel):
    model_config = ConfigDict(extra="forbid")
    rail1_id: str
    rail2_id: str
    profile_ids: list[str]
    layer: Optional[str] = None
    name: Optional[str] = None


class PipeInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    curve_id: str
    radius: float
    cap: bool = True
    layer: Optional[str] = None
    name: Optional[str] = None


class ExtrudeCurveInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    curve_id: str
    direction: list[float]
    cap: bool = True
    layer: Optional[str] = None
    name: Optional[str] = None
    measure: bool = False


class NetworkSurfaceInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    curve_ids: list[str]
    layer: Optional[str] = None
    name: Optional[str] = None
    measure: bool = False


class SpherePatchInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    center: list[float] = Field(default_factory=lambda: [0.0, 0.0, 0.0])
    radius: float = 1000.0
    u_start_deg: float = -45.0
    u_end_deg: float = 45.0
    v_start_deg: float = -20.0
    v_end_deg: float = 45.0
    u_count: int = Field(12, ge=4, le=64)
    v_count: int = Field(8, ge=4, le=64)
    layer: Optional[str] = None
    name: Optional[str] = None
    measure: bool = False


class TrimWithPlanesInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    object_id: str
    planes: list[Any] = Field(..., description="Each plane is {origin:[x,y,z], normal:[x,y,z]} or [a,b,c,d].")
    delete_input: bool = True
    auto_checkpoint: bool = True
    layer: Optional[str] = None
    name: Optional[str] = None


class BooleanInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    operation: str
    object_id_a: str
    object_id_b: str
    delete_input: bool = True


class ScriptInput(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)
    code: str = Field(..., description="Python code to run inside Rhino. Alias: script.")
    undo_name: Optional[str] = None
    default_layer: Optional[str] = None
    auto_checkpoint: bool = Field(
        default=True,
        description="Legacy switch: False is the same as checkpoint='off'.",
    )
    checkpoint: Optional[str] = Field(
        default=None,
        description=(
            "Snapshot policy for this call: 'auto' (default - skipped automatically when the "
            "scene hasn't changed, throttled on large documents), 'off' (read-only audits, "
            "diagnostics, anything that creates nothing), or 'force' (always snapshot, e.g. "
            "before a risky boolean). Full .3dm writes are expensive on big models - use 'off' "
            "for scripts that only measure or print."
        ),
    )
    rollback_on_error: bool = Field(
        default=False,
        description="If True, delete live objects created by a script that returns failure.",
    )
    timeout_seconds: Optional[int] = Field(
        default=None,
        ge=5,
        le=600,
        description="Override the execution budget (default 180s, max 600). Raise it for large parametric builds.",
    )


class Python3Input(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)
    code: str = Field(..., description="CPython 3 code to run in Rhino 8 via RhinoCode/rhinocode.")
    timeout_seconds: int = Field(default=45, ge=1, le=300)
    rhino_id: Optional[str] = Field(None, description="Optional rhinocode pipe id from `rhinocode list --json`.")
    keep_script: bool = Field(default=False, description="Keep the temporary .py file for debugging.")


class UndoInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    count: int = 1


class LogInput(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)
    limit: int = Field(50, alias="count", description="Max entries to return (default 50).")
    errors_only: bool = False


class SetCameraInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    location: Optional[list[float]] = Field(None, description="Camera position [x, y, z] in model units. Omit when using box_min/box_max.")
    target: Optional[list[float]] = Field(None, description="Camera target [x, y, z]. Omit when using box_min/box_max.")
    lens_length: Optional[float] = Field(None, description="Lens focal length in mm. 50=normal, 24=wide, 135=tele.")
    projection: Optional[str] = Field(None, description="'perspective' | 'parallel'. Defaults to current.")
    box_min: Optional[list[float]] = Field(None, description="Bounding box min [x,y,z] to zoom-frame. Provide with box_max - camera distance auto-computed.")
    box_max: Optional[list[float]] = Field(None, description="Bounding box max [x,y,z] to zoom-frame. Provide with box_min.")
    fit: Optional[str] = Field(
        None,
        description="Disambiguates when BOTH a bbox and a camera are given: 'bbox' frames box_min/box_max, 'camera' uses location/target. Without it, mixing the two modes is rejected.",
    )


class GetRhinoCommandsInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    filter: str = Field(default="", description="Case-insensitive substring filter. Empty = return all (capped to 200).")


class LayerMaterialInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    layer: str = Field(..., description="Layer name (full path or short name).")
    color: Optional[list[int]] = Field(None, description="Diffuse color [R, G, B] or [R, G, B, A], 0-255.")
    roughness: Optional[float] = Field(None, ge=0.0, le=1.0, description="PBR roughness 0=mirror, 1=matte.")
    metallic: Optional[float] = Field(None, ge=0.0, le=1.0, description="PBR metallic factor 0=dielectric, 1=metal.")
    opacity: Optional[float] = Field(None, ge=0.0, le=1.0, description="Opacity 0=transparent, 1=opaque.")
    emission: Optional[list[int]] = Field(None, description="Emissive color [R, G, B], 0-255.")


class RunCommandInput(BaseModel):
    model_config = ConfigDict(extra="forbid")
    command: str = Field(..., description="Rhino command string, exactly as typed in the command line (e.g. 'Contour', '_Box 0,0,0 1000,1000,3000').")
    echo: bool = Field(default=False, description="Echo the command to Rhino's command line. Default False (silent).")


# Capabilities Resource --------------------------------------------------
# Long-tail commands (still callable inside `batch`) and discoverable workflows.
CAPABILITIES: dict[str, Any] = {
    # "version", "tool_count", "tool_profile" and "plugin_commands" are filled in
    # dynamically by the capabilities() resource - never hand-edit counts here.
    "protocol_5": {
        "multiplex": "request_id-matched responses; reads/ping/cancel answer while long commands run",
        "idempotent_retry": "re-sent request_ids replay cached results instead of re-executing",
        "cancel": "cancel_operation stops the running command at its next checkpoint",
        "binary_image": "viewport captures travel as raw bytes (flag 0x02), not base64",
        "columnar_query": "query_scene(format='columnar') returns parallel arrays (40-60% fewer tokens)",
        "wal": "write-ahead log of mutating commands; get_recovery_log after a crash",
    },
    "tool_surface": "consolidated",
    "preferred_tools": [
        "query_scene", "create_object", "transform_objects", "batch", "report_areas",
        "capture_viewport", "get_viewport_image", "capture_review_set", "compare_before_after",
        "capture_inspection_view", "get_section_profile", "execute_python3",
        "loft_surface", "sphere_patch", "derive_floors_from_mass", "place_openings_on_facade",
    ],
    "mcneel_compatibility_aliases": {
        "get_viewport_image": "capture_viewport with metadata + image content",
        "list_objects": "query_scene(scope='objects')",
        "set_selection": "select_objects",
        "run_python": "execute_script",
    },
    "vision_loop": {
        "multi_angle_review": "capture_review_set returns hero/plan/elevation/detail image blocks in one call",
        "before_after_diff": "compare_before_after captures, edits, captures, and reports pixel-change metrics",
        "annotations": "capture tools can include selected/visible object labels, bboxes, and layer colors",
    },
    "python_engines": {
        "ironpython": "execute_script / run_python",
        "cpython3": "execute_python3 via Rhino 8.11+ RhinoCode rhinocode CLI; Developer mode required",
    },
    "universal_create_types": {
        "architecture": ["wall", "slab", "floor", "column", "opening", "window", "door", "roof", "massing", "building_mass", "core"],
        "primitives": ["point", "line", "polyline", "circle", "arc", "ellipse", "curve", "box", "sphere", "cone", "cylinder", "surface"],
    },
    "transform_operations": ["move", "rotate", "scale", "mirror", "array", "align_to_grid"],
    "object_selectors": ["selected", "all", "last_created", "by_layer:LayerName", "by_name:Pattern", "<guid>"],
    "batch_features": {
        "atomic": True,
        "rollback_on_failure": True,
        "reference_resolution": "$N or $N.path[i]",
        "examples": ["$1.object_ids[0]", "$2.mass_id", "$3.bounding_box.min"],
        "per_op_errors": True,
    },
    "etag": {
        "field": "scene_version",
        "use": "Compare across calls. Same version = scene unchanged. Skip redundant queries.",
    },
    "legacy_plugin_commands_available_via_batch": [
        "create_wall", "create_slab", "create_column", "create_opening", "create_roof",
        "create_box", "create_cylinder", "create_sphere", "create_line", "create_polyline",
        "loft", "sweep1", "pipe", "extrude_curve", "fillet_edges", "offset_curve",
        "extrude_curves", "join_curves", "offset_and_extrude", "move_objects",
        "rotate_objects", "scale_objects", "mirror_objects", "array_objects",
        "list_layers", "set_active_layer", "delete_layer",
        "set_object_layer", "get_context", "get_scene_summary", "get_objects",
        "get_object_details", "validate_architecture", "suggest_tools", "lint_script",
        "get_camera_target", "redo", "get_log_stats", "create_floor_stack",
        "group_objects", "ungroup_objects", "get_groups", "hollow_solid",
        "create_objects_batch",
    ],
    "examples": {
        "massing_first_move": {
            "tool": "create_object",
            "args": {
                "type": "massing",
                "params": {
                    "footprint": [[0, 0, 0], [30000, 0, 0], [30000, 18000, 0], [0, 18000, 0]],
                    "levels": 4,
                    "level_height": 3600,
                },
                "layer": "Massing",
                "name": "Office_4L",
            },
        },
        "atomic_office_in_one_call": {
            "tool": "batch",
            "args": {
                "atomic": True,
                "commands": [
                    {"type": "create_object", "params": {"type": "massing", "params": {
                        "footprint": [[0, 0, 0], [30000, 0, 0], [30000, 18000, 0], [0, 18000, 0]],
                        "levels": 4, "level_height": 3600,
                    }}},
                    {"type": "derive_floors_from_mass", "params": {
                        "mass_id": "$1.mass_id",
                        "level_heights": [4200, 3600, 3600, 3600],
                    }},
                ],
            },
        },
        "facade_in_one_call": {
            "tool": "place_openings_on_facade",
            "args": {
                "wall_ids": ["by_layer:Wall"],
                "rhythm": 3000,
                "width": 1500,
                "sill": 900,
                "head": 2400,
            },
        },
    },
}


# Resource --------------------------------------------------
@mcp.resource("rhino://capabilities")
async def capabilities() -> str:
    """Long-tail capabilities, examples, legacy command names, preferred workflows.

    Resources are returned as serialized text; this one ships as JSON for easy parsing.
    Version, tool count and profile are computed - never hand-maintained. When Rhino
    is reachable, plugin_commands is fetched live from the plugin dispatch table
    (list_commands) so it can never drift from the actual C# registry.
    """
    caps = dict(CAPABILITIES)
    caps["version"] = _VERSION
    caps["tool_profile"] = _TOOL_PROFILE
    caps["tool_count"] = _exposed_tool_count()
    caps["safe_mode"] = _SAFE_MODE
    caps["safe_mode_blocked_tools"] = sorted(_SAFE_MODE_BLOCKED)
    try:
        live = await _exec_simple("list_commands", {})
        if live.get("status") == "ok" and live.get("commands"):
            caps["plugin_commands"] = live["commands"]
            caps["plugin_commands_source"] = "live (plugin dispatch table)"
    except Exception:
        pass  # Rhino not running - the static legacy list below still applies.
    return orjson.dumps(caps).decode("utf-8")


@mcp.resource("rhino://arch-defaults")
async def arch_defaults_resource() -> dict:
    """Standard architectural defaults: wall thicknesses, opening sizes, layer names."""
    return {
        "wall": {"height": 3000, "thickness": 200},
        "slab": {"thickness": 200},
        "column": {"width": 400, "depth": 400, "height": 3000},
        "door": {"width": 900, "height": 2100},
        "window": {"width": 1200, "height": 1500, "sill": 900},
        "roof": {"thickness": 200},
        "massing": {"level_height": 3000, "core_layer": "Core"},
        "layers": ["Wall", "Slab", "Column", "Beam", "Opening", "Roof", "Stair", "Furniture", "Site", "Grid", "Annotation", "Massing", "Core::Walls", "Core::Shafts"],
    }


# Tools --------------------------------------------------

@mcp.tool(name="ping", annotations=RO)
async def ping(params: Optional[Empty] = None) -> dict:
    """Health check — verify Rhino bridge is reachable on 127.0.0.1:9544.

    Returns: bridge status, Rhino version, document name, model units, scene_version (etag),
    protocol version, safe_mode flag, MCP server Python path, and dependency status.

    Cheap (sub-ms). Call at conversation start and to check if scene has changed (etag)."""
    import sys as _sys
    try:
        async def _probe() -> dict:
            conn = await get_connection()
            return await conn.ping()

        data = await asyncio.wait_for(_probe(), timeout=10.0)
        data["capabilities_resource"] = "rhino://capabilities"
        data["safe_mode"] = _SAFE_MODE
        data["mcp_python"] = _sys.executable
        data["mcp_version"] = _VERSION
        data["tool_profile"] = _TOOL_PROFILE
        # Check optional dependencies without importing them. Importing cv2/numpy during
        # a health check can be surprisingly slow on Windows and made MCP ping appear hung.
        dep_status = {}
        for pkg, module_name in {"pymupdf": "fitz", "cv2": "cv2", "numpy": "numpy"}.items():
            dep_status[pkg] = "installed" if importlib.util.find_spec(module_name) else "missing"
        data["optional_dependencies"] = dep_status
        # Script engines: state availability outright so an agent never has to infer
        # it from a version string in a tool description (field report R1/S2).
        mode = str(data.get("mode") or "").lower()
        rhino_ver = _parse_rhino_version(data.get("rhino_version"))
        rhinocode = _find_rhinocode()
        py3_reasons = []
        if rhino_ver is None:
            py3_reasons.append("could not parse the Rhino version")
        elif rhino_ver < (8, 11):
            py3_reasons.append(
                f"RhinoCode needs Rhino 8.11+, found {data.get('rhino_version')}"
            )
        if rhinocode is None:
            py3_reasons.append("rhinocode CLI not found on PATH or in Rhino 8/System")
        if mode != "developer":
            py3_reasons.append(f"AIBridge mode is '{mode or 'unknown'}', execute_python3 needs 'developer'")
        data["script_engines"] = {
            "ironpython": {
                "available": True,
                "tool": "execute_script",
                "version": "IronPython 2.7",
                "note": "No f-strings, type hints, or py3-only stdlib. `rab` helpers are auto-imported.",
            },
            "python3": {
                "available": not py3_reasons,
                "tool": "execute_python3",
                "engine": "RhinoCode CPython 3",
                "reason": "; ".join(py3_reasons) if py3_reasons else "ready",
                "rhinocode_path": rhinocode,
            },
            "rab_helpers": {
                "available": _RAB_ENABLED,
                "note": "Auto-imported into execute_script. rab.wall/slab/column/grid/arch/vault/tracery/...",
            },
        }
        # Protocol 4.x (legacy single-flight) and 5.x (multiplexed) are both supported.
        plugin_ver = data.get("protocol_version", "")
        plugin_major = plugin_ver.split(".", 1)[0] if plugin_ver else ""
        if plugin_ver and plugin_major not in ("4", "5"):
            data["version_warning"] = (f"MCP server expects protocol 4.x/5.x; plugin reports {plugin_ver}. "
                                       "Update the .rhp plugin for full compatibility.")
        elif plugin_major == "4":
            data["version_note"] = ("Plugin is protocol 4 (legacy single-flight). Update the .rhp to v4.8 "
                                    "for multiplexing, idempotent retries, cancellation and binary frames.")
        try:
            conn = await get_connection()
            data["client_mode"] = "multiplexed" if conn._server_multiplex else "legacy"
        except Exception:
            pass
        return data
    except asyncio.TimeoutError:
        return {"status": "error",
                "error_code": "RHINO_PING_TIMEOUT",
                "message": "Timed out waiting for Rhino AIBridge to answer ping.",
                "recoverable": True,
                "hint": "Run AIBridgeStop then AIBridge in Rhino. If Rhino is closing or hidden, close it in Task Manager and reopen."}
    except Exception as e:
        return {"status": "error", "message": str(e),
                "hint": "Is Rhino running with the AIBridge plugin loaded? Check 127.0.0.1:9544."}


@mcp.tool(name="query_scene", annotations=RO)
async def query_scene(params: QuerySceneInput) -> dict:
    """Universal scene query - replaces get_context, get_scene_summary, get_objects, list_layers.

    scope='summary' for full scene summary (counts, bbox, layers).
    scope='layers' for layer list with counts.
    scope='objects' (default) with filter={layer, type, name_pattern} and detail=ids/summary/full.

    Phase 2: served from the snapshot cache, so all branches are O(1) or O(M) rather than O(N).
    Returns scene_version - use it as an etag across calls."""
    return await _exec_simple("query_scene", params.model_dump(exclude_none=True))


@mcp.tool(name="list_objects", annotations=RO)
async def list_objects(params: QuerySceneInput) -> dict:
    """McNeel-compatible alias for object listing. Uses query_scene(scope='objects')."""
    data = params.model_dump(exclude_none=True)
    data["scope"] = "objects"
    return await _exec_simple("query_scene", data)


@mcp.tool(name="create_object", annotations=WR)
async def create_object(params: CreateObjectInput) -> dict:
    """Universal creation tool. Prefer this over primitive-specific tools.

    Architecture types route to specialized creators (wall, slab, column, opening, roof,
    massing, core). Primitives (box, sphere, cylinder, etc.) go through the generic path.

    Returns object_ids and bounding_box. Pass measure=true to also compute area/volume
    (off by default - saves a Brep integration on every floor of a 30-floor stack).

    UNITS: every example below is in MILLIMETRES. Values are raw model units and are never
    converted - in a metres document use 6.0 rather than 6000. Call ping first and read
    unit_system. The response echoes unit_system plus a warning if the new geometry looks
    off by ~1000x for the document's units.

    Examples:
    - type='massing', params={footprint:[[0,0,0],[30000,0,0],[30000,18000,0],[0,18000,0]], levels:4, level_height:3600}
    - type='wall', params={start_point:[0,0,0], end_point:[6000,0,0], height:3000, thickness:200}
    - type='box', params={origin:[0,0,0], size_x:8000, size_y:8000, size_z:3600}
    - type='core', params={boundary:[[9000,6000,0],[15000,6000,0],[15000,12000,0],[9000,12000,0]], height:14400}
    """
    return await _exec_simple("create_object", params.model_dump(exclude_none=True))


@mcp.tool(name="transform_objects", annotations=WR)
async def transform_objects(params: TransformObjectsInput) -> dict:
    """Universal transform tool - replaces move/rotate/scale/mirror/array.

    For one transform, use shorthand fields. For chained transforms, use operations[]:
    each op's output object_ids feed the next, so you can move-then-array in a single call.

    Selectors: 'selected', 'all', 'last_created', 'by_layer:Wall', 'by_name:Floor*', or GUIDs."""
    return await _exec_simple("transform_objects", params.model_dump(exclude_none=True, by_alias=True))


@mcp.tool(name="modify_object", annotations=WR)
async def modify_object(params: ModifyObjectInput) -> dict:
    """Rename, recolor, change layer, show/hide, or apply a simple transform to one object."""
    return await _exec_simple("modify_object", params.model_dump(exclude_none=True))


@mcp.tool(name="batch", annotations=WR)
async def batch(params: BatchCommandInput) -> dict:
    """Run many Rhino commands in one round-trip. Supports atomic rollback and $N references.

    WHEN TO USE BATCH (already know all params upfront):
    - Creating many independent objects (walls, slabs, columns in bulk)
    - Layer setup, material assignment, bulk visibility changes
    - Linked ops via $N reference (e.g. massing -> derive_floors in one shot)

    WHEN TO USE INDIVIDUAL TOOL CALLS INSTEAD (step-by-step is more accurate):
    - You need to READ a result before deciding the next step (inspect IDs, bbox, count)
    - Complex boolean/modification ops where geometry must be verified first
    - Placing openings referencing wall IDs returned from a previous create
    - Any workflow needing capture_viewport or validate_objects between steps
    - Debugging: one tool at a time isolates failures
    - Any op where a wrong param would be hard to undo

    RULE: if the next command depends on INSPECTING this command's output (not just
    chaining IDs via $N), use individual calls. If you already know all params, batch.

    Each sub-command: {"type": "<command_name>", "params": {...}}

    References: "$N" resolves to the Nth (1-indexed) prior result:
        $1.object_ids[0]      -> first GUID created by op 1
        $2.mass_id            -> mass_id field from op 2

    With atomic=True: whole batch rolls back on any failure (one undo record).
    With atomic=False: each sub-op commits independently - use for large bulk builds.
    Legacy commands (any name from rhino://capabilities) are callable inside batch."""
    raw_commands = [c.model_dump() for c in params.commands]
    return await _exec_batch(raw_commands, atomic=params.atomic, stop_on_error=params.stop_on_error)


# Architect intelligence layer --------------------------------------------------

@mcp.tool(name="derive_floors_from_mass", annotations=WR)
async def derive_floors_from_mass(params: DeriveFloorsFromMassInput) -> dict:
    """Section a massing solid at floor heights and extrude each section into a slab.

    Variable level_heights[] for non-uniform floor heights (e.g. taller ground floor).
    Pair with create_object(type='massing') in a batch - chain via $1.mass_id."""
    return await _exec_simple("derive_floors_from_mass", params.model_dump(exclude_none=True))


@mcp.tool(name="create_core", annotations=WR)
async def create_core(params: CreateCoreInput) -> dict:
    """Create a building core as a unit - perimeter walls plus lift, stair, and shaft modules.

    Optional punch_through[] subtracts the core modules from listed massing solids,
    carving the actual voids in your floor stack."""
    return await _exec_simple("create_core", params.model_dump(exclude_none=True))


@mcp.tool(name="place_openings_on_facade", annotations=WR)
async def place_openings_on_facade(params: PlaceOpeningsInput) -> dict:
    """Distribute repeated openings (windows or doors) along walls at a constant rhythm.

    The whole facade in one call. Pass wall_ids=['by_layer:Wall'] to facade-ize every wall."""
    return await _exec_simple("place_openings_on_facade", params.model_dump(exclude_none=True))


@mcp.tool(name="align_to_grid", annotations=WR)
async def align_to_grid(params: AlignGridInput) -> dict:
    """Snap object bounding-box centers to an architectural grid. snap_z controls vertical."""
    return await _exec_simple("align_to_grid", params.model_dump())


@mcp.tool(name="report_areas", annotations=RO)
async def report_areas(params: ReportAreasInput) -> dict:
    """GFA / NFA-style area schedule grouped by layer, level, or name.

    For solid Breps with known volume and bbox height, plan_area = volume / height.
    Falls back to top-face area, then to bbox footprint."""
    return await _exec_simple("report_areas", params.model_dump())


# Layers --------------------------------------------------

@mcp.tool(name="create_layer", annotations=WI)
async def create_layer(params: LayerInput) -> dict:
    """Create or update a layer."""
    return await _exec_simple("create_layer", params.model_dump(exclude_none=True))


@mcp.tool(name="setup_arch_layers", annotations=WI)
async def setup_arch_layers(params: SetupLayersInput) -> dict:
    """Create the standard architectural layer set: Wall, Slab, Column, Beam, Opening, Roof, Stair, etc."""
    return await _exec_simple("setup_arch_layers", {"prefix": params.prefix})


@mcp.tool(name="batch_layer_visibility", annotations=WI)
async def batch_layer_visibility(params: BatchLayerVisInput) -> dict:
    """Show/hide/isolate layers in one call."""
    return await _exec_simple("batch_layer_visibility", params.model_dump(exclude_none=True))


# Analysis --------------------------------------------------

@mcp.tool(name="measure_object", annotations=RO)
async def measure_object(params: ObjectIdInput) -> dict:
    """Measure area, volume, length, and bounding box for one object."""
    return await _exec_simple("measure_object", {"object_id": params.object_id})


@mcp.tool(name="measure_distance", annotations=RO)
async def measure_distance(params: MeasureDistInput) -> dict:
    """Distance between two points."""
    return await _exec_simple("measure_distance", params.model_dump())


@mcp.tool(name="check_intersection", annotations=RO)
async def check_intersection(params: CheckIntInput) -> dict:
    """Check whether two Rhino objects intersect (bounding-box check)."""
    return await _exec_simple("check_intersection", params.model_dump())


@mcp.tool(name="validate_objects", annotations=RO)
async def validate_objects(params: ValidateInput) -> dict:
    """Check geometry health. Separates REAL corruption from mere openness.

    - `invalid`: corrupt breps - always fix these.
    - `open`: not closed. Legitimate for single-surface roofs, vault webs and glazing.
      Each entry carries naked_edge_count/naked_edge_length, so a hairline gap (short
      total length on something that should be solid) is distinguishable from a
      deliberately open surface. Pass expect_shells=true when open geometry is intended
      and they stop being counted as issues.

    Scope it instead of scanning the whole scene: `layer`, `name_pattern`, or
    `since_version` (from get_tracker_version) to validate only what you just created."""
    return await _exec_simple("validate_objects", params.model_dump(exclude_none=True))


@mcp.tool(name="assert_geometry", annotations=RO)
async def assert_geometry(params: AssertGeometryInput) -> dict:
    """Assert what the geometry MEANS - catches the errors brep validity cannot.

    Agents generating thousands of parametric objects make arithmetic and wiring
    mistakes (doubled base heights, swapped arguments, cutters added instead of
    subtracted) that produce perfectly valid, closed, non-degenerate solids.
    Run this right after a generation step, while the fix is still cheap.

    Returns pass/fail per assertion with the offending object ids.

    Example - a floor of vault webs that must crown at 33.0m, stay inside the
    building envelope, and not float:
        assertions=[
          {"kind":"bbox","selector":"by_name:nave_web","z_max":33.0,"tol":0.01},
          {"kind":"envelope","selector":"all","box":[[0,-24,-1],[130,24,97]]},
          {"kind":"supported","selector":"last_created","max_gap":0.15},
          {"kind":"count","selector":"by_layer:Vault","expect":60}
        ]"""
    return await _exec_simple("assert_geometry", params.model_dump(exclude_none=True))


@mcp.tool(name="find_unsupported", annotations=RO)
async def find_unsupported(params: FindUnsupportedInput) -> dict:
    """Find objects floating in space - nothing beneath them within max_gap.

    Catches floating spires, pinnacles, statues and disconnected drums: the defect
    class that otherwise survives until a human notices it in a render. Results are
    sorted worst-gap first. Bbox-based, so deliberately cantilevered or interlocking
    geometry can appear as a false positive."""
    return await _exec_simple("find_unsupported", params.model_dump(exclude_none=True))


@mcp.tool(name="section_preview", annotations=RO)
async def section_preview(params: SectionPreviewInput) -> Any:
    """Cheap interior inspection: clip at a station, look square at the cut, restore.

    One call to see inside the model - open shafts, floating drums, missing floors,
    roof planes that stop short. Far faster than hunting camera angles, and unlike
    create_section/cut_section it creates no permanent geometry: the clipping plane
    and camera are always restored."""
    payload = params.model_dump(exclude_none=True)
    as_json = payload.pop("as_json", False)
    result = await _exec_simple("section_preview", payload)
    return result if as_json else _as_mcp_image(result)


# Reusable code substrate --------------------------------------------------
# Modules are written by the SERVER into the AIBridge directory that the script
# bootstrap already puts on sys.path. Nothing opens a file inside Rhino, so the
# IronPython handle-leak that used to lock library files can't happen.

_MODULE_NAME_OK = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
_RESERVED_MODULES = {"rab", "os", "sys", "io", "re", "math", "json", "Rhino", "System"}


@mcp.tool(name="write_module", annotations=WI)
async def write_module(params: WriteModuleInput) -> dict:
    """Save a reusable Python module that execute_script can import.

    Write your geometry library ONCE, then `import mylib` (or `rab.use('mylib')` to
    hot-reload) in every later script instead of re-pasting hundreds of lines.

    The file is written by the MCP server, never by code running inside Rhino, so
    it cannot leave a locked handle behind. Re-writing the same name is safe -
    use `rab.use(name)` to pick up the new version without restarting Rhino.

    SCOPE: modules are stored per USER, not per document, so a generic name like
    `site` or `build` will be silently shared by every project. Prefix names with
    the project (`zb_tower`, `nd_vault`) to avoid collisions.

    NOTE: `rab.use(name)` re-imports the module fresh, which discards any runtime
    monkey-patching you did to it — re-apply patches after reloading, or better,
    put the parameter you are iterating on in the module itself."""
    name = params.name.strip()
    if not _MODULE_NAME_OK.match(name):
        return {"status": "error", "error_code": "INVALID_MODULE_NAME",
                "message": "Module names must be a valid Python identifier (letters, digits, underscore; no .py)."}
    if name in _RESERVED_MODULES:
        return {"status": "error", "error_code": "RESERVED_MODULE_NAME",
                "message": f"'{name}' would shadow a built-in or the rab helpers. Choose another name."}
    try:
        compile(params.source, f"{name}.py", "exec")
    except SyntaxError as e:
        # Only catches py3-parseable errors; IronPython-2-only issues still surface at import.
        return {"status": "error", "error_code": "SYNTAX_ERROR",
                "message": f"line {e.lineno}: {e.msg}",
                "retry_hint": "Fix the syntax and resend. Remember execute_script runs IronPython 2."}
    path = _aibridge_dir() / f"{name}.py"
    if path.exists() and not params.overwrite:
        return {"status": "error", "error_code": "MODULE_EXISTS",
                "message": f"Module '{name}' already exists. Pass overwrite=true to replace it."}
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(params.source, encoding="utf-8")
    except OSError as e:
        return {"status": "error", "error_code": "WRITE_FAILED", "message": str(e)}
    non_ascii = [i + 1 for i, line in enumerate(params.source.splitlines())
                 if any(ord(ch) > 127 for ch in line)]
    out = {
        "status": "ok",
        "module": name,
        "path": str(path),
        "lines": len(params.source.splitlines()),
        "usage": f"In execute_script: `import {name}` (first use) or `{name} = rab.use('{name}')` to hot-reload.",
    }
    if non_ascii:
        out["warning"] = (f"Non-ASCII characters on lines {non_ascii[:5]} - IronPython 2 rejects them "
                          "unless the file starts with a coding declaration. Prefer plain ASCII.")
    return out


@mcp.tool(name="list_modules", annotations=RO)
async def list_modules(params: Optional[Empty] = None) -> dict:
    """List reusable modules available to execute_script (written via write_module)."""
    d = _aibridge_dir()
    mods = []
    try:
        for f in sorted(d.glob("*.py")):
            mods.append({
                "module": f.stem,
                "lines": len(f.read_text(encoding="utf-8", errors="replace").splitlines()),
                "bytes": f.stat().st_size,
                "builtin": f.stem == "rab",
            })
    except OSError as e:
        return {"status": "error", "message": str(e)}
    return {"status": "ok", "modules": mods, "count": len(mods), "directory": str(d)}


@mcp.tool(name="read_module", annotations=RO)
async def read_module(params: ModuleNameInput) -> dict:
    """Read back the source of a saved module."""
    path = _aibridge_dir() / f"{params.name.strip()}.py"
    if not path.is_file():
        return {"status": "error", "error_code": "MODULE_NOT_FOUND",
                "message": f"No module '{params.name}'. Call list_modules to see what exists."}
    return {"status": "ok", "module": params.name, "path": str(path),
            "source": path.read_text(encoding="utf-8")}


@mcp.tool(name="detect_clashes", annotations=RO)
async def detect_clashes(params: DetectClashesInput) -> dict:
    """Real clash / coordination check. Broad phase: an RTree over bounding boxes finds candidate
    pairs; narrow phase: a true Brep-Brep intersection (not just bbox overlap) with tolerance
    confirms real contact. Returns each clashing pair with a contact point, intersection length,
    and kind ('overlap' = hard interpenetration, 'touch'/'intersect' = surfaces meet). Scope with
    object_ids/layer; empty scope checks every solid in the scene."""
    return await _exec_simple("detect_clashes", params.model_dump(exclude_none=True))


@mcp.tool(name="select_by_semantic", annotations=RO)
async def select_by_semantic(params: SemanticSelectInput) -> dict:
    """Semantic selection by type + level + facing orientation -- e.g. 'all south-facing windows on
    level 3' is type='opening', level=3, orientation='S'. Orientation is derived from each element's
    geometry (largest near-vertical face normal); +Y is treated as North. Optionally selects the
    matches in Rhino and always returns their ids with type/level/orientation."""
    return await _exec_simple("select_by_semantic", params.model_dump(exclude_none=True))


# Viewport --------------------------------------------------

@mcp.tool(name="capture_viewport", annotations=RO)
async def capture_viewport(params: CaptureInput) -> Any:
    """Capture the active viewport as JPEG (default for shaded) or PNG (default for wireframe).

    Phase 1 - no disk round-trip. format='auto' picks based on display mode.
    restore_state=True (default) saves and restores the viewport camera + display mode after
    capture, so inspecting the model from any angle never disrupts the user's current view.
    Pass view='Top' and/or display_mode= to temporarily switch before capturing.
    For explicit camera overrides, either use capture_inspection_view or pass
    view={location:[x,y,z], target:[x,y,z], projection:'parallel|perspective'}.
    Pass as_json=true to get the base64 JSON payload instead of MCP image content."""
    payload = params.model_dump(exclude_none=True)
    as_json = payload.pop("as_json", False)
    if isinstance(payload.get("view"), dict):
        view_payload = payload.pop("view")
        payload.update(view_payload)
        result = await _exec_simple("capture_inspection_view", payload)
    else:
        result = await _exec_simple("capture_viewport", payload)
    return result if as_json else _as_mcp_image(result)


@mcp.tool(name="capture_viewport_json", annotations=RO)
async def capture_viewport_json(params: CaptureInput) -> dict:
    """Capture the viewport and return the full JSON payload with base64 + metadata.

    Alias for capture_viewport(as_json=true) - kept for backward compatibility (full profile only)."""
    payload = params.model_dump(exclude_none=True)
    payload.pop("as_json", None)
    if isinstance(payload.get("view"), dict):
        view_payload = payload.pop("view")
        payload.update(view_payload)
        return await _exec_simple("capture_inspection_view", payload)
    return await _exec_simple("capture_viewport", payload)


@mcp.tool(name="get_viewport_image", annotations=RO)
async def get_viewport_image(params: CaptureInput) -> Any:
    """McNeel-compatible viewport capture: returns metadata text plus an image content block."""
    payload = params.model_dump(exclude_none=True)
    payload.pop("as_json", None)
    if isinstance(payload.get("view"), dict):
        view_payload = payload.pop("view")
        payload.update(view_payload)
        result = await _exec_simple("capture_inspection_view", payload)
    else:
        result = await _exec_simple("capture_viewport", payload)
    return _image_with_metadata(result)


@mcp.tool(name="capture_inspection_view", annotations=RO)
async def capture_inspection_view(params: InspectionCaptureInput) -> Any:
    """Temporarily inspect from a requested camera, return an image, then restore the viewport.

    Pass as_json=true to get the base64 JSON payload instead of MCP image content."""
    payload = params.model_dump(exclude_none=True)
    as_json = payload.pop("as_json", False)
    result = await _exec_simple("capture_inspection_view", payload)
    return result if as_json else _as_mcp_image(result)


@mcp.tool(name="capture_inspection_view_json", annotations=RO)
async def capture_inspection_view_json(params: InspectionCaptureInput) -> dict:
    """Inspection capture with full JSON metadata instead of image-only content.

    Alias for capture_inspection_view(as_json=true) - kept for backward compatibility (full profile only)."""
    payload = params.model_dump(exclude_none=True)
    payload.pop("as_json", None)
    return await _exec_simple("capture_inspection_view", payload)


@mcp.tool(name="capture_review_set", annotations=RO)
async def capture_review_set(params: ReviewCaptureInput) -> Any:
    """Capture a multi-angle architectural review set in one call.

    Returns metadata + image blocks for hero, plan, elevation, and detail views.
    Use this after geometry edits so the AI can self-debug without asking the user
    to rotate the Rhino viewport.
    """
    target = params.target or [0.0, 0.0, 0.0]
    box_min = params.box_min
    box_max = params.box_max
    distance = params.distance or 22000.0
    display_mode = params.display_mode

    # Try to use the actual scene bbox when the caller did not provide one.
    if box_min is None or box_max is None or params.target is None:
        scene = await _exec_simple("query_scene", {"scope": "summary"})
        bbox = scene.get("bbox") or scene.get("bounding_box") or {}
        mn = bbox.get("min") if isinstance(bbox, dict) else None
        mx = bbox.get("max") if isinstance(bbox, dict) else None
        if isinstance(mn, list) and isinstance(mx, list) and len(mn) >= 3 and len(mx) >= 3:
            box_min = box_min or mn[:3]
            box_max = box_max or mx[:3]
            if params.target is None:
                target = [(mn[i] + mx[i]) / 2.0 for i in range(3)]
                diag = sum((mx[i] - mn[i]) ** 2 for i in range(3)) ** 0.5
                distance = params.distance or max(diag * 1.6, 1000.0)

    base = {
        "width": params.width,
        "height": params.height,
        "max_bytes": params.max_bytes,
        "format": params.format,
        "quality": params.quality,
        "display_mode": display_mode,
        "annotate": params.include_annotations,
        "annotation_scope": params.annotation_scope,
        "max_annotations": params.max_annotations,
    }
    view_map: dict[str, dict[str, Any]] = {
        "hero": {
            **base, "target": target, "direction": [1.0, -1.0, -0.55],
            "distance": distance, "projection": "perspective",
        },
        "plan": {**base, "view": "Top", "display_mode": display_mode or "Technical"},
        "front": {**base, "view": "Front", "display_mode": display_mode or "Technical"},
        "right": {**base, "view": "Right", "display_mode": display_mode or "Technical"},
        "left": {**base, "view": "Left", "display_mode": display_mode or "Technical"},
        "back": {**base, "view": "Back", "display_mode": display_mode or "Technical"},
        "detail": {
            **base, "box_min": box_min, "box_max": box_max,
            "projection": "parallel", "display_mode": display_mode or "Shaded",
        },
    }

    content: list[Any] = [{
        "status": "ok",
        "review_set": params.views,
        "target": target,
        "box_min": box_min,
        "box_max": box_max,
        "note": "Each following metadata block is immediately followed by its image content.",
    }]
    for requested in params.views:
        name = requested.strip().lower()
        payload = view_map.get(name)
        if payload is None:
            content.append({"status": "error", "capture_name": requested, "message": f"Unknown review view '{requested}'."})
            continue
        if name == "detail" and (not box_min or not box_max):
            payload = {**base, "target": target, "direction": [1.0, -1.0, -0.55], "distance": distance, "projection": "perspective"}
        command = "capture_viewport" if "view" in payload else "capture_inspection_view"
        result = await _exec_simple(command, {k: v for k, v in payload.items() if v is not None})
        content.extend(_named_image_content(name, result))
    return content


@mcp.tool(name="compare_before_after", annotations=WR)
async def compare_before_after(params: BeforeAfterInput) -> Any:
    """Capture before, run a batch, capture after, and return visual diff metrics.

    This is the model's QA loop: see the starting state, perform edits atomically,
    see the result, and get a coarse pixel-change score in one tool call.
    """
    capture_params = params.capture.model_dump(exclude_none=True)
    capture_params.pop("as_json", None)
    capture_params["restore_state"] = True
    before = await _exec_simple("capture_viewport", capture_params)
    raw_commands = [c.model_dump() for c in params.commands]
    batch_result = await _exec_batch(raw_commands, atomic=params.atomic, stop_on_error=params.stop_on_error)
    after = await _exec_simple("capture_viewport", capture_params)
    # cv2 decode/diff is CPU-bound - run off the event loop so concurrent
    # protocol-5 calls (ping, reads) stay responsive.
    diff = await asyncio.to_thread(
        _compare_base64_images, before.get("image_base64"), after.get("image_base64")
    )
    batch_ok = batch_result.get("status") == "ok"
    rolled_back = bool(batch_result.get("rolled_back"))

    meta = {
        "status": "ok" if batch_ok else "error",
        "batch": batch_result,
        "batch_applied": batch_ok and not rolled_back,
        "rolled_back": rolled_back,
        "diff": {k: v for k, v in diff.items() if k != "diff_base64"},
        "diff_interpretation": (
            "Diff reflects the committed edit."
            if batch_ok and not rolled_back
            else "Diff is diagnostic only; the batch failed or rolled back."
        ),
        "note": "Returned content order: metadata, before image, after image, optional diff heatmap.",
    }
    content: list[Any] = [meta]
    content.extend(_named_image_content("before", before))
    content.extend(_named_image_content("after", after))
    if params.include_diff_image and diff.get("status") == "ok" and diff.get("diff_base64"):
        content.extend(_named_image_content("diff_heatmap", diff, key="diff_base64", default_format="png"))
    return content


@mcp.tool(name="set_view", annotations=WI)
async def set_view(params: ViewInput) -> dict:
    """Switch viewport to a named projection: Top, Front, Right, Left, Back, Perspective."""
    return await _exec_simple("set_view", params.model_dump())


@mcp.tool(name="set_display_mode", annotations=WI)
async def set_display_mode(params: DisplayInput) -> dict:
    """Set the active viewport display mode: Wireframe, Shaded, Rendered, Arctic, Ghosted, etc."""
    return await _exec_simple("set_display_mode", params.model_dump())


@mcp.tool(name="select_objects", annotations=WI)
async def select_objects(params: SelectInput) -> dict:
    """Select objects by GUID. clear_selection=True (default) deselects everything first."""
    return await _exec_simple("select_objects", params.model_dump())


@mcp.tool(name="set_selection", annotations=WI)
async def set_selection(params: SelectInput) -> dict:
    """McNeel-compatible alias for selecting objects by GUID."""
    return await _exec_simple("select_objects", params.model_dump())


@mcp.tool(name="set_camera", annotations=WI)
async def set_camera(params: SetCameraInput) -> dict:
    """Precisely position the viewport camera.

    Two MUTUALLY EXCLUSIVE modes:
    1. Explicit camera: location + target (+ optional lens_length, projection).
    2. Bbox framing: box_min + box_max - camera distance auto-computed to fit the box.

    Passing both is ambiguous and is rejected unless you also pass fit='bbox' or
    fit='camera' to say which one wins. (Previously the server silently picked one.)

    Examples:
        set_camera(location=[10000, -15000, 8000], target=[0, 0, 3000])
        set_camera(box_min=[0,0,0], box_max=[12000,8000,15000], projection="perspective")"""
    data = params.model_dump(exclude_none=True)
    fit = data.pop("fit", None)
    has_box = "box_min" in data or "box_max" in data
    has_cam = "location" in data or "target" in data
    if has_box and has_cam:
        if fit == "bbox":
            data.pop("location", None)
            data.pop("target", None)
        elif fit == "camera":
            data.pop("box_min", None)
            data.pop("box_max", None)
        else:
            return {
                "status": "error",
                "error_code": "AMBIGUOUS_CAMERA",
                "message": "set_camera received BOTH a bounding box (box_min/box_max) and an explicit camera "
                           "(location/target). These are different framing modes.",
                "retry_hint": "Pass fit='bbox' or fit='camera' to choose, or send only one mode's parameters.",
            }
    if has_box and not ("box_min" in data and "box_max" in data):
        return {
            "status": "error",
            "error_code": "INCOMPLETE_BBOX",
            "message": "Bbox framing needs BOTH box_min and box_max.",
            "retry_hint": "Supply the missing corner, or switch to location/target.",
        }
    return await _exec_simple("set_camera", data)


@mcp.tool(name="get_rhino_commands", annotations=RO)
async def get_rhino_commands(params: GetRhinoCommandsInput) -> dict:
    """List all registered Rhino command names (live, not hardcoded).

    Use this to discover whether a command like Contour, FilletEdge, or ProjectCurves exists
    before calling it via execute_script or batch. filter narrows by substring (case-insensitive)."""
    return await _exec_simple("get_rhino_commands", params.model_dump())


# Geometry ops --------------------------------------------------

@mcp.tool(name="get_cross_section", annotations=WR)
async def get_cross_section(params: CrossSectionInput) -> dict:
    """Cut a solid at a Z height and return section curves - useful for plan views."""
    return await _exec_simple("get_cross_section", params.model_dump(exclude_none=True))


@mcp.tool(name="get_section_profile", annotations=RO)
async def get_section_profile(params: SectionProfileInput) -> dict:
    """Return a read-only section profile as polylines + SVG, without adding Rhino objects."""
    return await _exec_simple("get_section_profile", params.model_dump(exclude_none=True))


@mcp.tool(name="get_silhouette", annotations=RO)
async def get_silhouette(params: SilhouetteInput) -> dict:
    """Return cheap directional silhouette feedback as SVG/polyline rectangles."""
    return await _exec_simple("get_silhouette", params.model_dump(exclude_none=True))


@mcp.tool(name="loft_surface", annotations=WR)
async def loft_surface(params: LoftInput) -> dict:
    """Create one or more lofted Breps/surfaces from ordered curve ids."""
    return await _exec_simple("loft_surface", params.model_dump(exclude_none=True))


@mcp.tool(name="sweep1", annotations=WR)
async def sweep1(params: Sweep1Input) -> dict:
    """Create a one-rail sweep from a rail curve and one or more profile curves."""
    return await _exec_simple("sweep1", params.model_dump(exclude_none=True))


@mcp.tool(name="sweep2", annotations=WR)
async def sweep2(params: Sweep2Input) -> dict:
    """Create a two-rail sweep from two rail curves and one or more profile curves."""
    return await _exec_simple("sweep2", params.model_dump(exclude_none=True))


@mcp.tool(name="pipe_curve", annotations=WR)
async def pipe_curve(params: PipeInput) -> dict:
    """Create a pipe Brep around a curve."""
    return await _exec_simple("pipe_curve", params.model_dump(exclude_none=True))


@mcp.tool(name="extrude_curve", annotations=WR)
async def extrude_curve(params: ExtrudeCurveInput) -> dict:
    """Extrude a curve along a vector, optionally capping closed profiles."""
    return await _exec_simple("extrude_curve", params.model_dump(exclude_none=True))


@mcp.tool(name="network_surface", annotations=WR)
async def network_surface(params: NetworkSurfaceInput) -> dict:
    """Create an edge/network surface from boundary or section curves."""
    return await _exec_simple("network_surface", params.model_dump(exclude_none=True))


@mcp.tool(name="sphere_patch", annotations=WR)
async def sphere_patch(params: SpherePatchInput) -> dict:
    """Create a rectangular patch sampled from a sphere, useful for shell studies."""
    return await _exec_simple("sphere_patch", params.model_dump(exclude_none=True))


@mcp.tool(name="trim_with_planes", annotations=WR)
async def trim_with_planes(params: TrimWithPlanesInput) -> dict:
    """Trim a Brep by one or more half-space planes. Auto-checkpoints by default."""
    return await _exec_simple("trim_with_planes", params.model_dump(exclude_none=True))


@mcp.tool(name="boolean_operation", annotations=WR)
async def boolean_operation(params: BooleanInput) -> dict:
    """Boolean union / difference / intersection between two objects."""
    return await _exec_simple("boolean_operation", params.model_dump())


@mcp.tool(name="delete_objects", annotations=WR)
async def delete_objects(params: DeleteInput) -> dict:
    """Delete objects by GUID or selector string: 'all', 'by_layer:Layer', 'by_name:Pattern', 'selected'."""
    return await _exec_simple("delete_objects", params.model_dump())

# Escape hatches --------------------------------------------------

@mcp.tool(name="execute_script", annotations=WR)
async def execute_script(params: ScriptInput) -> dict:
    """Run arbitrary Python inside Rhino. Powerful escape hatch — prefer structured tools.

    START WITH `rab.help()` — it prints the whole helper API with signatures AND the
    document's unit system, so you never guess a name or a scale.

    KNOWN RHINOCOMMON TRAPS (these fail SILENTLY and produce plausible geometry —
    use the rab wrapper instead of the raw call):
      - Curve.CreateInterpolatedCurve(pts, 3, ChordPeriodic) returns an OPEN,
        non-periodic curve. Lofting it splits the skin at the seam.  -> rab.periodic_curve(pts)
      - Lofted+capped Breps often come back with INWARD normals, and a boolean
        difference against an inverted solid ADDS material (recesses become bulges).
        AddBrep() re-orients on insert, so auditing the document afterwards shows
        nothing wrong.  -> rab.orient(brep); rab.boolean_diff already checks this.
      - Brep.CreatePlanarBreps needs an explicit System.Array[Curve]; a single Curve
        silently returns zero results.  -> rab.cap(curve)

    Auto-imported preamble: rhinoscriptsyntax as rs, scriptcontext as sc, Rhino, System,
    and `rab` — a concise helper library. PREFER rab for common elements:
      rab.wall((0,0,0), (12000,0,0), height=3000, thickness=200)
      rab.slab([(0,0),(30000,0),(30000,18000),(0,18000)], thickness=250, z=3600)
      for pt in rab.grid((0,0), 4, 3, 8400, 8400): rab.column(pt, h=3600)
      rab.extrude(points, height, z=0, layer_path=...)   # closed profile -> solid
      rab.ids_on("Wall"), rab.bbox(ids), rab.move(ids, [dx,dy,dz]), rab.copy_to(ids, v)
      rab.boolean_diff(a_id, b_id)   # validity-checked, raises with a useful hint
      rab.layer("Building::Walls", color=[180,60,60]), rab.info()
    Use undo_name to wrap in an undo record.

    IMPORTANT — Rhino uses IronPython 2. Avoid these Python 3-isms:
      - open(path, encoding='utf-8')  →  use io.open(path, encoding='utf-8')
      - re.fullmatch(pat, s)          →  use re.match(pat + '$', s)
      - from __future__ import annotations  →  not supported, remove it
      - f-strings                      →  use .format() or % formatting
      - type hints (x: int = 0)        →  remove annotations
    """
    data = params.model_dump(exclude_none=True, by_alias=False)
    # Normalize alias: if 'script' came through, map to 'code' for C#
    if "script" in data and "code" not in data:
        data["code"] = data.pop("script")
    if _RAB_ENABLED and isinstance(data.get("code"), str):
        data["code"] = _RAB_BOOTSTRAP + data["code"]
    result = await _exec_simple("execute_script", data)
    # Compact mode: if result has many object_ids, summarize to save tokens
    if isinstance(result, dict):
        ids = result.get("object_ids", [])
        if isinstance(ids, list) and len(ids) > 20:
            result["object_ids_count"] = len(ids)
            result["object_ids_sample"] = ids[:5]
            result["object_ids"] = f"[{len(ids)} objects — use query_scene to inspect]"
    return result


@mcp.tool(name="run_python", annotations=WR)
async def run_python(params: ScriptInput) -> dict:
    """McNeel-compatible alias for execute_script. Prefer structured tools when possible."""
    return await execute_script(params)


@mcp.tool(name="execute_python3", annotations=WR)
async def execute_python3(params: Python3Input) -> dict:
    """Run CPython 3 in Rhino 8 via RhinoCode's official `rhinocode` CLI.

    This supplements `execute_script`, which uses Rhino's legacy IronPython engine.
    Requirements:
    - Rhino 8.11+ with RhinoCode installed
    - RhinoCode script server running (`StartScriptServer`; this tool starts it)
    - AIBridge in Developer mode
    """
    blocked, health = await _developer_mode_required()
    if blocked:
        return blocked

    version = _parse_rhino_version(health.get("rhino_version"))
    if version is None or version < (8, 11):
        return {
            "status": "error",
            "error_code": "RHINOCODE_UNSUPPORTED_RHINO_VERSION",
            "message": "execute_python3 requires Rhino 8.11+ with RhinoCode script server support.",
            "rhino_version": health.get("rhino_version", "unknown"),
            "retry_hint": "Use execute_script on Rhino 8.9, or update Rhino to 8.11+ for CPython 3.",
        }

    rhinocode = _find_rhinocode()
    if not rhinocode:
        return {
            "status": "error",
            "error_code": "RHINOCODE_NOT_FOUND",
            "message": "Could not find Rhino 8's rhinocode CLI.",
            "retry_hint": r"Install/update Rhino 8.11+, or add C:\Program Files\Rhino 8\System to PATH.",
        }

    start = await _exec_simple("start_script_server", {})
    if start.get("status") != "ok" or start.get("started") is not True:
        return {
            "status": "error",
            "error_code": "SCRIPT_SERVER_FAILED",
            "message": "Could not start RhinoCode script server. Rhino 8.11+ is required.",
            "start_result": start,
        }

    with tempfile.NamedTemporaryFile("w", suffix=".py", prefix="rab_py3_", delete=False, encoding="utf-8") as f:
        script_path = f.name
        f.write(params.code)
        if not params.code.endswith("\n"):
            f.write("\n")

    args = [rhinocode]
    if params.rhino_id:
        args.extend(["--rhino", params.rhino_id])
    args.extend(["script", script_path])
    proc = await _run_process(args, params.timeout_seconds)

    if not params.keep_script:
        try:
            os.unlink(script_path)
        except OSError:
            pass

    ok = proc["returncode"] == 0
    return {
        "status": "ok" if ok else "error",
        "engine": "RhinoCode CPython 3",
        "rhinocode": rhinocode,
        "returncode": proc["returncode"],
        "stdout": proc["stdout"] or "(no stdout)",
        "stderr": proc["stderr"] or "",
        "script_path": script_path if params.keep_script else None,
        "message": None if ok else "rhinocode script returned a non-zero exit code.",
    }


@mcp.tool(name="undo", annotations=WI)
async def undo(params: UndoInput) -> dict:
    """Undo one or more Rhino operations."""
    return await _exec_simple("undo", params.model_dump())


@mcp.tool(name="get_log", annotations=RO)
async def get_log(params: LogInput) -> dict:
    """Fetch recent bridge log entries for debugging.

    limit: max entries to return (default 50, alias: count).
    errors_only: True filters to errors/warnings only."""
    return await _exec_simple("get_log", params.model_dump(by_alias=True))


# Materials --------------------------------------------------

@mcp.tool(name="set_layer_material", annotations=WI)
async def set_layer_material(params: LayerMaterialInput) -> dict:
    """Set PBR material properties on a layer - color, roughness, metallic, opacity, emission.

    Updates both the layer display color and the render material (Rendered/Arctic/Raytraced).

    Examples:
        set_layer_material(layer="Wall", color=[220, 210, 195], roughness=0.8)
        set_layer_material(layer="Glass", color=[180, 220, 255], opacity=0.2, roughness=0.05)
        set_layer_material(layer="Core::Walls", color=[80, 80, 80], metallic=0.0, roughness=0.9)"""
    return await _exec_simple("set_layer_material", params.model_dump(exclude_none=True))


# Native commands --------------------------------------------------

@mcp.tool(name="run_command", annotations=WR)
async def run_command(params: RunCommandInput) -> dict:
    """Execute any Rhino command string via RhinoApp.RunScript.

    Escape hatch for commands not covered by structured tools. Tracks newly created objects
    and captures command-line output (including print() from RunPythonScript).
    For full Python script execution with better output capture, use execute_script instead.
    Prefer structured tools when available - run_command has no rollback guarantee.

    Examples:
        run_command(command="_Contour _SelAll _Enter 0,0,0 0,0,1 3000")
        run_command(command="_FilletEdge _SelId <guid> _Enter 50")
        run_command(command="_Make2D _SelAll _Enter")"""
    return await _exec_simple("run_command", params.model_dump())


# Entry point --------------------------------------------------



# =============================================================================
# SECTIONS & PLANS
# =============================================================================

@mcp.tool(name="create_section", annotations=WR)
async def create_section(label: str = "", start_x: Optional[float] = None, start_y: Optional[float] = None, start_z: Optional[float] = None, end_x: Optional[float] = None, end_y: Optional[float] = None, end_z: Optional[float] = None, view_side: str = "left") -> dict:
    """Create an architectural section line with arrowheads on a dedicated layer. The model will place a default section line at the model center — reposition it and call cut_section when satisfied."""
    params = {"view_side": view_side}
    if label: params["label"] = label
    if start_x is not None: params["start_point"] = {"x": start_x, "y": start_y or 0, "z": start_z or 0}
    if end_x is not None: params["end_point"] = {"x": end_x, "y": end_y or 0, "z": end_z or 0}
    return await _exec_simple("create_section", params)


@mcp.tool(name="create_elevation", annotations=WR)
async def create_elevation(label: str = "", direction: str = "north", offset: Optional[float] = None) -> dict:
    """Create an elevation marker for the specified direction (north/south/east/west)."""
    params = {"direction": direction}
    if label: params["label"] = label
    if offset is not None: params["offset"] = offset
    return await _exec_simple("create_elevation", params)


@mcp.tool(name="cut_section", annotations=WR)
async def cut_section(label: str, capture: bool = True, restore_view: bool = True) -> dict:
    """Cut the named section, optionally capture it, and restore the user's viewport by default."""
    return await _exec_simple("cut_section", {"label": label, "capture": capture, "restore_view": restore_view})


@mcp.tool(name="align_view_to_section", annotations=WI)
async def align_view_to_section(label: str) -> dict:
    """Align the viewport camera perpendicular to the named section/elevation cut plane."""
    return await _exec_simple("align_view_to_section", {"label": label})


@mcp.tool(name="create_plan", annotations=WR)
async def create_plan(floor: str, cut_height_mm: float = 1200.0, capture: bool = True, restore_view: bool = True) -> dict:
    """Generate a floor plan, optionally capture it, and restore the user's viewport by default."""
    return await _exec_simple("create_plan", {"floor": floor, "cut_height_mm": cut_height_mm, "capture": capture, "restore_view": restore_view})


@mcp.tool(name="create_all_plans", annotations=WR)
async def create_all_plans(cut_height_mm: float = 1200.0, capture: bool = True) -> dict:
    """Generate floor plans for ALL detected floor levels simultaneously."""
    return await _exec_simple("create_all_plans", {"cut_height_mm": cut_height_mm, "capture": capture})


@mcp.tool(name="list_sections", annotations=RO)
async def list_sections() -> dict:
    """List all sections, elevations, and plans currently defined in the model."""
    return await _exec_simple("list_sections", {})


@mcp.tool(name="update_section", annotations=WR)
async def update_section(label: str, start_x: Optional[float] = None, start_y: Optional[float] = None, start_z: Optional[float] = None, end_x: Optional[float] = None, end_y: Optional[float] = None, end_z: Optional[float] = None) -> dict:
    """Reposition an existing section line and re-cut."""
    params = {"label": label}
    if start_x is not None: params["start_point"] = {"x": start_x, "y": start_y or 0, "z": start_z or 0}
    if end_x is not None: params["end_point"] = {"x": end_x, "y": end_y or 0, "z": end_z or 0}
    return await _exec_simple("update_section", params)


@mcp.tool(name="remove_section", annotations=DE)
async def remove_section(label: str) -> dict:
    """Remove a section, elevation, or plan layer and its clipping plane."""
    return await _exec_simple("remove_section", {"label": label})


# =============================================================================
# ILLUSTRATION & DISPLAY MODES
# =============================================================================

@mcp.tool(name="create_display_mode", annotations=WR)
async def create_display_mode(name: str, preset: str = "", base_mode: str = "", background_color: str = "", edge_color: str = "", edge_thickness: Optional[int] = None, silhouette_thickness: Optional[int] = None, show_edges: Optional[bool] = None, show_silhouettes: Optional[bool] = None, shading_enabled: Optional[bool] = None) -> dict:
    """Create a custom Rhino display mode for illustration. Presets: diagram, technical, blueprint, sketch, axonometric, atmospheric, monochrome, cutaway."""
    params = {"name": name}
    if preset: params["preset"] = preset
    if base_mode: params["base_mode"] = base_mode
    if background_color: params["background_color"] = background_color
    if edge_color: params["edge_color"] = edge_color
    if edge_thickness is not None: params["edge_thickness"] = edge_thickness
    if silhouette_thickness is not None: params["silhouette_thickness"] = silhouette_thickness
    if show_edges is not None: params["show_edges"] = show_edges
    if show_silhouettes is not None: params["show_silhouettes"] = show_silhouettes
    if shading_enabled is not None: params["shading_enabled"] = shading_enabled
    return await _exec_simple("create_display_mode", params)


@mcp.tool(name="apply_display_mode", annotations=WI)
async def apply_display_mode(name: str) -> dict:
    """Apply a display mode (built-in or custom AI- mode) to the active viewport."""
    return await _exec_simple("apply_display_mode", {"name": name})


@mcp.tool(name="list_display_modes", annotations=RO)
async def list_display_modes() -> dict:
    """List all available display modes including custom AI-created ones."""
    return await _exec_simple("list_display_modes", {})


@mcp.tool(name="adjust_display_mode", annotations=WR)
async def adjust_display_mode(name: str, background_color: str = "", edge_color: str = "", edge_thickness: Optional[int] = None, silhouette_thickness: Optional[int] = None) -> dict:
    """Adjust parameters of an existing custom AI display mode."""
    params = {"name": name}
    if background_color: params["background_color"] = background_color
    if edge_color: params["edge_color"] = edge_color
    if edge_thickness is not None: params["edge_thickness"] = edge_thickness
    if silhouette_thickness is not None: params["silhouette_thickness"] = silhouette_thickness
    return await _exec_simple("adjust_display_mode", params)


@mcp.tool(name="delete_display_mode", annotations=DE)
async def delete_display_mode(name: str) -> dict:
    """Delete a custom AI display mode (only AI- prefixed modes can be deleted)."""
    return await _exec_simple("delete_display_mode", {"name": name})


@mcp.tool(name="capture_illustration", annotations=RO)
async def capture_illustration(display_mode: str = "", width: int = 1600, height: int = 1200, style_notes: str = "", restore_mode: bool = True) -> dict:
    """Capture the viewport as an illustration using the specified or current display mode."""
    params = {"width": width, "height": height, "restore_mode": restore_mode}
    if display_mode: params["display_mode"] = display_mode
    if style_notes: params["style_notes"] = style_notes
    return await _exec_simple("capture_illustration", params)


# =============================================================================
# MATERIAL INTELLIGENCE
# =============================================================================

@mcp.tool(name="search_materials", annotations=RO)
async def search_materials(keyword: str, limit: int = 5) -> dict:
    """Search AmbientCG for PBR materials matching keyword. Returns candidates with names, preview info, and real-world dimensions. Call download_material with a specific asset_id to proceed."""
    try:
        from rhino_architect.material_downloader import search_materials as _search
        results = _search(keyword, limit)
        return {"status": "ok", "results": results, "count": len(results)}
    except Exception as e:
        return {"status": "error", "message": str(e)}


@mcp.tool(name="download_material", annotations=WR)
async def download_material(asset_id: str, layer_name: str, resolution: str = "2K", confirmed: bool = False) -> dict:
    """
    Download and apply a PBR material from AmbientCG to a Rhino layer.
    IMPORTANT: First call with confirmed=False to get a preview of what will be downloaded.
    Only call with confirmed=True after the user has explicitly approved.
    asset_id: from search_materials results.
    layer_name: Rhino layer to assign the material to.
    resolution: '1K', '2K', or '4K'.
    """
    try:
        from rhino_architect.material_downloader import get_material_info, download_material as _download, compute_uv_repeat
        info = get_material_info(asset_id)
        if not info:
            return {"status": "error", "message": f"Asset {asset_id} not found"}

        # Preview mode — return info without downloading
        if not confirmed:
            dims = info.get("dimensionsInMeters", [1.0, 1.0])
            size_m = dims[0] if dims else 1.0
            return {
                "status": "preview",
                "asset_id": asset_id,
                "display_name": info.get("displayName", asset_id),
                "physical_size_m": size_m,
                "resolution": resolution,
                "license": "CC0 (free, no attribution required)",
                "message": f"Ready to download '{info.get('displayName', asset_id)}' ({resolution}, CC0). Call again with confirmed=True to proceed.",
                "confirmed_required": True
            }

        # Download
        result = _download(asset_id, resolution)

        # Get model unit system from Rhino
        ping_result = await _exec_simple("ping", {})
        unit_system = ping_result.get("unit_system", "Meters")

        physical_size_m = result.get("physical_size_m", 1.0)
        uv_repeat = compute_uv_repeat(physical_size_m, unit_system)

        # Apply via C# handler
        apply_params = {
            "layer_name": layer_name,
            "material_name": result["display_name"],
            "maps": result["local_paths"],
            "physical_size_m": physical_size_m,
            "uv_repeat": uv_repeat
        }
        return await _exec_simple("apply_downloaded_material", apply_params)
    except Exception as e:
        return {"status": "error", "message": str(e)}


@mcp.tool(name="edit_material", annotations=WR)
async def edit_material(layer_name: str = "", material_name: str = "", roughness: Optional[float] = None, metallic: Optional[float] = None, diffuse_color: str = "", transparency: Optional[float] = None, texture_scale: Optional[float] = None, texture_rotation: Optional[float] = None) -> dict:
    """Edit properties of an existing Rhino render material on a layer."""
    params = {}
    if layer_name: params["layer_name"] = layer_name
    if material_name: params["material_name"] = material_name
    if roughness is not None: params["roughness"] = roughness
    if metallic is not None: params["metallic"] = metallic
    if diffuse_color: params["diffuse_color"] = diffuse_color
    if transparency is not None: params["transparency"] = transparency
    if texture_scale is not None: params["texture_scale"] = texture_scale
    if texture_rotation is not None: params["texture_rotation"] = texture_rotation
    return await _exec_simple("edit_material", params)


@mcp.tool(name="list_materials", annotations=RO)
async def list_materials() -> dict:
    """List all render materials in the current Rhino document."""
    return await _exec_simple("list_materials", {})


@mcp.tool(name="get_material", annotations=RO)
async def get_material(layer_name: str = "", material_index: Optional[int] = None) -> dict:
    """Get full properties of a render material by layer name or material index."""
    params = {}
    if layer_name: params["layer_name"] = layer_name
    if material_index is not None: params["material_index"] = material_index
    return await _exec_simple("get_material", params)


# =============================================================================
# FILE TRACING
# =============================================================================

@mcp.tool(name="import_dwg", annotations=WR)
async def import_dwg(file_path: str) -> dict:
    """Import a DWG or DXF file into Rhino using the native importer (100% accurate, no AI interpretation). Post-processes imported geometry."""
    return await _exec_simple("import_dwg", {"file_path": file_path})


@mcp.tool(name="calibrate_scale", annotations=WR)
async def calibrate_scale(point1_x: float, point1_y: float, point1_z: float, point2_x: float, point2_y: float, point2_z: float, known_distance: float, unit: str = "mm") -> dict:
    """Calibrate model scale by specifying two points and their known real-world distance. Rescales all geometry to match. Use after importing or tracing files that may be at wrong scale."""
    return await _exec_simple("calibrate_scale", {
        "point1": {"x": point1_x, "y": point1_y, "z": point1_z},
        "point2": {"x": point2_x, "y": point2_y, "z": point2_z},
        "known_distance": known_distance,
        "unit": unit
    })


# =============================================================================
# PDF / FILE TRACING TOOLS  (v4.7)
# =============================================================================

@mcp.tool(name="get_pdf_info", annotations=RO)
async def get_pdf_info(pdf_path: str) -> dict:
    """Inspect a PDF file: page count, page sizes in mm, vector/text content flag.

    Call this before trace_pdf to choose the right page number and confirm
    the file is a vector drawing (not a scanned raster).

    Args:
        pdf_path: Absolute path to the PDF file.
    """
    try:
        from rhino_architect.pdf_tracer import get_pdf_info as _info
        return await asyncio.to_thread(_info, pdf_path)
    except ImportError as e:
        return {"error": str(e)}


@mcp.tool(name="preview_pdf_page", annotations=RO)
async def preview_pdf_page(pdf_path: str, page_number: int = 0) -> Any:
    """Render a PDF page as a base64 PNG thumbnail for previewing before tracing.

    Args:
        pdf_path: Absolute path to the PDF file.
        page_number: 0-indexed page number (default 0).
    """
    try:
        from rhino_architect.pdf_tracer import render_page_preview
        b64 = await asyncio.to_thread(render_page_preview, pdf_path, page_number)
        if b64:
            return Image(data=base64.b64decode(b64), format="png")
        return {"error": "Could not render page"}
    except ImportError as e:
        return {"error": str(e)}


@mcp.tool(name="preview_pdf_page_json", annotations=RO)
async def preview_pdf_page_json(pdf_path: str, page_number: int = 0) -> dict:
    """Render a PDF page and return base64 JSON metadata instead of MCP image content."""
    try:
        from rhino_architect.pdf_tracer import render_page_preview
        b64 = await asyncio.to_thread(render_page_preview, pdf_path, page_number)
        if b64:
            return {"status": "ok", "page": page_number, "image_base64": b64, "format": "png",
                    "note": "Render the image to confirm the page looks correct before tracing."}
        return {"status": "error", "error_code": "CAPTURE_FAILED", "message": "Could not render page"}
    except ImportError as e:
        return {"status": "error", "error_code": "MISSING_DEPENDENCY", "message": str(e)}


@mcp.tool(name="trace_pdf", annotations=WR)
async def trace_pdf(
    pdf_path: str,
    page_number: int = 0,
    dpi: int = 300,
    model_unit: str = "mm",
    confidence_threshold: float = 0.65,
    layer_prefix: str = "Traced",
    z_elevation: float = 0.0,
    merge_tolerance_px: float = 5.0,
    min_line_length_px: float = 10.0,
) -> dict:
    """Trace a PDF drawing page and import the geometry into Rhino as curves, arcs, polylines and text.

    Two-step process handled automatically:
      1. Python CV pipeline (PyMuPDF + OpenCV) extracts geometry from the PDF.
      2. C# TracingManager creates Rhino objects on organised layers.

    Low-confidence detections go to a '{layer_prefix}::REVIEW' layer (shown in red)
    so you can inspect and accept/reject them manually.

    Requirements: pip install pymupdf opencv-python numpy

    Args:
        pdf_path: Absolute path to the PDF file.
        page_number: 0-indexed page number (default 0).
        dpi: Render resolution. 300 is good for most drawings; use 600 for fine detail.
        model_unit: Target model unit ("mm", "cm", "m", "ft", "in"). Must match the Rhino document.
        confidence_threshold: Elements below this confidence go to the REVIEW layer (0.0–1.0).
        layer_prefix: Prefix for created layers (default "Traced").
        z_elevation: Z height at which all geometry is placed.
        merge_tolerance_px: Distance in pixels within which collinear segments are merged.
        min_line_length_px: Ignore detected lines shorter than this (pixels).
    """
    try:
        from rhino_architect.pdf_tracer import trace_pdf as _trace
    except ImportError as e:
        import sys as _sys
        return {
            "error": f"pdf_tracer import failed: {e}",
            "fix": "Install into the MCP server\'s Python environment:",
            "command": f"{_sys.executable} -m pip install pymupdf opencv-python numpy",
            "python_path": _sys.executable,
            "note": "This is NOT your system Python or Codex Python — it\'s the MCP bridge\'s own venv."
        }

    # Step 1: Extract geometry in Python. The CV pipeline (PyMuPDF + OpenCV) is
    # CPU-bound and can run for many seconds - run it off the event loop so the
    # MCP server (ping, concurrent tool calls) stays responsive.
    trace_result = await asyncio.to_thread(
        _trace,
        pdf_path=pdf_path,
        page_number=page_number,
        dpi=dpi,
        model_unit=model_unit,
        confidence_threshold=confidence_threshold,
        merge_tolerance_px=merge_tolerance_px,
        min_line_length_px=min_line_length_px,
    )

    if "error" in trace_result and not trace_result.get("elements"):
        return trace_result

    meta = trace_result.get("metadata", {})
    elements = trace_result.get("elements", [])

    if not elements:
        return {"status": "ok", "message": "No geometry detected in this page.",
                           "metadata": meta}

    # Step 2: Send to Rhino C# to create objects
    payload = {
        "elements": elements,
        "layer_prefix": layer_prefix,
        "confidence_threshold": confidence_threshold,
        "z_elevation": z_elevation,
        "source_file": meta.get("source_file", os.path.basename(pdf_path)),
        "page_number": page_number,
    }
    rhino_result = await _exec_simple("apply_traced_elements", payload)

    return {
        "status": "ok",
        "trace_metadata": meta,
        "rhino_result": rhino_result,
        "note": f"Elements on REVIEW layer need manual inspection. Open layer panel to check '{layer_prefix}::REVIEW'.",
    }


@mcp.tool(name="clear_trace_layers", annotations=DE)
async def clear_trace_layers(layer_prefix: str = "Traced") -> dict:
    """Delete all objects and layers created by a previous trace_pdf call.

    Args:
        layer_prefix: The prefix used when the layers were created (default "Traced").
    """
    return await _exec_simple("clear_trace_layers", {"layer_prefix": layer_prefix})


@mcp.tool(name="get_trace_layers", annotations=RO)
async def get_trace_layers(layer_prefix: str = "Traced") -> dict:
    """List all trace layers and their object counts.

    Args:
        layer_prefix: Layer prefix to search for (default "Traced").
    """
    return await _exec_simple("get_trace_layers", {"layer_prefix": layer_prefix})


# =============================================================================
# DESIGN MEMORY TOOLS
# =============================================================================

@mcp.tool(name="set_design_brief", annotations=WR)
async def set_design_brief(brief: str) -> dict:
    """Store the project design brief inside the Rhino file (.3dm UserData).

    Call this at the start of any significant design session. The brief persists
    in the .3dm file and survives save/reload. Include: building type, program,
    key constraints, structural approach, client requirements.
    """
    return await _exec_simple("set_design_brief", {"brief": brief})


@mcp.tool(name="get_design_brief", annotations=RO)
async def get_design_brief() -> dict:
    """Retrieve the project design brief and global design rules stored in the Rhino file."""
    return await _exec_simple("get_design_brief", {})


@mcp.tool(name="tag_object", annotations=WR)
async def tag_object(ids: list[str], tags: dict) -> dict:
    """Write metadata tags to one or more Rhino objects (stored in UserDictionary, persists in .3dm).

    Useful tag keys:
      ai_group    -- logical group name (e.g. 'tower_core_level_3')
      ai_rule     -- regeneration rule (e.g. 'concrete 300mm, 8.4m bay')
      ai_label    -- human-readable label
      ai_relations -- JSON string: {"children": ["id1", "id2"], "parent": "id0"}
    """
    return await _exec_simple("tag_object", {"ids": ids, "tags": tags})


@mcp.tool(name="get_provenance", annotations=RO)
async def get_provenance(id: str) -> dict:
    """Get the full creation context (provenance) for a Rhino object.

    Returns: which tool created it, with what parameters, in which session.
    Answers: 'why does this object exist?' and 'how was it created?'
    All AI-created objects are auto-tagged at creation time.
    """
    return await _exec_simple("get_provenance", {"id": id})


@mcp.tool(name="search_memory", annotations=RO)
async def search_memory(query: str) -> dict:
    """Search the design memory for objects, rules, groups, and sessions matching a keyword.

    Searches across: design brief, session logs, named groups, and all object tags.
    Returns matching results with source and context (max 50 hits).
    Example queries: 'tower core', 'concrete 300mm', 'facade A', 'level 3 columns'.
    """
    return await _exec_simple("search_memory", {"query": query})


@mcp.tool(name="get_related_objects", annotations=RO)
async def get_related_objects(id: str, relation: str = "") -> dict:
    """Get objects related to a given object via stored ai_relations tags.

    relation: 'parent', 'children', 'mirrors', 'group', or '' for all relations.
    Example: get all windows that belong to a specific facade wall.
    """
    return await _exec_simple("get_related_objects", {"id": id, "relation": relation})


@mcp.tool(name="name_group", annotations=WR)
async def name_group(name: str, ids: list[str]) -> dict:
    """Create or update a named group of objects stored in the Rhino file.

    Named groups persist in the .3dm file. Use to label sets of objects:
    'tower_core', 'north_facade', 'level_3_columns'. Retrieve with get_group.
    """
    return await _exec_simple("name_group", {"name": name, "ids": ids})


@mcp.tool(name="get_group", annotations=RO)
async def get_group(name: str) -> dict:
    """Get the object IDs belonging to a named group stored in the Rhino file."""
    return await _exec_simple("get_group", {"name": name})


@mcp.tool(name="get_all_groups", annotations=RO)
async def get_all_groups() -> dict:
    """List all named groups and their member object IDs stored in the Rhino file."""
    return await _exec_simple("get_all_groups", {})


@mcp.tool(name="add_design_rule", annotations=WR)
async def add_design_rule(rule: str) -> dict:
    """Add a global design rule to the project memory (persists in .3dm file).

    Rules guide future generation decisions. Examples:
      'bay spacing must be 8400mm'
      'concrete walls 300mm thick'
      'floor-to-floor height 3500mm'
      'no windows below 900mm sill height'
    """
    return await _exec_simple("add_design_rule", {"rule": rule})


@mcp.tool(name="log_session", annotations=WR)
async def log_session(summary: str) -> dict:
    """Log a summary of the current AI session to the project memory (persists in .3dm).

    Call at the end of a work session with a brief description of what was done.
    Logs persist in the .3dm file and provide context for future sessions.
    """
    return await _exec_simple("log_session", {"summary": summary})


# =============================================================================
# INCREMENTAL SCENE SYNC TOOLS
# =============================================================================

@mcp.tool(name="get_scene_diff", annotations=RO)
async def get_scene_diff(from_version: int) -> dict:
    """Get what changed in the Rhino scene since a specific version number.

    Returns arrays of added, deleted, and modified object refs.
    Use at the start of every session to catch up cheaply instead of
    re-querying the full scene. Get current version from ping or get_tracker_version.

    WHEN TO USE: much faster than get_scene_summary on large models --
    only returns what changed, not everything.
    """
    return await _exec_simple("get_scene_diff", {"from_version": from_version})


@mcp.tool(name="get_change_log", annotations=RO)
async def get_change_log(limit: int = 50, since_version: int = 0) -> dict:
    """Get the chronological log of recent scene change events.

    Returns change events (added/deleted/modified) with timestamps and version numbers.
    Useful for understanding the sequence of recent edits or auditing a session.
    Max limit: 200 events.
    """
    return await _exec_simple("get_change_log", {
        "limit": limit, "since_version": since_version
    })


@mcp.tool(name="get_tracker_version", annotations=RO)
async def get_tracker_version() -> dict:
    """Get the current change tracker version number.

    Workflow: store this version, do work or wait for user edits,
    then call get_scene_diff(from_version=stored_version) to see what changed.
    """
    return await _exec_simple("get_tracker_version", {})


# =============================================================================
# SEMANTIC SCENE INTELLIGENCE TOOLS
# =============================================================================

@mcp.tool(name="analyze_architecture", annotations=RO)
async def analyze_architecture() -> dict:
    """Run a full semantic analysis of the Rhino scene.

    Classifies all geometry into architectural types: walls, slabs, columns,
    cores, facade panels, openings, stairs, massing. Detects floor levels by
    clustering Z-positions of flat geometry. Detects structural grid from
    column centroid positions.

    Returns: level count, system breakdown (counts + IDs), detected grid spacing,
    unclassified geometry ratio.

    Result is CACHED against scene_version -- calling twice costs almost nothing
    if the scene has not changed. Force refresh by modifying the scene.
    """
    return await _exec_simple("analyze_architecture", {})


@mcp.tool(name="get_building_systems", annotations=RO)
async def get_building_systems(system: str = "all") -> dict:
    """Get objects grouped by architectural building system.

    system options:
      'structure'   -- columns, slabs, cores
      'envelope'    -- walls, facade panels
      'openings'    -- windows, doors
      'circulation' -- stairs, ramps
      'all'         -- everything (default)

    Each object includes: id, level index, layer, bounding box size [dx, dy, dz] in mm.
    Call analyze_architecture first for an overview, then drill into systems.
    """
    return await _exec_simple("get_building_systems", {"system": system})


@mcp.tool(name="get_level_summary", annotations=RO)
async def get_level_summary(level: int = -1) -> dict:
    """Get a summary of one or all detected floor levels in the model.

    level: floor index (0 = ground floor), or -1 for all levels (default).
    Returns per level: elevation (mm), object count, count by architectural type.
    Levels are auto-detected by clustering the Z-positions of flat geometry.
    """
    params = {"level": level} if level >= 0 else {}
    return await _exec_simple("get_level_summary", params)


@mcp.tool(name="detect_design_patterns", annotations=RO)
async def detect_design_patterns() -> dict:
    """Detect repeating design patterns in the Rhino model.

    Finds:
      - Structural grid: dominant X/Y spacing from column centroid positions
      - Repeated modules: bounding-box sizes that appear 3+ times
      - Level count and detected floor heights

    Use before adding new elements to understand the existing design logic
    (bay spacing, grid, typical element sizes) so you can match them.
    """
    return await _exec_simple("detect_design_patterns", {})


@mcp.tool(name="find_unassigned_geometry", annotations=RO)
async def find_unassigned_geometry(min_volume: float = 0.0) -> dict:
    """Find geometry that couldn't be classified into any architectural system.

    min_volume: minimum bounding box volume in mm^3 to filter tiny objects (default: 0 = all).
    Returns objects with layer and bounding box size [dx, dy, dz].

    Use to review orphaned geometry, decide what to do with it (tag it,
    assign to a layer, delete it, or reclassify it).
    """
    return await _exec_simple("find_unassigned_geometry", {"min_volume": min_volume})


# =============================================================================
# SMART BATCHING -- PREVIEW
# =============================================================================

@mcp.tool(name="batch_preview", annotations=RO)
async def batch_preview(commands: list[dict]) -> dict:
    """Validate a batch plan without executing any commands (dry run, zero mutations).

    Checks each step:
      - Is it a known command?
      - Are $N reference paths ($1, $1.object_ids, $1.object_ids[0]) forward-reference-free?
      - Are there destructive commands that need extra care?
      - Which steps involve viewport captures (consider capture_at_end)?

    Returns per-step status (valid/invalid/warning), estimated creates/deletes,
    and all warnings. Completely safe to call at any time -- does NOT modify Rhino.

    WHEN TO USE: before any complex or destructive batch, especially those with
    many  chains or boolean operations.
    """
    return await _exec_simple("batch_preview", {"commands": commands})


# =============================================================================
# v4.7.4: TIER 1 — ACCURACY & SPEED BOOSTERS
# =============================================================================

@mcp.tool(name="set_state", annotations=WR)
async def set_state(key: str, value: Any = None) -> dict:
    """Store a value in the session scratchpad for later retrieval.

    Use this to cache derived geometry data across calls — face centers, grid
    points, reference coordinates, computed values. Avoids re-deriving the same
    data in every execute_script call.

    key: unique name (e.g. "iwan_face_centers", "plinth_corners")
    value: any JSON-serializable data (number, string, array, object)

    Example flow:
        set_state(key="dome_center", value=[0, 0, 15000])
        ... later ...
        get_state(key="dome_center") → {"value": [0, 0, 15000]}
    """
    return await _exec_simple("set_state", {"key": key, "value": value})


@mcp.tool(name="get_state", annotations=RO)
async def get_state(key: str = "") -> dict:
    """Retrieve a value from the session scratchpad.

    key: the key to retrieve. If empty/omitted, returns a listing of all stored keys
         with type info and value previews.
    """
    params = {}
    if key:
        params["key"] = key
    return await _exec_simple("get_state", params)


@mcp.tool(name="clear_state", annotations=WR)
async def clear_state(key: str = "") -> dict:
    """Remove one or all keys from the session scratchpad.

    key: specific key to remove. If empty, clears ALL stored state.
    """
    params = {}
    if key:
        params["key"] = key
    return await _exec_simple("clear_state", params)


class SetPbrMaterialInput(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)
    layer: str = Field(description="Layer to assign the material to (created if missing)")
    base_color: Any = Field(default=None, description="RGB array [r,g,b] (0-255) or hex string '#rrggbb'. Default: light gray")
    roughness: float = Field(default=0.5, description="Surface roughness 0.0 (mirror) to 1.0 (matte)")
    metallic: float = Field(default=0.0, description="Metalness 0.0 (dielectric) to 1.0 (metal)")
    opacity: float = Field(default=1.0, description="Opacity 0.0 (transparent) to 1.0 (opaque)")
    name: str = Field(default="", description="Material name. Default: PBR_{layer}")
    texture_maps: dict | None = Field(default=None, description="Optional texture map file paths. Keys: albedo, roughness, normal, metallic, ao, displacement. Values: absolute file paths to image files.")
    uv_repeat: float = Field(default=1.0, description="UV repeat factor for texture tiling. Use compute_uv_repeat() result for physically accurate sizing.")


@mcp.tool(name="set_pbr_material", annotations=WR)
async def set_pbr_material(params: SetPbrMaterialInput) -> dict:
    """Create a PBR material and assign it to a layer in one call.

    Supports both solid colors AND texture maps (albedo, normal, roughness, metallic, ao, displacement).
    For texture-based materials, pass texture_maps={"albedo": "/path/to/color.jpg", "normal": "/path/to/normal.jpg", ...}
    along with uv_repeat for physically accurate tiling.

    Replaces the ~12-line boilerplate of CreateBasicMaterial → SimulatedMaterial
    → ToPhysicallyBased → set properties → Add → assign. One call does it all.

    Common presets:
        White marble:   base_color=[240,235,230], roughness=0.3, metallic=0.0
        Sandstone:      base_color=[194,178,128], roughness=0.8, metallic=0.0
        Polished metal: base_color=[180,180,190], roughness=0.1, metallic=0.9
        Glass:          base_color=[200,220,255], roughness=0.05, metallic=0.0, opacity=0.3
        Concrete:       base_color=[170,170,170], roughness=0.9, metallic=0.0
        Dark wood:      base_color=[101,67,33],   roughness=0.6, metallic=0.0
        Red brick:      base_color=[178,34,34],   roughness=0.85, metallic=0.0
        Gold leaf:      base_color=[255,215,0],   roughness=0.2, metallic=1.0
    """
    return await _exec_simple("set_pbr_material", params.model_dump(exclude_none=True))


class RevolveProfileInput(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)
    points: list[list[float]] = Field(description="Profile points as [[x,y,z], ...]. Min 2 points. The profile is revolved around the axis.")
    axis_start: list[float] = Field(description="Axis start point [x,y,z]")
    axis_end: list[float] = Field(description="Axis end point [x,y,z]")
    angle_degrees: float = Field(default=360.0, description="Sweep angle in degrees (360 = full revolution)")
    cap: bool = Field(default=True, description="Cap the ends to create a closed solid")
    layer: str = Field(default="", description="Target layer (created if missing)")
    curve_degree: int = Field(default=3, description="Profile curve degree: 1=polyline, 3=smooth cubic")


@mcp.tool(name="revolve_profile", annotations=WR)
async def revolve_profile(params: RevolveProfileInput) -> dict:
    """Revolve a 2D profile around an axis to create a solid of revolution.

    Covers domes, minarets, columns, finials, vases, balusters, chhatri caps,
    and any lathe-turned architectural element.

    The profile points define the cross-section. The axis defines what the
    profile rotates around. Points should be in the plane containing the axis.

    Example — onion dome:
        revolve_profile(
            points=[[0,0,0], [3000,0,2000], [2000,0,6000], [500,0,8000], [0,0,8500]],
            axis_start=[0,0,0], axis_end=[0,0,8500],
            layer="Dome")
    """
    return await _exec_simple("revolve_profile", params.model_dump(exclude_none=True))


class LayerEntry(BaseModel):
    path: str = Field(description="Layer path using :: separator (e.g. 'Building::Walls::Exterior')")
    color: Optional[list[int]] = Field(default=None, description="RGB color [r,g,b] for the leaf layer")
    visible: Optional[bool] = Field(default=None, description="Layer visibility")
    material: Optional[dict] = Field(default=None, description="PBR material dict: {base_color, roughness, metallic, opacity}")


@mcp.tool(name="create_layer_tree", annotations=WR)
async def create_layer_tree(layers: list[dict]) -> dict:
    """Create an entire layer hierarchy in one call.

    Each entry specifies a full path (Parent::Child::Grandchild), optional color,
    visibility, and PBR material. Intermediate layers are created automatically.
    Existing layers are reused (not duplicated).

    Example — typical building setup:
        create_layer_tree(layers=[
            {"path": "Site::Ground",      "color": [80,140,80]},
            {"path": "Building::Walls",   "color": [180,60,60],  "material": {"base_color": [240,235,230], "roughness": 0.3}},
            {"path": "Building::Slabs",   "color": [100,100,180]},
            {"path": "Building::Columns", "color": [60,150,60]},
            {"path": "Building::Roof",    "color": [140,80,140]},
            {"path": "Landscape::Trees",  "color": [40,120,40]},
        ])

    Replaces 30+ individual create_layer calls at project startup.
    """
    return await _exec_simple("create_layer_tree", {"layers": layers})


@mcp.tool(name="thumbnail", annotations=RO)
async def thumbnail(
    width: int = 480,
    height: int = 360,
    quality: int = 75,
    wireframe: bool = False,
    as_json: bool = False,
) -> Any:
    """Capture a viewport thumbnail for design QA. Returns base64 JPEG.

    Default mode is Shaded (wireframe=False) at 480x360 for readable design checks.
    Set wireframe=True for fastest capture (<1s) at the cost of visual detail.
    Set as_json=True to get the JSON/base64 payload instead of MCP image content.

    Returns image_base64, camera info, and visible object count.
    Call this after major modeling steps to verify geometry, materials, and layout.
    """
    result = await _exec_simple("thumbnail", {
        "width": width, "height": height,
        "quality": quality, "wireframe": wireframe,
    })
    return result if as_json else _as_mcp_image(result, key="image_base64", default_format="jpeg")


@mcp.tool(name="thumbnail_json", annotations=RO)
async def thumbnail_json(
    width: int = 480,
    height: int = 360,
    quality: int = 75,
    wireframe: bool = False,
) -> dict:
    """Capture a viewport thumbnail and return JSON/base64 metadata."""
    return await _exec_simple("thumbnail", {
        "width": width, "height": height,
        "quality": quality, "wireframe": wireframe,
    })


# =============================================================================
# v4.7.4: TIER 2 — WORKFLOW FEATURES
# =============================================================================

@mcp.tool(name="export_objects", annotations=WI)
async def export_objects(
    format: str = "stl",
    path: str = "",
    object_ids: Optional[list[str]] = None,
) -> dict:
    """Export geometry to a file. Supports STL, OBJ, STEP, IGES, 3DM.

    format: output format (stl, obj, step, iges, 3dm)
    path: output file path. If empty, saves to temp directory.
    object_ids: specific objects to export. If empty, exports all visible geometry.
    """
    params = {"format": format}
    if path:
        params["path"] = path
    if object_ids:
        params["object_ids"] = object_ids
    return await _exec_simple("export_objects", params)


@mcp.tool(name="save_checkpoint", annotations=WR)
async def save_checkpoint(name: str) -> dict:
    """Save the current model state as a named checkpoint.

    Use before risky operations (complex booleans, major redesigns) so you
    can restore_checkpoint if things go wrong. Much faster than manual undo
    through dozens of steps.

    name: descriptive name like "before_roof", "clean_massing", "final_columns"
    """
    return await _exec_simple("save_checkpoint", {"name": name})


@mcp.tool(name="restore_checkpoint", annotations=WR)
async def restore_checkpoint(name: str) -> dict:
    """Restore the model to a previously saved checkpoint.

    WARNING: This replaces all current geometry with the checkpoint state.
    Save a new checkpoint first if you want to preserve current work.

    name: checkpoint name (from save_checkpoint)
    """
    return await _exec_simple("restore_checkpoint", {"name": name})


@mcp.tool(name="list_checkpoints", annotations=RO)
async def list_checkpoints() -> dict:
    """List all saved design checkpoints with size and timestamp."""
    return await _exec_simple("list_checkpoints", {})


# =============================================================================
# ENTRY POINT


# =============================================================================
# v4.8: PROTOCOL 5 TOOLS - cancellation, checkpoint hygiene, crash recovery
# =============================================================================

@mcp.tool(name="cancel_operation", annotations=WI)
async def cancel_operation() -> dict:
    """Cancel the most recent long-running mutating operation in Rhino.

    Works while the operation is still executing (protocol 5 multiplexing lets
    this tool run concurrently). The plugin stops at its next checkpoint -
    batches stop at the next op boundary (atomic batches roll back), and
    facade/floor generators return partial results flagged cancelled: true.
    """
    try:
        conn = await get_connection()
    except RhinoConnectionError as e:
        return {"status": "error", "error_code": "RHINO_NOT_CONNECTED", "message": str(e)}
    rid = conn.last_mutating_request_id
    if not rid:
        return {"status": "error", "message": "No mutating operation has been issued on this connection yet."}
    result = await conn.cancel(rid)
    result.setdefault("status", "ok")
    result["request_id"] = rid
    return result


@mcp.tool(name="delete_checkpoint", annotations=DE)
async def delete_checkpoint(name: str) -> dict:
    """Delete a saved design checkpoint (registry entry + .3dm file).

    Checkpoints persist across Rhino sessions (registry.json sidecar); auto-
    checkpoints are capped at the 10 newest, but named ones live until deleted.
    """
    return await _exec_simple("delete_checkpoint", {"name": name})


@mcp.tool(name="get_recovery_log", annotations=RO)
async def get_recovery_log(limit: int = 50) -> dict:
    """Read the write-ahead log of mutating commands (crash recovery).

    Every mutating command is journaled BEFORE execution ('begin') and after
    ('end' + status). After a Rhino crash or unexpected restart, read the tail
    to see exactly what was in flight, then diff against query_scene to decide
    what to re-issue. Falls back to the most recent session log if Rhino was
    restarted since the last write.
    """
    return await _exec_simple("get_recovery_log", {"limit": limit})


# =============================================================================
# TOOL PROFILES (RHINO_TOOLS=lean|standard|full)
# =============================================================================
# The plugin's full command vocabulary stays callable through `batch` regardless
# of profile - profiles only control which tools are ADVERTISED to the MCP
# client, which is what costs context and dilutes tool selection.
#
#   lean     ~21 tools - small/local models (Ollama), minimal context
#   standard ~65 tools - daily-driver surface for Claude/GPT class models (default)
#   full     everything, including *_json twins and McNeel-compat aliases

_LEAN_TOOLS: frozenset[str] = frozenset({
    "ping", "query_scene", "create_object", "transform_objects", "modify_object",
    "delete_objects", "batch", "execute_script", "capture_viewport", "set_view",
    "set_display_mode", "set_camera", "undo", "create_layer",
    "batch_layer_visibility", "select_objects", "measure_object", "report_areas",
    "save_checkpoint", "restore_checkpoint", "get_log",
})

_STANDARD_TOOLS: frozenset[str] = _LEAN_TOOLS | frozenset({
    # Architect intelligence
    "derive_floors_from_mass", "create_core", "place_openings_on_facade",
    "setup_arch_layers", "create_layer_tree",
    # Semantic analysis & QA
    "analyze_architecture", "get_level_summary", "select_by_semantic",
    "detect_clashes", "validate_objects",
    # Intent validation + reusable code substrate (v4.10.1)
    "assert_geometry", "find_unsupported", "section_preview",
    "write_module", "list_modules", "read_module",
    # Vision loop
    "capture_review_set", "capture_inspection_view", "compare_before_after",
    "thumbnail",
    # Geometry
    "boolean_operation", "extrude_curve", "loft_surface", "revolve_profile",
    # Sections & plans
    "create_section", "cut_section", "create_plan", "create_all_plans",
    "list_sections",
    # Materials
    "set_pbr_material", "list_materials", "search_materials", "download_material",
    # Import & tracing
    "import_dwg", "trace_pdf", "get_pdf_info",
    # Design memory
    "set_design_brief", "get_design_brief", "add_design_rule", "search_memory",
    # Scene sync & state
    "get_scene_diff", "set_state", "get_state",
    # Safety & ops
    "batch_preview", "list_checkpoints", "cancel_operation", "export_objects",
    # Escape hatches
    "execute_python3", "run_command", "get_rhino_commands",
})


def _inline_schema_refs(schema: Any, defs: dict[str, Any] | None = None, _depth: int = 0) -> Any:
    """Resolve $ref/$defs in a JSON schema so it is readable without dereferencing.

    Tools that take a single pydantic model advertise
        {"properties": {"params": {"$ref": "#/$defs/ScriptInput"}}, "$defs": {...}}
    The full field list IS present, but only inside $defs. Clients that do not
    resolve $ref render this as `{"params": {}}`, so an agent has to GUESS every
    parameter name and only finds out it was wrong from a validation error.
    That was the single biggest time cost reported from real sessions.

    Inlining keeps the calling convention identical - it only makes the existing
    schema self-describing.
    """
    if _depth > 12:  # cycle guard; our models nest at most 2-3 deep
        return schema
    if isinstance(schema, list):
        return [_inline_schema_refs(v, defs, _depth + 1) for v in schema]
    if not isinstance(schema, dict):
        return schema

    if defs is None:
        defs = schema.get("$defs") or {}

    ref = schema.get("$ref")
    if isinstance(ref, str) and ref.startswith("#/$defs/"):
        target = defs.get(ref.split("/")[-1])
        if isinstance(target, dict):
            resolved = _inline_schema_refs(target, defs, _depth + 1)
            # Preserve any sibling keys (e.g. a description on the property).
            merged = {k: v for k, v in schema.items() if k != "$ref"}
            return {**resolved, **merged} if merged else resolved

    out = {}
    for key, value in schema.items():
        if key == "$defs":
            continue
        out[key] = _inline_schema_refs(value, defs, _depth + 1)
    return out


def _flatten_tool_schemas() -> int:
    """Rewrite every registered tool's advertised schema to be self-describing."""
    try:
        tools = mcp._tool_manager._tools
    except AttributeError:
        logger.warning("FastMCP tool registry not found - schemas left as-is.")
        return 0
    changed = 0
    for tool in tools.values():
        schema = getattr(tool, "parameters", None)
        if not isinstance(schema, dict) or "$defs" not in schema:
            continue
        try:
            tool.parameters = _inline_schema_refs(schema)
            changed += 1
        except Exception as e:  # never let schema polish break startup
            logger.warning("Could not inline schema for %s: %s", tool.name, e)
    if changed:
        logger.info("Inlined $ref schemas for %d tools (parameters are now self-describing).", changed)
    return changed


def _exposed_tool_count() -> int:
    try:
        return len(mcp._tool_manager._tools)
    except AttributeError:  # FastMCP internals moved - fall back to "unknown"
        return -1


def _apply_tool_profile() -> str:
    """Prune the FastMCP tool registry down to the selected profile.

    Anything pruned here is still executable through `batch` (the plugin dispatch
    table is unaffected) and is documented in the rhino://capabilities resource.
    """
    profile = os.environ.get("RHINO_TOOLS", "standard").strip().lower()
    if profile not in ("lean", "standard", "full"):
        logger.warning("Unknown RHINO_TOOLS=%r - using 'standard'. Valid: lean|standard|full.", profile)
        profile = "standard"
    if profile == "full":
        logger.info("Tool profile 'full': all %d tools exposed.", _exposed_tool_count())
        return profile
    allowed = _LEAN_TOOLS if profile == "lean" else _STANDARD_TOOLS
    try:
        tools = mcp._tool_manager._tools
    except AttributeError:
        logger.warning("FastMCP tool registry not found - exposing all tools (profile ignored).")
        return "full"
    unknown = allowed - set(tools)
    if unknown:  # typo guard: profile names must match registered tools
        logger.warning("Profile lists unregistered tool names (ignored): %s", sorted(unknown))
    pruned = [name for name in list(tools) if name not in allowed]
    for name in pruned:
        del tools[name]
    logger.info(
        "Tool profile '%s': %d tools exposed, %d pruned (still callable via batch; set RHINO_TOOLS=full to restore).",
        profile, len(tools), len(pruned),
    )
    return profile


_TOOL_PROFILE = _apply_tool_profile()
_SCHEMAS_INLINED = _flatten_tool_schemas()


# =============================================================================
# ENTRY POINT
# =============================================================================

def main():
    """Entry point for the rhino-architect MCP server."""
    mcp.run()


if __name__ == "__main__":
    main()
