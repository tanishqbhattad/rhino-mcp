# Rhino MCP — AI-Assisted Architectural Modelling in Rhino 3D

**A Model Context Protocol (MCP) server that gives Claude, ChatGPT, Codex, Gemini or a local Ollama model full control of Rhino 8 / Rhinoceros 3D.** Describe a building in plain language and watch it get modelled — massing, floor plates, cores, facades, vaults, plans, sections and area schedules — with the AI checking its own work as it goes.

> Built by an architect, for architects. No coding required to use it.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Rhino 8](https://img.shields.io/badge/Rhino-8.x-blue)](https://www.rhino3d.com/)
[![MCP](https://img.shields.io/badge/MCP-Model%20Context%20Protocol-green)](https://modelcontextprotocol.io/)
[![Works with Claude](https://img.shields.io/badge/Works%20with-Claude-orange)](https://claude.ai/)
[![Works with ChatGPT](https://img.shields.io/badge/Works%20with-ChatGPT-brightgreen)](https://openai.com/)
[![Works with Codex](https://img.shields.io/badge/Works%20with-Codex-blue)](https://openai.com/codex)
[![Works with Ollama](https://img.shields.io/badge/Works%20with-Ollama-purple)](https://ollama.com/)

*Keywords: rhino mcp · rhino 3d mcp server · rhinoceros mcp · model context protocol rhino · rhino 8 ai plugin · claude rhino integration · ai architectural modelling · ai 3d modelling · generative architecture · computational design · rhinocommon automation · architectural bim ai*

---

## Why this Rhino MCP?

Several Rhino MCP servers exist. This one is built for **long, real modelling sessions** — one user drove it to ~8,000 objects reconstructing a Gothic cathedral at 1:1, with zero invalid breps.

|  | This project |
|---|---|
| **Tool surface** | 123 tools, curated into `lean` / `standard` / `full` profiles so small models aren't drowned |
| **Transport** | **Protocol 5** — multiplexed, so reads, `ping` and cancel answer instantly while a 3-minute script runs |
| **Reliability** | Idempotent retries (no duplicate geometry on reconnect), atomic batches with rollback, write-ahead-log crash recovery |
| **Correctness** | **Intent validation** — asserts what the geometry *means*, not just that it parses |
| **Geometry stdlib** | `rab` helpers auto-imported into every script: walls, slabs, grids, **pointed arches, rib vaults, rose tracery, mouldings** |
| **Vision loop** | Auto-thumbnails after every edit, multi-angle review sets, before/after pixel diffs, one-call section previews |
| **Install** | Pre-built plugin — **no .NET SDK needed**, one double-click, and it verifies itself |

If Rhino MCP saves you modelling time, please **star the repo** — stars are how other Rhino users find it.

---

## What makes it different: the AI checks its own work

Valid geometry is not correct geometry. An AI generating thousands of parametric objects makes **arithmetic and wiring** errors — a doubled base height, swapped arguments, a boolean cutter added instead of subtracted — and every one of those produces a perfectly closed, valid solid that `validate_objects` would happily pass.

So this server validates **intent**:

```jsonc
assert_geometry(assertions=[
  {"kind": "bbox",      "selector": ["by_name:nave_web"], "z_max": 33000, "tol": 10},
  {"kind": "count",     "selector": ["by_layer:Vault"],   "expect": 60},
  {"kind": "supported", "selector": ["last_created"],     "max_gap": 150},
  {"kind": "envelope",  "selector": ["all"], "box": [[0,-24000,-1000],[130000,24000,97000]]}
])
```

Failures come back with the **offending object IDs**, at the moment of creation — while the fix is one parameter away instead of twenty tool calls later.

- **`assert_geometry`** — bbox / envelope / count / count_delta / watertight / supported post-conditions
- **`find_unsupported`** — finds floating spires, pinnacles and statuary (nothing beneath them)
- **`section_preview`** — cut the model at any station and look inside, in one call, leaving no geometry behind
- **`validate_objects`** — separates *real corruption* from *intentionally open shells*, with naked-edge lengths so a hairline gap stands out

---

## For architects — no coding needed

If you can describe a building, you can model it:

> *"Create a 4-storey office massing on a 30×18 m footprint, taller ground floor, derive the floor slabs, add a core with two lifts and a stair, then put windows on the north facade every 3 metres."*

You can ask for:

- **Massing studies** — footprints, levels, setbacks, options side by side
- **Floor plans and sections** — cut, captured and restored without touching your viewport
- **Area schedules** — GFA/NFA by layer, level or name, ready to paste into a report
- **Historical work** — pointed and equilateral arches, rib vaults, rose windows, mouldings
- **Materials and views** — *"make the facade glass, the core concrete, show me a hero shot"*
- **Drawing imports** — trace a PDF site plan or import a DWG and build on top of it

---

## Quick start

1. **Download** this repository (green *Code* button → *Download ZIP*, then extract — or `git clone`).
2. **Close Rhino and your AI client**, then double-click **`INSTALL.bat`**.
   It installs the plugin, the Python server and configures every AI client it finds, then prints a **PASS/FAIL verification report** and writes `install-log.txt`. It will not close without telling you what happened.
3. **Open Rhino 8.** First time only: run `PlugInManager`, click *Install*, and choose
   `%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\RhinoAIBridge\RhinoAIBridge.rhp`.
   After that **the bridge starts automatically with Rhino** — no command to type.
4. **Restart your AI client** and ask it: *"ping Rhino"*.

Requirements: Windows, **Rhino 8**, and one MCP-capable AI client. No .NET SDK required.

Access modes are set with the `RHINO_AIBRIDGE_MODE` environment variable — `safe` (blocks code and destructive edits), `standard` (allows deletes/booleans), or `developer` (everything, required for `execute_python3`).

---

## The `rab` geometry library

Every `execute_script` call gets `rab` auto-imported, so the model writes intent instead of boilerplate:

```python
rab.wall((0,0,0), (12000,0,0), height=3000, thickness=200, layer_path="Wall")
rab.slab([(0,0),(30000,0),(30000,18000),(0,18000)], thickness=250, z=3600)
for pt in rab.grid((0,0), 4, 3, 8400, 8400):
    rab.column(pt, h=3600)

# Historical geometry, solved exactly - not approximated
void = rab.arch(3000, 2000, 800, pier=2500, kind="pointed", origin=(4000,0,0))
rab.boolean_diff(wall_id, void)
rab.vault_quadripartite(corners, springing_z=5000, crown_z=9000)
rab.rose_window((6000,0,14000), 2400, spokes=12, foils=12)
```

The two-centred arch solver uses `c = (h²−s²)/2s`, `R = c+s`, and the extrados is **concentric** (same centres) — which is what makes archivolt offsets exact. Vault webs blend to a flat fold at the boss so they land precisely on the crown, where a naive Coons patch overshoots.

**Reusable code:** `write_module(name, source)` saves a library that later scripts import with `rab.use('name')`. The *server* writes the file, so nothing inside Rhino ever holds a file handle open.

---

## Tool surface: 123 tools, three profiles

Set `RHINO_TOOLS=lean|standard|full` in the server environment:

| Profile | Tools | For |
|---|---|---|
| `lean` | ~21 | small/local models (Ollama), minimal context |
| `standard` *(default)* | ~71 | Claude / GPT-class daily driving |
| `full` | 123 | everything, including JSON twins and compatibility aliases |

Anything not exposed in the active profile is **still callable** as a `batch` sub-command, and the live command list is always available from `list_commands` and the `rhino://capabilities` resource.

### Scene & context
| Tool | What it does |
|------|-------------|
| `ping` | Health check — Rhino version, units, `scene_version` etag, and **which script engines are actually available** |
| `query_scene` | Universal query: filter by type, layer, name, bbox; `columnar` format saves 40–60% tokens |
| `list_commands` | Live plugin dispatch table (never drifts from the code) |
| `get_scene_diff` / `get_change_log` / `get_tracker_version` | Incremental sync — catch up on user edits cheaply |
| `get_state` / `set_state` / `clear_state` | Session scratchpad for derived data |

### Geometry creation
| Tool | What it does |
|------|-------------|
| `create_object` | Universal create: wall, slab, column, opening, roof, massing, core + primitives |
| `derive_floors_from_mass` | Section a massing solid at floor heights into real slabs (variable storey heights) |
| `create_core` | Lift/stair/shaft core, with `punch_through` to carve real voids |
| `place_openings_on_facade` | A whole facade of openings in one call |
| `loft_surface` · `sweep1` · `sweep2` · `pipe_curve` · `extrude_curve` · `network_surface` · `sphere_patch` · `revolve_profile` | Surface and solid modelling |
| `boolean_operation` · `trim_with_planes` | Constructive solid geometry |

### Correctness & QA
| Tool | What it does |
|------|-------------|
| `assert_geometry` | **Post-condition contracts** — bbox, envelope, count, count_delta, watertight, supported |
| `find_unsupported` | Objects floating with nothing beneath them |
| `section_preview` | Instant interior inspection at any station; no permanent geometry |
| `validate_objects` | Corruption vs. intentional open shells, scoped by layer / name / `since_version` |
| `detect_clashes` | Real clash detection: RTree broad phase + Brep–Brep narrow phase |
| `select_by_semantic` | *"all south-facing windows on level 3"* |
| `analyze_architecture` · `get_building_systems` · `get_level_summary` · `detect_design_patterns` · `find_unassigned_geometry` | Semantic understanding of the model |
| `measure_object` · `measure_distance` · `check_intersection` · `report_areas` | Measurement and schedules |

### Viewport, drawings & presentation
| Tool | What it does |
|------|-------------|
| `capture_viewport` · `thumbnail` · `get_viewport_image` | Images straight into the conversation |
| `capture_review_set` | Hero, plan, elevations and detail in one multi-image call |
| `compare_before_after` | Capture → edit → capture, with pixel-change metrics |
| `capture_inspection_view` · `set_camera` · `set_view` | Precise camera control (bbox framing or explicit) |
| `create_section` · `cut_section` · `create_elevation` · `create_plan` · `create_all_plans` · `list_sections` | Drawing production |
| `create_display_mode` · `apply_display_mode` · `capture_illustration` | 8 illustration presets: diagram, technical, blueprint, sketch, axonometric, atmospheric, monochrome, cutaway |

### Layers, materials & memory
| Tool | What it does |
|------|-------------|
| `create_layer_tree` · `setup_arch_layers` · `create_layer` · `batch_layer_visibility` | Layer discipline in one call |
| `set_pbr_material` · `set_layer_material` · `search_materials` · `download_material` · `edit_material` | PBR materials + CC0 AmbientCG library with unit-aware UV scaling |
| `set_design_brief` · `add_design_rule` · `tag_object` · `get_provenance` · `search_memory` · `name_group` · `log_session` | **Design memory persisted inside the .3dm** — survives save/reload |

### Import, scripting & safety
| Tool | What it does |
|------|-------------|
| `import_dwg` · `trace_pdf` · `get_pdf_info` · `calibrate_scale` · `export_objects` | Get existing drawings in, get geometry out |
| `execute_script` | IronPython 2 inside Rhino, with `rab` auto-imported |
| `execute_python3` | CPython 3 via RhinoCode (Rhino 8.11+, Developer mode) |
| `write_module` · `list_modules` · `read_module` | Reusable code libraries, no file locking |
| `batch` · `batch_preview` | Atomic multi-op transactions and dry runs |
| `save_checkpoint` · `restore_checkpoint` · `undo` · `cancel_operation` · `get_recovery_log` | Rollback, cancellation and crash recovery |

---

## The `rhino-architect` skill

`skills/rhino-architect/` is an [Agent Skill](https://modelcontextprotocol.io/) that teaches the model *how architects work*: phase ordering (brief → layers → massing → verify → structure → envelope → drawings), millimetre defaults, when to batch vs. step, verification cadence, anti-patterns, and debugged parametric generators for stairs and curtain walls. Point your client at that folder, or install the packaged `.skill`.

---

## Connecting AI providers

`INSTALL.bat` configures whatever it detects. Manual configuration:

### Claude Desktop
`%APPDATA%\Claude\claude_desktop_config.json`
```json
{
  "mcpServers": {
    "rhino-architect": {
      "command": "uv",
      "args": ["--directory", "C:/path/to/rhino-mcp/server", "run", "--frozen", "rhino-architect"],
      "env": { "RHINO_TOOLS": "standard" }
    }
  }
}
```

### OpenAI Codex
`~/.codex/config.toml`
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
```

### Gemini Antigravity
Configured automatically at `%USERPROFILE%\.gemini\antigravity\mcp_config.json`. Restart Antigravity, then ask *"ping Rhino"*.

### Ollama (fully local, free)
Use `RHINO_TOOLS=lean` so a smaller model isn't overwhelmed.
```
ollama pull qwen2.5-coder:7b
cd server && uv run python chat.py --provider ollama --model qwen2.5-coder:7b
```
Best local models: `qwen2.5-coder:32b`, `deepseek-r1:32b`, `llama3.1:70b`.

---

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `RHINO_TOOLS` | `standard` | Tool profile: `lean`, `standard`, `full` |
| `RHINO_AIBRIDGE_MODE` | `safe` | Plugin access mode: `safe`, `standard`, `developer` |
| `RHINO_HOST` / `RHINO_PORT` | `127.0.0.1` / `9544` | Bridge address |
| `RHINO_SAFE_MODE` | unset | `1` blocks destructive tools on the Python side too |
| `RHINO_TIMING` | unset | `1` adds `elapsed_ms` to every response |
| `RHINO_RAB` | `1` | `0` disables the auto-imported `rab` helpers |

---

## Architecture

```
Claude Desktop / ChatGPT / Codex / Antigravity / Ollama
         |  MCP (stdio)
         v
  server/src/rhino_architect/server.py   <- FastMCP Python server (123 tools, profile-gated)
         |  TCP 127.0.0.1:9544
         |  per-user auth token + [1-byte flag][4-byte len][payload]
         v
  plugin/RhinoAIBridge.rhp               <- C# Rhino 8 plugin (.NET 8), 157 commands
         |  UI-thread dispatch + deferred redraw
         v
  Rhino 8 document
```

**Protocol 5** multiplexes by `request_id`, so reads and `ping` answer from the plugin's socket thread while the UI thread runs a long script. Mutating requests are registered for **idempotent replay**: if the connection drops after delivery, re-sending the same `request_id` replays the cached result instead of duplicating geometry. Viewport captures travel as raw **binary frames** rather than inflated base64.

**Atomic batches** run inside a single Rhino undo record; any failure triggers one `Doc.Undo()`.

**Checkpoint economics:** snapshots are skipped when the scene hasn't changed, throttled on large documents, and controllable per call with `checkpoint="off" | "auto" | "force"`.

---

## Building from source

Only needed to modify the C# plugin. Target machines never need the .NET SDK.

```
cd plugin && dotnet build --configuration Release
cd ../server && uv sync --group dev && uv run pytest -q && uv run ruff check src tests
```

CI builds the plugin, lints, and runs the test suite on every push.

---

## Troubleshooting

**"Cannot connect to Rhino"** — open Rhino. The bridge auto-starts; if it doesn't, run `AIBridge` once and re-check `PlugInManager` shows the plugin as loaded.

**The installer closed instantly / I don't know if it worked** — fixed in v4.11.0. The installer now prints a verification report and always waits for ENTER. Every run writes `install-log.txt`; attach that to an issue.

**"Rhino is running" / "MCP server is running" during install** — both hold their files open. Quit Rhino *and* your AI client, then re-run.

**Tool not found in my client** — check your profile. `RHINO_TOOLS=standard` hides ~50 long-tail tools; they remain callable via `batch`, or set `RHINO_TOOLS=full`.

**`execute_python3` unavailable** — call `ping` and read `script_engines.python3.reason`. It states exactly what's missing (Rhino 8.11+, the `rhinocode` CLI, or Developer mode).

**Health check** — `powershell install/rhino-mcp-healthcheck.ps1`, or `cd server && uv run python ../scripts/doctor.py`.

---

## Changelog

### v4.11.0 (current)
- **Installer rewritten** — verification report, `install-log.txt`, detects locked files from a running Rhino or AI client, and never closes without explaining itself
- **`.gitattributes` pins Windows scripts to CRLF** — LF endings made `cmd.exe` mis-seek on `goto`/`call`, which is why the installer appeared to close instantly for ZIP downloads
- **Shipped plugin refreshed** — `dist/plugin/` had drifted a month and a half behind the source
- CI now fails if `dist/` is out of sync with a fresh build

### v4.10.0
- **Intent validation**: `assert_geometry`, `find_unsupported`, `section_preview`
- **`rab` geometry stdlib** auto-imported into every script, including the exact two-centred arch solver, rib vaults, rose tracery and named mouldings
- **Reusable modules**: `write_module` / `list_modules` / `read_module`
- **Tool profiles** `lean` / `standard` / `full`; **checkpoint economics** (zero-delta skip, throttling, per-call policy)
- Zero-touch bridge — the plugin loads with Rhino and starts its server automatically
- `validate_objects` separates corruption from open shells and takes scoping filters
- `ping` reports live script-engine availability
- Fixed: event-loop blocking during PDF tracing, display-mode capture race, camera-dict schema, in-flight registry leak, Safe-mode gaps, binary frames in single-command batches

### v4.8.0
- **Protocol 5**: multiplexed connection, idempotent retries, cooperative cancellation, binary image frames, write-ahead-log crash recovery
- Offscreen ViewCapture rendering, columnar queries (40–60% fewer tokens)

### v4.7.x
- Authenticated localhost bridge, access modes, sections/plans, illustration engine, material intelligence, PDF tracing, DWG import, Codex + Antigravity support

<details>
<summary>Older releases</summary>

- **v4.6** — auth token, 3-tier trust modes, dry-run support, viewport metadata
- **v4.5** — pre-built plugin (no .NET SDK), auto-thumbnails, atomic batch rollback
- **v4.0** — scene snapshot cache (O(1) reads), deferred redraw, architect intelligence layer

</details>

---

## Contributing

Issues and pull requests welcome — especially **field reports**. The most valuable contribution this project has received was a detailed write-up of an 8,000-object modelling session, which produced seven confirmed bug fixes and an entire new category of tooling. If you push this thing hard and it bends, please tell us how.

## License

MIT — see [LICENSE](LICENSE). Free for personal and commercial use.

---

*Built by Tanishq Bhattad — https://github.com/tanishqbhattad*
