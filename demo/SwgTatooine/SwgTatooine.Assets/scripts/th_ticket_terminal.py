import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Asset source: modelled at its local origin (base centre, screen facing -Y).
# Placed in other assets through a collection-instance empty carrying asset_ref.
SRC = get_col("TatooineTicketTerminal")
M_METAL = bpy.data.materials['TH_Metal']; M_SCREEN = bpy.data.materials['TH_ScreenBlue']

root = bpy.data.objects.new("ASSET_TicketTerminal", None)
root.empty_display_type = 'ARROWS'; root.empty_display_size = 0.5
SRC.objects.link(root)
root["asset_id"] = "ticket_terminal"
root["asset_kind"] = "interactable"

body = mk("TT_Body", box_bm(-0.4, 0.4, -0.25, 0.25, 0.0, 1.7, (0.05, 0.05, 0.1, 0.0)), M_METAL, SRC, bevel=0.03)
screen = mk("TT_Screen", box_bm(-0.25, 0.25, -0.23, -0.15, 0.9, 1.45), M_SCREEN, SRC, bevel=0)
for o in (body, screen):
    o.parent = root

# Where the player stands to use it.
it = bpy.data.objects.new("TT_Interact", None)
it.empty_display_type = 'ARROWS'; it.empty_display_size = 0.6
it.location = (0.0, -0.9, 0.0)                 # local +Y (forward, like every marker) faces the terminal
SRC.objects.link(it); it.parent = root
it["interact_kind"] = "shuttle_ticket"
it["interact_radius"] = 1.5

# The source collection is not rendered in place; only its instances are.
lc = bpy.context.view_layer.layer_collection.children.get(SRC.name)
if lc:
    lc.exclude = True

# Placement in the shuttleport (C = (32, 14, 0)): in the gate, right of centre, just past the top of the ramp, screen
# to the street, base centre at local (2.1434, -20.4714, 0.3), placed by hand (was further inside, at local
# (4.5, -15.55, 0.3)).
port = bpy.data.collections["TatooineShuttleport"]
for n in ("SP_TicketTerminal", "SP_TicketScreen"):
    old = bpy.data.objects.get(n)
    if old:
        bpy.data.objects.remove(old, do_unlink=True)
    me = bpy.data.meshes.get(n)
    if me and me.users == 0:
        bpy.data.meshes.remove(me)
inst = bpy.data.objects.new("SP_TicketTerminal", None)
inst.instance_type = 'COLLECTION'; inst.instance_collection = SRC
inst.location = (32.0 + 2.1434, 14.0 - 20.4714, 0.3)
inst["asset_ref"] = "ticket_terminal"
port.objects.link(inst)

result = {"source": [o.name for o in SRC.objects], "instance": inst.name, "at": list(inst.location)}
