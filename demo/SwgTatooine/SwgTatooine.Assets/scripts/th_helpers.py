import bpy, bmesh, math
from mathutils import Vector, Matrix

scene = bpy.context.scene


def get_col(name, clear=True):
    c = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    if c.name not in scene.collection.children:
        scene.collection.children.link(c)
    if clear:
        for o in list(c.objects):
            bpy.data.objects.remove(o, do_unlink=True)
    return c


def simple_mat(name):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    try:
        m.use_nodes = True
    except Exception:
        pass
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial'); out.location = (600, 0)
    b = nt.nodes.new('ShaderNodeBsdfPrincipled'); b.location = (300, 0)
    nt.links.new(b.outputs['BSDF'], out.inputs['Surface'])
    return m, nt, b


def sock(sockets, name, typ=None):
    for s in sockets:
        if s.name == name and (typ is None or s.type == typ):
            return s
    raise KeyError(name)


def ramp(nt, fac, stops, loc=(0, 0)):
    r = nt.nodes.new('ShaderNodeValToRGB'); r.location = loc
    els = r.color_ramp.elements
    els[0].position, els[0].color = stops[0][0], (*stops[0][1], 1)
    els[1].position, els[1].color = stops[1][0], (*stops[1][1], 1)
    for p, c in stops[2:]:
        e = els.new(p); e.color = (*c, 1)
    nt.links.new(fac, r.inputs['Fac'])
    return r


# ---------- bmesh builders ----------
def box_bm(x0, x1, y0, y1, z0, z1, ins=(0, 0, 0, 0)):
    """Box with optional top-face inset (left -x, right +x, front -y, back +y).
    Face order: bottom, top, front(-y), right(+x), back(+y), left(-x)."""
    l, r, f, bk = ins
    bm = bmesh.new()
    vb = [bm.verts.new(p) for p in [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0)]]
    vt = [bm.verts.new(p) for p in [(x0+l, y0+f, z1), (x1-r, y0+f, z1), (x1-r, y1-bk, z1), (x0+l, y1-bk, z1)]]
    bm.faces.new(vb[::-1]); bm.faces.new(vt)
    for i in range(4):
        j = (i + 1) % 4
        bm.faces.new((vb[i], vb[j], vt[j], vt[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def prism(pts, a0, a1, plane='XZ'):
    """Extrude a 2D polygon. plane 'XZ': pts=(x,z), extruded along y in [a0,a1];
    plane 'YZ': pts=(y,z), extruded along x in [a0,a1]."""
    bm = bmesh.new()
    if plane == 'XZ':
        mk3 = lambda p, a: (p[0], a, p[1])
    else:
        mk3 = lambda p, a: (a, p[0], p[1])
    vf = [bm.verts.new(mk3(p, a0)) for p in pts]
    vk = [bm.verts.new(mk3(p, a1)) for p in pts]
    bm.faces.new(vf); bm.faces.new(vk[::-1])
    n = len(pts)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((vf[i], vf[j], vk[j], vk[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def arc(cx, zs, r, a0, a1, n=24):
    return [(cx + r*math.cos(a0 + (a1-a0)*i/n), zs + r*math.sin(a0 + (a1-a0)*i/n)) for i in range(n+1)]


def arch_pts(cx, w, zb, zs):
    r = w / 2
    return [(cx + r, zb)] + arc(cx, zs, r, 0, math.pi) + [(cx - r, zb)]


def surround_pts(cx, R, r, zb, zs):
    return [(cx + R, zb)] + arc(cx, zs, R, 0, math.pi) + [(cx - R, zb), (cx - r, zb)] \
        + arc(cx, zs, r, math.pi, 0) + [(cx + r, zb)]


LOWPOLY_EDGE = 0.35   # target edge length (m) around curved primitives: segment counts follow the radius


def ring_segs(r, lo=8, hi=64):
    """Even segment count giving ~LOWPOLY_EDGE edges around a circle of radius r, clamped to [lo, hi]."""
    n = int(math.ceil(2 * math.pi * r / LOWPOLY_EDGE))
    return max(lo, min(hi, n + n % 2))


def cyl_bm(cx, cy, z0, z1, r0, r1=None, seg=None):
    seg = seg or ring_segs(max(r0, r0 if r1 is None else r1))
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r0,
                          radius2=r0 if r1 is None else r1, depth=z1 - z0)
    bmesh.ops.translate(bm, verts=bm.verts, vec=(cx, cy, (z0 + z1) / 2))
    return bm


def annulus_bm(cx, cy, z0, z1, r_in, r_out, seg=None):
    seg = seg or ring_segs(r_out)
    bm = bmesh.new()
    rings = []
    for z in (z0, z1):
        for r in (r_in, r_out):
            rings.append([bm.verts.new((cx + r*math.cos(2*math.pi*i/seg), cy + r*math.sin(2*math.pi*i/seg), z))
                          for i in range(seg)])
    bi, bo, ti, to = rings
    for i in range(seg):
        j = (i + 1) % seg
        bm.faces.new((bi[i], bi[j], bo[j], bo[i]))
        bm.faces.new((ti[i], to[i], to[j], ti[j]))
        bm.faces.new((bo[i], bo[j], to[j], to[i]))
        bm.faces.new((bi[i], ti[i], ti[j], bi[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def dome_segs(r):
    """Multiple of 4 (the equator ring must exist for the hemisphere cut): 12..24 around."""
    n = ring_segs(r, 12, 24)
    return n + (-n) % 4


def dome_bm(cx, cy, z, r, sz, seg=None):
    seg = seg or dome_segs(r)
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=seg // 2, radius=r)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.z < -1e-4], context='VERTS')
    bmesh.ops.scale(bm, verts=bm.verts, vec=(1, 1, sz))
    bmesh.ops.translate(bm, verts=bm.verts, vec=(cx, cy, z))
    return bm


def hcyl_bm(cx, cy, cz, r, depth, dir_xy, seg=None, r_end=None):
    """Horizontal cylinder with axis along dir_xy, centred at (cx,cy,cz)."""
    seg = seg or ring_segs(r, 8, 32)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r,
                          radius2=r if r_end is None else r_end, depth=depth)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(-90), 3, 'X'))
    ang = math.atan2(dir_xy[1], dir_xy[0]) - math.pi / 2
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(ang, 3, 'Z'))
    bmesh.ops.translate(bm, verts=bm.verts, vec=(cx, cy, cz))
    return bm


def xform(bm, rot_z=0.0, rot_x=0.0, rot_y=0.0, pivot=(0, 0, 0), move=(0, 0, 0)):
    for ang, ax in ((rot_x, 'X'), (rot_y, 'Y'), (rot_z, 'Z')):
        if ang:
            bmesh.ops.rotate(bm, verts=bm.verts, cent=pivot, matrix=Matrix.Rotation(ang, 3, ax))
    bmesh.ops.translate(bm, verts=bm.verts, vec=move)
    return bm


def mk(name, bm, mats, col, offset=(0, 0, 0), bevel=0.06, segs=1, smooth=True):   # segs 1: a single chamfer (low poly)
    if not isinstance(mats, (list, tuple)):
        mats = [mats]
    bmesh.ops.translate(bm, verts=bm.verts, vec=offset)
    me = bpy.data.meshes.get(name)
    if me:
        bpy.data.meshes.remove(me)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me); bm.free()
    for m in mats:
        me.materials.append(m)
    ob = bpy.data.objects.new(name, me)
    col.objects.link(ob)
    if smooth:
        for p in me.polygons:
            p.use_smooth = True
    if bevel:
        m = ob.modifiers.new("Bevel", 'BEVEL')
        m.width = bevel; m.segments = segs; m.limit_method = 'ANGLE'
        m.angle_limit = math.radians(30); m.harden_normals = True
    return ob


def apply_bool(target, cutter, op):
    m = target.modifiers.new("Bool", 'BOOLEAN')
    m.object = cutter; m.operation = op; m.solver = 'EXACT'
    try:
        m.material_mode = 'TRANSFER'
    except Exception:
        pass
    target.modifiers.move(target.modifiers.find(m.name), 0)
    for o in target.modifiers:
        if o != m:
            o.show_viewport = False
    dg = bpy.context.evaluated_depsgraph_get()
    new_me = bpy.data.meshes.new_from_object(target.evaluated_get(dg))
    for o in target.modifiers:
        o.show_viewport = True
    target.modifiers.remove(m)
    old = target.data; target.data = new_me; bpy.data.meshes.remove(old)
    cme = cutter.data
    bpy.data.objects.remove(cutter, do_unlink=True)
    bpy.data.meshes.remove(cme)


def look_cam(name, target, az_deg, el_deg, dist, lens=35, shift_y=0.1, col=None):
    cd = bpy.data.cameras.get(name) or bpy.data.cameras.new(name)
    cd.lens = lens; cd.clip_end = 2000; cd.shift_y = shift_y
    cam = bpy.data.objects.get(name) or bpy.data.objects.new(name, cd)
    col = col or bpy.data.collections['TatooineEnv']
    if cam.name not in col.objects:
        col.objects.link(cam)
    t = Vector(target); el, az = math.radians(el_deg), math.radians(az_deg)
    cam.location = t + Vector((dist*math.sin(az)*math.cos(el), -dist*math.cos(az)*math.cos(el), dist*math.sin(el)))
    cam.rotation_euler = (t - cam.location).to_track_quat('-Z', 'Y').to_euler()
    return cam


# ---------- assets / UVs ----------
UV0 = "UV0"
UV0_TILE_M = 4.0   # must match th_texlib.TILE_M


def asset_root(o):
    while o is not None and "asset_id" not in o:
        o = o.parent
    return o


def unwrap_uv0(ob, root):
    """World-scale box projection in asset-local space (see th_uv.py)."""
    to_local = root.matrix_world.inverted() @ ob.matrix_world
    nrm = to_local.to_3x3().inverted().transposed()
    me = ob.data
    while me.uv_layers:
        me.uv_layers.remove(me.uv_layers[0])
    bm = bmesh.new(); bm.from_mesh(me)
    uv = bm.loops.layers.uv.new(UV0)
    s = 1.0 / UV0_TILE_M
    for f in bm.faces:
        n = nrm @ f.normal
        ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
        for loop in f.loops:
            p = to_local @ loop.vert.co
            if az >= ax and az >= ay:
                u, v = p.x, p.y
            elif ax >= ay:
                u, v = (p.y if n.x > 0 else -p.y), p.z
            else:
                u, v = (-p.x if n.y > 0 else p.x), p.z
            loop[uv].uv = (u * s, v * s)
    bm.to_mesh(me); bm.free()
    me.uv_layers.active = me.uv_layers[UV0]
    me.uv_layers[UV0].active_render = True


# ---------- foundations ----------
FOUNDATION_D = 1.0   # how far ground-contact geometry continues below z = 0, so uneven terrain never shows a gap
ENTRY_SLOPE = 0.4    # rise / run of walkable entry ramps: 21.8 deg

# Small house entry ramp (TH_EntryRamp), built by th_foundations and mirrored by th_collision: one definition for both.
# Placed by hand in the scene (2026-10-21): moved +0.961 x, +2.002 y, -0.031 z from the first build, so it is centred
# on the door (x -2.0) and its top edge meets the plinth front (y -4.8); its landing now lies inside the plinth.
H1_RAMP = {"x0": -3.9392, "x1": -0.1392, "y_top": -4.8983, "rise": 0.6, "dz": -0.0305, "landing": 2.3}

# Shuttleport gate ramp (SP_EntryRamp.S), same sharing. The plaza top is flat (0.3) to r 21.2, then its bevelled
# rim drops to ~0.22 at r 21.35; dz keeps the landing (0.295 + dz = 0.20) at least 2.5 cm under every plaza surface
# it overlaps, so the ramp emerges from the plaza side (~r 21.39) instead of z-fighting with the top and the rim.
SP_GATE_RAMP = {"y_top": -21.35, "rise": 0.295, "dz": -0.095, "landing": 0.6, "half_width": 3.0}


def add_skirt(me, depth=FOUNDATION_D):
    """Continue a mesh's flat bottom (z = 0) straight down to -depth. Idempotent: on a mesh already
    skirted, only the skirt bottom is moved to the new depth."""
    bm = bmesh.new(); bm.from_mesh(me)
    low = [v for v in bm.verts if v.co.z < -1e-4]
    if low:
        for v in low:
            v.co.z = -depth
    else:
        bottom = [f for f in bm.faces if f.normal.z < -0.99 and all(abs(v.co.z) < 1e-4 for v in f.verts)]
        res = bmesh.ops.extrude_face_region(bm, geom=bottom)
        verts = [g for g in res['geom'] if isinstance(g, bmesh.types.BMVert)]
        bmesh.ops.translate(bm, verts=verts, vec=(0, 0, -depth))
        stale = [f for f in bottom if f.is_valid]      # use_keep_orig=False normally removes them already
        if stale:
            bmesh.ops.delete(bm, geom=stale, context='FACES')
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me); bm.free()


# ---------- portals / spawns ----------
# Both are empties parented to an asset root, posed in asset-local space, local +Y = forward.
# PORTAL_*: trigger volume (CUBE empty, scale = half extents, mirrored in portal_half). It fires when a
#           character inside it moves with a positive component along its +Y.
# SPAWN_*:  where a character arriving through a portal appears; +Y = facing.
PORTAL_DEPTH = 0.8   # volume depth in front of a door, > a character's radius
# The server fires a portal on a directed crossing of the volume's mid-plane (a containment test misses a running body
# at 10 Hz). The volume therefore stands PORTAL_SETBACK off the facade: a body stopped by the facade (centre at the
# 0.3 m body half-extent) is then 0.6 - 0.3 = 0.3 m past the mid-plane, not on it.
PORTAL_SETBACK = 0.2


def _replace(name):
    o = bpy.data.objects.get(name)
    if o:
        bpy.data.objects.remove(o, do_unlink=True)


def make_portal(root, name, portal_id, center, half, yaw, target, target_spawn, exit_spawn):
    _replace(name)
    p = bpy.data.objects.new(name, None)
    p.empty_display_type = 'CUBE'
    p.location = center; p.rotation_euler = (0, 0, yaw); p.scale = half
    p.show_in_front = True
    p["portal_id"] = portal_id
    p["portal_half"] = tuple(half)
    p["portal_target"] = target
    p["portal_target_spawn"] = target_spawn
    p["portal_exit_spawn"] = exit_spawn
    root.users_collection[0].objects.link(p); p.parent = root
    return p


def make_approach(root, name, target, pts):
    """APPROACH_*: how a bot reaches `target` (a PORTAL_* empty, or a child-asset instance whose interaction it uses).
    A POLY curve, asset-local, one point per change of slope or heading, at feet height: the first on open ground
    outside the asset's collision footprint, the last inside the portal volume / on the interaction's stand point.
    th_export.py checks both ends and the body's clearance along it."""
    _replace(name)
    cu = bpy.data.curves.get(name) or bpy.data.curves.new(name, 'CURVE')
    cu.splines.clear(); cu.dimensions = '3D'
    sp = cu.splines.new('POLY'); sp.points.add(len(pts) - 1)
    for p, c in zip(sp.points, pts):
        p.co = (*c, 1.0)
    o = bpy.data.objects.new(name, cu)
    o.show_in_front = True; o.hide_render = True
    o["approach_for"] = target.name
    root.users_collection[0].objects.link(o); o.parent = root
    return o


def make_nav_node(root, name, loc):
    """NAV_*: a waypoint of an interior's navigation graph (descriptor nav, th_nav.py), asset-local, feet height.
    Nodes are indexed in name order; th_export.py derives the edges and the cell lookup and refuses a graph that
    leaves part of the floor unreachable."""
    _replace(name)
    n = bpy.data.objects.new(name, None)
    n.empty_display_type = 'SPHERE'; n.empty_display_size = 0.15
    n.location = loc; n.show_in_front = True
    n["nav_node"] = True
    root.users_collection[0].objects.link(n); n.parent = root
    return n


def make_spawn(root, name, spawn_id, loc, yaw):
    _replace(name)
    s = bpy.data.objects.new(name, None)
    s.empty_display_type = 'ARROWS'; s.empty_display_size = 0.6
    s.location = loc; s.rotation_euler = (0, 0, yaw)
    s["spawn_id"] = spawn_id
    root.users_collection[0].objects.link(s); s.parent = root
    return s
