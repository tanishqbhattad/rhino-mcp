# Rhino MCP Deep Audit - 2026-06-10

Scope: full repository scan of the Rhino plugin, Python MCP server, installer scripts, docs, release notes, and support tools.

## Current Resolution Status

This audit started as a bug list and is preserved for traceability. As of the clean-slate pass on 2026-06-10, the actionable items below have been fixed or mitigated in the working tree:

- Fixed: findings 1, 2, 3, 5, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, and 20.
- Mitigated: finding 4 now rejects new UI work during shutdown and returns `COMMAND_TIMEOUT` with `may_still_be_running` when a command times out. True mid-operation cancellation still requires future per-tool cancellation tokens.
- Mitigated: finding 6 now restores the user's viewport after section/plan captures and reports `view_restored`. A fully detached offscreen section renderer remains a future quality upgrade.
- Deferred by design: finding 8 would require wrapping arbitrary user scripts in transactional geometry checkpoints. Current behavior is documented as a higher-risk Developer-mode escape hatch.

## Verification Performed

- `dotnet build plugin\RhinoAIBridge.csproj -c Release`: passed, 0 warnings, 0 errors.
- `uv --directory server run --frozen python -m py_compile ...`: passed for the Python MCP package and `chat.py`.
- `uv --directory server run --frozen python -m pytest -q`: initially exposed a missing `pytest` dependency; fixed by adding the locked dev dependency. Current result: 45 passed.
- Manual review of high-risk areas: auth handshake, mode gating, UI-thread dispatch, batch rollback, capture/vision tools, CPython3 bridge, section/clipping tools, installer patchers, docs, healthcheck, and standalone chat client.

## High Priority Findings

### 1. Test suite is advertised but cannot run from a clean frozen install

`server/tests/test_rhino_ai_bridge.py` imports `pytest`, and README advertises a pytest suite, but `server/pyproject.toml` has no dev dependency group or optional dependency containing `pytest`.

Impact: contributors and release automation cannot run the test suite with the documented command.

Fix: add a dev dependency group, lock it, and document `uv run --group dev pytest`.

### 2. `set_display_mode` silently succeeds when the mode is missing

`plugin/CommandHandler.cs` finds the mode and only applies it if non-null, but always returns `status: ok`.

Impact: captures may report success while Rhino stayed in the previous display mode. This matches the field report where display mode sometimes appeared to revert or not apply.

Fix: return a typed error when `DisplayModeDescription.FindByName(...)` returns null, and include available close matches.

### 3. `redo` reports requested count, not actual redo count

`DoUndo` correctly counts successful `Doc.Undo()` calls, but `DoRedo` still loops and returns the requested count without checking whether redo actually happened.

Impact: agents can believe state moved forward when no redo stack existed.

Fix: mirror the undo implementation and report `redone` plus `requested`.

### 4. UI dispatcher timeouts do not cancel the Rhino-side work

`UiDispatcher.Invoke` times out the waiting TCP worker, but the queued UI-thread action still runs to completion if it later gets the UI thread.

Impact: MCP client can time out while Rhino continues mutating the model. This is the timeout desync observed during large hide/edit loops.

Fix: introduce cooperative command cancellation / operation IDs, or make long operations chunked and cancellable. At minimum, return a `MAY_STILL_BE_RUNNING` warning on timeout.

### 5. Shutdown still has a plausible `Server Busy` path

`AIBridgePlugin.OnClosing` installs an OLE message filter on the closing path, but worker threads may already be blocked in `RhinoApp.InvokeOnUiThread`. The comment says threads get up to 2 seconds to wind down, but there is no actual join/drain.

Impact: the Windows "Server Busy" popup can still appear during Rhino close, especially if an MCP request is mid-flight.

Fix: add a shutdown gate in `UiDispatcher`, reject new invokes once closing starts, track active client tasks, and drain briefly before uninstalling the message filter.

### 6. Section/clipping capture is coupled to the active viewport

`SectionManager` creates clipping planes bound to the active viewport, aligns that viewport, redraws, then captures the active view.

Impact: section/plan captures can fail silently or affect the user's current viewport. The field report's "clipping plane had zero effect" fits this design.

Fix: add a dedicated temporary/offscreen section capture path, or create/validate a named view and verify the clipping plane is bound to the viewport being captured.

### 7. `capture_viewport` disables redraw immediately before capture

The capture path applies view/display overrides, then sets `Doc.Views.RedrawEnabled = false` before `CaptureToBitmap`.

Impact: on some display modes/scenes this can capture stale view state. It also makes display-mode restore issues harder to diagnose.

Fix: force a redraw after applying overrides, capture without suppressing redraw, or use a safer offscreen/detached capture path for inspection views.

### 8. Boolean failure tracking in `execute_script` is not automatic

The preamble defines `_rab_check_bool`, and response parsing looks for `[BOOLEAN_FAIL]`, but normal user scripts calling RhinoCommon boolean methods directly are not wrapped.

Impact: docs/comments imply silent boolean failures are surfaced automatically, but only scripts that manually call `_rab_check_bool(...)` get that warning.

Fix: either document it as an explicit helper or provide higher-level boolean helpers that call the wrapper.

## Medium Priority Findings

### 9. Several Pydantic models use mutable list defaults

Examples: `level_heights`, `walls`, `modules`, `punch_through`, `show`, `hide`, and `ValidateInput.object_ids`.

Impact: Pydantic v2 is safer than raw Python classes here, but this remains fragile and non-idiomatic.

Fix: use `Field(default_factory=list)`.

### 10. Python safe mode does not independently validate batch subcommands

`_exec_simple` checks `_SAFE_MODE_BLOCKED`, but `_exec_batch` sends the batch directly. The C# plugin still enforces mode in `Dispatch`, so this is not a security bypass, but Python-side behavior is inconsistent.

Impact: batch errors come from the plugin, not the Python MCP layer; UX and error wording differ.

Fix: pre-scan batch subcommands before sending them.

### 11. `CaptureInput.view` is now `Any`

This was necessary to support camera override objects quickly, but the schema is now loose.

Impact: invalid list/int/etc. values pass MCP validation and fail later or get stringified oddly.

Fix: replace with a discriminated union-like model: `view_name: str | camera: CameraInput`, or split `capture_viewport` and `capture_inspection_view` schemas cleanly.

### 12. `run_command` returns `status: ok` even when Rhino command returns false

The response includes `success: false`, but top-level status remains ok.

Impact: agents that only inspect top-level status will continue after failed Rhino commands.

Fix: return `status: error` with `COMMAND_FAILED` when `RhinoApp.RunScript` returns false.

### 13. `compare_before_after` captures after even when the batch fails

This is useful for visual debugging, but the tool returns mixed semantics: error status plus after image and diff.

Impact: agents may interpret a diff as intended success rather than partial/rolled-back result.

Fix: include explicit `batch_applied: true/false`, `rolled_back`, and `diff_interpretation`.

### 14. Healthcheck failure advice pointed to the wrong recovery path

`install/rhino-mcp-healthcheck.ps1` used to direct users toward the lower-level PowerShell installer instead of the normal `INSTALL.bat` / `FIX-RHINO-MCP.bat` recovery flow.

Impact: failed healthcheck recovery was harder than necessary for non-technical users.

Status: fixed. The healthcheck now points users to `INSTALL.bat` or `FIX-RHINO-MCP.bat`.

### 15. Standalone `chat.py` is stale compared with MCP

`chat.py` exposes an older OpenAI-function surface, lacks the new multi-angle / CPython3 / rollback options, and still contains mojibake.

Impact: users who try `rhino-chat` get a less capable and less polished product than the MCP.

Fix: either regenerate its schemas from the MCP tool definitions or mark it as legacy.

## Low Priority / Polish Findings

### 16. Mojibake remains in comments, scripts, docs, and `chat.py`

There are many `-`, `->`, and box-drawing artifacts. Most are comments, but some are user-facing CLI strings.

Impact: public polish issue, not a runtime blocker.

Fix: normalize affected files to UTF-8 and prefer ASCII comments/banners.

### 17. File headers still say older versions/repos

Several plugin files still say v4.5 or v4.7.5 and old repo URLs in comments.

Impact: confusing during support and GitHub issue triage.

Fix: standardize headers or remove versioned headers entirely.

### 18. README manual Claude config omits `--frozen`

The installer uses `--frozen`, but the manual Claude example does not.

Impact: manual users may run a subtly different environment from installer users.

Fix: add `--frozen` to the manual Claude example.

### 19. README says no .NET SDK required, but build docs are nearby

This is technically true for release ZIP users, but contributors still need the SDK.

Impact: minor confusion.

Fix: split "Install from release" and "Build from source" requirements more explicitly.

### 20. No GitHub Actions workflow is present

There is no `.github/workflows` directory.

Impact: regressions like missing pytest cannot be caught before release.

Fix: add CI for Python compile/tests and C# build.

## Positive Findings

- C# release build is clean: 0 warnings, 0 errors.
- Python package syntax is clean.
- Auth handshake and C# mode enforcement are correctly placed at the raw TCP dispatch layer.
- Batch subcommands route through `Dispatch`, so C# mode enforcement covers batch operations.
- `CaptureAddedIds` now filters dead/duplicate IDs, which fixes the earlier misleading object bookkeeping issue.
- CPython3 execution now gates Rhino version and script-server startup instead of silently reporting success.
- Direct image returns, multi-angle review, and before/after comparison are the right architecture for agent self-debugging.

## Suggested Fix Order

1. Silent-success fixes: `set_display_mode`, `redo`, `run_command`.
2. Shutdown/timeout hardening: close the `Server Busy` and timeout-desync holes.
3. Capture/section reliability: redraw/capture path and temporary section capture.
4. Test/CI packaging: add pytest dev dependency and GitHub Actions.
5. Polish pass: mojibake, stale headers, stale `chat.py`, docs mismatches.
