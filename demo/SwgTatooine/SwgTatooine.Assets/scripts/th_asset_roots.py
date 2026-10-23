import bpy

# One root empty per asset at its local origin. Render objects are parented to it (world pose kept),
# so the exporter reads every object in asset-local space as root.matrix_world.inverted() @ obj.matrix_world.
ASSETS = [
    # (root name, asset_id, collection, origin)
    ("ASSET_House1", "tat_house_small", "TatooineHouse", (0.0, 0.0, 0.0)),
    ("ASSET_House2", "tat_house_large", "TatooineHouse_Large", (-22.0, -5.0, 0.0)),
    ("ASSET_Shuttleport", "tat_shuttleport", "TatooineShuttleport", (32.0, 14.0, 0.0)),
]

out = {}
for rname, aid, cname, origin in ASSETS:
    col = bpy.data.collections[cname]
    root = bpy.data.objects.get(rname)
    if root is None:
        root = bpy.data.objects.new(rname, None)
        root.empty_display_type = 'ARROWS'; root.empty_display_size = 2.0
        col.objects.link(root)
    root.location = origin
    root["asset_id"] = aid
    root["asset_kind"] = "building"
    bpy.context.view_layer.update()
    n = 0
    for o in col.objects:
        if o is root or o.parent is not None:
            continue
        mw = o.matrix_world.copy()
        o.parent = root
        o.matrix_parent_inverse = root.matrix_world.inverted()
        o.matrix_world = mw
        n += 1
    out[aid] = n

result = out
