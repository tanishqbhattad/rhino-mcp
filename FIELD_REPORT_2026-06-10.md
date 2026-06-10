# Rhino MCP Field Report - 2026-06-10

This report comes from three full AI-driven Rhino builds, roughly 60 scripted
calls, and 1,800+ generated objects. It tracks real failure modes observed in
architectural modeling workflows.

## Fixed In Current Patch

### 1. `execute_python3` silently no-ops on Rhino 8.9

**Impact:** High. The tool returned `status: ok` and `returncode: 0` while
creating no geometry because RhinoCode CPython execution needs Rhino 8.11+.

**Fix:** `execute_python3` now reads Rhino's reported version from `ping` and
hard-fails below Rhino 8.11 with `RHINOCODE_UNSUPPORTED_RHINO_VERSION`. It also
requires `start_script_server.started == true`, not only `status == ok`.

**Repro:** On Rhino 8.9, call `execute_python3` with a simple geometry script.
Old behavior: success with no objects. New behavior: explicit error and retry
hint to use `execute_script` or upgrade Rhino.

### 2. `ping` required an empty `params` object

**Impact:** Medium. Calling `ping` without arguments failed validation even
though it has no meaningful input.

**Fix:** `ping(params=None)` is now accepted.

### 3. `capture_viewport` schema rejected camera override objects

**Impact:** Medium. Tool docs and model usage implied camera overrides were
accepted, but Pydantic only allowed `view: str`.

**Fix:** `capture_viewport.view` now accepts a named view string or a camera
object. Camera objects are routed to `capture_inspection_view`.

**Example:**

```json
{
  "view": {
    "location": [10000, -12000, 8000],
    "target": [0, 0, 2000],
    "projection": "perspective"
  }
}
```

### 4. `new_object_ids` included duplicates and consumed intermediates

**Impact:** Medium. Scripts that created temporary geometry could report more
created objects than remained in the scene.

**Fix:** `CaptureAddedIds` now reports only live, unique object IDs after the
command finishes.

### 5. Failed scripts could leave partial geometry

**Impact:** Medium. `execute_script` wraps an undo record, but a failing script
could still leave orphaned geometry.

**Fix:** Added opt-in `rollback_on_error` to `execute_script`. When true, live
objects created by a failed script are deleted before the tool returns.

## Open Bugs / Larger Fixes

### A. Clipping plane and section capture path is unreliable

**Observed:**

- `AddClippingPlane` with a `views` list threw a `NoneType` iteration failure.
- `ViewNames()` returned duplicate `Top` and no `Perspective` in one document.
- Clipping planes bound to the active view had no visible effect on captures.
- Cutaway workflows fell back to hiding hundreds of objects manually.

**Desired fix:** Add a dedicated `capture_viewport(section_plane=...)` path that
creates a temporary clipping plane only for the capture and guarantees cleanup.
Do not rely on document-persistent section layers for visual QA.

### B. Timeout desync between MCP client and Rhino server

**Observed:** A long hide loop timed out on the MCP client, but Rhino continued
executing and advanced `scene_version`.

**Desired fix:** Add cancellation-aware command execution and/or shorter
server-side chunking for large object loops. At minimum, expose a `job_id` for
long-running commands so clients can poll/cancel instead of assuming failure.

### C. Display mode does not always survive document switches/captures

**Observed:** Display mode sometimes reverted after captures or document changes
despite `restore_state`.

**Desired fix:** Store display mode by viewport runtime serial/name and reapply
after document switch/capture completion. Add regression tests with multiple
open documents.

### D. Boolean diagnostics are too shallow

**Observed:** Empty boolean results are reported as generic failure. Hollow shell
and coplanar/tangent cases need better explanation.

**Desired fix:** Return per-cutter diagnostics for multi-cutter differences:
invalid input, nested void, no intersection, coplanar faces, tolerance issue.
Offer optional auto-jitter/repair retry.

## Highest-Impact Feature Work

### 1. Server-side bulk operations with filters

Add one-call operations for hide/show/delete/relayer/select using filters:

- layer path
- name pattern
- type
- bbox intersection/containment
- visibility/selection state

This removes slow per-object rhinoscript loops and prevents timeout desync.

### 2. Architectural primitives

Add native, parametric architecture tools:

- `arch_solid(profile="four_centred" | "pointed" | "ogee", ...)`
- `dome(profile="onion" | "hemisphere" | "ribbed", ...)`
- `stair(rise, run, width, flights, landing, railing=True)`
- `balustrade(path, spacing, height, style)`
- `spire(sides, taper, height, base_radius)`
- `array_polar` and `array_along_curve`

These collapse hundreds of lines of ad hoc IronPython into structured,
inspectable calls.

### 3. Real section capture

Implement:

```json
capture_viewport({
  "section_plane": {"origin": [0,0,1500], "normal": [0,0,1]},
  "view": "Top",
  "temporary": true
})
```

The plane must affect only the returned image and must not persist in the Rhino
document.

### 4. Sun and lighting control

Add:

- `set_sun(azimuth, altitude)`
- `set_skylight(enabled, intensity)`
- `set_ambient_light(color, intensity)`
- capture-time lighting overrides for hero shots

### 5. Honest capability detection

At handshake, expose version-gated capabilities:

- Rhino version
- RhinoCode/CPython availability
- section capture support
- display mode persistence support
- optional Python packages

Tools should fail before doing work when the current Rhino version cannot
support the operation.

## Things That Worked Very Well

- `execute_script` was reliable across heavy use.
- `sc.sticky` persistence made reusable helpers practical.
- `create_layer_tree` with PBR materials in one call was highly effective.
- `scene_version` etags were useful for diagnosing silent failures.
- Automatic checkpoints saved multiple failed modeling branches.
