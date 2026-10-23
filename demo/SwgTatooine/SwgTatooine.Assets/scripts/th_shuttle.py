import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Shuttle (asset "shuttle", kind "vehicle"): a low-poly take on the reference — a lofted fuselage with a wedge nose
# and a dark canopy (hull faces), two high side nacelles on pylons, two low intake pods, a swept dorsal fin, chrome nozzles with an
# engine glow, and three landing legs. Sized for the shuttleport pad (deck r 11.3 m): ~15 m long, ~7.3 m wide, the
# legs resting at local z = 0. Modelled at its local origin, nose towards -Y like every asset's front; the root is
# placed on the pad of the editing scene (shuttleport origin + deck top 1.26) so the fit can be seen, without being
# part of the shuttleport (each asset bakes and exports in isolation). Collision: th_collision.py. Re-runnable.

M_HULL = bpy.data.materials["TH_ShuttleHull"]      # shared library texture (panelled light grey)
M_GLASS = bpy.data.materials["TH_DarkGlass"]
M_TRIM = bpy.data.materials["TH_PanelGunmetal"]    # intakes, nozzles, legs (th_terminals.py)


def engine_glow():
    m, nt, b = simple_mat("TH_EngineGlow")
    b.inputs['Base Color'].default_value = (0.45, 0.75, 1.0, 1)
    b.inputs['Emission Color'].default_value = (0.45, 0.75, 1.0, 1)
    b.inputs['Emission Strength'].default_value = 1.5
    return m


M_GLOW = engine_glow()
PAD_TOP = 1.26                                     # shuttleport deck top, asset-local z


def section(hw, zb, zt):
    """Octagonal hull cross-section in X/Z: narrower flat belly and roof, vertical-ish sides."""
    h = zt - zb
    return [(-0.7 * hw, zb), (0.7 * hw, zb), (hw, zb + 0.35 * h), (hw, zb + 0.7 * h),
            (0.6 * hw, zt), (-0.6 * hw, zt), (-hw, zb + 0.7 * h), (-hw, zb + 0.35 * h)]


def loft(stations, glass=None):
    """Hull through cross-sections [(y, hw, zb, zt), ...], capped at both ends. `glass` maps a segment index (between
    station k and k+1) to the section edges that get material 1: the canopy is part of the hull's own faces, so it
    follows its shape exactly instead of being a plate laid on it."""
    glass = glass or {}
    bm = bmesh.new()
    rings = [[bm.verts.new((x, y, z)) for x, z in section(hw, zb, zt)] for y, hw, zb, zt in stations]
    n = len(rings[0])
    for k, (a, b) in enumerate(zip(rings, rings[1:])):
        for i in range(n):
            j = (i + 1) % n
            f = bm.faces.new((a[i], a[j], b[j], b[i]))
            f.material_index = 1 if i in glass.get(k, ()) else 0
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def ycyl(cx, cz, y0, y1, r0, r1=None, seg=10):
    """Cylinder / cone along Y from y0 (radius r0) to y1 (radius r1)."""
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r0, radius2=r0 if r1 is None else r1,
                          depth=y1 - y0)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(-90), 3, 'X'))
    bmesh.ops.translate(bm, verts=bm.verts, vec=(cx, (y0 + y1) / 2, cz))
    return bm


col = get_col("TatooineShuttle")
root = bpy.data.objects.new("ASSET_Shuttle", None)
root.empty_display_type = 'ARROWS'; root.empty_display_size = 2.0
root.location = (32.0, 14.0, PAD_TOP)
col.objects.link(root)
root["asset_id"] = "shuttle"
root["asset_kind"] = "vehicle"


def part(name, bm, mat, bevel=0.04):
    ob = mk(name, bm, mat, col, bevel=bevel)
    ob.parent = root                               # local coordinates: no parent inverse
    unwrap_uv0(ob, root)
    return ob


# fuselage: wedge nose at -Y, widest amidships, tapering to the tail; canopy = the upper faces (upper sides and roof,
# section edges 3-4, 4-5, 5-6) of the segment behind the nose
part("SH_Fuselage", loft([
    (-7.6, 0.35, 2.25, 2.55),
    (-6.4, 1.25, 1.9, 3.05),
    (-4.4, 1.8, 1.7, 3.7),
    (-1.0, 2.0, 1.6, 4.0),
    (3.0, 1.9, 1.6, 3.9),
    (6.0, 1.5, 1.8, 3.5),
    (7.2, 1.15, 2.0, 3.2),
], glass={1: (3, 4, 5)}), [M_HULL, M_GLASS])
# dorsal spine and swept fin
part("SH_Spine", box_bm(-0.45, 0.45, -1.5, 5.5, 3.95, 4.25, (0.1, 0.1, 0.3, 0.3)), M_HULL)
part("SH_Fin", prism([(3.2, 4.2), (6.9, 3.4), (7.7, 6.4), (6.5, 6.4)], -0.14, 0.14, 'YZ'), M_HULL, bevel=0.03)

for side, tag in ((-1, "L"), (1, "R")):
    # high nacelle on a pylon, with a dark intake ring, chrome nozzle and glow
    x = side * 2.9
    part(f"SH_Nacelle.{tag}", ycyl(x, 3.0, -2.2, 5.8, 0.72, 0.68), M_HULL)
    part(f"SH_Intake.{tag}", ycyl(x, 3.0, -2.45, -2.2, 0.6, 0.72), M_TRIM, bevel=0)
    part(f"SH_Nozzle.{tag}", ycyl(x, 3.0, 5.8, 6.9, 0.5, 0.62), M_TRIM, bevel=0)
    part(f"SH_Glow.{tag}", ycyl(x, 3.0, 6.86, 6.92, 0.5), M_GLOW, bevel=0)
    part(f"SH_Pylon.{tag}", box_bm(min(side * 1.7, side * 2.4), max(side * 1.7, side * 2.4), -0.5, 3.5, 2.75, 3.15),
         M_HULL)
    # low intake pod along the belly
    xl = side * 2.35
    part(f"SH_Pod.{tag}", ycyl(xl, 2.0, -1.4, 4.2, 0.62, 0.55), M_HULL)
    part(f"SH_PodIntake.{tag}", ycyl(xl, 2.0, -1.6, -1.4, 0.5, 0.62), M_TRIM, bevel=0)
    part(f"SH_PodNozzle.{tag}", ycyl(xl, 2.0, 4.2, 4.9, 0.42, 0.5), M_TRIM, bevel=0)
    part(f"SH_PodGlow.{tag}", ycyl(xl, 2.0, 4.86, 4.92, 0.42), M_GLOW, bevel=0)

# landing legs: one under the nose, two aft; feet at z = 0
for i, (lx, ly) in enumerate([(0.0, -4.6), (-1.5, 3.6), (1.5, 3.6)]):
    part(f"SH_Leg.{i}", cyl_bm(lx, ly, 0.1, 1.75, 0.12), M_TRIM, bevel=0)
    part(f"SH_Foot.{i}", cyl_bm(lx, ly, 0.0, 0.1, 0.38, 0.32), M_TRIM, bevel=0)

result = {"root": root.name, "parts": len(root.children), "at": list(root.location)}
