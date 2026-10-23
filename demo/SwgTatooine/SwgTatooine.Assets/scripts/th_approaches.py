import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Bot approach paths (th_helpers.make_approach; descriptor portals[].approach / children[].approach). Asset-local,
# feet height; a bot reaches the first point by open-ground navigation, then walks the points in order.
# Heights follow the collision: ENTRY_SLOPE ramps (0.4) down to the terrain at z 0, then landings and steps.
# Interior exits start on a nav node instead: th_interior_nav.py.

ob = bpy.data.objects
out = {}

# House doors end where the body rests against the facade (5 cm off it), 0.25 m past the portal's mid-plane.
# small house: ramp (th_helpers.H1_RAMP) meets z 0 at y -6.32 and its top (0.57) at y -4.90; steps -4.45 (0.6) to
# -3.85 (0.86); facade y -3.72, portal centre y -4.32. Collision footprint ends at y -8.90.
out["house1"] = make_approach(ob["ASSET_House1"], "APPROACH_House1_front", ob["PORTAL_House1_front"], [
    (-2.0, -9.4, 0.0), (-2.0, -6.32, 0.0), (-2.0, -4.90, 0.57), (-2.0, -4.45, 0.6), (-2.0, -4.07, 0.77)]).name

# large house: ramp meets z 0 at y -8.75, top (0.7) at -7.0; steps -5.9 (0.7) to -5.1 (1.0); facade y -4.85, portal
# centre y -5.45.
# Collision footprint ends at y -11.25.
out["house2"] = make_approach(ob["ASSET_House2"], "APPROACH_House2_front", ob["PORTAL_House2_front"], [
    (1.5, -11.75, 0.0), (1.5, -8.75, 0.0), (1.5, -7.0, 0.7), (1.5, -5.9, 0.7), (1.5, -5.2, 0.96)]).name

# shuttleport ticket terminal: straight up the gate ramp (meets z 0 at y -21.85, its top end 0.2 at -21.35) to the
# terminal's stand point, 0.9 m in front of it, on the ramp's top. Derived from the terminal's placement (moved by
# hand); th_export.py refuses it if the terminal moves somewhere this no longer fits. Collision footprint ends at
# y -24.59.
sp, tt = ob["ASSET_Shuttleport"], ob["SP_TicketTerminal"]
tx, ty, _ = sp.matrix_world.inverted() @ tt.matrix_world.translation
out["shuttleport"] = make_approach(sp, "APPROACH_Shuttleport_ticket", tt, [
    (tx, -25.2, 0.0), (tx, -21.85, 0.0), (tx, ty - 0.9, 0.2)]).name

result = out
