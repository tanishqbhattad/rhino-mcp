# Massing & Structure

## Footprints

Footprint = ordered XY loop, counter-clockwise, closed implicitly (don't repeat the first point). Z always 0 for ground-based massing. L/U/T shapes are fine - just trace the outline. For a podium+tower, create two massings and stack: tower footprint at `z = podium_height` is NOT supported by `create_object(type="massing")` params directly - create the tower at z=0 and `transform_objects` move it up, or extrude a curve at elevation.

## Floor stacks

`derive_floors_from_mass(mass_id, level_heights=[4200,3600,3600])` sections the mass at each accumulated height and extrudes slabs. Variable list beats uniform `levels`+`level_height` whenever the ground floor differs (it almost always does). `slab_thickness` default 250. Slabs land on the `Slab` layer.

For non-extruded masses (tapered towers, terraced hills) the derived plates follow the mass section at each level - this is the whole point: sculpt the mass first, floors follow.

## Cores

`create_core(boundary=[...], height=H, punch_through=[mass_id, slab_ids...])`:

- Size rule of thumb: core area ≈ 8-12% of gross plate area for offices.
- Position: for a single-core tower keep the core centroid within ~10% of the plate centroid; edge/side cores are fine for plates under ~900m².
- `punch_through` is essential - without it the lift shafts are decoration. Pass the massing id and/or slab ids to carve real voids.
- Modules: pass `modules=[{type:"lift",...},{type:"stair",...}]` when you need explicit shaft layout; otherwise the default lift+stair split is reasonable.

## Columns & grids

Before adding structure to an existing model: `detect_design_patterns` returns the dominant X/Y spacing from existing column centroids - match it. For a new grid:

- Offices: 8100 or 8400 (fits 3 parking bays below), perimeter offset 600-1000 from facade.
- Residential: 6000-7200 aligned to party walls.

Generate the grid in ONE batch: build the list of `create_object(type="column")` sub-commands in code, don't loop tool calls. Columns run slab-to-slab; simplest correct approach is one column per floor (height = clear height), all on `Column` layer, then `align_to_grid` if positions drifted.

## Verifying massing

After massing + floors: `capture_review_set(views=["hero","plan","front"])`. Check: floor count matches brief, ground floor visibly taller, core pokes through every plate, nothing floats. `get_level_summary` should report the exact level count and elevations you intended - if levels merge (two plates at nearly the same Z), your level_heights accumulated wrong.

## Editing existing massing

Never rebuild what you can transform. `transform_objects` supports chained ops in one call: `operations=[{type:"move",...},{type:"array", count_x:4, spacing_x:8000}]`. Selectors accept `by_layer:Massing`, `by_name:Tower*`, `last_created`, `selected`. To make massing options side by side: `transform_objects(copy=true, translation=[dx,0,0])` then edit the copy - keep options on layers `Options::A`, `Options::B` and use `batch_layer_visibility(isolate=...)` to compare.
