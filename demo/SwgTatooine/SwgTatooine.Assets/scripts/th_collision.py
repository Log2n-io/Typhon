import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())
exec(open(os.path.join(TH_ROOT, "scripts", "th_colprims.py")).read(), globals())

# Exterior collision. Conventions (asset-local space, ground contact, 2 m band): see th_colprims.py.
col = get_col("TatooineCollision")
for o in list(col.objects):
    bpy.data.objects.remove(o, do_unlink=True)
for me in [m for m in bpy.data.meshes if m.name.startswith("COL_") and m.users == 0]:
    bpy.data.meshes.remove(me)

STATS = {}

# =============== House 1 (asset origin (0,0,0)) ===============
R = make_root("COL_House1", "tat_house_small", (0, 0, 0), (0, 15, 0))
box(R, "Plinth", -5.0, 6.1, -4.8, 4.6, -FOUNDATION_D, 0.6, floor=0.0)
# entry ramp = th_helpers.H1_RAMP (its landing lies inside the plinth, so no separate landing box); built with
# ramp() rather than entry_ramp() to carry the ramp's small vertical offset (dz)
_p = H1_RAMP
_run = (_p["rise"] + FOUNDATION_D) / ENTRY_SLOPE
ramp(R, "EntryRamp", ((_p["x0"] + _p["x1"]) / 2, _p["y_top"] - _run, -FOUNDATION_D + _p["dz"]), (0, 1), run=_run,
     rise=_p["rise"] + FOUNDATION_D, width=_p["x1"] - _p["x0"], extend=0.0)
box(R, "Body", -4.2, 2.0, -3.5, 3.8, 0.6, 3.6, floor=0.6)
box(R, "ArchSurround", -3.0, -1.0, -3.72, -3.4, 0.6, 3.2, floor=0.6)
ramp(R, "Steps", (-2.0, -4.45, 0.6), (0, 1), run=0.6, rise=0.26, width=1.8, t=0.2, extend=0.2)
box(R, "StepTop", -2.9, -1.1, -3.85, -3.72, 0.6, 0.86, floor=0.6)
for i, (x0, x1, y0, z1) in enumerate([(-4.5, -3.75, -3.95, 2.95), (-0.75, -0.05, -3.95, 2.65), (0.35, 1.35, -4.1, 2.75)]):
    box(R, f"Pilaster.{i:02d}", x0, x1, y0, -3.4, 0.6, z1, floor=0.6)
box(R, "Wing", 1.8, 5.6, -4.3, 4.0, 0.6, 3.35, floor=0.6)

# =============== House 2 (asset origin (-22,-5,0)) ===============
R = make_root("COL_House2", "tat_house_large", (-22, -5, 0), (0, 17, 0))
box(R, "Plinth", -9.0, 9.0, -7.0, 7.0, -FOUNDATION_D, 0.7, floor=0.0)
entry_ramp(R, "EntryRamp", 1.5, y_top=-7.0, rise=0.7, width=3.4)
box(R, "MainBody", -3.5, 7.5, -4.5, 4.5, 0.7, 5.2, floor=0.7)
box(R, "ArchSurround", 0.05, 2.95, -4.85, -4.3, 0.7, 4.35, floor=0.7)
ramp(R, "Steps", (1.5, -5.9, 0.7), (0, 1), run=0.8, rise=0.3, width=2.4, t=0.2, extend=0.2)
box(R, "StepTop", 0.3, 2.7, -5.1, -4.85, 0.7, 1.0, floor=0.7)
for i, (x0, x1) in enumerate([(-0.7, 0.15), (2.85, 3.7)]):
    box(R, f"Pilaster.{i:02d}", x0, x1, -5.15, -4.35, 0.7, 3.9, floor=0.7)
box(R, "Wing", -8.2, -3.3, -3.5, 4.0, 0.7, 3.6, floor=0.7)
cyl(R, "Tower", 7.5, -4.5, 0.7, 6.2, 2.3, floor=0.7)
box(R, "CourtWallFront.00", -8.5, -5.55, -6.6, -6.1, 0.7, 2.7, floor=0.7)
box(R, "CourtWallFront.01", -4.05, -1.2, -6.6, -6.1, 0.7, 2.7, floor=0.7)
box(R, "CourtGateLintel", -5.55, -4.05, -6.6, -6.1, 2.45, 2.7, floor=0.7)
box(R, "CourtWallSide", -8.5, -8.0, -6.6, -3.5, 0.7, 2.7, floor=0.7)

# =============== Shuttleport (asset origin (32,14,0)) ===============
R = make_root("COL_Shuttleport", "tat_shuttleport", (32, 14, 0), (0, 46, 0))
PLAZA = 0.3
cyl(R, "Plaza", 0, 0, -FOUNDATION_D, 0.3, 21.6, floor=0.0)
# gate entry ramp = th_helpers.SP_GATE_RAMP (th_foundations.shuttleport): ENTRY_SLOPE down to -FOUNDATION_D, so
# the gate is walkable on any terrain. Lowered by dz like the render ramps; the step left onto the Plaza cylinder
# (r 21.6, top 0.3) is ~0.2 m, under the motor's step height.
_p = SP_GATE_RAMP
_run = (_p["rise"] + FOUNDATION_D) / ENTRY_SLOPE
_low = -FOUNDATION_D + _p["dz"]
ramp(R, "EntryRamp.S", (0.0, _p["y_top"] - _run, _low), (0, 1), run=_run, rise=_p["rise"] + FOUNDATION_D,
     width=2 * _p["half_width"], extend=0.0)

# ring wall r 19.4..20.4 as tangent boxes, the -Y gate left open
gate = math.degrees(math.asin(3.0 / 19.9))
k = 0
for a_start, a_end in ((270 + gate, 630 - gate),):
    n = math.ceil((a_end - a_start) / 10.0)
    step = (a_end - a_start) / n
    for s in range(n):
        a = math.radians(a_start + step * (s + 0.5))
        half_len = 20.4 * math.sin(math.radians(step / 2))
        box(R, f"Wall.{k:02d}", -half_len, half_len, 19.4, 20.4, PLAZA, 3.0, floor=PLAZA, rot_z=a - math.pi / 2)
        k += 1

# buttresses: their outer face is a steep slope, from r 21.3 at the plaza to r 20.4 at 2.5 m
k = 0
for deg in range(0, 360, 20):
    if abs((deg - 270 + 180) % 360 - 180) < 20:
        continue
    a = math.radians(deg)
    out = Vector((math.cos(a), math.sin(a)))
    ramp(R, f"Buttress.{k:02d}", (21.3 * out.x, 21.3 * out.y, PLAZA), (-out.x, -out.y),
         run=0.9 * BAND / 2.2, rise=BAND, width=1.0, t=1.0, extend=0.0)
    k += 1
for i, (x, y) in enumerate([(-3.9, -19.9), (3.9, -19.9)]):
    box(R, f"GatePylon.{i:02d}", x - 0.9, x + 0.9, y - 0.9, y + 0.9, PLAZA, 4.6, floor=PLAZA)

cyl(R, "Pad", 0, 0, PLAZA, 1.26, 12.0, floor=PLAZA)
# stairs -> ramp through the step nosings (rise 0.225 / run 0.6)
ramp(R, "Stairs", (0.0, -15.6, PLAZA), (0, 1), run=2.56, rise=0.96, width=6.0)
box(R, "StairTop", -3.0, 3.0, -13.04, -11.0, PLAZA, 1.26, floor=PLAZA)
k = 0
for deg in range(0, 360, 22):
    if abs((deg - 270 + 180) % 360 - 180) < 22:
        continue
    a = math.radians(deg)
    capsule(R, f"LampPost.{k:02d}", 12.7 * math.cos(a), 12.7 * math.sin(a), PLAZA, 1.05, 0.18, floor=PLAZA)
    k += 1
box(R, "Office", -5.0, 3.0, 14.0, 18.6, PLAZA, 4.3, floor=PLAZA)
cyl(R, "Tower", 6.0, 15.5, PLAZA, 9.0, 2.0, floor=PLAZA)
capsule(R, "Droid", -4.2, -15.4, PLAZA, 1.6, 0.4, floor=PLAZA)
# the ticket terminal is its own asset (COL_TicketTerminal below), referenced by SP_TicketTerminal

# =============== Ticket terminal (asset origin = its base) ===============
inst = bpy.data.objects["SP_TicketTerminal"]
R = make_root("COL_TicketTerminal", "ticket_terminal", inst.matrix_world.translation, (0, 46, 0))
box(R, "Body", -0.4, 0.4, -0.25, 0.25, 0.0, 1.7, floor=0.0)

# =============== Terminals (th_terminals.py; asset origins = their base centres) ===============
TERMINALS = [
    ("COL_BazaarTerminal", "bazaar_terminal", "ASSET_BazaarTerminal",
     [("Body", -0.6, 0.6, -0.65, 0.45, 0.0, 1.5)]),                       # body + front pillar
    ("COL_CraftingStation", "crafting_station", "ASSET_CraftingStation",
     [("Body", -1.38, 1.35, -0.55, 0.55, 0.0, 1.5),
      ("Console", -0.6, 0.6, -0.8, -0.55, 0.0, 0.72)]),
    ("COL_MissionTerminal", "mission_terminal", "ASSET_MissionTerminal",
     [("Body", -0.33, 0.3, -0.26, 0.25, 0.0, 1.3)]),
]
for col_name, aid, root_name, boxes in TERMINALS:
    R = make_root(col_name, aid, bpy.data.objects[root_name].matrix_world.translation, (0, 6, 0))
    for name, x0, x1, y0, y1, z0, z1 in boxes:
        box(R, name, x0, x1, y0, y1, z0, z1, floor=0.0)

# =============== Shuttle (th_shuttle.py; asset origin = between its feet, on the pad) ===============
# Within the 2 m band only the legs, the belly and the low pods matter (the nacelles start above 2.2 m): one box for the
# hull down to the feet (nobody walks between the legs) and one per low pod.
R = make_root("COL_Shuttle", "shuttle", bpy.data.objects["ASSET_Shuttle"].matrix_world.translation, (0, 30, 0))
box(R, "Hull", -2.0, 2.0, -7.4, 7.2, 0.0, 4.0, floor=0.0)
box(R, "Pod.L", -3.0, -1.7, -1.6, 4.9, 0.0, 2.6, floor=0.0)
box(R, "Pod.R", 1.7, 3.0, -1.6, 4.9, 0.0, 2.6, floor=0.0)

# =============== Swoop (th_swoop.py; asset origin = the ground under it, hovering) ===============
# One box around the whole bike, forks included (mesh bounds x +-0.422, y -2.164..1.865, z 0.33..1.184).
R = make_root("COL_Swoop", "swoop", bpy.data.objects["ASSET_Swoop"].matrix_world.translation, (0, 4, 0))
box(R, "Body", -0.43, 0.43, -2.17, 1.87, 0.33, 1.19, floor=0.0)

# =============== Creature nest (th_creature_nest.py; asset origin = its centre on the ground) ===============
# One cylinder over the twig rim (foot r 2.25, crest r ~1.95, 0.9 m high, sunk 0.3 m); the branches sticking out
# are not solid.
R = make_root("COL_CreatureNest", "creature_nest", bpy.data.objects["ASSET_CreatureNest"].matrix_world.translation,
              (0, 7, 0))
cyl(R, "Body", 0, 0, -0.3, 0.9, 2.15, floor=0.0)

# =============== Soldier camp (th_camps.py; asset origin = its centre on the ground) ===============
# Tent, fire ring, log seats, the crate stacks, barrel and pennant pole; the spawns stand clear of all of them.
R = make_root("COL_SoldierCamp", "soldier_camp", bpy.data.objects["ASSET_SoldierCamp"].matrix_world.translation,
              (0, 5.5, 0))   # behind, short of the terminals
box(R, "Tent", -1.2, 1.2, 1.0, 3.0, -0.15, 1.8, floor=0.0)
cyl(R, "Fire", 0.0, -0.4, -0.15, 0.3, 0.55, floor=0.0)
box(R, "Seat.0", -0.6, 0.6, -1.59, -1.31, -0.15, 0.26, floor=0.0)
box(R, "Seat.1", 0.81, 1.09, -0.6, 0.5, -0.15, 0.26, floor=0.0)
box(R, "CratesL", -2.95, -2.05, -1.05, 0.7, -0.15, 1.15, floor=0.0)
box(R, "CrateR", 1.97, 2.93, -1.33, 0.13, -0.15, 0.58, floor=0.0)
cyl(R, "Barrel", 2.5, 1.0, -0.15, 0.9, 0.31, floor=0.0)
cyl(R, "Pole", 2.4, 2.4, -0.15, 2.7, 0.05, floor=0.0)

# =============== Ninja hideout (th_camps.py; asset origin = its centre on the ground) ===============
# The canopy's back half (lower than a 1.65 m body from y 1.0) as one box down to the ground with the back curtain;
# the front half stays walkable under it. Front poles, brazier, pots, rolled hides.
R = make_root("COL_NinjaHideout", "ninja_hideout", bpy.data.objects["ASSET_NinjaHideout"].matrix_world.translation,
              (0, 5.5, 0))   # behind, short of the terminals
box(R, "CanopyBack", -1.9, 1.9, 1.0, 2.6, -0.15, 1.7, floor=0.0)
for i, x in enumerate((-1.86, 1.86)):
    cyl(R, f"Pole.{i}", x, -0.26, -0.15, 2.3, 0.08, floor=0.0)
cyl(R, "Brazier", 0.0, -1.4, -0.15, 0.8, 0.35, floor=0.0)
box(R, "Pots", -2.8, -1.9, 1.3, 2.3, -0.15, 0.62, floor=0.0)
box(R, "Rolls", 1.8, 2.8, 1.25, 2.15, -0.15, 0.6, floor=0.0)

# =============== Town hall (th_townhall.py; asset origin = its centre on the ground, floor P = 0.9) ===============
# Plinth and entry ramp (FOUNDATION_D rules), every volume standing on the plinth up to the band, the door's arch
# surround (the facade a body stops at), the towers and the porch pillars; the porch roof is above the band.
R = make_root("COL_Townhall", "tat_townhall", bpy.data.objects["ASSET_Townhall"].matrix_world.translation, (0, 32, 0))
P = 0.9
box(R, "Plinth", -15.2, 15.2, -13.0, 9.6, -FOUNDATION_D, P, floor=0.0)
entry_ramp(R, "EntryRamp", 0.0, y_top=-13.0, rise=P, width=6.0)
box(R, "Hall", -7.4, 7.4, -6.4, 8.4, P, P + 8.4, floor=P)
box(R, "WingW", -14.0, -7.4, -6.4, 8.4, P, P + 4.0, floor=P)
box(R, "WingE", 7.4, 14.0, -6.4, 8.4, P, P + 4.0, floor=P)
box(R, "Vestibule", -3.4, 3.4, -10.4, -6.4, P, P + 5.4, floor=P)
box(R, "ArchSurround", -1.6, 1.6, -10.7, -10.4, P, P + 4.2, floor=P)
for i, sx in enumerate((-1, 1)):
    cyl(R, f"Tower.{i:02d}", sx * 5.1, -8.0, P, P + 11.5, 1.7, floor=P)
    box(R, f"PorchPillar.{i:02d}", *sorted((sx * 2.52, sx * 3.18)), -12.68, -12.02, P, P + 4.6, floor=P)

# =============== Factory (th_industry.py; asset origin = its centre on the ground, pad top 0.25) ===============
# Pad (stepped onto), the shed with its base course and eave, the bay frame and console (the stand point at y -2.1 is
# clear of it), the hopper / chute / bin, the fan housing, the power box and the pipes.
R = make_root("COL_Factory", "factory", bpy.data.objects["ASSET_Factory"].matrix_world.translation, (0, 7, 0))
P = 0.25
box(R, "Pad", -2.8, 2.8, -2.35, 2.35, -FOUNDATION_D, P, floor=0.0)
box(R, "Body", -2.5, 2.5, -1.3, 2.2, P, 3.12, floor=P)
box(R, "BayFrame", -1.15, 1.15, -1.38, -1.3, P, 2.55, floor=P)
box(R, "Console", -0.55, 0.55, -1.75, -1.38, P, 1.17, floor=P)
box(R, "Output", 1.4, 2.2, -2.2, -1.3, P, 2.25, floor=P)
box(R, "FanHousing", -2.85, -2.5, -0.62, 1.2, P, 2.45, floor=P)
box(R, "PowerBox", 2.5, 2.88, 0.4, 1.6, P, 1.7, floor=P)
box(R, "Pipes", 2.5, 2.67, -0.72, -0.08, P, 3.0, floor=P)

# =============== Resource extractor (th_industry.py; asset origin = its centre on the ground, pad top 0.3) ===============
# Pad, the derrick as one box over its feet (wellhead, casing and braces inside), the silo (legs, ore bin) as one
# cylinder, the feeder box, the conveyor's low end (higher up it clears a body) and its two supports, the generator with
# its exhaust, the fuel tank on its saddles.
R = make_root("COL_ResourceExtractor", "resource_extractor",
              bpy.data.objects["ASSET_ResourceExtractor"].matrix_world.translation, (0, 7, 0))
P = 0.3
box(R, "Pad", -3.35, 3.35, -2.8, 2.8, -FOUNDATION_D, P, floor=0.0)
box(R, "Derrick", -2.43, 0.43, -1.13, 1.73, P, 7.2, floor=P)
cyl(R, "Silo", 2.05, 1.45, P, 4.4, 1.0, floor=P)
box(R, "Feeder", 0.1, 0.9, -2.0, -1.2, P, 1.25, floor=P)
_f0, _f1 = Vector((0.5, -1.6, 1.3)), Vector((1.7, 0.9, 4.35))          # th_industry.py F0 / F1
_a = math.atan2(_f1.y - _f0.y, _f1.x - _f0.x)
box(R, "ConveyorLow", _f0.x - 0.1, _f0.x + 0.9, _f0.y - 0.27, _f0.y + 0.27, P, 2.3, floor=P, rot_z=_a,
    pivot=(_f0.x, _f0.y))
for i, t in enumerate((0.33, 0.66)):
    _p = _f0.lerp(_f1, t)
    box(R, f"ConveyorSupport.{i}", _p.x - 0.32, _p.x + 0.32, _p.y - 0.12, _p.y + 0.12, P, _p.z, floor=P)
box(R, "Generator", -2.95, -0.35, 1.8, 2.7, P, 3.72, floor=P)
box(R, "Tank", -3.1, -1.5, -2.47, -1.53, P, 1.32, floor=P)

result = STATS
