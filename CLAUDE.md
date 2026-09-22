# CLAUDE.md — rhino-mcp

Guidance for Claude Code working in this repository.
**Full engineering handoff: `docs/HANDOFF.md`** — read it first in a new session.

## What this is

An MCP server giving an LLM control of **Rhino 8** for architectural modelling. Two processes:

- **`server/`** — Python FastMCP server (128 tools), talks MCP/stdio to the client
- **`plugin/`** — C# .NET 8 RhinoCommon plugin (`.rhp`) running *inside* Rhino
- They speak **Protocol 5.1** over TCP `127.0.0.1:9544` (override: `RHINO_HOST`/`RHINO_PORT`)

**Nothing works without Rhino 8 open and the AIBridge plugin loaded.** Without it every tool
times out. `run_selftest` (~700 ms warm, six stages) is the fastest way to confirm the stack is alive.

## Core design principle

Valid geometry ≠ correct geometry. An LLM building thousands of parametric objects makes
arithmetic and wiring errors that produce perfectly valid breps. So this server validates
**intent**: `assert_geometry`, `assert_dimensions`, `find_unsupported`, `section_preview`.
New features should be able to tell the model *it built the wrong thing* — not just that it parsed.

## Commands

```powershell
# Python
uv --directory server sync --group dev --frozen
uv --directory server run python -m pytest -q          # 120 collected
# If uv can't reinstall because a running MCP server holds rhino-architect.exe open
# (happens after a version bump), add --no-sync - the install is editable anyway.
uv --directory server run ruff check src tests ../evals
uv --directory server run rhino-architect-doctor

# C# plugin  — CLOSE RHINO FIRST (it holds the .rhp open)
cd plugin ; .\build.bat

# Evals (semi-automated: the agent half runs in the MCP client)
uv --directory server run python ../evals/run_evals.py list
uv --directory server run python ../evals/run_evals.py check gothic_cathedral --label opus-5

# Session distiller: mine the WAL for skill/eval material (report needs no Rhino)
uv --directory server run python ../evals/distill_session.py report latest
```

## Hard rules — each cost real debugging time

1. **`rab.py` is IronPython 2 + pure ASCII.** No f-strings, no type hints. A single byte > 127
   makes the import fail *silently* (`rab` becomes `None`). Enforced by test.
2. **`*.bat`/`*.cmd`/`*.ps1` need CRLF.** `cmd.exe` executes by byte offset; LF scrambles
   `goto`/`call` → "installer closes instantly". `.gitattributes` pins this — don't remove it.
3. **Never `Set-Content -Encoding UTF8`** for TOML/JSON — PS 5.1 writes a BOM that breaks parsers.
4. **.NET builds are never bit-reproducible** (fresh MVID per compile). Never hash-compare
   `dist/plugin` against a fresh build; `scripts/check_dist_current.ps1` checks version, command
   coverage, protocol version and advertised features instead.
5. **Use absolute paths with .NET file APIs in PowerShell** — they use the process cwd, not
   `Set-Location`.
6. **Refresh `dist/plugin/` on every release.** It's what `INSTALL.bat` ships to users; it once
   drifted six weeks behind source.
7. **`VERSION` and `server/pyproject.toml:version` must match.**
8. **Don't restart Claude Desktop programmatically** — it kills the session. Ask the user.
9. Fully qualify **`System.Environment`** in the plugin (`Rhino.DocObjects.Environment` collides).
10. **Ruff is real-bugs-only (`E9`,`F`).** Don't add a formatter and reflow 18k lines.
11. **Arm `[ThreadStatic]` state inside `UiDispatcher.Invoke`, not in the calling task.**
    Commands dispatch from a thread-pool task but run on Rhino's UI thread, so
    `OperationRegistry.SetCurrent` / `ProgressReporter.SetCurrent` must sit inside the
    `Invoke` lambda. Getting this wrong fails *silently* — the command succeeds and emits
    nothing. Pinned by `test_progress_sink_is_armed_on_the_ui_thread`.
12. **Progress frames must never resolve a request.** `_reader_loop` handles
    `type == "progress"` *before* future matching, and `_dispatch_progress` swallows callback
    exceptions — a raise there tears down the connection and fails every in-flight command.
    Both are covered by tests in `test_protocol.py`.

## Where things live

| Need | File |
|---|---|
| Tool definitions, profiles, schema flattening | `server/src/rhino_architect/server.py` (3694 L) |
| Transport: multiplex, retry, cancel, WAL | `server/src/rhino_architect/protocol.py` |
| Geometry stdlib injected into scripts (51 fns) | `server/src/rhino_architect/rab.py` |
| C# command dispatch (160 commands) | `plugin/CommandHandler.cs` (5903 L) |
| Layer counts / selector resolution | `plugin/SceneSnapshot.cs` |
| Job model for long operations | `plugin/OperationRegistry.cs` |
| Invariants that must not break | `server/tests/test_field_report_regressions.py` |
| What "working" means, concretely | `evals/tasks/10_gothic_cathedral.json` |
| Turn a past session into eval/skill material | `evals/distill_session.py` |
| Agent Skill taught to users | `skills/rhino-architect/SKILL.md` |

## Tool profiles

`RHINO_TOOLS=lean|standard|full` → **21 / 76 / 128** advertised tools (measured, not estimated).
Pruned tools stay callable via `batch`, so profiles cost discoverability, never capability.
Definitions: `server.py` ~line 3380.

If you add a tool with a Pydantic model parameter, confirm its schema renders real properties —
`$ref`/`$defs` that aren't inlined make clients show `{"params": {}}` and the model then guesses
parameter names.

## Working style for this repo

- **Measure, don't assert.** Frame-coverage %, volume deltas, byte-identical images. Every fix in
  v4.13–v4.15 was verified in a live Rhino before being called done.
- **Regression test in the same commit as the fix**, especially for anything a field report found.
- **Comments explain *why*** — the good ones read like "this once shipped a plugin missing most
  commands". Preserve them; they're why those bugs haven't returned.
- **Update `skills/rhino-architect/SKILL.md` when tool ergonomics change**, or it teaches the old way.
- Commit messages describe user-visible effect, not the diff.

## Security

Never write credentials into files in this repo — **it is public**. Use `gh auth login` +
`gh auth setup-git`, or `git config --global credential.helper manager`. See `docs/HANDOFF.md` §10.
