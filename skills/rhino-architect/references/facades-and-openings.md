# Facades & Openings

## The one-call facade

`place_openings_on_facade(wall_ids=["by_layer:Wall"], rhythm=3000, width=1500, sill=900, head=2400)` distributes openings along walls at constant rhythm. Rules:

- rhythm ≥ width + 600 (leave structure between openings)
- Residential: rhythm 3000-3600, width 1200-1800. Office ribbon: rhythm = mullion module (1350/1500), width ≈ rhythm - 150.
- Doors: sill=0, head=2100, width=900 (single) / 1800 (double).
- `margin` keeps openings off wall ends - default is usually right; set explicitly near corners.

## Orientation-aware work

`select_by_semantic(type="opening", orientation="S", level=2)` - orientation derives from geometry (+Y = North). Typical uses: smaller openings on west face, deeper reveals south, service walls blank north. Workflow: select semantically → get ids → `delete_objects` + re-place with different rhythm, or tag them (`tag_object`) for later.

## Curtain walls

For a real curtain wall (mullions, transoms, panels) use `scripts/curtain_wall.py` via `execute_script` - set PARAMS at the top (wall start/end/height, module, mullion depth). It produces a `Facade::CurtainWall` layer tree with mullions as real geometry. Do not attempt to model mullion-by-mullion with create_object calls; that's 400 round trips for one elevation.

Glass material for the panels: `set_pbr_material(layer="Facade::Glass", base_color=[180,220,255], roughness=0.05, opacity=0.25)`.

## Openings that must cut

`create_object(type="opening")` on a wall creates the opening element; whether it visually reads as a hole depends on wall/opening layer display. When a real boolean void is required (rendering, sections), `boolean_operation(operation="difference", object_id_a=wall_id, object_id_b=opening_id, delete_input=false)` - checkpoint first, booleans on thin walls fail when faces are coplanar: make the cutter 10mm deeper than the wall on both sides.

## Verify

`capture_inspection_view` perpendicular to the facade (direction = facade normal, parallel projection) reads as a true elevation. Count the openings in the image; compare against `rhythm × wall length`. Check ground-floor doors land at z=0.
