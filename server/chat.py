# RhinoAIBridge â€” Multi-Provider Chat Client
# by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

#!/usr/bin/env python3
"""RhinoAIBridge â€” multi-provider chat client.

Bypasses the MCP layer and talks directly to the Rhino plugin over TCP.
Works with any OpenAI-compatible API: OpenAI, Ollama, LM Studio, etc.

Usage:
  uv run python chat.py                                  # Ollama + qwen2.5-coder:7b
  uv run python chat.py --provider openai                # OpenAI gpt-4o  (needs OPENAI_API_KEY)
  uv run python chat.py --provider openai --model gpt-4o-mini
  uv run python chat.py --provider codex                        # OpenAI Codex (codex-mini-latest)
  uv run python chat.py --provider ollama --model llama3.1:8b
  uv run python chat.py --base-url http://localhost:1234/v1 --model lmstudio-model

Ollama models with reliable tool-calling:
  qwen2.5-coder:7b  (recommended â€” best function-calling accuracy)
  qwen2.5:14b       (larger, more capable)
  llama3.1:8b       (good general-purpose)
  mistral:7b        (solid tool use)

Environment variables:
  OPENAI_API_KEY   â€” required when using --provider openai
  RHINO_HOST       â€” plugin host (default: 127.0.0.1)
  RHINO_PORT       â€” plugin port (default: 9544)
"""

from __future__ import annotations

import argparse
import gzip
import json
import os
import struct
import sys
from pathlib import Path
from typing import Any

# â”€â”€ Dependency check â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

try:
    from openai import OpenAI
    import orjson
except ImportError as _e:
    print(f"ERROR: missing package â€” {_e}")
    print("Run:  uv add openai   (orjson is already in the venv)")
    sys.exit(1)

# â”€â”€ Sync TCP connection to the Rhino plugin â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

class RhinoBridge:
    """Blocking TCP socket to the plugin. Same wire format as protocol.py (4-byte BE length + JSON)."""

    MAX_FRAME = 50 * 1024 * 1024  # 50 MB cap â€” matches plugin

    def __init__(self, host: str = "127.0.0.1", port: int = 9544):
        import socket
        self._sock = socket.create_connection((host, port), timeout=5.0)
        self._sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self._sock.settimeout(60.0)
        token_path = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "AIBridge" / "token"
        if token_path.is_file():
            token = token_path.read_text(encoding="utf-8").strip()
            if token:
                response = self._send_recv({"type": "auth", "token": token})
                if response.get("status") != "ok" and response.get("error_code") == "AUTH_REQUIRED":
                    raise ConnectionError("Rhino rejected the local auth token. Restart AIBridge in Rhino.")

    def _send_recv(self, payload: dict[str, Any]) -> dict[str, Any]:
        body = orjson.dumps(payload)
        # Client â†’ server: old 4-byte format (requests are always small)
        self._sock.sendall(struct.pack(">I", len(body)) + body)
        # Server â†’ client: Tier 1 protocol [1-byte flag][4-byte length][payload]
        flag = self._recv_exact(1)[0]
        (length,) = struct.unpack(">I", self._recv_exact(4))
        if length <= 0 or length > self.MAX_FRAME:
            raise ConnectionError(f"Bad frame length: {length}")
        data = self._recv_exact(length)
        if flag == 0x01:
            data = gzip.decompress(data)
        return orjson.loads(data)

    def _recv_exact(self, n: int) -> bytes:
        buf = bytearray()
        while len(buf) < n:
            chunk = self._sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("Rhino plugin disconnected unexpectedly")
            buf.extend(chunk)
        return bytes(buf)

    def call(self, command: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        payload: dict[str, Any] = {"type": command}
        if params:
            payload["params"] = params
        return self._send_recv(payload)

    def batch(self, commands: list[dict], atomic: bool = True,
              stop_on_error: bool | None = None) -> dict[str, Any]:
        payload: dict[str, Any] = {"type": "batch", "commands": commands, "atomic": atomic}
        if stop_on_error is not None:
            payload["stop_on_error"] = stop_on_error
        return self._send_recv(payload)

    def close(self) -> None:
        try:
            self._sock.close()
        except Exception:
            pass


# â”€â”€ Tool dispatch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

# Max characters of a tool result returned to the model. Large scene dumps get truncated
# so they don't eat the context window; the model can re-query with a tighter filter.
_MAX_RESULT_CHARS = 6000


def dispatch(bridge: RhinoBridge, tool_name: str, args: dict[str, Any]) -> str:
    """Execute a tool call against the plugin and return a JSON string."""
    try:
        if tool_name == "batch":
            commands = args.get("commands", [])
            atomic = args.get("atomic", True)
            stop_on_error = args.get("stop_on_error")
            result = bridge.batch(commands, atomic=atomic, stop_on_error=stop_on_error)
        else:
            result = bridge.call(tool_name, args if args else None)

        text = json.dumps(result, indent=2)
        if len(text) > _MAX_RESULT_CHARS:
            text = text[:_MAX_RESULT_CHARS] + "\n... (truncated â€” use a tighter filter to see more)"
        return text
    except Exception as exc:
        return json.dumps({"status": "error", "message": str(exc)})


# â”€â”€ Tool schemas (OpenAI function-calling format) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

def _fn(name: str, description: str, properties: dict, required: list[str] | None = None) -> dict:
    return {
        "type": "function",
        "function": {
            "name": name,
            "description": description,
            "parameters": {
                "type": "object",
                "properties": properties,
                "required": required or [],
                "additionalProperties": False,
            },
        },
    }


TOOLS: list[dict] = [

    _fn("ping", "Verify Rhino is alive. Returns build hash, units, scene_version.", {}),

    _fn("query_scene",
        "Query the scene. scope='summary' for scene overview, 'layers' for layer list, "
        "'objects' (default) to list objects. Filtered by layer/type/name_pattern. "
        "Returns scene_version etag â€” skip re-querying if version unchanged.",
        {
            "scope":  {"type": "string", "enum": ["objects", "layers", "summary", "scene"],
                       "default": "objects"},
            "filter": {"type": "object", "description": "layer, type, name_pattern keys",
                       "properties": {
                           "layer":        {"type": "string"},
                           "type":         {"type": "string"},
                           "name_pattern": {"type": "string"},
                       }},
            "detail": {"type": "string", "enum": ["ids", "summary", "full"], "default": "summary"},
            "limit":  {"type": "integer", "default": 80, "minimum": 1, "maximum": 500},
        }),

    _fn("create_object",
        "Create geometry. Architectural types: wall, slab/floor, column, opening/window/door, "
        "roof, massing/building_mass, core. Primitives: box, sphere, cylinder, cone, line, "
        "polyline, circle, arc, curve, point. Returns object_ids.",
        {
            "type":   {"type": "string",
                       "description": "wall|slab|column|opening|roof|massing|core|box|sphere|"
                                      "cylinder|cone|line|polyline|circle|arc|ellipse|curve|point"},
            "params": {"type": "object",
                       "description": (
                           "Type-specific params. "
                           "box: {origin:[x,y,z], size_x, size_y, size_z}. "
                           "wall: {start_point:[x,y,z], end_point:[x,y,z], height, thickness}. "
                           "massing: {footprint:[[x,y,z],...], levels, level_height}. "
                           "core: {boundary:[[x,y,z],...], height, wall_thickness}. "
                           "All units in mm."
                       )},
            "layer":       {"type": "string"},
            "name":        {"type": "string"},
            "color":       {"type": "array", "items": {"type": "integer"},
                            "minItems": 3, "maxItems": 4},
            "measure":     {"type": "boolean", "default": False,
                            "description": "Return area/volume. Off by default (saves compute)."},
            "translation": {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
            "rotation":    {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 4},
        },
        required=["type"]),

    _fn("transform_objects",
        "Move, rotate, scale, mirror, or array objects. Use operations[] for chained transforms. "
        "Selectors: 'selected', 'all', 'last_created', 'by_layer:Wall', 'by_name:Floor*', or GUIDs.",
        {
            "object_ids":  {"type": "array", "items": {"type": "string"},
                            "description": "GUIDs or selector strings"},
            "translation": {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
            "angle_degrees": {"type": "number"},
            "center":      {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
            "scale_factor":{"type": "number"},
            "copy":        {"type": "boolean", "default": False},
            "count_x":     {"type": "integer"},
            "count_y":     {"type": "integer"},
            "spacing_x":   {"type": "number"},
            "spacing_y":   {"type": "number"},
            "operations":  {
                "type": "array",
                "description": "Chained ops: [{type:'move'|'rotate'|'scale'|'mirror'|'array', ...}]",
                "items": {"type": "object"},
            },
        },
        required=["object_ids"]),

    _fn("modify_object",
        "Change one object's name, layer, color, visibility, or apply a simple transform.",
        {
            "id":        {"type": "string", "description": "Object GUID"},
            "new_name":  {"type": "string"},
            "new_layer": {"type": "string"},
            "new_color": {"type": "array", "items": {"type": "integer"},
                          "minItems": 3, "maxItems": 4},
            "visible":   {"type": "boolean"},
            "translation":{"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
        }),

    _fn("batch",
        "Run multiple commands in one round-trip with optional atomic rollback. "
        "Each command: {\"type\": \"<command_name>\", \"params\": {...}}. "
        "Reference prior results with $N paths: '$1.object_ids[0]', '$2.mass_id'. "
        "Example: [{\"type\":\"create_object\",\"params\":{\"type\":\"massing\",...}}, "
        "{\"type\":\"derive_floors_from_mass\",\"params\":{\"mass_id\":\"$1.mass_id\",...}}]",
        {
            "commands": {
                "type": "array",
                "description": "List of {type, params} dicts. type = plugin command name.",
                "items": {
                    "type": "object",
                    "properties": {
                        "type":   {"type": "string",
                                   "description": "Command name e.g. create_object, derive_floors_from_mass"},
                        "params": {"type": "object"},
                    },
                    "required": ["type"],
                },
            },
            "atomic":        {"type": "boolean", "default": True,
                              "description": "Roll back all ops if any fails."},
            "stop_on_error": {"type": "boolean"},
        },
        required=["commands"]),

    # â”€â”€ Architect intelligence â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    _fn("derive_floors_from_mass",
        "Section a massing solid at floor heights and extrude each into a slab. "
        "Use level_heights[] for variable floor heights. Chain via $1.mass_id in a batch.",
        {
            "mass_id":      {"type": "string"},
            "level_heights":{"type": "array", "items": {"type": "number"},
                             "description": "Per-floor heights in mm. If empty, uses levels+level_height."},
            "levels":       {"type": "integer"},
            "level_height": {"type": "number", "default": 3000},
            "slab_thickness":{"type": "number", "default": 250},
            "start_z":      {"type": "number"},
            "layer":        {"type": "string", "default": "Slab"},
        },
        required=["mass_id"]),

    _fn("create_core",
        "Create a building core: perimeter walls plus lift/stair/shaft modules. "
        "punch_through[] subtracts the core from listed massing GUIDs.",
        {
            "boundary":       {"type": "array", "items": {"type": "array", "items": {"type": "number"}},
                               "description": "Corner points [[x,y,z],...] defining the core footprint"},
            "height":         {"type": "number", "default": 3000},
            "z_level":        {"type": "number", "description": "Base Z. Default: 0."},
            "wall_thickness": {"type": "number", "default": 200},
            "punch_through":  {"type": "array", "items": {"type": "string"},
                               "description": "Massing GUIDs to punch the core void through"},
            "wall_layer":     {"type": "string", "default": "Core::Walls"},
            "shaft_layer":    {"type": "string", "default": "Core::Shafts"},
        },
        required=["boundary"]),

    _fn("place_openings_on_facade",
        "Distribute windows or doors along walls at a constant rhythm. "
        "Pass wall_ids=['by_layer:Wall'] to apply to all walls at once.",
        {
            "wall_ids": {"type": "array", "items": {"type": "string"},
                         "description": "Wall GUIDs or selectors like 'by_layer:Wall'"},
            "rhythm":   {"type": "number", "default": 3000, "description": "Spacing between openings (mm)"},
            "sill":     {"type": "number", "default": 900},
            "head":     {"type": "number", "default": 2400},
            "width":    {"type": "number", "default": 1200},
            "layer":    {"type": "string", "default": "Opening"},
        },
        required=["wall_ids"]),

    _fn("align_to_grid",
        "Snap object bounding-box centres to an architectural grid.",
        {
            "object_ids":   {"type": "array", "items": {"type": "string"}},
            "grid_spacing": {"type": "number", "default": 1000},
            "snap_z":       {"type": "boolean", "default": False},
        },
        required=["object_ids"]),

    _fn("report_areas",
        "GFA/NFA area schedule grouped by layer, level, or name.",
        {
            "by":           {"type": "string", "enum": ["layer", "level", "name"], "default": "layer"},
            "level_height": {"type": "number", "default": 3000},
        }),

    # â”€â”€ Layers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    _fn("create_layer",
        "Create or update a layer.",
        {
            "name":    {"type": "string"},
            "color":   {"type": "array", "items": {"type": "integer"}, "minItems": 3, "maxItems": 4},
            "visible": {"type": "boolean", "default": True},
            "locked":  {"type": "boolean", "default": False},
            "parent":  {"type": "string"},
        },
        required=["name"]),

    _fn("setup_arch_layers",
        "Create the standard architectural layer set: Wall, Slab, Column, Beam, Opening, Roof, etc.",
        {
            "prefix": {"type": "string", "default": "",
                       "description": "Optional prefix for all layer names, e.g. 'Tower_'"},
        }),

    _fn("batch_layer_visibility",
        "Show/hide/isolate layers in one call.",
        {
            "show":    {"type": "array", "items": {"type": "string"}, "default": []},
            "hide":    {"type": "array", "items": {"type": "string"}, "default": []},
            "isolate": {"type": "string", "description": "Layer name to isolate (hides all others)"},
        }),

    # â”€â”€ Analysis â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    _fn("measure_object",
        "Return area, volume, length, and bounding box for one object.",
        {
            "object_id": {"type": "string"},
        },
        required=["object_id"]),

    _fn("measure_distance",
        "Distance between two 3D points.",
        {
            "point_a": {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
            "point_b": {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
        },
        required=["point_a", "point_b"]),

    _fn("check_intersection",
        "Check whether two objects' bounding boxes intersect.",
        {
            "object_id_a": {"type": "string"},
            "object_id_b": {"type": "string"},
        },
        required=["object_id_a", "object_id_b"]),

    _fn("validate_objects",
        "Validate object geometry for errors. Empty list = whole scene (capped at 100 Breps).",
        {
            "object_ids": {"type": "array", "items": {"type": "string"}, "default": []},
        }),

    # â”€â”€ Viewport â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    _fn("capture_viewport",
        "Capture the active viewport as JPEG or PNG. Returns base64 image data. "
        "restore_state=True (default) saves and restores camera+display-mode so the user's "
        "view is undisturbed. Pass view= / display_mode= to temporarily switch before capture.",
        {
            "width":        {"type": "integer", "default": 800},
            "height":       {"type": "integer", "default": 600},
            "format":       {"type": "string", "enum": ["auto", "jpeg", "png"], "default": "auto"},
            "quality":      {"type": "integer", "default": 80, "minimum": 1, "maximum": 100},
            "max_bytes":    {"type": "integer", "default": 800000},
            "restore_state":{"type": "boolean", "default": True},
            "view":         {"type": "string", "description": "Temporary view: Top, Front, Right, Perspective, etc."},
            "display_mode": {"type": "string", "description": "Temporary display mode: Wireframe, Shaded, Rendered, Arctic, etc."},
        }),

    _fn("set_view",
        "Switch viewport: Top, Front, Right, Perspective, etc.",
        {
            "view_name": {"type": "string"},
        },
        required=["view_name"]),

    _fn("set_display_mode",
        "Set display mode: Wireframe, Shaded, Rendered, Arctic, etc.",
        {
            "mode": {"type": "string"},
        },
        required=["mode"]),

    _fn("select_objects",
        "Select objects by GUID.",
        {
            "object_ids":      {"type": "array", "items": {"type": "string"}},
            "clear_selection": {"type": "boolean", "default": True},
        },
        required=["object_ids"]),

    _fn("set_camera",
        "Precisely position the viewport camera. Two modes: "
        "(1) Explicit: location + target + optional lens_length/projection. "
        "(2) Bbox framing: box_min + box_max â€” auto-computes camera distance to frame the volume.",
        {
            "location":    {"type": "array", "items": {"type": "number"}, "description": "Camera position [x,y,z]."},
            "target":      {"type": "array", "items": {"type": "number"}, "description": "Camera target [x,y,z]."},
            "lens_length": {"type": "number", "description": "Focal length mm: 50=normal, 24=wide, 135=tele."},
            "projection":  {"type": "string", "enum": ["perspective", "parallel"]},
            "box_min":     {"type": "array", "items": {"type": "number"}, "description": "BBox min [x,y,z] to frame."},
            "box_max":     {"type": "array", "items": {"type": "number"}, "description": "BBox max [x,y,z] to frame."},
        }),

    _fn("get_rhino_commands",
        "List all registered Rhino command names (live). Use to discover commands before calling "
        "them via execute_script or batch. filter is a case-insensitive substring.",
        {
            "filter": {"type": "string", "default": "", "description": "Substring filter (case-insensitive). Empty = all (capped 200)."},
        }),

    _fn("set_layer_material",
        "Set PBR material on a layer: color [R,G,B], roughness, metallic, opacity, emission. "
        "Updates both the display color and the render material (Rendered/Arctic viewport).",
        {
            "layer":     {"type": "string"},
            "color":     {"type": "array", "items": {"type": "integer"}, "description": "[R,G,B] or [R,G,B,A] 0-255"},
            "roughness": {"type": "number", "minimum": 0, "maximum": 1},
            "metallic":  {"type": "number", "minimum": 0, "maximum": 1},
            "opacity":   {"type": "number", "minimum": 0, "maximum": 1},
            "emission":  {"type": "array", "items": {"type": "integer"}, "description": "[R,G,B] emissive 0-255"},
        },
        required=["layer"]),

    _fn("run_command",
        "Execute any Rhino command string via RhinoApp.RunScript. "
        "Escape hatch â€” prefer structured tools when available. Tracks newly created objects.",
        {
            "command": {"type": "string", "description": "Command exactly as typed in Rhino command line"},
            "echo":    {"type": "boolean", "default": False},
        },
        required=["command"]),

    # â”€â”€ Geometry ops â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    _fn("get_cross_section",
        "Cut a solid at a Z height and return section curves â€” useful for plan views.",
        {
            "object_id": {"type": "string"},
            "z_height":  {"type": "number"},
            "layer":     {"type": "string"},
            "name":      {"type": "string"},
        },
        required=["object_id", "z_height"]),

    _fn("boolean_operation",
        "Boolean union/difference/intersection between two objects.",
        {
            "operation":    {"type": "string", "enum": ["union", "difference", "intersection"]},
            "object_id_a":  {"type": "string"},
            "object_id_b":  {"type": "string"},
            "delete_input": {"type": "boolean", "default": True},
        },
        required=["operation", "object_id_a", "object_id_b"]),

    _fn("delete_objects",
        "Delete objects by GUID or selector (all, by_layer:Layer, by_name:Pattern, selected).",
        {
            "object_ids": {"type": "array", "items": {"type": "string"}},
        },
        required=["object_ids"]),

    # â”€â”€ Escape hatches â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    _fn("execute_script",
        "Run arbitrary Python inside Rhino. Powerful escape hatch â€” prefer structured tools. "
        "Preamble auto-imports: rhinoscriptsyntax, scriptcontext, Rhino, System.",
        {
            "code":          {"type": "string"},
            "undo_name":     {"type": "string"},
            "default_layer": {"type": "string"},
        },
        required=["code"]),

    _fn("undo",
        "Undo one or more Rhino operations.",
        {
            "count": {"type": "integer", "default": 1, "minimum": 1},
        }),

    _fn("get_log",
        "Fetch recent bridge log entries for debugging.",
        {
            "count":       {"type": "integer", "default": 50},
            "errors_only": {"type": "boolean", "default": False},
        }),
]


# â”€â”€ System prompt â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

SYSTEM_PROMPT = """\
You are an AI assistant controlling Rhino 3D through RhinoAIBridge.
You have 28 tools covering scene queries, geometry creation, transforms, layers, and analysis.

Key rules:
- All dimensions are in MILLIMETRES unless the user explicitly says otherwise.
- When creating multiple related objects, use `batch` with atomic=true so they roll back together on failure.
- Chain results in a batch with $N references: $1.object_ids[0], $2.mass_id, $3.bounding_box.min.
- Before creating objects, call query_scene or ping to confirm Rhino is connected.
- Prefer structured tools (create_object, derive_floors_from_mass, etc.) over execute_script.
- Call report_areas after building a floor stack to verify GFA numbers.
- Think step by step. When unsure about dimensions, ask the user before creating geometry.
"""


# â”€â”€ Agentic chat loop â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

def _tool_call_summary(tool_name: str, args: dict) -> str:
    """One-line summary of a tool call for the terminal display."""
    if tool_name == "batch":
        cmds = args.get("commands", [])
        atomic = args.get("atomic", True)
        types = ", ".join(c.get("type", "?") for c in cmds[:4])
        suffix = "..." if len(cmds) > 4 else ""
        return f"batch({len(cmds)} ops, atomic={atomic}) [{types}{suffix}]"
    short = {k: v for i, (k, v) in enumerate(args.items()) if i < 3}
    suffix = " ..." if len(args) > 3 else ""
    parts = ", ".join(f"{k}={repr(v)[:30]}" for k, v in short.items())
    return f"{tool_name}({parts}{suffix})"


def run_chat(client: OpenAI, model: str, bridge: RhinoBridge) -> None:
    messages: list[dict] = [{"role": "system", "content": SYSTEM_PROMPT}]

    print(f"\n  RhinoAIBridge â€” {model}")
    print("  Type your request. 'exit' to quit.\n")

    while True:
        # â”€â”€ Get user input â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        try:
            user_input = input("You: ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\nBye.")
            break

        if not user_input:
            continue
        if user_input.lower() in ("exit", "quit", "q", "bye"):
            print("Bye.")
            break

        messages.append({"role": "user", "content": user_input})

        # â”€â”€ Agentic loop â€” keep going until the model stops calling tools â”€â”€â”€â”€â”€
        while True:
            try:
                response = client.chat.completions.create(
                    model=model,
                    messages=messages,
                    tools=TOOLS,
                    tool_choice="auto",
                    temperature=0.2,   # low temp for deterministic geometry
                )
            except Exception as exc:
                print(f"\n[API error] {exc}\n")
                messages.pop()   # drop the failed user turn so state stays clean
                break

            choice = response.choices[0]
            msg = choice.message

            # Serialise message for history (exclude None fields cleanly)
            msg_dict: dict = {"role": "assistant"}
            if msg.content:
                msg_dict["content"] = msg.content
            if msg.tool_calls:
                msg_dict["tool_calls"] = [
                    {
                        "id": tc.id,
                        "type": "function",
                        "function": {"name": tc.function.name, "arguments": tc.function.arguments},
                    }
                    for tc in msg.tool_calls
                ]
            messages.append(msg_dict)

            # â”€â”€ No tool calls â€” model is done, print reply â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            if not msg.tool_calls:
                print(f"\nAssistant: {msg.content or '(no reply)'}\n")
                break

            # â”€â”€ Execute each tool call and feed results back â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            for tc in msg.tool_calls:
                fn_name = tc.function.name
                try:
                    fn_args = json.loads(tc.function.arguments)
                except json.JSONDecodeError:
                    fn_args = {}

                print(f"  âš™  {_tool_call_summary(fn_name, fn_args)}")
                result_text = dispatch(bridge, fn_name, fn_args)

                # Parse first line of result for a quick status hint
                try:
                    status = json.loads(result_text).get("status", "")
                    hint = f"  â†’ {status}" if status and status != "ok" else ""
                except Exception:
                    hint = ""
                if hint:
                    print(hint)

                messages.append({
                    "role": "tool",
                    "tool_call_id": tc.id,
                    "content": result_text,
                })

        print()   # blank line between exchanges


# â”€â”€ Entry point â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

def main() -> None:
    parser = argparse.ArgumentParser(
        description="RhinoAIBridge chat â€” connect Rhino to any OpenAI-compatible model",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    provider_grp = parser.add_mutually_exclusive_group()
    provider_grp.add_argument("--provider", choices=["openai", "codex", "ollama"], default=None,
                              help="Shorthand provider. Sets base-url and default model.")
    parser.add_argument("--model", default=None, help="Model name (overrides provider default)")
    parser.add_argument("--base-url", default=None,
                        help="OpenAI-compatible API base URL (overrides --provider)")
    parser.add_argument("--api-key", default=None,
                        help="API key (defaults to OPENAI_API_KEY env var; 'ollama' for Ollama)")
    parser.add_argument("--host", default=os.environ.get("RHINO_HOST", "127.0.0.1"),
                        help="Rhino plugin host (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=int(os.environ.get("RHINO_PORT", "9544")),
                        help="Rhino plugin port (default: 9544)")
    args = parser.parse_args()

    # â”€â”€ Resolve provider settings â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    provider = args.provider or ("openai" if args.base_url and "openai" in (args.base_url or "") else "ollama")

    if args.base_url:
        base_url = args.base_url
        api_key = args.api_key or os.environ.get("OPENAI_API_KEY", "local")
        model = args.model or "unknown-model"
    elif provider == "openai":
        base_url = "https://api.openai.com/v1"
        api_key = args.api_key or os.environ.get("OPENAI_API_KEY", "")
        model = args.model or "gpt-4o"
        if not api_key:
            print("ERROR: OPENAI_API_KEY env var not set (or pass --api-key).", file=sys.stderr)
            sys.exit(1)
    elif provider == "codex":
        base_url = "https://api.openai.com/v1"
        api_key = args.api_key or os.environ.get("OPENAI_API_KEY", "")
        model = args.model or "codex-mini-latest"
        if not api_key:
            print("ERROR: OPENAI_API_KEY env var not set (or pass --api-key).", file=sys.stderr)
            sys.exit(1)
    else:   # ollama
        base_url = "http://localhost:11434/v1"
        api_key = "ollama"
        model = args.model or "qwen2.5-coder:7b"

    # â”€â”€ Connect to Rhino â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    print(f"Connecting to Rhino at {args.host}:{args.port} ...", end=" ", flush=True)
    try:
        bridge = RhinoBridge(args.host, args.port)
        # Quick ping to confirm the plugin is alive
        pong = bridge.call("ping")
        build = pong.get("build_hash", "?")
        units = pong.get("units", "?")
        print(f"ok  (build:{build}, units:{units})")
    except Exception as exc:
        print(f"FAILED\n\nERROR: {exc}", file=sys.stderr)
        print("\nMake sure Rhino 8 is open and AIBridge is running.", file=sys.stderr)
        print("In Rhino's command line, type:  AIBridge", file=sys.stderr)
        sys.exit(1)

    # â”€â”€ Create OpenAI client â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    client = OpenAI(base_url=base_url, api_key=api_key)

    # â”€â”€ Run â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    try:
        run_chat(client, model, bridge)
    finally:
        bridge.close()


if __name__ == "__main__":
    main()
