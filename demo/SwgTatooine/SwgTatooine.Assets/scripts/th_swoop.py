import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Swoop (asset "swoop", kind "vehicle"): a one-seat hover bike after SWG's swoop. Twin boxy engine pods with red
# panels at the back, a ribbed seat, a front fairing with handlebars, two long fork rods down to a small steering
# vane, skids underneath. Modelled at its local origin, nose towards -Y like every asset's front, hovering: the skids
# are HOVER above z = 0.
#
# Sized around the seated Quaternius rider (TatooineCharacterRef, SitDown's last frame at the published scale):
# the rider's origin (feet, knees above it) is SEAT, so
#   - the feet rest on the footboards (top = SEAT z), the shins straddle a narrow spine (inner shin gap 0.15 m),
#   - the hips sit on the cushion (top = SEAT z + 0.216), the fairing stays 0.1 m ahead of the knees,
#   - the handlebar grips are where the fists reach forward (the pose's arms are not symmetrical: a riding pose is
#     still to make).
# SEAT_Swoop_driver marks it (seat_id; local +Y = the rider's facing; seat_clip / seat_clip_at = the character clip
# and time that make the pose). SW_Mount: where a player stands to get on (and gets off), left of the seat, facing
# the bike (interact_kind "mount"). Collision: th_collision.py. Re-runnable.

M_BODY = bpy.data.materials.get("TH_PanelKhaki")
if M_BODY is None:
    M_BODY, _, b = simple_mat("TH_PanelKhaki")
    b.inputs['Base Color'].default_value = (0.46, 0.40, 0.29, 1)
    b.inputs['Metallic'].default_value = 0.35; b.inputs['Roughness'].default_value = 0.55
    M_BODY.diffuse_color = (0.46, 0.40, 0.29, 1)
M_RED = bpy.data.materials["TH_StripRed"]          # th_terminals.py
M_GUN = bpy.data.materials["TH_PanelGunmetal"]     # th_terminals.py
M_GLOW = bpy.data.materials["TH_EngineGlow"]       # th_shuttle.py

HOVER = 0.33                                       # skid underside above the ground
SEAT = (0.0, 0.0, 0.62)                            # rider origin: feet on the footboards, knees above y = 0
CUSHION = SEAT[2] + 0.216                          # underside of the rider's thighs and hips
PLACE = (-30.0, 26.0)                              # editing-scene spot, next to the character reference

col = get_col("TatooineSwoop")
root = bpy.data.objects.new("ASSET_Swoop", None)
root.empty_display_type = 'ARROWS'; root.empty_display_size = 1.0
col.objects.link(root)
root["asset_id"] = "swoop"
root["asset_kind"] = "vehicle"
sc = bpy.context.scene
hit, loc, *_ = sc.ray_cast(bpy.context.evaluated_depsgraph_get(), Vector((*PLACE, 100.0)), Vector((0, 0, -1)))
root.location = (*PLACE, loc.z if hit else 0.0)


def section(hw, zb, zt):
    """Octagonal cross-section in X/Z (as th_shuttle.py)."""
    h = zt - zb
    return [(-0.6 * hw, zb), (0.6 * hw, zb), (hw, zb + 0.3 * h), (hw, zb + 0.75 * h),
            (0.55 * hw, zt), (-0.55 * hw, zt), (-hw, zb + 0.75 * h), (-hw, zb + 0.3 * h)]


def loft(stations):
    bm = bmesh.new()
    rings = [[bm.verts.new((x, y, z)) for x, z in section(hw, zb, zt)] for y, hw, zb, zt in stations]
    n = len(rings[0])
    for a, b in zip(rings, rings[1:]):
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((a[i], a[j], b[j], b[i]))
    bm.faces.new(list(reversed(rings[0]))); bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def rod(p0, p1, r, seg=6):
    """Cylinder of radius r from p0 to p1."""
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r, radius2=r, depth=d.length)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=d.to_track_quat('Z', 'Y').to_matrix())
    bmesh.ops.translate(bm, verts=bm.verts, vec=(p0 + p1) / 2)
    return bm


def ycyl(cx, cz, y0, y1, r0, r1=None, seg=10):
    return rod((cx, y0, cz), (cx, y1, cz), r0, seg) if r1 is None else _cone_y(cx, cz, y0, y1, r0, r1, seg)


def _cone_y(cx, cz, y0, y1, r0, r1, seg):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r0, radius2=r1, depth=y1 - y0)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(-90), 3, 'X'))
    bmesh.ops.translate(bm, verts=bm.verts, vec=(cx, (y0 + y1) / 2, cz))
    return bm


def part(name, bm, mat, bevel=0.015):
    ob = mk(name, bm, mat, col, bevel=bevel)
    ob.parent = root                               # local coordinates: no parent inverse
    unwrap_uv0(ob, root)
    return ob


fz = SEAT[2]
# front fairing: from a blunt nose to a rear face 0.1 m ahead of the knees (y -0.035)
part("SW_Fairing", loft([
    (-1.05, 0.07, fz + 0.00, fz + 0.10),
    (-0.75, 0.17, fz - 0.12, fz + 0.28),
    (-0.42, 0.21, fz - 0.16, fz + 0.38),
    (-0.14, 0.19, fz - 0.14, fz + 0.36),
]), M_BODY)
for s, t in ((-1, "L"), (1, "R")):
    part(f"SW_FairingPanel.{t}", prism([(-0.95, fz + 0.02), (-0.45, fz - 0.06), (-0.2, fz - 0.04), (-0.2, fz + 0.24),
                                        (-0.45, fz + 0.27)], s * 0.205, s * 0.215, 'YZ'), M_RED, bevel=0)
# spine between the shins (inner shin gap 0.148), footboards under the feet, cushion block under the hips
part("SW_Spine", box_bm(-0.055, 0.055, -0.2, 0.12, fz - 0.18, fz - 0.01), M_BODY)
for s, t in ((-1, "L"), (1, "R")):
    part(f"SW_Footboard.{t}", box_bm(min(s * 0.05, s * 0.25), max(s * 0.05, s * 0.25), -0.13, 0.15, fz - 0.035, fz),
         M_GUN, bevel=0.008)
part("SW_SeatBase", box_bm(-0.16, 0.16, 0.05, 0.52, fz - 0.16, CUSHION - 0.04, (0.02, 0.02, 0.06, 0.0)), M_BODY)
for i in range(5):                                  # ribbed cushion
    y0 = 0.08 + i * 0.085
    part(f"SW_SeatRib.{i}", box_bm(-0.14, 0.14, y0, y0 + 0.07, CUSHION - 0.04, CUSHION, (0.01, 0.01, 0.01, 0.01)),
         M_GUN, bevel=0.01)
# rear cowl behind the hips (back of the hips y 0.40), red stripe on top
part("SW_RearCowl", loft([
    (0.46, 0.16, fz - 0.16, CUSHION + 0.02),
    (0.62, 0.2, fz - 0.16, CUSHION + 0.13),
    (0.92, 0.22, fz - 0.12, CUSHION + 0.15),
]), M_BODY)
part("SW_CowlStripe", box_bm(-0.08, 0.08, 0.64, 0.9, CUSHION + 0.14, CUSHION + 0.16), M_RED, bevel=0)
# twin engine pods: red top and outer panels, nozzles with a glow
for s, t in ((-1, "L"), (1, "R")):
    x0, x1 = sorted((s * 0.11, s * 0.41))
    xc = (x0 + x1) / 2
    part(f"SW_Pod.{t}", box_bm(x0, x1, 0.82, 1.75, fz - 0.08, fz + 0.36, (0.03, 0.03, 0.08, 0.03)), M_BODY, bevel=0.03)
    part(f"SW_PodTop.{t}", box_bm(x0 + 0.04, x1 - 0.04, 0.95, 1.68, fz + 0.36, fz + 0.375), M_RED, bevel=0)
    xo = x1 if s > 0 else x0
    part(f"SW_PodSide.{t}", box_bm(min(xo, xo + s * 0.012), max(xo, xo + s * 0.012), 0.95, 1.62, fz - 0.02, fz + 0.3),
         M_RED, bevel=0)
    part(f"SW_Nozzle.{t}", _cone_y(xc, fz + 0.14, 1.75, 1.86, 0.12, 0.1, 10), M_GUN, bevel=0)
    part(f"SW_Glow.{t}", ycyl(xc, fz + 0.14, 1.855, 1.865, 0.095, seg=10), M_GLOW, bevel=0)
    part(f"SW_Fin.{t}", prism([(1.15, fz - 0.08), (1.6, fz - 0.08), (1.55, HOVER + 0.06), (1.35, HOVER + 0.06)],
                              xc - 0.015, xc + 0.015, 'YZ'), M_BODY, bevel=0)
# handlebars: stem from the fairing top, bar across, grips where the fists reach
hb = fz + 0.5
part("SW_Stem", rod((0, -0.36, fz + 0.36), (0, -0.3, hb), 0.025), M_GUN, bevel=0)
part("SW_Bar", rod((-0.2, -0.27, hb), (0.2, -0.27, hb), 0.018), M_GUN, bevel=0)
for s, t in ((-1, "L"), (1, "R")):
    part(f"SW_Grip.{t}", rod((s * 0.19, -0.27, hb), (s * 0.29, -0.22, hb + 0.04), 0.025), M_GUN, bevel=0)
# fork rods down to the steering vane
tip = Vector((0.0, -2.05, HOVER + 0.05))
for s, t in ((-1, "L"), (1, "R")):
    part(f"SW_Fork.{t}", rod((s * 0.15, -0.7, fz - 0.04), (s * 0.05, tip.y + 0.12, tip.z + 0.08), 0.022), M_GUN, bevel=0)
part("SW_VaneHub", rod((-0.07, tip.y + 0.1, tip.z + 0.08), (0.07, tip.y + 0.1, tip.z + 0.08), 0.045, 8), M_GUN, bevel=0)
part("SW_Vane", prism([(tip.y - 0.12, tip.z), (tip.y + 0.22, tip.z - 0.02), (tip.y + 0.2, tip.z + 0.16),
                       (tip.y - 0.02, tip.z + 0.14)], -0.17, 0.17, 'YZ'), M_BODY, bevel=0.01)
part("SW_VaneStripe", prism([(tip.y - 0.1, tip.z + 0.05), (tip.y + 0.12, tip.z + 0.04), (tip.y + 0.12, tip.z + 0.09),
                             (tip.y - 0.06, tip.z + 0.1)], -0.175, 0.175, 'YZ'), M_RED, bevel=0)
# skids and their struts
for s, t in ((-1, "L"), (1, "R")):
    x = s * 0.17
    part(f"SW_Skid.{t}", rod((x, -0.6, HOVER + 0.02), (x, 0.8, HOVER + 0.02), 0.02), M_GUN, bevel=0)
    for k, y in enumerate((-0.45, 0.6)):
        part(f"SW_Strut.{t}{k}", rod((x, y, HOVER + 0.02), (s * 0.1, y, fz - 0.15), 0.015), M_GUN, bevel=0)

# rider seat marker (local +Y = facing, so turned to face -Y)
seat = bpy.data.objects.new("SEAT_Swoop_driver", None)
seat.empty_display_type = 'ARROWS'; seat.empty_display_size = 0.4
seat.location = SEAT; seat.rotation_euler = (0, 0, math.pi)
seat["seat_id"] = "driver"
seat["seat_clip"] = "SitDown"; seat["seat_clip_at"] = "end"
col.objects.link(seat); seat.parent = root

# mount point: on the ground left of the seat, facing the bike (+X)
mount = bpy.data.objects.new("SW_Mount", None)
mount.empty_display_type = 'ARROWS'; mount.empty_display_size = 0.6
mount.location = (-0.9, 0.15, 0.0); mount.rotation_euler = (0, 0, -math.pi / 2)
mount["interact_kind"] = "mount"; mount["interact_radius"] = 1.5; mount["interact_seat"] = "driver"
col.objects.link(mount); mount.parent = root

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
