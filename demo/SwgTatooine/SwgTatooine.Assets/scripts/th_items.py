import bpy, os, json, random  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Crafting items (kind "item"): what a player holds in the inventory, drops as loot or crafts with, after the crafting
# catalogue's § 13 (claude/design/SwgTatooine/design/crafting-catalog.md): materials (13.1), cores (13.2), looted
# add-ons (13.3), crafted parts (13.4), armour pieces as objects (13.6; worn armour is a later task), potions and
# tonics (13.7), kits and containers (13.10). Low poly, flat colours, no texture; no collision.
#
# Frame: origin = the middle of the item's footprint on the ground (z 0), resting the way it lies or stands when
# dropped; front towards -Y. Sizes after the catalogue, the player being 1.8 m tall.
# Mk I-V (materials, cores, add-ons): one model each. The part that shows the Mk (a band, tag, seams, the crystals,
# the glow) uses the shared material TH_ItemMk, published at Mk I; the root's item_mk lists every level's colour and
# glow (MK below), which th_export.py writes into the descriptor for the client to apply (ASSET_FORMAT.md 5.11).
# The crate's label plate uses TH_ItemLabel and carries a 0..1 UV0 on its face, for the client to draw the content.
#
# All items live in the TatooineItems collection, one ASSET_ root each, laid out in rows (a category per row) on free
# ground west of the weapons. Re-runnable: the collection is rebuilt. The chassis copies the swoop's frame and seat
# and body (th_swoop.py): run that first.

X0 = -45.5                                     # editing-scene x of each row's first item
ROW_Y = [47.0, 48.2, 49.4, 51.5, 54.0, 55.2, 56.6]
GAP = 0.25                                     # between items along a row
SEED = 13
rng = random.Random(SEED)

MK = [  # Mk I .. V: linear base colour, emissive colour, emissive strength (glTF KHR_materials_emissive_strength)
    {"base_color": [0.62, 0.64, 0.66], "emissive": [0.0, 0.0, 0.0], "emissive_strength": 0.0},    # I: plain metal
    {"base_color": [0.30, 0.78, 0.34], "emissive": [0.30, 0.78, 0.34], "emissive_strength": 0.6},  # II: green
    {"base_color": [0.25, 0.52, 1.00], "emissive": [0.25, 0.52, 1.00], "emissive_strength": 1.0},  # III: blue
    {"base_color": [0.68, 0.32, 1.00], "emissive": [0.68, 0.32, 1.00], "emissive_strength": 1.5},  # IV: violet
    {"base_color": [1.00, 0.62, 0.15], "emissive": [1.00, 0.62, 0.15], "emissive_strength": 2.5},  # V: gold
]


def flat(name, rgb, rough=0.6, metal=0.0, emit=0.0):
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


M_MK = flat("TH_ItemMk", MK[0]["base_color"], 0.35, 0.3)
M_LABEL = flat("TH_ItemLabel", (0.82, 0.80, 0.72), 0.7)
M_STEEL = bpy.data.materials["TH_WpnSteel"]       # th_weapons.py
M_DARK = bpy.data.materials["TH_WpnBlack"]
M_BRASS = bpy.data.materials["TH_WpnBrass"]
M_GLOW = bpy.data.materials["TH_WpnGlowBlue"]
M_GUN = bpy.data.materials["TH_PanelGunmetal"]    # th_terminals.py
M_RED = bpy.data.materials["TH_StripRed"]
M_WHITE = bpy.data.materials["TH_PanelWhite"]
M_KHAKI = bpy.data.materials["TH_PanelKhaki"]     # th_swoop.py
M_HAZ = bpy.data.materials["TH_HazardYellow"]     # th_industry.py
M_HIDE = bpy.data.materials["TH_HideTan"]
M_HIDED = bpy.data.materials["TH_HideDark"]
M_HIDEP = bpy.data.materials["TH_HidePale"]
M_CLAY = bpy.data.materials["TH_Clay"]
M_CANVAS = bpy.data.materials["TH_CampCanvas"]
M_ROCK = flat("TH_ItmRock", (0.34, 0.33, 0.31), 0.95)
M_CRYSTAL = flat("TH_ItmCrystal", (0.55, 0.85, 0.95), 0.12, 0.0, 0.4)
M_GLASS = flat("TH_ItmGlass", (0.62, 0.76, 0.80), 0.06)
M_GLASSB = flat("TH_ItmGlassBrown", (0.30, 0.15, 0.05), 0.12)
M_BONE = flat("TH_ItmBone", (0.84, 0.79, 0.66), 0.7)
M_CHITIN = flat("TH_ItmChitin", (0.22, 0.17, 0.10), 0.35)
M_FIBER = flat("TH_ItmFiber", (0.72, 0.64, 0.45), 0.95)
M_ROPE = flat("TH_ItmRope", (0.48, 0.37, 0.22), 0.95)
M_FLESH = flat("TH_ItmFlesh", (0.55, 0.15, 0.17), 0.25)
M_VENOM = flat("TH_ItmVenom", (0.50, 0.85, 0.12), 0.15, 0.0, 0.3)
M_DRUM = flat("TH_ItmDrumBlue", (0.12, 0.22, 0.42), 0.5, 0.3)
M_WATER = flat("TH_ItmWaterBlue", (0.18, 0.42, 0.78), 0.5)
M_CASE = flat("TH_ItmCaseOlive", (0.20, 0.22, 0.16), 0.6, 0.2)
M_GREY = flat("TH_ItmGrey", (0.50, 0.50, 0.50), 0.5, 0.3)
M_EDGE = flat("TH_ItmEdge", (0.85, 0.87, 0.90), 0.15, 1.0)
M_MIRROR = flat("TH_ItmMirror", (0.90, 0.92, 0.95), 0.08, 1.0)
M_PAD = flat("TH_ItmPadded", (0.50, 0.40, 0.28), 0.95)
M_PADL = flat("TH_ItmPaddedLight", (0.60, 0.49, 0.34), 0.95)
M_PADD = flat("TH_ItmPaddedDark", (0.28, 0.22, 0.15), 0.9)
M_PLATE = flat("TH_ItmPlate", (0.80, 0.82, 0.80), 0.45, 0.1)
M_SUIT = flat("TH_ItmSuit", (0.09, 0.09, 0.10), 0.7)
M_VISOR = flat("TH_ItmVisor", (0.90, 0.45, 0.10), 0.1, 0.0, 0.5)
LIQ = {"red": flat("TH_ItmLiqRed", (0.80, 0.08, 0.06), 0.15, 0.0, 0.4),
       "yellow": flat("TH_ItmLiqYellow", (0.95, 0.75, 0.10), 0.15, 0.0, 0.4),
       "grey": flat("TH_ItmLiqGrey", (0.45, 0.45, 0.47), 0.5),
       "green": flat("TH_ItmLiqGreen", (0.20, 0.80, 0.25), 0.15, 0.0, 0.4),
       "blue": flat("TH_ItmLiqBlue", (0.15, 0.55, 1.00), 0.1, 0.0, 1.0),
       "amber": flat("TH_ItmLiqAmber", (0.85, 0.50, 0.10), 0.15, 0.0, 0.3),
       "pale": flat("TH_ItmGelPale", (0.85, 0.88, 0.75), 0.2, 0.0, 0.15)}

top = get_col("TatooineItems")
cursor = {}


# ---- roots and parts ----
def item(aid, name, cat, row, w, mk=False):
    """An item's ASSET_ root; w = its width along x, for the row layout."""
    x = cursor.get(row, X0)
    cursor[row] = x + w + GAP
    r = bpy.data.objects.new(f"ASSET_It{name}", None)
    r.empty_display_type = 'ARROWS'; r.empty_display_size = 0.08
    top.objects.link(r)
    r["asset_id"], r["asset_kind"], r["item_category"] = aid, "item", cat
    if mk:
        r["item_mk"] = json.dumps({"material": M_MK.name, "levels": MK})
    r.location = (x + w / 2, ROW_Y[row], 0.0)
    return r


def part(r, pname, bm, m, smooth=False, solid=0.0):
    """smooth: smooth shading, sharp above 40 deg. solid: an open surface given that thickness (Solidify)."""
    ob = mk(f"{r.name[6:]}_{pname}", bm, m, top, bevel=0, smooth=smooth)
    if smooth:
        ob.data.set_sharp_from_angle(angle=math.radians(40))
    if solid:
        s = ob.modifiers.new("Solidify", 'SOLIDIFY'); s.thickness = solid; s.offset = 0.0; s.use_rim = True
    ob.parent = r
    return ob


# ---- shapes (metres, item frame) ----
def box(x0, x1, y0, y1, z0, z1, ins=(0, 0, 0, 0)):
    return box_bm(x0, x1, y0, y1, z0, z1, ins)


def zc(z0, z1, r0, r1=None, seg=10, x=0.0, y=0.0):
    return cyl_bm(x, y, z0, z1, r0, r1, seg)


def xcyl(x0, x1, r, y=0.0, z=0.0, seg=10, r1=None):
    return hcyl_bm((x0 + x1) / 2, y, z, r, x1 - x0, (1, 0), seg, r1)


def ycyl(y0, y1, r, x=0.0, z=0.0, seg=10, r1=None):
    return hcyl_bm(x, (y0 + y1) / 2, z, r, y1 - y0, (0, 1), seg, r1)


def zring(z0, z1, rin, rout, seg=12, x=0.0, y=0.0):
    return annulus_bm(x, y, z0, z1, rin, rout, seg)


def xring(x0, x1, rin, rout, y=0.0, z=0.0, seg=12):
    return xform(annulus_bm(0, 0, x0, x1, rin, rout, seg), rot_y=math.pi / 2, move=(0, y, z))


def loft(rings, loop=True, caps=(True, True)):
    """Faces between successive rings of points (equal counts); loop: each ring is closed, and capped per caps."""
    bm = bmesh.new()
    vs = [[bm.verts.new(p) for p in ring] for ring in rings]
    n = len(rings[0])
    for a, b in zip(vs, vs[1:]):
        for i in range(n if loop else n - 1):
            j = (i + 1) % n
            bm.faces.new((a[i], a[j], b[j], b[i]))
    if loop and caps[0]:
        bm.faces.new(vs[0][::-1])
    if loop and caps[1]:
        bm.faces.new(vs[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def lathe(prof, seg=10, x=0.0, y=0.0, phase=0.0):
    """Surface of revolution about Z: prof = [(r, z), ...], bottom to top. An r of 0 closes to a point; other ends get
    a flat cap."""
    bm = bmesh.new()
    rings = []
    for rad, z in prof:
        if rad <= 1e-6:
            rings.append([bm.verts.new((x, y, z))])
        else:
            rings.append([bm.verts.new((x + rad * math.cos(2 * math.pi * (i + phase) / seg),
                                        y + rad * math.sin(2 * math.pi * (i + phase) / seg), z)) for i in range(seg)])
    for a, b in zip(rings, rings[1:]):
        for i in range(seg):
            j = (i + 1) % seg
            if len(a) == 1 and len(b) > 1:
                bm.faces.new((a[0], b[j], b[i]))
            elif len(b) == 1 and len(a) > 1:
                bm.faces.new((a[i], a[j], b[0]))
            elif len(a) > 1:
                bm.faces.new((a[i], a[j], b[j], b[i]))
    if len(rings[0]) > 1:
        bm.faces.new(rings[0][::-1])
    if len(rings[-1]) > 1:
        bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def ering(z, rx, ry, seg=12, cx=0.0, cy=0.0, a0=None, a1=None):
    """Ellipse at height z: closed (seg points) or, with a0/a1, the open arc from a0 to a1 (seg + 1 points)."""
    angles = [2 * math.pi * i / seg for i in range(seg)] if a0 is None else [a0 + (a1 - a0) * i / seg for i in range(seg + 1)]
    return [(cx + rx * math.cos(a), cy + ry * math.sin(a), z) for a in angles]


FRONT = (-math.pi + 0.35, -0.35)              # the -Y half of an ellipse, as an arc
BACK = (0.35, math.pi - 0.35)


def tube(pts, radii, seg=8, caps=(True, True)):
    """A tube through pts with a radius per point."""
    pts = [Vector(p) for p in pts]
    rings = []
    for k, p in enumerate(pts):
        t = (pts[min(k + 1, len(pts) - 1)] - pts[max(k - 1, 0)]).normalized()
        ref = Vector((0, 0, 1)) if abs(t.z) < 0.9 else Vector((1, 0, 0))
        n = t.cross(ref).normalized(); b = t.cross(n)
        rings.append([p + radii[k] * (math.cos(2 * math.pi * i / seg) * n + math.sin(2 * math.pi * i / seg) * b)
                      for i in range(seg)])
    return loft(rings, True, caps)


def ball(c, r, seg=8, s=(1, 1, 1)):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=max(4, seg // 2), radius=r)
    bmesh.ops.scale(bm, verts=bm.verts, vec=s)
    bmesh.ops.translate(bm, verts=bm.verts, vec=c)
    return bm


def rock(c, size):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=1.0)
    for v in bm.verts:
        v.co *= rng.uniform(0.8, 1.1)
    bmesh.ops.scale(bm, verts=bm.verts, vec=size)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(rng.uniform(0, math.pi), 3, 'Z'))
    bmesh.ops.translate(bm, verts=bm.verts, vec=c)
    for v in bm.verts:                          # flat underneath: it rests on the ground
        v.co.z = max(v.co.z, 0.0)
    return bm


def slab(pts, z0, z1):
    """A polygon pts = (x, y), extruded from z0 to z1."""
    bm = bmesh.new()
    lo = [bm.verts.new((x, y, z0)) for x, y in pts]
    hi = [bm.verts.new((x, y, z1)) for x, y in pts]
    bm.faces.new(lo[::-1]); bm.faces.new(hi)
    for i in range(len(pts)):
        j = (i + 1) % len(pts)
        bm.faces.new((lo[i], lo[j], hi[j], hi[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def rod(p0, p1, r0, r1=None, seg=6):
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r0, radius2=r0 if r1 is None else r1, depth=d.length)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=d.to_track_quat('Z', 'Y').to_matrix())
    bmesh.ops.translate(bm, verts=bm.verts, vec=(p0 + p1) / 2)
    return bm


def beam(p0, p1, w, h):
    """Box beam from p0 to p1, w wide, h high."""
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    bm = box_bm(0.0, d.length, -w / 2, w / 2, -h / 2, h / 2)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=d.to_track_quat('X', 'Z').to_matrix())
    bmesh.ops.translate(bm, verts=bm.verts, vec=p0)
    return bm


def shard(p, r, h, tilt=(0.0, 0.0), seg=6):
    """A pointed hexagonal prism from p, tilted (about x, about y)."""
    bm = lathe([(r, 0.0), (r, h), (0.0, h + r * 1.6)], seg)
    return xform(bm, rot_x=tilt[0], rot_y=tilt[1], move=p)


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


def lie_x(bm, z):
    """A shape built along +Z laid along -X (its z 0 end at x 0): x -> z, z -> -x, then raised by z."""
    return xform(bm, rot_y=-math.pi / 2, move=(0, 0, z))


def mirror_x(bm):
    bmesh.ops.scale(bm, verts=bm.verts, vec=(-1, 1, 1))
    bmesh.ops.reverse_faces(bm, faces=bm.faces)
    return bm


# ======================= 13.1 materials (row 0): the Mk on a band, tag, strap or the crystals =======================
r = item("mat_ore", "Ore", "material", 0, 0.36, mk=True)
part(r, "Rock", rock((0, 0, 0.09), (0.15, 0.12, 0.1)), M_ROCK)
part(r, "Rock2", rock((0.13, 0.07, 0.04), (0.07, 0.06, 0.05)), M_ROCK)
part(r, "Rock3", rock((-0.11, -0.08, 0.04), (0.06, 0.05, 0.045)), M_ROCK)
part(r, "Veins", merge([shard((0.03, -0.04, 0.14), 0.012, 0.035, (0.5, 0.3)), shard((-0.05, 0.02, 0.15), 0.01, 0.03, (-0.3, -0.5)),
                        shard((0.06, 0.04, 0.13), 0.009, 0.025, (-0.4, 0.6)), shard((-0.02, -0.08, 0.1), 0.01, 0.03, (0.9, 0.0))]), M_MK)

r = item("mat_water", "Water", "material", 0, 0.22, mk=True)
part(r, "Body", lathe([(0, 0), (0.1, 0), (0.1, 0.34), (0.085, 0.37), (0, 0.37)], 12), M_STEEL)
part(r, "Ribs", merge([zring(0.06, 0.075, 0.098, 0.106), zring(0.27, 0.285, 0.098, 0.106)]), M_GUN)
part(r, "Band", zring(0.14, 0.2, 0.098, 0.104), M_MK)
part(r, "Label", box(-0.035, 0.035, -0.104, -0.097, 0.215, 0.255), M_WATER)
part(r, "Cap", zc(0.36, 0.4, 0.03, seg=10), M_DARK)
part(r, "Handle", merge([box(-0.07, -0.055, -0.008, 0.008, 0.355, 0.43), box(0.055, 0.07, -0.008, 0.008, 0.355, 0.43),
                         box(-0.07, 0.07, -0.01, 0.01, 0.43, 0.445)]), M_GUN)

r = item("mat_metal", "Metal", "material", 0, 0.26, mk=True)
ING = (0.015, 0.015, 0.012, 0.012)
part(r, "Ingots", merge([box(-0.125, 0.125, -0.085, -0.005, 0, 0.045, ING), box(-0.125, 0.125, 0.005, 0.085, 0, 0.045, ING),
                         box(-0.125, 0.125, -0.04, 0.04, 0.045, 0.09, ING)]), M_STEEL)
part(r, "Stamps", merge([box(-0.03, 0.03, -0.012, 0.012, 0.09, 0.094), box(-0.03, 0.03, -0.086, -0.08, 0.012, 0.033)]), M_MK)

r = item("mat_crystal", "Crystal", "material", 0, 0.16, mk=True)
part(r, "Base", rock((0, 0, 0.015), (0.06, 0.05, 0.03)), M_ROCK)
part(r, "Prisms", merge([shard((0, 0, 0.01), 0.02, 0.09), shard((0.03, 0.01, 0.01), 0.014, 0.06, (0.2, 0.5)),
                         shard((-0.03, 0.015, 0.01), 0.015, 0.065, (0.3, -0.5)), shard((0.0, -0.03, 0.01), 0.012, 0.05, (-0.6, 0.1)),
                         shard((0.01, 0.035, 0.01), 0.011, 0.045, (0.6, 0.2))]), M_MK)

r = item("mat_gas", "Gas", "material", 0, 0.16, mk=True)
part(r, "Bottle", lathe([(0, 0), (0.07, 0), (0.075, 0.012), (0.075, 0.3), (0.06, 0.34), (0.025, 0.355), (0, 0.355)], 12), M_WHITE)
part(r, "Foot", zring(0, 0.015, 0.068, 0.078), M_DARK)
part(r, "Band", zring(0.265, 0.295, 0.073, 0.079), M_MK)
part(r, "Neck", zc(0.35, 0.385, 0.015, seg=8), M_BRASS)
part(r, "Valve", merge([box(-0.02, 0.02, -0.012, 0.012, 0.372, 0.395), xcyl(0.02, 0.045, 0.006, z=0.383, seg=6)]), M_BRASS)
part(r, "Wheel", zring(0.395, 0.403, 0.012, 0.026, 10), M_RED)

r = item("mat_chemical", "Chemical", "material", 0, 0.28, mk=True)
part(r, "Drum", lathe([(0, 0), (0.13, 0), (0.13, 0.35), (0, 0.35)], 12), M_DRUM)
part(r, "Rims", merge([zring(0, 0.015, 0.125, 0.137), zring(0.335, 0.35, 0.125, 0.137), zring(0.11, 0.12, 0.128, 0.135),
                       zring(0.23, 0.24, 0.128, 0.135)]), M_GUN)
part(r, "Hazard", merge([tinted(zring(0.15, 0.2, 0.128, 0.134), 0), tinted(zring(0.143, 0.15, 0.128, 0.134), 1),
                         tinted(zring(0.2, 0.207, 0.128, 0.134), 1)]), [M_HAZ, M_DARK])
part(r, "Bung", zc(0.35, 0.362, 0.02, x=0.06, seg=8), M_DARK)
part(r, "Tag", box(-0.04, 0.04, -0.134, -0.126, 0.255, 0.31), M_MK)

r = item("mat_fiber", "Fiber", "material", 0, 0.4, mk=True)
part(r, "Bundle", xform(lie_x(lathe([(0, 0), (0.075, 0), (0.058, 0.05), (0.054, 0.2), (0.058, 0.35), (0.075, 0.4), (0, 0.4)],
                                    10), 0.075), move=(0.2, 0, 0)), M_FIBER)
for k, x in enumerate((-0.11, 0.11)):
    part(r, f"Tie{k}", xring(x - 0.012, x + 0.012, 0.05, 0.061, z=0.075, seg=10), M_MK)

r = item("mat_hide", "Hide", "material", 0, 0.5, mk=True)
part(r, "Roll", xcyl(-0.25, 0.25, 0.08, z=0.08, seg=10), M_HIDE)
part(r, "Ends", merge([xcyl(-0.254, -0.25, 0.07, z=0.08, seg=10), xcyl(0.25, 0.254, 0.07, z=0.08, seg=10)]), M_HIDED)
part(r, "Flap", slab([(-0.25, -0.02), (0.25, -0.02), (0.22, -0.17), (0.05, -0.2), (-0.2, -0.16)], 0, 0.008), M_HIDE)
for k, x in enumerate((-0.14, 0.14)):
    part(r, f"Strap{k}", xring(x - 0.015, x + 0.015, 0.078, 0.086, z=0.08, seg=10), M_MK)

r = item("mat_bone", "Bone", "material", 0, 0.42, mk=True)


def bone(p0, p1, rad):
    p0, p1 = Vector(p0), Vector(p1)
    d = (p1 - p0).normalized()
    return merge([rod(p0 + d * rad, p1 - d * rad, rad, rad, 6),
                  ball(p0, rad * 1.7, 6, (1.0, 1.25, 0.9)), ball(p1, rad * 1.7, 6, (1.0, 1.25, 0.9))])


part(r, "Bones", merge([bone((-0.2, -0.03, 0.026), (0.2, -0.02, 0.026), 0.016), bone((-0.17, 0.035, 0.026), (0.18, 0.025, 0.026), 0.015),
                        bone((-0.19, 0.0, 0.062), (0.19, 0.005, 0.066), 0.014)]), M_BONE)
part(r, "Cord", xring(-0.015, 0.015, 0.048, 0.056, y=0.0, z=0.042, seg=10), M_MK)

r = item("mat_gland", "Gland", "material", 0, 0.1, mk=True)
part(r, "Base", zc(0, 0.015, 0.045, seg=10), M_GUN)
part(r, "Lid", merge([zc(0.105, 0.12, 0.042, seg=10), zc(0.12, 0.128, 0.012, seg=8)]), M_MK)
part(r, "Rods", merge([zc(0.015, 0.105, 0.004, seg=5, x=0.038 * math.cos(a), y=0.038 * math.sin(a))
                       for a in (math.pi / 2, math.pi / 2 + 2.09, math.pi / 2 + 4.19)]), M_STEEL)
part(r, "Organ", merge([ball((0, 0, 0.058), 0.03, 10, (1.0, 0.8, 1.15)), ball((0.018, -0.01, 0.042), 0.017, 8),
                        tube([(0, 0, 0.09), (0.006, 0.0, 0.1), (0.002, 0.0, 0.106)], [0.008, 0.006, 0.005], 6)]), M_FLESH, smooth=True)

r = item("mat_venom", "Venom", "material", 0, 0.05, mk=True)
part(r, "Venom", lathe([(0, 0), (0.016, 0), (0.018, 0.004), (0.018, 0.05), (0, 0.05)], 10), M_VENOM, smooth=True)
part(r, "Glass", lathe([(0, 0.05), (0.018, 0.05), (0.018, 0.072), (0.011, 0.08), (0.009, 0.084), (0, 0.084)], 10), M_GLASS, smooth=True)
part(r, "Stopper", lathe([(0, 0.08), (0.0085, 0.08), (0.011, 0.1), (0, 0.1)], 8), M_MK)

# ======================= 13.2 cores (row 1): the Mk on the seams; 13.3 add-ons: on the glow =======================
r = item("core_ranged", "CoreRanged", "core", 1, 0.14, mk=True)
part(r, "Plinth", box(-0.05, 0.05, -0.045, 0.045, 0, 0.015), M_DARK)
part(r, "Body", box(-0.045, 0.045, -0.04, 0.04, 0.015, 0.11), M_STEEL)
part(r, "Fins", merge([box(sx * 0.045, sx * 0.068, y - 0.003, y + 0.003, 0.025, 0.1) if sx > 0 else
                       box(sx * 0.068, sx * 0.045, y - 0.003, y + 0.003, 0.025, 0.1)
                       for sx in (-1, 1) for y in (-0.032, -0.016, 0.0, 0.016, 0.032)]), M_GUN)
part(r, "Seams", merge([box(-0.0465, 0.0465, -0.0415, 0.0415, z, z + 0.004) for z in (0.045, 0.075)]
                       + [box(-0.004, 0.004, -0.0418, -0.039, 0.015, 0.11), zc(0.11, 0.122, 0.01, seg=8)]), M_MK)
part(r, "Socket", zring(0.11, 0.126, 0.016, 0.026, 10), M_DARK)

r = item("core_melee", "CoreMelee", "core", 1, 0.12, mk=True)
part(r, "Foot", zc(0, 0.01, 0.03, seg=10), M_DARK)
part(r, "Body", zc(0.01, 0.115, 0.034, seg=10), M_GUN)
part(r, "Brackets", merge([zring(z, z + 0.013, 0.033, 0.042, 10) for z in (0.025, 0.085)]
                          + [box(0.04, 0.06, -0.008, 0.008, z, z + 0.013) for z in (0.025, 0.085)]
                          + [box(-0.06, -0.04, -0.008, 0.008, z, z + 0.013) for z in (0.025, 0.085)]), M_STEEL)
part(r, "Cap", zc(0.115, 0.122, 0.024, seg=10), M_DARK)
part(r, "Seams", merge([zring(z, z + 0.004, 0.033, 0.0365, 10) for z in (0.052, 0.066)] + [zc(0.122, 0.125, 0.01, seg=8)]), M_MK)

r = item("core_armor", "CoreArmor", "core", 1, 0.25, mk=True)
part(r, "Pad", box(-0.125, 0.125, -0.09, 0.09, 0, 0.022, (0.012, 0.012, 0.012, 0.012)), M_PAD)
part(r, "Node", zc(0.018, 0.04, 0.04, seg=12), M_STEEL)
part(r, "Seams", merge([zc(0.04, 0.046, 0.024, seg=12), zring(0.018, 0.034, 0.039, 0.044, 12),
                        box(-0.112, -0.044, -0.003, 0.003, 0.018, 0.0245), box(0.044, 0.112, -0.003, 0.003, 0.018, 0.0245)]), M_MK)
part(r, "Straps", merge([box(x - 0.012, x + 0.012, -0.095, 0.095, 0.0, 0.026) for x in (-0.08, 0.08)]), M_HIDED)
part(r, "Buckles", merge([box(x - 0.015, x + 0.015, -0.065, -0.045, 0.024, 0.031) for x in (-0.08, 0.08)]), M_STEEL)

r = item("core_propulsion", "CorePropulsion", "core", 1, 0.22, mk=True)
part(r, "Body", lathe([(0, 0), (0.045, 0), (0.06, 0.04), (0.075, 0.05), (0.08, 0.06), (0.08, 0.24), (0, 0.24)], 12), M_STEEL)
part(r, "Base", zring(0, 0.012, 0.04, 0.062, 12), M_DARK)
part(r, "Intake", zring(0.235, 0.3, 0.08, 0.1, 12), M_GUN)
part(r, "Hub", zc(0.24, 0.285, 0.025, seg=10), M_DARK)
part(r, "Blades", merge([xform(box(0.025, 0.079, -0.009, 0.009, -0.002, 0.002), rot_x=0.5, rot_z=k * math.pi / 4, move=(0, 0, 0.27))
                         for k in range(8)]), M_STEEL)
part(r, "Seams", merge([zring(z, z + 0.006, 0.078, 0.083, 12) for z in (0.1, 0.17)] + [zc(0.285, 0.29, 0.012, seg=8)]), M_MK)

r = item("addon_weapon", "AddonWeapon", "addon", 1, 0.12, mk=True)
part(r, "Tooth", tube([(-0.045, 0, 0.016), (-0.01, 0.004, 0.016), (0.025, 0.013, 0.013), (0.05, 0.03, 0.008)],
                      [0.016, 0.014, 0.009, 0.0015], 8), M_BONE, smooth=True)
part(r, "Mount", xcyl(-0.058, -0.04, 0.0175, z=0.0175, seg=8), M_STEEL)
part(r, "Binding", xcyl(-0.032, -0.022, 0.0168, y=0.0015, z=0.0165, seg=8), M_ROPE)
part(r, "Splinter", xform(lathe([(0.006, 0.0), (0.006, 0.05), (0.0, 0.062)], 6), rot_y=math.pi / 2, rot_z=-0.15,
                          move=(-0.05, -0.022, 0.007)), M_MK)

r = item("addon_armor", "AddonArmor", "addon", 1, 0.13, mk=True)
rings = []
for i in range(7):
    v = i / 6
    w = 0.062 * math.sin(math.pi * (0.06 + 0.88 * v)) ** 0.6
    rings.append([(u * w, -0.075 + 0.15 * v, 0.004 + 0.024 * (1 - u * u) * math.sin(math.pi * (0.05 + 0.9 * v)) ** 0.5)
                  for u in (-1, -0.66, -0.33, 0, 0.33, 0.66, 1)])
part(r, "Scale", loft(rings, loop=False), M_CHITIN, solid=0.006)
part(r, "Ridge", tube([(0, -0.068, 0.012), (0, -0.03, 0.026), (0, 0.02, 0.031), (0, 0.06, 0.02), (0, 0.072, 0.01)],
                      [0.002, 0.005, 0.006, 0.004, 0.0015], 6), M_MK)

r = item("addon_tuning", "AddonTuning", "addon", 1, 0.2, mk=True)
part(r, "Tray", merge([box(-0.1, 0.1, -0.06, 0.06, 0, 0.01), box(-0.1, 0.1, -0.06, -0.054, 0.01, 0.035),
                       box(-0.1, 0.1, 0.054, 0.06, 0.01, 0.035), box(-0.1, -0.094, -0.054, 0.054, 0.01, 0.035),
                       box(0.094, 0.1, -0.054, 0.054, 0.01, 0.035)]), M_GUN)
part(r, "Lid", xform(box(-0.1, 0.1, 0, 0.12, 0, 0.006), rot_x=math.radians(80), move=(0, 0.06, 0.035)), M_GUN)
part(r, "Injectors", merge([xcyl(-0.08, 0.04, 0.009, y=y, z=0.019, seg=8) for y in (-0.035, -0.012, 0.011)]), M_STEEL)
part(r, "Nozzle", xform(lathe([(0, 0), (0.017, 0), (0.012, 0.05), (0.008, 0.06), (0, 0.06)], 8), rot_y=math.pi / 2,
                        move=(-0.03, 0.036, 0.019)), M_BRASS)
part(r, "Glow", merge([xcyl(0.04, 0.05, 0.007, y=y, z=0.019, seg=8) for y in (-0.035, -0.012, 0.011)]
                      + [xcyl(0.024, 0.032, 0.0095, y=0.036, z=0.019, seg=8)]), M_MK)
part(r, "Latch", box(-0.012, 0.012, -0.064, -0.06, 0.02, 0.034), M_STEEL)

r = item("addon_additive", "AddonAdditive", "addon", 1, 0.04, mk=True)
part(r, "Bottle", lathe([(0, 0), (0.015, 0), (0.016, 0.003), (0.016, 0.032), (0.008, 0.04), (0.007, 0.043), (0, 0.043)], 10),
     M_GLASSB, smooth=True)
part(r, "Label", zring(0.01, 0.025, 0.0155, 0.0168, 10), M_WHITE)
part(r, "Collar", zc(0.04, 0.047, 0.009, seg=10), M_DARK)
part(r, "Bulb", lathe([(0, 0.047), (0.008, 0.047), (0.0092, 0.053), (0.0085, 0.058), (0.005, 0.061), (0, 0.062)], 10), M_MK, smooth=True)

# ======================= 13.4 crafted parts (rows 2 and 3) =======================


def barrel(r, L, nfins, R):
    a = R / 0.02                                     # scale of the pistol barrel's details
    x0 = -L / 2
    part(r, "Tube", xcyl(x0, L / 2 - 0.015 * a, 0.011 * a, z=R, seg=8), M_DARK)
    part(r, "Breech", box(x0, x0 + 0.04 * a, -0.017 * a, 0.017 * a, R - 0.017 * a, R + 0.017 * a), M_GUN)
    part(r, "Crystal", xform(lathe([(0.007 * a, 0), (0.007 * a, 0.022 * a), (0, 0.03 * a)], 6), rot_y=math.pi / 2,
                             move=(x0 + 0.006 * a, 0, R + 0.019 * a)), M_CRYSTAL)
    step = (L - 0.075 * a) / nfins
    part(r, "Fins", merge([xring(x - 0.002 * a, x + 0.002 * a, 0.01 * a, R, z=R, seg=10)
                           for x in [x0 + 0.05 * a + step * (k + 0.5) for k in range(nfins)]]), M_STEEL)
    part(r, "Muzzle", xcyl(L / 2 - 0.015 * a, L / 2, 0.015 * a, z=R, seg=8), M_GUN)


barrel(item("part_barrel_pistol", "BarrelPistol", "part", 2, 0.15), 0.15, 5, 0.02)
barrel(item("part_barrel_carbine", "BarrelCarbine", "part", 2, 0.3), 0.3, 9, 0.025)

r = item("part_power_cell", "PowerCell", "part", 2, 0.05)
part(r, "Body", zc(0.006, 0.056, 0.02, seg=10), M_GUN)
part(r, "Caps", merge([zc(0, 0.008, 0.021, seg=10), zc(0.054, 0.062, 0.021, seg=10)]), M_BRASS)
part(r, "Window", zring(0.022, 0.04, 0.0195, 0.0212, 10), M_GLOW)
part(r, "Contacts", merge([zc(0.062, 0.074, 0.003, seg=6, x=x) for x in (-0.008, 0.008)]), M_STEEL)

# stock: lying on its side, the butt at -x, the mount at +x; outline = lower/upper edges, piecewise linear in x
ST_X = [-0.125, -0.11, -0.06, 0.04, 0.1]
ST_LO = [-0.06, -0.065, -0.052, -0.02, -0.014]
ST_HI = [0.035, 0.036, 0.03, 0.018, 0.014]


def st_y(xs, ys, x):
    for k in range(len(xs) - 1):
        if xs[k] <= x <= xs[k + 1]:
            return ys[k] + (ys[k + 1] - ys[k]) * (x - xs[k]) / (xs[k + 1] - xs[k])
    return ys[-1]


def stock_slab(xa, xb, z0, z1, inset=0.0):
    xs = [x for x in ST_X if xa < x < xb]
    xs = [xa] + xs + [xb]
    return slab([(x, st_y(ST_X, ST_LO, x) + inset) for x in xs] + [(x, st_y(ST_X, ST_HI, x) - inset) for x in xs[::-1]], z0, z1)


for aid, name, m_body in (("part_stock_bone", "StockBone", M_BONE), ("part_stock_fiber", "StockFiber", M_FIBER)):
    r = item(aid, name, "part", 2, 0.26)
    part(r, "Body", stock_slab(-0.122, 0.1, 0, 0.035), m_body)
    part(r, "Butt", stock_slab(-0.13, -0.122, -0.002, 0.037), M_DARK)
    part(r, "Mount", box(0.1, 0.126, -0.012, 0.012, 0.005, 0.03), M_STEEL)
    if m_body is M_FIBER:                            # woven: raised bands across
        part(r, "Weave", merge([stock_slab(x, x + 0.012, 0.0, 0.038, -0.001) for x in (-0.09, -0.05, -0.01, 0.03, 0.065)]), M_ROPE)
    else:                                            # bone: a darker cheek-rest inlay
        part(r, "Inlay", stock_slab(-0.1, 0.0, 0.035, 0.037, 0.012), M_HIDED)

r = item("part_grip", "Grip", "part", 2, 0.14)
part(r, "Core", xcyl(-0.07, 0.07, 0.007, z=0.018, seg=8), M_STEEL)
part(r, "Wrap", xcyl(-0.045, 0.045, 0.015, z=0.018, seg=10), M_HIDE)
part(r, "Lacing", merge([xring(x - 0.002, x + 0.002, 0.014, 0.0165, z=0.018, seg=10) for x in (-0.033, -0.011, 0.011, 0.033)]), M_HIDED)
part(r, "Ends", merge([xcyl(-0.06, -0.045, 0.018, z=0.018, seg=8), xcyl(0.045, 0.055, 0.012, z=0.018, seg=8)]), M_STEEL)

r = item("part_blade", "Blade", "part", 2, 0.7)
part(r, "Blade", slab([(-0.27, -0.018), (0.27, -0.018), (0.33, -0.004), (0.35, 0.018), (-0.27, 0.018)], 0, 0.007), M_STEEL)
part(r, "Edge", slab([(-0.25, -0.0185), (0.27, -0.0185), (0.27, -0.012), (-0.25, -0.012)], 0.0005, 0.0075), M_EDGE)
part(r, "Fuller", slab([(-0.25, 0.006), (0.2, 0.006), (0.2, 0.011), (-0.25, 0.011)], 0.0065, 0.0074), M_DARK)
part(r, "Tang", slab([(-0.35, -0.007), (-0.27, -0.009), (-0.27, 0.009), (-0.35, 0.007)], 0.001, 0.006), M_DARK)

r = item("part_knuckle_plate", "KnucklePlate", "part", 2, 0.13)
XS = (-0.045, -0.015, 0.015, 0.045)
part(r, "Plate", box(-0.06, 0.06, -0.03, -0.012, 0, 0.02), M_STEEL)
part(r, "Studs", merge([box(x - 0.011, x + 0.011, -0.037, -0.03, 0.003, 0.017, (0.003, 0.003, 0.0, 0.0)) for x in XS]), M_DARK)
part(r, "Rings", merge([zring(0, 0.012, 0.0095, 0.0135, 10, x=x, y=0.0015) for x in XS]), M_STEEL)
part(r, "PalmBar", box(-0.055, 0.055, 0.014, 0.024, 0, 0.012), M_DARK)

r = item("part_vibro_unit", "VibroUnit", "part", 2, 0.09)
part(r, "Housing", box(-0.035, 0.015, -0.018, 0.018, 0, 0.028), M_GUN)
part(r, "Coil", merge([xcyl(0.015, 0.04, 0.011, z=0.014, seg=8)] + [xring(x, x + 0.003, 0.01, 0.0135, z=0.014, seg=8)
                                                                  for x in (0.019, 0.026, 0.033)]), M_BRASS)
part(r, "Emitter", xcyl(0.04, 0.045, 0.014, z=0.014, seg=10), M_STEEL)
part(r, "Socket", zring(0.028, 0.033, 0.006, 0.011, 8, x=-0.015), M_DARK)
part(r, "Glow", merge([zc(0.026, 0.031, 0.0062, seg=8, x=-0.015), box(-0.03, 0.01, -0.0188, -0.017, 0.01, 0.018)]), M_GLOW)

r = item("part_padded_segment", "PaddedSegment", "part", 2, 0.3)
part(r, "Backing", box(-0.15, 0.15, -0.1, 0.1, 0, 0.008), M_PADD)
part(r, "Quilt", merge([box(-0.15 + 0.075 * i + 0.004, -0.15 + 0.075 * (i + 1) - 0.004, -0.1 + 0.2 / 3 * j + 0.004,
                            -0.1 + 0.2 / 3 * (j + 1) - 0.004, 0.008, 0.022, (0.009,) * 4) for i in range(4) for j in range(3)]), M_PAD)
part(r, "Binding", merge([box(-0.152, 0.152, -0.102, -0.094, 0, 0.014), box(-0.152, 0.152, 0.094, 0.102, 0, 0.014),
                          box(-0.152, -0.144, -0.094, 0.094, 0, 0.014), box(0.144, 0.152, -0.094, 0.094, 0, 0.014)]), M_HIDED)

r = item("part_composite_segment", "CompositeSegment", "part", 2, 0.32)


def arc_sheet(R, a, y0, y1, z0, n=8):
    """A strip of the cylinder of radius R (axis along y), from -a to a about the top, lowest edge at z0."""
    ring = lambda y: [(R * math.sin(-a + 2 * a * i / n), y, R * math.cos(-a + 2 * a * i / n) - R * math.cos(a) + z0)
                      for i in range(n + 1)]
    return loft([ring(y0), ring(y1)], loop=False)


A = math.radians(40)
part(r, "Plate", arc_sheet(0.24, A, -0.1, 0.1, 0.012), M_PLATE, solid=0.01)
part(r, "Inner", arc_sheet(0.232, A + 0.035, -0.104, 0.104, 0.003), M_MIRROR, solid=0.003)
part(r, "Ridges", merge([arc_sheet(0.246, A - 0.02, y - 0.006, y + 0.006, 0.0175) for y in (-0.05, 0.05)]), M_GUN, solid=0.004)


def ampoule(r, liquid):
    part(r, "Liquid", lathe([(0, 0), (0.01, 0), (0.011, 0.003), (0.011, 0.036), (0, 0.036)], 8), liquid, smooth=True)
    part(r, "Glass", lathe([(0, 0.036), (0.011, 0.036), (0.011, 0.04), (0.004, 0.048), (0.003, 0.054), (0.0045, 0.056),
                            (0.002, 0.06), (0, 0.061)], 8), M_GLASS, smooth=True)
    part(r, "Label", zring(0.012, 0.02, 0.0105, 0.0116, 8), M_WHITE)


for aid, name, liq in (("part_compound_restorative", "CompoundRestorative", "pale"),
                       ("part_compound_stimulant", "CompoundStimulant", "amber"),
                       ("part_compound_hardener", "CompoundHardener", "grey"),
                       ("part_compound_vital", "CompoundVital", "green")):
    ampoule(item(aid, name, "part", 2, 0.03), LIQ[liq])

VIAL_GLASS = [(0.017, 0.07), (0.011, 0.078), (0.009, 0.083), (0, 0.083)]


def vial(r, liquid):
    """Faceted vial (6 sides) with a cap, empty or with liquid up to 0.055."""
    if liquid is None:
        part(r, "Glass", lathe([(0, 0), (0.014, 0), (0.017, 0.006)] + VIAL_GLASS, 6), M_GLASS)
    else:
        part(r, "Liquid", lathe([(0, 0), (0.014, 0), (0.017, 0.006), (0.017, 0.055), (0, 0.055)], 6), liquid)
        part(r, "Glass", lathe([(0, 0.055), (0.017, 0.055)] + VIAL_GLASS, 6), M_GLASS)
    part(r, "Cap", lathe([(0, 0.08), (0.0115, 0.08), (0.0115, 0.1), (0, 0.1)], 6), M_STEEL)


vial(item("part_vial", "Vial", "part", 2, 0.04), None)

# big parts (row 3)
r = item("part_structural_module", "StructuralModule", "part", 3, 1.0)
W = 0.03
edges = [((-0.5, y, z), (0.5, y, z)) for y in (-0.5 + W, 0.5 - W) for z in (W, 1 - W)] \
    + [((x, -0.5, z), (x, 0.5, z)) for x in (-0.5 + W, 0.5 - W) for z in (W, 1 - W)] \
    + [((x, y, 0), (x, y, 1)) for x in (-0.5 + W, 0.5 - W) for y in (-0.5 + W, 0.5 - W)]
part(r, "Frame", merge([beam(a, b, 2 * W, 2 * W) for a, b in edges]), M_GUN)
part(r, "Panels", merge([box(-0.49, -0.47, -0.44, 0.44, 0.06, 0.94), box(-0.44, 0.44, 0.47, 0.49, 0.06, 0.94)]), M_STEEL)
part(r, "Braces", merge([beam((-0.44, -0.47, 0.06), (0.44, -0.47, 0.94), 0.05, 0.03), beam((0.47, -0.44, 0.06), (0.47, 0.44, 0.94), 0.03, 0.05)]),
     M_HAZ)
part(r, "Feet", merge([box(x - 0.06, x + 0.06, y - 0.06, y + 0.06, 0, 0.02) for x in (-0.47, 0.47) for y in (-0.47, 0.47)]), M_DARK)

r = item("part_wall_module", "WallModule", "part", 3, 2.0)
part(r, "Wall", box(-1, 1, -0.15, 0.15, 0, 1, (0.0, 0.0, 0.02, 0.02)), M_CLAY)
part(r, "Truss", merge([box(-1, 1, -0.2, -0.15, 0.04, 0.12), box(-1, 1, -0.2, -0.15, 0.86, 0.94)]
                       + [box(x - 0.04, x + 0.04, -0.2, -0.15, 0.12, 0.86) for x in (-0.94, 0.0, 0.94)]
                       + [beam((x0, -0.175, 0.14), (x1, -0.175, 0.84), 0.05, 0.05) for x0, x1 in ((-0.9, -0.05), (0.9, 0.05))]), M_GUN)

r = item("part_storage_module", "StorageModule", "part", 3, 1.0)
part(r, "Bin", box(-0.48, 0.48, -0.43, 0.43, 0.05, 0.95), M_GUN)
part(r, "Skids", merge([box(-0.5, 0.5, y - 0.05, y + 0.05, 0, 0.05) for y in (-0.33, 0.33)]), M_DARK)
part(r, "Ribs", merge([box(-0.5, 0.5, -0.45, 0.45, z, z + 0.04) for z in (0.3, 0.7)]), M_STEEL)
part(r, "Corners", merge([box(x - 0.03, x + 0.03, y - 0.03, y + 0.03, 0.05, 0.97) for x in (-0.48, 0.48) for y in (-0.43, 0.43)]), M_DARK)
part(r, "Hatch", merge([zc(0.95, 0.99, 0.25, seg=16), zc(0.99, 1.06, 0.025, seg=8)]), M_STEEL)
part(r, "Wheel", merge([zring(1.04, 1.055, 0.08, 0.1, 12)] + [xform(box(0.0, 0.09, -0.008, 0.008, 1.042, 1.053), rot_z=k * math.pi / 2)
                                                               for k in range(4)]), M_RED)
part(r, "Label", box(-0.2, 0.2, -0.455, -0.43, 0.45, 0.6), M_HAZ)

r = item("part_extraction_unit", "ExtractionUnit", "part", 3, 1.3)
DX = -0.32                                         # centres the footprint (base at -0.3, drill at 0.8)
part(r, "Base", box(-0.3 + DX, 0.3 + DX, -0.3, 0.3, 0, 0.2), M_GUN)
part(r, "Turntable", zc(0.2, 0.3, 0.22, seg=16, x=DX), M_STEEL)
part(r, "Post", box(-0.08 + DX, 0.08 + DX, -0.08, 0.08, 0.3, 1.25), M_HAZ)
part(r, "Arm", beam((DX, 0, 1.18), (0.86 + DX, 0, 1.24), 0.14, 0.14), M_HAZ)
part(r, "Piston", merge([rod((0.08 + DX, 0, 0.7), (0.45 + DX, 0, 1.13), 0.03, 0.03, 8),
                         rod((0.3 + DX, 0, 0.95), (0.48 + DX, 0, 1.16), 0.018, 0.018, 8)]), M_STEEL)
part(r, "Motor", merge([zc(0.85, 1.3, 0.13, seg=12, x=0.8 + DX), zc(1.3, 1.36, 0.09, seg=12, x=0.8 + DX)]), M_GUN)
part(r, "Shaft", zc(0.15, 0.85, 0.04, seg=8, x=0.8 + DX), M_STEEL)
part(r, "Flights", merge([xform(box(0.0, 0.12, -0.03, 0.03, -0.006, 0.006), rot_x=0.3, rot_z=k * math.pi / 3,
                                move=(0.8 + DX, 0, 0.2 + k * 0.05)) for k in range(13)]), M_STEEL)
part(r, "Tip", lathe([(0, 0.02), (0.075, 0.15), (0, 0.16)], 8, x=0.8 + DX), M_DARK)

r = item("part_assembly_unit", "AssemblyUnit", "part", 3, 1.5)
legs = []
for x in (-0.7, 0.7):
    legs += [beam((x, -0.35, 0.02), (x, 0, 1.12), 0.07, 0.07), beam((x, 0.35, 0.02), (x, 0, 1.12), 0.07, 0.07),
             box(x - 0.06, x + 0.06, -0.42, 0.42, 0, 0.04)]
part(r, "Legs", merge(legs), M_GUN)
part(r, "Rail", box(-0.75, 0.75, -0.06, 0.06, 1.1, 1.2), M_STEEL)
part(r, "Carriage", box(-0.15, 0.15, -0.1, 0.1, 1.05, 1.27), M_HAZ)
part(r, "Column", zc(0.66, 1.05, 0.07, seg=10), M_GUN)
part(r, "Head", box(-0.12, 0.12, -0.12, 0.12, 0.54, 0.68), M_DARK)
part(r, "Nozzle", lathe([(0, 0.4), (0.05, 0.54), (0, 0.54)], 10), M_BRASS)
part(r, "Emitter", zring(0.5, 0.53, 0.035, 0.06, 10), M_GLOW)
part(r, "Bed", box(-0.4, 0.4, -0.25, 0.25, 0, 0.1), M_STEEL)

r = item("part_engine", "Engine", "part", 3, 0.46)
POD = [(0, 0), (0.055, 0), (0.085, 0.06), (0.11, 0.12), (0.11, 0.5), (0.1, 0.56), (0, 0.56)]


def along_y(bm, x, z):
    """A shape built along +Z turned to point along -Y (its z 0 at y 0.28, the rear), at (x, z)."""
    return xform(bm, rot_x=math.pi / 2, move=(x, 0.28, z))


for k, x in enumerate((-0.12, 0.12)):
    part(r, f"Pod{k}", along_y(lathe(POD, 12), x, 0.13), M_KHAKI)
    part(r, f"Intake{k}", along_y(zring(0.53, 0.6, 0.088, 0.12, 12), x, 0.13), M_GUN)
    part(r, f"Fan{k}", along_y(merge([zc(0.545, 0.55, 0.095, seg=12), lathe([(0.03, 0.55), (0.0, 0.59)], 8)]), x, 0.13), M_DARK)
    part(r, f"Stripe{k}", along_y(zring(0.3, 0.33, 0.108, 0.113, 12), x, 0.13), M_RED)
    part(r, f"Nozzle{k}", along_y(zc(-0.006, 0.002, 0.05, seg=10), x, 0.13), M_GLOW)
part(r, "Mount", merge([box(-0.12, 0.12, -0.2, 0.2, 0.0, 0.11), box(-0.03, 0.03, -0.22, 0.24, 0.11, 0.22)]), M_DARK)

r = item("part_repulsor", "Repulsor", "part", 3, 0.5)
part(r, "Disc", lathe([(0, 0.05), (0.2, 0.05), (0.25, 0.08), (0.25, 0.1), (0.2, 0.12), (0.06, 0.13), (0, 0.13)], 16), M_GUN)
part(r, "Ribs", merge([xform(box(0.07, 0.22, -0.008, 0.008, 0.11, 0.135), rot_z=k * math.pi / 4) for k in range(8)]), M_STEEL)
part(r, "Cap", zc(0.13, 0.15, 0.06, seg=12), M_KHAKI)
part(r, "Crystals", merge([lathe([(0, 0.0), (0.025, 0.025), (0.025, 0.055), (0, 0.055)], 6,
                                 x=0.16 * math.cos(k * math.pi / 3), y=0.16 * math.sin(k * math.pi / 3)) for k in range(6)]), M_CRYSTAL)
part(r, "Emitter", zc(0.02, 0.05, 0.05, seg=10), M_GLOW)

r = item("part_chassis", "Chassis", "part", 3, 0.7)
sw = bpy.data.objects.get("ASSET_Swoop")
if sw is None:
    raise RuntimeError("part_chassis copies the swoop's frame: run th_swoop.py first")
KEEP = ("SW_Bar", "SW_Fairing", "SW_Footboard", "SW_Fork", "SW_Grip", "SW_RearCowl", "SW_SeatBase", "SW_SeatRib", "SW_Skid",
        "SW_Spine", "SW_Stem", "SW_Strut", "SW_Vane", "SW_VaneHub")         # the swoop without its engine pods and trim
src = [o for o in sw.children if o.type == 'MESH' and o.name.split(".")[0] in KEEP]
bpy.context.view_layer.update()
skid_z = min((sw.matrix_world.inverted() @ o.matrix_world @ Vector(c)).z for o in src if o.name.startswith("SW_Skid")
             for c in o.bound_box)
for o in src:
    name = "ItChassis_" + o.name[3:]
    if bpy.data.meshes.get(name):                     # a previous run's copy (its object went with the collection)
        bpy.data.meshes.remove(bpy.data.meshes[name])
    c = o.copy(); c.data = o.data.copy()
    c.name = c.data.name = name
    top.objects.link(c)
    c.parent = r
    c.matrix_parent_inverse = Matrix.Identity(4)
    c.matrix_basis = Matrix.Translation((0, 0, -skid_z)) @ sw.matrix_world.inverted() @ o.matrix_world

# ======================= 13.6 armour (rows 4 padded, 5 composite), as objects =======================


def limb(parts, y, z):
    """[(bm, material)] built along +Z (z 0 = the hand end) laid along -X at (y, z), centred on x."""
    out = []
    for bm, m in parts:
        lo = min(v.co.z for v in bm.verts); hi = max(v.co.z for v in bm.verts)
        out.append((lie_x(bm, z), m, lo, hi))
    L0 = min(lo for _, _, lo, _ in out); L1 = max(hi for _, _, _, hi in out)
    for bm, _, _, _ in out:
        bmesh.ops.translate(bm, verts=bm.verts, vec=((L0 + L1) / 2, y, 0))
    return [(bm, m) for bm, m, _, _ in out]


def tube_z(z0, z1, r0, r1, seg=10, ry=1.1):
    """An elliptic tube along z (x radius r, y radius r x ry), capped."""
    return loft([ering(z0, r0, r0 * ry, seg), ering(z1, r1, r1 * ry, seg)])


def band_z(z0, z1, r, seg=10, ry=1.1):
    return tube_z(z0, z1, r, r, seg, ry)


def dome(c, rx, ry, rz, lat0=0.0, seg=12, rows=4):
    """Upper part of an ellipsoid from latitude lat0 (radians) to the pole, the bottom capped."""
    rings = [ering(c[2] + rz * math.sin(t), rx * math.cos(t), ry * math.cos(t), seg, c[0], c[1])
             for t in [lat0 + (math.pi / 2 - lat0) * k / rows for k in range(rows)]]
    bm = loft(rings, caps=(True, False))
    tip = bm.verts.new((c[0], c[1], c[2] + rz))
    last = [v for v in bm.verts][-seg - 1:-1]
    for i in range(seg):
        bm.faces.new((last[i], last[(i + 1) % seg], tip))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def shell(rings_z, a, seg=8, out=0.0):
    """An open shell over the arc a = (a0, a1) of ellipses [(z, rx, ry)], pushed out by `out`."""
    return loft([ering(z, rx + out, ry + out, seg, a0=a[0], a1=a[1]) for z, rx, ry in rings_z], loop=False)


# --- padded
r = item("armor_padded_helmet", "ArmorPaddedHelmet", "armor", 4, 0.26)
part(r, "Cap", dome((0, 0, 0.12), 0.105, 0.12, 0.11, 0.0, 14, 4), M_PAD, smooth=True)
part(r, "Seam", band_z(0.16, 0.17, 0.1015, 14, 1.14), M_PADD)
part(r, "Band", band_z(0.105, 0.135, 0.111, 14, 1.13), M_PADD)
for sx in (-1, 1):
    part(r, f"Flap{sx}", prism([(-0.05, 0.13), (0.05, 0.13), (0.04, 0.02), (0.0, 0.0), (-0.04, 0.02)], sx * 0.1 - 0.0075,
                               sx * 0.1 + 0.0075, plane='YZ'), M_PAD)
part(r, "NeckFlap", prism([(-0.07, 0.13), (0.07, 0.13), (0.06, 0.04), (-0.06, 0.04)], 0.11, 0.125), M_PAD)
part(r, "ChinStrap", box(-0.1, 0.1, -0.008, 0.008, 0.0, 0.008), M_HIDED)

r = item("armor_padded_torso", "ArmorPaddedTorso", "armor", 4, 0.38)
TORSO = [(0.0, 0.15, 0.1), (0.1, 0.15, 0.1), (0.25, 0.165, 0.11), (0.38, 0.175, 0.115), (0.45, 0.16, 0.105), (0.5, 0.09, 0.07)]
part(r, "Body", loft([ering(z, rx, ry, 16) for z, rx, ry in TORSO]), M_PAD, smooth=True)
part(r, "Collar", band_z(0.48, 0.55, 0.07, 12, 0.86), M_PADD)
part(r, "Panels", merge([box(x0, x1, -0.124, -0.06, 0.22, 0.42) for x0, x1 in ((-0.13, -0.012), (0.012, 0.13))]),
     M_PADL)
part(r, "Quilting", merge([box(x0, x1, -0.126, -0.12, z, z + 0.006) for x0, x1 in ((-0.125, -0.017), (0.017, 0.125)) for z in (0.285, 0.355)]
                          + [box(-0.004, 0.004, -0.122, -0.11, 0.08, 0.45)]), M_PADD)
part(r, "Belt", band_z(0.03, 0.08, 0.153, 16, 0.69), M_HIDED)
part(r, "Pouches", merge([box(x - 0.025, x + 0.025, -0.125, -0.06, 0.025, 0.085, (0.004, 0.004, 0.004, 0.0)) for x in (-0.1, -0.045, 0.045, 0.1)]),
     M_PADD)
part(r, "Buckle", box(-0.015, 0.015, -0.113, -0.104, 0.035, 0.075), M_STEEL)
part(r, "Shoulders", merge([ball((sx * 0.12, 0.0, 0.45), 0.055, 10, (1.0, 1.15, 0.55)) for sx in (-1, 1)]), M_PAD, smooth=True)

r = item("armor_padded_arms", "ArmorPaddedArms", "armor", 4, 0.42)
for k, y in enumerate((-0.065, 0.065)):
    arm = [(tube_z(0.0, 0.18, 0.038, 0.044, 10, 1.15), M_PAD),                  # bracer, wrist at z 0
           (band_z(0.03, 0.045, 0.0405, 10, 1.15), M_HIDED), (band_z(0.12, 0.135, 0.0435, 10, 1.15), M_HIDED),
           (tube_z(0.22, 0.42, 0.046, 0.054, 10, 1.15), M_PAD),                  # bicep pad
           (band_z(0.28, 0.288, 0.049, 10, 1.15), M_PADD), (band_z(0.35, 0.358, 0.0515, 10, 1.15), M_PADD)]
    for j, (bm, m) in enumerate(limb(arm, y, 0.055)):
        part(r, f"Arm{k}_{j}", bm, m)

r = item("armor_padded_legs", "ArmorPaddedLegs", "armor", 4, 0.4)
LEG = [(0.0, 0.05, 0.055), (0.25, 0.055, 0.06), (0.45, 0.06, 0.065), (0.65, 0.075, 0.08), (0.85, 0.085, 0.09)]
for k, sx in enumerate((-1, 1)):
    x = sx * 0.095
    part(r, f"Leg{k}", loft([ering(z, rx, ry, 12, x) for z, rx, ry in LEG]), M_PADD, smooth=True)
    part(r, f"Thigh{k}", xform(shell([(0.5, 0.068, 0.073), (0.64, 0.075, 0.08), (0.78, 0.082, 0.087)], FRONT, 8, 0.008),
                              move=(x, 0, 0)), M_PADL, solid=0.012)
    part(r, f"Knee{k}", ball((x, -0.06, 0.45), 0.05, 10, (1.0, 0.55, 1.15)), M_PAD, smooth=True)
    part(r, f"Strap{k}", xform(band_z(0.4, 0.415, 0.062, 12, 1.1), move=(x, 0, 0)), M_HIDED)
part(r, "Hips", loft([ering(z, 0.19, 0.105, 16) for z in (0.78, 0.92)]), M_PADD, smooth=True)
part(r, "Belt", band_z(0.86, 0.9, 0.193, 16, 0.56), M_HIDED)


def glove(r, k, y, side, m_cuff, m_hand, m_fing, plates=None):
    """Lying palm down, the wrist at -x, fingers at +x; side -1 = left (thumb towards +y), 1 = right."""
    part(r, f"Cuff{k}", xform(lie_x(tube_z(0.0, 0.065, 0.033, 0.036, 10, 1.3), 0.034), move=(-0.035, y, 0)), m_cuff)
    part(r, f"Hand{k}", box(-0.045, 0.04, y - 0.041, y + 0.041, 0, 0.03, (0.006, 0.0, 0.006, 0.006)), m_hand)
    part(r, f"Fingers{k}", merge([box(0.035, 0.095 - abs(j - 1.5) * 0.008, y + f - 0.0085, y + f + 0.0085, 0.0, 0.017)
                                  for j, f in enumerate((-0.03, -0.01, 0.01, 0.03))]), m_fing)
    part(r, f"Thumb{k}", xform(box(0.0, 0.05, -0.009, 0.009, 0.0, 0.017), rot_z=-side * 0.6, move=(-0.005, y - side * 0.04, 0)), m_fing)
    if plates:
        plates(r, k, y)


r = item("armor_padded_gloves", "ArmorPaddedGloves", "armor", 4, 0.2)
for k, (y, side) in enumerate(((-0.06, -1), (0.06, 1))):
    glove(r, k, y, side, M_PAD, M_PAD, M_PADD)
    part(r, f"CuffSeam{k}", xform(lie_x(band_z(0.02, 0.028, 0.0345, 10, 1.3), 0.034), move=(-0.035, y, 0)), M_PADD)

r = item("armor_padded_boots", "ArmorPaddedBoots", "armor", 4, 0.24)
for k, sx in enumerate((-1, 1)):
    x = sx * 0.065
    part(r, f"Sole{k}", box(x - 0.045, x + 0.045, -0.13, 0.1, 0, 0.02), M_DARK)
    part(r, f"Foot{k}", box(x - 0.043, x + 0.043, -0.125, 0.095, 0.02, 0.085, (0.006, 0.006, 0.05, 0.0)), M_PAD)
    part(r, f"Shaft{k}", loft([ering(z, rx, ry, 12, x, 0.035) for z, rx, ry in ((0.06, 0.046, 0.056), (0.25, 0.042, 0.05), (0.4, 0.048, 0.055))]),
         M_PAD, smooth=True)
    part(r, f"Straps{k}", merge([xform(band_z(z, z + 0.014, rr, 12, 1.18), move=(x, 0.035, 0)) for z, rr in ((0.11, 0.0465), (0.2, 0.0445), (0.3, 0.0445))]),
         M_HIDED)
    part(r, f"Buckles{k}", merge([box(x + 0.035, x + 0.052, 0.025, 0.045, z - 0.002, z + 0.016) for z in (0.11, 0.2, 0.3)]), M_STEEL)

# --- composite
r = item("armor_composite_helmet", "ArmorCompositeHelmet", "armor", 5, 0.26)
part(r, "Seal", tube_z(0.0, 0.06, 0.068, 0.072, 12, 1.12), M_SUIT)
part(r, "Shell", dome((0, 0.005, 0.14), 0.115, 0.13, 0.12, math.radians(-50), 14, 5), M_PLATE, smooth=True)
part(r, "Visor", shell([(0.125, 0.1155, 0.131), (0.16, 0.1125, 0.1275)], (-math.pi + 0.6, -0.6), 8, 0.003), M_VISOR, solid=0.006)
part(r, "Jaw", prism([(-0.065, 0.035), (0.065, 0.035), (0.085, 0.118), (-0.085, 0.118)], -0.142, -0.095), M_PLATE)
part(r, "Vents", merge([box(-0.045, 0.045, -0.145, -0.14, z, z + 0.007) for z in (0.055, 0.072, 0.089)]), M_SUIT)
crest = [(0, -0.135 * math.cos(math.radians(t)) - 0.0, 0.14 + 0.128 * math.sin(math.radians(t))) for t in (40, 65, 90, 115, 140)]
part(r, "Crest", tube(crest, [0.006, 0.009, 0.01, 0.009, 0.006], 6), M_GUN)
part(r, "EarPods", merge([hcyl_bm(sx * 0.118, 0.0, 0.14, 0.03, 0.022, (1, 0), 10) for sx in (-1, 1)]), M_GUN)
part(r, "Antenna", rod((-0.125, 0.012, 0.16), (-0.13, 0.03, 0.27), 0.004, 0.0025, 5), M_DARK)

r = item("armor_composite_torso", "ArmorCompositeTorso", "armor", 5, 0.38)
part(r, "Suit", loft([ering(z, rx * 0.96, ry * 0.96, 16) for z, rx, ry in TORSO]), M_SUIT, smooth=True)
PLATES = [(0.24, 0.165, 0.11), (0.34, 0.172, 0.114), (0.43, 0.16, 0.106), (0.485, 0.11, 0.08)]
part(r, "Breastplate", shell(PLATES, FRONT, 10, 0.012), M_PLATE, solid=0.012)
part(r, "Backplate", shell(PLATES, BACK, 10, 0.012), M_PLATE, solid=0.012)
part(r, "BellyBand", merge([tinted(band_z(z, z + 0.04, 0.152, 16, 0.69), k % 2) for k, z in enumerate((0.05, 0.1, 0.15))]),
     [M_PLATE, M_GUN])
part(r, "Centre", box(-0.03, 0.03, -0.122, -0.085, 0.06, 0.22), M_PLATE)
part(r, "Slots", merge([box(-0.022, 0.022, -0.125, -0.12, z, z + 0.008) for z in (0.09, 0.13, 0.17)]), M_SUIT)
part(r, "Collar", band_z(0.48, 0.535, 0.075, 12, 0.85), M_GUN)

r = item("armor_composite_arms", "ArmorCompositeArms", "armor", 5, 0.44)
for k, y in enumerate((-0.07, 0.07)):
    arm = [(tube_z(0.0, 0.42, 0.036, 0.046, 10, 1.1), M_SUIT),                   # sleeve
           (tube_z(0.03, 0.19, 0.041, 0.047, 10, 1.1), M_PLATE),                 # forearm shell
           (band_z(0.0, 0.03, 0.04, 10, 1.1), M_GUN),                            # wrist cuff
           (tube_z(0.26, 0.34, 0.05, 0.052, 10, 1.1), M_PLATE),                  # bicep band
           (xform(dome((0, 0, 0.0), 0.062, 0.068, 0.055, 0.0, 12, 3), move=(0, 0, 0.375)), M_PLATE)]   # shoulder cap
    for j, (bm, m) in enumerate(limb(arm, y, 0.062)):
        part(r, f"Arm{k}_{j}", bm, m, smooth=(j == 4))

r = item("armor_composite_legs", "ArmorCompositeLegs", "armor", 5, 0.4)
for k, sx in enumerate((-1, 1)):
    x = sx * 0.095
    part(r, f"Leg{k}", loft([ering(z, rx * 0.95, ry * 0.95, 12, x) for z, rx, ry in LEG]), M_SUIT, smooth=True)
    part(r, f"Thigh{k}", xform(shell([(0.52, 0.066, 0.071), (0.65, 0.073, 0.078), (0.78, 0.08, 0.085)], FRONT, 8, 0.008),
                              move=(x, 0, 0)), M_PLATE, solid=0.01)
    part(r, f"Shin{k}", xform(shell([(0.06, 0.05, 0.055), (0.22, 0.054, 0.059), (0.38, 0.058, 0.063)], FRONT, 8, 0.008),
                             move=(x, 0, 0)), M_PLATE, solid=0.01)
    part(r, f"Knee{k}", ball((x, -0.055, 0.45), 0.05, 10, (1.0, 0.7, 1.0)), M_PLATE, smooth=True)
part(r, "Hips", loft([ering(z, 0.185, 0.1, 16) for z in (0.78, 0.92)]), M_SUIT, smooth=True)
part(r, "Belt", band_z(0.85, 0.9, 0.188, 16, 0.56), M_GUN)


def knuckles(r, k, y):
    part(r, f"Knuckles{k}", box(0.024, 0.046, y - 0.04, y + 0.04, 0.026, 0.036), M_PLATE)
    part(r, f"BackPlate{k}", box(-0.035, 0.018, y - 0.03, y + 0.03, 0.028, 0.035), M_PLATE)


r = item("armor_composite_gloves", "ArmorCompositeGloves", "armor", 5, 0.2)
for k, (y, side) in enumerate(((-0.06, -1), (0.06, 1))):
    glove(r, k, y, side, M_PLATE, M_SUIT, M_SUIT, knuckles)

r = item("armor_composite_boots", "ArmorCompositeBoots", "armor", 5, 0.24)
for k, sx in enumerate((-1, 1)):
    x = sx * 0.065
    part(r, f"Sole{k}", box(x - 0.046, x + 0.046, -0.13, 0.1, 0, 0.022), M_DARK)
    part(r, f"Foot{k}", box(x - 0.044, x + 0.044, -0.126, 0.096, 0.022, 0.09, (0.008, 0.008, 0.055, 0.0)), M_PLATE)
    part(r, f"Shaft{k}", loft([ering(z, rx, ry, 12, x, 0.035) for z, rx, ry in ((0.07, 0.047, 0.057), (0.28, 0.043, 0.052), (0.4, 0.048, 0.056))]),
         M_PLATE, smooth=True)
    part(r, f"Top{k}", xform(band_z(0.39, 0.415, 0.05, 12, 1.16), move=(x, 0.035, 0)), M_SUIT)
    part(r, f"Ridge{k}", box(x - 0.008, x + 0.008, -0.03, -0.014, 0.12, 0.38), M_GUN)

# ======================= 13.7 potions and tonics, 13.10 kits and containers (row 6) =======================
r = item("potion_stim", "Stim", "potion", 6, 0.08)
part(r, "Bodies", merge([zc(0.008, 0.1, 0.016, seg=10, x=x) for x in (-0.017, 0.017)]), M_WHITE, smooth=True)
part(r, "Ends", merge([zc(0.0, 0.008, 0.016, seg=10, x=x) for x in (-0.017, 0.017)]
                      + [zc(0.1, 0.11, 0.012, seg=10, x=x) for x in (-0.017, 0.017)]), M_DARK)
part(r, "Band", box(-0.035, 0.035, -0.0185, 0.0185, 0.04, 0.07), M_RED)
part(r, "Needles", merge([zc(0.11, 0.125, 0.003, seg=6, x=x) for x in (-0.017, 0.017)]), M_STEEL)

r = item("potion_regen", "RegenDraught", "potion", 6, 0.11)
part(r, "Gel", lathe([(0, 0), (0.035, 0), (0.05, 0.025), (0.05, 0.05), (0.03, 0.075), (0, 0.075)], 12), LIQ["blue"], smooth=True)
part(r, "Neck", lathe([(0, 0.075), (0.03, 0.075), (0.016, 0.09), (0.014, 0.1), (0, 0.1)], 12), M_GLASS, smooth=True)
part(r, "Stopper", merge([zc(0.097, 0.118, 0.016, seg=10), zring(0.105, 0.11, 0.015, 0.019, 10)]), M_STEEL)
part(r, "Bands", merge([zring(0.03, 0.04, 0.0495, 0.0535, 12), zring(0.003, 0.01, 0.036, 0.042, 12)]), M_STEEL)

for aid, name, liq in (("potion_fury", "FuryTonic", "red"), ("potion_haste", "HasteTonic", "yellow"),
                       ("potion_ironhide", "IronhideTonic", "grey"), ("potion_vigor", "VigorTonic", "green")):
    vial(item(aid, name, "potion", 6, 0.04), LIQ[liq])

r = item("kit_weapon_repair", "KitWeaponRepair", "kit", 6, 0.3)
part(r, "Case", box(-0.15, 0.15, -0.1, 0.1, 0, 0.09), M_CASE)
part(r, "Band", box(-0.152, 0.152, -0.102, 0.102, 0.04, 0.055), M_GREY)
part(r, "Handle", merge([box(-0.07, -0.055, -0.01, 0.01, 0.09, 0.12), box(0.055, 0.07, -0.01, 0.01, 0.09, 0.12),
                         box(-0.07, 0.07, -0.012, 0.012, 0.12, 0.132)]), M_DARK)
part(r, "Latches", merge([box(x - 0.012, x + 0.012, -0.106, -0.1, 0.06, 0.085) for x in (-0.09, 0.09)]), M_STEEL)

r = item("kit_armor_repair", "KitArmorRepair", "kit", 6, 0.32)
part(r, "Roll", xcyl(-0.15, 0.15, 0.055, z=0.055, seg=10), M_CANVAS)
part(r, "Flap", slab([(-0.15, -0.02), (0.15, -0.02), (0.15, -0.13), (-0.15, -0.13)], 0, 0.006), M_CANVAS)
part(r, "Patches", merge([tinted(box(x - 0.025, x + 0.025, -0.02, 0.02, 0.098, 0.113), k) for k, x in enumerate((-0.07, 0.0, 0.07))]),
     [M_PAD, M_HIDED, M_PADD])
part(r, "Straps", merge([xring(x - 0.01, x + 0.01, 0.054, 0.06, z=0.055, seg=10) for x in (-0.11, 0.11)]), M_HIDED)
part(r, "Rivets", merge([zc(0.006, 0.011, 0.006, seg=6, x=-0.12 + 0.04 * k, y=-0.09) for k in range(7)]
                        + [box(x - 0.008, x + 0.008, -0.064, -0.056, 0.04, 0.07) for x in (-0.11, 0.11)]), M_BRASS)

r = item("kit_vehicle_repair", "KitVehicleRepair", "kit", 6, 0.4)
part(r, "Case", box(-0.2, 0.2, -0.11, 0.11, 0, 0.12), M_KHAKI)
part(r, "Stripe", box(-0.202, 0.202, -0.112, 0.112, 0.085, 0.1), M_RED)
part(r, "Handle", merge([box(-0.09, -0.075, -0.01, 0.01, 0.12, 0.15), box(0.075, 0.09, -0.01, 0.01, 0.12, 0.15),
                         box(-0.09, 0.09, -0.012, 0.012, 0.15, 0.162)]), M_DARK)
part(r, "Canister", xcyl(-0.16, 0.08, 0.035, y=-0.148, z=0.047, seg=10), M_WHITE)
part(r, "Valve", merge([xcyl(0.08, 0.1, 0.012, y=-0.148, z=0.047, seg=8), box(0.098, 0.11, -0.16, -0.136, 0.04, 0.06)]), M_BRASS)
part(r, "Clips", merge([xring(x - 0.008, x + 0.008, 0.034, 0.039, y=-0.148, z=0.047, seg=10) for x in (-0.11, 0.03)]
                       + [box(x - 0.008, x + 0.008, -0.12, -0.108, 0.04, 0.06) for x in (-0.11, 0.03)]), M_DARK)

r = item("kit_structure_repair", "KitStructureRepair", "kit", 6, 0.6)
part(r, "Floor", box(-0.3, 0.3, -0.22, 0.22, 0, 0.03), M_KHAKI)
slats = []
for z in (0.05, 0.16):
    slats += [box(-0.3, 0.3, -0.22, -0.2, z, z + 0.08), box(-0.3, 0.3, 0.2, 0.22, z, z + 0.08),
              box(-0.3, -0.28, -0.2, 0.2, z, z + 0.08), box(0.28, 0.3, -0.2, 0.2, z, z + 0.08)]
part(r, "Slats", merge(slats), M_KHAKI)
part(r, "Posts", merge([box(x - 0.025, x + 0.025, y - 0.025, y + 0.025, 0, 0.26) for x in (-0.3, 0.3) for y in (-0.22, 0.22)]), M_GUN)
part(r, "Drums", merge([lathe([(0, 0.03), (0.075, 0.03), (0.075, 0.3), (0, 0.3)], 10, x=x, y=y) for x, y in ((-0.18, -0.09), (-0.02, -0.09), (-0.1, 0.09))]),
     M_DRUM)
part(r, "Lids", merge([zc(0.3, 0.31, 0.07, seg=10, x=x, y=y) for x, y in ((-0.18, -0.09), (-0.02, -0.09), (-0.1, 0.09))]), M_HAZ)
part(r, "Plates", merge([box(0.12 + 0.035 * k, 0.132 + 0.035 * k, -0.17, 0.17, 0.03, 0.34 - 0.02 * k) for k in range(3)]), M_STEEL)

r = item("crate", "Crate", "container", 6, 0.62)
part(r, "Body", box(-0.3, 0.3, -0.225, 0.225, 0.03, 0.42), M_GUN)
part(r, "Skids", merge([box(-0.3, 0.3, y - 0.025, y + 0.025, 0, 0.03) for y in (-0.17, 0.17)]), M_DARK)
part(r, "Ribs", merge([box(x - 0.015, x + 0.015, -0.237, -0.225, 0.03, 0.42) for x in (-0.22, 0.22)]
                      + [box(x - 0.015, x + 0.015, 0.225, 0.237, 0.03, 0.42) for x in (-0.22, -0.08, 0.08, 0.22)]
                      + [box(-0.312, -0.3, y - 0.015, y + 0.015, 0.03, 0.42) for y in (-0.1, 0.1)]
                      + [box(0.3, 0.312, y - 0.015, y + 0.015, 0.03, 0.42) for y in (-0.1, 0.1)]), M_STEEL)
part(r, "Rim", merge([box(-0.31, 0.31, -0.235, -0.205, 0.41, 0.44), box(-0.31, 0.31, 0.205, 0.235, 0.41, 0.44),
                      box(-0.31, -0.28, -0.205, 0.205, 0.41, 0.44), box(0.28, 0.31, -0.205, 0.205, 0.41, 0.44)]), M_STEEL)
part(r, "LabelFrame", box(-0.125, 0.125, -0.238, -0.225, 0.145, 0.305), M_DARK)
lab = part(r, "Label", box(-0.11, 0.11, -0.242, -0.236, 0.16, 0.29), M_LABEL)
uv = lab.data.uv_layers.new(name=UV0)                 # 0..1 across the plate's front face, (0, 0) at its lower left
for poly in lab.data.polygons:
    for li in poly.loop_indices:
        co = lab.data.vertices[lab.data.loops[li].vertex_index].co
        uv.data[li].uv = ((co.x + 0.11) / 0.22, (co.z - 0.16) / 0.13) if poly.normal.y < -0.5 else (0.0, 0.0)
r["item_label"] = M_LABEL.name

# ---- report ----
bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()
out = {}
for rr in [o for o in top.objects if o.get("asset_kind") == "item"]:
    tris, lo, hi = 0, Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for o in rr.children:
        if o.type == 'MESH':
            e = o.evaluated_get(dg); m = e.to_mesh(); m.calc_loop_triangles(); tris += len(m.loop_triangles)
            for v in m.vertices:
                p = o.matrix_local @ v.co
                lo = Vector(map(min, lo, p)); hi = Vector(map(max, hi, p))
            e.to_mesh_clear()
    out[rr["asset_id"]] = {"tris": tris, "size": [round(v, 3) for v in hi - lo], "z0": round(lo.z, 3)}
result = out
