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

result = out
