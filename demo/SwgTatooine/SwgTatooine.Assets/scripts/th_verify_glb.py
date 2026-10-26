import bpy, sys, math, os, json, struct, subprocess, tempfile, shutil
from mathutils import Vector

# Re-imports an exported GLB into an empty scene and renders it, to check what the client will actually get:
#   blender -b --factory-startup --python scripts/th_verify_glb.py -- <file.glb> <out.png> [azimuth_deg] [elevation_deg]
#                                                                       [lod]
# Writes <out.png> (lit, AO multiplied into the base colour) and <out>_ao.png (AO alone, unlit). Only one level of
# detail is shown: LOD0 by default (the nodes the GLB hides on load stay hidden), or the node <LOD0 node>_LOD<lod>.
#
# Blender's importer rejects KHR_texture_basisu, so the GLB is first rewritten into a temporary .gltf whose images
# are the SHIPPED KTX2 transcoded back to PNG (ktx extract: the same transcoding the client does, artefacts
# included). Geometry stays EXT_meshopt_compression, decoded by the importer. Blender also ignores glTF occlusion:
# the AO image the importer leaves on the glTF Settings node is wired into the shading here, so it can be judged.
ROOT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KTX_EXE = os.path.join(ROOT_DIR, "tools", "ktx", "bin", "ktx" + (".exe" if os.name == "nt" else ""))
args = sys.argv[sys.argv.index("--") + 1:]
glb, out = os.path.abspath(args[0]), os.path.abspath(args[1])
az, el = (float(args[2]) if len(args) > 2 else 35.0), (float(args[3]) if len(args) > 3 else 25.0)
lod = int(args[4]) if len(args) > 4 else 0


def ktx_to_png_gltf(glb, tmp):
    """GLB with KTX2 images -> tmp/asset.gltf + asset.bin + one PNG per image.
    Returns (path, transcoded count, names of the nodes hidden on load)."""
    b = open(glb, 'rb').read()
    n = struct.unpack('<I', b[12:16])[0]
    j = json.loads(b[20:20 + n])
    bin_data = b[20 + n + 8:]
    base = os.path.dirname(glb)
    for i, img in enumerate(j.get("images", [])):
        if "uri" in img:
            src = os.path.join(base, img.pop("uri"))
        else:
            v = j["bufferViews"][img.pop("bufferView")]
            src = os.path.join(tmp, f"img{i}.ktx2")
            with open(src, 'wb') as f:
                f.write(bin_data[v.get("byteOffset", 0):v.get("byteOffset", 0) + v["byteLength"]])
        png = f"img{i}.png"
        subprocess.run([KTX_EXE, "extract", "--transcode", "rgba8", "--level", "0", src, os.path.join(tmp, png)],
                       check=True, capture_output=True)
        img["uri"] = png; img["mimeType"] = "image/png"
    for t in j.get("textures", []):
        ext = t.get("extensions", {}).pop("KHR_texture_basisu", None)
        if ext is not None:
            t["source"] = ext["source"]
        if not t.get("extensions"):
            t.pop("extensions", None)
    for key in ("extensionsUsed", "extensionsRequired"):
        j[key] = [e for e in j.get(key, []) if e != "KHR_texture_basisu"]
    with open(os.path.join(tmp, "asset.bin"), 'wb') as f:
        f.write(bin_data)
    j["buffers"][0]["uri"] = "asset.bin"                       # GLB BIN chunk; meshopt fallback buffers stay uri-less
    path = os.path.join(tmp, "asset.gltf")
    with open(path, 'w', encoding='utf8') as f:
        json.dump(j, f)
    hidden = [n["name"] for n in j["nodes"] if n.get("extensions", {}).get("KHR_node_visibility", {}).get("visible") is False]
    return path, len(j.get("images", [])), hidden


def occlusion_image(m):
    """The image node feeding the importer's glTF Settings / glTF Material Output 'Occlusion' socket, if any."""
    for node in m.node_tree.nodes:
        if node.type == 'GROUP' and node.node_tree and node.node_tree.name.startswith("glTF"):
            sock = node.inputs.get("Occlusion")
            stack = [l.from_node for l in sock.links] if sock else []
            while stack:
                up = stack.pop()
                if up.type == 'TEX_IMAGE':
                    return up
                stack += [l.from_node for s in up.inputs for l in s.links]
    return None


def wire_ao(m, ao_only):
    """Multiply AO into Base Color (lit view), or show AO alone as emission (AO view)."""
    nt = m.node_tree
    bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    tex = occlusion_image(m)
    if bsdf is None or tex is None:
        return False
    sep = nt.nodes.new('ShaderNodeSeparateColor')
    nt.links.new(tex.outputs['Color'], sep.inputs['Color'])
    if ao_only:
        out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL')
        emit = nt.nodes.new('ShaderNodeEmission')
        nt.links.new(sep.outputs['Red'], emit.inputs['Color'])
        nt.links.new(emit.outputs['Emission'], out.inputs['Surface'])
        return True
    mul = nt.nodes.new('ShaderNodeMix'); mul.data_type = 'RGBA'; mul.blend_type = 'MULTIPLY'
    mul.inputs['Factor'].default_value = 1.0
    base = bsdf.inputs['Base Color']
    if base.links:
        nt.links.new(base.links[0].from_socket, mul.inputs['A'])
    else:
        mul.inputs['A'].default_value = base.default_value
    nt.links.new(sep.outputs['Red'], mul.inputs['B'])
    nt.links.new(mul.outputs['Result'], base)
    return True


def render(path):
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)


tmp = tempfile.mkdtemp(prefix="th_verify_")
try:
    gltf, transcoded, hidden = ktx_to_png_gltf(glb, tmp)
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.ops.import_scene.gltf(filepath=gltf)
    for img in bpy.data.images:
        img.pack()                                              # the temp folder goes away before the renders end
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    if lod:
        shown = [o for o in meshes if o.name.endswith(f"_LOD{lod}")]
        if len(shown) != 1:
            raise ValueError(f"LOD{lod}: expected one node, found {[o.name for o in shown]}")
    else:
        shown = [o for o in meshes if o.name not in hidden]
    for o in meshes:
        if o not in shown:
            bpy.data.objects.remove(o, do_unlink=True)
    meshes = shown
    for o in meshes:                                            # glTF: COLOR_0 multiplies the base colour
        for s in o.material_slots:
            bsdf = next((n for n in s.material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
            if o.data.color_attributes and bsdf is not None and not bsdf.inputs['Base Color'].links:
                ca = s.material.node_tree.nodes.new('ShaderNodeVertexColor')
                ca.layer_name = o.data.color_attributes[0].name
                s.material.node_tree.links.new(ca.outputs['Color'], bsdf.inputs['Base Color'])
    pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    lo = Vector(map(min, *pts)); hi = Vector(map(max, *pts))
    center, radius = (lo + hi) / 2, (hi - lo).length / 2

    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE'
    sc.render.resolution_x, sc.render.resolution_y = 1280, 800
    sc.view_settings.view_transform = 'AgX'
    w = sc.world or bpy.data.worlds.new("w"); sc.world = w
    w.color = (0.35, 0.38, 0.45)
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN'))
    sun.data.energy = 3.0; sun.rotation_euler = (math.radians(50), 0, math.radians(30))
    sc.collection.objects.link(sun)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); sc.collection.objects.link(cam)
    a, e = math.radians(az), math.radians(el)
    d = radius * 2.4
    cam.location = center + Vector((d * math.sin(a) * math.cos(e), -d * math.cos(a) * math.cos(e), d * math.sin(e)))
    cam.rotation_euler = (center - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sc.camera = cam

    mats = {s.material for o in meshes for s in o.material_slots if s.material}
    lit = [m.name for m in mats if wire_ao(m, ao_only=False)]
    render(out)
    for m in mats:                                              # AO view: fresh wiring on top of the lit one
        wire_ao(m, ao_only=True)
    sun.hide_render = True; w.color = (0, 0, 0)
    stem, ext = os.path.splitext(out)
    render(f"{stem}_ao{ext}")
    print("VERIFY", os.path.basename(glb), "meshes", len(meshes), "images transcoded", transcoded,
          "missing", sorted({n.image.name for m in mats for n in m.node_tree.nodes
                             if n.type == 'TEX_IMAGE' and n.image and not n.image.has_data}),
          "materials", len(mats), "with AO", len(lit), "without AO", sorted(m.name for m in mats if m.name not in lit))
finally:
    shutil.rmtree(tmp, ignore_errors=True)
