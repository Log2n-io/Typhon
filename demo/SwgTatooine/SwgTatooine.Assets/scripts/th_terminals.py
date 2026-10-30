import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_helpers.py")).read(), globals())

# Interactable terminals, after the SWG ones: bazaar terminal (~1.5 m), crafting station (~1.5 m), mission terminal
# (~1.3 m). Low poly: boxes with a single small chamfer. Flat panel materials (white for the bazaar, blue-grey gunmetal
# for the others; TH_Metal is the rusty vaporator texture, wrong for these) plus TH_Duracrete from the shared library,
# and flat emissive materials for screens and holo panels (no texture, no AO: the exporter skips AO on emissive).
# Emission stays around 1.2-1.5: higher, colour x strength passes 1 and tone mapping washes the screens to pastel.
# Each asset is modelled at its local origin (base centre on the ground, front facing -Y like every asset) under its
# own ASSET_ root, placed in a free row of the editing scene; an interaction empty stands where the player uses it
# (local +Y faces the terminal); the bazaar and mission terminals are also usable from any side (interact_reach_m). Collision is in th_collision.py (one or two boxes each). Re-runnable.

M_DARK = bpy.data.materials["TH_Duracrete"]


def panel(name, rgb, metallic, roughness):
    m, nt, b = simple_mat(name)
    b.inputs['Base Color'].default_value = (*rgb, 1)
    b.inputs['Metallic'].default_value = metallic
    b.inputs['Roughness'].default_value = roughness
    return m


M_WHITE = panel("TH_PanelWhite", (0.72, 0.73, 0.74), 0.2, 0.45)
M_GUN = panel("TH_PanelGunmetal", (0.22, 0.25, 0.29), 0.55, 0.5)
M_RED = panel("TH_StripRed", (0.32, 0.06, 0.04), 0.1, 0.6)


def emissive(name, rgb, strength):
    m, nt, b = simple_mat(name)
    b.inputs['Base Color'].default_value = (*rgb, 1)
    b.inputs['Emission Color'].default_value = (*rgb, 1)
    b.inputs['Emission Strength'].default_value = strength
    b.inputs['Roughness'].default_value = 0.4
    return m


M_MAGENTA = emissive("TH_ScreenMagenta", (1.0, 0.12, 0.8), 1.4)
M_HOLO = emissive("TH_HoloCyan", (0.45, 0.85, 1.0), 1.1)
M_GREEN = emissive("TH_ScreenGreen", (0.55, 1.0, 0.12), 1.3)
M_AMBER = emissive("TH_ScreenAmber", (1.0, 0.45, 0.05), 1.5)

BEVEL = 0.012                 # chamfer for small props (the 6 cm default is building-sized)
ROW_Y = 34.0                  # free row of the editing scene, away from the buildings


def asset(col_name, root_name, asset_id, x):
    col = get_col(col_name)
    root = bpy.data.objects.new(root_name, None)
    root.empty_display_type = 'ARROWS'; root.empty_display_size = 0.5
    root.location = (x, ROW_Y, 0.0)
    col.objects.link(root)
    root["asset_id"] = asset_id
    root["asset_kind"] = "interactable"
    return col, root


def part(col, root, name, bm, mat, bevel=BEVEL, rot_x=0.0, rot_z=0.0, pivot=(0, 0, 0)):
    if rot_x or rot_z:
        xform(bm, rot_x=rot_x, rot_z=rot_z, pivot=pivot)
    ob = mk(name, bm, mat, col, bevel=bevel)
    ob.parent = root                          # modelled in local coordinates: no parent inverse
    unwrap_uv0(ob, root)
    return ob


def interact(col, root, name, kind, y, radius, all_round=False):
    """The stand point; all_round: also usable from any side (interact_reach_m about the terminal's centre). Call it
    after the parts: the reach is measured on them."""
    it = bpy.data.objects.new(name, None)
    it.empty_display_type = 'ARROWS'; it.empty_display_size = 0.5
    it.location = (0.0, y, 0.0)
    col.objects.link(it); it.parent = root
    it["interact_kind"] = kind
    it["interact_radius"] = radius
    if all_round:
        bpy.context.view_layer.update()
        set_interact_reach(it, root)
    return it


out = {}

# ---------------- bazaar terminal: tapered body, front pillar screen, tilted top screen, holo fins ----------------
col, root = asset("TatooineBazaarTerminal", "ASSET_BazaarTerminal", "bazaar_terminal", -12.0)
part(col, root, "BT_Base", box_bm(-0.6, 0.6, -0.45, 0.45, 0.0, 0.16, (0.02, 0.02, 0.02, 0.02)), M_DARK)
part(col, root, "BT_Body", box_bm(-0.58, 0.58, -0.42, 0.42, 0.16, 0.92, (0.1, 0.1, 0.08, 0.08)), M_WHITE)
part(col, root, "BT_Cap", box_bm(-0.48, 0.48, -0.34, 0.34, 0.92, 1.0, (0.04, 0.04, 0.04, 0.04)), M_WHITE)
part(col, root, "BT_Neck", box_bm(-0.08, 0.08, -0.06, 0.1, 1.0, 1.12), M_DARK)
tilt = math.radians(-12)                   # leans back: about +X, a negative angle takes the top towards +Y
part(col, root, "BT_TopFrame", box_bm(-0.5, 0.5, -0.05, 0.05, 1.05, 1.5), M_WHITE, rot_x=tilt, pivot=(0, 0, 1.05))
part(col, root, "BT_TopScreen", box_bm(-0.44, 0.44, -0.062, -0.05, 1.1, 1.45), M_MAGENTA, bevel=0, rot_x=tilt,
     pivot=(0, 0, 1.05))
part(col, root, "BT_Pillar", box_bm(-0.17, 0.17, -0.62, -0.4, 0.0, 1.05, (0, 0, 0.02, 0)), M_WHITE)
part(col, root, "BT_PillarScreen", box_bm(-0.13, 0.13, -0.632, -0.62, 0.45, 1.0), M_MAGENTA, bevel=0)
for i, (side, z) in enumerate([(-1, 0.6), (1, 0.6), (-1, 0.8), (1, 0.8)]):
    cx = side * 0.33
    part(col, root, f"BT_Holo.{i}", box_bm(cx - 0.13, cx + 0.13, -0.475, -0.46, z, z + 0.07), M_HOLO, bevel=0,
         rot_z=side * math.radians(15), pivot=(cx, -0.47, 0))
interact(col, root, "BT_Interact", "bazaar", -1.1, 1.5, all_round=True)
out["bazaar_terminal"] = {"root": root.name, "parts": len(root.children)}

# ---------------- crafting station: wide machine, side cabinet, front console, stacked tiers, canisters ----------------
col, root = asset("TatooineCraftingStation", "ASSET_CraftingStation", "crafting_station", -7.0)
part(col, root, "CS_Base", box_bm(-1.35, 1.35, -0.55, 0.55, 0.0, 0.14, (0.02, 0.02, 0.02, 0.02)), M_DARK)
part(col, root, "CS_Body", box_bm(-1.2, 1.3, -0.45, 0.5, 0.14, 0.95), M_GUN)
part(col, root, "CS_Grill", box_bm(-1.2, 1.3, -0.47, -0.45, 0.16, 0.3), M_DARK, bevel=0)
part(col, root, "CS_LeftCab", box_bm(-1.38, -0.95, -0.5, 0.35, 0.14, 0.82, (0, 0, 0.12, 0)), M_GUN)
part(col, root, "CS_LeftScreen", box_bm(-1.33, -1.0, -0.512, -0.5, 0.48, 0.74), M_GREEN, bevel=0)
part(col, root, "CS_Console", box_bm(-0.6, 0.6, -0.78, -0.45, 0.14, 0.72, (0, 0, 0.15, 0)), M_GUN)
part(col, root, "CS_ConsoleScreen.L", box_bm(-0.5, -0.1, -0.792, -0.78, 0.48, 0.64), M_GREEN, bevel=0)
part(col, root, "CS_ConsoleScreen.R", box_bm(0.14, 0.4, -0.792, -0.78, 0.48, 0.64), M_AMBER, bevel=0)
part(col, root, "CS_Tier1", box_bm(-0.62, 0.62, -0.42, 0.42, 0.95, 1.04), M_GUN)
part(col, root, "CS_TierPost", box_bm(-0.45, 0.45, -0.3, 0.3, 1.04, 1.12), M_DARK)
part(col, root, "CS_Tier2", box_bm(-0.58, 0.58, -0.4, 0.4, 1.12, 1.2), M_GUN)
part(col, root, "CS_Hood", box_bm(-0.3, 0.3, -0.22, 0.22, 1.2, 1.5, (0.08, 0.08, 0.06, 0.06)), M_GUN)
part(col, root, "CS_RightBlock", box_bm(0.62, 1.28, -0.4, 0.45, 0.95, 1.12), M_GUN)
part(col, root, "CS_CanisterBase", box_bm(-1.18, -0.72, -0.25, 0.2, 0.95, 1.0), M_DARK)
for i in range(3):
    x0 = -1.15 + 0.14 * i
    part(col, root, f"CS_Canister.{i}", box_bm(x0, x0 + 0.11, -0.12, 0.08, 1.0, 1.22), M_HOLO, bevel=0)
part(col, root, "CS_Pipe", hcyl_bm(0.95, -0.48, 0.62, 0.035, 0.6, (1, 0)), M_GUN, bevel=0)
interact(col, root, "CS_Interact", "crafting", -1.4, 1.8)
out["crafting_station"] = {"root": root.name, "parts": len(root.children)}

# ---------------- mission terminal: base plate, split legs, central column, screen head, keypad ----------------
col, root = asset("TatooineMissionTerminal", "ASSET_MissionTerminal", "mission_terminal", -2.0)
part(col, root, "MT_Base", box_bm(-0.3, 0.3, -0.25, 0.25, 0.0, 0.07, (0.02, 0.02, 0.02, 0.02)), M_GUN)
part(col, root, "MT_Pedal", box_bm(-0.1, 0.1, -0.22, -0.02, 0.07, 0.1), M_DARK)
part(col, root, "MT_LegL", box_bm(-0.26, -0.1, -0.12, 0.12, 0.07, 0.55, (0, 0.03, 0, 0)), M_GUN)
part(col, root, "MT_LegR", box_bm(0.1, 0.26, -0.12, 0.12, 0.07, 0.55, (0.03, 0, 0, 0)), M_GUN)
part(col, root, "MT_Column", box_bm(-0.06, 0.06, -0.04, 0.06, 0.07, 0.86), M_GUN)
part(col, root, "MT_Strip", box_bm(-0.035, 0.035, -0.052, -0.04, 0.2, 0.8), M_RED, bevel=0)
part(col, root, "MT_Head", box_bm(-0.26, 0.26, -0.12, 0.1, 0.86, 1.3), M_GUN)
part(col, root, "MT_Screen", box_bm(-0.21, 0.21, -0.132, -0.12, 0.95, 1.25), M_AMBER, bevel=0)
part(col, root, "MT_Keypad", box_bm(-0.17, 0.17, -0.24, -0.1, 0.8, 0.84), M_GUN, rot_x=math.radians(15),
     pivot=(0, -0.1, 0.82))        # front edge lower, facing the user
part(col, root, "MT_SidePanel", box_bm(-0.33, -0.26, -0.08, 0.06, 0.78, 1.3), M_GUN)
part(col, root, "MT_SideLights", box_bm(-0.315, -0.275, -0.092, -0.08, 1.05, 1.22), M_GREEN, bevel=0)
interact(col, root, "MT_Interact", "mission", -0.65, 1.2, all_round=True)
out["mission_terminal"] = {"root": root.name, "parts": len(root.children)}

result = out
