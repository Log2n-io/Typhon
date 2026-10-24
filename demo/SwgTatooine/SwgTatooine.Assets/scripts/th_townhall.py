import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Town hall exterior (asset "tat_townhall", kind "building"); its interior is th_interiors.py TOWNHALL, its portal
# th_portals.py, its approach th_approaches.py, its collision th_collision.py. Same plan coordinates as the interior
# (asset-local, front -Y): every room of the interior sits inside the volume above it, walls T thick, the interior
# floor (z 0 there) at the plinth top P here.
#   - assembly hall x +-7.4, y -6.4..8.4, walls 8.4 m, under a 13.8 m dome (drum, oculus ring)
#   - two wings x 7.4..14.0 each side, 4 m, two small domes each (archives / mayor west, clerk / lounge east)
#   - vestibule x +-3.4, y -10.4..-6.4, 5.4 m, small dome, arched door under a pillared porch
#   - two towers flanking the vestibule, 11.5 m, domed, with banners
# Round windows (dark recesses) wherever the interior has one; the hall's clerestory sits above the wing roofs.
# Sandstone / dome stone / door wood from the texture library, flat red banners with a brass emblem.
# Ground contact: the plinth continues FOUNDATION_D below z 0, the entry ramp keeps its slope down to -FOUNDATION_D
# (th_foundations.py rules). Re-runnable.

P = 0.9                          # plinth top = interior floor
T = 0.4                          # wall thickness (th_interiors.T)
PLACE = (-60.0, 0.0)             # editing-scene spot, west of the large house
DOOR = dict(w=2.4, zs=2.6)       # door arch: width, springing height above the floor (interior TOWNHALL door)
TOWER_X, TOWER_Y, TOWER_R, TOWER_H = 5.1, -8.0, 1.7, 11.5
TOWER_SEG = dome_segs(TOWER_R)  # shaft and bands as many sides as the cap


def flat(name, rgb, rough=0.8, metal=0.0):
    m = bpy.data.materials.get(name)
    if m is None:
        m, _, b = simple_mat(name)
        b.inputs['Base Color'].default_value = (*rgb, 1)
        b.inputs['Roughness'].default_value = rough
        b.inputs['Metallic'].default_value = metal
        m.diffuse_color = (*rgb, 1)
    return m


M_STONE = bpy.data.materials["TH_Sandstone"]
M_DOME = bpy.data.materials["TH_DomeStone"]
M_WOOD = bpy.data.materials["TH_DoorWood"]
M_BATTEN = bpy.data.materials["TH_DoorBatten"]
M_WINDOW = bpy.data.materials["TH_WindowDark"]
M_BANNER = flat("TH_Banner", (0.36, 0.05, 0.04), 0.9)        # also the interior's banners
M_BRASS = flat("TH_Brass", (0.62, 0.46, 0.2), 0.4, 0.8)      # emblems, the chandelier

col = get_col("TatooineTownhall")
root = bpy.data.objects.new("ASSET_Townhall", None)
root.empty_display_type = 'ARROWS'; root.empty_display_size = 2.0
col.objects.link(root)
root["asset_id"], root["asset_kind"] = "tat_townhall", "building"
hit, loc, *_ = scene.ray_cast(bpy.context.evaluated_depsgraph_get(), Vector((*PLACE, 100.0)), Vector((0, 0, -1)))
root.location = (*PLACE, loc.z if hit else 0.0)
bpy.context.view_layer.update()
parts = []


def part(name, bm, mats, bevel=0.06):
    ob = mk(f"TOWN_{name}", bm, mats, col, bevel=bevel, smooth=False)
    ob.parent = root                               # local coordinates: no parent inverse
    parts.append(ob)
    return ob


def windows(ob, specs):
    """Round dark recesses (0.25 m deep) into ob: specs = [(cx, cy, cz, r, dir_xy into the wall)]."""
    for i, (cx, cy, cz, r, d) in enumerate(specs):
        cut = mk(f"_TOWN_win_{ob.name}_{i}", hcyl_bm(cx, cy, cz, r, 0.5, d), M_WINDOW, col, bevel=0)
        cut.parent = root
        apply_bool(ob, cut, 'DIFFERENCE')


def banner(name, x, y_front, z_top, z_bot, w=0.9):
    """Hanging banner facing -Y with a pointed foot, its rod and a brass emblem disc."""
    h = w / 2
    pts = [(x - h, z_top), (x + h, z_top), (x + h, z_bot + 0.35), (x, z_bot), (x - h, z_bot + 0.35)]
    part(f"{name}", prism(pts, y_front, y_front + 0.03), M_BANNER, bevel=0)
    part(f"{name}Rod", hcyl_bm(x, y_front + 0.04, z_top + 0.05, 0.035, w + 0.3, (1, 0), seg=6), M_BRASS, bevel=0)
    part(f"{name}Emblem", hcyl_bm(x, y_front - 0.01, z_top - 1.2, 0.24, 0.03, (0, 1), seg=12), M_BRASS, bevel=0)


# ---- plinth and entry ramp (th_foundations rules) ----
bm = box_bm(-15.2, 15.2, -13.0, 9.6, 0.0, P, (0.25, 0.25, 0.25, 0.25))
pl = part("Plinth", bm, M_STONE)
add_skirt(pl.data)
y_far = -13.0 - (P + FOUNDATION_D) / ENTRY_SLOPE
part("EntryRamp", prism([(y_far, -FOUNDATION_D), (-12.6, -FOUNDATION_D), (-12.6, P), (-13.0, P)], -3.0, 3.0, 'YZ'),
     M_STONE, bevel=0.04)

# ---- assembly hall + dome ----
hall = part("Hall", box_bm(-7.4, 7.4, -6.4, 8.4, P, P + 8.4), M_STONE)
windows(hall, [(sx * 7.4, y, P + 6.0, 0.55, (-sx, 0)) for sx in (-1, 1) for y in (-3.5, 1.0, 5.5)]
        + [(x, 8.4, P + 6.0, 0.55, (0, -1)) for x in (-4.5, 4.5)])
part("HallDeck", box_bm(-7.55, 7.55, -6.55, 8.55, P + 8.4, P + 8.8), M_STONE)
part("Dome", dome_bm(0.0, 1.0, P + 8.0, 6.9, (6.5 * 0.75 + T) / 6.9), M_DOME, bevel=0)
part("DomeDrum", annulus_bm(0.0, 1.0, P + 8.75, P + 9.1, 6.5, 7.05, seg=dome_segs(6.9)), M_DOME)   # rings: the dome's count
apex = P + 8.0 + 6.5 * 0.75 + T
part("Oculus", annulus_bm(0.0, 1.0, apex - 0.15, apex + 0.15, 0.75, 1.1, seg=12), M_DOME, bevel=0.03)
for i, (x, y) in enumerate([(-5.6, 7.2), (5.6, 7.2)]):
    part(f"RoofVent.{i:02d}", cyl_bm(x, y, P + 8.8, P + 9.4, 0.38), M_STONE)
    part(f"RoofVentCap.{i:02d}", cyl_bm(x, y, P + 9.4, P + 9.6, 0.45, 0.3), M_STONE, bevel=0)

# ---- wings: archives / mayor (west), clerk / lounge (east) ----
for side, sx in (("W", -1), ("E", 1)):
    x0, x1 = sorted((sx * 7.4, sx * 14.0))
    wing = part(f"Wing{side}", box_bm(x0, x1, -6.4, 8.4, P, P + 4.0), M_STONE)
    windows(wing, [(sx * 10.5, -6.4, P + 1.7, 0.35, (0, 1)), (sx * 10.5, 8.4, P + 1.7, 0.35, (0, -1)),
                   (sx * 14.0, 5.8 if sx < 0 else 6.0, P + 1.7, 0.35, (-sx, 0))]
           + ([(sx * 14.0, -2.6, P + 1.7, 0.35, (-sx, 0))] if sx > 0 else []))
    part(f"Wing{side}Deck", box_bm(x0 - (0.15 if sx < 0 else 0.0), x1 + (0.15 if sx > 0 else 0.0), -6.55, 8.55,
                                   P + 4.0, P + 4.25), M_STONE)
    for j, y in enumerate((-2.7, 4.5)):
        part(f"Wing{side}Dome.{j}", dome_bm(sx * 10.5, y, P + 3.6, 3.0, (2.6 * 0.55 + T) / 3.0), M_DOME, bevel=0)
        part(f"Wing{side}DomeDrum.{j}", annulus_bm(sx * 10.5, y, P + 4.25, P + 4.45, 2.85, 3.15, seg=dome_segs(3.0)),
                   M_DOME)

# ---- vestibule, door, porch ----
fo = part("Vestibule", box_bm(-3.4, 3.4, -10.4, -6.4, P, P + 5.4), M_STONE)
cut = mk("_TOWN_door", prism(arch_pts(0.0, DOOR["w"], P, P + DOOR["zs"]), -10.5, -10.2), M_STONE, col, bevel=0)
cut.parent = root
apply_bool(fo, cut, 'DIFFERENCE')
part("VestibuleDeck", box_bm(-3.55, 3.55, -10.55, -6.4, P + 5.4, P + 5.7), M_STONE)
part("VestibuleDome", dome_bm(0.0, -8.2, P + 5.0, 2.1, (1.7 * 0.6 + T) / 2.1), M_DOME, bevel=0)
part("ArchSurround", prism(surround_pts(0.0, DOOR["w"] / 2 + 0.4, DOOR["w"] / 2, P, P + DOOR["zs"]), -10.7, -10.4),
     M_STONE)
r = (DOOR["w"] - 0.1) / 2
part("Door", prism(arch_pts(0.0, DOOR["w"] - 0.1, P, P + DOOR["zs"]), -10.24, -10.19), M_WOOD, bevel=0.01)
for i, dx in enumerate((-0.35 * r, 0.0, 0.35 * r)):
    top = P + DOOR["zs"] + math.sqrt(r * r - dx * dx) - 0.12
    part(f"DoorBatten.{i:02d}", box_bm(dx - 0.05, dx + 0.05, -10.28, -10.24, P + 0.08, top), M_BATTEN, bevel=0.005)
for i, z in enumerate((0.4, 0.95 * DOOR["zs"], DOOR["zs"] + 0.25)):
    part(f"DoorRail.{i:02d}", box_bm(-r + 0.1, r - 0.1, -10.28, -10.24, P + z, P + z + 0.1), M_BATTEN, bevel=0.005)
for i, sx in enumerate((-1, 1)):
    x0, x1 = sorted((sx * 2.6, sx * 3.1))
    part(f"PorchPillar.{i:02d}", box_bm(x0, x1, -12.6, -12.1, P, P + 4.6), M_STONE)
    part(f"PorchPillarBase.{i:02d}", box_bm(x0 - 0.08, x1 + 0.08, -12.68, -12.02, P, P + 0.3), M_STONE, bevel=0.03)
part("PorchRoof", box_bm(-3.3, 3.3, -12.8, -10.4, P + 4.6, P + 5.0), M_STONE)

# ---- towers ----
for i, sx in enumerate((-1, 1)):
    x = sx * TOWER_X
    tw = part(f"Tower.{i:02d}", cyl_bm(x, TOWER_Y, P, P + TOWER_H, TOWER_R, seg=TOWER_SEG), M_STONE)
    windows(tw, [(x, TOWER_Y - TOWER_R, P + z, 0.25, (0, 1)) for z in (3.4, 10.4)]
            + [(x + sx * TOWER_R, TOWER_Y, P + 10.4, 0.25, (-sx, 0))])
    for j, z in enumerate((4.6, TOWER_H - 0.3)):
        part(f"TowerBand.{i:02d}{j}", annulus_bm(x, TOWER_Y, P + z, P + z + 0.3, TOWER_R - 0.1, TOWER_R + 0.12, seg=TOWER_SEG),
             M_STONE)
    part(f"TowerDome.{i:02d}", dome_bm(x, TOWER_Y, P + TOWER_H, TOWER_R + 0.05, 0.9), M_DOME, bevel=0)
    part(f"TowerFinial.{i:02d}", cyl_bm(x, TOWER_Y, P + TOWER_H + 1.5, P + TOWER_H + 2.1, 0.12, 0.0, seg=8), M_BRASS,
         bevel=0)
    banner(f"TowerBanner.{i:02d}", x, TOWER_Y - TOWER_R - 0.14, P + 9.4, P + 5.3)

bpy.context.view_layer.update()
for ob in parts:
    unwrap_uv0(ob, root)

dg = bpy.context.evaluated_depsgraph_get()
tris, lo, hi = 0, Vector((1e9,) * 3), Vector((-1e9,) * 3)
for o in parts:
    e = o.evaluated_get(dg); m = e.to_mesh(); m.calc_loop_triangles(); tris += len(m.loop_triangles)
    for v in m.vertices:
        lo = Vector(map(min, lo, v.co)); hi = Vector(map(max, hi, v.co))
    e.to_mesh_clear()
result = {"root": root.name, "at": [round(v, 3) for v in root.location], "parts": len(parts), "tris": tris,
          "lo": [round(v, 2) for v in lo], "hi": [round(v, 2) for v in hi]}
