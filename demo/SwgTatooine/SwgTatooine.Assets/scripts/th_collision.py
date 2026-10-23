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

result = STATS
