# Dog-leg (U-return) stair generator for rhino-architect (execute_script / IronPython 2 safe).
# Edit PARAMS, run via execute_script. Creates treads and a mid landing on layer "Stair".
# All units mm. Riser count is computed from total_rise / target_riser.
# Convention: flight 1 climbs along `direction`; the landing is step n1; flight 2
# returns opposite the direction, offset across by tread_width + gap.

PARAMS = {
    "origin": [0.0, 0.0, 0.0],      # bottom of first riser, corner of the stair footprint
    "direction": [1.0, 0.0, 0.0],   # run direction of the FIRST flight (XY)
    "total_rise": 3600.0,            # floor-to-floor height
    "tread_width": 1200.0,           # flight width
    "tread_depth": 280.0,            # going
    "target_riser": 170.0,           # ideal riser height (code range 150-190)
    "gap": 100.0,                    # gap between the two flights
    "tread_thickness": 60.0,
    "landing_depth": 1200.0,
    "layer": "Stair",
}

import math
import rhinoscriptsyntax as rs
import scriptcontext as sc
import System
import Rhino
from Rhino.Geometry import Point3d, Vector3d, Plane, Box, Interval


def _unit(v):
    l = math.sqrt(v[0] * v[0] + v[1] * v[1])
    if l < 1e-9:
        return Vector3d(1.0, 0.0, 0.0)
    return Vector3d(v[0] / l, v[1] / l, 0.0)


def _add_box(base_pt, u, v, du, dv, dz, layer):
    """Axis box: base corner at min-u/min-v/min-z, extents du/dv/dz. u x v must = +Z."""
    pl = Plane(base_pt, u, v)
    box = Box(pl, Interval(0, du), Interval(0, dv), Interval(0, dz))
    gid = sc.doc.Objects.AddBrep(box.ToBrep())
    if gid != System.Guid.Empty:
        rs.ObjectLayer(str(gid), layer)
    return gid


def build():
    p = PARAMS
    if not rs.IsLayer(p["layer"]):
        rs.AddLayer(p["layer"])

    o = Point3d(p["origin"][0], p["origin"][1], p["origin"][2])
    u = _unit(p["direction"])                       # along flight 1
    v = Vector3d.CrossProduct(Vector3d.ZAxis, u)    # across the flight; u x v == +Z

    n_r = int(round(p["total_rise"] / p["target_riser"]))
    if n_r < 4:
        n_r = 4
    riser = p["total_rise"] / n_r
    if riser < 150 or riser > 190:
        print("[WARN] riser %.1fmm outside 150-190 code range" % riser)

    n1 = n_r // 2            # risers climbed by flight 1 (landing is step n1)
    n2 = n_r - n1            # risers climbed by flight 2 (top step is the floor itself)
    d, w, t = p["tread_depth"], p["tread_width"], p["tread_thickness"]
    created = 0

    # Flight 1: n1-1 treads (tops at riser*1 .. riser*(n1-1)); the landing is step n1.
    for i in range(n1 - 1):
        base = Point3d(o) + u * (i * d) + Vector3d.ZAxis * ((i + 1) * riser - t)
        _add_box(base, u, v, d, w, t, p["layer"])
        created += 1

    # Mid landing: step n1, spans both flights + gap.
    flight1_run = (n1 - 1) * d
    total_width = 2 * w + p["gap"]
    land_base = Point3d(o) + u * flight1_run + Vector3d.ZAxis * (n1 * riser - t)
    _add_box(land_base, u, v, p["landing_depth"], total_width, t, p["layer"])
    created += 1

    # Flight 2: returns toward the origin in the second lane (v offset w + gap).
    # Tread j (j = 0 steps off the landing's near edge) occupies
    # u in [flight1_run - (j+1)*d, flight1_run - j*d]; top = (n1 + j + 1) * riser.
    # The final riser steps onto the upper floor (no tread needed for it).
    for j in range(n2 - 1):
        base = Point3d(o) + u * (flight1_run - (j + 1) * d) \
               + v * (w + p["gap"]) \
               + Vector3d.ZAxis * ((n1 + j + 1) * riser - t)
        _add_box(base, u, v, d, w, t, p["layer"])
        created += 1

    sc.doc.Views.Redraw()
    print("Stair: %d elements, %d risers @ %.1fmm, going %.0fmm, footprint %.0f x %.0f mm" % (
        created, n_r, riser, d,
        max(flight1_run + p["landing_depth"], (n2 - 1) * d + p["landing_depth"]),
        total_width))


build()
