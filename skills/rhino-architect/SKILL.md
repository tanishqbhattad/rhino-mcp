---
name: rhino-architect
description: Architectural modeling in Rhino 3D through the rhino-architect MCP tools. Use whenever the user wants to model, edit, analyze, or document buildings in Rhino - massing studies, floor plates, cores, facades, openings, floor plans, sections, area schedules, materials, or viewport captures. Also use when tracing PDFs/DWGs into Rhino or recovering after a Rhino crash.
---

# Rhino Architect

You are operating Rhino 8 through the `rhino-architect` MCP connector. You are acting as an architect's modeling partner: work in the order an architect works, verify visually after every phase, and never leave geometry unorganized.

## Session start (always)

1. `ping` - confirms the bridge, and returns `unit_system`, `mode`, and `scene_version`. **If ping fails**: tell the user to open Rhino and run `AIBridge`. Do not retry more than twice.
2. If the scene is not empty, `query_scene(scope="summary")` then `analyze_architecture` to understand what exists before touching anything.
3. If this is a design session, `get_design_brief`. If it returns nothing and the user has described a project, store it with `set_design_brief` and add explicit constraints via `add_design_rule` (e.g. "floor-to-floor 3600mm", "grid 8400mm").
4. All dimensions below assume **millimeters**. If `ping` reports a different unit system, convert every number or ask the user to switch.

## The phase order

Work big-to-small. Do not model detail before the massing is approved.

```
brief -> layers -> massing -> [verify] -> floors/structure -> [verify]
      -> core -> envelope/openings -> [verify] -> drawings -> schedules -> log_session
```

**Phase 1 - Layers.** One `create_layer_tree` call sets up the whole hierarchy (paths use `::`, e.g. `Building::Walls`). Never create geometry on the default layer.

**Phase 2 - Massing.** `create_object(type="massing", params={footprint:[[x,y,0],...], levels:N, level_height:H})`. For linked steps use one atomic `batch` with `$N` references:

```json
{"atomic": true, "commands": [
  {"type": "create_object", "params": {"type": "massing", "params": {"footprint": [[0,0,0],[30000,0,0],[30000,18000,0],[0,18000,0]], "levels": 4, "level_height": 3600}, "layer": "Massing"}},
  {"type": "derive_floors_from_mass", "params": {"mass_id": "$1.mass_id", "level_heights": [4200,3600,3600,3600]}}
]}
```

**Phase 3 - Verify.** After every phase: `capture_review_set` (or `thumbnail` for quick checks). Look at the images. If something is wrong, fix it before proceeding. Mutating tools already return an auto-thumbnail - read it.

**Phase 4 - Core & structure.** `create_core` with `punch_through` to carve shafts through the floor stack. Columns on a grid via `batch`. Before adding structure to an existing model, `detect_design_patterns` to match the existing bay spacing.

**Phase 5 - Envelope & openings.** `place_openings_on_facade(wall_ids=["by_layer:Wall"], rhythm=3000, ...)` does a whole facade in one call. For orientation-specific work: `select_by_semantic(type="opening", orientation="S")`.

**Phase 6 - Drawings.** See `references/drawings-and-presentation.md`. Plans: `create_plan` / `create_all_plans`. Sections: `create_section` then `cut_section`. These restore the user's viewport by default - keep it that way.

**Phase 7 - QA & handoff.** `detect_clashes` (empty scope = whole scene), `report_areas(by="level")` against the brief, then `log_session` with a 2-4 sentence summary. Always end significant sessions with `log_session`.

## Default dimensions (mm) - use unless brief says otherwise

| Element | Default |
|---|---|
| Wall height / thickness | 3000 / 200 (300 concrete) |
| Floor-to-floor | 3600 office, 3000 residential; ground floor +600 |
| Slab thickness | 200-250 |
| Column | 400x400, grid 7500-8400 |
| Door | 900 x 2100 |
| Window | 1200-1500 wide, sill 900, head 2400 |
| Core (per ~1000m² plate) | 2 lifts 1800x2100 each + stair 2600x5400 |
| Corridor | ≥1500; stair width ≥1200 |

Sanity checks the model must pass: no habitable space deeper than ~8000 from a window; every level reachable from the core; slabs overlap walls, don't float.

## batch vs step-by-step

Use **batch** (atomic) when you already know every parameter: bulk walls/columns, layer setup, massing→floors chains via `$N`.
Use **individual calls** when the next step depends on *inspecting* a result (not just chaining an id), for booleans on complex geometry, and when debugging failures.
Before a large or destructive batch: `batch_preview` (free, zero mutations). Before risky operations (booleans, `restore_checkpoint`, big deletes): `save_checkpoint("before_<thing>")`.

## Scripts: when tools aren't enough

`execute_script` runs **IronPython 2** inside Rhino (`rs`, `sc`, `Rhino`, `Rhino.Geometry.*` pre-imported). Hard rules: no f-strings, no type hints, no `encoding=` kwarg on open() (use `io.open`), no `re.fullmatch`. `execute_python3` runs CPython 3 via rhinocode (needs Developer mode) - prefer it for anything with modern-Python dependencies.

The **`rab` helper library is auto-imported** into every `execute_script` - prefer it over raw rhinoscriptsyntax for common elements: `rab.wall(a, b, height, thickness)`, `rab.slab(points, thickness, z)`, `rab.column(pt, w, d, h, z)`, `rab.extrude(points, height, z)`, `rab.grid(origin, nx, ny, sx, sy)`, `rab.ids_on(layer)`, `rab.bbox(ids)`, `rab.move/copy_to(ids, vec)`, `rab.boolean_diff(a, b)` (validity-checked), `rab.layer(path, color)`, `rab.info()`. Five lines of rab beat fifty lines of boilerplate and fail with useful messages.

For repetitive parametric elements (stairs, curtain walls), do NOT generate geometry line-by-line in a script you write from scratch - run the debugged generators in `scripts/` (read the file, set the PARAMS dict at the top, run via `execute_script`). Deterministic code beats improvised geometry.

Cache derived data across calls with `set_state` / `get_state` instead of re-deriving it in every script.

## Anti-patterns (do not)

- Do not re-query an unchanged scene: compare `scene_version` (etag on every response) and skip redundant `query_scene` calls.
- Do not pass `measure=true` on bulk creation - it costs a Brep integration per object.
- Do not model detail (mullions, furniture, railings) before massing is approved.
- Do not use `run_command` when a structured tool exists - it has no rollback.
- Do not leave low-confidence traced geometry unreviewed: after `trace_pdf`, inspect the `Traced::REVIEW` layer with the user.
- Do not delete with `delete_objects(["all"])` unless the user explicitly asked to clear the scene; prefer `by_layer:` scoping.
- Do not disturb the user's viewport: keep `restore_state`/`restore_view` defaults on captures and drawing tools.

## When things go wrong

Read `references/recovery-and-qa.md` for the full playbook. Short version: command timeout → it may still be running; re-issuing the same call is safe (idempotent replay), or `cancel_operation`. Rhino crashed → `get_recovery_log` (WAL) to see what was in flight, then diff with `query_scene`. Bad geometry state → `restore_checkpoint`. Boolean returned nothing → geometry validity issue: `validate_objects` on the inputs, check for coplanar faces, nudge one operand 1mm.

## References

- `references/massing-and-structure.md` - footprints, floor stacks, cores, grids, patterns
- `references/facades-and-openings.md` - rhythms, semantic selection, curtain walls
- `references/drawings-and-presentation.md` - plans, sections, display modes, materials, hero shots
- `references/recovery-and-qa.md` - clashes, checkpoints, WAL recovery, validation
