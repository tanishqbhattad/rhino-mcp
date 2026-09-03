# -*- coding: utf-8 -*-
# rab - Rhino Architect Bridge helper library.
# Auto-deployed to %LOCALAPPDATA%\AIBridge\rab.py by the MCP server and
# auto-imported into every execute_script call. IronPython 2 compatible:
# no f-strings, no type hints, no py3-only stdlib.
#
# Purpose: let the model write 5 lines of intent instead of 50 lines of
# rhinoscriptsyntax boilerplate.
#
# START HERE:  rab.help()          - the whole API with signatures + your units
#              rab.help('arch')    - full docs for one function
#
# UNITS: every length is in MODEL UNITS, never auto-converted. The examples below
# are written with rab.m() so they are correct in ANY document; check
# rab.units() (or ping.unit_system) before hard-coding numbers.
#
#   rab.wall((0,0,0), (rab.m(12),0,0), height=rab.m(3), thickness=rab.m(0.2))
#   rab.slab([(0,0),(rab.m(30),0),(rab.m(30),rab.m(18)),(0,rab.m(18))], thickness=rab.m(0.25))
#   for pt in rab.grid((0,0), 4, 3, rab.m(8.4), rab.m(8.4)): rab.column(pt, h=rab.m(3.6))
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

def _all_objects(include_hidden=True):
    """Every object in the document, INCLUDING those on hidden layers.

    The default enumerator skips hidden objects, so rab under-reported by 21 on a
    942-object model while the C# side (which sets HiddenObjects) was right.
    """
    st = Rhino.DocObjects.ObjectEnumeratorSettings()
    st.NormalObjects = True
    st.LockedObjects = True
    st.HiddenObjects = bool(include_hidden)
    st.IncludeLights = False
    st.IncludeGrips = False
    st.DeletedObjects = False
    return list(sc.doc.Objects.GetObjectList(st))


def ids_on(layer_path, include_hidden=True, include_sublayers=True):
    """Guid strings of objects on a layer, including hidden ones and sublayers.

    include_sublayers=True matches the by_layer: selector on the server side, so
    'Building' picks up 'Building::Walls' too.
    """
    target = layer_path
    prefix = layer_path + "::"
    out = []
    for o in _all_objects(include_hidden):
        try:
            lp = sc.doc.Layers[o.Attributes.LayerIndex].FullPath
        except Exception:
            continue
        if lp == target or (include_sublayers and lp.startswith(prefix)):
            out.append(_sid(o.Id))
    return out


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


def orient(brep):
    """Force a closed Brep's normals OUTWARD. Returns the same brep, flipped if needed.

    THE TRAP THIS EXISTS FOR: lofted-and-capped Breps frequently come back with
    INWARD normals. A boolean difference against an inverted solid ADDS material
    instead of removing it, so window recesses render as bulges. It is especially
    nasty because sc.doc.Objects.AddBrep() re-orients on insert - so auditing the
    document afterwards reports zero inverted solids while the bug is live. The
    corruption exists only in the in-memory Brep, BEFORE it is added.

    Always call this on a Brep you built in memory, before booleaning or adding.
    """
    if brep is None:
        return brep
    try:
        if brep.SolidOrientation == Rhino.Geometry.BrepSolidOrientation.Inward:
            brep.Flip()
    except Exception:
        pass
    return brep


def is_inverted(brep):
    """True when a closed Brep's normals point inward (see orient())."""
    try:
        return brep is not None and \
            brep.SolidOrientation == Rhino.Geometry.BrepSolidOrientation.Inward
    except Exception:
        return False


def periodic_curve(points, degree=3):
    """Closed PERIODIC (smooth-seam) curve through points.

    THE TRAP THIS EXISTS FOR: Curve.CreateInterpolatedCurve(pts, 3,
    CurveKnotStyle.ChordPeriodic) returns an OPEN, non-periodic curve
    (IsClosed False, IsPeriodic False). Lofting those produces a skin split down
    the seam and a "solid" that silently is not closed. NurbsCurve.Create(True,
    degree, pts) is the call that actually gives a periodic curve.

    Pass the points ONCE - do not repeat the first point at the end.
    """
    pts = [_p3(p) for p in points]
    if len(pts) > 2 and pts[0].DistanceTo(pts[-1]) < _tol():
        pts = pts[:-1]                      # a repeated seam point breaks periodicity
    if len(pts) < 3:
        raise ValueError("periodic_curve: need at least 3 distinct points")
    arr = System.Array[Point3d](pts)
    crv = NurbsCurve.Create(True, int(degree), arr)
    if crv is None:
        raise ValueError("periodic_curve: NurbsCurve.Create returned None (duplicate or collinear points?)")
    if not crv.IsClosed:
        raise ValueError("periodic_curve: result is not closed - check for duplicate points")
    return crv


def cap(curves, layer_path=None, name=None, add=False):
    """Planar Brep(s) from one or more closed planar curves.

    THE TRAP THIS EXISTS FOR: Brep.CreatePlanarBreps overload resolution needs an
    explicit System.Array[Curve]. Handing it a single Curve silently returns zero
    results instead of raising, so the failure looks like "the geometry was wrong".
    """
    if not isinstance(curves, (list, tuple)):
        curves = [curves]
    resolved = []
    for c in curves:
        cc = c if isinstance(c, Rhino.Geometry.Curve) else rs.coercecurve(c)
        if cc is None:
            raise ValueError("cap: not a curve: %s" % (c,))
        resolved.append(cc)
    arr = System.Array[Rhino.Geometry.Curve](resolved)
    breps = Brep.CreatePlanarBreps(arr, _tol())
    if not breps or len(breps) == 0:
        raise ValueError("cap: no planar Brep produced - are the curves closed, planar and coplanar?")
    if not add:
        return list(breps)
    ids = []
    for b in breps:
        ids.append(_assign(sc.doc.Objects.AddBrep(orient(b)), layer_path, name))
    return ids


def assign(ids, layer_path=None, name=None, color=None):
    """Set layer / name / colour on MANY objects in one pass.

    Doing this per object costs two document transactions each; on a 335-object
    rebuild that is 670 transactions. Returns the ids for chaining.
    """
    if isinstance(ids, str):
        ids = [ids]
    ids = [str(i) for i in ids]
    if not ids:
        return ids
    if layer_path:
        layer(layer_path)
        rs.ObjectLayer(ids, layer_path)          # rhinoscriptsyntax accepts a list
    if color is not None:
        rs.ObjectColor(ids, System.Drawing.Color.FromArgb(color[0], color[1], color[2]))
    if name:
        # Names must be unique-ish per object; suffix when assigning in bulk.
        if len(ids) == 1:
            rs.ObjectName(ids[0], name)
        else:
            for i, oid in enumerate(ids):
                rs.ObjectName(oid, "%s_%d" % (name, i + 1))
    return ids


def boolean_diff(a_id, b_id, delete_input=True):
    """Boolean difference a - b, with validity AND ORIENTATION checks.

    Orientation is the check that actually matters here: differencing against an
    inverted solid ADDS material (recesses become bulges). Both operands are
    oriented outward before the operation.
    """
    a = rs.coercebrep(a_id); b = rs.coercebrep(b_id)
    if a is None or b is None:
        raise ValueError("boolean_diff: inputs must be Breps/solids")
    if not a.IsValid or not b.IsValid:
        raise ValueError("boolean_diff: invalid input Brep - run validate_objects")
    flipped = []
    if is_inverted(a):
        flipped.append("a")
    if is_inverted(b):
        flipped.append("b")
    orient(a); orient(b)
    if flipped:
        print("[rab] boolean_diff: re-oriented inverted operand(s) %s before subtracting"
              % ", ".join(flipped))
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
    """RETURNS A CURVE (PolyCurve), never a GUID - nothing is added to the document.

    Closed planar profile for an arched opening (ring=None) or a voussoir band.

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


def annulus_wall(center, r_in, r_out, z0, z1, a0=0.0, a1=360.0,
                 layer_path=None, name=None):
    """A curved wall segment: the solid between two radii, swept a0->a1 degrees.

    The apse/chevet primitive. r_in=0 gives a solid pier rather than failing - a
    zero-radius inner circle is a degenerate curve, not an annulus.
    """
    c = _p3(center)
    z0 = float(z0); z1 = float(z1)
    if abs(z1 - z0) < _tol():
        raise ValueError("annulus_wall: z0 and z1 are equal")
    pl = Plane(Point3d(c.X, c.Y, min(z0, z1)), Vector3d.ZAxis)
    full = abs(float(a1) - float(a0)) >= 359.999

    if full:
        outer = Circle(pl, float(r_out)).ToNurbsCurve()
        curves = [outer]
        if float(r_in) > _tol():
            curves.append(Circle(pl, float(r_in)).ToNurbsCurve())
        faces = Brep.CreatePlanarBreps(System.Array[Rhino.Geometry.Curve](curves), _tol())
    else:
        a0r = math.radians(float(a0)); a1r = math.radians(float(a1))
        pts = []
        steps = max(8, int(abs(a1r - a0r) / 0.12))
        for i in range(steps + 1):
            t = a0r + (a1r - a0r) * i / float(steps)
            pts.append(pl.PointAt(math.cos(t) * float(r_out), math.sin(t) * float(r_out)))
        inner_r = max(float(r_in), 0.0)
        for i in range(steps, -1, -1):
            t = a0r + (a1r - a0r) * i / float(steps)
            pts.append(pl.PointAt(math.cos(t) * inner_r, math.sin(t) * inner_r))
        pts.append(Point3d(pts[0]))
        loop = Polyline(pts).ToNurbsCurve()
        faces = Brep.CreatePlanarBreps(System.Array[Rhino.Geometry.Curve]([loop]), _tol())

    if not faces or len(faces) == 0:
        raise ValueError("annulus_wall: could not build the base face")
    solid = faces[0].Faces[0].CreateExtrusion(
        Line(Point3d(c.X, c.Y, min(z0, z1)),
             Point3d(c.X, c.Y, max(z0, z1))).ToNurbsCurve(), True)
    if solid is None:
        raise ValueError("annulus_wall: extrusion failed")
    return _assign(sc.doc.Objects.AddBrep(orient(solid)), layer_path, name)


def buttress_pier(base, width, depth, height, setbacks=None, layer_path="Buttress", name=None):
    """Stepped buttress pier. setbacks = [(at_height, inset), ...] applied cumulatively."""
    b = _p3(base)
    setbacks = sorted(setbacks or [], key=lambda s: s[0])
    ids = []
    z = b.Z
    w = float(width); d = float(depth); inset = 0.0
    stages = list(setbacks) + [(float(height) + b.Z, 0.0)]
    for (at_h, step) in stages:
        top = min(float(at_h), b.Z + float(height))
        if top - z < _tol():
            continue
        ids.append(box((b.X - w / 2.0 + inset, b.Y - d / 2.0 + inset, z),
                       w - 2 * inset, d - 2 * inset, top - z, layer_path, name))
        z = top
        inset += float(step)
        if z >= b.Z + float(height) - _tol():
            break
    return ids


def spire(center, base_radius, height, sides=8, stages=1, layer_path="Spire", name=None):
    """Tapered polygonal spire. stages>1 gives a stepped silhouette rather than one cone."""
    c = _p3(center)
    ids = []
    n = max(3, int(sides))
    for s in range(max(1, int(stages))):
        f0 = s / float(stages)
        f1 = (s + 1) / float(stages)
        r0 = float(base_radius) * (1.0 - f0)
        r1 = float(base_radius) * (1.0 - f1)
        z0 = c.Z + float(height) * f0
        z1 = c.Z + float(height) * f1
        if r0 < _tol():
            break

        def ring(r, z):
            pts = []
            for i in range(n):
                a = 2.0 * math.pi * i / n
                pts.append(Point3d(c.X + r * math.cos(a), c.Y + r * math.sin(a), z))
            pts.append(Point3d(pts[0]))
            return Polyline(pts).ToNurbsCurve()

        if r1 < _tol():
            # Final taper to a point: loft to a tiny ring instead of a degenerate apex,
            # because a true apex makes the result un-booleanable.
            r1 = max(float(base_radius) * 0.02, _tol() * 10)
        breps = Brep.CreateFromLoft([ring(r0, z0), ring(r1, z1)],
                                    Point3d.Unset, Point3d.Unset,
                                    Rhino.Geometry.LoftType.Straight, False)
        if not breps or len(breps) == 0:
            continue
        solid = breps[0].CapPlanarHoles(_tol()) or breps[0]
        ids.append(_assign(sc.doc.Objects.AddBrep(orient(solid)), layer_path, name))
    return ids


def gable_roof(a, b, width, ridge_height, eaves_z, layer_path="Roof", name=None):
    """Gable roof over a rectangular bay: a->b is the ridge line in plan."""
    pa = _p3(a); pb = _p3(b)
    run = Vector3d(pb - pa); run.Z = 0.0
    if run.Length < _tol():
        raise ValueError("gable_roof: a and b coincide")
    u = Vector3d(run); u.Unitize()
    v = Vector3d.CrossProduct(Vector3d.ZAxis, u)
    hw = float(width) / 2.0
    ez = float(eaves_z); rz = float(ridge_height)
    p0 = Point3d(pa.X, pa.Y, ez) - v * hw
    p1 = Point3d(pa.X, pa.Y, ez) + v * hw
    q0 = Point3d(pb.X, pb.Y, ez) - v * hw
    q1 = Point3d(pb.X, pb.Y, ez) + v * hw
    ra = Point3d(pa.X, pa.Y, rz)
    rb = Point3d(pb.X, pb.Y, rz)
    section = Polyline([p0, ra, p1, Point3d(p1), Point3d(p0)]).ToNurbsCurve()
    section2 = Polyline([q0, rb, q1, Point3d(q1), Point3d(q0)]).ToNurbsCurve()
    breps = Brep.CreateFromLoft([section, section2], Point3d.Unset, Point3d.Unset,
                                Rhino.Geometry.LoftType.Straight, False)
    if not breps or len(breps) == 0:
        raise ValueError("gable_roof: loft failed")
    solid = breps[0].CapPlanarHoles(_tol()) or breps[0]
    return _assign(sc.doc.Objects.AddBrep(orient(solid)), layer_path, name)


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


def wall_profile(plane, outline, holes=None, thickness=200.0,
                 layer_path=None, name=None):
    """A wall panel with openings, built from PLANE-RELATIVE (u, v) coordinates.

    plane     : Rhino.Geometry.Plane - the wall's face plane. u runs along the wall,
                v runs up. Use rab.plane_from_wall(a, b) if you have two points.
    outline   : [(u, v), ...] closed loop of the panel face
    holes     : [[(u, v), ...], ...] openings, each a closed loop
    thickness : extruded along the plane NORMAL

    THE TRAP THIS EXISTS FOR: Extrusion.AddInnerProfile refuses inner loops when the
    profile plane is oriented one way but not the other - it worked on XZ-plane walls
    and failed on every YZ-plane wall (9/9 in one session). That is RhinoCommon
    profile-plane handedness, not something callers should have to know. This tries
    the Extrusion path and silently falls back to CreatePlanarBreps +
    CreateFromOffsetFace, which produced a closed solid every time.
    """
    holes = holes or []

    def _loop(uv):
        pts = [plane.PointAt(float(u), float(v)) for (u, v) in uv]
        if pts[0].DistanceTo(pts[-1]) > _tol():
            pts.append(Point3d(pts[0]))
        return Polyline(pts).ToNurbsCurve()

    outer = _loop(outline)
    inners = [_loop(h) for h in holes]
    if not outer.IsClosed:
        raise ValueError("wall_profile: outline is not closed")
    t = float(thickness)

    # Attempt 1: Extrusion with inner profiles (cheap and light).
    try:
        ext = Extrusion.Create(outer, -t, True)
        if ext is not None:
            ok = True
            for h in inners:
                if not ext.AddInnerProfile(h):
                    ok = False
                    break
            if ok:
                brep = ext.ToBrep()
                if brep is not None and brep.IsSolid:
                    return _assign(sc.doc.Objects.AddBrep(orient(brep)), layer_path, name)
    except Exception:
        pass

    # Attempt 2: planar face with holes, then thicken. Handedness-proof.
    curves = [outer] + inners
    faces = Brep.CreatePlanarBreps(System.Array[Rhino.Geometry.Curve](curves), _tol())
    if not faces or len(faces) == 0:
        raise ValueError("wall_profile: could not build a planar face - check the loops "
                         "are closed, planar and that holes lie inside the outline")
    face = faces[0]
    solid = face.Faces[0].CreateExtrusion(
        Line(plane.Origin, plane.Origin - plane.Normal * t).ToNurbsCurve(), True)
    if solid is None:
        raise ValueError("wall_profile: extrusion of the planar face failed")
    return _assign(sc.doc.Objects.AddBrep(orient(solid)), layer_path, name)


def plane_from_wall(start, end, up=(0, 0, 1)):
    """Plane whose X runs start->end (in plan) and Y points up.

    Feed this to wall_profile so its (u, v) coordinates mean "along the wall" and
    "up the wall" regardless of which way the wall faces.
    """
    a = _p3(start)
    b = _p3(end)
    along = [b.X - a.X, b.Y - a.Y, 0.0]
    if abs(along[0]) < 1e-9 and abs(along[1]) < 1e-9:
        along = [1.0, 0.0, 0.0]
    return _frame(a, along, up)


def mirror_y(ids, y=0.0, copy=True):
    """Mirror objects about the plane y = <y>. Halves the script for symmetric buildings."""
    ids = [str(i) for i in (ids if isinstance(ids, (list, tuple)) else [ids])]
    xf = Rhino.Geometry.Transform.Mirror(Plane(Point3d(0, float(y), 0), Vector3d.YAxis))
    out = []
    for oid in ids:
        nid = sc.doc.Objects.Transform(System.Guid(oid), xf, not copy)
        if nid != System.Guid.Empty:
            out.append(_sid(nid))
    return out


def mirror_x(ids, x=0.0, copy=True):
    """Mirror objects about the plane x = <x>."""
    ids = [str(i) for i in (ids if isinstance(ids, (list, tuple)) else [ids])]
    xf = Rhino.Geometry.Transform.Mirror(Plane(Point3d(float(x), 0, 0), Vector3d.XAxis))
    out = []
    for oid in ids:
        nid = sc.doc.Objects.Transform(System.Guid(oid), xf, not copy)
        if nid != System.Guid.Empty:
            out.append(_sid(nid))
    return out


def array_x(ids, step, n):
    """Copy objects n-1 times along X at `step` spacing. Returns ALL ids (originals first)."""
    ids = [str(i) for i in (ids if isinstance(ids, (list, tuple)) else [ids])]
    out = list(ids)
    for i in range(1, int(n)):
        out.extend(copy_to(ids, [float(step) * i, 0, 0]))
    return out


def radial(ids, center, angles_deg):
    """Copy objects around a vertical axis at the given angles (degrees)."""
    ids = [str(i) for i in (ids if isinstance(ids, (list, tuple)) else [ids])]
    c = _p3(center)
    out = []
    for a in angles_deg:
        xf = Rhino.Geometry.Transform.Rotation(math.radians(float(a)), Vector3d.ZAxis, c)
        for oid in ids:
            nid = sc.doc.Objects.Transform(System.Guid(oid), xf, False)
            if nid != System.Guid.Empty:
                out.append(_sid(nid))
    return out


# --- Discoverability --------------------------------------------------------

_HELP_GROUPS = [
    ("create",   ["box", "wall", "slab", "column", "line", "extrude", "grid"]),
    ("historic", ["arch", "arch_geometry", "arch_curve", "arch_profile",
                  "vault_quadripartite", "vault_web", "rose_window", "sweep_profile"]),
    ("curves",   ["periodic_curve", "cap"]),
    ("edit",     ["move", "copy_to", "delete", "boolean_diff", "orient", "is_inverted"]),
    ("query",    ["ids_on", "bbox", "info", "units"]),
    ("organise", ["layer", "assign"]),
    ("modules",  ["use"]),
]


def _signature(fn, fname):
    try:
        import inspect
        spec = inspect.getargspec(fn)
        args = list(spec.args)
        defaults = list(spec.defaults or ())
        n_req = len(args) - len(defaults)
        parts = []
        for i, a in enumerate(args):
            if i < n_req:
                parts.append(a)
            else:
                parts.append("%s=%r" % (a, defaults[i - n_req]))
        return "%s(%s)" % (fname, ", ".join(parts))
    except Exception:
        return "%s(...)" % fname


def doc(name):
    """Print the full signature and docstring of one rab function."""
    fn = globals().get(name)
    if fn is None or not callable(fn):
        print("[rab] no function named '%s'. Run rab.help() for the list." % name)
        return None
    print(_signature(fn, name))
    d = (fn.__doc__ or "(no docstring)").strip()
    for line in d.split("\n"):
        print("    " + line.rstrip())
    return None


def help(name=None):
    """List the rab API with signatures, or rab.help('wall') for one function.

    Start here. The library is not discoverable through dir() alone, and the
    numbers in these examples depend on the document's unit system - which this
    prints at the top so you cannot mistake metres for millimetres.
    """
    if name:
        return doc(name)
    u = units()
    print("rab %s  |  document units: %s  |  tolerance: %s"
          % (__version__, u["unit_system"], u["tolerance"]))
    print("ALL LENGTHS ARE IN MODEL UNITS - a wall 3.0 high in metres is 3000 in millimetres.")
    print("")
    for group, names in _HELP_GROUPS:
        print("[%s]" % group)
        for fname in names:
            fn = globals().get(fname)
            if fn is None or not callable(fn):
                continue
            first = ((fn.__doc__ or "").strip().split("\n") or [""])[0]
            print("  %-58s %s" % (_signature(fn, fname), first[:60]))
        print("")
    print("rab.help('arch') for full docs on one function.")
    return None


def units():
    """Current document unit system, tolerance, and a metres->model-units factor."""
    try:
        us = sc.doc.ModelUnitSystem
        name = str(us)
    except Exception:
        name = "Unknown"
    per_metre = {
        "Millimeters": 1000.0, "Centimeters": 100.0, "Meters": 1.0,
        "Inches": 39.3701, "Feet": 3.28084,
    }.get(name, 1.0)
    return {"unit_system": name, "tolerance": _tol(), "per_metre": per_metre}


def m(metres):
    """Convert metres to model units. rab.m(3) is 3000 in a mm doc, 3.0 in a metre doc.

    Use this instead of hard-coding numbers when you do not control the document.
    """
    return float(metres) * units()["per_metre"]


def info():
    """Print a scene summary by layer. Counts hidden objects too (and says so)."""
    counts = {}
    hidden = 0
    for o in _all_objects(True):
        try:
            lp = sc.doc.Layers[o.Attributes.LayerIndex].FullPath
        except Exception:
            lp = "(unknown)"
        counts[lp] = counts.get(lp, 0) + 1
        try:
            if not o.Visible:
                hidden += 1
        except Exception:
            pass
    total = sum(counts.values())
    parts = ["%s:%d" % (k, counts[k]) for k in sorted(counts)]
    suffix = "" if hidden == 0 else "  (+%d hidden)" % hidden
    print("rab.info: %d objects%s | %s" % (total, suffix, ", ".join(parts)))
    return counts
