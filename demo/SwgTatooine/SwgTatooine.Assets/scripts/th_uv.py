import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# UV0: world-scale box projection in ASSET-LOCAL space, so every asset samples the shared tileable
# textures (th_texlib.py) at the same texel density (TILE_M per tile) and a building's faces line up
# across objects. Vertical faces get v = z, matching the vertical XZ bake plane of the texture library.
# The lightmap set (UV1, unique per asset, for the AO bake) is made at export time, after the bevel
# modifiers are applied: a lightmap unwrap of the unbevelled mesh would give the bevels zero area.

COLLECTIONS = ["TatooineHouse", "TatooineHouse_Large", "TatooineShuttleport", "TatooineTicketTerminal",
               "TatooineBazaarTerminal", "TatooineCraftingStation", "TatooineMissionTerminal",
               "TatooineShuttle"]

done = {}
for cname in COLLECTIONS:
    n = 0
    for ob in bpy.data.collections[cname].objects:
        if ob.type != 'MESH':
            continue
        root = asset_root(ob)
        if root is None:
            raise RuntimeError(f"{ob.name} has no asset root")
        unwrap_uv0(ob, root); n += 1
    done[cname] = n

result = done
