import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Low-poly pass, IN PLACE, for assets whose builders cannot be re-run: the houses (built before the scripts existed)
# and the shuttleport (th_shuttleport.py would re-create the procedural materials the texture library replaced).
# Scripted assets (interiors) get the same counts from th_helpers' defaults instead. Idempotent.
#   - Bevel modifiers -> 1 segment (a chamfer)
#   - untouched UV-sphere hemispheres (dome_bm), vertical cylinders / cones (cyl_bm, also with an add_skirt
#     foundation) and flat rings (annulus_bm) -> rebuilt with the th_helpers segment counts (ring_segs /
#     dome_segs). A mesh whose topology no longer matches the primitive exactly (boolean applied, edited) is left
#     alone (SP_RingWall: its gate cuts are applied).
#   - boolean cutters of the objects in scope get the same treatment, so the cuts stay consistent
#   - UV0 regenerated on every rebuilt mesh (cutters too when they carried UV0: cut faces take the cutter's UVs)
ROOTS = ["ASSET_House1", "ASSET_House2", "ASSET_Shuttleport"]
FIT_TOL = 1e-2


def descendants(o):
    for c in o.children:
        yield c
        yield from descendants(c)


def fit_dome(me):
    """dome_bm topology (UV-sphere upper half, open at the equator) -> (seg, (cx, cy, z, r, sz)) or None."""
    nv, nf = len(me.vertices), len(me.polygons)
    s = next((s for s in range(8, 257, 4) if s * s // 4 + 1 == nv and s * s // 4 == nf), None)
    if s is None:
        return None
    co = [v.co for v in me.vertices]
    x0, x1 = min(c.x for c in co), max(c.x for c in co)
    y0, y1 = min(c.y for c in co), max(c.y for c in co)
    z0, z1 = min(c.z for c in co), max(c.z for c in co)
    cx, cy, r, h = (x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2, z1 - z0
    if r <= 0 or h <= 0 or abs((y1 - y0) / 2 - r) > FIT_TOL * r:
        return None
    if any(abs(((c.x - cx) ** 2 + (c.y - cy) ** 2) / r ** 2 + ((c.z - z0) / h) ** 2 - 1) > FIT_TOL for c in co):
        return None
    return s, (cx, cy, z0, r, h / r)


def fit_cyl(me):
    """cyl_bm topology (vertical cylinder / cone, capped) -> (seg, (cx, cy, z0, z1, r0, r1)) or None."""
    nv, nf = len(me.vertices), len(me.polygons)
    if nv % 2 or nv < 24 or nf != nv // 2 + 2:
        return None
    s = nv // 2
    if sorted(len(p.vertices) for p in me.polygons)[-2:] != [s, s]:
        return None
    zs = sorted(v.co.z for v in me.vertices)
    z0, z1 = zs[0], zs[-1]
    if z1 - z0 < 1e-4:
        return None
    rings = []
    for z in (z0, z1):
        ring = [v.co for v in me.vertices if abs(v.co.z - z) < 1e-4]
        if len(ring) != s:
            return None
        cx, cy = sum(c.x for c in ring) / s, sum(c.y for c in ring) / s
        rs = [math.hypot(c.x - cx, c.y - cy) for c in ring]
        r = sum(rs) / s
        if r <= 0 or max(abs(x - r) for x in rs) > FIT_TOL * r:
            return None
        rings.append((cx, cy, r))
    (bx, by, r0), (tx, ty, r1) = rings
    if math.hypot(bx - tx, by - ty) > FIT_TOL * max(r0, r1):
        return None
    return s, (bx, by, z0, z1, r0, r1)


def rings_at(me, s, levels):
    """For each z level: (cx, cy, sorted distinct radii) of the vertices there, or None if not clean circles."""
    out = []
    for z in levels:
        pts = [v.co for v in me.vertices if abs(v.co.z - z) < 1e-4]
        cx, cy = sum(c.x for c in pts) / len(pts), sum(c.y for c in pts) / len(pts)
        radii = []
        for d in sorted(math.hypot(c.x - cx, c.y - cy) for c in pts):
            if not radii or d - radii[-1][-1] > FIT_TOL * max(d, 1e-6):
                radii.append([d])
            else:
                radii[-1].append(d)
        if any(len(g) != s for g in radii):
            return None
        out.append((cx, cy, [sum(g) / s for g in radii]))
    return out


def fit_annulus(me):
    """annulus_bm topology (flat ring, 2 z levels x 2 radii) -> (seg, (cx, cy, z0, z1, r_in, r_out)) or None."""
    nv, nf = len(me.vertices), len(me.polygons)
    if nv % 4 or nv < 48 or nf != nv:
        return None
    s = nv // 4
    levels = sorted({round(v.co.z, 4) for v in me.vertices})
    if len(levels) != 2:
        return None
    rings = rings_at(me, s, levels)
    if not rings or any(len(r[2]) != 2 for r in rings) or abs(rings[0][2][0] - rings[1][2][0]) > FIT_TOL * rings[0][2][0]:
        return None
    (cx, cy, (ri, ro)), _ = rings
    return s, (cx, cy, levels[0], levels[1], ri, ro)


def fit_skirted_cyl(me):
    """cyl_bm from z = 0 + add_skirt (3 z levels, skirt ring = bottom ring) -> (seg, (cx, cy, z1, r0, r1, depth))."""
    nv, nf = len(me.vertices), len(me.polygons)
    if nv % 3 or nv < 36 or nf != 2 * (nv // 3) + 2:
        return None
    s = nv // 3
    levels = sorted({round(v.co.z, 4) for v in me.vertices})
    if len(levels) != 3 or abs(levels[1]) > 1e-4:
        return None
    rings = rings_at(me, s, levels)
    if not rings or any(len(r[2]) != 1 for r in rings):
        return None
    (cx, cy, (rs,)), (_, _, (r0,)), (_, _, (r1,)) = rings
    if abs(rs - r0) > FIT_TOL * r0:
        return None
    return s, (cx, cy, levels[2], r0, r1, -levels[0])


def rebuild(ob, root):
    """Re-tessellate ob's mesh if it is an untouched dome / cylinder / annulus / skirted cylinder with more segments
    than the target."""
    me = ob.data
    if len({p.material_index for p in me.polygons}) > 1:
        return None
    skirt, bm, kind, s, new = None, None, None, 0, 0
    if fit := fit_dome(me):
        kind, (s, (cx, cy, z, r, sz)) = "dome", fit
        new = dome_segs(r)
        bm = dome_bm(cx, cy, z, r, sz, seg=new) if new < s else None
    elif fit := fit_cyl(me):
        kind, (s, (cx, cy, z0, z1, r0, r1)) = "cyl", fit
        new = ring_segs(max(r0, r1))
        bm = cyl_bm(cx, cy, z0, z1, r0, r1, seg=new) if new < s else None
    elif fit := fit_annulus(me):
        kind, (s, (cx, cy, z0, z1, ri, ro)) = "annulus", fit
        new = ring_segs(ro)
        bm = annulus_bm(cx, cy, z0, z1, ri, ro, seg=new) if new < s else None
    elif fit := fit_skirted_cyl(me):
        kind, (s, (cx, cy, z1, r0, r1, skirt)) = "skirted_cyl", fit
        new = ring_segs(max(r0, r1))
        bm = cyl_bm(cx, cy, 0.0, z1, r0, r1, seg=new) if new < s else None
    if bm is None:
        return None
    if me.users > 1:                                          # never edit a mesh shared with something else
        me = ob.data = me.copy()
    mat_index = me.polygons[0].material_index if me.polygons else 0
    smooth = sum(p.use_smooth for p in me.polygons) * 2 >= len(me.polygons)
    had_uv0 = UV0 in me.uv_layers
    for f in bm.faces:
        f.material_index = mat_index; f.smooth = smooth
    bm.to_mesh(me); bm.free()
    if skirt:
        add_skirt(me, depth=skirt)
    if root is not None and (had_uv0 or ob.parent is not None):
        unwrap_uv0(ob, root)
    return (kind, s, new)


report = {}
for rname in ROOTS:
    root = bpy.data.objects[rname]
    rep = report[rname] = {"bevels": 0, "rebuilt": [], "cutters": [], "tris_before": 0, "tris_after": 0}
    objs = [o for o in descendants(root) if o.type == 'MESH']
    dg = bpy.context.evaluated_depsgraph_get()
    rep["tris_before"] = sum(sum(len(p.vertices) - 2 for p in o.evaluated_get(dg).data.polygons) for o in objs)
    cutters = {}
    for o in objs:
        for m in o.modifiers:
            if m.type == 'BEVEL' and m.segments != 1:
                m.segments = 1; rep["bevels"] += 1
            elif m.type == 'BOOLEAN' and m.object is not None and m.object.type == 'MESH' and m.object not in objs:
                cutters[m.object] = root
        r = rebuild(o, root)
        if r:
            rep["rebuilt"].append([o.name, *r])
    for c, croot in cutters.items():
        r = rebuild(c, croot if UV0 in c.data.uv_layers else None)
        if r:
            rep["cutters"].append([c.name, *r])
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    rep["tris_after"] = sum(sum(len(p.vertices) - 2 for p in o.evaluated_get(dg).data.polygons) for o in objs)
    rep["densest_left"] = sorted(([sum(len(p.vertices) - 2 for p in o.evaluated_get(dg).data.polygons), o.name]
                                  for o in objs), reverse=True)[:6]

result = report
