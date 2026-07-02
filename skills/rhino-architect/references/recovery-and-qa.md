# Recovery & QA

## Routine QA (before calling anything "done")

1. `detect_clashes` - empty scope checks every solid. `overlap` = real interpenetration (fix), `touch` = surfaces meet (usually fine: slab on wall). Scope with `layer` on big models.
2. `validate_objects` - invalid Breps poison later booleans; fix or rebuild them now.
3. `report_areas(by="level")` vs brief.
4. `find_unassigned_geometry` - orphans mean layer discipline slipped; assign or delete.
5. `capture_review_set` - final visual pass.

## Checkpoints

`save_checkpoint(name)` before: booleans on complex geometry, restore operations, mass deletes, any script that mutates >20 objects (scripts auto-checkpoint by default - leave that on). `restore_checkpoint` REPLACES the whole model - checkpoint current state first if it has any value. Named checkpoints persist across Rhino sessions; auto-checkpoints keep the 10 newest. Prefer checkpoints over stacked `undo` for anything more than one step back.

## Timeouts & cancellation

A tool timing out does NOT mean it failed - the command may still be running in Rhino. The protocol replays by request_id, so re-issuing the same call is safe (you'll get the finished result, not a duplicate). If the user wants it stopped: `cancel_operation` - it halts at the next checkpoint; atomic batches roll back.

## After a Rhino crash

1. Reopen Rhino, run `AIBridge`, `ping`.
2. `get_recovery_log` - the write-ahead log shows every mutating command with begin/end status. Anything with `begin` but no `end` was in flight when Rhino died.
3. `query_scene(scope="summary")` + `get_level_summary` to see actual state; compare with the WAL tail.
4. Re-issue only what's missing. Then `save_checkpoint("post_recovery")`.

## Connection errors

`RHINO_NOT_CONNECTED`: Rhino closed or AIBridge stopped - ask the user to run `AIBridge` in Rhino. Do not loop retries.
`MODE_BLOCKED`: the plugin is in Safe/Standard mode - `execute_script`/`execute_python3`/`run_command` need Developer mode; destructive ops need Standard+. Tell the user which mode to pick when starting AIBridge, don't try to work around it.

## Boolean failures

Boolean returned empty/None: (1) `validate_objects` both operands; (2) coplanar faces are the usual cause - move one operand 1mm or oversize the cutter; (3) tolerance: model absolute tolerance should be 0.01mm-1mm for buildings; (4) worst case, rebuild the operand from its section (`get_section_profile` → extrude fresh).

## Scene sync after user edits

The user works in Rhino between your turns. Cheap catch-up: compare `scene_version` from your last response with `ping`; if changed, `get_scene_diff(from_version=...)` returns added/deleted/modified only. If the diff reports `truncated`, fall back to a full `query_scene`.
