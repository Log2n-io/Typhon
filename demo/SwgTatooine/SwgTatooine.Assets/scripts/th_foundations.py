import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Ground contact for terrain that is not flat. Every piece that touches the ground continues FOUNDATION_D
# below z = 0: plinths and the plaza get a vertical skirt; entry ramps keep their slope all the way down,
# so wherever the terrain sits, the ramp meets it on the same walkable incline (ENTRY_SLOPE, 21.8 deg).
# th_collision.py mirrors all of this.

M_STONE = bpy.data.materials["TH_Sandstone"]


def reparent(ob, root):
    ob.parent = root
    ob.matrix_parent_inverse = root.matrix_world.inverted()


def entry_ramp(name, col, root, offset, x0, x1, y_top, rise, landing=0.4, rot_z=0.0):
    """Prism along X: slope from (y_top, rise) down to z = -FOUNDATION_D, then a flat landing of `landing`
    metres behind y_top so the ramp overlaps whatever it climbs onto. Built facing -Y (climbing +Y), then turned
    by rot_z about the asset origin."""
    old = bpy.data.objects.get(name)
    if old:
        bpy.data.objects.remove(old, do_unlink=True)
    y_far = y_top - (rise + FOUNDATION_D) / ENTRY_SLOPE
    pts = [(y_far, -FOUNDATION_D), (y_top + landing, -FOUNDATION_D), (y_top + landing, rise), (y_top, rise)]
    bm = prism(pts, x0, x1, 'YZ')
    if rot_z:
        xform(bm, rot_z=rot_z)
    ob = mk(name, bm, M_STONE, col, offset=offset, bevel=0.04)
    reparent(ob, root)
    unwrap_uv0(ob, root)
    return ob


out = {}


def house1():
    # the old 40-50 deg slope platform was fused into TH_Plinth; rebuild the plinth alone
    h1 = bpy.data.objects["TH_Plinth"]
    bm = box_bm(-5.0, 6.1, -4.8, 4.6, 0.0, 0.6, (0.25, 0.25, 0.25, 0.25))
    bm.to_mesh(h1.data); bm.free()
    add_skirt(h1.data)
    unwrap_uv0(h1, bpy.data.objects["ASSET_House1"])
    # ramp in front of the door (th_helpers.H1_RAMP: centred on the door, top edge on the plinth front)
    p = H1_RAMP
    r1 = entry_ramp("TH_EntryRamp", bpy.data.collections["TatooineHouse"], bpy.data.objects["ASSET_House1"],
                    (0, 0, p["dz"]), p["x0"], p["x1"], y_top=p["y_top"], rise=p["rise"], landing=p["landing"])
    out["house1"] = {"ramp": r1.name, "ground_reach_y": round(p["y_top"] - (p["rise"] + FOUNDATION_D) / ENTRY_SLOPE, 2)}


def house2():
    h2 = bpy.data.objects["TH2_Plinth"]
    add_skirt(h2.data)
    unwrap_uv0(h2, bpy.data.objects["ASSET_House2"])
    r2 = entry_ramp("TH2_EntryRamp", bpy.data.collections["TatooineHouse_Large"], bpy.data.objects["ASSET_House2"],
                    (-22.0, -5.0, 0.0), -0.2, 3.2, y_top=-7.0, rise=0.7)
    out["house2"] = {"ramp": r2.name, "ground_reach_y": round(-7.0 - 0.7 / ENTRY_SLOPE, 2)}


def shuttleport():
    root = bpy.data.objects["ASSET_Shuttleport"]
    pz = bpy.data.objects["SP_Plaza"]
    add_skirt(pz.data)
    unwrap_uv0(pz, root)
    # Gate ramps. The plaza top (0.3) is under the step height on flat ground, but wherever the terrain is lower
    # the edge becomes a ledge that needs a jump; a ramp at the gate (-Y, 6 m between the pylons), like the
    # houses'. th_helpers.SP_GATE_RAMP: the landing runs under the plaza, low enough to stay under its bevelled rim
    # (no z-fighting, no shadow acne; the exporter culls the buried landing top), and the slope emerges from the
    # plaza side.
    p = SP_GATE_RAMP
    col = bpy.data.collections["TatooineShuttleport"]
    ramps = {}
    for gate, rot in (("S", 0.0),):
        ramps[gate] = entry_ramp(f"SP_EntryRamp.{gate}", col, root, (32.0, 14.0, p["dz"]), -p["half_width"],
                                 p["half_width"], y_top=p["y_top"], rise=p["rise"], landing=p["landing"], rot_z=rot).name
    out["shuttleport"] = {"ramps": ramps,
                          "ground_reach_r": round(-p["y_top"] + (p["rise"] + FOUNDATION_D) / ENTRY_SLOPE, 2)}


# Sections run on a fresh rebuild; FOUNDATIONS_ONLY = {...} set before exec runs a subset.
ONLY = globals().get("FOUNDATIONS_ONLY")
for _name, _fn in (("house1", house1), ("house2", house2), ("shuttleport", shuttleport)):
    if ONLY is None or _name in ONLY:
        _fn()

out["slope_deg"] = round(math.degrees(math.atan(ENTRY_SLOPE)), 1)
out["foundation_d"] = FOUNDATION_D
result = out
