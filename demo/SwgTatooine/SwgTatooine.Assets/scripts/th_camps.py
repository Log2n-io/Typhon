import bpy, os, random  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Humanoid lairs (kind "prop"), the camp-centre counterparts of creature_nest (th_creature_nest.py): the place a group
# of humanoid enemies (character skins) holds, which players attack.
# - soldier_camp (soldiers, blue soldiers): a ridge tent, a campfire, supply crates, a barrel, a pennant pole.
# - ninja_hideout (ninjas, sand ninjas): a low lean-to of patched hides over a rug, a back curtain, a brazier, clay
#   pots, rolled hides.
# Flat colours and flat shading, like the nest. Each is modelled at its local origin on the ground, front (the tent
# opening, the canopy's high side) towards -Y, about 6 m across. Ground-touching parts continue SINK below z = 0: no
# gap on uneven ground.
#
# Spawn points <prefix>_Spawn.0-3 (spawn ids creature.0-3, the nest's, so lair code reads either) stand on the ground
# in the camp, facing out from its centre, each 0.3 m body square clear of the colliders: where an enemy appears.
# Collision: th_collision.py. Re-runnable.

SINK = 0.15
SEED = 23
PLACE = {"soldier_camp": (-14.0, 24.0), "ninja_hideout": (-6.0, 24.0)}   # editing-scene spots, right of the nest


def flat(name, rgb, rough=0.9, metal=0.0, emit=0.0):
    m = bpy.data.materials.get(name)
    if m is None:
        m, _, b = simple_mat(name)
        b.inputs['Base Color'].default_value = (*rgb, 1)
        b.inputs['Roughness'].default_value = rough
        b.inputs['Metallic'].default_value = metal
        if emit:
            b.inputs['Emission Color'].default_value = (*rgb, 1)
            b.inputs['Emission Strength'].default_value = emit
        m.diffuse_color = (*rgb, 1)
    return m


M_WOOD = bpy.data.materials["TH_NestWood"]          # th_creature_nest.py
M_GUN = bpy.data.materials["TH_PanelGunmetal"]      # th_terminals.py
M_RED = bpy.data.materials["TH_StripRed"]           # th_terminals.py
M_KHAKI = bpy.data.materials["TH_PanelKhaki"]       # th_swoop.py
M_CANVAS = flat("TH_CampCanvas", (0.30, 0.28, 0.19))
M_SHADE = flat("TH_CampShade", (0.04, 0.04, 0.035))
M_STONE = flat("TH_CampStone", (0.36, 0.31, 0.26))
M_EMBER = flat("TH_CampEmber", (0.07, 0.03, 0.02))
M_FLAME = flat("TH_CampFlame", (1.0, 0.42, 0.08), 0.5, 0.0, 1.5)
M_CORE = flat("TH_CampFlameCore", (1.0, 0.82, 0.38), 0.5, 0.0, 2.0)
M_HIDE = flat("TH_HideTan", (0.50, 0.36, 0.20))
M_HIDE2 = flat("TH_HideDark", (0.30, 0.20, 0.12))
M_HIDE3 = flat("TH_HidePale", (0.42, 0.37, 0.30))
M_RUG = flat("TH_RugRed", (0.34, 0.07, 0.06))
M_CLOTH = flat("TH_ClothDark", (0.09, 0.09, 0.12))
M_CLAY = flat("TH_Clay", (0.55, 0.30, 0.17))

rng = random.Random(SEED)


def camp(aid, name):
    col = get_col(f"Tatooine{name}")
    root = bpy.data.objects.new(f"ASSET_{name}", None)
    root.empty_display_type = 'ARROWS'; root.empty_display_size = 1.0
    col.objects.link(root)
    root["asset_id"], root["asset_kind"] = aid, "prop"
    hit, loc, *_ = scene.ray_cast(bpy.context.evaluated_depsgraph_get(), Vector((*PLACE[aid], 100.0)), Vector((0, 0, -1)))
    root.location = (*PLACE[aid], loc.z if hit else 0.0)
    return col, root


def part(col, root, name, bm, mats):
    ob = mk(name, bm, mats, col, bevel=0, smooth=False)
    ob.parent = root                               # local coordinates: no parent inverse
    unwrap_uv0(ob, root)
    return ob


def rod(p0, p1, r0, r1, seg=5):
    """Tapered rod from p0 (radius r0) to p1 (radius r1)."""
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r0, radius2=r1, depth=d.length)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=d.to_track_quat('Z', 'Y').to_matrix())
    bmesh.ops.translate(bm, verts=bm.verts, vec=(p0 + p1) / 2)
    return bm


def lathe(cx, cy, profile, seg):
    """Solid of revolution about the vertical through (cx, cy): profile = [(r, z)] bottom to top, capped."""
    bm = bmesh.new()
    rings = [[bm.verts.new((cx + r * math.cos(2 * math.pi * i / seg), cy + r * math.sin(2 * math.pi * i / seg), z))
              for i in range(seg)] for r, z in profile]
    for a, b in zip(rings, rings[1:]):
        for i in range(seg):
            j = (i + 1) % seg
            bm.faces.new((a[i], a[j], b[j], b[i]))
    bm.faces.new(rings[0][::-1]); bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def rock(c, size):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=1.0)
    for v in bm.verts:
        v.co *= rng.uniform(0.85, 1.1)
    bmesh.ops.scale(bm, verts=bm.verts, vec=size)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(rng.uniform(0, math.pi), 3, 'Z'))
    bmesh.ops.translate(bm, verts=bm.verts, vec=c)
    return bm


def merge(bms):
    """One bmesh from several; faces keep their material index."""
    out = bmesh.new()
    for b in bms:
        me = bpy.data.meshes.new("_tmp"); b.to_mesh(me); b.free()
        out.from_mesh(me); bpy.data.meshes.remove(me)
    return out


def tinted(bm, idx):
    for f in bm.faces:
        f.material_index = idx
    return bm


def fire(col, root, pre, c, r_ring, stones, h_flame):
    """Stone ring, ember bed, crossed logs and a two-cone flame (emissive), centred on c (ground)."""
    # the core cone is thinner and taller than the outer one, so its tip pokes out: an enclosed core is culled at export
    cx, cy = c
    ring = [rock((cx + r_ring * math.cos(a), cy + r_ring * math.sin(a), 0.02), (0.15, 0.12, 0.1))
            for a in (2 * math.pi * (i + rng.uniform(-0.15, 0.15)) / stones for i in range(stones))]
    part(col, root, f"{pre}_FireStones", merge(ring), M_STONE)
    part(col, root, f"{pre}_FireEmbers", cyl_bm(cx, cy, -0.02, 0.04, r_ring - 0.08, seg=8), M_EMBER)
    logs = []
    for i in range(3):
        a = 2 * math.pi * i / 3 + 0.4
        logs.append(rod((cx + (r_ring - 0.05) * math.cos(a), cy + (r_ring - 0.05) * math.sin(a), 0.04),
                        (cx + 0.05 * math.cos(a), cy + 0.05 * math.sin(a), 0.38), 0.05, 0.035, seg=6))
    part(col, root, f"{pre}_FireLogs", merge(logs), M_WOOD)
    flame = merge([tinted(cyl_bm(cx, cy, 0.04, 0.04 + 0.8 * h_flame, 0.17, 0.0, seg=6), 0),
                   tinted(xform(cyl_bm(cx, cy, 0.04, 0.04 + 1.1 * h_flame, 0.1, 0.0, seg=5), rot_z=0.5,
                                pivot=(cx, cy, 0)), 1)])
    part(col, root, f"{pre}_Flame", flame, [M_FLAME, M_CORE])


def spawns(root, pre, centre, pts):
    """creature.0-3 at pts, facing away from centre (local +Y = facing)."""
    for i, (x, y) in enumerate(pts):
        make_spawn(root, f"{pre}_Spawn.{i}", f"creature.{i}", (x, y, 0.0),
                   math.atan2(y - centre[1], x - centre[0]) - math.pi / 2)


def stats(root):
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    tris = 0
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for o in root.children:
        if o.type == 'MESH':
            e = o.evaluated_get(dg); m = e.to_mesh(); m.calc_loop_triangles(); tris += len(m.loop_triangles)
            for v in m.vertices:
                lo = Vector(map(min, lo, v.co)); hi = Vector(map(max, hi, v.co))
            e.to_mesh_clear()
    return {"root": root.name, "at": [round(v, 3) for v in root.location], "parts": len(root.children), "tris": tris,
            "lo": [round(v, 3) for v in lo], "hi": [round(v, 3) for v in hi]}


result = {}

# =============== soldier_camp ===============
# Ridge tent at the back (x +-1.2, y 1.0..3.0, ridge 1.8 m) opening towards the fire (0, -0.4), two log seats by the
# fire; crates and a barrel on the flanks (x beyond +-2.0); a pennant pole behind the right flank.
col, root = camp("soldier_camp", "SoldierCamp")
TENT = (-1.2, 1.2, 1.0, 3.0, 1.8)
FIRE = (0.0, -0.4)

x0, x1, y0, y1, h = TENT
bm = prism([(x0, -SINK), (x1, -SINK), (0.0, h)], y0, y1)        # closed prism; the front triangle is the doorway
for f in bm.faces:
    f.material_index = 1 if f.normal.y < -0.9 else 0
part(col, root, "SC_Tent", bm, [M_CANVAS, M_SHADE])
flaps = []                                                       # door flaps folded back against the front slopes
for s in (-1, 1):
    flaps.append(prism([(0.0, h - 0.05), (s * 0.55, 0.0), (s * 0.9, 0.0)], y0 - 0.12, y0 - 0.06))
    flaps[-1] = xform(flaps[-1], rot_z=s * 0.35, pivot=(s * 0.45, y0, 0))
part(col, root, "SC_TentFlaps", merge(flaps), M_CANVAS)
poles = [rod((0, y, -SINK), (0, y, h + 0.12), 0.03, 0.03, seg=5) for y in (y0 - 0.04, y1 + 0.04)]
poles.append(rod((0, y0 - 0.04, h + 0.06), (0, y1 + 0.04, h + 0.06), 0.025, 0.025, seg=5))
part(col, root, "SC_TentPoles", merge(poles), M_WOOD)

fire(col, root, "SC", FIRE, 0.45, 8, 0.55)
seats = [rod((-0.6, -1.45, 0.12), (0.6, -1.45, 0.12), 0.14, 0.13, seg=7),          # log seats by the fire
         rod((0.95, -0.6, 0.12), (0.95, 0.5, 0.12), 0.14, 0.13, seg=7)]
part(col, root, "SC_Seats", merge(seats), M_WOOD)

crates = [tinted(box_bm(-2.9, -2.1, -1.0, -0.2, -SINK, 0.7), 0),                  # left stack
          tinted(box_bm(-2.95, -2.05, -1.05, -0.15, 0.48, 0.56), 1),              # its red band
          tinted(xform(box_bm(-2.8, -2.25, -0.85, -0.35, 0.7, 1.15), rot_z=0.2, pivot=(-2.5, -0.6, 0)), 2),
          tinted(box_bm(-2.9, -2.2, 0.0, 0.7, -SINK, 0.6), 2),
          tinted(box_bm(2.0, 2.9, -1.3, 0.1, -SINK, 0.5), 0),                    # right: long weapons crate
          tinted(box_bm(1.97, 2.93, -1.33, 0.13, 0.5, 0.58), 1)]                  # its lid
part(col, root, "SC_Crates", merge(crates), [M_GUN, M_RED, M_KHAKI])
barrel = merge([tinted(cyl_bm(2.5, 1.0, -SINK, 0.9, 0.3, seg=10), 0),
                tinted(cyl_bm(2.5, 1.0, 0.55, 0.65, 0.31, seg=10), 1)])
part(col, root, "SC_Barrel", barrel, [M_KHAKI, M_RED])
part(col, root, "SC_PennantPole", rod((2.4, 2.4, -SINK), (2.4, 2.4, 2.7), 0.045, 0.03, seg=6), M_GUN)
pennant = prism([(2.43, 2.65), (3.25, 2.42), (2.43, 2.2)], 2.39, 2.41)
part(col, root, "SC_Pennant", pennant, M_RED)

spawns(root, "SC", FIRE, [(-1.5, -1.9), (1.5, -1.9), (-1.6, 0.6), (1.6, 0.6)])
result["soldier_camp"] = stats(root)

# =============== ninja_hideout ===============
# Lean-to of patched hides on four poles (x +-1.8, front edge y -0.2 at 2.1 m, back edge y 2.4 at 1.1 m) over a rug,
# a hide curtain closing the back, a brazier in front (0, -1.4), clay pots and rolled hides on the flanks.
col, root = camp("ninja_hideout", "NinjaHideout")
CX, YF, YB, HF, HB = 1.8, -0.2, 2.4, 2.1, 1.1
BRAZIER = (0.0, -1.4)


def canopy_z(x, y):
    t = (y - YF) / (YB - YF)
    sag = 0.12 * math.sin(math.pi * (x + CX) / (2 * CX)) * math.sin(math.pi * t)
    return HF + (HB - HF) * t - sag


NX, NY = 4, 3
bm = bmesh.new()
g = [[bm.verts.new((-CX - 0.1 + (2 * CX + 0.2) * i / NX, YF - 0.1 + (YB - YF + 0.2) * j / NY, 0.0))
      for j in range(NY + 1)] for i in range(NX + 1)]
for row in g:
    for v in row:
        v.co.z = canopy_z(v.co.x, v.co.y)
for i in range(NX):
    for j in range(NY):
        f = bm.faces.new((g[i][j], g[i + 1][j], g[i + 1][j + 1], g[i][j + 1]))
        f.material_index = rng.choice((0, 0, 1, 1, 2))               # patchwork of three hides
bmesh.ops.solidify(bm, geom=bm.faces[:], thickness=0.04)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
part(col, root, "NH_Canopy", bm, [M_HIDE, M_HIDE2, M_HIDE3])

fringe = []                                                       # ragged strips hanging off the front edge
for k in range(7):
    x = -CX + 2 * CX * (k + 0.5) / 7 + rng.uniform(-0.1, 0.1)
    z = canopy_z(x, YF - 0.08)
    fringe.append(tinted(prism([(x - 0.17, z), (x + 0.17, z), (x + rng.uniform(-0.05, 0.05), z - rng.uniform(0.25, 0.4))],
                               YF - 0.11, YF - 0.09), k % 2))
part(col, root, "NH_Fringe", merge(fringe), [M_HIDE2, M_CLOTH])

poles = []
for x in (-CX, CX):
    for y, top in ((YF, HF), (YB, HB)):
        lean = 0.12 if y == YF else -0.06                           # the front poles splay out, the back ones in
        poles.append(rod((x * 1.0 + math.copysign(lean, x), y - lean, -SINK), (x, y, top + 0.18), 0.045, 0.03, seg=6))
part(col, root, "NH_Poles", merge(poles), M_WOOD)

curtain = bmesh.new()                                             # back wall: hides from the back edge to the ground
c = [[curtain.verts.new((-CX + 2 * CX * i / NX, YB + 0.02 + 0.12 * (1 - zf), (HB - 0.02) * zf - SINK * (1 - zf)))
      for zf in (0.0, 1.0)] for i in range(NX + 1)]
for i in range(NX):
    f = curtain.faces.new((c[i][0], c[i + 1][0], c[i + 1][1], c[i][1]))
    f.material_index = i % 2
bmesh.ops.solidify(curtain, geom=curtain.faces[:], thickness=0.04)
bmesh.ops.recalc_face_normals(curtain, faces=curtain.faces)
part(col, root, "NH_Curtain", curtain, [M_HIDE2, M_HIDE])

rug = merge([tinted(box_bm(-1.45, 1.45, 0.15, 2.2, -0.02, 0.03), 0),
             tinted(box_bm(-1.25, 1.25, 0.35, 2.0, 0.03, 0.035), 1)])   # border + inner field
part(col, root, "NH_Rug", rug, [M_CLOTH, M_RUG])
cushions = [xform(box_bm(-0.35, 0.35, -0.25, 0.25, 0.035, 0.2), rot_z=a, move=(x, y, 0))
            for x, y, a in ((-0.8, 1.6, 0.3), (0.75, 1.65, -0.25), (0.0, 1.85, 0.05))]
part(col, root, "NH_Cushions", merge(cushions), M_CLOTH)

bx, by = BRAZIER
brazier = merge([tinted(lathe(bx, by, [(0.12, 0.5), (0.3, 0.62), (0.34, 0.78), (0.26, 0.8)], 8), 0)]
                + [tinted(rod((bx + 0.3 * math.cos(a), by + 0.3 * math.sin(a), -SINK),
                              (bx + 0.12 * math.cos(a), by + 0.12 * math.sin(a), 0.56), 0.03, 0.025, seg=4), 0)
                   for a in (0.3, 0.3 + 2 * math.pi / 3, 0.3 + 4 * math.pi / 3)]
                + [tinted(cyl_bm(bx, by, 0.7, 0.8, 0.24, seg=8), 1)])
part(col, root, "NH_Brazier", brazier, [M_GUN, M_EMBER])
flame = merge([tinted(cyl_bm(bx, by, 0.78, 1.15, 0.15, 0.0, seg=6), 0),                # core tip pokes out (fire())
               tinted(xform(cyl_bm(bx, by, 0.78, 1.3, 0.09, 0.0, seg=5), rot_z=0.5, pivot=(bx, by, 0)), 1)])
part(col, root, "NH_Flame", flame, [M_FLAME, M_CORE])

pots = [lathe(x, y, [(0.1 * s, -SINK), (0.2 * s, 0.12 * s), (0.24 * s, 0.3 * s), (0.16 * s, 0.5 * s), (0.08 * s, 0.56 * s),
                     (0.1 * s, 0.62 * s)], 8)
        for x, y, s in ((-2.35, 1.55, 1.0), (-2.1, 2.0, 0.75), (-2.55, 2.05, 0.85))]
part(col, root, "NH_Pots", merge(pots), M_CLAY)
rolls = [tinted(hcyl_bm(2.3, y, z, 0.17, 0.95, (1, 0.15 * k), seg=7), k % 2)
         for k, (y, z) in enumerate(((1.5, 0.12), (1.9, 0.12), (1.7, 0.4)))]
part(col, root, "NH_Rolls", merge(rolls), [M_HIDE, M_HIDE2])

spawns(root, "NH", (0.0, 0.4), [(-1.3, -1.7), (1.3, -1.7), (-2.4, 0.3), (2.4, 0.3)])
result["ninja_hideout"] = stats(root)
