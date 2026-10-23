import bpy, math, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_creatures.py")).read(), globals())   # CREATURES, CREATURE_PACK
from mathutils import Vector

# Size check for the creatures of th_creatures.py: each one appended from its source .blend into the editing scene,
# scaled to its size, turned by its yaw and stood in a row on the swoop's left (+X; the swoop faces -Y), in its idle
# pose, next to the seated reference character, with its collision box drawn as a wireframe child. Reference only:
# nothing here is exported (no ASSET_ root); the creatures are published from their own files. It also recomputes
# each box from the idle clip and reports where th_creatures.py has drifted. Re-runnable: it replaces what it made.

PACK = os.path.join(TH_ROOT, "externals", CREATURE_PACK["dir"])
COL = "TatooineCreatureRef"
ROW_X, ROW_Y0, GAP = -27.8, 21.0, 0.8      # world: the row's line, its first creature's back edge, the gap between

col = bpy.data.collections.get(COL)
if col is None:
    col = bpy.data.collections.new(COL)
    bpy.context.scene.collection.children.link(col)
for o in list(col.objects):
    bpy.data.objects.remove(o, do_unlink=True)
for a in [a for a in bpy.data.actions if a.get("th_creature_ref")]:
    bpy.data.actions.remove(a)
for m in [m for m in bpy.data.meshes if m.users == 0 and m.name.startswith("COLREF_")]:
    bpy.data.meshes.remove(m)


def mesh_bounds(mesh, origin):
    """World AABB of the evaluated (posed) mesh's vertices, relative to origin."""
    e = mesh.evaluated_get(bpy.context.evaluated_depsgraph_get())
    pts = [e.matrix_world @ v.co - origin for v in e.data.vertices]
    return Vector([min(p[k] for p in pts) for k in range(3)]), Vector([max(p[k] for p in pts) for k in range(3)])


def measure(lo, hi, metric):
    return (hi - lo).z if metric == "height" else max((hi - lo).x, (hi - lo).y)


def to_gltf(lo, hi):
    """Blender AABB (relative to the origin, creature facing -Y) -> glTF [[min], [max]] (+Y up, +Z front)."""
    return [[lo.x, lo.z, -hi.y], [hi.x, hi.z, -lo.y]]


def box_object(name, arm, box):
    """A wireframe box child of arm at box (glTF frame, asset-local metres), unscaled by the rig."""
    (x0, y0, z0), (x1, y1, z1) = box
    lo, hi = Vector((x0, -z1, y0)), Vector((x1, -z0, y1))      # back to Blender: (x, -z, y)
    me = bpy.data.meshes.new(f"COLREF_{name}")
    me.from_pydata([(lo.x if i & 1 == 0 else hi.x, lo.y if i & 2 == 0 else hi.y, lo.z if i & 4 == 0 else hi.z)
                    for i in range(8)], [], [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4),
                                             (1, 5, 7, 3)])
    o = bpy.data.objects.new(f"COLREF_{name}", me)
    col.objects.link(o)
    o.display_type, o.hide_render = "WIRE", True
    o.location = arm.location                              # world, unparented: the rig's scale must not apply
    o.parent = arm
    o.matrix_parent_inverse = arm.matrix_world.inverted()
    return o


report, scales, y = {}, {}, ROW_Y0
scene, frame0 = bpy.context.scene, bpy.context.scene.frame_current
for cid, c in CREATURES.items():
    name = os.path.splitext(c["file"])[0]
    with bpy.data.libraries.load(os.path.join(PACK, c["file"]), link=False) as (src, dst):
        dst.objects = [n for n in src.objects]
        dst.actions = [n for n in src.actions]
    objs = [o for o in dst.objects if o is not None]
    acts = {a.name.split(".")[0].split("_", 1)[1]: a for a in dst.actions if a is not None}   # clip suffix -> action
    for a in acts.values():
        a["th_creature_ref"] = cid            # the next run removes them; Snake_Angry's come in as Snake_*.001
    arm = next(o for o in objs if o.type == "ARMATURE")
    mesh = next(o for o in objs if o.type == "MESH")
    for o in objs:
        col.objects.link(o)
    arm.name, mesh.name = f"{name}Armature", name
    arm.location = (0, 0, 0)
    arm.rotation_euler.z += math.radians(c["yaw"])
    # the source's own scale (Rat: 0.3955) stays: the size is measured with it and multiplied on top
    arm.data.pose_position = "REST"
    bpy.context.view_layer.update()
    lo, hi = mesh_bounds(mesh, Vector())
    metric, metres = c["size"]
    s = scales[metric] if metres is None else metres / measure(lo, hi, metric)
    scales[cid] = s
    arm.scale = arm.scale * s
    bpy.context.view_layer.update()
    lo, hi = mesh_bounds(mesh, Vector())
    # stand it on the ground (rest pose) and give it its slot along the row
    arm.location = (ROW_X - (lo.x + hi.x) / 2, y - lo.y, -lo.z)
    y += (hi - lo).y + GAP
    arm.data.pose_position = "POSE"

    # the box: every frame of the idle clip, relative to the rig's root
    idle = acts[c["roles"]["idle"]]
    arm.animation_data_create().action = idle
    blo, bhi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for f in range(int(idle.frame_range[0]), int(idle.frame_range[1]) + 1):
        scene.frame_set(f)
        flo, fhi = mesh_bounds(mesh, arm.matrix_world.translation)
        blo, bhi = Vector(map(min, blo, flo)), Vector(map(max, bhi, fhi))
    scene.frame_set(frame0)
    measured = to_gltf(blo, bhi)
    # rounded outwards to the centimetre (a vertex may stick out up to 0.5 mm rather than grow the box a centimetre)
    box = [[math.floor(v * 100 + 0.05) / 100 for v in measured[0]], [math.ceil(v * 100 - 0.05) / 100 for v in measured[1]]]
    box_object(name, arm, c["box"])

    missing = sorted(set(c["clips"]) - set(acts))
    report[cid] = {"scale": round(s, 5), "rest_dims_m": [round(v, 3) for v in (hi - lo)],
                   "idle_box": [[round(v, 4) for v in p] for p in measured],
                   "box_ok": all(abs(a - b) < 1e-9 for p, q in zip(box, c["box"]) for a, b in zip(p, q)),
                   "box_should_be": box, "missing_clips": missing}

result = report
