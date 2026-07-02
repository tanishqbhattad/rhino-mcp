# Curtain wall generator for rhino-architect (execute_script / IronPython 2 safe).
# Edit PARAMS, run via execute_script. Creates mullions, transoms and glass panels
# on Facade::CurtainWall::Mullions / ::Glass. All units mm.

PARAMS = {
    "start": [0.0, 0.0, 0.0],        # wall start (XY, z = base elevation)
    "end": [12000.0, 0.0, 0.0],      # wall end
    "height": 3600.0,                 # panel height (one storey)
    "module": 1500.0,                 # vertical mullion spacing (panel width target)
    "transom_heights": [900.0],       # horizontal transoms at these heights (list, may be empty)
    "mullion_w": 60.0,                # mullion face width
    "mullion_d": 150.0,               # mullion depth (into the building, -v side)
    "glass_t": 24.0,                  # glass thickness
    "layer_mullion": "Facade::CurtainWall::Mullions",
    "layer_glass": "Facade::CurtainWall::Glass",
}

import math
import rhinoscriptsyntax as rs
import scriptcontext as sc
import System
import Rhino
from Rhino.Geometry import Point3d, Vector3d, Plane, Box, Interval


def _ensure_layer(path):
    if not rs.IsLayer(path):
        rs.AddLayer(path)


def _add_box(base_pt, u, v, du, dv, dz, layer):
    pl = Plane(base_pt, u, v)
    b = Box(pl, Interval(0, du), Interval(0, dv), Interval(0, dz))
    gid = sc.doc.Objects.AddBrep(b.ToBrep())
    if gid != System.Guid.Empty:
        rs.ObjectLayer(str(gid), layer)
    return gid


def build():
    p = PARAMS
    _ensure_layer(p["layer_mullion"])
    _ensure_layer(p["layer_glass"])

    s = Point3d(p["start"][0], p["start"][1], p["start"][2])
    e = Point3d(p["end"][0], p["end"][1], p["end"][2])
    run = Vector3d(e - s)
    run.Z = 0.0
    length = run.Length
    if length < 1.0:
        print("[ERROR] start/end coincide")
        return
    u = Vector3d(run)
    u.Unitize()
    v = Vector3d.CrossProduct(Vector3d.ZAxis, u)   # facade normal-ish (inward = -v by convention)

    n_panels = max(1, int(round(length / p["module"])))
    module = length / n_panels
    mw, md, h = p["mullion_w"], p["mullion_d"], p["height"]

    count_m = 0
    # vertical mullions (n_panels + 1), all centered on the grid so every bay is
    # uniform; edge mullions overhang the wall ends by mw/2 (negligible, consistent).
    for i in range(n_panels + 1):
        x = i * module - (mw * 0.5)
        base = Point3d(s) + u * x + v * (-md)
        _add_box(base, u, v, mw, md, h, p["layer_mullion"])
        count_m += 1

    # horizontal transoms per bay
    for th in p["transom_heights"]:
        for i in range(n_panels):
            x0 = i * module + (mw * 0.5)
            bay = module - mw
            base = Point3d(s) + u * x0 + v * (-md) + Vector3d.ZAxis * (th - mw * 0.5)
            _add_box(base, u, v, bay, md, mw, p["layer_mullion"])
            count_m += 1

    # glass panels (one per bay, full height between mullions; simple single sheet)
    count_g = 0
    for i in range(n_panels):
        x0 = i * module + (mw * 0.5)
        bay = module - mw
        base = Point3d(s) + u * x0 + v * (-p["glass_t"] * 0.5)
        _add_box(base, u, v, bay, p["glass_t"], h, p["layer_glass"])
        count_g += 1

    sc.doc.Views.Redraw()
    print("CurtainWall: %d panels @ %.0fmm module, %d mullion/transom elements, %d glass sheets, length %.0fmm" % (
        n_panels, module, count_m, count_g, length))


build()
