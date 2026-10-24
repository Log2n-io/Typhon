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


def build_shell(name, col, rooms, outer, cuts, domes):
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
    for f in bm.faces:
        c = f.calc_center_median()
        if f.normal.z > 0.9 and abs(c.z) < 0.02:
            f.material_index = 1
        elif any(c.z > dcz + 0.01 and (c.x - dcx) ** 2 + (c.y - dcy) ** 2 <= (dr + 0.05) ** 2
                 for dcx, dcy, dcz, dr in domes):
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

    # one dome / skylight (the houses' "dome", "skylight") or several ("domes", "skylights")
    domes = spec.get("domes") or [spec["dome"]]
    shell, culled = build_shell(f"{short}_Shell", acol, spec["rooms"], spec["outer"], spec["cuts"], domes)
    pieces = [shell]
    d = spec["door"]
    pieces += door_leaf(short, acol, d["cx"], d["y_w"], d["w"], d["zs"])
    for i, s in enumerate(c for c in spec["cuts"] if c[0] == 'hcyl'):
        pieces.append(window_pane(f"{short}_Window.{i:02d}", acol, s))
    for i, (sx, sy, sz, sr) in enumerate(spec.get("skylights") or [spec["skylight"]]):
        pieces.append(mk(f"{short}_Skylight" + (f".{i:02d}" if i else ""), cyl_bm(sx, sy, sz - 0.01, sz, sr),
                         MAT["daylight"], acol, bevel=0))
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


# =========================== town hall ===========================
# Inside th_townhall.py's volumes (same plan coordinates). Vestibule x +-3 (y -10..-6.4, 5 m, small dome) through a
# 3 m arch into the assembly hall x +-7, y -6..8: walls 8 m under a dome to 12.9 m with an oculus, two colonnades
# (x +-4.6) leaving an open floor for crowds, a stage across the back (0.3 m, a ramp-like step) with the council table,
# chairs, lectern and a great banner, a brass chandelier. Four side rooms (3.6 m, small domes), each through an arch
# in a hall side wall: archives and the mayor's office west, the clerk's office and a lounge east.
def lib_mat(name, rgb, rough=0.8, metal=0.0):
    """Get-or-create flat material shared with th_townhall.py (same values there)."""
    m = bpy.data.materials.get(name)
    if m is None:
        m, _, b = simple_mat(name)
        b.inputs['Base Color'].default_value = (*rgb, 1); b.inputs['Roughness'].default_value = rough
        b.inputs['Metallic'].default_value = metal
        m.diffuse_color = (*rgb, 1)
    return m


MAT["banner"] = lib_mat("TH_Banner", (0.36, 0.05, 0.04), 0.9)
MAT["brass"] = lib_mat("TH_Brass", (0.62, 0.46, 0.2), 0.4, 0.8)
MAT["leaf"] = flat_mat("TH_Leaf", (0.16, 0.26, 0.10), 0.9)

TH_COLUMNS = [(sx * 4.6, y) for sx in (-1, 1) for y in (-3.5, -0.5, 2.5, 5.5)]
TH_STAGE_Y, TH_STAGE_H = 5.4, 0.3
CLERK_CONSOLES = [(-5.45, -4.25), (-1.7, -0.5)]                      # the first against the filing cabinets: no niche
ARCHIVE_STOOLS = [(-10.5, -1.6), (-8.9, -2.8)]                       # none between table and shelf: no pocket


def th_banner_y(x, y_wall, z_top, z_bot, w, face):
    """Banner on a wall facing +-Y (face = the side it hangs on, +1: towards +Y), pointed foot."""
    h = w / 2
    pts = [(x - h, z_top), (x + h, z_top), (x + h, z_bot + 0.35), (x, z_bot), (x - h, z_bot + 0.35)]
    return prism(pts, *sorted((y_wall, y_wall + face * 0.03)))


def th_banner_x(y, x_wall, z_top, z_bot, w, face):
    h = w / 2
    pts = [(y - h, z_top), (y + h, z_top), (y + h, z_bot + 0.35), (y, z_bot), (y - h, z_bot + 0.35)]
    return prism(pts, *sorted((x_wall, x_wall + face * 0.03)), 'YZ')


def shrub(x, y, r=0.4, h=1.0):
    """Planter with a conical shrub: [(bmesh, material key, bevel)] x 2."""
    return [(cyl_bm(x, y, 0.0, 0.6, r, r - 0.06), "floor", 0.02),
            (cyl_bm(x, y, 0.6, 0.6 + h, r + 0.05, 0.05, seg=7), "leaf", 0)]


def townhall_furniture():
    f = []
    add = lambda name, bm, mat, bevel=0: f.append((name, bm, mat, bevel))
    # ---- assembly hall ----
    for k, (x, y) in enumerate(TH_COLUMNS):
        add(f"ColumnBase.{k:02d}", cyl_bm(x, y, 0.0, 0.3, 0.45), "wall", 0.02)
        add(f"ColumnShaft.{k:02d}", cyl_bm(x, y, 0.3, 7.3, 0.32), "wall")
        add(f"ColumnCapital.{k:02d}", cyl_bm(x, y, 7.3, 7.7, 0.32, 0.5), "wall")
        add(f"ColumnAbacus.{k:02d}", box_bm(x - 0.55, x + 0.55, y - 0.55, y + 0.55, 7.7, 8.0), "wall", 0.02)
    add("Stage", box_bm(-7.0, 7.0, TH_STAGE_Y, 8.0, 0.0, TH_STAGE_H), "wood", 0.02)
    add("StageStep", box_bm(-7.0, 7.0, TH_STAGE_Y - 0.4, TH_STAGE_Y, 0.0, TH_STAGE_H / 2), "wood", 0.02)
    s = TH_STAGE_H
    add("CouncilTable", box_bm(-3.0, 3.0, 6.2, 7.0, s, s + 0.72), "wood_dark", 0.02)
    add("CouncilTableTop", box_bm(-3.1, 3.1, 6.15, 7.05, s + 0.72, s + 0.78), "wood", 0.01)
    for i, x in enumerate((-2.4, -1.2, 0.0, 1.2, 2.4)):
        back = 2.1 if x == 0.0 else 1.6                              # the mayor's seat stands out
        add(f"CouncilSeat.{i}", box_bm(x - 0.28, x + 0.28, 7.2, 7.7, s, s + 0.48), "banner", 0.02)
        add(f"CouncilBack.{i}", box_bm(x - 0.28, x + 0.28, 7.7, 7.85, s, s + back), "wood_dark", 0.01)
    add("CouncilCrest", hcyl_bm(0.0, 7.68, s + 1.85, 0.14, 0.04, (0, 1), seg=10), "brass")
    add("Lectern", box_bm(-0.35, 0.35, 5.55, 5.95, s, s + 1.05), "wood_dark", 0.01)
    add("LecternTop", box_bm(-0.42, 0.42, 5.5, 6.0, s + 1.05, s + 1.12, (0, 0, 0.1, 0)), "wood", 0.01)
    add("LecternEmblem", hcyl_bm(0.0, 5.54, s + 0.65, 0.17, 0.03, (0, 1), seg=10), "brass")
    add("GreatBanner", th_banner_y(0.0, 7.97, 6.9, 2.7, 2.2, -1), "banner")
    add("GreatBannerRod", hcyl_bm(0.0, 7.93, 6.95, 0.04, 2.6, (1, 0), seg=6), "brass")
    add("GreatBannerEmblem", hcyl_bm(0.0, 7.93, 5.5, 0.5, 0.03, (0, 1), seg=16), "brass")
    for i, sx in enumerate((-1, 1)):
        add(f"WallBanner.{i}", th_banner_x(1.0, sx * 6.97, 5.0, 2.5, 1.0, -sx), "banner")
        add(f"WallBannerRod.{i}", hcyl_bm(sx * 6.93, 1.0, 5.05, 0.03, 1.3, (0, 1), seg=6), "brass")
        add(f"WallBannerEmblem.{i}", hcyl_bm(sx * 6.94, 1.0, 4.2, 0.22, 0.03, (1, 0), seg=12), "brass")
        add(f"HallBench.{i}", box_bm(*sorted((sx * 6.5, sx * 6.95)), -1.5, 3.3, 0.0, 0.45), "wood", 0.02)
        for bm, mat, bevel in shrub(sx * 2.3, -5.55):
            add(f"HallPlanter.{i}{mat[0]}", bm, mat, bevel)
    add("Runner", box_bm(-1.2, 1.2, -9.8, TH_STAGE_Y - 0.4, 0.0, 0.012), "rug")
    # chandelier: brass ring under the dome on four rods, eight lamps
    add("ChandelierRing", annulus_bm(0.0, 1.0, 6.9, 7.05, 2.0, 2.25, seg=24), "brass")
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        add(f"ChandelierRod.{i}", cyl_bm(2.12 * math.cos(a), 1.0 + 2.12 * math.sin(a), 7.05, 12.6, 0.03, seg=6), "brass")
    for i in range(8):
        a = i * math.pi / 4
        add(f"ChandelierLamp.{i}", cyl_bm(2.12 * math.cos(a), 1.0 + 2.12 * math.sin(a), 7.05, 7.3, 0.12, 0.08, seg=6),
            "lamp")
    for i, (x, y0, y1) in enumerate([(-5.0, -5.97, -5.87), (5.0, -5.97, -5.87), (-6.0, 7.87, 7.97), (6.0, 7.87, 7.97)]):
        add(f"HallSconce.{i}", box_bm(x - 0.15, x + 0.15, y0, y1, 3.0, 3.25), "lamp")
    # ---- vestibule ----
    add("NoticeFrame", box_bm(-3.0, -2.9, -9.1, -7.3, 0.9, 2.1), "metal", 0.01)
    add("NoticeScreen", box_bm(-2.9, -2.88, -9.0, -7.4, 1.0, 2.0), "screen")
    add("VestibuleBench", box_bm(2.5, 2.95, -9.2, -7.4, 0.0, 0.45), "wood", 0.02)
    for i, x in enumerate((-2.2, 2.2)):
        add(f"VestibuleSconce.{i}", box_bm(x - 0.15, x + 0.15, -9.97, -9.87, 3.0, 3.25), "lamp")
    # ---- archives (west front) ----
    for i, (y0, y1) in enumerate([(-5.7, 0.3)]):                    # one run: a gap would be a niche no bot leaves
        add(f"Shelf.{i}", box_bm(-13.55, -13.05, y0, y1, 0.0, 2.2), "wood", 0.02)
        for j, (z, mat) in enumerate([(0.4, "rug"), (0.95, "fabric"), (1.5, "rug")]):
            add(f"Books.{i}{j}", box_bm(-13.05, -12.98, y0 + 0.1, y1 - 0.1, z, z + 0.4), mat)
    add("ReadingTable", box_bm(-11.5, -9.5, -3.5, -2.1, 0.70, 0.76), "wood", 0.01)
    for i, x in enumerate((-11.24, -9.76)):
        add(f"ReadingTrestle.{i}", box_bm(x - 0.06, x + 0.06, -3.4, -2.2, 0.0, 0.70), "wood_dark", 0.005)
    for i, (x, y) in enumerate([(-10.9, -2.6), (-10.2, -3.0)]):
        add(f"Scroll.{i}", hcyl_bm(x, y, 0.81, 0.05, 0.5, (1, 0.3), seg=6), "fabric")
    for i, (x, y) in enumerate(ARCHIVE_STOOLS):
        add(f"ArchiveStool.{i}", cyl_bm(x, y, 0.0, 0.45, 0.2, 0.18), "wood", 0.01)
    add("RecordConsole", box_bm(-12.8, -11.8, -5.95, -5.45, 0.0, 1.0, (0, 0, 0.15, 0)), "metal", 0.02)
    add("RecordScreen", box_bm(-12.6, -12.0, -5.46, -5.42, 0.5, 0.92), "screen")
    add("ArchiveCrate.00", box_bm(-8.4, -7.75, -5.9, -5.25, 0.0, 0.65), "wood_dark", 0.02)
    add("ArchiveCrate.01", box_bm(-8.3, -7.85, -5.8, -5.35, 0.65, 1.0), "wood_dark", 0.02)
    add("ArchiveLamp", box_bm(-10.65, -10.35, 0.5, 0.6, 2.4, 2.6), "lamp")
    # ---- mayor's office (west back) ----
    add("OfficeRug", box_bm(-12.4, -8.6, 2.2, 6.6, 0.0, 0.012), "rug")
    add("Desk", box_bm(-12.0, -9.4, 5.3, 6.1, 0.0, 0.74), "wood_dark", 0.02)
    add("DeskTop", box_bm(-12.08, -9.32, 5.25, 6.15, 0.74, 0.8), "wood", 0.01)
    add("DeskTerminal", box_bm(-11.0, -10.4, 5.9, 6.0, 0.8, 1.15), "metal", 0.005)
    add("DeskScreen", box_bm(-10.95, -10.45, 5.86, 5.9, 0.85, 1.1), "screen")
    add("MayorSeat", box_bm(-11.0, -10.4, 6.5, 7.0, 0.0, 0.5), "banner", 0.02)
    add("MayorBack", box_bm(-11.0, -10.4, 7.0, 7.12, 0.0, 1.3), "wood_dark", 0.01)
    for i, x in enumerate((-11.6, -9.8)):
        add(f"VisitorSeat.{i}", box_bm(x - 0.25, x + 0.25, 4.0, 4.5, 0.0, 0.45), "fabric", 0.02)
        add(f"VisitorBack.{i}", box_bm(x - 0.25, x + 0.25, 3.88, 4.0, 0.0, 0.9), "wood", 0.01)
    add("Bookcase", box_bm(-13.55, -13.05, 2.0, 4.0, 0.0, 2.0), "wood", 0.02)
    add("BookcaseBooks", box_bm(-13.05, -12.98, 2.1, 3.9, 0.9, 1.3), "rug")
    add("StrongChest", box_bm(-13.4, -12.6, 7.35, 7.9, 0.0, 0.5), "wood_dark", 0.02)
    for bm, mat, bevel in shrub(-8.1, 7.3, 0.35, 0.9):
        add(f"OfficePlant.{mat[0]}", bm, mat, bevel)
    add("OfficeLamp", box_bm(-13.6, -13.5, 6.85, 7.15, 2.2, 2.4), "lamp")
    # ---- clerk's office (east front) ----
    add("Counter", box_bm(9.6, 10.0, -5.0, -0.6, 0.0, 1.02), "wood_dark", 0.02)
    add("CounterTop", box_bm(9.5, 10.15, -5.05, -0.55, 1.02, 1.1), "wood", 0.01)
    for i, (y0, y1) in enumerate(CLERK_CONSOLES):
        add(f"ClerkConsole.{i}", box_bm(13.05, 13.55, y0, y1, 0.0, 1.0, (0.15, 0, 0, 0)), "metal", 0.02)
        add(f"ClerkScreen.{i}", box_bm(13.01, 13.05, y0 + 0.2, y1 - 0.2, 0.5, 0.92), "screen")
    add("FilingCabinets", box_bm(11.8, 13.4, -5.95, -5.5, 0.0, 1.3), "metal", 0.02)
    add("ClerkStool", cyl_bm(11.0, -2.8, 0.0, 0.62, 0.22, 0.2), "metal", 0.01)
    add("WaitingBench", box_bm(7.9, 9.2, -5.95, -5.5, 0.0, 0.45), "wood", 0.02)
    add("CounterSign", box_bm(9.75, 9.85, -3.3, -2.3, 2.3, 2.7), "screen")
    add("ClerkLamp", box_bm(8.4, 8.7, -5.97, -5.87, 2.3, 2.5), "lamp")
    # ---- lounge (east back) ----
    add("LoungeRug", cyl_bm(10.6, 4.4, 0.0, 0.012, 1.9), "rug")
    add("LoungeTableTop", cyl_bm(10.6, 4.4, 0.70, 0.76, 0.85), "wood", 0.01)
    add("LoungeTableLeg", cyl_bm(10.6, 4.4, 0.0, 0.70, 0.12), "wood_dark")
    add("LoungeTableFoot", cyl_bm(10.6, 4.4, 0.0, 0.05, 0.4), "wood_dark", 0.01)
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        add(f"LoungeStool.{i}", cyl_bm(10.6 + 1.3 * math.cos(a), 4.4 + 1.3 * math.sin(a), 0.0, 0.45, 0.22, 0.2),
            "fabric", 0.01)
    add("CouchSeat", box_bm(12.95, 13.55, 2.0, 4.6, 0.0, 0.45), "fabric", 0.03)
    add("CouchBack", box_bm(13.35, 13.55, 2.0, 4.6, 0.45, 0.9), "fabric", 0.03)
    for i, (y0, y1) in enumerate([(1.85, 2.0), (4.6, 4.75)]):
        add(f"CouchArm.{i}", box_bm(12.95, 13.55, y0, y1, 0.0, 0.65), "wood_dark", 0.01)
    add("Sideboard", box_bm(9.4, 11.6, 7.5, 7.95, 0.0, 0.95), "wood", 0.02)
    for i, x in enumerate((9.8, 10.5, 11.2)):
        add(f"SideboardJar.{i}", cyl_bm(x, 7.72, 0.95, 1.27, 0.12, 0.09), "dome", 0.01)
    for bm, mat, bevel in shrub(8.0, 7.4, 0.35, 0.9):
        add(f"LoungePlant.{mat[0]}", bm, mat, bevel)
    add("LoungeLamp", box_bm(10.35, 10.65, 1.0, 1.1, 2.4, 2.6), "lamp")
    return f


def townhall_collision(R):
    box(R, "Floor", -14.0, 14.0, -10.4, 8.4, -T, 0.0, floor=0.0)
    walls(R, [("HallFront.00", -7.4, -1.5, -6.4, -6.0), ("HallFront.01", 1.5, 7.4, -6.4, -6.0),
              ("Back", -14.0, 14.0, 8.0, 8.4),
              ("HallW.00", -7.4, -7.0, -6.0, -3.6), ("HallW.01", -7.4, -7.0, -1.8, 3.6), ("HallW.02", -7.4, -7.0, 5.4, 8.0),
              ("HallE.00", 7.0, 7.4, -6.0, -3.6), ("HallE.01", 7.0, 7.4, -1.8, 3.6), ("HallE.02", 7.0, 7.4, 5.4, 8.0),
              ("WingWFront", -14.0, -7.4, -6.4, -6.0), ("WingEFront", 7.4, 14.0, -6.4, -6.0),
              ("WingWOuter", -14.0, -13.6, -6.0, 8.0), ("WingEOuter", 13.6, 14.0, -6.0, 8.0),
              ("WingWDivider", -13.6, -7.4, 0.6, 1.0), ("WingEDivider", 7.4, 13.6, 0.6, 1.0),
              ("VestibuleW", -3.4, -3.0, -10.4, -6.4), ("VestibuleE", 3.0, 3.4, -10.4, -6.4),
              ("VestibuleFront", -3.0, 3.0, -10.4, -10.0)])
    # hall
    for k, (x, y) in enumerate(TH_COLUMNS):
        cyl(R, f"Column.{k:02d}", x, y, 0.0, 8.0, 0.45, floor=0.0)
    s = TH_STAGE_H
    box(R, "Stage", -7.0, 7.0, TH_STAGE_Y, 8.0, 0.0, s, floor=0.0)
    ramp(R, "StageStep", (0.0, TH_STAGE_Y - 0.6, 0.0), (0, 1), run=s / ENTRY_SLOPE, rise=s, width=14.0, t=0.3,
         extend=0.0)                                                 # the two steps as one slope, like the houses'
    box(R, "CouncilTable", -3.1, 3.1, 6.15, 7.05, s, s + 0.78, floor=s)
    box(R, "CouncilSeats", -2.68, 2.68, 7.2, 7.85, s, s + 2.1, floor=s)
    box(R, "Lectern", -0.42, 0.42, 5.5, 6.0, s, s + 1.12, floor=s)
    for i, sx in enumerate((-1, 1)):
        box(R, f"HallBench.{i}", *sorted((sx * 6.5, sx * 6.95)), -1.5, 3.3, 0.0, 0.45, floor=0.0)
        cyl(R, f"HallPlanter.{i}", sx * 2.3, -5.55, 0.0, 1.6, 0.45, floor=0.0)
    # vestibule
    box(R, "NoticeBoard", -3.0, -2.88, -9.1, -7.3, 0.9, 2.1, floor=0.0)
    box(R, "VestibuleBench", 2.5, 2.95, -9.2, -7.4, 0.0, 0.45, floor=0.0)
    # archives
    box(R, "Shelf", -13.55, -12.98, -5.7, 0.3, 0.0, 2.2, floor=0.0)
    box(R, "ReadingTable", -11.5, -9.5, -3.5, -2.1, 0.0, 0.86, floor=0.0)
    for i, (x, y) in enumerate(ARCHIVE_STOOLS):
        cyl(R, f"ArchiveStool.{i}", x, y, 0.0, 0.45, 0.2, floor=0.0)
    box(R, "RecordConsole", -12.8, -11.8, -5.95, -5.42, 0.0, 1.0, floor=0.0)
    box(R, "ArchiveCrates", -8.4, -7.75, -5.9, -5.25, 0.0, 1.0, floor=0.0)
    # mayor's office
    box(R, "Desk", -12.08, -9.32, 5.25, 6.15, 0.0, 1.15, floor=0.0)
    box(R, "MayorChair", -11.0, -10.4, 6.5, 7.12, 0.0, 1.3, floor=0.0)
    for i, x in enumerate((-11.6, -9.8)):
        box(R, f"VisitorChair.{i}", x - 0.25, x + 0.25, 3.88, 4.5, 0.0, 0.9, floor=0.0)
    box(R, "Bookcase", -13.55, -12.98, 2.0, 4.0, 0.0, 2.0, floor=0.0)
    box(R, "StrongChest", -13.4, -12.6, 7.35, 7.9, 0.0, 0.5, floor=0.0)
    cyl(R, "OfficePlant", -8.1, 7.3, 0.0, 1.5, 0.4, floor=0.0)
    # clerk's office
    box(R, "Counter", 9.5, 10.15, -5.05, -0.55, 0.0, 1.1, floor=0.0)
    for i, (y0, y1) in enumerate(CLERK_CONSOLES):
        box(R, f"ClerkConsole.{i}", 13.01, 13.55, y0, y1, 0.0, 1.0, floor=0.0)
    box(R, "FilingCabinets", 11.8, 13.4, -5.95, -5.5, 0.0, 1.3, floor=0.0)
    cyl(R, "ClerkStool", 11.0, -2.8, 0.0, 0.62, 0.22, floor=0.0)
    box(R, "WaitingBench", 7.9, 9.2, -5.95, -5.5, 0.0, 0.45, floor=0.0)
    # lounge
    cyl(R, "LoungeTable", 10.6, 4.4, 0.0, 0.76, 0.85, floor=0.0)
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        cyl(R, f"LoungeStool.{i}", 10.6 + 1.3 * math.cos(a), 4.4 + 1.3 * math.sin(a), 0.0, 0.45, 0.22, floor=0.0)
    box(R, "Couch", 12.95, 13.55, 1.85, 4.75, 0.0, 0.9, floor=0.0)
    box(R, "Sideboard", 9.4, 11.6, 7.5, 7.95, 0.0, 1.27, floor=0.0)
    cyl(R, "LoungePlant", 8.0, 7.4, 0.0, 1.5, 0.4, floor=0.0)


_hall_ell = ('ell', 0.0, 1.0, 8.0, 6.5, 0.75)
_vest_ell = ('ell', 0.0, -8.2, 5.0, 1.7, 0.6)
_side_ells = [('ell', sx * 10.5, y, 3.6, 2.6, 0.55) for sx in (-1, 1) for y in (-2.7, 4.5)]


def _outer_ell(e):
    _, cx, cy, cz, r, sz = e
    return ('ell', cx, cy, cz, r + T, (r * sz + T) / (r + T))


TOWNHALL = dict(
    collection="TatooineTownhall_Interior", root="ASSET_TownhallInterior",
    asset_id="tat_townhall_interior", exterior="tat_townhall", exterior_spawn="front",
    rooms=[('box', -7.0, 7.0, -6.0, 8.0, 0.0, 8.0), _hall_ell,
           ('box', -3.0, 3.0, -10.0, -6.4, 0.0, 5.0), _vest_ell,
           ('box', -13.6, -7.4, -6.0, 0.6, 0.0, 3.6), ('box', -13.6, -7.4, 1.0, 8.0, 0.0, 3.6),
           ('box', 7.4, 13.6, -6.0, 0.6, 0.0, 3.6), ('box', 7.4, 13.6, 1.0, 8.0, 0.0, 3.6)] + _side_ells,
    outer=[('box', -7.4, 7.4, -6.4, 8.4, -T, 8.0 + T), _outer_ell(_hall_ell),
           ('box', -3.4, 3.4, -10.4, -6.0, -T, 5.0 + T), _outer_ell(_vest_ell),
           ('box', -14.0, -7.0, -6.4, 8.4, -T, 3.6 + T), ('box', 7.0, 14.0, -6.4, 8.4, -T, 3.6 + T)]
          + [_outer_ell(e) for e in _side_ells],
    cuts=[('archx', 0.0, 3.0, 0.0, 3.4, -6.5, -5.9),                 # vestibule <-> hall
          ('archx', 0.0, 2.4, 0.0, 2.6, -10.2, -9.9),                # door recess
          ('cyl', 0.0, 1.0, 12.5, 13.3, 0.8)]                         # oculus
         + [('archy', y, 1.8, 0.0, 2.2, sx * 7.2 - 0.3, sx * 7.2 + 0.3) for sx in (-1, 1) for y in (-2.7, 4.5)]
         + [('hcyl', sx * 7.0, y, 6.0, 0.55, 0.5, (-sx, 0)) for sx in (-1, 1) for y in (-3.5, 1.0, 5.5)]   # clerestory
         + [('hcyl', x, 8.0, 6.0, 0.55, 0.5, (0, -1)) for x in (-4.5, 4.5)]
         + [('hcyl', sx * 10.5, -6.0, 1.7, 0.35, 0.5, (0, 1)) for sx in (-1, 1)]                       # side rooms
         + [('hcyl', sx * 10.5, 8.0, 1.7, 0.35, 0.5, (0, -1)) for sx in (-1, 1)]
         + [('hcyl', -13.6, 5.8, 1.7, 0.35, 0.5, (1, 0)), ('hcyl', 13.6, 6.0, 1.7, 0.35, 0.5, (-1, 0)),
            ('hcyl', 13.6, -2.6, 1.7, 0.35, 0.5, (-1, 0))],
    domes=[(0.0, 1.0, 8.0, 6.5), (0.0, -8.2, 5.0, 1.7)] + [(e[1], e[2], 3.6, 2.6) for e in _side_ells],
    door=dict(cx=0.0, y_w=-10.0, w=2.4, zs=2.6, top=3.8),
    skylights=[(0.0, 1.0, 13.3, 0.8)],
    furniture=townhall_furniture(),
    lights=[("LightHall", (0.0, 1.0, 6.6), 1500.0), ("LightStage", (0.0, 6.0, 4.5), 400.0),
            ("LightFloor", (0.0, -3.5, 4.5), 400.0), ("LightVestibule", (0.0, -8.2, 4.0), 250.0),
            ("LightArchives", (-10.5, -2.7, 2.8), 180.0), ("LightOffice", (-10.5, 4.5, 2.8), 180.0),
            ("LightClerk", (10.5, -2.7, 2.8), 180.0), ("LightLounge", (10.5, 4.5, 2.8), 180.0)],
    collision=townhall_collision,
    origin=(30.0, -45.0, 0.05), collision_origin=(30.0, -72.0, 0.05),
    camera=((0.0, -5.6, 1.7), (0.0, 6.0, 4.0), 16),
)

# Builds every interior; INTERIORS_ONLY = {"townhall", ...} set before exec builds a subset. Rebuilding one drops its
# NAV_* nodes and exit approach with its collection: re-run th_interior_nav.py for it after.
ONLY = globals().get("INTERIORS_ONLY")
result = {k: build(s) for k, s in (("small", SMALL), ("large", LARGE), ("townhall", TOWNHALL))
          if ONLY is None or k in ONLY}
