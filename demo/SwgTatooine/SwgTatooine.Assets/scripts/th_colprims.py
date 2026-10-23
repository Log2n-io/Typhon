# Collision primitive builders, shared by th_collision.py (exteriors) and th_interiors.py.
# Callers exec th_helpers.py first, then this file, then set the global `col` (target collection).

# Collision proxies as typed primitives (box, cylinder, capsule, sphere) in ASSET-LOCAL space.
# The exporter reads each child's col_* props + its pose relative to the COL_ root; the mesh is display only.
#
# Ground contact: anything touching the ground continues FOUNDATION_D below z = 0, as the render
# geometry does (th_foundations.py), so a building on uneven terrain has no gap under its colliders.
#
# Height band: characters walk on the ground or on walkable tops; nothing flies. A primitive is kept only
# where it can touch a character: from its base up to (floor + BAND), floor being the walkable surface it
# stands on. Roofs, domes, decks and lintels above the band are not emitted.
BAND = 2.0

M_COL = bpy.data.materials.get("COL_Proxy") or simple_mat("COL_Proxy")[0]
STATS = globals().get("STATS", {})


def make_root(name, asset_id, origin, display_offset):
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = 'PLAIN_AXES'; e.empty_display_size = 2.0
    e.location = Vector(origin) + Vector(display_offset)
    e["col_for"] = asset_id
    e["col_band_m"] = BAND
    col.objects.link(e)
    STATS[asset_id] = {"box": 0, "cylinder": 0, "capsule": 0, "sphere": 0}
    return e


def _mesh(shape, d):
    bm = bmesh.new()
    if shape == "box":
        bmesh.ops.create_cube(bm, size=2.0)
        bmesh.ops.scale(bm, verts=bm.verts, vec=d["half"])
    elif shape == "cylinder":
        bmesh.ops.create_cone(bm, cap_ends=True, segments=16, radius1=d["r"], radius2=d["r"], depth=2 * d["hh"])
    elif shape == "sphere":
        bmesh.ops.create_uvsphere(bm, u_segments=12, v_segments=6, radius=d["r"])
    elif shape == "capsule":
        bmesh.ops.create_uvsphere(bm, u_segments=12, v_segments=8, radius=d["r"])
        for v in bm.verts:
            v.co.z += d["hh"] if v.co.z > 1e-6 else (-d["hh"] if v.co.z < -1e-6 else 0.0)
    return bm


def prim(root, name, shape, loc, rot=(0.0, 0.0, 0.0), **d):
    full = f"{root.name}_{name}"
    me = bpy.data.meshes.new(full)
    bm = _mesh(shape, d); bm.to_mesh(me); bm.free()
    me.materials.append(M_COL)
    ob = bpy.data.objects.new(full, me)
    col.objects.link(ob)
    ob.parent = root
    ob.location = loc; ob.rotation_euler = rot
    ob.show_wire = True
    ob["col_shape"] = shape
    if shape == "box":
        ob["col_half"] = tuple(d["half"])
    else:
        ob["col_radius"] = d["r"]
    if shape in ("cylinder", "capsule"):
        ob["col_half_height"] = d["hh"]   # cylinder: half height; capsule: half height of the straight part
    STATS[root["col_for"]][shape] += 1
    return ob


def box(root, name, x0, x1, y0, y1, z0, z1, floor, rot_z=0.0, pivot=(0.0, 0.0)):
    """Axis box in local coords, clipped to the band; optionally rotated about Z around pivot."""
    top = min(z1, floor + BAND)
    if z0 >= top:
        return None
    c = Vector(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + top) / 2))
    if rot_z:
        c = Matrix.Rotation(rot_z, 3, 'Z') @ (c - Vector((*pivot, 0))) + Vector((*pivot, 0))
    return prim(root, name, "box", c, (0, 0, rot_z), half=((x1 - x0) / 2, (y1 - y0) / 2, (top - z0) / 2))


def ramp(root, name, low, direction, run, rise, width, t=0.3, extend=0.4):
    """Walkable incline: a box whose top face is the slope from `low` (centre of the bottom edge) going
    `run` metres horizontally along `direction` while climbing `rise`. It is extended below the low edge
    so it meets the floor without a lip, and buried by its thickness t."""
    d2 = Vector((direction[0], direction[1], 0.0)).normalized()
    L = math.hypot(run, rise)
    d = Vector((d2.x * run, d2.y * run, rise)) / L          # along the slope, uphill
    w = Vector((d2.y, -d2.x, 0.0))                          # across the slope (keeps (w, d, n) right-handed)
    n = w.cross(d)                                          # slope normal, pointing up
    a = Vector(low) - d * extend
    b = Vector(low) + d * L
    c = (a + b) / 2 - n * (t / 2)
    m = Matrix((w, d, n)).transposed()                      # local X=w, Y=d, Z=n
    return prim(root, name, "box", c, m.to_euler(), half=(width / 2, (L + extend) / 2, t / 2))


def entry_ramp(root, name, cx, y_top, rise, width):
    """th_foundations.entry_ramp: ENTRY_SLOPE incline climbing +Y to (y_top, rise), continued down to
    -FOUNDATION_D so it meets uneven terrain anywhere along it."""
    total = rise + FOUNDATION_D
    run = total / ENTRY_SLOPE
    return ramp(root, name, (cx, y_top - run, -FOUNDATION_D), (0, 1), run=run, rise=total, width=width, extend=0.0)


def cyl(root, name, cx, cy, z0, z1, r, floor):
    top = min(z1, floor + BAND)
    if z0 >= top:
        return None
    return prim(root, name, "cylinder", (cx, cy, (z0 + top) / 2), r=r, hh=(top - z0) / 2)


def capsule(root, name, cx, cy, z0, z1, r, floor):
    top = min(z1, floor + BAND)
    hh = max((top - z0) / 2 - r, 0.0)
    return prim(root, name, "capsule", (cx, cy, (z0 + top) / 2), r=r, hh=hh)
