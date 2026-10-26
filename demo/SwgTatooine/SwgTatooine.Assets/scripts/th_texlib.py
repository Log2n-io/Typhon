import bpy, bmesh, os
import numpy as np

# Shared, tileable texture library for every Tatooine asset.
#
# The procedural materials stay as the source of truth (renamed SRC_*, fake user). Each one is baked in
# Cycles on a vertical XZ plane (u = x, v = z, the same orientation th_uv.py gives vertical faces), then
# made seamless by cross-fading an overlap band, and TH_* is rebuilt as an image material on UV0.
# Roughness and metallic are constants in every source, so they stay factors: two maps per material.
#
# Set ONLY = ["TH_Sandstone", ...] before exec() to rebake a subset.

LIB = ["TH_Sandstone", "TH_DomeStone", "TH_DoorWood", "TH_DoorBatten",
       "TH_Metal", "TH_Duracrete", "TH_ShuttleHull",
       "TH_DarkDuracrete", "TH_SteelPlate"]                 # these two: sources in th_industry.py
TILE_M = 4.0       # metres covered by one tile
RES = 1024         # tile resolution -> 256 px/m
OVER = 128         # cross-fade band, px
SAMPLES = 16
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(bpy.data.filepath)), "textures", "tatooine_common")   # the open .blend's root
UV0 = "UV0"

scene = bpy.context.scene
os.makedirs(OUT_DIR, exist_ok=True)


def slug(name):
    return name[3:].lower() if name.startswith("TH_") else name.lower()


def source_of(name):
    src = bpy.data.materials.get("SRC_" + name[3:])
    if src is None:
        src = bpy.data.materials[name].copy()
        src.name = "SRC_" + name[3:]
        src.use_fake_user = True
    return src


def bake_plane(mat):
    size = TILE_M * (RES + OVER) / RES
    bm = bmesh.new()
    vs = [bm.verts.new(p) for p in [(0, 0, 0), (size, 0, 0), (size, 0, size), (0, 0, size)]]
    f = bm.faces.new(vs)
    uv = bm.loops.layers.uv.new(UV0)
    for loop, (u, v) in zip(f.loops, [(0, 0), (1, 0), (1, 1), (0, 1)]):
        loop[uv].uv = (u, v)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new("_bake_plane"); bm.to_mesh(me); bm.free()
    if me.polygons[0].normal.y > 0:          # face must look down -Y so tangent x, bitangent z, normal -y
        me.flip_normals()
    me.materials.append(mat)
    ob = bpy.data.objects.new("_bake_plane", me)
    scene.collection.objects.link(ob)
    return ob


def bake_pass(ob, mat, kind):
    w = h = RES + OVER
    img = bpy.data.images.new(f"_bake_{kind}", w, h, alpha=False)
    img.colorspace_settings.name = 'sRGB' if kind == "basecolor" else 'Non-Color'
    node = mat.node_tree.nodes.new('ShaderNodeTexImage'); node.image = img
    mat.node_tree.nodes.active = node
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    ob.select_set(True); bpy.context.view_layer.objects.active = ob
    if kind == "basecolor":
        bpy.ops.object.bake(type='DIFFUSE', pass_filter={'COLOR'}, use_clear=True, margin=0)
    else:
        bpy.ops.object.bake(type='NORMAL', normal_space='TANGENT', use_clear=True, margin=0)
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)
    mat.node_tree.nodes.remove(node); bpy.data.images.remove(img)
    return px


def seamless(px, normal):
    """Tile of RES from a (RES+OVER)^2 bake: columns/rows [0, OVER) fade from the sample one tile further
    (x+RES) to their own, so the tile's first column continues its last one."""
    t = np.linspace(0.0, 1.0, OVER, dtype=np.float32)
    t = t * t * (3 - 2 * t)                                  # smoothstep weights
    out = px[:, :RES].copy()
    out[:, :OVER] = px[:, RES:RES + OVER] * (1 - t)[None, :, None] + px[:, :OVER] * t[None, :, None]
    tmp = out
    out = tmp[:RES].copy()
    out[:OVER] = tmp[RES:RES + OVER] * (1 - t)[:, None, None] + tmp[:OVER] * t[:, None, None]
    if normal:                                               # blended normals must be renormalised
        n = out[..., :3] * 2 - 1
        n /= np.linalg.norm(n, axis=-1, keepdims=True) + 1e-8
        out[..., :3] = n * 0.5 + 0.5
    out[..., 3] = 1.0
    return out


def save(px, name, colorspace):
    path = os.path.join(OUT_DIR, name + ".png")
    img = bpy.data.images.get(name + ".png")
    if img is None:
        img = bpy.data.images.new(name + ".png", RES, RES, alpha=False)
    elif img.size[0] != RES:
        img.scale(RES, RES)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(px.ravel())
    img.filepath_raw = path; img.file_format = 'PNG'
    img.save()
    img.filepath = bpy.path.relpath(path).replace("\\", "/"); img.source = 'FILE'; img.reload()   # relative with /: the tree moves, any OS
    return img


def rebuild(name, src, col_img, nrm_img):
    p_src = next(n for n in src.node_tree.nodes if n.bl_idname == 'ShaderNodeBsdfPrincipled')
    m = bpy.data.materials[name]
    nt = m.node_tree; nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial'); out.location = (500, 0)
    b = nt.nodes.new('ShaderNodeBsdfPrincipled'); b.location = (200, 0)
    nt.links.new(b.outputs['BSDF'], out.inputs['Surface'])
    uvn = nt.nodes.new('ShaderNodeUVMap'); uvn.uv_map = UV0; uvn.location = (-700, 0)
    tc = nt.nodes.new('ShaderNodeTexImage'); tc.image = col_img; tc.location = (-400, 150)
    tn = nt.nodes.new('ShaderNodeTexImage'); tn.image = nrm_img; tn.location = (-400, -150)
    nm = nt.nodes.new('ShaderNodeNormalMap'); nm.uv_map = UV0; nm.location = (-100, -150)
    for t in (tc, tn):
        nt.links.new(uvn.outputs['UV'], t.inputs['Vector'])
    nt.links.new(tc.outputs['Color'], b.inputs['Base Color'])
    nt.links.new(tn.outputs['Color'], nm.inputs['Color'])
    nt.links.new(nm.outputs['Normal'], b.inputs['Normal'])
    b.inputs['Roughness'].default_value = p_src.inputs['Roughness'].default_value
    b.inputs['Metallic'].default_value = p_src.inputs['Metallic'].default_value
    m["texlib_tile_m"] = TILE_M


prev = (scene.render.engine, scene.cycles.samples)
scene.render.engine = 'CYCLES'; scene.cycles.samples = SAMPLES
done = {}
try:
    for name in globals().get("ONLY") or LIB:
        src = source_of(name)
        tmp = src.copy()
        # Cycles' diffuse colour is base x (1 - metallic): bake the base colour itself (the TH_ copy keeps metallic)
        next(n for n in tmp.node_tree.nodes if n.bl_idname == 'ShaderNodeBsdfPrincipled').inputs['Metallic'].default_value = 0.0
        ob = bake_plane(tmp)
        try:
            col = seamless(bake_pass(ob, tmp, "basecolor"), normal=False)
            nrm = seamless(bake_pass(ob, tmp, "normal"), normal=True)
        finally:
            me = ob.data
            bpy.data.objects.remove(ob, do_unlink=True); bpy.data.meshes.remove(me)
            bpy.data.materials.remove(tmp)
        s = slug(name)
        rebuild(name, src, save(col, f"{s}_basecolor", 'sRGB'), save(nrm, f"{s}_normal", 'Non-Color'))
        done[name] = s
finally:
    scene.render.engine, scene.cycles.samples = prev

result = done
