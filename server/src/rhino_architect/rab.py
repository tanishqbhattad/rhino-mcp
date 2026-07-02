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


import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc
import System
from Rhino.Geometry import (
    Brep, Extrusion, Interval, Line, Plane, Point3d, Polyline, Vector3d,
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


# ── Layers ───────────────────────────────────────────────────────────────

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


# ── Creation ─────────────────────────────────────────────────────────────

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


# ── Query & edit ─────────────────────────────────────────────────────────

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
