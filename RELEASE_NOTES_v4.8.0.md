# RhinoAIBridge v4.8.0 - Protocol 5

The largest infrastructure release since the snapshot cache. The wire protocol moves
to version 5 while staying fully backward compatible in both directions (new server +
old client, old server + new client both work; features degrade gracefully).

## Protocol 5

- **Multiplexed connection.** Every request carries a `request_id`; responses are
  matched by id and may return out of order. Ping, cancel, `get_state`,
  `get_scene_diff`, `get_tracker_version`, `get_change_log`, `get_recovery_log` and
  snapshot-backed `query_scene` are answered directly on the plugin's TCP thread -
  they stay sub-millisecond even while a 180s script runs on the UI thread.
- **Idempotent retries.** Mutating commands are registered in an operation registry.
  If the connection drops mid-round-trip, the client re-sends the SAME `request_id`
  and the plugin replays the cached result (or joins the still-running operation)
  instead of executing twice. The duplicate-geometry-on-reconnect class of bugs is gone.
- **Cooperative cancellation.** New `cancel_operation` tool. Cancel frames are handled
  inline on the TCP thread and signal the running command's CancellationToken; batches
  stop at the next op boundary (atomic batches roll back), facade/floor generators
  return partial results flagged `cancelled: true`. Server-side timeouts now also
  signal cancel, so an abandoned command stops mutating the model.
- **Binary image frames (flag 0x02).** Viewport captures travel as raw JPEG/PNG bytes
  with a JSON header - no base64 inflation (-25% wire bytes) and no giant-string JSON
  parsing on either side.
- **Hello handshake.** Client and server negotiate features; either side silently
  falls back to legacy single-flight behavior when the other predates protocol 5.

## Speed

- `capture_viewport` renders through `ViewCapture` (offscreen, exact resolution, no
  pre-capture redraw), with automatic fallback to the legacy path.
- 3dm export uses native FileIO (`WriteSelectedObjectsOnly`) instead of scripted
  `-_Export`; `restore_checkpoint` imports via `File3dm.Read` with real error
  reporting (a failed restore can no longer wipe the document).
- Responses serialize JObject -> UTF-8 stream directly (no intermediate string).

## Accuracy

- **Geometry post-conditions**: every create returns `is_valid`/`is_solid`/`face_count`;
  booleans and openings return per-result solidity - bad geometry is visible without a
  follow-up call.
- **Oriented openings**: `create_opening` / `place_openings_on_facade` derive the wall's
  frame from its longest horizontal edge - diagonal walls now get correct openings.
- **Whole-scene validation**: per-object validity is cached in the scene snapshot
  (invalidated on geometry replace). `validate_objects` covers 5000-object scenes
  incrementally instead of capping at 100.
- **Unit-aware defaults**: all architectural defaults (wall 3000mm, sill 900mm, grid
  1000mm, ...) scale by the document's unit system - a meters document gets a 3m wall.

## Agent ergonomics

- **Columnar listings**: `query_scene(format="columnar")` returns parallel arrays
  (ids/names/layers/types/bboxes) - typically 40-60% fewer tokens on large scenes.
- **Batch review strips**: batches with 8+ ops embed plan + front thumbnails alongside
  the perspective thumbnail, so the model verifies massing and elevation in one shot.
- **Checkpoint hygiene**: the checkpoint registry persists across sessions
  (registry.json), auto-checkpoints are capped at the 10 newest, and there is a new
  `delete_checkpoint` tool.
- **Crash recovery**: every mutating command is journaled to a write-ahead log
  (%APPDATA%\AIBridge\wal\) before and after execution; the new `get_recovery_log`
  tool reads the tail so the agent can reconstruct context after a crash.

## Compatibility

- Plugin protocol: 5.0 (accepts protocol-4 clients with full legacy semantics).
- MCP server: works against 4.x plugins (legacy single-flight mode, conservative
  retry rules) and 5.x plugins (full feature set).
- Tool count: 115 (new: `cancel_operation`, `delete_checkpoint`, `get_recovery_log`).
