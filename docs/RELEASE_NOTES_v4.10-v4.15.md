# RhinoAIBridge v4.10.0 - v4.15.0

Consolidated notes for the six releases after Protocol 5. Written retroactively from the
commit record, so the claims here are the ones that were actually verified at the time.

**There is no v4.9.0.** The line runs v4.8.0 -> v4.10.0; the 4.9.x fixes were folded into
v4.10.0 rather than shipped on their own. Releases after v4.8.0 are also untagged in git -
the last tag is `v4.8.0`.

Three of these releases (v4.12, v4.13, v4.14/v4.15) are direct responses to **field reports
from real modelling sessions**. That is why the fixes look unusually specific: each one has a
session behind it, and a measurement that proved it.

---

## v4.15.0 - assertions against the brief, and a self-test that proves the stack

Phases 5-7 of the code-grounded field report.

### Guardrails

- **`execute_script` post-conditions.** Every script returns `created_bbox`,
  `created_solid_count`, `created_open_count`, `created_invalid_count` - a phase script
  reports its own health without a follow-up `validate_objects`.
- **Drift warning.** Geometry landing more than 2x the scene diagonal from the scene centre
  sets `drift_warning`. A plane-origin mistake had put a roof 66.5 m away and nothing noticed
  until a render; a bbox comparison is nearly free and catches that whole class.
- **Checkpoint economics.** `list_checkpoints` reports `total_size_mb`, the directory and the
  retention count, and warns past 200 MB. Retention is settable with
  `RHINO_MAX_AUTO_CHECKPOINTS` - 11 x 12 MB had accumulated invisibly.
- **Slow calls always report `elapsed_ms`** past 5 s even with `RHINO_TIMING` off, so the
  model learns what is expensive instead of meeting it later as a timeout
  (`RHINO_SLOW_CALL_MS` tunes it, 0 disables).

### New tools

- **`assert_dimensions(targets=[...])`** - a target/actual/deviation table against the brief.
  Measures `length_x`, `width_y`, `height`, `top_z`, `base_z`, `span`, `clear_between`.
  Verified live: `clear_between` across two pier rows returned exactly 14500; a deliberate
  2000 error was caught at `deviation_pct` 6.67. This is the QA call that brief-driven work
  was missing - *"the nave is 12.5 m clear"* is checkable, *"the geometry is valid"* is not.
- **`capture_elevations(views=[n,s,e,w,plan,hero])`** - a whole orthographic set in one call,
  every view framed on the **same** selection so scales are comparable, with
  `exclude_layers` removing e.g. a site slab from the fit. Verified: 4 views framed on 21 of
  22 objects, correct parallel/perspective per view, viewport restored afterwards.
- **`run_selftest`** - the health check the report asked for. Builds ~10 objects (piers plus
  two walls with openings, one on the YZ plane), asserts counts and watertightness, checks a
  dimension, captures, cleans up, and times every stage. It proves transport, IronPython +
  `rab`, selectors, assertions and capture work *together*, which `ping` cannot.
  Measured live: **568 ms, all six stages.**

### `rab` additions

`annulus_wall` (apse rings; `r_in=0` yields a solid pier instead of failing on a degenerate
inner circle), `buttress_pier` with setbacks, `spire` (tapers to a small ring, never a
degenerate apex, so the result stays booleanable), `gable_roof`, `mirror_x`, `mirror_y`,
`array_x`, `radial`.

### Eval task 10 + regression tests

`evals/tasks/10_gothic_cathedral.json` packs every trap from section A into one task: a
nested layer tree with the leaf name `Piers` deliberately repeated under two parents, a
YZ-plane wall with pointed-arch openings, an annulus apse, and dimension assertions. The
harness gained `dimension` and `watertight` assertion types that delegate to
`assert_dimensions` / `assert_geometry`, so evals and live QA agree by construction.

First end-to-end run, built through the MCP exactly as an agent would:

```
[PASS] gothic_cathedral 9/9
  clear_between actual=14500.0 target=14500.0 deviation=0.0
  height        actual=14000.0 target=14000.0 deviation=0.0
  top_z         actual=21000.0 target=21000.0 deviation=0.0
  2/2 closed valid solids, 0 objects on the default layer
```

Both YZ-plane walls came back closed, valid, 98 faces each with all four openings cut - the
case that failed 9 times out of 9 before `rab.wall_profile` existed.

`server/tests/test_field_report_regressions.py` pins the fixes that were **silent**, which is
what makes them worth guarding: name-keyed layer counts, the `by_layer_exact:` escape hatch,
`wall_profile`'s planar fallback, and `rab.py` staying f-string-free and pure ASCII.

> **Counting note:** v4.15.0 shipped **84 collected tests**, 12 of them from
> `test_field_report_regressions.py`. That file defines 9 test *functions* - one carries a
> `@pytest.mark.parametrize` over 4 tools - so "9" and "12" both describe it correctly
> depending on whether you count functions or collected cases.

---

## v4.14.0 - the layer and selector fixes (field report, phases 1-4)

Root causes were confirmed at the cited lines before anything changed.

- **A1 - `list_layers` reported `object_count` 0 for every nested layer.**
  `CountsByLayerName()` keys by `FullPath` while `ListLayers` looked up by leaf `Name`, and
  duplicate leaf names overwrote each other in the dictionary. Added `CountsByLayerIndex()` -
  index is the only unambiguous key. Verified on a tree with two `Piers` layers: 3 and 5,
  each correct, previously colliding.
- **A2 - `by_layer:` matched one layer exactly**, so a parent whose objects all live on child
  layers selected nothing. `ByLayerName` now includes descendants, with a leaf-name fallback
  that matches *every* duplicate. `by_layer_exact:` keeps the old behaviour. One fix, five
  tools. Verified: `by_layer:ND`=15, `ND::03_Facade`=5 (was 0), exact=0.
- **A3 - `report_areas` ran `VolumeMassProperties.Compute` on every Brep**, including hundreds
  of open vault webs, on the UI thread under a 60 s budget. Now `mode='fast'` integrates only
  closed solids - volume is undefined for an open shell - with a computation budget, scope
  selector and cancellation checks. Reports `volumes_computed` / `open_breps_skipped`.
- **A4 - a client-side timeout hid completed work.** The result was already in the replay
  cache; nothing exposed it. Added `get_operation_result(request_id)` and `list_operations`,
  and the timeout message now hands back the `request_id`.
- **A5 - an abandoned read kept holding the UI thread** - one orphaned `report_areas` stalled
  four captures. Reads are recomputable, so they are auto-cancelled on client timeout;
  mutations are deliberately left running, because their result is wanted and retrievable.
- **A6 - `rab` counted 921 of 942 objects.** The default document enumerator skips hidden
  objects while the C# side sets `HiddenObjects`. `rab` now uses an explicit
  `ObjectEnumeratorSettings`, and `info()` reports `(+N hidden)`.
- **A7 - `Extrusion.AddInnerProfile` refuses inner loops on YZ-plane profiles** (9 failures
  out of 9). New `rab.wall_profile()` tries Extrusion, then falls back to
  `CreatePlanarBreps` + `CreateExtrusion`. Verified live: YZ and XZ walls both closed, valid,
  14 faces.
- **A8** - `arch_profile` documents that it returns a Curve, never a GUID. The
  skipped-checkpoint message now says *"pre-run snapshot identical to the existing
  checkpoint"* instead of *"scene unchanged"*, which had been read as "nothing happened" on a
  script that created 260 objects.

---

## v4.13.0 - `capture(fit=...)`, provably idempotent

Closes the camera items from the v4.11 field report.

`width`/`height` set **resolution**, and the live viewport's frustum aspect is unrelated to
it, so Rhino widened the FOV or cropped instead of reframing - every capture was a guess.
`capture_viewport` now takes `fit='by_layer:Massing'` or `fit={'selector':..., 'margin':...}`
and solves the camera exactly. For each bbox corner in camera space:

```
D >= |v.x|/tan(halfFovH) + v.z    and    D >= |v.y|/tan(halfFovV) + v.z
```

the max over all corners is the nearest distance that still contains everything. Measured:
vertical coverage **100.0%** (touching the frame edge), horizontal 70.4% - a mathematically
tight fit, not `ZoomExtents`' bounding-*sphere* slack.

**Camera drift** was reproduced in the first implementation: two identical captures moved the
camera and the lens went 33.7 -> 32.4. The cause is a feedback loop - the vertical half-angle
is read from the frustum, and the frustum is then rewritten, so folding the margin into the
*angle* compounds on every call. Fixed by leaving the angles untouched and expressing margin
purely as camera distance. Verified: three consecutive fit captures return **byte-identical
images (8505 bytes)** and the same camera to 2 dp. `fit` is derived from the bbox, never from
the current camera, so it cannot drift.

Also fixed while testing: selectors rejected a bare string (`'by_layer:FIT'` failed to
deserialize as `List<string>`); and a `JsonSerializationException` was classified
`COMMAND_FAILED`, so a wrong parameter *type* was reported as "usually a geometry validity
issue" - JSON errors now classify as `INVALID_REQUEST` with a hint that names the real
problem.

---

## v4.12.0 - self-describing schemas, two silent RhinoCommon traps

Response to the v4.11.0 field report (parametric tower + 50-option massing study). Its
diagnosis was right: **the gap was discoverability, not capability.**

- **Untyped schemas - the report's biggest time cost.** Tools taking a Pydantic model
  advertised `$ref`/`$defs` with the real fields hidden. Clients that do not dereference
  render that as `{"params": {}}`, so every parameter name had to be guessed and failures
  surfaced only as validation errors. `_inline_schema_refs()` now resolves them for all 43
  model-based tools at startup; the calling convention is unchanged. `assert_geometry`,
  `batch`, `validate_objects` and `select_by_semantic` had gone unused purely because finding
  their arguments cost more than hand-rolling the logic.
- **`Curve.CreateInterpolatedCurve(pts, 3, ChordPeriodic)` returns an OPEN, non-periodic
  curve.** Reproduced: `IsClosed` False, `IsPeriodic` False. `rab.periodic_curve()` uses
  `NurbsCurve.Create(True, degree, pts)` instead -> closed and periodic.
- **Lofted and capped Breps come back inward, and differencing against an inverted solid ADDS
  material.** `rab.orient()` / `is_inverted()` expose it, and `boolean_diff` re-orients both
  operands. Measured end to end: volume 1000 -> 840, exactly -160 as intended. The test also
  reproduces *why this hides* - `AddBrep` re-orients on insert, so a document audit reports
  nothing wrong while the bug is live.
- `Brep.CreatePlanarBreps` needs `System.Array[Curve]` or silently returns none -> `rab.cap()`.
- **`rab.help()`** prints the whole API with real signatures and leads with the document's
  unit system; `rab.help('arch')` for one function. `rab.units()` / `rab.m()` make scripts
  unit-portable.
- `execute_script` takes `timeout_seconds` (5-600) for long parametric builds, and
  `retry_hint` no longer misclassifies Python errors as geometry validity issues.

---

## v4.11.0 - the installer was broken for everyone, and the shipped plugin was six weeks stale

Two real user-facing failures, both root-caused.

- **`INSTALL.bat` had LF line endings.** `cmd.exe` executes batch files by tracking a **byte
  offset**, so with LF-only endings `goto` and `call` seek to the wrong position: the script
  jumps around and appears to close instantly after the first keypress. GitHub's "Download
  ZIP" serves blobs exactly as stored, so *every* downloader got a broken installer
  regardless of their `core.autocrlf`. `.gitattributes` now pins `*.bat`/`*.cmd`/`*.ps1` to
  CRLF in the repository itself.
- `INSTALL.bat` rewritten: tee'd `install-log.txt`, a PASS/FAIL verification report, proves
  the server actually imports rather than trusting exit codes, detects a running Rhino *and* a
  running AI client (both hold files open), sets the plugin LoadMode to AtStartup, and always
  waits for ENTER so the report cannot vanish.
- **`dist/plugin/` - the binary `INSTALL.bat` actually copies - was a six-week-old build**
  with no `assert_geometry`, `find_unsupported`, `section_preview`, `list_commands`, clash
  detection or checkpoint economics. Every install from GitHub got that plugin. Refreshed, and
  CI now guards it.
- Version single-sourced everywhere; `__init__.py` reads package metadata instead of
  hardcoding 4.8.0.
- README rewritten for current capability and discoverability.

---

## v4.10.0 - tool profiles and protocol tests

- **`RHINO_TOOLS=lean|standard|full`** profiles. The full tool count is too many for most
  models' tool-selection to stay sharp, so profiles control what gets *advertised*. Pruned
  tools stay callable via `batch`, so profiles cost discoverability, never capability.
- Capture tools gain `as_json`; `*_json` twins and McNeel-compat aliases are full-profile only.
- **Event-loop fixes:** `trace_pdf`, PDF preview and image diff moved off the event loop via
  `asyncio.to_thread`.
- Inputs normalized: sentinel defaults (`-1`, `-361`, bare `None`) become `Optional[...]`;
  `CreateCoreInput` is `extra=forbid`.
- `capabilities` computes `tool_count` and fetches `plugin_commands` live via a new C#
  `list_commands`.
- **Socket-level integration tests for `RhinoProtocol`** against a mock plugin server,
  including a FIFO-leak regression.
- Carries forward the 4.9.x fixes: heartbeat no-exit, FIFO future leak, copy alias, Zip Slip
  guard, clash detection, semantic orientation, "Server Busy" OLE filter.

---

## Compatibility

| | |
|---|---|
| Plugin protocol | 5.0 (accepts protocol-4 clients with legacy semantics) |
| Registered tools | 128 (`lean` ~21 / `standard` ~65 / `full` 128) |
| Tests | 84 collected (81 test functions; one is parametrized over 4 tools) |
| Python | >= 3.10, `mcp[cli]` >= 1.2.0 (locked at 1.27.1) |
| Plugin | .NET 8 + RhinoCommon, Rhino 8 |
