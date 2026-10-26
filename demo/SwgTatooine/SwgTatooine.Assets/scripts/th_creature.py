"""Publish the creatures of th_creatures.py into build/assets (kind "creature", ASSET_FORMAT.md 5.8). Headless Blender:
    blender -b --factory-startup --python scripts/th_creature.py -- [creature_id ...]     (default: every creature)

The sources are Blender 2.79 files, not glTF, so unlike th_character.py this one needs Blender. Per creature:
  1. its source .blend appended into an empty scene: one armature, one skinned mesh, one action per clip
  2. the scale and yaw of its th_creatures.py entry put on the armature (the size measured on the rest pose after the
     yaw, exactly as th_creature_ref.py does), the actions renamed to their published clip names, the flat colour
     materials rebuilt as plain Principled base colours
  3. checks: the clip set is the entry's; the collision box is the idle clip's bounds rounded outwards to the cm, as in
     the entry (th_creature_ref.py recomputes and reports it); the loop clips play in place
  4. Blender's glTF exporter (every action a clip), then gltfpack (meshoptimizer 1.3) as for characters: meshopt
     compression, quantization, animations resampled; named nodes and materials kept (-kn -km)
  5. descriptor, kind "creature": one box collider, forward "+Z", clips with durations and loop flags, roles, flies
  6. published like every asset: <id>-<rev> GLB + descriptor at the revision of revisions.json, catalogue entry merged.
     Republishing a revision with different bytes is refused: bump it first
No LODs: the creatures are 1.6k to 4.9k triangles.
"""
import bpy, json, math, os, shutil, struct, subprocess, sys, tempfile
from mathutils import Vector

TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_publish.py")).read(), globals())
exec(open(os.path.join(TH_ROOT, "scripts", "th_creatures.py")).read(), globals())   # CREATURES, CREATURE_PACK

GLTFPACK = tool("gltfpack")
PACK = os.path.join(ROOT_DIR, "externals", CREATURE_PACK["dir"])
IN_PLACE_M = 0.05             # a loop clip whose root bone travels further than this horizontally has root motion
DRY = os.environ.get("TH_DRY")  # a directory: write the GLBs and descriptors there instead of publishing


def load(cid):
    """An empty scene holding the creature's source: (armature, mesh, {clip suffix: action})."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    c = CREATURES[cid]
    with bpy.data.libraries.load(os.path.join(PACK, c["file"]), link=False) as (src, dst):
        dst.objects = list(src.objects)
        dst.actions = list(src.actions)
    objs = [o for o in dst.objects if o is not None]
    for o in objs:
        bpy.context.scene.collection.objects.link(o)
    arm = next(o for o in objs if o.type == "ARMATURE")
    mesh = next(o for o in objs if o.type == "MESH")
    if len(objs) != 2:
        raise ValueError(f"{cid}: expected one armature and one mesh, found {[o.name for o in objs]}")
    acts = {a.name.split("_", 1)[1]: a for a in dst.actions if a is not None}
    arm.location = (0, 0, 0)
    arm.rotation_euler.z += math.radians(c["yaw"])
    return arm, mesh, acts


def vertex_bounds(mesh, origin=Vector()):
    bpy.context.view_layer.update()
    e = mesh.evaluated_get(bpy.context.evaluated_depsgraph_get())
    pts = [e.matrix_world @ v.co - origin for v in e.data.vertices]
    return Vector([min(p[k] for p in pts) for k in range(3)]), Vector([max(p[k] for p in pts) for k in range(3)])


def to_gltf(lo, hi):
    """Blender AABB (creature facing -Y) -> glTF [[min], [max]] (+Y up, +Z front)."""
    return [[lo.x, lo.z, -hi.y], [hi.x, hi.z, -lo.y]]


def scale_of(cid, cache={}):
    """The publish scale: the entry's size over the rest pose's measure (after the yaw, with the source's transforms)."""
    metric, metres = CREATURES[cid]["size"]
    if metres is None:
        return scale_of(metric)
    if cid not in cache:
        arm, mesh, _ = load(cid)
        arm.data.pose_position = "REST"
        lo, hi = vertex_bounds(mesh)
        cache[cid] = metres / ((hi - lo).z if metric == "height" else max((hi - lo).x, (hi - lo).y))
    return cache[cid]


def principled_colour(m):
    if m.node_tree:
        b = next((n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if b:
            return tuple(b.inputs["Base Color"].default_value)[:3]
    return tuple(m.diffuse_color)[:3]


def flat_material(m):
    rgb = principled_colour(m)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    b = nt.nodes.new("ShaderNodeBsdfPrincipled")
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Roughness"].default_value = 0.9
    b.inputs["Metallic"].default_value = 0.0
    nt.links.new(b.outputs["BSDF"], out.inputs["Surface"])


def root_travel(arm, action):
    """Largest horizontal distance the root bone's head moves from its first frame over the clip (metres)."""
    arm.animation_data.action = action
    roots = [b for b in arm.pose.bones if b.parent is None]
    sc = bpy.context.scene
    start, travel = None, 0.0
    for f in range(int(action.frame_range[0]), int(action.frame_range[1]) + 1):
        sc.frame_set(f)
        p = [arm.matrix_world @ b.head for b in roots]
        start = start or p
        travel = max(travel, max(math.hypot(a.x - s.x, a.y - s.y) for a, s in zip(p, start)))
    return travel


def glb_json(b):
    return json.loads(b[20:20 + struct.unpack("<I", b[12:16])[0]])


def name_body(glb):
    """gltfpack drops the name of the skinned mesh node: name it "Body" as on characters (JSON chunk only)."""
    g = glb_json(glb)
    nodes = [n for n in g["nodes"] if "mesh" in n]
    if len(nodes) != 1:
        raise ValueError(f"expected one mesh node after gltfpack, found {len(nodes)}")
    nodes[0]["name"] = "Body"
    chunk = json.dumps(g, separators=(",", ":")).encode()
    chunk += b" " * (-len(chunk) % 4)
    rest = glb[20 + struct.unpack("<I", glb[12:16])[0]:]
    return struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(chunk) + len(rest)) + \
        struct.pack("<II", len(chunk), 0x4E4F534A) + chunk + rest


def build(cid):
    c = CREATURES[cid]
    s = scale_of(cid)
    arm, mesh, acts = load(cid)
    if set(acts) != set(c["clips"]):
        raise ValueError(f"{cid}: source clips {sorted(acts)} != th_creatures.py clips {sorted(c['clips'])}")
    if not set(CREATURE_REQUIRED_ROLES) <= set(c["roles"]) or not set(c["roles"].values()) <= set(c["clips"]):
        raise ValueError(f"{cid}: roles {c['roles']} must cover {CREATURE_REQUIRED_ROLES} with its own clips")
    arm.scale = arm.scale * s
    for suffix, a in acts.items():
        a.name = suffix
    for m in mesh.data.materials:
        flat_material(m)

    # rest-pose bounds (render), then the idle clip's bounds (the collider), both relative to the rig's root
    arm.data.pose_position = "REST"
    render_box = to_gltf(*vertex_bounds(mesh))
    arm.data.pose_position = "POSE"
    arm.animation_data_create()
    idle = acts[c["roles"]["idle"]]
    arm.animation_data.action = idle
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for f in range(int(idle.frame_range[0]), int(idle.frame_range[1]) + 1):
        bpy.context.scene.frame_set(f)
        flo, fhi = vertex_bounds(mesh)
        lo, hi = Vector(map(min, lo, flo)), Vector(map(max, hi, fhi))
    m = to_gltf(lo, hi)
    box = [[math.floor(v * 100 + 0.05) / 100 for v in m[0]], [math.ceil(v * 100 - 0.05) / 100 for v in m[1]]]
    if any(abs(a - b) > 1e-9 for p, q in zip(box, c["box"]) for a, b in zip(p, q)):
        raise ValueError(f"{cid}: idle bounds give box {box}, th_creatures.py has {c['box']}: update it "
                         f"(th_creature_ref.py reports the same)")
    moving = {n: round(root_travel(arm, acts[n]), 3) for n, loop in c["clips"].items() if loop}
    if any(t > IN_PLACE_M for t in moving.values()):
        raise ValueError(f"{cid}: loop clips with root motion (m): {moving}")
    arm.animation_data.action = idle
    bpy.context.scene.frame_set(0)

    tmp = tempfile.mkdtemp(prefix="th_creature_")
    try:
        raw, out = os.path.join(tmp, "raw.glb"), os.path.join(tmp, "out.glb")
        bpy.ops.export_scene.gltf(filepath=raw, export_format="GLB", export_animations=True,
                                  export_animation_mode="ACTIONS", export_skins=True, export_morph=False,
                                  export_yup=True, export_cameras=False, export_lights=False, export_extras=False,
                                  export_materials="EXPORT", export_image_format="NONE")
        subprocess.run([GLTFPACK, "-i", raw, "-o", out, "-cc", "-kn", "-km"], check=True, capture_output=True)
        glb = name_body(open(out, "rb").read())
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    g = glb_json(glb)
    acc = g["accessors"]
    clips = sorted(a["name"] for a in g.get("animations", []))
    if clips != sorted(c["clips"]):
        raise ValueError(f"{cid}: the GLB's clips {clips} != {sorted(c['clips'])}")
    if len(g.get("skins", [])) != 1:
        raise ValueError(f"{cid}: {len(g.get('skins', []))} skins in the GLB, expected one")
    tris = sum(acc[p["indices"]]["count"] // 3 for mh in g["meshes"] for p in mh["primitives"])
    (x0, y0, z0), (x1, y1, z1) = c["box"]
    half = [round((x1 - x0) / 2, 4), round((y1 - y0) / 2, 4), round((z1 - z0) / 2, 4)]
    centre = [round((x0 + x1) / 2, 4), round((y0 + y1) / 2, 4), round((z0 + z1) / 2, 4)]
    glb_name = asset_name(cid, "glb")
    if DRY:
        open(os.path.join(DRY, glb_name), "wb").write(glb)
    else:
        publish(glb_name, glb)
    d = {
        "schema": SCHEMA, "id": cid, "kind": "creature",
        "frame": "glTF: metres, +Y up, right-handed",
        "forward": "+Z",
        "render": {"glb": glb_name, "triangles": tris, "materials": sorted(mt.get("name", "") for mt in g["materials"]),
                   "skinned": True, "joints": len(g["skins"][0]["joints"])},
        "bounds": {"render": [[round(v, 4) for v in p] for p in render_box], "collision": c["box"]},
        "collision": {"band_m": None, "shapes": [{"type": "box", "half": half}],
                      "colliders": [{"shape": 0, "name": "Body", "pos": centre, "rot": [0.0, 0.0, 0.0, 1.0]}]},
        "animations": {"clips": [{"name": a["name"], "duration": round(max(acc[x["input"]]["max"][0]
                                                                          for x in a["samplers"]), 4),
                                  "loop": c["clips"][a["name"]]} for a in g["animations"]],
                       "roles": c["roles"]},
        "flies": c["flies"],
        "source": {"pack": CREATURE_PACK["name"], "file": c["file"], "license": CREATURE_PACK["license"],
                   "scale": round(s, 6)},
        "portals": [], "spawns": [], "interactions": [], "children": [],
    }
    return d, glb_name, len(glb), moving


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ids = argv or list(CREATURES)
    unknown = [i for i in ids if i not in CREATURES]
    if unknown:
        raise SystemExit(f"unknown creature ids {unknown}; known: {sorted(CREATURES)}")
    entries, report = {}, {}
    for cid in ids:
        d, glb_name, size, moving = build(cid)
        if DRY:
            json.dump(d, open(os.path.join(DRY, asset_name(cid, "json")), "w"), indent=2)
        else:
            entries[cid] = (publish_descriptor(d), glb_name)
        report[cid] = {"glb": glb_name, "bytes": size, "triangles": d["render"]["triangles"],
                       "joints": d["render"]["joints"], "materials": len(d["render"]["materials"]),
                       "scale": d["source"]["scale"], "clips": len(d["animations"]["clips"]), "root_travel_m": moving}
    cat = {"version": None} if DRY else write_catalogue(entries, set(), merge_textures=True)
    print("TH_CREATURE " + json.dumps({"creatures": report, "catalogue_version": cat["version"]}))
