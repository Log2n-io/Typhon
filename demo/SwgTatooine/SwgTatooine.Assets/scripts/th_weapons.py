import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Hand weapons (kind "weapon"), held in a character's right fist (joint Fist.R; th_weapon_ref.py shows them held):
# three swords (SwordSlash), three blaster pistols (Shoot_OneHanded: the only shooting clip is one-handed, so no
# rifles), one vibroknuckler (Punch). Low poly, flat colours, after SWG's.
#
# Every weapon is modelled in its GRIP frame: origin = the centre of the grip, where the fist closes; +Z = along the
# grip towards the thumb (a sword's blade, a pistol's top); -Y = the knuckles' side (a blade's edge, the barrel, the
# knuckler's blade), the way the weapon points. One frame for all, so one grip transform per joint fits every weapon,
# turned by the weapon's grip_roll_deg about its Y axis: the clips hold the fist differently (SwordSlash thumb up,
# Shoot_OneHanded and Punch palm down), so a blaster rolls -90 deg to stand upright and the knuckler 180 deg to hook
# its blade up and out.
# The grip transform: GRIP_POS_M / GRIP_ROT place the grip frame in joint Fist.R's frame (the glTF joint's axes, which
# are the Blender bone's: +Y from the wrist through the knuckles), the position in metres (the character's scale
# removed), then grip_roll_deg about the weapon's Y. Stored on each root (grip_joint, grip_pos_m, grip_basis = the
# Blender-frame weapon's axes in joint axes, row-major); th_export.py turns it into the descriptor's weapon.attach,
# th_weapon_ref.py into a held instance.
# Markers (empties, weapon_marker; local +Y = forward, like every marker): "muzzle" on blasters, where a bolt leaves
# and its direction; "blade_base" and "blade_tip" on swords and the knuckler, for trails and hit tests.
#
# Each weapon has its own collection under TatooineWeapons (so th_weapon_ref.py can instance it into a hand), its
# ASSET_ root at the grip, displayed in a row. Re-runnable.

ROW = (-31.5, 40.0, 1.2)            # editing-scene spot of the first weapon, left of th_weapon_ref.py's characters;
GAP = 0.6                           # the row runs along +Y, so a view along X shows every profile


def mat(name, rgb, metal=0.0, rough=0.6, emit=0.0):
    m = bpy.data.materials.get(name)
    if m is None:
        m, _, b = simple_mat(name)
        b.inputs['Base Color'].default_value = (*rgb, 1)
        b.inputs['Metallic'].default_value = metal
        b.inputs['Roughness'].default_value = rough
        if emit:
            b.inputs['Emission Color'].default_value = (*rgb, 1)
            b.inputs['Emission Strength'].default_value = emit
        m.diffuse_color = (*rgb, 1)
    return m


M_STEEL = mat("TH_WpnSteel", (0.62, 0.64, 0.66), 0.8, 0.35)
M_DARK = mat("TH_WpnBlack", (0.06, 0.06, 0.065), 0.4, 0.5)
M_GRIP = mat("TH_WpnGrip", (0.13, 0.08, 0.05), 0.0, 0.9)
M_BRASS = mat("TH_WpnBrass", (0.55, 0.42, 0.22), 0.7, 0.45)
M_GLOW = mat("TH_WpnGlowBlue", (0.25, 0.6, 1.0), 0.0, 0.5, 1.4)
M_GUN = bpy.data.materials["TH_PanelGunmetal"]     # th_terminals.py
M_RED = bpy.data.materials["TH_StripRed"]          # th_terminals.py
M_KHAKI = bpy.data.materials["TH_PanelKhaki"]      # th_swoop.py

top = get_col("TatooineWeapons")


GRIP_ROLL = {"sword": 0.0, "blaster": -90.0, "knuckler": 180.0}
GRIP_CLIP = {"sword": "SwordSlash", "blaster": "Shoot_OneHanded", "knuckler": "Punch"}   # the character clip it attacks with
GRIP_POS_M = (0.0, 0.110, 0.021)                                  # the middle of the closed fist, joint frame, metres
GRIP_ROT = Matrix(((0, 0, -1), (0, -1, 0), (-1, 0, 0)))           # columns: the weapon's X, Y, Z in joint axes


def weapon(aid, cls, name, y):
    """A weapon's collection (child of TatooineWeapons) and ASSET_ root at its grip, y along the row."""
    c = bpy.data.collections.get(f"TatooineWeapon_{name}") or bpy.data.collections.new(f"TatooineWeapon_{name}")
    if c.name not in top.children:
        top.children.link(c)
    for o in list(c.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    r = bpy.data.objects.new(f"ASSET_{name}", None)
    r.empty_display_type = 'ARROWS'; r.empty_display_size = 0.1
    c.objects.link(r)
    r["asset_id"], r["asset_kind"], r["weapon_class"] = aid, "weapon", cls
    r["weapon_clip"], r["grip_joint"], r["grip_pos_m"] = GRIP_CLIP[cls], "Fist.R", GRIP_POS_M
    r["grip_basis"] = [v for row in GRIP_ROT @ Matrix.Rotation(math.radians(GRIP_ROLL[cls]), 3, 'Y') for v in row]
    r.location = (ROW[0], ROW[1] + y, ROW[2])
    c.instance_offset = r.location
    return c, r


def part(c, r, pname, bm, m):
    ob = mk(f"{r.name[6:]}_{pname}", bm, m, c, bevel=0, smooth=False)
    ob.parent = r
    return ob


def marker(c, r, kind, loc):
    e = bpy.data.objects.new(f"{kind.upper()}_{r.name[6:]}", None)
    e.empty_display_type = 'ARROWS'; e.empty_display_size = 0.04
    e.location = loc
    e.rotation_euler = (0, 0, math.pi)                   # local +Y (forward) = the weapon's -Y, the way it points
    e["weapon_marker"] = kind
    c.objects.link(e); e.parent = r
    return e


def zcyl(z0, z1, r0, r1=None, seg=8, x=0.0, y=0.0):
    return cyl_bm(x, y, z0, z1, r0, r1, seg)


def ycyl(y0, y1, r, z, seg=8, x=0.0):
    """Cylinder along Y (a barrel) from y0 to y1 at height z."""
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r, radius2=r, depth=abs(y1 - y0))
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, 'X'))
    bmesh.ops.translate(bm, verts=bm.verts, vec=(x, (y0 + y1) / 2, z))
    return bm


def flat(pts, t):
    """A blade-like plate: polygon pts = (y, z), thickness t across X."""
    return prism(pts, -t / 2, t / 2, plane='YZ')


def hilt(c, r, grip_r=0.016, pommel=0.022, guard=(0.035, 0.09, 0.03), m_guard=None):
    """Pommel, wrapped grip (z -0.07..0.07) and cross-guard (across the edge plane, along Y) up to z 0.10."""
    part(c, r, "Pommel", zcyl(-0.10, -0.07, pommel * 0.7, pommel, 8), m_guard or M_STEEL)
    part(c, r, "Grip", zcyl(-0.07, 0.07, grip_r, None, 8), M_GRIP)
    gx, gy, gz = guard
    part(c, r, "Guard", box_bm(-gx / 2, gx / 2, -gy, gy, 0.07, 0.07 + gz), m_guard or M_STEEL)


out = {}

# ---- sword 1: vibroblade. Straight, single edge, a blue power cell at the base of the spine.
c, r = weapon("sword_vibroblade", "sword", "SwordVibroblade", 0 * GAP)
hilt(c, r, guard=(0.03, 0.06, 0.025), m_guard=M_DARK)
part(c, r, "Blade", flat([(-0.03, 0.095), (0.018, 0.095), (0.018, 0.74), (-0.005, 0.82), (-0.03, 0.70)], 0.008),
     M_STEEL)
part(c, r, "Cell", box_bm(-0.009, 0.009, 0.006, 0.026, 0.11, 0.24), M_GLOW)
marker(c, r, "blade_base", (0, -0.006, 0.10)); marker(c, r, "blade_tip", (0, -0.005, 0.82))

# ---- sword 2: curved sword. A sabre arcing back (+Y), widening towards the tip, round guard.
c, r = weapon("sword_curved", "sword", "SwordCurved", 1 * GAP)
hilt(c, r, guard=(0.03, 0.05, 0.02), m_guard=M_BRASS)
n, L = 10, 0.72
edge, spine = [], []
for i in range(n + 1):
    t = i / n
    z = 0.09 + L * t
    bend = 0.11 * t * t                                  # the arc, towards the back
    w = 0.032 + 0.022 * math.sin(math.pi * min(t, 0.85) / 0.85 * 0.5)
    edge.append((-0.02 + bend, z))
    spine.append((-0.02 + bend + w, z))
spine[-1] = (edge[-1][0] + 0.004, edge[-1][1] + 0.02)   # the point, on the edge side
part(c, r, "Blade", flat(edge + spine[::-1], 0.007), M_STEEL)
marker(c, r, "blade_base", (0, 0.0, 0.09)); marker(c, r, "blade_tip", (0, edge[-1][0], 0.83))

# ---- sword 3: ryyk blade. Short and broad, a notched spine, a heavy square guard and long grip.
c, r = weapon("sword_ryyk", "sword", "SwordRyyk", 2 * GAP)
part(c, r, "Pommel", box_bm(-0.02, 0.02, -0.03, 0.03, -0.12, -0.08), M_DARK)
part(c, r, "Grip", zcyl(-0.08, 0.07, 0.018, None, 6), M_GRIP)
part(c, r, "Guard", box_bm(-0.025, 0.025, -0.07, 0.07, 0.07, 0.11), M_DARK)
part(c, r, "Blade", flat([(-0.05, 0.11), (0.05, 0.11), (0.05, 0.25), (0.035, 0.28), (0.05, 0.31), (0.05, 0.45),
                          (0.035, 0.48), (0.05, 0.51), (0.045, 0.62), (-0.01, 0.70), (-0.06, 0.60)], 0.012), M_STEEL)
marker(c, r, "blade_base", (0, -0.05, 0.11)); marker(c, r, "blade_tip", (0, -0.01, 0.70))

# ---- blasters: the grip raked back 12 deg; receiver above the fist, the barrel forward (-Y).
RAKE = math.radians(12)


def pistol_grip(c, r, m, h=0.13, d=0.032, w=0.026):
    bm = box_bm(-w / 2, w / 2, -d / 2, d / 2, -0.07, -0.07 + h)
    xform(bm, rot_x=-RAKE)
    part(c, r, "Grip", bm, m)


def trigger_guard(c, r, y0, y1, z, m):
    part(c, r, "TriggerGuard", box_bm(-0.005, 0.005, y0, y1, z - 0.006, z), m)
    part(c, r, "TriggerGuardFront", box_bm(-0.005, 0.005, y0, y0 + 0.008, z, z + 0.035), m)


# blaster 1: DL-44 heavy pistol. Long receiver, flash hider, a scope on top.
c, r = weapon("blaster_dl44", "blaster", "BlasterDL44", 3 * GAP)
pistol_grip(c, r, M_GRIP)
part(c, r, "Receiver", box_bm(-0.016, 0.016, -0.13, 0.05, 0.055, 0.105), M_DARK)
part(c, r, "Barrel", ycyl(-0.24, -0.13, 0.012, 0.08, 8), M_DARK)
part(c, r, "FlashHider", ycyl(-0.30, -0.24, 0.018, 0.08, 8), M_GUN)
part(c, r, "Scope", ycyl(-0.10, 0.03, 0.012, 0.13, 8), M_DARK)
part(c, r, "ScopeMount", box_bm(-0.006, 0.006, -0.06, -0.01, 0.105, 0.12), M_DARK)
part(c, r, "Cell", box_bm(0.016, 0.026, -0.11, -0.04, 0.06, 0.09), M_BRASS)
trigger_guard(c, r, -0.07, 0.0, 0.02, M_DARK)
marker(c, r, "muzzle", (0, -0.30, 0.08))

# blaster 2: DH-17 pistol. Boxy, square barrel shroud, a side magazine, red stripe.
c, r = weapon("blaster_dh17", "blaster", "BlasterDH17", 4 * GAP)
pistol_grip(c, r, M_DARK, h=0.12)
part(c, r, "Receiver", box_bm(-0.018, 0.018, -0.16, 0.04, 0.05, 0.11), M_GUN)
part(c, r, "Shroud", box_bm(-0.013, 0.013, -0.25, -0.16, 0.065, 0.097), M_GUN)
part(c, r, "Muzzle", box_bm(-0.016, 0.016, -0.265, -0.25, 0.062, 0.1), M_DARK)
part(c, r, "Magazine", box_bm(-0.05, -0.018, -0.12, -0.08, 0.07, 0.095), M_DARK)
part(c, r, "Stripe", box_bm(0.018, 0.0195, -0.15, 0.03, 0.085, 0.095), M_RED)
part(c, r, "Sight", box_bm(-0.004, 0.004, -0.15, -0.13, 0.11, 0.122), M_DARK)
trigger_guard(c, r, -0.07, 0.0, 0.02, M_DARK)
marker(c, r, "muzzle", (0, -0.265, 0.081))

# blaster 3: scout pistol. Compact and rounded, khaki body, a stubby barrel, glowing cell.
c, r = weapon("blaster_scout", "blaster", "BlasterScout", 5 * GAP)
pistol_grip(c, r, M_KHAKI, h=0.12, d=0.03)
part(c, r, "Body", ycyl(-0.13, 0.04, 0.028, 0.08, 10), M_KHAKI)
part(c, r, "Barrel", ycyl(-0.18, -0.13, 0.011, 0.075, 8), M_DARK)
part(c, r, "Cell", box_bm(-0.008, 0.008, -0.10, -0.02, 0.105, 0.115), M_GLOW)
trigger_guard(c, r, -0.065, 0.0, 0.025, M_DARK)
marker(c, r, "muzzle", (0, -0.18, 0.075))

# ---- vibroknuckler: a D-ring the fingers go through (grip bar inside the fist, a front bar over the knuckles), and
# a crescent blade along the front bar, hooking up past the thumb (after the SWG one).
c, r = weapon("vibroknuckler", "knuckler", "Vibroknuckler", 6 * GAP)
part(c, r, "GripBar", zcyl(-0.06, 0.06, 0.011, None, 6), M_DARK)
part(c, r, "FrontBar", box_bm(-0.012, 0.012, -0.07, -0.05, -0.075, 0.075), M_STEEL)
part(c, r, "Top", box_bm(-0.01, 0.01, -0.06, 0.0, 0.06, 0.075), M_STEEL)
part(c, r, "Bottom", box_bm(-0.01, 0.01, -0.06, 0.0, -0.075, -0.06), M_STEEL)
blade = [(-0.07, -0.075)]
for i in range(8):                                       # outer edge: a forward bulge, then up and back to the tip
    t = (i + 1) / 8
    blade.append((-0.07 - 0.05 * math.sin(math.pi * t * 0.8), -0.075 + 0.25 * t))
blade += [(-0.035, 0.175), (-0.07, 0.10), (-0.07, 0.075)]
part(c, r, "Blade", flat(blade, 0.006), M_STEEL)
part(c, r, "Edge", flat([(p[0] - 0.001, p[1]) for p in blade[2:8]] + [(p[0] + 0.012, p[1]) for p in blade[7:1:-1]],
                        0.0065), M_GLOW)
marker(c, r, "blade_base", (0, -0.07, -0.075)); marker(c, r, "blade_tip", (0, -0.035, 0.175))

bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()
for rr in [o for o in top.all_objects if o.get("asset_kind") == "weapon"]:
    tris, lo, hi = 0, Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for o in rr.children:
        if o.type == 'MESH':
            m = o.evaluated_get(dg).to_mesh(); m.calc_loop_triangles(); tris += len(m.loop_triangles)
            for v in m.vertices:
                p = o.matrix_local @ v.co
                lo = Vector(map(min, lo, p)); hi = Vector(map(max, hi, p))
            o.evaluated_get(dg).to_mesh_clear()
    out[rr["asset_id"]] = {"tris": tris, "size": [round(v, 3) for v in hi - lo]}
result = out
