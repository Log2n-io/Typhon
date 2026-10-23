import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())
exec(open(os.path.join(TH_ROOT, "scripts", "th_colprims.py")).read(), globals())

# House interiors: dedicated assets, loaded at runtime as their own scene behind a portal, never part of the
# exterior. This .blend is only the editing scene, so they sit in the main scene, away from the exteriors; their
# coordinates are asset-local (floor at z = 0, relative to the ASSET_ root) and follow the exterior's footprint
# minus the walls, so the rooms read as the inside of the building.
#
# Shell = (union of rooms inflated by T) - (union of rooms + openings + recesses), exact booleans; faces that
# do not look into a room are then deleted (never visible from inside). Materials by face: floor, dome, walls.
# Furniture, lamps, windows, door leaf, lights, collision (th_colprims rules + a floor slab, since there is no
# terrain inside), the exit portal and the entry spawn complete each asset.

T = 0.4
MAT = {k: bpy.data.materials[v] for k, v in dict(
    wall="TH_Sandstone", dome="TH_DomeStone", floor="TH_Duracrete", wood="TH_DoorWood", wood_dark="TH_DoorBatten",
    metal="TH_Metal", screen="TH_ScreenBlue", lamp="TH_LampOrange").items()}


def flat_mat(name, rgb, rough=0.8, emission=0.0):
    m, nt, b = simple_mat(name)
    b.inputs['Base Color'].default_value = (*rgb, 1); b.inputs['Roughness'].default_value = rough
    if emission:
        b.inputs['Emission Color'].default_value = (*rgb, 1); b.inputs['Emission Strength'].default_value = emission
    m.diffuse_color = (*rgb, 1)
    return m


MAT["rug"] = flat_mat("TH_Rug", (0.32, 0.08, 0.05), 0.95)
MAT["fabric"] = flat_mat("TH_Fabric", (0.62, 0.55, 0.44), 0.9)
MAT["daylight"] = flat_mat("TH_WindowLight", (1.0, 0.86, 0.66), 0.5, emission=6.0)


# ---------------- solids: mesh + analytic inside test ----------------
def solid_bm(s):
    k = s[0]
    if k == 'box':
        return box_bm(*s[1:7])
    if k == 'cyl':
        cx, cy, z0, z1, r = s[1:6]
        return cyl_bm(cx, cy, z0, z1, r)
    if k == 'ell':
        cx, cy, cz, r, sz = s[1:6]
        bm = bmesh.new()
        n = ring_segs(r, 12, 24)
        bmesh.ops.create_uvsphere(bm, u_segments=n, v_segments=n // 2, radius=r)
        bmesh.ops.scale(bm, verts=bm.verts, vec=(1, 1, sz))
        bmesh.ops.translate(bm, verts=bm.verts, vec=(cx, cy, cz))
        return bm
    if k == 'archx':                        # arch opening in a wall facing +-Y: (cx, w, zb, zs, y0, y1)
        cx, w, zb, zs, y0, y1 = s[1:7]
        return prism(arch_pts(cx, w, zb, zs), y0, y1, 'XZ')
    if k == 'archy':                        # arch opening in a wall facing +-X: (cy, w, zb, zs, x0, x1)
        cy, w, zb, zs, x0, x1 = s[1:7]
        return prism(arch_pts(cy, w, zb, zs), x0, x1, 'YZ')
    if k == 'hcyl':                         # round window recess: (cx, cy, cz, r, depth, dir_xy), dir into the room
        cx, cy, cz, r, depth, d = s[1:7]
        return hcyl_bm(cx, cy, cz, r, depth, d)
    raise ValueError(k)


def inside(s, p, eps=1e-4):
    k = s[0]; x, y, z = p
    if k == 'box':
        x0, x1, y0, y1, z0, z1 = s[1:7]
        return x0 - eps <= x <= x1 + eps and y0 - eps <= y <= y1 + eps and z0 - eps <= z <= z1 + eps
    if k == 'cyl':
        cx, cy, z0, z1, r = s[1:6]
        return (x - cx) ** 2 + (y - cy) ** 2 <= (r + eps) ** 2 and z0 - eps <= z <= z1 + eps
    if k == 'ell':
        cx, cy, cz, r, sz = s[1:6]
        return ((x - cx) / r) ** 2 + ((y - cy) / r) ** 2 + ((z - cz) / (r * sz)) ** 2 <= 1 + eps
    if k in ('archx', 'archy'):
        c, w, zb, zs, a0, a1 = s[1:7]
        u, a = (x, y) if k == 'archx' else (y, x)
        rr = w / 2
        if not (a0 - eps <= a <= a1 + eps) or z < zb - eps:
            return False
        return (abs(u - c) <= rr + eps and z <= zs + eps) or ((u - c) ** 2 + (z - zs) ** 2 <= (rr + eps) ** 2)
    if k == 'hcyl':
        cx, cy, cz, r, depth, d = s[1:7]
        dv = Vector((d[0], d[1], 0)).normalized()
        q = Vector((x - cx, y - cy, z - cz))
        t = q.dot(dv)
        return abs(t) <= depth / 2 + eps and (q - dv * t).length <= r + eps
    raise ValueError(k)


def union(name, solids, col):
    base = mk(name, solid_bm(solids[0]), MAT["wall"], col, bevel=0, smooth=False)
    for i, s in enumerate(solids[1:]):
        apply_bool(base, mk(f"_{name}_{i}", solid_bm(s), MAT["wall"], col, bevel=0, smooth=False), 'UNION')
    return base


def build_shell(name, col, rooms, outer, cuts, dome):
    shell = union(name, outer, col)
    inner = union(f"_{name}_inner", rooms + cuts, col)
    apply_bool(shell, inner, 'DIFFERENCE')
    me = shell.data
    me.materials.clear()
    for k in ("wall", "floor", "dome"):
        me.materials.append(MAT[k])
    bm = bmesh.new(); bm.from_mesh(me)
    hidden = [f for f in bm.faces
              if not any(inside(s, f.calc_center_median() + f.normal * 0.03) for s in rooms + cuts)]
    bmesh.ops.delete(bm, geom=hidden, context='FACES')
    dcx, dcy, dcz, dr = dome
    for f in bm.faces:
        c = f.calc_center_median()
        if f.normal.z > 0.9 and abs(c.z) < 0.02:
            f.material_index = 1
        elif c.z > dcz + 0.01 and (c.x - dcx) ** 2 + (c.y - dcy) ** 2 <= (dr + 0.05) ** 2:
            f.material_index = 2
        else:
            f.material_index = 0
    bm.to_mesh(me); bm.free()
    return shell, len(hidden)


def planar_dome_uv(shell):
    """Box projection switches axis across a dome and shows as patches; seen from below, a single top-down
    projection reads continuous (it only stretches in the thin, near-vertical band at the rim)."""
    me = shell.data; uv = me.uv_layers[UV0].data; s = 1.0 / UV0_TILE_M
    for p in me.polygons:
        if p.material_index == 2:
            for li in p.loop_indices:
                co = me.vertices[me.loops[li].vertex_index].co
                uv[li].uv = (co.x * s, co.y * s)


# ---------------- per-interior pieces ----------------
def door_leaf(prefix, col, cx, y_w, w, zs):
    """Closed door at the back of its recess (recess cut from y_w - 0.2), facing +Y (into the room)."""
    yb = y_w - 0.2
    out = [mk(f"{prefix}_Door", prism(arch_pts(cx, w - 0.1, 0.0, zs), yb, yb + 0.05, 'XZ'), MAT["wood"], col, bevel=0.01)]
    r = (w - 0.1) / 2
    for i, dx in enumerate((-0.35 * r, 0.0, 0.35 * r)):
        top = zs + math.sqrt(max(r * r - dx * dx, 0.0)) - 0.12
        out.append(mk(f"{prefix}_DoorBatten.{i:02d}", box_bm(cx + dx - 0.04, cx + dx + 0.04, yb + 0.05, yb + 0.08, 0.08, top),
                      MAT["wood_dark"], col, bevel=0.005))
    for i, z in enumerate((0.35, 0.95 * zs, zs + 0.2)):
        out.append(mk(f"{prefix}_DoorRail.{i:02d}", box_bm(cx - r + 0.08, cx + r - 0.08, yb + 0.05, yb + 0.08, z, z + 0.08),
                      MAT["wood_dark"], col, bevel=0.005))
    return out


def window_pane(name, col, s):
    _, cx, cy, cz, r, depth, d = s
    dv = Vector((d[0], d[1], 0)).normalized()
    back = Vector((cx, cy, cz)) - dv * (depth / 2 - 0.005)
    return mk(name, hcyl_bm(back.x, back.y, back.z, r, 0.01, d), MAT["daylight"], col, bevel=0)


def point_light(name, col, loc, watts, rgb=(1.0, 0.8, 0.6), radius=0.3):
    ld = bpy.data.lights.get(name) or bpy.data.lights.new(name, 'POINT')
    ld.energy = watts; ld.color = rgb; ld.shadow_soft_size = radius
    o = bpy.data.objects.new(name, ld); o.location = loc
    col.objects.link(o)
    return o


def new_build_collection(name):
    c = bpy.data.collections.get(name)
    if c:
        for o in list(c.objects):
            data = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if isinstance(data, bpy.types.Mesh) and data.users == 0:
                bpy.data.meshes.remove(data)
        for sc in bpy.data.scenes:
            if c.name in sc.collection.children:
                sc.collection.children.unlink(c)
    else:
        c = bpy.data.collections.new(name)
    bpy.data.scenes["Scene"].collection.children.link(c)
    return c


def preview_camera(name, loc, target, lens=16):
    cd = bpy.data.cameras.get(name) or bpy.data.cameras.new(name)
    cd.lens = lens; cd.clip_start = 0.05
    cam = bpy.data.objects.get(name) or bpy.data.objects.new(name, cd)
    env = bpy.data.collections["TatooineEnv"]
    if cam.name not in env.objects:
        env.objects.link(cam)
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    return cam


def build(spec):
    global col, STATS
    acol = new_build_collection(spec["collection"])
    ccol = new_build_collection(spec["collection"] + "_Collision")

    root = bpy.data.objects.new(spec["root"], None)
    root.empty_display_type = 'ARROWS'; root.empty_display_size = 1.0
    acol.objects.link(root)
    root["asset_id"] = spec["asset_id"]; root["asset_kind"] = "interior"
    short = spec["root"][len("ASSET_"):]

    shell, culled = build_shell(f"{short}_Shell", acol, spec["rooms"], spec["outer"], spec["cuts"], spec["dome"])
    pieces = [shell]
    d = spec["door"]
    pieces += door_leaf(short, acol, d["cx"], d["y_w"], d["w"], d["zs"])
    for i, s in enumerate(c for c in spec["cuts"] if c[0] == 'hcyl'):
        pieces.append(window_pane(f"{short}_Window.{i:02d}", acol, s))
    sx, sy, sz, sr = spec["skylight"]
    pieces.append(mk(f"{short}_Skylight", cyl_bm(sx, sy, sz - 0.01, sz, sr), MAT["daylight"], acol, bevel=0))
    for name, bm, mat, bevel in spec["furniture"]:
        pieces.append(mk(f"{short}_{name}", bm, MAT[mat], acol, bevel=bevel))
    for ob in pieces:
        ob.parent = root
    bpy.context.view_layer.update()
    for ob in pieces:
        unwrap_uv0(ob, root)
    planar_dome_uv(shell)
    for name, loc, watts in spec["lights"]:
        point_light(f"{short}_{name}", acol, loc, watts).parent = root

    # exit portal: inside the door, PORTAL_SETBACK off the wall (a body stopped by the wall is 0.3 m past the
    # mid-plane), +Y pointing out through it; entry spawn facing into the room, 0.3 m clear of the volume
    hz = d["top"] / 2
    spawn = make_spawn(root, f"SPAWN_{short}_entry", "entry", (d["cx"], d["y_w"] + 1.6, 0.0), 0.0)
    make_portal(root, f"PORTAL_{short}_{spec['exterior_spawn']}", spec["exterior_spawn"],
                (d["cx"], d["y_w"] + PORTAL_SETBACK + PORTAL_DEPTH / 2, hz), (d["w"] / 2 + 0.1, PORTAL_DEPTH / 2, hz), math.pi,
                target=spec["exterior"], target_spawn=spec["exterior_spawn"], exit_spawn=spawn.name)

    col = ccol; STATS = {}
    R = make_root(f"COL_{short}", spec["asset_id"], (0, 0, 0), (0, 0, 0))
    spec["collision"](R)

    # built around the world origin, then moved to where they are displayed; children follow their root
    o = Vector(spec["origin"])
    root.location = o
    R.location = Vector(spec["collision_origin"])
    loc, tgt, lens = spec["camera"]
    preview_camera(f"TatooineCam{short}", o + Vector(loc), o + Vector(tgt), lens)
    return {"origin": tuple(o), "shell_faces": len(shell.data.polygons), "culled_outer_faces": culled,
            "objects": len(acol.objects), "collision": STATS[spec["asset_id"]]}


def walls(R, segs, h_floor=0.0):
    for name, x0, x1, y0, y1 in segs:
        box(R, name, x0, x1, y0, y1, h_floor, h_floor + BAND, floor=h_floor)


def ring(R, prefix, cx, cy, r_in, r_out, n, skip):
    k = 0
    for i in range(n):
        a = 2 * math.pi * (i + 0.5) / n
        mid = (cx + (r_in + r_out) / 2 * math.cos(a), cy + (r_in + r_out) / 2 * math.sin(a))
        if skip(*mid):
            continue
        hl = r_out * math.sin(math.pi / n)
        box(R, f"{prefix}.{k:02d}", cx - hl, cx + hl, cy + r_in, cy + r_out, 0.0, BAND, floor=0.0,
            rot_z=a - math.pi / 2, pivot=(cx, cy))
        k += 1


# =========================== small house ===========================
def small_furniture():
    f = []
    f.append(("Rug", cyl_bm(-1.1, 0.6, 0.0, 0.012, 1.5), "rug", 0))
    f.append(("TableTop", cyl_bm(-1.1, 0.6, 0.70, 0.76, 0.6), "wood", 0.01))
    f.append(("TableLeg", cyl_bm(-1.1, 0.6, 0.0, 0.70, 0.1), "wood_dark", 0))
    f.append(("TableFoot", cyl_bm(-1.1, 0.6, 0.0, 0.05, 0.35), "wood_dark", 0.01))
    for i, (x, y) in enumerate([(-2.1, 0.1), (-0.1, 1.2)]):
        f.append((f"Stool.{i:02d}", cyl_bm(x, y, 0.0, 0.45, 0.2, 0.18), "wood", 0.01))
    f.append(("Cabinet", box_bm(-3.5, -2.1, 2.95, 3.4, 0.0, 1.4), "wood", 0.02))
    for i, x in enumerate((-3.25, -2.85, -2.4)):
        f.append((f"Jar.{i:02d}", cyl_bm(x, 3.17, 1.4, 1.72, 0.12, 0.09), "dome", 0.01))
    f.append(("Crate.00", box_bm(0.7, 1.3, 2.6, 3.2, 0.0, 0.6), "wood_dark", 0.02))
    f.append(("Crate.01", box_bm(0.8, 1.2, 2.7, 3.1, 0.6, 1.0), "wood_dark", 0.02))
    f.append(("BedFrame", box_bm(4.0, 5.1, 1.4, 3.5, 0.0, 0.35), "wood", 0.02))
    f.append(("Mattress", box_bm(4.05, 5.05, 1.45, 3.45, 0.35, 0.5), "fabric", 0.04))
    f.append(("Pillow", box_bm(4.15, 4.95, 3.0, 3.4, 0.5, 0.6), "fabric", 0.04))
    f.append(("Chest", box_bm(4.1, 5.0, 0.6, 1.1, 0.0, 0.5), "wood_dark", 0.02))
    f.append(("Console", box_bm(2.4, 3.6, -3.9, -3.4, 0.0, 1.0, (0, 0, 0, 0.15)), "metal", 0.02))
    f.append(("ConsoleScreen", box_bm(2.6, 3.4, -3.46, -3.42, 0.5, 0.92), "screen", 0))
    f.append(("WallLamp.00", box_bm(-3.8, -3.7, 0.05, 0.35, 1.9, 2.1), "lamp", 0))
    f.append(("WallLamp.01", box_bm(5.1, 5.2, -1.15, -0.85, 1.7, 1.9), "lamp", 0))
    return f


def small_collision(R):
    box(R, "Floor", -4.2, 5.6, -4.3, 4.0, -T, 0.0, floor=0.0)
    walls(R, [("WallFront", -4.2, 2.0, -3.5, -3.1), ("WallBack", -4.2, 2.0, 3.4, 3.8),
              ("WallLeft", -4.2, -3.8, -3.5, 3.8),
              ("WallShared.00", 1.6, 2.0, -4.3, -0.5), ("WallShared.01", 1.6, 2.0, 1.0, 4.0),
              ("WingFront", 1.6, 5.6, -4.3, -3.9), ("WingBack", 1.6, 5.6, 3.6, 4.0), ("WingRight", 5.2, 5.6, -4.3, 4.0)])
    cyl(R, "Table", -1.1, 0.6, 0.0, 0.76, 0.6, floor=0.0)
    cyl(R, "Stool.00", -2.1, 0.1, 0.0, 0.45, 0.2, floor=0.0)
    cyl(R, "Stool.01", -0.1, 1.2, 0.0, 0.45, 0.2, floor=0.0)
    box(R, "Cabinet", -3.5, -2.1, 2.95, 3.4, 0.0, 1.72, floor=0.0)
    box(R, "Crates", 0.7, 1.3, 2.6, 3.2, 0.0, 1.0, floor=0.0)
    box(R, "Bed", 4.0, 5.1, 1.4, 3.5, 0.0, 0.6, floor=0.0)
    box(R, "Chest", 4.1, 5.0, 0.6, 1.1, 0.0, 0.5, floor=0.0)
    box(R, "Console", 2.4, 3.6, -3.9, -3.4, 0.0, 1.0, floor=0.0)


SMALL = dict(
    collection="TatooineHouse_Interior", root="ASSET_House1Interior",
    asset_id="tat_house_small_interior", exterior="tat_house_small", exterior_spawn="front",
    rooms=[('box', -3.8, 1.6, -3.1, 3.4, 0.0, 3.0), ('ell', -1.1, 0.15, 3.0, 2.3, 0.7),
           ('box', 2.0, 5.2, -3.9, 3.6, 0.0, 2.6)],
    outer=[('box', -4.2, 2.0, -3.5, 3.8, -T, 3.0 + T), ('ell', -1.1, 0.15, 3.0, 2.3 + T, (2.3 * 0.7 + T) / (2.3 + T)),
           ('box', 1.6, 5.6, -4.3, 4.0, -T, 2.6 + T)],
    cuts=[('archy', 0.25, 1.5, 0.0, 1.6, 1.5, 2.1),               # main room <-> wing
          ('archx', -2.0, 1.3, 0.0, 1.55, -3.3, -3.0),            # door recess
          ('cyl', -1.1, 0.15, 4.5, 4.85, 0.35)],                  # oculus at the dome top
    dome=(-1.1, 0.15, 3.0, 2.3),
    door=dict(cx=-2.0, y_w=-3.1, w=1.3, zs=1.55, top=2.2),
    skylight=(-1.1, 0.15, 4.85, 0.35),
    furniture=small_furniture(),
    lights=[("LightMain", (-1.1, 0.15, 2.6), 250.0), ("LightWing", (3.6, 0.0, 2.1), 120.0)],
    collision=small_collision,
    origin=(0.0, -40.0, 0.05), collision_origin=(0.0, -56.0, 0.05),   # +5 cm: clear of the ground plane
    camera=((-3.4, -2.7, 1.7), (0.8, 2.2, 1.3), 16),
)


# =========================== large house ===========================
def large_furniture():
    f = []
    f.append(("Rug", box_bm(0.2, 3.8, -0.4, 2.6, 0.0, 0.012), "rug", 0))
    f.append(("TableTop", box_bm(0.9, 3.1, 0.6, 1.6, 0.70, 0.76), "wood", 0.01))
    for i, x in enumerate((1.15, 2.85)):
        f.append((f"TableTrestle.{i:02d}", box_bm(x - 0.06, x + 0.06, 0.75, 1.45, 0.0, 0.70), "wood_dark", 0.005))
    for i, (y0, y1) in enumerate([(0.15, 0.45), (1.75, 2.05)]):
        f.append((f"Bench.{i:02d}", box_bm(1.0, 3.0, y0, y1, 0.0, 0.45, (0.02, 0.02, 0.02, 0.02)), "wood", 0.01))
    for i, (x0, x1) in enumerate([(-2.5, -1.0), (4.8, 6.3)]):
        f.append((f"Cabinet.{i:02d}", box_bm(x0, x1, 3.6, 4.1, 0.0, 2.0), "wood", 0.02))
        for j in range(3):
            x = x0 + 0.3 + j * (x1 - x0 - 0.6) / 2
            f.append((f"Jar.{i}{j}", cyl_bm(x, 3.85, 2.0, 2.32, 0.12, 0.09), "dome", 0.01))
    f.append(("Crate.00", box_bm(-2.9, -2.2, 2.6, 3.3, 0.0, 0.7), "wood_dark", 0.02))
    f.append(("Crate.01", box_bm(-2.8, -2.3, 2.7, 3.2, 0.7, 1.2), "wood_dark", 0.02))
    f.append(("Planter", cyl_bm(-2.3, -3.3, 0.0, 0.6, 0.5, 0.42), "floor", 0.02))
    f.append(("TowerConsole", box_bm(7.9, 8.8, -3.9, -3.4, 0.0, 1.0, (0, 0, 0.15, 0)), "metal", 0.02))
    f.append(("TowerScreen", box_bm(8.1, 8.6, -3.94, -3.9, 0.5, 0.92), "screen", 0))
    f.append(("TowerChair", cyl_bm(8.3, -4.5, 0.0, 0.45, 0.25, 0.22), "metal", 0.01))
    f.append(("BedFrame", box_bm(-7.6, -6.5, 1.3, 3.4, 0.0, 0.35), "wood", 0.02))
    f.append(("Mattress", box_bm(-7.55, -6.55, 1.35, 3.35, 0.35, 0.5), "fabric", 0.04))
    f.append(("Pillow", box_bm(-7.45, -6.65, 2.9, 3.3, 0.5, 0.6), "fabric", 0.04))
    f.append(("Wardrobe", box_bm(-5.2, -3.9, 3.1, 3.6, 0.0, 2.1), "wood", 0.02))
    f.append(("Chest", box_bm(-7.5, -6.6, 0.4, 0.9, 0.0, 0.5), "wood_dark", 0.02))
    f.append(("WallLamp.00", box_bm(1.85, 2.15, 4.0, 4.1, 2.5, 2.7), "lamp", 0))
    f.append(("WallLamp.01", box_bm(-0.65, -0.35, -4.1, -4.0, 2.3, 2.5), "lamp", 0))
    f.append(("WallLamp.02", box_bm(3.35, 3.65, -4.1, -4.0, 2.3, 2.5), "lamp", 0))
    f.append(("WallLamp.03", box_bm(-7.8, -7.7, -1.65, -1.35, 1.8, 2.0), "lamp", 0))
    return f


def large_collision(R):
    in_hall = lambda x, y: -3.5 <= x <= 7.5 and -4.5 <= y <= 4.5
    box(R, "Floor", -8.2, 9.8, -6.8, 4.5, -T, 0.0, floor=0.0)
    walls(R, [("HallFront", -3.5, 5.65, -4.5, -4.1), ("HallBack", -3.5, 7.5, 4.1, 4.5),
              ("HallRight", 7.1, 7.5, -2.65, 4.5),
              ("HallLeft.00", -3.5, -3.1, -4.5, 0.0), ("HallLeft.01", -3.5, -3.1, 1.6, 4.5),
              ("WingFront", -8.2, -3.1, -3.5, -3.1), ("WingBack", -8.2, -3.1, 3.6, 4.0), ("WingLeft", -8.2, -7.8, -3.5, 4.0)])
    ring(R, "TowerWall", 7.5, -4.5, 1.9, 2.3, 16, skip=in_hall)
    box(R, "Table", 0.9, 3.1, 0.6, 1.6, 0.0, 0.76, floor=0.0)
    box(R, "Bench.00", 1.0, 3.0, 0.15, 0.45, 0.0, 0.45, floor=0.0)
    box(R, "Bench.01", 1.0, 3.0, 1.75, 2.05, 0.0, 0.45, floor=0.0)
    box(R, "Cabinet.00", -2.5, -1.0, 3.6, 4.1, 0.0, 2.32, floor=0.0)
    box(R, "Cabinet.01", 4.8, 6.3, 3.6, 4.1, 0.0, 2.32, floor=0.0)
    box(R, "Crates", -2.9, -2.2, 2.6, 3.3, 0.0, 1.2, floor=0.0)
    cyl(R, "Planter", -2.3, -3.3, 0.0, 0.6, 0.5, floor=0.0)
    box(R, "TowerConsole", 7.9, 8.8, -3.9, -3.4, 0.0, 1.0, floor=0.0)
    cyl(R, "TowerChair", 8.3, -4.5, 0.0, 0.45, 0.25, floor=0.0)
    box(R, "Bed", -7.6, -6.5, 1.3, 3.4, 0.0, 0.6, floor=0.0)
    box(R, "Wardrobe", -5.2, -3.9, 3.1, 3.6, 0.0, 2.1, floor=0.0)
    box(R, "Chest", -7.5, -6.6, 0.4, 0.9, 0.0, 0.5, floor=0.0)


_ta = math.radians(-40)
LARGE = dict(
    collection="TatooineHouse_Large_Interior", root="ASSET_House2Interior",
    asset_id="tat_house_large_interior", exterior="tat_house_large", exterior_spawn="front",
    rooms=[('box', -3.1, 7.1, -4.1, 4.1, 0.0, 4.0), ('ell', 2.0, 0.0, 4.0, 3.5, 0.68),
           ('box', -7.8, -3.5, -3.1, 3.6, 0.0, 2.6), ('cyl', 7.5, -4.5, 0.0, 4.6, 1.9)],
    outer=[('box', -3.5, 7.5, -4.5, 4.5, -T, 4.0 + T), ('ell', 2.0, 0.0, 4.0, 3.5 + T, (3.5 * 0.68 + T) / (3.5 + T)),
           ('box', -8.2, -3.1, -3.5, 4.0, -T, 2.6 + T), ('cyl', 7.5, -4.5, -T, 4.6 + T, 1.9 + T)],
    cuts=[('archy', 0.8, 1.6, 0.0, 1.6, -3.6, -3.0),               # hall <-> wing
          ('archx', 1.5, 1.8, 0.0, 2.1, -4.3, -4.0),               # door recess
          ('cyl', 2.0, 0.0, 6.2, 6.6, 0.45),                       # oculus
          ('hcyl', -1.6, -4.1, 2.4, 0.35, 0.5, (0, 1)),            # round windows (recess 0.25 into the wall)
          ('hcyl', 4.6, -4.1, 2.4, 0.35, 0.5, (0, 1)),
          ('hcyl', 7.5 + 1.9 * math.cos(_ta), -4.5 + 1.9 * math.sin(_ta), 2.6, 0.35, 0.5, (-math.cos(_ta), -math.sin(_ta))),
          ('hcyl', -7.8, 0.25, 1.7, 0.3, 0.5, (1, 0))],
    dome=(2.0, 0.0, 4.0, 3.5),
    door=dict(cx=1.5, y_w=-4.1, w=1.8, zs=2.1, top=3.0),
    skylight=(2.0, 0.0, 6.6, 0.45),
    furniture=large_furniture(),
    lights=[("LightHall", (2.0, 0.0, 3.4), 500.0), ("LightWing", (-5.6, 0.2, 2.1), 150.0),
            ("LightTower", (7.5, -4.5, 3.0), 150.0)],
    collision=large_collision,
    origin=(-26.0, -40.0, 0.05), collision_origin=(-26.0, -58.0, 0.05),
    camera=((-2.7, -3.7, 1.8), (5.5, 2.8, 1.8), 16),
)

result = {k: build(s) for k, s in (("small", SMALL), ("large", LARGE))}
