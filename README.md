# RhinoAIBridge v4.7.6 — AI Control of Rhino 8 via MCP

> **The most powerful Rhino MCP server.** 112 tools give Claude, ChatGPT, Codex, or Ollama full control of Rhino 8 — create geometry, manipulate layers, capture viewports, manage materials, generate architectural drawings, trace PDFs, and more. No .NET SDK required on the target machine.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Rhino 8](https://img.shields.io/badge/Rhino-8.x-blue)](https://www.rhino3d.com/)
[![MCP](https://img.shields.io/badge/MCP-Model%20Context%20Protocol-green)](https://modelcontextprotocol.io/)
[![Works with Claude](https://img.shields.io/badge/Works%20with-Claude-orange)](https://claude.ai/)
[![Works with ChatGPT](https://img.shields.io/badge/Works%20with-ChatGPT-brightgreen)](https://openai.com/)
[![Works with Codex](https://img.shields.io/badge/Works%20with-Codex-blue)](https://openai.com/codex)
[![Works with Ollama](https://img.shields.io/badge/Works%20with-Ollama-purple)](https://ollama.com/)

---

## What is this?

**RhinoAIBridge** is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that bridges any MCP-compatible AI — Claude Desktop, ChatGPT, Codex, or local Ollama models — directly into Rhino 3D (version 8). The AI can read your scene, create and modify geometry, manage layers, run scripts, capture viewport images, generate architectural drawings, manage materials, trace PDFs, and execute any Rhino command — all from a natural language conversation.

This is the **fastest, most feature-complete Rhino AI integration available**:

- **Sub-millisecond ping** with in-process scene cache (no full scene walks)
- **Local authenticated TCP bridge** — per-user token handshake on `127.0.0.1`
- **Auto-thumbnails** — every mutation returns a viewport JPEG so the AI sees what it built
- **Atomic batches** — multi-step operations roll back as one unit on failure
- **No .NET SDK required** on target machines — plugin is pre-built
- **112 MCP tools** across 12 categories

---

## Quick Install (2 minutes)

**Requirements:** Rhino 8 · Python (auto-installed via uv) · Claude Desktop / ChatGPT API key / Codex / Ollama

### Windows

1. **Download** the latest release zip and unzip anywhere
2. **Close Rhino**, then **double-click `INSTALL.bat`** — copies the pre-built plugin, installs dependencies, and patches Claude Desktop, Codex, and Gemini Antigravity automatically
3. **Open Rhino 8** → type `PluginManager` → Install → browse to:
   `%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\RhinoAIBridge\RhinoAIBridge.rhp`
   *(First time only — auto-loads on future starts)*
4. **In Rhino command line**, type: `AIBridge`, then choose Safe, Standard, or Developer mode
5. **Restart Claude Desktop** → ask: *"ping Rhino"*

That is it. No Visual Studio. No .NET SDK. No manual config editing.

For a detailed walkthrough see **INSTALL_GUIDE.txt**.

---

## 112 MCP Tools

### Scene & Context
| Tool | What it does |
|------|-------------|
| `ping` | Liveness check — returns doc name, unit system, object count, protocol version |
| `query_scene` | Smart scene query: filter by type, layer, name, bbox, visibility |
| `get_rhino_commands` | Discover all available Rhino commands with substring filter |
| `get_state` | Read persistent key-value state |
| `set_state` | Write persistent key-value state |
| `clear_state` | Clear all persistent state |

### Geometry Creation
| Tool | What it does |
|------|-------------|
| `create_object` | Universal create: Box, Cylinder, Sphere, Wall, Slab, Column, Roof, Curve, Point... |
| `derive_floors_from_mass` | Auto-generate floor slabs by intersecting a massing solid |
| `create_core` | Structural core punched through a massing solid |
| `place_openings_on_facade` | Parametric window/door placement on any brep face |
| `revolve_profile` | Create solids of revolution from profile curves |
| `get_cross_section` | Cut a section curve at any Z height |

### Modify & Transform
| Tool | What it does |
|------|-------------|
| `modify_object` | Change name, layer, colour, visibility, material |
| `transform_objects` | Move, rotate, scale, mirror, array |
| `boolean_operation` | Union, difference, intersection |
| `delete_objects` | Delete by ID list |
| `align_to_grid` | Snap objects to a grid |
| `undo` | Undo last operation |

### Layers & Organization
| Tool | What it does |
|------|-------------|
| `create_layer` | Create layer with colour |
| `create_layer_tree` | Create hierarchical layer structure |
| `setup_arch_layers` | One-call layer stack: Site, Structure, Facade, Slab, Core, Roof... |
| `batch_layer_visibility` | Show/hide multiple layers at once |
| `name_group` | Name a group of objects |
| `get_group` | Get group info |
| `get_all_groups` | List all groups |

### Materials & Appearance
| Tool | What it does |
|------|-------------|
| `set_layer_material` | PBR material: color, roughness, metallic, opacity, emission |
| `set_pbr_material` | Full PBR material on individual objects |
| `search_materials` | Search AmbientCG material library |
| `download_material` | Download and apply materials from AmbientCG |
| `edit_material` | Edit existing material properties |
| `list_materials` | List all materials in the document |
| `get_material` | Get details of a specific material |

### Viewport & Capture
| Tool | What it does |
|------|-------------|
| `capture_viewport` | JPEG/PNG viewport image with auto-downscale + state restore |
| `capture_review_set` | Hero, plan, elevation, and detail captures in one multi-image call |
| `compare_before_after` | Capture before, run a batch, capture after, and return visual diff metrics |
| `set_view` | Switch to Top/Front/Right/Perspective/named view |
| `set_display_mode` | Wireframe, Shaded, Rendered, Ghosted, Arctic... |
| `set_camera` | Position camera by location+target or zoom to bounding box |
| `select_objects` | Highlight objects in the Rhino viewport |
| `thumbnail` | Quick low-res viewport thumbnail |

### Sections & Drawings
| Tool | What it does |
|------|-------------|
| `create_section` | Define a section plane |
| `create_elevation` | Create elevation view |
| `cut_section` | Generate section cut geometry |
| `align_view_to_section` | Orient viewport to match section |
| `create_plan` | Generate a plan view at a given level |
| `create_all_plans` | Auto-generate all floor plans |
| `list_sections` | List defined sections |
| `update_section` | Modify section parameters |
| `remove_section` | Delete a section |

### Display Mode Engine
| Tool | What it does |
|------|-------------|
| `create_display_mode` | 8 presets: diagram, technical, blueprint, sketch, axonometric, atmospheric, monochrome, cutaway |
| `apply_display_mode` | Apply a custom display mode |
| `list_display_modes` | List available display modes |
| `adjust_display_mode` | Fine-tune display mode parameters |
| `delete_display_mode` | Remove a custom display mode |
| `capture_illustration` | Capture viewport with illustration settings |

### PDF & File Import
| Tool | What it does |
|------|-------------|
| `get_pdf_info` | PDF metadata and page count |
| `preview_pdf_page` | Preview a PDF page as image |
| `trace_pdf` | Trace PDF geometry into Rhino (vector + OCR pipeline) |
| `clear_trace_layers` | Remove traced PDF layers |
| `get_trace_layers` | List traced PDF layers |
| `import_dwg` | Import DWG/DXF files |
| `calibrate_scale` | Calibrate imported geometry scale |
| `export_objects` | Export objects to various formats |

### Analysis & Measurement
| Tool | What it does |
|------|-------------|
| `measure_object` | Area, volume, bounding box, centroid |
| `measure_distance` | Distance between two points |
| `check_intersection` | Detect clashes between objects |
| `validate_objects` | Check for bad geometry / open edges |
| `report_areas` | Area schedule grouped by layer or type |
| `analyze_architecture` | Architectural analysis of the model |
| `get_building_systems` | Identify building systems |
| `get_level_summary` | Summary per floor level |
| `detect_design_patterns` | Detect repeated design patterns |
| `find_unassigned_geometry` | Find geometry not assigned to any layer category |

### Design Memory & Provenance
| Tool | What it does |
|------|-------------|
| `set_design_brief` | Store design intent and constraints |
| `get_design_brief` | Retrieve current design brief |
| `tag_object` | Tag objects with metadata |
| `get_provenance` | Get object creation/modification history |
| `search_memory` | Search design memory by keyword |
| `get_related_objects` | Find objects related by design intent |
| `add_design_rule` | Add a design validation rule |
| `log_session` | Log a design session summary |
| `get_scene_diff` | Compare current scene to a previous state |
| `get_change_log` | Get change history |
| `get_tracker_version` | Get change tracker version |

### Scripting & Workflow
| Tool | What it does |
|------|-------------|
| `run_command` | Execute any Rhino command string; returns new object IDs |
| `execute_script` | Run RhinoScript or Python code |
| `execute_python3` | Run Rhino 8 CPython 3 via RhinoCode/rhinocode in Developer mode |
| `batch` | Send multiple operations as one atomic transaction |
| `batch_preview` | Preview batch operations without executing |
| `get_log` | Inspect bridge command log with timestamps and timing |
| `save_checkpoint` | Save scene checkpoint for rollback |
| `restore_checkpoint` | Restore scene to a saved checkpoint |
| `list_checkpoints` | List available checkpoints |

---

## Connecting Different AI Providers

### Claude Desktop (recommended)
The installer patches `claude_desktop_config.json` automatically. Just restart Claude Desktop after install.

Manual config — `%APPDATA%\Claude\claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "rhino-architect": {
      "command": "uv",
      "args": ["--directory", "C:\\path\\to\\rhino-mcp\\server", "run", "rhino-architect"]
    }
  }
}
```

### OpenAI Codex
INSTALL.bat option [4] writes `%USERPROFILE%\.codex\config.toml` automatically.

Manual config — `~/.codex/config.toml`:
```toml
[mcp_servers.rhino_architect]
command = "uv"
args = ["--directory", "C:\\path\\to\\rhino-mcp\\server", "run", "--frozen", "rhino-architect"]
startup_timeout_sec = 20
tool_timeout_sec = 120
enabled = true

[mcp_servers.rhino_architect.env]
RHINO_HOST = "127.0.0.1"
RHINO_PORT = "9544"
RHINO_SAFE_MODE = "0"
```

Or via CLI:
```powershell
codex mcp add rhino_architect --env RHINO_SAFE_MODE=0 -- uv --directory "C:\path\to\rhino-mcp\server" run --frozen rhino-architect
codex mcp list
```

Then in Rhino run `AIBridge`, and ask Codex: *"ping Rhino"*

### Gemini Antigravity
`INSTALL.bat` configures `%USERPROFILE%\.gemini\antigravity\mcp_config.json` automatically when Antigravity is detected.

Restart Antigravity after installation, run `AIBridge` in Rhino, choose an access mode, and ask: *"ping Rhino"*

### ChatGPT
```
cd server
set OPENAI_API_KEY=sk-...
uv run python chat.py --provider openai --model gpt-4o
```

### Ollama (fully local, free)
```
ollama pull qwen2.5-coder:7b
cd server
uv run python chat.py --provider ollama --model qwen2.5-coder:7b
```
Best local models: `qwen2.5-coder:32b`, `deepseek-r1:32b`, `llama3.1:70b`

### Anthropic API directly
```
cd server
set ANTHROPIC_API_KEY=sk-ant-...
uv run python chat.py --provider anthropic --model claude-opus-4-6
```

---

## Architecture

```
Claude Desktop / ChatGPT / Codex / Ollama
         |  MCP (stdio)
         v
  server/src/rhino_architect/server.py   <- FastMCP Python server (112 tools)
         |  TCP 127.0.0.1:9544
         |  local auth token + [1-byte flag][4-byte len][JSON payload]
         v
  plugin/RhinoAIBridge.rhp              <- C# Rhino 8 plugin (.NET 8)
         |  UI thread dispatch + deferred redraw
         v
  Rhino 8 Document
```

**Wire protocol:** The local bridge requires a per-user auth token before it accepts commands. Responses use raw JSON framing to avoid Rhino plugin-loader runtime dependency failures. The scene snapshot cache means repeated `query_scene` calls are O(1) regardless of object count.

**Atomic batches:** Send `batch(commands=[...], atomic=true)` and the entire sequence runs inside one Rhino undo record. Any failure triggers `Doc.Undo()` — the scene is left exactly as it was.

**Auto-thumbnails:** Every mutating tool captures a 240x180 JPEG after the viewport redraws and embeds it in the response. Claude sees the result immediately without a separate `capture_viewport` call.

---

## Building from Source

Only needed if you want to modify the C# plugin. Target machines do NOT need the .NET SDK.

Requirements: .NET 8 SDK, Rhino 8 (for RhinoCommon)

```
cd plugin
dotnet build --configuration Release
```

Output: `plugin/bin/Release/net8.0/`. Copy `.rhp`, DLLs, JSON runtime files, and `runtimes/` to the Rhino plugin folder.

---

## Troubleshooting

**"Cannot connect to Rhino"** — Make sure Rhino is open and you have run the `AIBridge` command in Rhino's command line.

**Plugin does not appear in Claude** — Restart Claude Desktop after install. Check `%APPDATA%\Claude\claude_desktop_config.json` contains the `rhino-architect` server entry.

**Protocol version mismatch** — Run `INSTALL.bat` again to copy the latest plugin binary. The v4.7.6 plugin reports protocol 4.7.

**uv not found** — Open a new terminal after install (PATH change does not apply to the current session).

**Ollama connection refused** — Run `ollama serve` in a separate terminal before starting `chat.py`.

**Large scenes slow** — Use `query_scene` with filters rather than fetching everything at once. The cache handles 5000+ objects at interactive speed.

**Run health check** — `cd server && uv run python ../scripts/doctor.py` checks plugin, port, Claude Desktop, and Codex config in one pass.

**Codex TOML parse error** — Re-run `INSTALL.bat`. v4.7.6 uses inline array format that all TOML parsers accept.

**Codex not seeing the server** — Run `codex mcp list` and confirm `rhino_architect` is listed and `enabled = true`. Re-run INSTALL.bat option [4] if missing.

---

## Changelog

### v4.7.6 (current)
- **Rhino 8 runtime compatibility** — removes plugin-loader dependencies on `System.Threading.Channels` and response compression assemblies
- **Authenticated localhost bridge** — per-user token handshake, client cap, idle timeout, and connection cleanup
- **Startup access modes** — run `AIBridge` and choose Safe, Standard, or Developer mode
- **112 MCP tools** — direct MCP images, review captures, before/after diffs, CPython 3 execution, McNeel-compatible aliases, surface primitives, SVG section/silhouette feedback, and JSON fallbacks
- **Geometry correctness fixes** — live transform GUIDs, planar offsets, vertical wall validation, layer full paths, and closed polyline reporting
- **Clean SDK-free installer** — deploys the complete pre-built .NET 8 payload and configures Claude Desktop, Codex, and Gemini Antigravity

### v4.7.5
- **90 tools** — up from 32 in v4.5
- **15 missing dispatch entries fixed** — `set_design_brief`, `get_design_brief`, `tag_object`, `get_provenance`, `search_memory`, `get_related_objects`, `name_group`, `get_group`, `get_all_groups`, `add_design_rule`, `log_session`, `get_scene_diff`, `get_change_log`, `get_tracker_version`, and `get_design_rules` now properly registered in the C# plugin
- **Double-serialization fix** — 35+ tools were wrapping JSON in `json.dumps()` causing clients to receive escaped strings. All tools now return native dicts
- **MCP tool annotations** — every tool tagged with read-only, write, write-idempotent, or destructive hints for AI clients
- **Codex TOML fix** — inline args array format prevents parse errors on all platforms
- **Protocol v4.7** baked into the pre-built plugin binary

### v4.7.4
- Job system with background execution
- Force-refresh on scene queries
- 12 new architectural intelligence tools
- Installer improvements and healthcheck script

### v4.7.0
- **Sections & Plans**: `create_section`, `create_elevation`, `cut_section`, `create_plan`, `create_all_plans`
- **Illustration Engine**: `create_display_mode` with 8 presets; `capture_illustration`
- **Material Intelligence**: `search_materials`, `download_material` with unit-aware UV scaling
- **PDF Tracing**: `trace_pdf` (PyMuPDF pipeline, vector text at 1.0 confidence)
- **File Import**: `import_dwg`, `calibrate_scale`
- **Codex support**: `scripts/patch_codex_config.py` + INSTALL.bat option [4]
- **Doctor script**: `scripts/doctor.py` — full health-check

### v4.6
- Auth token + 3-tier trust modes
- Dry-run support on delete/boolean/batch
- Viewport metadata + scene query modes
- 42-test pytest suite

### v4.5
- Pre-built plugin — no .NET SDK required on target machines
- `set_camera`, `get_rhino_commands`, `set_layer_material`, `run_command`
- Auto-thumbnails, gzip compression, atomic batch rollback

### v4.0
- Scene snapshot cache — O(1) reads
- Deferred redraw and atomic batches
- Architect intelligence: massing, floors, core, facade, schedules

---

## License

MIT — see LICENSE. Free for personal and commercial use.

---

*Built by Tanishq Bhattad — https://github.com/tanishqbhattad*
