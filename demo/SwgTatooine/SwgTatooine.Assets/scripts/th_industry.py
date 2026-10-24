import bpy, os, random  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Industrial buildings (kind "building", no door, no interior), dark duracrete and steel plate:
# - factory (~5.6 x 4.7 m, half the small house's plinth each way): steel shed under a three-tooth sawtooth roof
#   (glazing facing +Y), a fabrication window and console at the front where the player starts manufacturing
#   (interaction "manufacture"), an output chute and bin, a fan housing (-X), a power box and pipes (+X), a chimney.
# - resource_extractor (~6.7 x 5.6 m, the factory x 1.2): a drill derrick over a wellhead (casing, drill pipe, drive
#   housing, crown block and beacon), an ore silo on legs over an ore bin, an inclined conveyor from a feeder box up to
#   the silo, a generator with its exhaust stack behind the derrick, a fuel tank.
# Each stands on a low duracrete pad (low enough to step onto: no ramp) that continues FOUNDATION_D below z 0. Asset
# local, front towards -Y. Textures: TH_DarkDuracrete and TH_SteelPlate, shared library entries whose procedural
# sources (SRC_*) this script defines; after changing a source, rebake with th_texlib.py (ONLY = [...]) and bump the
# textures in revisions.json. Flat gunmetal / hazard / emissive details. Collision: th_collision.py. Re-runnable.

SEED = 31
PLACE = {"factory": (12.0, -30.0), "resource_extractor": (24.0, -30.0)}   # editing-scene spots, south of the small house
rng = random.Random(SEED)


def flat(name, rgb, rough=0.8, metal=0.0, emit=0.0):
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


# ---- texture library sources (baked by th_texlib.py; position-driven, 4 m periodic grids so the tile is seamless) ----
def library(name, rough, metal, build):
    """SRC_<name> (procedural, fake user) rebuilt by build(nt, bsdf); TH_<name> created flat if not baked yet."""
    src, nt, b = simple_mat("SRC_" + name)
    src.use_fake_user = True
    b.inputs['Roughness'].default_value = rough
    b.inputs['Metallic'].default_value = metal
    build(nt, b)
    m = bpy.data.materials.get("TH_" + name)
    if m is None:
        m, _, tb = simple_mat("TH_" + name)
        tb.inputs['Roughness'].default_value = rough
        tb.inputs['Metallic'].default_value = metal
    return m


def plane_xy(nt):
    """(x, z) of the bake plane as a 2D vector: the brick texture reads x, y."""
    g = nt.nodes.new('ShaderNodeNewGeometry')
    s = nt.nodes.new('ShaderNodeSeparateXYZ'); nt.links.new(g.outputs['Position'], s.inputs['Vector'])
    c = nt.nodes.new('ShaderNodeCombineXYZ')
    nt.links.new(s.outputs['X'], c.inputs['X']); nt.links.new(s.outputs['Z'], c.inputs['Y'])
    return g, c


def grid(nt, vec, w, h, mortar, offset, colour=False):
    br = nt.nodes.new('ShaderNodeTexBrick')
    nt.links.new(vec, br.inputs['Vector'])
    br.offset, br.offset_frequency = offset, 2
    br.inputs['Scale'].default_value = 1.0
    br.inputs['Mortar Size'].default_value = mortar
    br.inputs['Mortar Smooth'].default_value = 0.3
    br.inputs['Brick Width'].default_value = w
    br.inputs['Row Height'].default_value = h
    br.inputs['Bias'].default_value = 0.0
    for k, v in (('Color1', 0.9), ('Color2', 1.1), ('Mortar', 0.0)):
        br.inputs[k].default_value = (v, v, v, 1)
    return br


def mix(nt, a, b, fac, mode='MIX'):
    m = nt.nodes.new('ShaderNodeMix'); m.data_type = 'RGBA'; m.blend_type = mode
    for sock_, v in ((m.inputs[0], fac), (m.inputs[6], a), (m.inputs[7], b)):
        if isinstance(v, float):
            sock_.default_value = v
        elif isinstance(v, tuple):
            sock_.default_value = v
        else:
            nt.links.new(v, sock_)
    return m.outputs[2]


def bump(nt, b, height, strength):
    bp = nt.nodes.new('ShaderNodeBump')
    bp.inputs['Strength'].default_value = strength; bp.inputs['Distance'].default_value = 0.002
    nt.links.new(height, bp.inputs['Height']); nt.links.new(bp.outputs['Normal'], b.inputs['Normal'])


def math_node(nt, op, a, b):
    m = nt.nodes.new('ShaderNodeMath'); m.operation = op
    for s, v in zip(m.inputs, (a, b)):
        if isinstance(v, float):
            s.default_value = v
        else:
            nt.links.new(v, s)
    return m.outputs[0]


def dark_duracrete(nt, b):
    """Cool dark grey cast concrete, mottled, with formwork joints every 2 m x 1 m."""
    g, xy = plane_xy(nt)
    n = nt.nodes.new('ShaderNodeTexNoise'); nt.links.new(g.outputs['Position'], n.inputs['Vector'])
    n.inputs['Scale'].default_value = 0.8; n.inputs['Detail'].default_value = 10.0
    base = ramp(nt, n.outputs['Fac'], [(0.3, (0.042, 0.044, 0.047)), (0.7, (0.10, 0.10, 0.098))]).outputs['Color']
    br = grid(nt, xy.outputs['Vector'], 2.0, 1.0, 0.012, 0.0)
    col = mix(nt, base, (0.02, 0.021, 0.022, 1.0), br.outputs['Fac'])
    nt.links.new(col, b.inputs['Base Color'])
    bump(nt, b, math_node(nt, 'SUBTRACT', n.outputs['Fac'], math_node(nt, 'MULTIPLY', br.outputs['Fac'], 0.6)), 0.25)


def steel_plate(nt, b):
    """Cool grey steel plates (1 m x 2 m, staggered), each slightly tinted, recessed seams, soft mottling (no
    streaks: stretched noise reads as wood grain)."""
    g, xy = plane_xy(nt)
    br = grid(nt, xy.outputs['Vector'], 1.0, 2.0, 0.01, 0.5)
    br.inputs['Color1'].default_value = (0.85, 0.85, 0.85, 1); br.inputs['Color2'].default_value = (1.12, 1.12, 1.12, 1)
    n = nt.nodes.new('ShaderNodeTexNoise'); n.inputs['Scale'].default_value = 1.5; n.inputs['Detail'].default_value = 6.0
    nt.links.new(g.outputs['Position'], n.inputs['Vector'])
    base = ramp(nt, n.outputs['Fac'], [(0.3, (0.15, 0.16, 0.175)), (0.75, (0.21, 0.22, 0.235))]).outputs['Color']
    tinted_ = mix(nt, base, br.outputs['Color'], 1.0, 'MULTIPLY')
    col = mix(nt, tinted_, (0.035, 0.036, 0.04, 1.0), br.outputs['Fac'])
    nt.links.new(col, b.inputs['Base Color'])
    bump(nt, b, math_node(nt, 'SUBTRACT', 1.0, br.outputs['Fac']), 0.4)


M_CONC = library("DarkDuracrete", 0.85, 0.0, dark_duracrete)
M_STEEL = library("SteelPlate", 0.45, 0.35, steel_plate)
M_GUN = bpy.data.materials["TH_PanelGunmetal"]      # th_terminals.py
M_RED = bpy.data.materials["TH_StripRed"]           # th_terminals.py
M_GREEN = bpy.data.materials["TH_ScreenGreen"]      # th_terminals.py
M_AMBER = bpy.data.materials["TH_ScreenAmber"]      # th_terminals.py
M_WINDOW = bpy.data.materials["TH_WindowDark"]
M_HAZARD = flat("TH_HazardYellow", (0.62, 0.42, 0.04), 0.6)
M_BLACK = flat("TH_HazardBlack", (0.025, 0.025, 0.025), 0.7)
M_BEACON = flat("TH_BeaconRed", (1.0, 0.08, 0.04), 0.4, 0.0, 1.5)
M_ORE = flat("TH_Ore", (0.42, 0.22, 0.10), 0.95)


# ---- builders ----
def asset(aid, name):
    col = get_col(f"Tatooine{name}")
    root = bpy.data.objects.new(f"ASSET_{name}", None)
    root.empty_display_type = 'ARROWS'; root.empty_display_size = 1.0
    col.objects.link(root)
    root["asset_id"], root["asset_kind"] = aid, "building"
    hit, loc, *_ = scene.ray_cast(bpy.context.evaluated_depsgraph_get(), Vector((*PLACE[aid], 100.0)), Vector((0, 0, -1)))
    root.location = (*PLACE[aid], loc.z if hit else 0.0)
    return col, root


def part(col, root, name, bm, mats, bevel=0.0):
    ob = mk(name, bm, mats, col, bevel=bevel, smooth=False)
    ob.parent = root                               # local coordinates: no parent inverse
    return ob


def rod(p0, p1, r0, r1, seg=6):
    """Tapered rod from p0 (radius r0) to p1 (radius r1)."""
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r0, radius2=r1, depth=d.length)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=d.to_track_quat('Z', 'Y').to_matrix())
    bmesh.ops.translate(bm, verts=bm.verts, vec=(p0 + p1) / 2)
    return bm


def beam(p0, p1, w, h):
    """Box beam from p0 to p1, w wide, h high (its width stays horizontal)."""
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    bm = box_bm(0.0, d.length, -w / 2, w / 2, -h / 2, h / 2)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=d.to_track_quat('X', 'Z').to_matrix())
    bmesh.ops.translate(bm, verts=bm.verts, vec=p0)
    return bm


def rock(c, size):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=1.0)
    for v in bm.verts:
        v.co *= rng.uniform(0.8, 1.1)
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


def stripes_x(x0, x1, y0, y1, z0, z1, w=0.3):
    """Hazard band along x: alternating yellow (0) / black (1) blocks of width w."""
    n = max(2, round((x1 - x0) / w))
    return merge([tinted(box_bm(x0 + (x1 - x0) * i / n, x0 + (x1 - x0) * (i + 1) / n, y0, y1, z0, z1), i % 2)
                  for i in range(n)])


def pad(col, root, pre, hx, hy, top):
    ob = part(col, root, f"{pre}_Pad", box_bm(-hx, hx, -hy, hy, 0.0, top, (0.05, 0.05, 0.05, 0.05)), M_CONC, bevel=0.03)
    add_skirt(ob.data)
    return ob


def finish(root):
    bpy.context.view_layer.update()
    for o in root.children:
        if o.type == 'MESH':
            unwrap_uv0(o, root)
    dg = bpy.context.evaluated_depsgraph_get()
    tris, lo, hi = 0, Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for o in root.children:
        if o.type == 'MESH':
            e = o.evaluated_get(dg); m = e.to_mesh(); m.calc_loop_triangles(); tris += len(m.loop_triangles)
            for v in m.vertices:
                lo = Vector(map(min, lo, v.co)); hi = Vector(map(max, hi, v.co))
            e.to_mesh_clear()
    return {"root": root.name, "at": [round(v, 3) for v in root.location], "parts": len(root.children), "tris": tris,
            "lo": [round(v, 2) for v in lo], "hi": [round(v, 2) for v in hi]}


result = {}

# =============== factory ===============
# Pad x +-2.8, y +-2.35, top 0.25. Body x +-2.4, y -1.2..2.1 on a duracrete base course to 0.85, walls to 3.0, eave
# beam, sawtooth teeth (ridges along x, 0.83 m high). Front: fabrication window + console (the stand point in front),
# output chute into a bin on the right.
col, root = asset("factory", "Factory")
PL, WT, EAVE = 0.25, 3.0, 3.12
pad(col, root, "FA", 2.8, 2.35, PL)
part(col, root, "FA_Hazard", stripes_x(-2.7, 2.7, -2.3, -2.16, PL, PL + 0.02), [M_HAZARD, M_BLACK])
part(col, root, "FA_BaseCourse", box_bm(-2.5, 2.5, -1.3, 2.2, PL, PL + 0.6), M_CONC, bevel=0.03)
part(col, root, "FA_Body", box_bm(-2.4, 2.4, -1.2, 2.1, PL + 0.6, WT), M_STEEL, bevel=0.02)
part(col, root, "FA_Band", box_bm(-2.42, 2.42, -1.22, 2.12, 2.72, 2.82), M_RED)
cols = [box_bm(sx * 2.4 - 0.1, sx * 2.4 + 0.1, y - 0.1, y + 0.1, PL + 0.6, EAVE) for sx in (-1, 1) for y in (-1.2, 2.1)]
part(col, root, "FA_Columns", merge(cols), M_GUN, bevel=0.01)
part(col, root, "FA_Eave", box_bm(-2.5, 2.5, -1.3, 2.2, WT, EAVE), M_GUN, bevel=0.01)
teeth, glass, mull = [], [], []
for k in range(3):
    ya, yb = -1.2 + 1.1 * k, -1.2 + 1.1 * (k + 1)
    teeth.append(prism([(ya, EAVE), (yb, EAVE), (yb, EAVE + 0.83), (yb - 0.1, EAVE + 0.83)], -2.4, 2.4, 'YZ'))
    glass.append(box_bm(-2.2, 2.2, yb, yb + 0.03, EAVE + 0.1, EAVE + 0.74))
    mull += [box_bm(x - 0.04, x + 0.04, yb, yb + 0.06, EAVE + 0.06, EAVE + 0.78) for x in (-2.24, -1.1, 0.0, 1.1, 2.24)]
part(col, root, "FA_Roof", merge(teeth), M_STEEL, bevel=0.02)
part(col, root, "FA_RoofGlass", merge(glass), M_WINDOW)
part(col, root, "FA_RoofMullions", merge(mull), M_GUN)

# fabrication window: frame, dark glass, the furnace glow along its sill
part(col, root, "FA_BayFrame", box_bm(-1.15, 1.15, -1.38, -1.2, PL + 0.6, 2.55), M_GUN, bevel=0.015)
part(col, root, "FA_BayGlass", box_bm(-0.95, 0.95, -1.40, -1.37, 1.2, 2.38), M_WINDOW)
part(col, root, "FA_BayGlow", box_bm(-0.95, 0.95, -1.41, -1.39, 1.2, 1.34), M_AMBER)
part(col, root, "FA_Console", box_bm(-0.55, 0.55, -1.75, -1.38, PL, 1.15, (0, 0, 0.18, 0)), M_GUN, bevel=0.015)
part(col, root, "FA_ConsoleScreen", xform(box_bm(-0.4, 0.4, -1.62, -1.44, 1.15, 1.17), rot_x=math.radians(12),
                                         pivot=(0, -1.44, 1.15)), M_GREEN)
part(col, root, "FA_ConsoleKeys", box_bm(-0.45, 0.45, -1.7, -1.62, 0.98, 1.03), M_AMBER)
# output: hopper on the wall, chute down to a bin
part(col, root, "FA_Hopper", box_bm(1.45, 2.15, -1.5, -1.2, 1.55, 2.25, (0, 0, 0.12, 0)), M_GUN, bevel=0.015)
part(col, root, "FA_Chute", prism([(-1.45, 1.62), (-1.45, 1.78), (-1.95, 1.0), (-1.95, 0.84)], 1.55, 2.05, 'YZ'),
     M_STEEL)
part(col, root, "FA_Bin", box_bm(1.4, 2.2, -2.2, -1.65, PL, 0.8), M_GUN, bevel=0.015)
part(col, root, "FA_BinStripe", stripes_x(1.42, 2.18, -2.22, -2.2, 0.62, 0.74, 0.19), [M_HAZARD, M_BLACK])
# -X: fan housing
FY, FZ = 0.3, 1.65
part(col, root, "FA_FanHousing", box_bm(-2.75, -2.4, -0.6, 1.2, PL + 0.6, 2.45), M_GUN, bevel=0.015)
part(col, root, "FA_FanRing", hcyl_bm(-2.78, FY, FZ, 0.62, 0.06, (1, 0)), M_STEEL)
part(col, root, "FA_FanHole", hcyl_bm(-2.8, FY, FZ, 0.54, 0.04, (1, 0)), M_WINDOW)
blades = [xform(box_bm(-2.84, -2.82, FY - 0.06, FY + 0.06, FZ, FZ + 0.5), rot_x=a, pivot=(0, FY, FZ))
          for a in (0.3, 0.3 + 2 * math.pi / 3, 0.3 + 4 * math.pi / 3)]
blades.append(hcyl_bm(-2.83, FY, FZ, 0.12, 0.06, (1, 0), seg=8))
part(col, root, "FA_FanBlades", merge(blades), M_GUN)
# +X: power box, pipes into the wall
part(col, root, "FA_PowerBox", box_bm(2.4, 2.85, 0.4, 1.6, PL, 1.7), M_GUN, bevel=0.015)
part(col, root, "FA_PowerStripe", box_bm(2.85, 2.87, 0.5, 1.5, 1.4, 1.5), M_RED)
part(col, root, "FA_PowerLight", box_bm(2.85, 2.88, 0.55, 0.7, 1.2, 1.3), M_GREEN)
pipes = []
for y in (-0.6, -0.2):
    pipes += [cyl_bm(2.55, y, PL, 2.9, 0.08, seg=8), hcyl_bm(2.47, y, 2.9, 0.08, 0.2, (1, 0), seg=8),
              cyl_bm(2.55, y, 2.82, 2.98, 0.11, seg=8), cyl_bm(2.55, y, PL, PL + 0.12, 0.12, seg=8)]
part(col, root, "FA_Pipes", merge(pipes), M_STEEL)
# chimney (back left) through the roof, bands, cap, dark mouth; corner status lights
CX, CY = -1.7, 1.4
part(col, root, "FA_Chimney", cyl_bm(CX, CY, EAVE, 5.6, 0.3, seg=12), M_STEEL)
part(col, root, "FA_ChimneyBands", merge([annulus_bm(CX, CY, z, z + 0.12, 0.28, 0.36, seg=12) for z in (4.3, 5.35)]),
     M_GUN)
part(col, root, "FA_ChimneyCap", annulus_bm(CX, CY, 5.6, 5.72, 0.2, 0.38, seg=12), M_GUN)
part(col, root, "FA_ChimneyMouth", cyl_bm(CX, CY, 5.5, 5.7, 0.21, seg=12), M_WINDOW)
part(col, root, "FA_Lights", merge([box_bm(sx * 2.4 - 0.06, sx * 2.4 + 0.06, -1.36, -1.24, EAVE, EAVE + 0.1)
                                    for sx in (-1, 1)]), M_GREEN)

it = bpy.data.objects.new("FA_Interact", None)        # stand point in front of the console, local +Y faces it
it.empty_display_type = 'ARROWS'; it.empty_display_size = 0.5
it.location = (0.0, -2.1, PL)
col.objects.link(it); it.parent = root
it["interact_kind"] = "manufacture"
it["interact_radius"] = 1.8
result["factory"] = finish(root)

# =============== resource_extractor ===============
# Pad x +-3.35, y +-2.8, top 0.3. Derrick centred D over the wellhead (legs from +-1.25 at the pad to +-0.4 at 6.6 m),
# silo S (r 0.95) back right on legs over an ore bin, conveyor from the feeder box (front right) up to the silo top,
# generator behind the derrick, fuel tank front left.
col, root = asset("resource_extractor", "ResourceExtractor")
PL = 0.3
D, S = Vector((-1.0, 0.3)), Vector((2.05, 1.45))
DZ0, DZ1, DB, DT = PL, 6.6, 1.25, 0.4
pad(col, root, "RX", 3.35, 2.8, PL)
part(col, root, "RX_Hazard", stripes_x(-3.25, 3.25, -2.75, -2.61, PL, PL + 0.02), [M_HAZARD, M_BLACK])

# wellhead + drill
part(col, root, "RX_Collar", cyl_bm(D.x, D.y, PL, PL + 0.35, 1.0), M_CONC, bevel=0.03)
part(col, root, "RX_Casing", cyl_bm(D.x, D.y, PL + 0.35, 2.2, 0.4, seg=12), M_GUN)
part(col, root, "RX_Flanges", merge([cyl_bm(D.x, D.y, z, z + 0.15, 0.55, seg=12) for z in (1.0, 2.05)]), M_STEEL)
part(col, root, "RX_DrillPipe", cyl_bm(D.x, D.y, 2.2, 4.3, 0.14, seg=8), M_STEEL)
part(col, root, "RX_Drive", box_bm(D.x - 0.55, D.x + 0.55, D.y - 0.45, D.y + 0.45, 4.3, 5.15), M_STEEL, bevel=0.02)
part(col, root, "RX_DriveLid", box_bm(D.x - 0.6, D.x + 0.6, D.y - 0.5, D.y + 0.5, 5.15, 5.25), M_GUN)
part(col, root, "RX_DriveVent", box_bm(D.x - 0.4, D.x + 0.4, D.y - 0.47, D.y - 0.45, 4.5, 4.62), M_AMBER)
part(col, root, "RX_Cable", rod((D.x, D.y, 5.25), (D.x, D.y, DZ1), 0.025, 0.025, seg=4), M_BLACK)


def corner(sx, sy, z):
    h = DB + (DT - DB) * (z - DZ0) / (DZ1 - DZ0)
    return Vector((D.x + sx * h, D.y + sy * h, z))


CORNERS = [(-1, -1), (1, -1), (1, 1), (-1, 1)]
legs = [rod(corner(sx, sy, DZ0), corner(sx, sy, DZ1 + 0.05), 0.08, 0.06, seg=4) for sx, sy in CORNERS]
feet = [box_bm(*(lambda c: (c.x - 0.18, c.x + 0.18, c.y - 0.18, c.y + 0.18))(corner(sx, sy, DZ0)), PL, PL + 0.08)
        for sx, sy in CORNERS]
braces, levels = [], [PL + 0.25, 2.4, 4.4, 6.1]
for i in range(4):
    (ax, ay), (bx, by) = CORNERS[i], CORNERS[(i + 1) % 4]
    for z in levels:
        braces.append(rod(corner(ax, ay, z), corner(bx, by, z), 0.035, 0.035, seg=4))
    for z0, z1 in zip(levels, levels[1:]):
        braces.append(rod(corner(ax, ay, z0), corner(bx, by, z1), 0.03, 0.03, seg=4))
        braces.append(rod(corner(bx, by, z0), corner(ax, ay, z1), 0.03, 0.03, seg=4))
part(col, root, "RX_DerrickLegs", merge(legs + feet), M_GUN)
part(col, root, "RX_DerrickBraces", merge(braces), M_GUN)
part(col, root, "RX_Crown", box_bm(D.x - 0.55, D.x + 0.55, D.y - 0.55, D.y + 0.55, DZ1, DZ1 + 0.3), M_STEEL, bevel=0.02)
part(col, root, "RX_Sheave", hcyl_bm(D.x, D.y, DZ1 + 0.32, 0.3, 0.12, (1, 0), seg=12), M_GUN)
part(col, root, "RX_Beacon", cyl_bm(D.x + 0.4, D.y - 0.4, DZ1 + 0.3, DZ1 + 0.5, 0.08, seg=8), M_BEACON)

# silo on legs, hopper cone, spout, ore bin and heap
SR, SZ0, SZ1 = 0.95, 2.0, 3.9
silo_legs = [rod((S.x + sx * 0.68, S.y + sy * 0.68, PL), (S.x + sx * 0.6, S.y + sy * 0.6, SZ0 + 0.1), 0.07, 0.07, seg=4)
             for sx, sy in CORNERS]
part(col, root, "RX_SiloLegs", merge(silo_legs), M_GUN)
part(col, root, "RX_SiloCone", cyl_bm(S.x, S.y, 1.1, SZ0, 0.22, SR), M_STEEL)
part(col, root, "RX_Silo", cyl_bm(S.x, S.y, SZ0, SZ1, SR), M_STEEL, bevel=0.02)
part(col, root, "RX_SiloRings", merge([annulus_bm(S.x, S.y, z, z + 0.1, SR - 0.03, SR + 0.06) for z in (2.6, 3.3)]),
     M_GUN)
part(col, root, "RX_SiloRoof", cyl_bm(S.x, S.y, SZ1, SZ1 + 0.4, SR + 0.04, 0.3), M_GUN)
part(col, root, "RX_SiloHatch", cyl_bm(S.x, S.y, SZ1 + 0.4, SZ1 + 0.52, 0.32, seg=10), M_STEEL)
part(col, root, "RX_SiloStripe", merge([tinted(annulus_bm(S.x, S.y, 2.15, 2.3, SR - 0.02, SR + 0.02), 0)]), M_HAZARD)
part(col, root, "RX_Spout", cyl_bm(S.x, S.y, 0.85, 1.12, 0.17, seg=8), M_GUN)
part(col, root, "RX_OreBin", box_bm(S.x - 0.45, S.x + 0.45, S.y - 0.4, S.y + 0.4, PL, 0.7), M_GUN, bevel=0.015)
ore = [rock((S.x + rng.uniform(-0.25, 0.25), S.y + rng.uniform(-0.2, 0.2), 0.66 + rng.uniform(0, 0.08)),
            (rng.uniform(0.12, 0.2), rng.uniform(0.12, 0.2), rng.uniform(0.07, 0.11))) for _ in range(7)]
part(col, root, "RX_Ore", merge(ore), M_ORE)

# feeder box (front right), conveyor up to the silo top, two supports
F0 = Vector((0.5, -1.6, 1.3))
F1 = Vector((S.x - 0.35, S.y - 0.55, SZ1 + 0.45))
part(col, root, "RX_Feeder", box_bm(0.1, 0.9, -2.0, -1.2, PL, 1.25, (0.05, 0.05, 0.05, 0.05)), M_GUN, bevel=0.015)
part(col, root, "RX_FeederStripe", stripes_x(0.12, 0.88, -2.02, -2.0, 0.9, 1.02, 0.19), [M_HAZARD, M_BLACK])
part(col, root, "RX_FeederLight", box_bm(0.6, 0.78, -2.02, -2.0, 1.08, 1.16), M_GREEN)
part(col, root, "RX_Conveyor", beam(F0, F1, 0.5, 0.2), M_STEEL)
part(col, root, "RX_ConveyorBelt", beam(F0 + Vector((0, 0, 0.11)), F1 + Vector((0, 0, 0.11)), 0.4, 0.03), M_BLACK)
sup = []
for t in (0.33, 0.66):
    p = F0.lerp(F1, t)
    sup += [rod((p.x + dx, p.y, PL), (p.x + dx, p.y, p.z - 0.08), 0.05, 0.05, seg=4) for dx in (-0.22, 0.22)]
    sup.append(box_bm(p.x - 0.32, p.x + 0.32, p.y - 0.12, p.y + 0.12, PL, PL + 0.06))
part(col, root, "RX_ConveyorSupports", merge(sup), M_GUN)
part(col, root, "RX_WellPipe", rod((D.x + 0.35, D.y - 0.2, 0.9), (0.15, -1.45, 0.9), 0.12, 0.12, seg=8), M_STEEL)

# generator behind the derrick, exhaust stack, conduit up to the drive
part(col, root, "RX_Generator", box_bm(-2.9, -0.4, 1.85, 2.65, PL, 1.55), M_STEEL, bevel=0.02)
part(col, root, "RX_GeneratorLid", box_bm(-2.95, -0.35, 1.8, 2.7, 1.55, 1.65), M_GUN)
part(col, root, "RX_Grills", merge([box_bm(x, x + 0.5, 2.0, 2.5, 1.65, 1.68) for x in (-2.0, -1.35)]), M_BLACK)
part(col, root, "RX_GeneratorLight", box_bm(-0.9, -0.7, 1.83, 1.85, 1.2, 1.32), M_AMBER)
part(col, root, "RX_Exhaust", cyl_bm(-2.6, 2.25, 1.65, 3.6, 0.13, seg=8), M_GUN)
part(col, root, "RX_ExhaustCap", cyl_bm(-2.6, 2.25, 3.6, 3.72, 0.19, 0.14, seg=8), M_BLACK)
part(col, root, "RX_Conduit", rod((-0.8, 1.85, 1.4), (D.x, D.y + 0.45, 4.5), 0.06, 0.06, seg=6), M_BLACK)

# fuel tank (front left) on saddles, hazard band
part(col, root, "RX_Tank", hcyl_bm(-2.3, -2.0, 0.85, 0.45, 1.6, (1, 0), seg=12), M_STEEL)
part(col, root, "RX_TankBand", hcyl_bm(-2.3, -2.0, 0.85, 0.47, 0.18, (1, 0), seg=12), M_HAZARD)
part(col, root, "RX_TankSaddles", merge([box_bm(x - 0.08, x + 0.08, -2.35, -1.65, PL, 0.6) for x in (-2.85, -1.75)]),
     M_GUN)
result["resource_extractor"] = finish(root)
