import bpy, os, random  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Creature nest (asset "creature_nest", kind "prop"): the lair the creatures of th_creatures.py come out of, which
# players attack once its creatures are dead. A low ring of woven twigs around a straw bowl holding a heap of
# crossed sticks, with branches sticking out of the rim (after a top-view map of a giant nest). Flat colours and flat
# shading, like the creatures. Modelled at its local origin on the ground; round, so it has no front.
#
# About 4.4 m across (branches excluded) and 0.9 m high: the spider (1.8 m) fits in the bowl. Spawn points
# NE_CreatureSpawn.0-3 (spawn ids creature.0-3) stand on the ground 1.2 m outside the rim foot, facing out (the
# spider's box clears the collider): where a creature appears. Collision: th_collision.py (one cylinder).
# Re-runnable.

R_OUT, R_TOP, R_BOWL, H = 2.2, 1.75, 1.25, 0.9     # rim foot and crest radii, bowl edge radius, crest height
FLOOR = 0.18                                        # bowl floor height at its edge (its centre dips to FLOOR - 0.06)
SINK = 0.3                                          # the rim's foot continues this far below z = 0: no gap on uneven ground
SEED = 11
PLACE = (-22.0, 26.0)                               # editing-scene spot, right of the creature row


def flat(name, rgb, rough=0.9):
    m = bpy.data.materials.get(name)
    if m is None:
        m, _, b = simple_mat(name)
        b.inputs['Base Color'].default_value = (*rgb, 1)
        b.inputs['Roughness'].default_value = rough
        m.diffuse_color = (*rgb, 1)
    return m


M_TWIG = flat("TH_NestTwig", (0.15, 0.08, 0.04))
M_WOOD = flat("TH_NestWood", (0.25, 0.14, 0.07))
M_STRAW = flat("TH_NestStraw", (0.48, 0.30, 0.13))

col = get_col("TatooineCreatureNest")
root = bpy.data.objects.new("ASSET_CreatureNest", None)
root.empty_display_type = 'ARROWS'; root.empty_display_size = 1.0
col.objects.link(root)
root["asset_id"] = "creature_nest"
root["asset_kind"] = "prop"
hit, loc, *_ = scene.ray_cast(bpy.context.evaluated_depsgraph_get(), Vector((*PLACE, 100.0)), Vector((0, 0, -1)))
root.location = (*PLACE, loc.z if hit else 0.0)
rng = random.Random(SEED)


def part(name, bm, mat):
    ob = mk(name, bm, mat, col, bevel=0, smooth=False)
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


def merge(bms):
    out = bmesh.new()
    for b in bms:
        me = bpy.data.meshes.new("_tmp"); b.to_mesh(me); b.free()
        out.from_mesh(me); bpy.data.meshes.remove(me)
    return out


# rim: a lumpy ring, cross-section from the outer foot over the crest down to the bowl floor
N = 28
PROFILE = [(R_OUT + 0.1, -SINK), (R_OUT + 0.05, 0.0), (R_OUT, 0.42 * H), (R_TOP + 0.2, 0.92 * H), (R_TOP - 0.1, H),
           (R_BOWL + 0.15, 0.55 * H), (R_BOWL, FLOOR)]
bm = bmesh.new()
rings = []
for i in range(N):
    a = 2 * math.pi * i / N
    dr, dz = rng.uniform(-0.09, 0.09), rng.uniform(-0.1, 0.06)
    ring = []
    for k, (r, z) in enumerate(PROFILE):
        lump = 0.0 if k in (0, 1, len(PROFILE) - 1) else 1.0   # the foot and the bowl edge stay round and level
        rr = r + lump * (dr + rng.uniform(-0.04, 0.04))
        zz = z + lump * (dz * z / H)
        ring.append(bm.verts.new((rr * math.cos(a), rr * math.sin(a), zz)))
    rings.append(ring)
for i in range(N):
    a, b = rings[i], rings[(i + 1) % N]
    for k in range(len(PROFILE) - 1):
        bm.faces.new((a[k], b[k], b[k + 1], a[k + 1]))
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
part("NE_Rim", bm, M_TWIG)

# bowl floor: a shallow cone fan from the bowl edge to a dip in the middle
bm = bmesh.new()
edge = [bm.verts.new((R_BOWL * math.cos(2 * math.pi * i / N), R_BOWL * math.sin(2 * math.pi * i / N), FLOOR))
        for i in range(N)]
mid = bm.verts.new((0, 0, FLOOR - 0.06))
for i in range(N):
    bm.faces.new((edge[i], edge[(i + 1) % N], mid))
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
part("NE_Bowl", bm, M_STRAW)

# woven twigs lying across the crest, roughly tangential
twigs = []
for i in range(16):
    a = 2 * math.pi * (i + rng.uniform(-0.3, 0.3)) / 16
    r, half = rng.uniform(R_TOP - 0.15, R_TOP + 0.3), rng.uniform(0.5, 0.8)
    t = Vector((-math.sin(a), math.cos(a), 0)) * half
    c = Vector((r * math.cos(a), r * math.sin(a), H * rng.uniform(0.8, 0.98)))
    twigs.append(rod(c - t + Vector((0, 0, rng.uniform(-0.12, 0.05))), c + t + Vector((0, 0, rng.uniform(-0.12, 0.05))),
                     0.045, 0.03))
part("NE_Twigs", merge(twigs), M_WOOD)

# branches sticking out of the rim, each with a side fork
branches = []
for i in range(14):
    a = 2 * math.pi * (i + rng.uniform(-0.35, 0.35)) / 14
    out = Vector((math.cos(a), math.sin(a), 0))
    side = Vector((-out.y, out.x, 0))
    p0 = out * rng.uniform(R_TOP, R_OUT - 0.15) + Vector((0, 0, H * rng.uniform(0.35, 0.85)))
    p1 = p0 + out * rng.uniform(0.7, 1.2) + side * rng.uniform(-0.3, 0.3) + Vector((0, 0, rng.uniform(-0.15, 0.35)))
    branches.append(rod(p0, p1, 0.065, 0.02))
    f0 = p0.lerp(p1, rng.uniform(0.45, 0.65))
    f1 = f0 + (p1 - p0).normalized() * 0.25 + side * rng.choice((-1, 1)) * rng.uniform(0.25, 0.4) + Vector((0, 0, 0.12))
    branches.append(rod(f0, f1, 0.03, 0.012, seg=4))
part("NE_Branches", merge(branches), M_WOOD)

# a heap of crossed sticks in the bowl, stacked a little higher towards the middle
heap = []
for i in range(10):
    a = math.pi * (i + rng.uniform(-0.3, 0.3)) / 10              # headings spread over half a turn: they cross
    d = Vector((math.cos(a), math.sin(a), 0))
    c = Vector((rng.uniform(-0.35, 0.35), rng.uniform(-0.35, 0.35), 0))
    half = rng.uniform(0.45, 0.75)
    z = FLOOR + 0.03 + 0.2 * (1 - c.length / 0.5) * rng.uniform(0.5, 1.0)
    p0 = c - d * half + Vector((0, 0, z + rng.uniform(-0.06, 0.04)))
    p1 = c + d * half + Vector((0, 0, z + rng.uniform(-0.06, 0.04)))
    heap.append(rod(p0, p1, rng.uniform(0.05, 0.07), 0.03))
    if i % 3 == 0:
        f0 = p0.lerp(p1, rng.uniform(0.3, 0.7))
        heap.append(rod(f0, f0 + d * 0.2 + Vector((-d.y, d.x, 0)) * rng.choice((-1, 1)) * 0.3 + Vector((0, 0, 0.1)),
                        0.03, 0.012, seg=4))
part("NE_Heap", merge(heap), M_WOOD)

# where creatures appear: on the ground just outside the rim, facing out (local +Y = facing)
for i in range(4):
    a = math.pi / 4 + i * math.pi / 2
    make_spawn(root, f"NE_CreatureSpawn.{i}", f"creature.{i}",
               ((R_OUT + 1.2) * math.cos(a), (R_OUT + 1.2) * math.sin(a), 0.0), a - math.pi / 2)

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
result = {"root": root.name, "at": [round(v, 3) for v in root.location], "parts": len(root.children), "tris": tris,
          "size": [round(v, 3) for v in hi - lo], "z": [round(lo.z, 3), round(hi.z, 3)]}
