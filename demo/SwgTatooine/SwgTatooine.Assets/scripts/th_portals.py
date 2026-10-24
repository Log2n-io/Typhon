import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Exterior doors as portals into the interior assets (th_interiors.py). Conventions: th_helpers.make_portal.
# Every exterior door so far faces -Y: the volume sits in FRONT of the facade, outside the collision proxies,
# PORTAL_SETBACK off it, so a character stopped by the facade is inside it and well past its mid-plane (the server's
# crossing test); its +Y points into the building.


def door(asset_root_name, door_id, cx, y_face, z0, z1, width, spawn_xy, spawn_z):
    root = bpy.data.objects[asset_root_name]
    aid = root["asset_id"]
    short = asset_root_name[len("ASSET_"):]
    hx, hy, hz = width / 2, PORTAL_DEPTH / 2, (z1 - z0) / 2
    spawn = make_spawn(root, f"SPAWN_{short}_{door_id}", door_id, (spawn_xy[0], spawn_xy[1], spawn_z), math.pi)
    portal = make_portal(root, f"PORTAL_{short}_{door_id}", door_id, (cx, y_face - PORTAL_SETBACK - hy, z0 + hz), (hx, hy, hz), 0.0,
                         target=f"{aid}_interior", target_spawn="entry", exit_spawn=spawn.name)
    return portal.name, spawn.name


out = []
# small house: door x -2.6..-1.4, arch/step collision face y = -3.72, step top z 0.86, door top 2.8
out.append(door("ASSET_House1", "front", cx=-2.0, y_face=-3.72, z0=0.5, z1=2.8, width=1.4,
                spawn_xy=(-2.0, -5.4), spawn_z=0.6))
# large house: door x 0.65..2.35 (local), arch/step face y = -4.85, step top 1.0, door top 3.75
out.append(door("ASSET_House2", "front", cx=1.5, y_face=-4.85, z0=0.7, z1=3.75, width=1.9,
                spawn_xy=(1.5, -6.3), spawn_z=0.7))
# town hall: door x -1.2..1.2, arch surround face y = -10.7, plinth top 0.9, door top 3.8 above it; porch pillars at
# x +-2.52..3.18, the plinth front at y -13.0
out.append(door("ASSET_Townhall", "front", cx=0.0, y_face=-10.7, z0=0.9, z1=4.7, width=2.5,
                spawn_xy=(0.0, -12.3), spawn_z=0.9))
# the shuttleport office door is cosmetic: no portal

result = out
