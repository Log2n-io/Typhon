import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

C = (32.0, 14.0, 0.0)
col = get_col("TatooineShuttleport")
M_STONE = bpy.data.materials['TH_Sandstone']; M_DOME = bpy.data.materials['TH_DomeStone']
M_DOOR = bpy.data.materials['TH_DoorWood']; M_METAL = bpy.data.materials['TH_Metal']

# --- new materials ---
M_DURA, nt, b = simple_mat("TH_Duracrete")
g = nt.nodes.new('ShaderNodeNewGeometry')
n = nt.nodes.new('ShaderNodeTexNoise'); n.inputs['Scale'].default_value = 0.8; n.inputs['Detail'].default_value = 10
nt.links.new(g.outputs['Position'], n.inputs['Vector'])
r = ramp(nt, n.outputs['Fac'], [(0.3, (0.20, 0.18, 0.15)), (0.7, (0.36, 0.32, 0.26))])
nt.links.new(r.outputs['Color'], b.inputs['Base Color'])
bp = nt.nodes.new('ShaderNodeBump'); bp.inputs['Strength'].default_value = 0.2
nt.links.new(n.outputs['Fac'], bp.inputs['Height']); nt.links.new(bp.outputs['Normal'], b.inputs['Normal'])
b.inputs['Roughness'].default_value = 0.85

M_PAINT, nt, b = simple_mat("TH_PadPaint")
b.inputs['Base Color'].default_value = (0.70, 0.30, 0.04, 1); b.inputs['Roughness'].default_value = 0.75

M_HULL, nt, b = simple_mat("TH_ShuttleHull")  # used by the droid body
g = nt.nodes.new('ShaderNodeNewGeometry')
v = nt.nodes.new('ShaderNodeTexVoronoi'); v.distance = 'CHEBYCHEV'; v.inputs['Scale'].default_value = 0.7
nt.links.new(g.outputs['Position'], v.inputs['Vector'])
rv = ramp(nt, v.outputs['Color'], [(0.0, (0.60, 0.59, 0.57)), (1.0, (0.72, 0.71, 0.69))])
n = nt.nodes.new('ShaderNodeTexNoise'); n.inputs['Scale'].default_value = 2.5; n.inputs['Detail'].default_value = 8
nt.links.new(g.outputs['Position'], n.inputs['Vector'])
rn = ramp(nt, n.outputs['Fac'], [(0.55, (1, 1, 1)), (0.85, (0.82, 0.76, 0.68))])
mx = nt.nodes.new('ShaderNodeMix'); mx.data_type = 'RGBA'; mx.blend_type = 'MULTIPLY'
sock(mx.inputs, 'Factor', 'VALUE').default_value = 1.0
nt.links.new(rv.outputs['Color'], sock(mx.inputs, 'A', 'RGBA')); nt.links.new(rn.outputs['Color'], sock(mx.inputs, 'B', 'RGBA'))
nt.links.new(sock(mx.outputs, 'Result', 'RGBA'), b.inputs['Base Color'])
b.inputs['Roughness'].default_value = 0.45; b.inputs['Metallic'].default_value = 0.2

M_GLASS, nt, b = simple_mat("TH_DarkGlass")
b.inputs['Base Color'].default_value = (0.015, 0.03, 0.05, 1); b.inputs['Roughness'].default_value = 0.08


def emissive(name, col_rgb, strength):
    m, nt, b = simple_mat(name)
    b.inputs['Base Color'].default_value = (*col_rgb, 1)
    b.inputs['Emission Color'].default_value = (*col_rgb, 1)
    b.inputs['Emission Strength'].default_value = strength
    return m


M_LAMP = emissive("TH_LampOrange", (1.0, 0.45, 0.12), 8.0)
M_SCREEN = emissive("TH_ScreenBlue", (0.2, 0.55, 1.0), 4.0)
M_BEACON = emissive("TH_BeaconRed", (1.0, 0.12, 0.05), 12.0)


def P(name, bm, mats, **kw):
    return mk(name, bm, mats, col, offset=C, **kw)


# --- plaza + ring wall ---
add_skirt(P("SP_Plaza", cyl_bm(0, 0, 0.0, 0.3, 21.6, 21.3, 128), M_STONE, bevel=0.08).data)
wall = P("SP_RingWall", cyl_bm(0, 0, 0.3, 3.0, 20.4, 20.2, 128), M_STONE, bevel=0)
for cutter in (cyl_bm(0, 0, 0.2, 3.2, 19.4, 19.4, 128),
               box_bm(-3.0, 3.0, -21.5, -17.5, 0.2, 3.5)):          # one gate (-Y)
    apply_bool(wall, P("tmp", cutter, M_STONE, bevel=0, smooth=False), 'DIFFERENCE')
bv = wall.modifiers.new("Bevel", 'BEVEL'); bv.width = 0.12; bv.segments = 3
bv.limit_method = 'ANGLE'; bv.angle_limit = math.radians(30); bv.harden_normals = True
for p in wall.data.polygons:
    p.use_smooth = True

# buttresses around the outside of the wall, skipping the gate (-Y at 270deg). In the scene the 180deg one is
# SP_Buttress.15 (added in place when the -X gate was closed), so the names do not follow the angles there.
k = 0
for deg in range(0, 360, 20):
    if abs((deg - 270 + 180) % 360 - 180) < 20:
        continue
    bm = box_bm(-0.5, 0.5, 20.0, 21.3, 0.3, 2.5, (0.05, 0.05, 0.0, 0.9))
    xform(bm, rot_z=math.radians(deg - 90))
    P(f"SP_Buttress.{k:02d}", bm, M_STONE, bevel=0.06); k += 1

# gate pylons + lintel
for i, (x, y) in enumerate([(-3.9, -19.9), (3.9, -19.9)]):
    P(f"SP_GatePylon.{i:02d}", box_bm(x-0.9, x+0.9, y-0.9, y+0.9, 0.3, 4.6, (0.2, 0.2, 0.2, 0.2)), M_STONE, bevel=0.08)
    P(f"SP_GatePylonCap.{i:02d}", dome_bm(x, y, 4.6, 0.55, 0.8, 32), M_DOME, bevel=0)
P("SP_GateLintel", box_bm(-3.3, 3.3, -20.5, -19.3, 3.9, 4.4, (0.05, 0.05, 0.05, 0.05)), M_STONE, bevel=0.06)

# --- landing pad ---
P("SP_Pad", cyl_bm(0, 0, 0.3, 1.2, 12.0, 11.6, 128), M_STONE, bevel=0.08)
P("SP_PadDeck", cyl_bm(0, 0, 1.2, 1.26, 11.3, 11.3, 128), M_DURA, bevel=0.02)
P("SP_MarkRingOuter", annulus_bm(0, 0, 1.26, 1.275, 9.6, 10.0, 128), M_PAINT, bevel=0, smooth=False)
P("SP_MarkRingInner", annulus_bm(0, 0, 1.26, 1.275, 4.2, 4.6, 96), M_PAINT, bevel=0, smooth=False)
for i in range(4):
    bm = box_bm(-0.2, 0.2, 4.6, 9.6, 1.26, 1.275)
    xform(bm, rot_z=math.radians(45 + 90 * i))
    P(f"SP_MarkBar.{i:02d}", bm, M_PAINT, bevel=0, smooth=False)
for i in range(4):
    P(f"SP_Step.{i:02d}", box_bm(-3.0, 3.0, -15.0 + 0.6 * i, -11.0, 0.3, 0.525 + 0.225 * i), M_STONE, bevel=0.03)
k = 0
for deg in range(0, 360, 22):
    if abs((deg - 270 + 180) % 360 - 180) < 22:
        continue
    a = math.radians(deg); x, y = 12.7 * math.cos(a), 12.7 * math.sin(a)
    P(f"SP_LampPost.{k:02d}", cyl_bm(x, y, 0.3, 0.9, 0.18, 0.15, 16), M_METAL, bevel=0.01)
    P(f"SP_Lamp.{k:02d}", cyl_bm(x, y, 0.9, 1.05, 0.13, 0.1, 16), M_LAMP, bevel=0); k += 1

# --- port office with dome ---
office = P("SP_Office", box_bm(-5.0, 3.0, 14.0, 18.6, 0.3, 4.3, (0.15, 0.15, 0.15, 0.15)), M_STONE, bevel=0.07)
apply_bool(office, P("tmp", prism(arch_pts(-1.0, 1.6, 0.25, 2.2), 13.6, 14.5), M_STONE, bevel=0, smooth=False), 'DIFFERENCE')
P("SP_OfficeDoor", prism(arch_pts(-1.0, 1.4, 0.3, 2.2), 14.44, 14.5), M_DOOR, bevel=0.01)
P("SP_OfficeDeck", box_bm(-4.7, 2.7, 14.3, 18.3, 4.3, 4.6, (0.12, 0.12, 0.12, 0.12)), M_STONE, bevel=0.07)
P("SP_OfficeDomeDrum", cyl_bm(-1.0, 16.3, 4.6, 4.8, 1.8, 1.76), M_DOME, bevel=0.03)
P("SP_OfficeDome", dome_bm(-1.0, 16.3, 4.8, 1.7, 0.75), M_DOME, bevel=0)

# --- control tower ---
tx, ty = 6.0, 15.5
P("SP_Tower", cyl_bm(tx, ty, 0.3, 9.0, 2.0, 1.7), M_STONE, bevel=0.05)
P("SP_TowerCab", cyl_bm(tx, ty, 9.0, 10.2, 2.3, 2.3), M_STONE, bevel=0.05)
P("SP_TowerWindows", cyl_bm(tx, ty, 9.3, 9.9, 2.33, 2.33), M_GLASS, bevel=0)
P("SP_TowerRoof", cyl_bm(tx, ty, 10.2, 10.45, 2.45, 2.4), M_STONE, bevel=0.05)
P("SP_TowerDome", dome_bm(tx, ty, 10.45, 1.6, 0.6), M_DOME, bevel=0)
P("SP_Antenna", cyl_bm(tx, ty, 11.3, 14.0, 0.06, 0.04, 12), M_METAL, bevel=0)
P("SP_Beacon", cyl_bm(tx, ty, 14.0, 14.3, 0.15, 0.1, 16), M_BEACON, bevel=0)

# --- ticket collector droid (the ticket terminal is its own asset: th_ticket_terminal.py) ---
dx, dy = -4.2, -15.4
P("SP_DroidBody", cyl_bm(dx, dy, 0.55, 1.3, 0.32, 0.3, 32), M_HULL, bevel=0.02)
P("SP_DroidHead", dome_bm(dx, dy, 1.3, 0.3, 0.9, 32), M_METAL, bevel=0)
P("SP_DroidEye", hcyl_bm(dx, dy - 0.27, 1.45, 0.06, 0.08, (0, -1), 12), M_SCREEN, bevel=0)
for i, s in enumerate((-1, 1)):
    P(f"SP_DroidLeg.{i}", box_bm(dx + s*0.33 - 0.06, dx + s*0.33 + 0.06, dy - 0.12, dy + 0.12, 0.35, 1.2), M_METAL, bevel=0.01)
    P(f"SP_DroidFoot.{i}", box_bm(dx + s*0.33 - 0.12, dx + s*0.33 + 0.12, dy - 0.22, dy + 0.22, 0.3, 0.4), M_METAL, bevel=0.01)

# --- cameras ---
look_cam("TatooineCamPort", (C[0], C[1] - 2.0, 3.0), 32, 16, 56, shift_y=0.05)
look_cam("TatooineCamWide", (8.0, 4.0, 3.0), 32, 13, 95)
scene.camera = bpy.data.objects["TatooineCamPort"]
result = {"objects": len(col.objects), "wall_faces": len(wall.data.polygons)}
