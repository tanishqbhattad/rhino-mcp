# -*- coding: utf-8 -*-
# rab - Rhino Architect Bridge helper library.
# Auto-deployed to %LOCALAPPDATA%\AIBridge\rab.py by the MCP server and
# auto-imported into every execute_script call. IronPython 2 compatible:
# no f-strings, no type hints, no py3-only stdlib.
#
# Purpose: let the model write 5 lines of intent instead of 50 lines of
# rhinoscriptsyntax boilerplate. All dimensions in model units (usually mm).
#
#   rab.wall((0,0,0), (12000,0,0), height=3000, thickness=200)
#   rab.slab([(0,0),(30000,0),(30000,18000),(0,18000)], thickness=250, z=3600)
#   for pt in rab.grid((0,0), 4, 3, 8400, 8400): rab.column(pt, h=3600)
#   rab.info()


import math

import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc
import System
from Rhino.Geometry import (
    Arc, Brep, Circle, Extrusion, Interval, Line, NurbsCurve, Plane, Point3d,
    Polyline, PolyCurve, Vector3d,
)

__version__ = "1.0"

_TOL = None


def _tol():
    global _TOL
    if _TOL is None:
        _TOL = sc.doc.ModelAbsoluteTolerance or 0.01
    return _TOL


def _p3(p, z=None):
    """Accept (x,y), (x,y,z), Point3d, or list."""
    if isinstance(p, Point3d):
        return Point3d(p.X, p.Y, p.Z if z is None else z)
    x = float(p[0]); y = float(p[1])
    pz = float(p[2]) if (z is None and len(p) > 2) else float(z or 0.0)
    return Point3d(x, y, pz)


def _sid(guid):
    return str(guid)


# --- Layers ---------------------------------------------------------------

def layer(path, color=None):
    """Ensure a layer exists (nested via ::). Returns the path."""
    if not rs.IsLayer(path):
        rs.AddLayer(path)
    if color is not None:
        rs.LayerColor(path, System.Drawing.Color.FromArgb(color[0], color[1], color[2]))
    return path


def _assign(guid, layer_path, name):
    gid = _sid(guid)
    if layer_path:
        layer(layer_path)
        rs.ObjectLayer(gid, layer_path)
    if name:
        rs.ObjectName(gid, name)
    return gid


# --- Creation -------------------------------------------------------------

def box(origin, dx, dy, dz, layer_path=None, name=None):
    """Axis-aligned box from min corner. Returns guid str."""
    o = _p3(origin)
    b = Rhino.Geometry.Box(
        Plane(o, Vector3d.XAxis, Vector3d.YAxis),
        Interval(0, float(dx)), Interval(0, float(dy)), Interval(0, float(dz)))
    return _assign(sc.doc.Objects.AddBrep(b.ToBrep()), layer_path, name)


def wall(start, end, height=3000.0, thickness=200.0, layer_path="Wall", name=None):
    """Straight wall centered on the start-end line. Returns guid str."""
    a = _p3(start); b = _p3(end)
    run = Vector3d(b - a)
    run.Z = 0.0
    length = run.Length
    if length < _tol():
        raise ValueError("wall: start and end coincide")
    u = Vector3d(run); u.Unitize()
    v = Vector3d.CrossProduct(Vector3d.ZAxis, u)
    base = Point3d(a) - v * (float(thickness) * 0.5)
    bx = Rhino.Geometry.Box(
        Plane(base, u, v),
        Interval(0, length), Interval(0, float(thickness)), Interval(0, float(height)))
    return _assign(sc.doc.Objects.AddBrep(bx.ToBrep()), layer_path, name)


def extrude(points, height, z=0.0, layer_path=None, name=None):
    """Closed planar profile (XY point list) extruded vertically. Returns guid str."""
    pts = [_p3(p, z) for p in points]
    if pts[0].DistanceTo(pts[-1]) > _tol():
        pts.append(Point3d(pts[0]))
    pl = Polyline(pts)
    crv = pl.ToNurbsCurve()
    ext = Extrusion.Create(crv, float(height), True)
    if ext is None:
        raise ValueError("extrude: profile is not a valid closed planar curve")
    return _assign(sc.doc.Objects.AddExtrusion(ext), layer_path, name)


def slab(points, thickness=200.0, z=0.0, layer_path="Slab", name=None):
    """Floor plate: profile at elevation z, extruded DOWN by thickness (top = z)."""
    return extrude(points, -float(thickness), z=z, layer_path=layer_path, name=name)


def column(pt, w=400.0, d=400.0, h=3000.0, z=0.0, layer_path="Column", name=None):
    """Rectangular column centered on pt, base at z."""
    p = _p3(pt, z)
    return box((p.X - w * 0.5, p.Y - d * 0.5, p.Z), w, d, h, layer_path, name)


def line(start, end, layer_path=None, name=None):
    return _assign(sc.doc.Objects.AddLine(Line(_p3(start), _p3(end))), layer_path, name)


def grid(origin, nx, ny, sx, sy):
    """Grid points (list of (x, y) tuples), nx columns x ny rows from origin."""
    o = _p3(origin)
    out = []
    for i in range(int(nx)):
        for j in range(int(ny)):
            out.append((o.X + i * float(sx), o.Y + j * float(sy)))
    return out


# --- Query & edit -----------------------------------------------------------

def ids_on(layer_path):
    """Guid strings of all objects on a layer (empty list if no layer)."""
    if not rs.IsLayer(layer_path):
        return []
    got = rs.ObjectsByLayer(layer_path)
    return [_sid(g) for g in (got or [])]


def bbox(ids):
    """((minx,miny,minz),(maxx,maxy,maxz)) over guid strings."""
    bb = rs.BoundingBox(ids)
    if not bb:
        return None
    xs = [p.X for p in bb]; ys = [p.Y for p in bb]; zs = [p.Z for p in bb]
    return ((min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs)))


def move(ids, vec):
    rs.MoveObjects(ids, vec)
    return ids


def copy_to(ids, vec):
    got = rs.CopyObjects(ids, vec)
    return [_sid(g) for g in (got or [])]


def delete(ids):
    return rs.DeleteObjects(ids)


def boolean_diff(a_id, b_id, delete_input=True):
    """Boolean difference a - b with validity checks. Returns list of guid strs."""
    a = rs.coercebrep(a_id); b = rs.coercebrep(b_id)
    if a is None or b is None:
        raise ValueError("boolean_diff: inputs must be Breps/solids")
    if not a.IsValid or not b.IsValid:
        raise ValueError("boolean_diff: invalid input Brep - run validate_objects")
    out = Brep.CreateBooleanDifference([a], [b], _tol())
    if out is None or len(out) == 0:
        raise ValueError("boolean_diff: boolean failed (coplanar faces? oversize the cutter)")
    keep_layer = rs.ObjectLayer(a_id)
    new_ids = [_sid(sc.doc.Objects.AddBrep(br)) for br in out]
    for nid in new_ids:
        rs.ObjectLayer(nid, keep_layer)
    if delete_input:
        rs.DeleteObjects([a_id, b_id])
    return new_ids


# --- Reusable modules -------------------------------------------------------

def use(name):
    """Import a module saved with the write_module MCP tool, hot-reloading it.

    Write a geometry library once, then `mylib = rab.use('mylib')` in every later
    script. Picks up edits without restarting Rhino.
    """
    import sys as _sys
    if name in _sys.modules:
        # `reload` is an IronPython 2 builtin; this file never runs under py3.
        return reload(_sys.modules[name])  # noqa: F821
    __import__(name)
    return _sys.modules[name]


# --- Architectural stdlib ---------------------------------------------------
# Historical/curvilinear work that the structured tools do not cover. All angles
# in degrees, all lengths in model units.

def _frame(origin, along, up):
    """Build a Plane whose X axis runs along the opening and Y axis points up."""
    o = _p3(origin)
    u = Vector3d(float(along[0]), float(along[1]), float(along[2]))
    if u.Length < 1e-9:
        u = Vector3d(1.0, 0.0, 0.0)
    u.Unitize()
    v = Vector3d(float(up[0]), float(up[1]), float(up[2]))
    if v.Length < 1e-9:
        v = Vector3d(0.0, 0.0, 1.0)
    v.Unitize()
    return Plane(o, u, v)


def arch_geometry(span, rise, kind="pointed"):
    """Solve arch centres/radii. Returns dict with the exact construction.

    Two-centred (pointed) arch, half-span s and rise h:
        c = (h*h - s*s) / (2*s)     centre offset from the crown axis
        R = c + s                   radius
    The extrados is CONCENTRIC - same centres, radius R + ring - which is what
    makes archivolt offsets exact. c == 0 gives a semicircle; c == s gives the
    equilateral arch (rise = s*sqrt(3)).
    """
    s = float(span) / 2.0
    kind = (kind or "pointed").lower()
    if s <= 0:
        raise ValueError("arch: span must be positive")

    if kind == "semicircular" or kind == "round":
        h = s
    elif kind == "equilateral":
        h = s * math.sqrt(3.0)
    else:
        h = float(rise)
    if h <= 0:
        raise ValueError("arch: rise must be positive")

    if kind == "segmental" or (kind not in ("segmental",) and h < s - 1e-12 and kind == "auto"):
        # Single centre on the crown axis, below the springing line.
        R = (s * s + h * h) / (2.0 * h)
        return {"kind": "segmental", "half_span": s, "rise": h,
                "centers": [(0.0, h - R)], "radius": R}

    c = (h * h - s * s) / (2.0 * s)
    R = c + s
    resolved = kind
    if kind in ("pointed", "auto"):
        resolved = "semicircular" if abs(c) < 1e-9 else "pointed"
    return {"kind": resolved, "half_span": s, "rise": h,
            "centers": [(c, 0.0), (-c, 0.0)], "radius": R}


def _arc_2d(cx, cy, R, a0, a1):
    """Three points describing an arc, in the local 2D arch frame."""
    am = (a0 + a1) / 2.0
    return ((cx + R * math.cos(a0), cy + R * math.sin(a0)),
            (cx + R * math.cos(am), cy + R * math.sin(am)),
            (cx + R * math.cos(a1), cy + R * math.sin(a1)))


def arch_curve(span, rise, kind="pointed", offset=0.0, origin=(0, 0, 0),
               along=(1, 0, 0), up=(0, 0, 1), springing=0.0):
    """Intrados curve of an arch as a PolyCurve, springing at `springing` above origin.

    offset > 0 gives the concentric extrados (same centres) - use it for archivolt
    orders and voussoir bands.
    """
    g = arch_geometry(span, rise, kind)
    pl = _frame(origin, along, up)
    R = g["radius"] + float(offset)
    s = g["half_span"]
    sp = float(springing)

    def P(xy):
        return pl.PointAt(xy[0], sp + xy[1])

    pc = PolyCurve()
    if g["kind"] == "segmental":
        cx, cy = g["centers"][0]
        # Springing points sit on the intrados; with an offset they move outward.
        half = math.sqrt(max(R * R - (0 - cy) * (0 - cy), 0.0)) if R > abs(cy) else s
        a0 = math.atan2(0 - cy, -half)
        a1 = math.atan2(0 - cy, half)
        p0, pm, p1 = _arc_2d(cx, cy, R, a0, a1)
        pc.Append(Arc(P(p0), P(pm), P(p1)))
    else:
        cx, _ = g["centers"][0]
        h = g["rise"]
        # Left half: from the left springing (angle pi) up to the crown.
        a_end = math.atan2(h, -cx)
        p0, pm, p1 = _arc_2d(cx, 0.0, R, math.pi, a_end)
        pc.Append(Arc(P(p0), P(pm), P(p1)))
        # Right half mirrors about the crown axis.
        a_end2 = math.atan2(h, cx)
        q0, qm, q1 = _arc_2d(-cx, 0.0, R, a_end2, 0.0)
        pc.Append(Arc(P(q0), P(qm), P(q1)))
    return pc


def arch_profile(span, rise, pier=0.0, kind="pointed", ring=None,
                 origin=(0, 0, 0), along=(1, 0, 0), up=(0, 0, 1)):
    """Closed planar profile for an arched opening (ring=None) or a voussoir band.

    The opening profile is jamb -> arch head -> jamb -> sill, ready to extrude
    into a void for boolean_diff.
    """
    g = arch_geometry(span, rise, kind)
    s = g["half_span"]
    pl = _frame(origin, along, up)
    pier = float(pier)

    def P(x, y):
        return pl.PointAt(x, y)

    if ring is None:
        pc = PolyCurve()
        pc.Append(Line(P(-s, 0.0), P(-s, pier)).ToNurbsCurve())
        pc.Append(arch_curve(span, rise, kind, 0.0, origin, along, up, pier))
        pc.Append(Line(P(s, pier), P(s, 0.0)).ToNurbsCurve())
        pc.Append(Line(P(s, 0.0), P(-s, 0.0)).ToNurbsCurve())
        pc.MakeClosed(_tol())
        return pc

    t = float(ring)
    inner = arch_curve(span, rise, kind, 0.0, origin, along, up, pier)
    outer = arch_curve(span, rise, kind, t, origin, along, up, pier)
    pc = PolyCurve()
    pc.Append(inner)
    pc.Append(Line(inner.PointAtEnd, outer.PointAtEnd).ToNurbsCurve())
    rev = outer.Duplicate()
    rev.Reverse()
    pc.Append(rev)
    pc.Append(Line(outer.PointAtStart, inner.PointAtStart).ToNurbsCurve())
    pc.MakeClosed(_tol())
    return pc


def arch(span, rise, depth, pier=0.0, kind="pointed", ring=None,
         origin=(0, 0, 0), along=(1, 0, 0), up=(0, 0, 1),
         centered=True, layer_path=None, name=None):
    """Extrude an arch into a solid.

    ring=None  -> the OPENING solid (use as a boolean cutter for a window/door)
    ring=t     -> the arch band itself (voussoirs/archivolt) of thickness t

    centered=True (default) straddles the frame plane by depth/2 each way, so
    placing the origin on a wall's CENTRELINE cuts cleanly through it. Make depth
    a little larger than the wall thickness - a cutter that stops exactly on the
    face leaves coplanar faces and the boolean fails.

    Example - pointed window void in a 600 thick wall on the y=0 centreline:
        void = rab.arch(3000, 2000, 800, pier=2500, origin=(4000, 0, 0))
        rab.boolean_diff(wall_id, void)
    """
    prof = arch_profile(span, rise, pier, kind, ring, origin, along, up)
    pl = _frame(origin, along, up)
    d = float(depth)
    ext = Extrusion.Create(prof.ToNurbsCurve(), -d, True)
    if ext is None:
        raise ValueError("arch: could not extrude the profile (check span/rise/ring)")
    geo = ext.ToBrep()
    if centered:
        # Extrusion.Create with height -d displaces by +d along the frame normal;
        # shift back by half so the solid straddles the plane.
        geo.Translate(pl.Normal * (-d / 2.0))
    gid = sc.doc.Objects.AddBrep(geo)
    if gid == System.Guid.Empty:
        raise ValueError("arch: failed to add arch solid")
    return _assign(gid, layer_path, name)


def vault_web(springing_a, springing_b, boss, crown_rise, courses=8,
              layer_path="Vault", name=None):
    """One severy (web) of a rib vault, as a lofted masonry-course surface.

    springing_a/b : the two springing points of the wall arch (formeret)
    boss          : the vault's centre point (keystone) - the web dies into it
    crown_rise    : height of the formeret crown above the springing line

    Courses blend from the pointed wall arch at the boundary to a flat fold at the
    boss, so the surface lands EXACTLY on the boss instead of overshooting it -
    the failure mode of a naive Coons patch between the ribs.
    """
    a = _p3(springing_a)
    b = _p3(springing_b)
    bo = _p3(boss)
    n = max(2, int(courses))
    span = a.DistanceTo(b)
    if span < _tol():
        raise ValueError("vault_web: springing points coincide")

    along = Vector3d(b - a)
    along.Z = 0.0
    if along.Length < 1e-9:
        along = Vector3d(b - a)
    along.Unitize()

    mid = Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, (a.Z + b.Z) / 2.0)
    crown = Point3d(mid.X, mid.Y, mid.Z + float(crown_rise))

    sections = []
    for i in range(n + 1):
        t = float(i) / float(n)                 # 0 = wall arch, 1 = boss
        # Course endpoints slide from the springing line toward the boss.
        pa = Point3d(a.X + (bo.X - a.X) * t, a.Y + (bo.Y - a.Y) * t, a.Z + (bo.Z - a.Z) * t)
        pb = Point3d(b.X + (bo.X - b.X) * t, b.Y + (bo.Y - b.Y) * t, b.Z + (bo.Z - b.Z) * t)
        if pa.DistanceTo(pb) < _tol():
            sections.append(NurbsCurve.CreateControlPointCurve([pa, bo, pb], 1))
            continue
        # Apex interpolates from the formeret crown to the boss, so the fold dies
        # out exactly at the keystone.
        apex = Point3d(
            crown.X + (bo.X - crown.X) * t,
            crown.Y + (bo.Y - crown.Y) * t,
            crown.Z + (bo.Z - crown.Z) * t)
        arc = Arc(pa, apex, pb)
        if arc.IsValid:
            sections.append(arc.ToNurbsCurve())
        else:
            sections.append(NurbsCurve.CreateControlPointCurve([pa, apex, pb], 2))

    breps = Brep.CreateFromLoft(sections, Point3d.Unset, Point3d.Unset,
                                Rhino.Geometry.LoftType.Normal, False)
    if not breps or len(breps) == 0:
        raise ValueError("vault_web: loft failed - check the springing points and boss")
    gid = sc.doc.Objects.AddBrep(breps[0])
    return _assign(gid, layer_path, name)


def vault_quadripartite(corner_pts, springing_z, crown_z, courses=8,
                        layer_path="Vault", name=None):
    """Four severies over a rectangular bay. corner_pts = 4 plan corners.

    Returns the list of web ids. The boss sits at the bay centre at crown_z, so
    every web meets it exactly.
    """
    pts = [_p3(p, springing_z) for p in corner_pts]
    if len(pts) != 4:
        raise ValueError("vault_quadripartite: need exactly 4 corners")
    cx = sum([p.X for p in pts]) / 4.0
    cy = sum([p.Y for p in pts]) / 4.0
    boss = Point3d(cx, cy, float(crown_z))
    rise = float(crown_z) - float(springing_z)
    ids = []
    for i in range(4):
        a = pts[i]
        b = pts[(i + 1) % 4]
        nm = None if name is None else ("%s_%d" % (name, i + 1))
        ids.append(vault_web(a, b, boss, rise, courses, layer_path, nm))
    return ids


def rose_window(center, radius, spokes=12, foils=12, ring=0.12, depth=0.3,
                oculus=0.18, layer_path="Tracery", name=None,
                along=(1, 0, 0), up=(0, 0, 1)):
    """Rose window tracery: radial spokes, a foiled outer band and a central oculus.

    Returns the list of created ids. `oculus` is a fraction of `radius`.
    Build the glazing separately, or boolean this out of a wall panel.
    """
    pl = _frame(center, along, up)
    R = float(radius)
    t = float(ring)
    d = float(depth)
    ids = []

    def ring_curve(rad):
        return Circle(pl, _p3(center), rad).ToNurbsCurve()

    def band(r_out, r_in, nm):
        outer = ring_curve(r_out)
        inner = ring_curve(r_in)
        faces = Brep.CreatePlanarBreps([outer, inner], _tol())
        if not faces or len(faces) == 0:
            return None
        solid = faces[0].Faces[0].CreateExtrusion(
            Line(pl.Origin, pl.Origin + pl.Normal * d).ToNurbsCurve(), True)
        if solid is None:
            return None
        gid = sc.doc.Objects.AddBrep(solid)
        return _assign(gid, layer_path, nm)

    outer_id = band(R, R - t, None if name is None else name + "_rim")
    if outer_id:
        ids.append(outer_id)
    r_oc = R * float(oculus)
    oc_id = band(r_oc + t, r_oc, None if name is None else name + "_oculus")
    if oc_id:
        ids.append(oc_id)

    # Radial spokes between the oculus and the rim.
    n = max(3, int(spokes))
    for i in range(n):
        ang = 2.0 * math.pi * i / n
        p0 = pl.PointAt(math.cos(ang) * (r_oc + t), math.sin(ang) * (r_oc + t))
        p1 = pl.PointAt(math.cos(ang) * (R - t), math.sin(ang) * (R - t))
        pipe_axis = Line(p0, p1).ToNurbsCurve()
        pipes = Brep.CreatePipe(pipe_axis, t / 2.0, False, Rhino.Geometry.PipeCapMode.Flat,
                                True, _tol(), _tol())
        if pipes and len(pipes) > 0:
            gid = sc.doc.Objects.AddBrep(pipes[0])
            ids.append(_assign(gid, layer_path, None if name is None else "%s_spoke_%d" % (name, i)))

    # Foils: small circles riding the inside of the rim.
    f = max(0, int(foils))
    if f:
        r_foil = (R - t - (r_oc + t)) * 0.22
        r_ring = R - t - r_foil
        for i in range(f):
            ang = 2.0 * math.pi * i / f + math.pi / f
            cpt = pl.PointAt(math.cos(ang) * r_ring, math.sin(ang) * r_ring)
            fp = Plane(cpt, pl.Normal)
            c_out = Circle(fp, r_foil).ToNurbsCurve()
            c_in = Circle(fp, max(r_foil - t / 2.0, r_foil * 0.35)).ToNurbsCurve()
            faces = Brep.CreatePlanarBreps([c_out, c_in], _tol())
            if not faces or len(faces) == 0:
                continue
            solid = faces[0].Faces[0].CreateExtrusion(
                Line(cpt, cpt + pl.Normal * d).ToNurbsCurve(), True)
            if solid is None:
                continue
            gid = sc.doc.Objects.AddBrep(solid)
            ids.append(_assign(gid, layer_path, None if name is None else "%s_foil_%d" % (name, i)))
    return ids


# Named moulding profiles, as (u, v) offsets from the arris, unit scale.
MOULDING_PROFILES = {
    "roll":     [(0.0, 0.0), (0.5, 0.0), (0.5, 0.35), (0.35, 0.5), (0.0, 0.5)],
    "keel":     [(0.0, 0.0), (0.5, 0.0), (0.5, 0.3), (0.25, 0.62), (0.0, 0.3)],
    "cavetto":  [(0.0, 0.0), (0.5, 0.0), (0.5, 0.5), (0.18, 0.5), (0.0, 0.32)],
    "ovolo":    [(0.0, 0.0), (0.5, 0.0), (0.5, 0.18), (0.32, 0.5), (0.0, 0.5)],
    "fillet":   [(0.0, 0.0), (0.5, 0.0), (0.5, 0.5), (0.0, 0.5)],
    "ogee":     [(0.0, 0.0), (0.5, 0.0), (0.5, 0.25), (0.25, 0.3), (0.25, 0.5), (0.0, 0.5)],
    "scotia":   [(0.0, 0.0), (0.5, 0.0), (0.5, 0.5), (0.3, 0.28), (0.0, 0.5)],
}


def sweep_profile(rail_id, profile="roll", scale=1.0, layer_path=None, name=None):
    """Sweep a named moulding profile along a rail curve. Returns the new id.

    Profiles: roll, keel, cavetto, ovolo, fillet, ogee, scotia (see MOULDING_PROFILES).
    The profile is built in the rail's start frame, so it follows curvature correctly.
    """
    rail = rs.coercecurve(rail_id)
    if rail is None:
        raise ValueError("sweep_profile: rail_id is not a curve")
    key = (profile or "roll").lower()
    pts2d = MOULDING_PROFILES.get(key)
    if pts2d is None:
        raise ValueError("sweep_profile: unknown profile '%s'. Options: %s"
                         % (profile, ", ".join(sorted(MOULDING_PROFILES.keys()))))

    ok, frame = rail.PerpendicularFrameAt(rail.Domain.T0)
    if not ok:
        frame = Plane(rail.PointAtStart, rail.TangentAtStart)
    s = float(scale)
    pts = [frame.PointAt(u * s, v * s) for (u, v) in pts2d]
    pts.append(pts[0])
    prof = Polyline(pts).ToNurbsCurve()

    sweep = Rhino.Geometry.SweepOneRail()
    sweep.ClosedSweep = rail.IsClosed
    sweep.SetToRoadlikeTop()
    breps = sweep.PerformSweep(rail, prof)
    if not breps or len(breps) == 0:
        raise ValueError("sweep_profile: sweep failed")
    solid = breps[0].CapPlanarHoles(_tol()) or breps[0]
    gid = sc.doc.Objects.AddBrep(solid)
    return _assign(gid, layer_path, name)


def info():
    """Print a one-line scene summary (object count by layer)."""
    counts = {}
    for o in sc.doc.Objects:
        lp = sc.doc.Layers[o.Attributes.LayerIndex].FullPath
        counts[lp] = counts.get(lp, 0) + 1
    total = sum(counts.values())
    parts = ["%s:%d" % (k, counts[k]) for k in sorted(counts)]
    print("rab.info: %d objects | %s" % (total, ", ".join(parts)))
    return counts
