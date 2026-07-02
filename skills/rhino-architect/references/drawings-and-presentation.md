# Drawings & Presentation

## Floor plans

`create_plan(floor="Level 1", cut_height_mm=1200, capture=true)` cuts, captures, and restores the viewport. `create_all_plans` does every detected level at once. Cut height 1200 is standard (catches windows); use 900 if sills are low. Levels come from the same detection as `get_level_summary` - if plans come out empty, level detection failed: check slabs are flat and distinct in Z.

## Sections & elevations

1. `create_section(label="A", start_x=..., end_x=..., view_side="left")` places a section line with arrowheads on its own layer - position it through the core or the most revealing bay.
2. `cut_section(label="A", capture=true)` cuts and captures.
3. `align_view_to_section(label="A")` if the user wants to stay in the section view (this one intentionally does NOT restore).
4. Elevations: `create_elevation(direction="north")` then cut.
5. `list_sections` before creating new ones - reuse and `update_section` instead of stacking duplicates.

## Display modes & illustration

`set_display_mode` for built-ins (Wireframe, Shaded, Rendered, Arctic, Ghosted, Technical). For diagram styles, `create_display_mode(name="AI-Diagram", preset="diagram")` - presets: diagram, technical, blueprint, sketch, axonometric, atmospheric, monochrome, cutaway. Custom modes are AI- prefixed and deletable; built-ins are never modified.

Capture recipes:
- Design check: `thumbnail` (fast, small)
- Client-ish hero: `capture_inspection_view(direction=[1,-1,-0.55], projection="perspective", display_mode="Rendered", width=1600, height=1000)`
- Full review: `capture_review_set(views=["hero","plan","front","right","detail"])`
- Plan/elevation graphics: display_mode="Technical", parallel projection

## Materials

Fast PBR by layer: `set_pbr_material` - presets that read well: concrete [170,170,170] r0.9; glass [200,220,255] r0.05 opacity 0.3; sandstone [194,178,128] r0.8; dark wood [101,67,33] r0.6; metal [180,180,190] r0.1 m0.9.

Real textures: `search_materials(keyword)` → present options to the user → `download_material(asset_id, layer_name, confirmed=false)` first (preview), then `confirmed=true` only after the user approves. AmbientCG is CC0. UV repeat is computed from physical size automatically.

Apply materials to LAYERS, not objects - it keeps the model swappable.

## Area schedules

`report_areas(by="level")` for GFA per floor; `by="layer"` for program breakdown when layers encode use (Residential::Units, Retail, ...). Cross-check the total against footprint × levels; >5% deviation usually means duplicate slabs or a forgotten mezzanine. Present as a small table and offer to save to a file.

## Import & tracing

DWG/DXF: `import_dwg(file_path)` - native, accurate. PDF: `get_pdf_info` → `preview_pdf_page` (LOOK at it) → `trace_pdf(page_number=..., model_unit="mm")`. After tracing ALWAYS: (1) `calibrate_scale` on a known dimension (a door = 900mm works), (2) review the red `Traced::REVIEW` layer with the user, (3) build on new layers, never on traced ones.
