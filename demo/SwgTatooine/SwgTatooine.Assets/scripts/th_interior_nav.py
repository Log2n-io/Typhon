import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Interior navigation (descriptor nav + the exit portal's approach): how a bot anywhere inside reaches the door.
# NAV_<Interior>.<NN> waypoints (th_helpers.make_nav_node): one per room or corner a straight walk cannot see past, one
# each side of every opening between rooms. Node .00 stands in front of the door; the exit approach starts there and
# ends 0.25 m past the exit portal's mid-plane (the body 5 cm off the inner door wall), like the exterior doors
# (th_approaches.py). Asset-local, floor at z 0. th_export.py derives the edges and the cells and refuses a cell no
# node serves. Re-runnable.

ob = bpy.data.objects
out = {}


def nodes(root_name, pts):
    root = ob[root_name]
    short = root_name[len("ASSET_"):]
    for o in [c for c in root.children if "nav_node" in c]:
        bpy.data.objects.remove(o, do_unlink=True)
    for i, (x, y) in enumerate(pts):
        make_nav_node(root, f"NAV_{short}.{i:02d}", (x, y, 0.0))
    return root, short


# small house: main room x -3.8..1.6, y -3.1..3.4 (table, two stools, cabinet, crates); opening to the wing x 1.6..2.0,
# y -0.5..1.0; wing x 2.0..5.2, y -3.9..3.6 (console, chest, bed). Door x -2.0, inner wall y -3.1.
root, short = nodes("ASSET_House1Interior", [
    (-2.0, -2.2),                                            # .00 door
    (-3.0, -0.8), (-3.0, 2.0), (-1.1, 2.2), (0.6, -1.8), (0.5, 1.9),   # main room, round the table
    (0.9, 0.25), (2.7, 0.25),                                # either side of the opening
    (3.0, -2.4), (3.2, 2.6), (4.6, -0.6)])                   # wing
out["small"] = make_approach(root, f"APPROACH_{short}_front", ob[f"PORTAL_{short}_front"],
                             [(-2.0, -2.2, 0.0), (-2.0, -2.75, 0.0)]).name

# large house: hall x -3.1..7.1, y -4.1..4.1 (table, benches, cabinets, crates, planter); opening to the wing
# x -3.5..-3.1, y 0..1.6; wing x -7.8..-3.5, y -3.1..3.6 (bed, wardrobe, chest); tower r 1.9 round (7.5, -4.5), open
# to the hall at its x 5.65..7.1, y -4.1..-2.65 corner (console, chair). Door x 1.5, inner wall y -4.1.
root, short = nodes("ASSET_House2Interior", [
    (1.5, -3.0),                                             # .00 door
    (-1.5, -1.5), (-1.0, 2.6), (2.0, 2.8), (4.5, -1.5), (4.5, 2.6),    # hall, round the table
    (-2.6, 0.8), (-4.1, 0.8),                                # either side of the wing opening
    (-5.5, -2.0), (-6.0, 2.4),                               # wing
    (6.3, -3.5), (7.5, -5.0), (7.0, -5.8)])                  # tower
out["large"] = make_approach(root, f"APPROACH_{short}_front", ob[f"PORTAL_{short}_front"],
                             [(1.5, -3.0, 0.0), (1.5, -3.75, 0.0)]).name

# town hall: vestibule x +-3, y -10..-6.4, arch to the hall x +-1.5 (wall y -6.4..-6.0); hall x +-7, y -6..8, columns
# x +-4.6 (y -3.5, -0.5, 2.5, 5.5), stage y 5.4..8 (council table, seats, lectern; walkable, 0.3 m), benches on the side
# walls; side-room arches in the hall side walls at y -2.7 and 4.5 (wall x +-7.0..7.4); archives x -13.6..-7.4,
# y -6..0.6; mayor's office y 1..8; clerk's office (counter x 9.5..10.15, y -5.05..-0.55) and lounge (round table) east.
# Door x 0, inner wall y -10.
root, short = nodes("ASSET_TownhallInterior", [
    (0.0, -8.6),                                                         # .00 door
    (0.0, -6.2),                                                         # .01 the vestibule arch
    (0.0, -4.6), (-5.8, -4.8), (5.8, -4.8), (0.0, -0.5), (0.0, 3.0),     # .02-.06 hall floor
    (-5.8, 1.0), (5.8, 1.0), (-5.8, 7.0), (5.8, 7.0),                    # .07-.10 side aisles, stage ends
    (-6.3, -2.7), (-8.2, -2.7), (-6.3, 4.5), (-8.2, 4.5),                # .11-.14 west arches
    (6.3, -2.7), (8.2, -2.7), (6.3, 4.5), (8.2, 4.5),                    # .15-.18 east arches
    (-8.7, -4.7), (-12.2, -4.6), (-12.2, -1.0), (-8.7, -0.4),            # .19-.22 archives
    (-8.6, 2.0), (-12.3, 1.7), (-12.55, 4.85), (-9.0, 6.8), (-12.55, 6.8),   # .23-.27 mayor's office (west of the desk)
    (8.6, -4.6), (8.6, 0.0), (10.6, 0.0), (10.6, -5.6), (12.0, -4.0), (12.0, -1.0),   # .28-.33 clerk's office
    (8.4, 2.0), (8.6, 6.5), (12.2, 6.6), (12.2, 1.5),                    # .34-.37 lounge
    (-3.6, 6.7), (3.6, 6.7),                                             # .38-.39 beside the council table
    (-11.6, 7.6), (-9.7, 7.5), (-10.7, 4.85),                             # .40-.42 behind the mayor, before the desk
    (9.0, 7.15), (10.6, 6.3), (12.2, 4.4), (10.6, 2.5),                  # .43-.46 round the lounge table
    (1.9, -6.95), (1.9, -9.6),                                           # .47-.48 off either end of the vestibule bench
    (-3.45, 7.6), (3.45, 7.6)])                                          # .49-.50 behind the council table's ends
out["townhall"] = make_approach(root, f"APPROACH_{short}_front", ob[f"PORTAL_{short}_front"],
                                [(0.0, -8.6, 0.0), (0.0, -9.65, 0.0)]).name

result = out
