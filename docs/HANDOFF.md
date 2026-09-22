# rhino-mcp — Engineering Handoff

**Written:** 2026-09-11 · **Updated:** 2026-09-22 for v4.16.0
**Repo:** `C:\Users\Tanishq\Documents\rhino-mcp` → https://github.com/tanishqbhattad/rhino-mcp
**State:** `v4.16.0` is on branch `release/v4.16.0`, pushed to `origin`. **No PR yet, so no
CI has run; not merged, not tagged.** `main` is still at v4.15.0 (`1ba368e`). The last git tag
is `v4.8.0` — nothing after it was ever tagged.

This document is written for an engineer (or Claude Code) picking the project up cold.
Read §1–§3 to get oriented, §4 before you touch anything, §5 to run it, §9 for what's open.

---

## 1. What this is

An MCP (Model Context Protocol) server that gives an LLM — Claude, GPT, Codex, Gemini, or a
local Ollama model — real control of **Rhino 8 / Rhinoceros 3D** for architectural modelling.
You describe a building in plain language; the model builds it: massing, floor plates, cores,
facades, vaults, plans, sections, area schedules — and checks its own work as it goes.

It is **two processes**:

```
┌────────────────────┐     MCP / stdio      ┌──────────────────────────┐
│  AI client         │ ───────────────────► │  Python MCP server       │
│  (Claude Desktop,  │                      │  server/src/             │
│   Claude Code,     │ ◄─────────────────── │    rhino_architect/      │
│   Codex, Ollama)   │                      └───────────┬──────────────┘
└────────────────────┘                                  │
                                            Protocol 5.1 over TCP
                                            127.0.0.1:9544
                                            (length-prefixed frames)
                                                        │
                                            ┌───────────▼──────────────┐
                                            │  C# Rhino plugin         │
                                            │  plugin/*.cs → .rhp      │
                                            │  .NET 8 + RhinoCommon    │
                                            │  runs INSIDE Rhino 8     │
                                            └──────────────────────────┘
```

The important consequence: **nothing works without Rhino 8 open and the AIBridge plugin
loaded.** The Python half will start fine and every tool will time out.

### The project's actual thesis

Valid geometry is not correct geometry. An LLM generating thousands of parametric objects makes
*arithmetic and wiring* errors — a doubled base height, swapped arguments, a boolean cutter added
instead of subtracted — and every one of those produces a perfectly closed, valid brep that a
conventional validity check happily passes.

So this server validates **intent**, not just validity: `assert_geometry`, `assert_dimensions`,
`find_unsupported`, `section_preview`. That distinction is the reason the project exists, and it
should drive how new features are designed. If a feature can't tell the model *it built the wrong
thing*, it's probably the less valuable version of itself.

---

## 2. Repo map

```
rhino-mcp/
├── VERSION                      4.16.0  — single source of truth, bump this
├── README.md                    user-facing + SEO/discoverability (keep current!)
├── INSTALL.bat                  end-user installer — logs to install-log.txt, never closes silently
├── INSTALL_GUIDE.txt
├── .gitattributes               pins *.bat/*.cmd/*.ps1 to eol=crlf  ← load-bearing, see §4
│
├── server/                      the Python MCP half
│   ├── pyproject.toml           version mirrors VERSION; ruff = E9,F only; pytest asyncio_mode=auto
│   ├── uv.lock                  committed; CI runs --frozen
│   ├── chat.py                  standalone OpenAI-compatible chat client (Ollama etc.)
│   ├── src/rhino_architect/
│   │   ├── server.py            3802 L  — 128 FastMCP tools, profiles, schema flattening
│   │   ├── protocol.py           663 L  — Protocol 5.1 transport: multiplex, retry, cancel, progress
│   │   ├── rab.py               1107 L  — 51-fn geometry stdlib injected into execute_script
│   │   ├── pdf_tracer.py         488 L
│   │   ├── material_downloader.py 374 L
│   │   ├── doctor.py             190 L  — `uv run rhino-architect-doctor`
│   │   └── validation.py          12 L
│   └── tests/                   140 collected tests, 7 files — see §6
│
├── plugin/                      the C# half (12,104 L of .cs total)
│   ├── CommandHandler.cs        5903 L  — THE dispatch table. All 160 commands land here.
│   ├── SectionManager.cs        1224 L
│   ├── AIBridgeServer.cs         872 L  — TCP listener, framing, request_id multiplexing
│   ├── MaterialManager.cs        614 L
│   ├── DisplayModeManager.cs      576 L
│   ├── TracingManager.cs          532 L
│   ├── SceneSnapshot.cs           516 L  — CountsByLayerIndex(), ByLayerName(descendants)
│   ├── SemanticClassifier.cs      315 L
│   ├── OperationRegistry.cs       313 L  — job model, cancellation token, ProgressReporter
│   ├── DesignMemory.cs            242 L
│   ├── AIBridgeLogger.cs / ChangeTracker.cs / BatchPlanner.cs / UiDispatcher.cs /
│   │   RedrawScope.cs / SceneSnapshotRegistry.cs / AIBridgePlugin.cs
│   ├── RhinoAIBridge.csproj
│   └── build.bat                builds Release + copies into Rhino's plug-in dir
│
├── dist/plugin/                 PRE-BUILT .rhp that INSTALL.bat ships. MUST stay in sync — see §4
├── skills/rhino-architect/      Agent Skill: SKILL.md + 4 references + 2 generator scripts
├── evals/                       run_evals.py + distill_session.py + 10 task JSONs + results.jsonl
├── scripts/                     check_dist_current.ps1, doctor.py, patch_*_config.py
├── install/                     install-rhino-mcp.ps1, healthcheck, FIX-RHINO-MCP.bat, codex example
├── docs/                        audits, release notes, COMPATIBILITY.json, this file
└── .github/workflows/ci.yml     plugin build + dist staleness + py_compile + ruff + pytest
```

---

## 3. Architecture you need to hold in your head

### Protocol 5 (`protocol.py` ↔ `AIBridgeServer.cs`)

Length-prefixed frames over TCP, **multiplexed by `request_id`**. This is what makes the
project usable for real work: a 3-minute `execute_script` does not block `ping`, `query_scene`,
or `cancel_operation`. Concretely:

- **Multiplexing** — responses are routed by `request_id`, not by arrival order.
- **Idempotent replay** — a reconnect mid-write does not duplicate geometry.
- **Cooperative cancellation** — `cancel_operation(request_id)`; the C# side polls a flag.
- **Binary image frames** — flag `0x02`; viewport captures skip base64-in-JSON.
- **WAL crash recovery** — write-ahead log so a Rhino crash mid-batch is recoverable.
- **Auto-cancel of abandoned reads** — if a read times out on the Python side and the call is
  `retry_safe_always`, the server fires `cancel(rid)` so the plugin stops doing dead work.
- **Progress notifications (5.1, feature `progress`)** — a running command emits out-of-band
  frames `{"type":"progress","request_id":...,"percent":...,"message":...}` *before* its single
  real response, surfaced to the MCP client as progress notifications. Without this a 3-minute
  script is indistinguishable from a hang. Three rules make it safe:
  1. `_reader_loop` intercepts progress frames **before any future matching** — resolving the
     future would complete the command with a progress payload, and consuming a FIFO slot
     would desync legacy in-order matching for every later response.
  2. `_dispatch_progress` swallows callback exceptions. A raise inside the reader task calls
     `_abort_connection` and fails *every* in-flight request — an absurd price for a cosmetic
     status update.
  3. The client only sets `want_progress` once the plugin has advertised the feature, so an
     older plugin is never sent a parameter it must ignore.
  Plugin side: each command gets one `ProgressChannel`, shared by two emitters:
  - **Handlers** report real progress through `ProgressReporter` (thread-static pointer, 250 ms
    throttle) from the checkpoints where they already poll `CancelRequested`. Wired today: the
    `batch` loop (`op 7/40: create_object`).
  - **A heartbeat timer** (every 2 s, first tick at 2 s) reports elapsed time against the
    timeout budget (`running 42s of 180s budget`, capped at 99%) for commands that can't know
    their own percent — an opaque `execute_script`, `report_areas`, `detect_clashes`, …
  The channel serialises frames, never lets percent decrease (MCP requires increasing
  progress), silences the heartbeat for good once a handler reports, and is closed in
  `ExecuteOnUi`'s `finally` so no frame ever trails the response — including a timed-out
  command still running on the UI thread.
- **Timeouts surface the `request_id`** and tell the model to call `get_operation_result` —
  a long job that outlives its timeout is *retrievable*, not lost. This was a field-report fix;
  don't regress it.

`RHINO_HOST` / `RHINO_PORT` override the default `127.0.0.1:9544`.

### Tool profiles (`RHINO_TOOLS=lean|standard|full`)

128 registered tools is too many for most models' tool-selection to stay sharp, so profiles
control what gets **advertised**:

| Profile | Tools | For |
|---|---|---|
| `lean` | 21 | Small/local models (Ollama), minimal context |
| `standard` | 76 | **Default.** Claude/GPT-class daily driver |
| `full` | 128 | Everything, incl. `*_json` twins + McNeel-compat aliases |

(Counts measured by loading each profile, not estimated — an earlier draft said ~65 for
`standard`. `RHINO_TOOLS=<profile> uv --directory server run python -c "import
rhino_architect.server as S; print(len(S.mcp._tool_manager._tools))"` re-checks them.)

Critically: **pruned tools remain callable via `batch`.** Profiles cost nothing in capability,
only in discoverability. `_LEAN_TOOLS` / `_STANDARD_TOOLS` are frozensets at
`server.py:~3380`; `_apply_tool_profile()` does the pruning at startup.

### Schema flattening (`_inline_schema_refs` / `_flatten_tool_schemas`)

Pydantic emits `$ref`/`$defs` for model-typed params. Clients that don't dereference render the
tool as `{"params": {}}` — the model then can't see a single parameter name and guesses. So at
startup we **recursively inline** `$ref`s for all 43 model-based tools. If you add a tool with a
Pydantic model param, verify its schema renders with real properties.

### `rab` — the geometry stdlib

`server/src/rhino_architect/rab.py`, 51 functions, auto-injected into every `execute_script`
call and deployed to `%LOCALAPPDATA%\AIBridge\rab.py`. It exists because the model kept
rediscovering the same RhinoCommon traps. Highlights:

`wall() slab() column() grid() extrude() arch() arch_geometry() vault_quadripartite()
rose_window() sweep_profile() periodic_curve() orient() is_inverted() cap() wall_profile()
plane_from_wall() annulus_wall() buttress_pier() spire() gable_roof() mirror_x/y() array_x()
radial() help() doc() units() m() use() assign()`

Two worth knowing:

- **`wall_profile()`** — the "A7" fix. `Extrusion.AddInnerProfile` silently refuses inner loops
  for walls in the **YZ plane** (a handedness issue). So `wall_profile` tries the cheap Extrusion
  path, then falls back to `CreatePlanarBreps` + `CreateExtrusion`. Before this, window openings
  in N–S walls failed 9 times out of 9. Both halves must stay — there's a test asserting it.
- **`_all_objects()`** — uses `ObjectEnumeratorSettings` with `HiddenObjects = true`. The bare
  `for o in sc.doc.Objects:` enumerator **skips hidden objects** and under-reported a real scene
  by 21 objects. A test forbids reintroducing it.

> **`rab` runs under IronPython 2.** See §4 — the constraints are not negotiable and are
> enforced by tests.

---

## 4. Environment rules — read before touching anything

These are all *hard-won*. Each one cost real debugging time.

### 4.1 `rab.py` must be pure ASCII, IronPython-2-safe

- **No f-strings.** No type hints. `reload` is a builtin, not an import.
- **Pure ASCII source.** A box-drawing character in a comment once made the IronPython import
  fail silently — `rab` came back as `None` with no error anywhere. The bootstrap now prints the
  import error, and `test_rab_is_ironpython2_safe` fails the build on any byte > 127.

### 4.2 Never write config/TOML/JSON with PowerShell `Set-Content -Encoding UTF8`

PowerShell 5.1 writes a **BOM**, which breaks TOML and JSON parsers. Use `-Encoding
ASCII`/`utf8NoBOM`, or write via Python.

### 4.3 `*.bat` / `*.cmd` / `*.ps1` must have CRLF endings

`cmd.exe` executes batch files **by byte offset**. LF endings scramble `goto`/`call` and produce
exactly the symptom "INSTALL.bat opens and closes instantly". `.gitattributes` pins these to
`eol=crlf` — **do not remove it.**

### 4.4 .NET builds are never bit-reproducible

.NET embeds a fresh **MVID** in every compile, so two builds of identical source never hash the
same. Any CI check comparing `dist/plugin` hashes against a fresh build is unpassable — this was
tried and failed. `scripts/check_dist_current.ps1` instead checks **assembly version + full command coverage +
protocol version and advertised features**, and it:
- scopes its regex to the **dispatch block** (matching all JObject keys gave 374 vs 156),
- decodes at **byte offsets 0 and 1**, because UTF-16 alignment produced 2 false "missing"
  (skip this and you get false negatives — it bit me again while hand-checking a binary), and
- compares `PROTOCOL_VERSION` and every name in `FEATURES` against the shipped binary.

> That last check was added after the first two passed while `dist/plugin` lacked protocol 5.1
> **entirely**. A protocol bump that adds no new command and no new assembly version is
> invisible to a version+command check, and a feature the shipped plugin never advertises
> never negotiates — so the client degrades silently and the capability simply does not exist
> for anyone who installed from GitHub. Same class as the six-week-stale plugin, on an axis
> nothing was measuring.

### 4.5 .NET methods use the *process* cwd, not PowerShell's `Set-Location`

`[IO.File]::ReadAllBytes("relative\path")` returns 0 bytes or throws after `Set-Location`.
**Always use absolute paths** in PowerShell when calling .NET file APIs.

### 4.6 `$PSScriptRoot` is empty in a `param()` default on PS 5.1

Resolve it in the function body instead.

### 4.7 `Environment` is ambiguous in the plugin

`Rhino.DocObjects.Environment` collides. Fully qualify `System.Environment`.

### 4.8 Rhino holds the .rhp open

Plugin copy fails while Rhino is running. **Close Rhino before a plugin rebuild.** There is no
retry loop. Until v4.16.0, `build.bat` deleted the dependency DLLs *first*, then let the copy
fail silently, and still printed `BUILD SUCCESSFUL`. It now checks the `.rhp` is not held open
before deleting anything, fails loudly with "close Rhino", and exits 1. The fresh build is still
left in `plugin\bin\Release\net8.0`.

Compiling does **not** need Rhino closed: `dotnet build plugin/RhinoAIBridge.csproj -c Release`
works any time. Only installing into the plug-in folder does.

### 4.9 `uv` must use the managed standalone Python

Don't point it at a system/Store Python.

### 4.10 Do NOT restart Claude Desktop programmatically

It kills the session you're working in. Ask the user to restart it. (Less relevant in Claude
Code, but the plugin still needs a Rhino restart to reload a rebuilt .rhp.)

### 4.11b `[ThreadStatic]` state must be armed on the UI thread, inside `UiDispatcher.Invoke`

`OperationRegistry` (cancellation) and `ProgressReporter` (progress) both store their ambient
state in `[ThreadStatic]` fields. Commands are dispatched from a **thread-pool task**
(`Task.Run` in the read loop) but *execute* on Rhino's **UI thread** via
`UiDispatcher.Invoke`. Arming the state in the calling task sets it on the wrong thread, and
every handler then reads `null`.

This fails **completely silently**: the command succeeds, returns a normal result, and emits
nothing. No exception, no log line. It cost a full deploy/restart cycle to find, and only a
live end-to-end probe could see it — 93 unit tests, a clean compile and a mutation test all
passed while it was broken, because the failure lives across the thread boundary *between the
two processes*.

Arm both inside the `Invoke` lambda, adjacent to each other
(`AIBridgeServer.cs`, look for `OperationRegistry.SetCurrent(token)`).
Pinned by `test_progress_sink_is_armed_on_the_ui_thread`.

### 4.11c `rhinocode script` is fire-and-forget

Measured on Rhino 8.18: the CLI hands the file to Rhino's script server and returns after
~1 s, whether or not the script has finished. It relays **no** `print()` output and **no**
exit status — a script that raised returned exit code 0 with empty stderr, so
`execute_python3` used to report `status: ok` for crashed scripts, and its `timeout_seconds`
governed nothing.

`execute_python3` therefore runs user code inside `_PY3_HARNESS` (`server.py`), which captures
stdout/stderr/traceback and writes `result.json` atomically (tmp + `os.replace`); the server
polls for it until the deadline. The harness restores `sys.stdout` in `finally` — the script
server is long-lived, and a leaked redirect would swallow output from every later script.
Covered offline by `test_python3_harness.py` (the harness is plain CPython, so the tests just
run it with the test interpreter).

### 4.11 Selectors accept a bare string

`ResolveSelector` handles both `"by_layer:FIT"` and `["by_layer:FIT"]`. A bare string used to
fail to deserialize as `List<string>`. Keep both paths working.

---

## 5. How to run, build, test, ship

All commands assume `cd C:\Users\Tanishq\Documents\rhino-mcp`.

### Python server

```powershell
uv --directory server sync --group dev --frozen     # install locked deps
uv --directory server run rhino-architect           # run the MCP server (stdio)
uv --directory server run rhino-architect-doctor    # diagnose a broken install
uv --directory server run python -m pytest -q       # tests
uv --directory server run ruff check src tests ../evals
```

### C# plugin

```powershell
# 1. CLOSE RHINO (it holds RhinoAIBridge.rhp open)
cd plugin
.\build.bat            # restore + Release build + copy into Rhino's plug-in dir
```

Target: `%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\RhinoAIBridge`

First time ever: Rhino 8 → `PlugInManager` → Install → browse to the `.rhp`.
After that it auto-loads. `AIBridge` at the Rhino command line restarts the TCP server.
Logs: `%APPDATA%\AIBridge\logs\`

> **`build.bat` runs from its own folder** (`pushd "%~dp0"`), so it works from any cwd. Before
> v4.16.0 its `dotnet` calls were cwd-relative and silently built in the wrong place.
>
> **In Claude Code, call it by full path:** `cmd /c 'call "<repo>\plugin\build.bat" < NUL'`.
> The session sets `NoDefaultCurrentDirectoryInExePath=1`, so a bare `build.bat` is "not
> recognized" even from `plugin/`. `< NUL` gets past the final `pause`.

### Shipping a release

1. Bump `VERSION` **and** `server/pyproject.toml:version` (they must match).
2. Rebuild the plugin.
3. **Refresh `dist/plugin/`** — this is what `INSTALL.bat` copies onto users' machines. It once
   drifted six weeks behind source and every install shipped a plugin missing most commands.
   CI guards it now, but CI can only catch it after you've pushed.
4. `uv run python -m pytest -q` + `ruff check`.
5. Update `README.md` if capabilities changed (tool counts, feature list).
6. Commit, push, confirm CI green.

### End-user install path

`INSTALL.bat` — tees everything to `install-log.txt`, prints a PASS/FAIL report, detects a
running Rhino and a running MCP server, and holds the window open with `set /p`.

---

## 6. Tests and evals

### Unit tests — 140 collected, 7 files

| File | Collected | Covers |
|---|---|---|
| `test_rhino_ai_bridge.py` | 53 | the original broad suite |
| `test_protocol.py` | 23 | Protocol 5.1: framing, multiplex, retry, cancel, progress + its MCP bridge |
| `test_field_report_regressions.py` | 17 | field-report fixes A1/A2/A6/A7, schema/eval contracts, progress wiring |
| `test_python3_harness.py` | 14 | `execute_python3` output/exception capture (runs the harness, no Rhino) |
| `test_distill_session.py` | 15 | WAL parsing, pairing, truncation, failure/retry mining, draft tolerances |
| `test_elicitation.py` | 13 | delete/restore confirmation over a real in-memory MCP client |
| `test_rab.py` | 5 | `rab` helpers |

> Counted as pytest *collects* them, not as `def test_` lines. They differ:
> `test_field_report_regressions.py` defines 9 functions, one parametrized over 4 tools,
> giving 12 cases. Quote the collected number — it's the one CI gates on.

`test_field_report_regressions.py` is the interesting one: it asserts against **C# source text**
(the fixes need a live Rhino, so the *contract* is what's unit-testable). It will fail if someone
reintroduces name-keyed layer counts, drops the `by_layer_exact:` escape hatch, removes
`wall_profile`'s planar fallback, or puts a non-ASCII byte in `rab.py`.

### Evals — 10 tasks, semi-automated by design

The agent half runs in your MCP client; the scoring is exact (no screenshots, no vibes).

```powershell
uv --directory server run python ../evals/run_evals.py list
uv --directory server run python ../evals/run_evals.py prompt gothic_cathedral   # paste into the model
#    ... let the model build it in Rhino ...
uv --directory server run python ../evals/run_evals.py check gothic_cathedral --label opus-5
uv --directory server run python ../evals/run_evals.py reset
```

Results append to `evals/results.jsonl` with a timestamp and `--label`, so runs are comparable
across models and across tool/skill changes. **Use `--label`** — unlabelled runs are nearly
useless later.

Assertion types: `dimension`, `watertight`, `layer_count`, `level_count`, `clash_free`,
`clashes_at_least`, `gfa_total`, `scene_bbox_height`, `no_default_layer_geometry`, `section_count`.

**Task 10 (`gothic_cathedral`) is the flagship regression.** It deliberately packs every trap
from the v4.14 field report: nested layers with a duplicate leaf name (`Piers` appears under two
parents), a YZ-plane wall with pointed-arch openings (the A7 trap), `annulus_wall` for the apse,
watertightness on the walls, and three `assert_dimensions` checks. Last run: **9/9**, all three
dimensions at **0.0 deviation**.

`run_selftest` is the fast live smoke test — six stages (ping, build, assertions, dimensions,
capture, cleanup). Measured ~570 ms when written; it runs ~700 ms warm on this machine
(~1.4 s on the first call, which is IronPython JIT, not a regression).

### Session distiller — `evals/distill_session.py`

Every mutating command is already journaled to the WAL (`%APPDATA%\AIBridge\wal\*.jsonl`) as a
begin/end pair, so past sessions are a structured record nobody was reading. The distiller
turns them into the two things this project compounds on:

```powershell
uv --directory server run python ../evals/distill_session.py list
uv --directory server run python ../evals/distill_session.py report latest
uv --directory server run python ../evals/distill_session.py draft latest --id my_task \
    --out ../evals/tasks/11_my_task.json
```

- **`report`** — command mix, failures, retries-after-failure, slowest calls, scene-version
  progression. No Rhino needed. On the real 84-op Notre-Dame session it shows 376 s of command
  time, a 46 s `execute_script`, and **30 `set_camera` calls** — which is precisely the camera
  thrash the v4.13 `fit` work was built to remove. That is what this is for.
- **`draft`** — emits a runnable eval task whose assertions are *measured from the live scene*
  the session produced, so a good session becomes a regression test instead of being lost when
  Rhino closes. Needs Rhino up with that model still open. Verified live: a drafted task scores
  17/17 on its source scene, and catches a deleted pier (count) and a roof dropped 8 m (`top_z`).
  Drafting `height` alone missed that second one entirely, which is why every layer gets both.
  Tolerances are 1% of the measured scale, so they work in mm and m documents alike.

> **Known limit, stated in the tool's own output:** the plugin truncates journaled params at
> ~300 chars, and for `execute_script` that budget is entirely consumed by the `rab` bootstrap
> preamble — so the script body never reaches the WAL and rab-helper mining mostly cannot work.
> `report` says this explicitly rather than printing an empty section, which would read as
> "this session used no helpers". Widening the WAL params budget, or journaling the
> post-bootstrap slice, would unlock it.

---

## 7. The Agent Skill

`skills/rhino-architect/` — `SKILL.md` plus 4 reference docs (`massing-and-structure`,
`facades-and-openings`, `drawings-and-presentation`, `recovery-and-qa`) and 2 generator scripts
(`curtain_wall.py`, `stair_dogleg.py`).

`SKILL.md` has a **"long builds"** section — phase discipline, one heavy call per turn, verify as
you go. That pattern is what made the 8,000-object cathedral session work. If you change tool
ergonomics, the skill needs updating in the same commit or it will teach the old way.

---

## 8. Key history — three field reports

The last several releases were driven by **field reports from real modelling sessions**, which is
why the fixes are unusually specific. Context for anything that looks oddly narrow:

1. **Notre-Dame reconstruction, ~8,000 objects, 1:1** — zero invalid breps. Produced the
   intent-validation work and the checkpoint-economics fixes.
2. **Parametric tower + a 50-option school study** — produced `report_areas` fast paths, layer
   tree fixes, the job model.
3. **A code-grounded report with `file:line` references** — became the eight-phase plan delivered
   in v4.14–v4.15 (A1–A8 + B/C/D tranches).

Bugs found *while testing my own fixes* (a useful pattern to keep): the `fit` camera drifted
between identical calls because margin was folded into an angle read from the frustum I then
rewrote (fixed by keeping angles and expressing margin as distance; verified byte-identical ×3);
eval task 10 was internally self-inconsistent (rows 14500 apart in Y *and* a wall along Y — can't
both hold for a nave); `check_dist_current.ps1` produced false positives twice.

**Working principle worth inheriting: measure, don't assert.** Frame-coverage %, volume deltas,
byte-identical images, per-corner frustum math. Every claim in the last three releases was
verified live in Rhino before being called done.

---

## 9. What's open

### v4.16.0 is pushed on a branch, not shipped.
`release/v4.16.0` holds the whole release (see `README.md` changelog). To ship it: open a PR
into `main` (CI only runs on PRs and on `main`), confirm CI is green, merge, and tag
`v4.16.0`. Nothing else is mid-flight.

### Two items that need Tan (not code)

1. 🔴 **Revoke the exposed GitHub PAT** — see §10.
2. 🟡 **Update the GitHub repo description** — still says "115 AI tools"; it's **128** now.
   This field lives only in GitHub's web UI (Settings → General, or the ✏️ on the repo home).
   Suggested text:

   > MCP server for Rhino 8 — AI-assisted architectural modelling with Claude, GPT, Codex,
   > Gemini or Ollama. 128 tools, multiplexed transport, intent validation, and a geometry
   > stdlib for walls, vaults and tracery.

   Suggested topics to add: `agent-skills`, `parametric-design`, `rhino3d`, `mcp`,
   `architecture`, `computational-design`.

### Known gaps

- **Only `batch` reports *real* percent progress.** Everything else long gets the elapsed-time
  heartbeat. `place_openings_on_facade` and `derive_floors_from_mass` walk lists internally and
  could report `wall i of n` from their loops — worth doing if they turn out to be slow.
- **Which real MCP clients show elicitation prompts is unverified.** The confirmations are
  proven against the SDK's own client and a live Rhino, but not yet inside Claude Desktop or
  Claude Code. A client that doesn't advertise the capability simply never gets asked, which
  is the pre-v4.16 behaviour.
- **The WAL truncates params at ~300 chars**, so `execute_script` bodies never reach it and the
  distiller cannot mine `rab` usage. Widening the budget is a plugin change.
- **`run_selftest` measures ~700 ms warm** here vs ~570 ms when written. Not isolated.

### Roadmap

v4.16.0 delivered the MCP-spec progress work and the session distiller. Still open:

| Option | Why | Size |
|---|---|---|
| **Grasshopper bridge** | The single biggest capability gap. Parametric definitions are where Rhino power users live, and no MCP does this well. | Large — plan first |
| **Structured output** | The last MCP-spec item: typed tool results clients can validate. | Medium |

---

## 10. 🔴 Security: the exposed GitHub PAT

**You asked me to put the token in this document. I'm deliberately not doing that, for two
reasons:**

1. **I don't have it.** The token was pasted in an earlier chat, before this session's context
   window. It isn't in anything I can read now, so I can't reproduce it even if I wanted to.
2. **It must not be written down anyway.** This is a **public** repo. A PAT in
   `docs/HANDOFF.md` is a PAT in `git log` forever, readable by anyone, and scraped by bots
   within minutes of the push. That's the exact mechanism this section exists to prevent. A
   token that needs revoking does not need recording — and a token you keep using shouldn't live
   in a file either.

**What I verified (so you know the blast radius):**

```
working tree:   CLEAN  — no token-shaped strings (gh[pousr]_* / github_pat_*)
git history:    CLEAN  — 0 matches across `git log -p --all`
.git/config:    CLEAN  — remote is plain https, no embedded credentials
credential.helper: not set locally
```

So the leak is **chat-only**, not repository. Good news, but the token is still live until you
kill it.

### Revoke it — do this first

1. https://github.com/settings/tokens  (GitHub → Settings → Developer settings → Personal
   access tokens)
2. Find the token → **Delete** / **Revoke**. If you can't tell which one it is, revoke all of
   them; re-minting is cheap and anything still needed will simply prompt again.
3. Check https://github.com/settings/security-log for anything you don't recognise.

### Then authenticate properly — no token in any file

**Best option — GitHub CLI** (handles refresh, stores in the OS credential vault):

```powershell
winget install --id GitHub.cli
gh auth login          # → GitHub.com → HTTPS → Login with a web browser
gh auth setup-git      # makes git use gh's credentials
```

**Alternative — Git Credential Manager** (ships with Git for Windows):

```powershell
git config --global credential.helper manager
# next push opens a browser prompt once, then stores it in Windows Credential Manager
```

Either way the credential lives in the OS vault, never in the repo, never in a doc, and never in
a chat. If you ever *do* need a fresh PAT for automation, put it in an environment variable or
Windows Credential Manager — and give it the narrowest scope that works (`repo` only; or better,
a fine-grained token scoped to this single repository).

**One thing I can't do for you:** I can't sign into GitHub, enter credentials, or revoke the
token on your behalf. Those two clicks are yours.

---

## 11. Conventions worth keeping

- **`VERSION` is the single source of truth.** `pyproject.toml` mirrors it. Keep them equal.
- **Ruff is configured to real bugs only** (`E9`, `F`). Style stays the author's call — don't
  turn on a formatter and reflow 18k lines.
- **Comments explain *why*, especially for traps.** The good ones in this repo read like
  "this once shipped a plugin missing most commands" — that's the standard. Preserve them; they
  are the reason the same bug hasn't returned.
- **Verify live before claiming done.** Every fix in v4.13–v4.15 was confirmed in a running
  Rhino, not just in tests. Tests catch regressions; they don't prove a building looks right.
- **When you fix something a field report found, add the regression test in the same commit.**
  That's why `test_field_report_regressions.py` exists.
- **Commit messages here describe the user-visible effect**, not the diff
  (e.g. *"fix layer counts + by_layer descendants, report_areas fast path, recover abandoned
  work"*). Keep that.

---

## 12. Fast orientation for a new session

If you're Claude Code starting cold, in order:

1. `docs/HANDOFF.md` (this file) and `CLAUDE.md`
2. `README.md` — what users are promised
3. `server/src/rhino_architect/server.py` §TOOL PROFILES (~line 3370) — the tool surface shape
4. `server/src/rhino_architect/protocol.py` — the transport contract
5. `plugin/CommandHandler.cs` — the dispatch table; grep for the command name you care about
6. `server/tests/test_field_report_regressions.py` — the invariants that must not break
7. `evals/tasks/10_gothic_cathedral.json` — what "working" means, concretely

Then: **open Rhino 8**, confirm the AIBridge plugin loaded, and run `run_selftest`. If that's
green in ~570 ms, the whole stack is alive and you can trust everything else you measure.
