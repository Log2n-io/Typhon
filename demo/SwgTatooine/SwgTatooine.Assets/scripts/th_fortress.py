import bpy, os, random  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Raider fortress (kind "building", no door, no interior) for the Horde event: spawned in the open desert near a town
# for the event's duration; the raider waves come out of its gate. An original desert-raider stronghold, about the town
# hall's footprint (31 x 28 m) and twice its height (keep 26 m, watchtower fire at 31 m):
# - a battered curtain wall (10 m, crenellated) between four round corner towers with spiked scrap-metal roofs;
# - the gatehouse on the front: two square gate towers, an arch block over the 5 m gate, scrap-metal doors set back
#   4 m in the passage (shut: there is no inside), a pair of horns and a shield over the gate, torches either side;
# - a two-tier keep rising out of the courtyard, a watchtower on top with a beacon fire;
# - scrap plates on the walls, sharpened stakes along the wall foot, long red banners, pennants on the towers,
#   fire baskets on the gate towers.
# Rough stone (TH_Sandstone, shared library) and rusty scrap (TH_Metal); flat banner, bone, wood and flame colours.
# Asset local, front (the gate) towards -Y, origin = the middle of the footprint on the ground; ground-touching parts
# are built down to -FOUNDATION_D. Spawn points HF_Spawn.0-9 (spawn ids creature.0-9, the lairs' scheme) stand on an
# arc outside the gate, facing out, clear of the colliders: where a wave appears. Collision: th_collision.py.
# Re-runnable.

SEED = 41
PLACE = (90.0, 0.0)                          # editing-scene spot, east of the shuttleport
rng = random.Random(SEED)
D = FOUNDATION_D


def flat(name, rgb, rough=0.85, metal=0.0, emit=0.0):
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


M_STONE = bpy.data.materials["TH_Sandstone"]         # shared library
M_SCRAP = bpy.data.materials["TH_Metal"]             # shared library (rusty)
M_WOOD = bpy.data.materials["TH_NestWood"]           # th_creature_nest.py
M_BONE = bpy.data.materials["TH_ItmBone"]            # th_items.py
M_FLAME = bpy.data.materials["TH_CampFlame"]         # th_camps.py
M_CORE = bpy.data.materials["TH_CampFlameCore"]      # th_camps.py
M_EMBER = bpy.data.materials["TH_CampEmber"]         # th_camps.py
M_BANNER = flat("TH_RaiderBanner", (0.17, 0.02, 0.018), 0.9)
M_SOOT = flat("TH_RaiderSoot", (0.045, 0.04, 0.035), 0.95)
M_IRON = flat("TH_RaiderIron", (0.08, 0.075, 0.07), 0.55, 0.6)

col = get_col("TatooineHordeFortress")
root = bpy.data.objects.new("ASSET_HordeFortress", None)
root.empty_display_type = 'ARROWS'; root.empty_display_size = 2.0
col.objects.link(root)
root["asset_id"], root["asset_kind"] = "tat_horde_fortress", "building"
hit, loc, *_ = scene.ray_cast(bpy.context.evaluated_depsgraph_get(), Vector((*PLACE, 200.0)), Vector((0, 0, -1)))
root.location = (*PLACE, loc.z if hit else 0.0)


def part(name, bm, mats, bevel=0.0):
    ob = mk(f"HF_{name}", bm, mats, col, bevel=bevel, smooth=False)
    ob.parent = root                               # local coordinates: no parent inverse
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


def merlons(pts, z0, z1, w, t):
    """Battlement teeth w wide, t thick, centred on pts [(x, y, angle)], each turned by its angle about z: uneven
    heights, a slight lean, one in five broken down to a stump."""
    out = []
    for x, y, a in pts:
        top = z0 + (z1 - z0) * (0.35 if rng.random() < 0.2 else rng.uniform(0.8, 1.25))
        out.append(xform(box_bm(-w / 2, w / 2, -t / 2, t / 2, z0, top, (rng.uniform(0, 0.12), rng.uniform(0, 0.12), 0, 0)),
                         rot_y=rng.uniform(-0.06, 0.06), pivot=(0, 0, z0), rot_z=a, move=(x, y, 0)))
    return merge(out)


def spikes_on(pts, z, h, r=0.09):
    """Iron spikes standing on the teeth at pts, from z, h tall, splayed a little outwards."""
    return [rod((x, y, z - 0.2), (x + 0.25 * math.cos(a - math.pi / 2), y + 0.25 * math.sin(a - math.pi / 2), z + h), r, 0.0,
                seg=4) for x, y, a in pts]


def along(x0, y0, x1, y1, step, a):
    """Points every ~step along a segment, (x, y, a)."""
    n = max(1, round(math.hypot(x1 - x0, y1 - y0) / step))
    return [(x0 + (x1 - x0) * (i + 0.5) / n, y0 + (y1 - y0) * (i + 0.5) / n, a) for i in range(n)]


def flame(c, r, h):
    """Two crossed emissive cones; the core is thinner and taller so its tip pokes out (an enclosed core is culled at
    export)."""
    cx, cy, cz = c
    return merge([tinted(cyl_bm(cx, cy, cz, cz + 0.8 * h, r, 0.0, seg=6), 0),
                  tinted(xform(cyl_bm(cx, cy, cz, cz + 1.1 * h, 0.6 * r, 0.0, seg=5), rot_z=0.5, pivot=(cx, cy, 0)), 1)])


def banner(x, y, z_top, length, width, depth=0.06):
    """A cloth hanging on a front (-Y) face, its back at y, V-cut at the bottom; its rod; its bone mark."""
    zb = z_top - length
    cloth = prism([(x - width / 2, z_top), (x + width / 2, z_top), (x + width / 2, zb + 0.1 * width),
                   (x + width / 4, zb + 0.45 * width), (x, zb - 0.05 * width), (x - width / 4, zb + 0.5 * width),
                   (x - width / 2, zb + 0.15 * width)], y - depth, y, 'XZ')
    bar = rod((x - width / 2 - 0.25, y - depth - 0.06, z_top), (x + width / 2 + 0.25, y - depth - 0.06, z_top), 0.06, 0.06,
              seg=6)
    zc = z_top - 0.33 * length
    mark = merge([xform(box_bm(-0.05 * width, 0.05 * width, y - depth - 0.03, y - depth + 0.01, -0.3 * width, 0.3 * width),
                        rot_y=0.35, move=(x + k * 0.22 * width, 0, zc)) for k in (-1, 0, 1)])
    return cloth, bar, mark


# ---------------- layout (metres, asset local) ----------------
WH = 10.0                                    # curtain wall height
XO, YF, YB = 12.5, -10.5, 11.0               # outer faces of the walls at the top (side, front, back)
T = 2.2                                      # wall thickness at the top
BAT = 0.9                                    # batter: the outer face leans out this much more at the foundation bottom
GX = 2.5                                     # gate half width
GH = 7.5                                     # gate height
TOWERS = [(-XO, YF), (XO, YF), (XO, YB), (-XO, YB)]
TR0, TR1, TH = 3.0, 2.5, 15.0                # corner towers: radius at the bottom, at the top; height
GW = 1.75                                    # gate tower half width (x)
GT = [(-GX - GW, -11.5), (GX + GW, -11.5)]   # gate tower centres; each spans y -13.5..-8.5
GTH = 17.0                                   # gate tower height
GBAT = 0.45                                  # gate tower batter (front and sides)
KZ1, KZ2 = 20.0, 25.0                        # keep tiers' tops
KBAT = 1.0                                   # keep batter


def face_y(z):
    """y of the front wall's outer face at height z."""
    return YF - BAT * (WH - z) / (WH + D)


def gate_face_y(z):
    return -13.5 + GBAT * (z + D) / (GTH + D)


def keep_face_y(z):
    return -5.0 + KBAT * (z + D) / (KZ1 + D)


# ---- curtain walls: battered prisms; the front one in two halves either side of the gate ----
def wall_profile(o_top, inward):
    """(across, z) section of a wall whose outer face is at o_top at the top; inward = +1 / -1 (towards the centre)."""
    i_face = o_top + inward * T
    return [(o_top - inward * BAT, -D), (i_face, -D), (i_face, WH), (o_top, WH)]


walls = [prism(wall_profile(YF, 1), GX, XO, 'YZ'), prism(wall_profile(YF, 1), -XO, -GX, 'YZ'),
         prism(wall_profile(YB, -1), -XO, XO, 'YZ'),
         prism(wall_profile(XO, -1), YF, YB, 'XZ'), prism(wall_profile(-XO, 1), YF, YB, 'XZ')]
part("Walls", merge(walls), M_STONE, bevel=0.08)
teeth = (along(GX + 2 * GW, YF + 0.35, XO - TR1, YF + 0.35, 1.8, 0.0) + along(-XO + TR1, YF + 0.35, -GX - 2 * GW, YF + 0.35, 1.8, 0.0)
         + along(-XO + TR1, YB - 0.35, XO - TR1, YB - 0.35, 1.8, 0.0)
         + along(XO - 0.35, YF + TR1, XO - 0.35, YB - TR1, 1.8, math.pi / 2)
         + along(-XO + 0.35, YF + TR1, -XO + 0.35, YB - TR1, 1.8, math.pi / 2))
part("WallMerlons", merlons(teeth, WH, WH + 1.3, 1.0, 0.7), M_STONE, bevel=0.04)

# ---- corner towers: tapered, a soot band, an overhanging parapet, merlons, a spiked scrap roof, a pennant ----
bodies, soot, parapets, t_teeth, roofs, poles, pennants, TPLATES = [], [], [], [], [], [], [], []
for cx, cy in TOWERS:
    bodies.append(cyl_bm(cx, cy, -D, TH, TR0, TR1, seg=10))
    for k in range(5):                         # arrow slits on the outward faces, two rows
        a = math.atan2(cy, cx) + (k - 2) * 2 * math.pi / 10
        for z in (6.0, 11.0):
            rr = TR0 + (TR1 - TR0) * (z + D) / (TH + D)
            m = rr * math.cos(math.pi / 10) - 0.05
            soot.append(xform(box_bm(-0.2, 0.2, -0.12, 0.12, z, z + 1.4), rot_z=a + math.pi / 2,
                              move=(cx + m * math.cos(a), cy + m * math.sin(a), 0)))
    for k in range(3):                         # scrap patches, low on the outward faces
        a = math.atan2(cy, cx) + (k - 1) * 2 * math.pi / 10 + math.pi / 10
        z = rng.uniform(1.0, 4.0); h = rng.uniform(1.6, 2.6)
        rr = TR0 + (TR1 - TR0) * (z + h / 2 + D) / (TH + D)
        m = rr * math.cos(math.pi / 10) + 0.02
        TPLATES.append(xform(box_bm(-0.75, 0.75, -0.06, 0.06, -h / 2, h / 2), rot_x=rng.uniform(-0.1, 0.1),
                             rot_z=a + math.pi / 2 + rng.uniform(-0.1, 0.1), move=(cx + m * math.cos(a), cy + m * math.sin(a), z + h / 2)))
    parapets.append(annulus_bm(cx, cy, TH, TH + 1.0, 1.5, TR1 + 0.45, seg=10))
    t_teeth += [(cx + (TR1 + 0.1) * math.cos(a), cy + (TR1 + 0.1) * math.sin(a), a + math.pi / 2)
                for a in (2 * math.pi * (i + 0.5) / 10 for i in range(10))]
    roofs.append(cyl_bm(cx, cy, TH, TH + 5.0, 2.1, 0.0, seg=8))
    roofs += [rod((cx + 1.8 * math.cos(a), cy + 1.8 * math.sin(a), TH + 1.0),
                  (cx + 3.1 * math.cos(a), cy + 3.1 * math.sin(a), TH + 2.2), 0.14, 0.0, seg=4)
              for a in (2 * math.pi * i / 8 + 0.2 for i in range(8))]
    poles.append(rod((cx, cy, TH + 4.5), (cx, cy, TH + 8.0), 0.07, 0.05, seg=5))
    side = 1.0 if cx > 0 else -1.0
    pennants.append(prism([(cx, TH + 7.9), (cx + side * 2.0, TH + 7.4), (cx, TH + 6.9)], cy - 0.03, cy + 0.03, 'XZ'))
part("Towers", merge(bodies), M_STONE, bevel=0.08)
part("TowerSlits", merge(soot), M_SOOT)
part("TowerParapets", merge(parapets), M_STONE, bevel=0.04)
part("TowerMerlons", merlons(t_teeth, TH + 1.0, TH + 2.1, 0.9, 0.6), M_STONE, bevel=0.03)
part("TowerRoofs", merge(roofs), M_SCRAP)
part("TowerPoles", merge(poles), M_WOOD)
part("Pennants", merge(pennants), M_BANNER)

k_teeth_pre = (along(-5.8, -3.85, 5.8, -3.85, 1.6, 0.0) + along(-5.8, 6.85, 5.8, 6.85, 1.6, 0.0)
               + along(-5.85, -3.2, -5.85, 6.2, 1.6, math.pi / 2) + along(5.85, -3.2, 5.85, 6.2, 1.6, math.pi / 2))

# ---- gatehouse ----
part("GateTowers", merge([box_bm(x - GW, x + GW, y - 2.0, y + 3.0, -D, GTH, (GBAT, GBAT, GBAT, 0.0)) for x, y in GT]),
     M_STONE, bevel=0.08)
part("GateParapets", merge([box_bm(x - GW + 0.35, x + GW - 0.35, y - 1.6, y + 2.9, GTH, GTH + 0.6) for x, y in GT]),
     M_STONE, bevel=0.04)
g_teeth = []
for x, y in GT:
    g_teeth += along(x - 1.2, y - 1.35, x + 1.2, y - 1.35, 1.2, 0.0) + along(x - 1.2, y + 2.6, x + 1.2, y + 2.6, 1.2, 0.0)
    g_teeth += along(x - 1.2, y - 0.8, x - 1.2, y + 2.1, 1.2, math.pi / 2) + along(x + 1.2, y - 0.8, x + 1.2, y + 2.1, 1.2, math.pi / 2)
part("GateMerlons", merlons(g_teeth, GTH + 0.6, GTH + 1.6, 0.8, 0.5), M_STONE, bevel=0.03)
part("Spikes", merge(spikes_on(g_teeth, GTH + 1.5, 1.3) + spikes_on(k_teeth_pre, KZ1 + 1.5, 1.4)
                     + spikes_on(teeth[::2], WH + 1.2, 0.9, 0.07)), M_IRON)
for x, y in GT:                               # scrap patches on the gate towers' fronts and outer sides
    for z, dx in ((2.0, -0.6), (5.5, 0.7), (9.5, -0.3)):
        h = rng.uniform(1.6, 2.4); yy = gate_face_y(z + h / 2) - 0.02
        TPLATES.append(xform(box_bm(-0.8, 0.8, -0.12, 0.0, -h / 2, h / 2), rot_x=-math.atan2(GBAT, GTH + D),
                             rot_y=rng.uniform(-0.15, 0.15), move=(x + dx, yy, z + h / 2)))
part("TowerScrap", merge(TPLATES), M_SCRAP)
part("GateSoot", merge([box_bm(x - GW + GBAT - 0.04, x + GW - GBAT + 0.04, gate_face_y(GTH - 1.4) - 0.04, y + 3.0,
                               GTH - 1.4, GTH - 0.5) for x, y in GT]), M_SOOT)
# arch block over the gate (its front at the gate towers' top front), a scrap lintel beam
part("GateArch", box_bm(-GX - 0.5, GX + 0.5, gate_face_y(GTH), YF + T, GH, 14.0), M_STONE, bevel=0.06)   # into the battered towers
part("GateLintel", box_bm(-GX - 0.3, GX + 0.3, -13.4, -12.95, GH - 0.55, GH + 0.05), M_SCRAP)
# doors: two scrap leaves 4 m back in the passage, iron straps, spikes
DY = -9.45                                    # the doors' front face
part("Doors", merge([box_bm(-GX, -0.05, DY, DY + 0.35, -0.2, GH), box_bm(0.05, GX, DY, DY + 0.35, -0.2, GH)]), M_SCRAP,
     bevel=0.02)
straps = [box_bm(-GX + 0.1, GX - 0.1, DY - 0.1, DY, z, z + 0.3) for z in (1.0, 3.6, 6.2)]
straps += [box_bm(x - 0.12, x + 0.12, DY - 0.07, DY, 0.0, GH - 0.1) for x in (-1.4, 1.4)]
part("DoorStraps", merge(straps), M_IRON)
part("DoorSpikes", merge([rod((x, DY - 0.1, z + 0.15), (x, DY - 0.6, z + 0.15), 0.07, 0.0, seg=4)
                          for z in (1.0, 3.6, 6.2) for x in (-2.0, -0.9, 0.9, 2.0)]), M_IRON)
# trophy over the gate: a round shield (bone rim and boss, red field) between two great curved horns
SY = gate_face_y(GTH)                         # the arch's front
part("Shield", merge([tinted(hcyl_bm(0.0, SY - 0.12, 10.8, 1.6, 0.25, (0, 1), seg=12), 0),
                      tinted(hcyl_bm(0.0, SY - 0.27, 10.8, 1.25, 0.12, (0, 1), seg=12), 1),
                      tinted(hcyl_bm(0.0, SY - 0.38, 10.8, 0.35, 0.12, (0, 1), seg=8), 0)]), [M_BONE, M_BANNER])
horns = []
for sx in (-1, 1):
    pts = [(sx * (1.75 + 1.0 * math.sin(t)), SY - 0.25 - 0.5 * math.sin(t), 10.2 + 1.95 * (1 - math.cos(t)))
           for t in (0.0, 0.5, 1.0, 1.5, 2.0)]
    rads = [0.32, 0.26, 0.19, 0.11, 0.02]
    horns += [rod(p0, p1, r0, r1, seg=6) for p0, p1, r0, r1 in zip(pts, pts[1:], rads, rads[1:])]
part("Horns", merge(horns), M_BONE)

# ---- keep: two battered tiers, parapets, merlons, slits, a watchtower with a beacon fire ----
part("Keep", box_bm(-7.0, 7.0, -5.0, 8.0, -D, KZ1, (KBAT, KBAT, KBAT, KBAT)), M_STONE, bevel=0.08)
part("KeepParapet", box_bm(-6.2, 6.2, -4.2, 7.2, KZ1, KZ1 + 0.6), M_STONE, bevel=0.04)
k_teeth = k_teeth_pre
part("KeepMerlons", merlons(k_teeth, KZ1 + 0.6, KZ1 + 1.6, 0.9, 0.6), M_STONE, bevel=0.03)
part("KeepUpper", box_bm(-4.0, 4.0, -2.0, 5.5, KZ1, KZ2, (0.5, 0.5, 0.5, 0.5)), M_STONE, bevel=0.08)
part("KeepSoot", box_bm(-3.56, 3.56, -1.56, 5.06, KZ2 - 1.2, KZ2 - 0.4), M_SOOT)
part("KeepUpperParapet", box_bm(-3.7, 3.7, -1.7, 5.2, KZ2, KZ2 + 0.5), M_STONE, bevel=0.04)
u_teeth = (along(-3.3, -1.4, 3.3, -1.4, 1.4, 0.0) + along(-3.3, 4.9, 3.3, 4.9, 1.4, 0.0)
           + along(-3.4, -0.8, -3.4, 4.3, 1.4, math.pi / 2) + along(3.4, -0.8, 3.4, 4.3, 1.4, math.pi / 2))
part("KeepUpperMerlons", merlons(u_teeth, KZ2 + 0.5, KZ2 + 1.3, 0.8, 0.5), M_STONE, bevel=0.03)
part("KeepSlits", merge([box_bm(x - 0.25, x + 0.25, keep_face_y(z) - 0.06, keep_face_y(z + 1.8) + 0.06, z, z + 1.8)
                         for z in (9.0, 14.5) for x in (-3.5, 0.0, 3.5)]), M_SOOT)
WX, WY = 0.0, 1.75
part("Watchtower", cyl_bm(WX, WY, KZ2, 29.0, 1.7, 1.2, seg=8), M_STONE, bevel=0.04)
part("Beacon", annulus_bm(WX, WY, 29.0, 29.8, 0.9, 1.6, seg=8), M_SCRAP)
part("BeaconEmbers", cyl_bm(WX, WY, 29.0, 29.6, 0.95, seg=8), M_EMBER)
part("BeaconFire", flame((WX, WY, 29.55), 0.9, 2.0), [M_FLAME, M_CORE])
# courtyard floor between the walls and the keep (seen from above only)
part("Courtyard", box_bm(-XO + T, XO - T, YF + T, YB - T, -D, 0.1), M_SOOT)

# ---- scrap plates on the side and back walls' battered faces, stakes along the wall foot ----
LEAN = math.atan2(BAT, WH + D)                # the batter's angle from vertical
plates = []
for sx in (-1, 1):
    y = YF + TR0 + 0.4
    while y < YB - TR0 - 1.8:
        w = rng.uniform(1.4, 2.4); h = rng.uniform(1.6, 2.8); z = rng.uniform(1.2, 6.8)
        xw = sx * (XO + BAT * (WH - z - h / 2) / (WH + D))
        p = box_bm(0.0, 0.12, -w / 2, w / 2, -h / 2, h / 2)
        plates.append(xform(p, rot_y=-sx * LEAN, rot_x=rng.uniform(-0.12, 0.12),
                            move=(xw if sx > 0 else xw - 0.12, y + w / 2, z + h / 2)))
        y += w + rng.uniform(0.8, 2.0)
x = -XO + TR0 + 0.4
while x < XO - TR0 - 1.8:
    w = rng.uniform(1.4, 2.4); h = rng.uniform(1.6, 2.8); z = rng.uniform(1.2, 6.8)
    yw = YB + BAT * (WH - z - h / 2) / (WH + D)
    plates.append(xform(box_bm(-w / 2, w / 2, 0.0, 0.12, -h / 2, h / 2), rot_x=-LEAN, rot_y=rng.uniform(-0.12, 0.12),
                        move=(x + w / 2, yw, z + h / 2)))
    x += w + rng.uniform(0.8, 2.0)
part("ScrapPlates", merge(plates), M_SCRAP)

STAKE_OUT = 2.0                               # how far the stakes reach out from the wall foot
stakes = []
for x0, x1 in ((GX + 2 * GW + 0.3, XO - TR0 - 0.2), (-XO + TR0 + 0.2, -GX - 2 * GW - 0.3)):
    n = max(2, round((x1 - x0) / 0.8))
    for i in range(n):
        x = x0 + (x1 - x0) * (i + 0.5) / n
        stakes.append(rod((x, YF - 0.75, -0.3), (x + rng.uniform(-0.15, 0.15), YF - 0.75 - STAKE_OUT, rng.uniform(1.5, 1.9)),
                          0.16, 0.0, seg=5))
for sx in (-1, 1):
    y0, y1 = YF + TR0 + 0.3, YB - TR0 - 0.3
    n = round((y1 - y0) / 0.9)
    for i in range(n):
        y = y0 + (y1 - y0) * (i + 0.5) / n
        stakes.append(rod((sx * (XO + 0.75), y, -0.3), (sx * (XO + 0.75 + STAKE_OUT), y + rng.uniform(-0.15, 0.15),
                           rng.uniform(1.5, 1.9)), 0.16, 0.0, seg=5))
part("Stakes", merge(stakes), M_WOOD)

# ---- banners (hung clear of each battered face at its bottom), torches, fire baskets ----
cloths, bars, marks = [], [], []
for (x, _), (top, length, width) in zip(GT, [(15.5, 9.0, 2.0)] * 2):
    for b, l in zip(banner(x, gate_face_y(top - length) - 0.02, top, length, width), (cloths, bars, marks)):
        l.append(b)
for x in (-(GX + 2 * GW + XO - TR0) / 2, (GX + 2 * GW + XO - TR0) / 2):
    for b, l in zip(banner(x, face_y(9.6 - 5.5) - 0.02, 9.6, 5.5, 1.4), (cloths, bars, marks)):
        l.append(b)
for x in (-3.5, 3.5):
    for b, l in zip(banner(x, keep_face_y(19.0 - 8.0) - 0.02, 19.0, 8.0, 2.0), (cloths, bars, marks)):
        l.append(b)
part("Banners", merge(cloths), M_BANNER)
part("BannerBars", merge(bars), M_IRON)
part("BannerMarks", merge(marks), M_BONE)

torches, fires = [], []
for x in (-GX - 0.6, GX + 0.6):               # either side of the gate, on the gate towers' fronts
    y = gate_face_y(4.4)
    torches.append(rod((x, y + 0.05, 4.4), (x, y - 0.6, 5.0), 0.06, 0.06, seg=5))
    torches.append(cyl_bm(x, y - 0.65, 4.95, 5.35, 0.12, 0.22, seg=6))
    fires.append(flame((x, y - 0.65, 5.3), 0.18, 0.7))
for x, y in GT:                               # fire baskets on the gate towers
    torches.append(annulus_bm(x, y + 0.6, GTH + 0.6, GTH + 1.3, 0.45, 0.7, seg=8))
    torches.append(cyl_bm(x, y + 0.6, GTH + 0.6, GTH + 1.1, 0.5, seg=8))
    fires.append(flame((x, y + 0.6, GTH + 1.05), 0.5, 1.4))
part("Torches", merge(torches), M_IRON)
part("TorchFlames", merge(fires), [M_FLAME, M_CORE])

# every part on the shared box projection (the flat-colour parts ignore it)
bpy.context.view_layer.update()
for o in root.children:
    if o.type == 'MESH':
        unwrap_uv0(o, root)

# ---- spawns: an arc outside the gate, facing out from it ----
SC, SR = (0.0, -12.5), 8.0
for i in range(10):
    a = math.radians(205.0 + 130.0 * i / 9)
    x, y = SC[0] + SR * math.cos(a), SC[1] + SR * math.sin(a)
    make_spawn(root, f"HF_Spawn.{i}", f"creature.{i}", (x, y, 0.0), math.atan2(y - SC[1], x - SC[0]) - math.pi / 2)

bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()
tris, lo, hi = 0, Vector((1e9,) * 3), Vector((-1e9,) * 3)
for o in root.children:
    if o.type == 'MESH':
        e = o.evaluated_get(dg); m = e.to_mesh(); m.calc_loop_triangles(); tris += len(m.loop_triangles)
        for v in m.vertices:
            p = o.matrix_local @ v.co
            lo = Vector(map(min, lo, p)); hi = Vector(map(max, hi, p))
        e.to_mesh_clear()
result = {"root": root.name, "at": [round(v, 3) for v in root.location], "parts": len(root.children), "tris": tris,
          "lo": [round(v, 2) for v in lo], "hi": [round(v, 2) for v in hi]}
