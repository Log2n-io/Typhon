import bpy, os  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's (headless, no .blend)
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
import bpy, bmesh, math, os, json, hashlib, shutil, struct, subprocess
import numpy as np
from contextlib import contextmanager
from urllib.parse import unquote
from mathutils import Vector, Matrix, Quaternion

# Asset exporter: one GLB + one descriptor JSON per asset, plus a catalogue, into build/assets/ (flat folder,
# <name>-<rev> names, the revisions taken from revisions.json). Headless only, as a build step, never inside an editing session (inside the UI,
# overriding the scene while the window shows another one crashed Blender in the depsgraph):
#   blender -b blender/tatooine.blend --python scripts/th_export.py
#
# Per asset (every object carrying asset_id):
#   1. render meshes under the asset root -> evaluated (modifiers applied) -> asset-local -> joined into one mesh
#   2. UV1 = unique smart-project unwrap, AO baked on it in Cycles, in an isolated scene (+ ground plane for
#      exteriors), so no other asset of the editing scene occludes it
#   3. per-asset material copies: shared textures stay on UV0, the AO image goes to glTF occlusion on UV1
#   4. glTF export (separate files, EXT_meshopt_compression geometry), then packed into a GLB here: textures
#      re-encoded to KTX2 (KHR_texture_basisu); the shared library ones stay EXTERNAL (same <stem>-<rev> URI from every
#      GLB, same folder -> the client caches one GPU texture), the AO is embedded
#   5. descriptor: bounds, collision (shape library + colliders), portals, spawns, interactions, child assets
#   6. buildings only: two coarser levels in the same GLB (see LODs below), listed in descriptor render.lods
# Everything in the descriptor is in the glTF frame (metres, Y-up, right-handed), like the GLB.
# Catalogue: catalogue-<version>.json (immutable, what the game server pins and announces) + catalogue.json (pointer).
# Export only assets whose revision was bumped: re-exporting an unchanged revision is refused (th_publish.py).
# The brotli sidecars (<file>.br) the HTTP origin serves are written by `scripts/assets.py compress`, not here.

exec(open(os.path.join(TH_ROOT, "scripts", "th_publish.py")).read(), globals())   # ROOT_DIR, OUT_DIR, SCHEMA,
                                                                                   # sha, publish, write_catalogue
STAGE_DIR = os.path.join(ROOT_DIR, "build", "_stage")
TEX_DIR = os.path.normcase(os.path.join(ROOT_DIR, "textures"))
KTX_EXE = tool("ktx")                                                     # KTX-Software 4.4.2 (Khronos)
KTX_CACHE = os.path.join(ROOT_DIR, "build", "_ktxcache")
UV0, UV1 = "UV0", "UV1"
PART_M = "th_part_m"        # face attribute: largest dimension of the part the face came from
AO_PX_PER_M = 32            # AO is low frequency
AO_MIN, AO_MAX = 256, 2048
AO_SAMPLES = 64
AO_DISTANCE = {"interior": 1.0, "default": 1.5}
PROBE_PX, PROBE_SAMPLES = 512, 16
HIDDEN_AO = 0.02             # a face whose probe AO stays below this everywhere is never visible
MIN_EVIDENCE = 4             # probe texels a face must cover before it may be culled (thinner faces are kept)
FOUNDATION_D = 1.0          # th_helpers.FOUNDATION_D
GLTFPACK = tool("gltfpack")                                                     # meshoptimizer 1.3
# LODs: sibling leaf nodes <node>_LOD1 / _LOD2 in the same GLB, hidden on load (KHR_node_visibility), listed in
# render.lods with error_m, an upper bound of their geometric deviation from LOD0 in metres (the client switches on
# projected error, error_m * pixels_per_metre / distance):
#   LOD1: the same parts evaluated without their Bevel modifiers (the bevels are about half the triangles), culled,
#         unwrapped and AO-baked like LOD0 into its own AO map at quarter resolution; same textures, same draw count.
#         error_m = the widest bevel.
#   LOD2: LOD1 minus the parts smaller than LOD2_DROP_M, as ONE primitive with ONE untextured material: every corner
#         is coloured with its material's mean base colour x LOD1's AO, then the whole is cut by meshoptimizer at
#         LOD2_ERROR_M. One draw call, no texture; meant for the far field. Emissive glow is lost.
LOD_KINDS = {"building"}
LOD1_AO_SCALE = 0.25
LOD2_DROP_M = 0.5
LOD2_ERROR_M = 0.25
ONLY =os.environ.get("TH_EXPORT_ONLY", "").split(",") if os.environ.get("TH_EXPORT_ONLY") else None
if not bpy.app.background:
    raise RuntimeError("th_export.py runs headless: blender -b blender/tatooine.blend --python scripts/th_export.py")


# ---------------- frame conversion: Blender (Z-up) -> glTF (Y-up) ----------------
def _r(x, n=5):
    return round(x, n) + 0.0                # + 0.0 folds -0.0 into 0.0


def gv(v):
    return [_r(v[0]), _r(v[2]), _r(-v[1])]


def gq(q):                                  # mathutils (w, x, y, z) -> glTF [x, y, z, w]
    return [_r(q.x, 6), _r(q.z, 6), _r(-q.y, 6), _r(q.w, 6)]


def ghalf(h):                               # local half extents: Blender local Z is glTF local Y
    return [_r(h[0]), _r(h[2]), _r(h[1])]


FORWARD = "-Z"                              # markers face Blender local +Y, which is glTF local -Z


def gaabb(lo, hi):
    a, b = gv(lo), gv(hi)
    return [[min(a[i], b[i]) for i in range(3)], [max(a[i], b[i]) for i in range(3)]]


# ---------------- context helpers ----------------
# Everything happens in the editing scene itself: temporary objects live in an "_export" collection and the
# AO bake isolates the asset by hiding every other object from render. A second, temporary scene was tried
# first and crashed the glTF exporter in the depsgraph (evaluated_depsgraph_get on the override scene).
def export_col():
    sc = bpy.data.scenes["Scene"]
    c = bpy.data.collections.get("_export")
    if c is None:
        c = bpy.data.collections.new("_export")
    if c.name not in sc.collection.children:
        sc.collection.children.link(c)
    return c


@contextmanager
def isolated_render(sc, keep):
    """Only `keep` is visible to Cycles (AO rays) while inside."""
    hidden = [o for o in sc.objects if o not in keep and not o.hide_render]
    for o in hidden:
        o.hide_render = True
    try:
        yield
    finally:
        for o in hidden:
            o.hide_render = False


@contextmanager
def render_settings(sc, distance):
    w = sc.world.light_settings
    prev = (sc.render.engine, sc.cycles.samples, w.distance)
    sc.render.engine = 'CYCLES'; w.distance = distance
    try:
        yield
    finally:
        sc.render.engine, sc.cycles.samples, w.distance = prev


@contextmanager
def in_scene(sc, objs=(), active=None):
    """Operators (join, bake, glTF export) run against the export scene, its view layer and the given
    selection; headless there is no context to inherit them from."""
    kw = dict(scene=sc, view_layer=sc.view_layers[0])
    if active is not None:
        kw.update(active_object=active, object=active, edit_object=active,
                  selected_objects=list(objs), selected_editable_objects=list(objs))
    with bpy.context.temp_override(**kw):
        yield


def select_only(sc, objs, active):
    vl = sc.view_layers[0]
    vl.update()                                 # drop objects removed since the last update
    for o in vl.objects:
        o.select_set(False, view_layer=vl)
    for o in objs:
        o.select_set(True, view_layer=vl)
    vl.objects.active = active


@contextmanager
def all_layer_collections_included():
    """Asset sources may be excluded from the view layer (instanced only); evaluation needs them included."""
    def walk(lc):
        yield lc
        for c in lc.children:
            yield from walk(c)
    vl = bpy.data.scenes["Scene"].view_layers[0]
    excluded = [lc for lc in walk(vl.layer_collection) if lc.exclude]
    for lc in excluded:
        lc.exclude = False
    try:
        yield
    finally:
        for lc in excluded:
            lc.exclude = True


# ---------------- gathering ----------------
def descendants(o):
    for c in o.children:
        yield c
        yield from descendants(c)


def asset_roots():
    return sorted((o for o in bpy.data.objects if "asset_id" in o), key=lambda o: o["asset_id"])


def collision_root(asset_id):
    return next((o for o in bpy.data.objects if o.get("col_for") == asset_id), None)


# ---------------- 1. joined render mesh ----------------
@contextmanager
def bevels_disabled(objs):
    """Bevel modifiers of `objs` off for evaluation; yields the widest bevel width (the error of leaving them out)."""
    bevels = [(o, m) for o in objs for m in o.modifiers if m.type == 'BEVEL' and m.show_viewport]
    if any(m.offset_type != 'OFFSET' for _, m in bevels):
        raise ValueError("bevel width must be an offset to bound the LOD error")
    for _, m in bevels:
        m.show_viewport = False
    bpy.context.view_layer.update()
    try:
        yield max((m.width * max(o.matrix_world.to_scale()) for o, m in bevels), default=0.0)   # local -> metres
    finally:
        for _, m in bevels:
            m.show_viewport = True
        bpy.context.view_layer.update()


def build_render_mesh(root, sc, suffix=""):
    """Every render mesh under root, evaluated, in asset-local space, joined into one object. Each face carries the
    largest dimension of the part it came from (face attribute PART_M), for LOD2 to drop small parts."""
    inv = root.matrix_world.inverted()
    dg = bpy.context.evaluated_depsgraph_get()
    parts = []
    for o in descendants(root):
        if o.type != 'MESH':
            continue
        me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
        me.transform(inv @ o.matrix_world)
        if (inv @ o.matrix_world).determinant() < 0:
            me.flip_normals()
        size = max(o.dimensions)
        me.attributes.new(PART_M, 'FLOAT', 'FACE').data.foreach_set("value", [size] * len(me.polygons))
        ob = bpy.data.objects.new(f"_x_{o.name}", me)
        export_col().objects.link(ob)
        parts.append(ob)
    if not parts:
        return None
    select_only(sc, parts, parts[0])
    with in_scene(sc, parts, parts[0]):
        if len(parts) > 1:
            bpy.ops.object.join()
    joined = sc.view_layers[0].objects.active
    joined.name = f"_x_{root['asset_id']}{suffix}"
    joined.data.name = f"{root['asset_id']}{suffix}"        # glTF mesh name
    return joined


def lights_of(root, sc):
    inv = root.matrix_world.inverted()
    out = []
    for o in descendants(root):
        if o.type == 'LIGHT':
            lo = bpy.data.objects.new(f"_x_{o.name}", o.data)
            lo.matrix_world = inv @ o.matrix_world
            export_col().objects.link(lo)
            out.append(lo)
    return out


# ---------------- 2. UV1 + AO ----------------
def ao_resolution(me):
    area = sum(p.area for p in me.polygons)
    px = math.sqrt(area) * AO_PX_PER_M
    return int(min(max(2 ** math.ceil(math.log2(max(px, 1))), AO_MIN), AO_MAX))


def make_uv1(ob, sc):
    me = ob.data
    if UV1 in me.uv_layers:
        me.uv_layers.remove(me.uv_layers[UV1])
    me.uv_layers.new(name=UV1)
    me.uv_layers.active = me.uv_layers[UV1]
    select_only(sc, [ob], ob)
    with in_scene(sc, [ob], ob):
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.01, area_weight=0.0,
                                 correct_aspect=True, scale_to_bounds=False)
        bpy.ops.object.mode_set(mode='OBJECT')


def is_emissive(m):
    b = next((n for n in m.node_tree.nodes if n.bl_idname == 'ShaderNodeBsdfPrincipled'), None)
    return b is not None and b.inputs['Emission Strength'].default_value > 0.0


def gltf_output_group():
    g = bpy.data.node_groups.get("glTF Material Output")
    if g is None:
        g = bpy.data.node_groups.new("glTF Material Output", 'ShaderNodeTree')
        g.interface.new_socket("Occlusion", in_out='INPUT', socket_type='NodeSocketFloat')
    return g


def per_asset_materials(ob, asset_id, ao_img):
    """Copies of the shared materials for this asset: an active AO image node on UV1 for the bake, wired to
    the glTF occlusion socket for the export (skipped on emissive materials)."""
    copies = []
    for slot in ob.material_slots:
        m = slot.material.copy()
        m.name = f"{asset_id}:{slot.material.name}"
        m.use_backface_culling = True           # glTF doubleSided = false: every surface here is seen from one side
        nt = m.node_tree
        uv = nt.nodes.new('ShaderNodeUVMap'); uv.uv_map = UV1
        tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = ao_img; tex.name = "AO"
        nt.links.new(uv.outputs['UV'], tex.inputs['Vector'])
        nt.nodes.active = tex
        if not is_emissive(m):
            grp = nt.nodes.new('ShaderNodeGroup'); grp.node_tree = gltf_output_group()
            sep = nt.nodes.new('ShaderNodeSeparateColor')
            nt.links.new(tex.outputs['Color'], sep.inputs['Color'])
            nt.links.new(sep.outputs['Red'], grp.inputs['Occlusion'])
        slot.material = m
        copies.append(m)
    return copies


def ground_plane(sc, depth):
    """Buildings bake against a ground at the bottom of their foundation (depth = FOUNDATION_D), not at z = 0: the
    skirt is visible wherever the terrain is lower, so it must not come out black. Contact darkening at the terrain
    line is left to the runtime (shadows / SSAO), which knows where the terrain actually is. Things without a
    foundation (interactables, props) stand on the ground: depth 0, so their base gets its contact shade and their
    bottom faces are culled."""
    pm = bpy.data.meshes.new("_x_ground")
    bm = bmesh.new(); bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=200.0)
    bmesh.ops.translate(bm, verts=bm.verts, vec=(0, 0, -depth))
    bm.to_mesh(pm); bm.free()
    plane = bpy.data.objects.new("_x_ground", pm)
    export_col().objects.link(plane)
    return plane


def bake(ob, sc, samples, occluders):
    sc.cycles.samples = samples
    select_only(sc, [ob], ob)
    with isolated_render(sc, [ob] + occluders), in_scene(sc, [ob], ob):
        bpy.ops.object.bake(type='AO', use_clear=True, margin=4, target='IMAGE_TEXTURES')


def cull_hidden(ob, sc, probe):
    """Delete faces whose probe AO is black on EVERY texel they cover (a face half under a wall keeps its visible
    strip, so sampling a few points is not enough). A face covering fewer than MIN_EVIDENCE texel centres is kept:
    its texels hold dilated neighbour values, not its own (this deleted a thin antenna). Deleted through
    bpy.ops.mesh.delete, not a bmesh round trip, so the custom (hardened) normals of the remaining faces survive."""
    import numpy as np
    n = probe.size[0]
    px = np.array(probe.pixels[:], dtype=np.float32).reshape(n, n, 4)[..., 0]
    me = ob.data; uv = me.uv_layers[UV1].data
    me.calc_loop_triangles()
    lit = np.zeros(len(me.polygons), dtype=bool)
    evidence = np.zeros(len(me.polygons), dtype=np.int64)
    for t in me.loop_triangles:
        if lit[t.polygon_index]:
            continue
        p = np.array([uv[li].uv for li in t.loops], dtype=np.float64) * n - 0.5     # texel-centre space
        (ax, ay), (bx, by), (cx, cy) = p
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-12:                                 # degenerate in UV: no evidence
            continue
        x0, y0 = np.floor(p.min(0)).astype(int); x1, y1 = np.ceil(p.max(0)).astype(int)
        x0, y0, x1, y1 = max(x0, 0), max(y0, 0), min(x1, n - 1), min(y1, n - 1)
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
        w0 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / den
        w1 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / den
        inside = (w0 >= -0.02) & (w1 >= -0.02) & (1 - w0 - w1 >= -0.02)
        evidence[t.polygon_index] += int(inside.sum())
        if inside.any() and np.max(px[ys[inside], xs[inside]]) >= HIDDEN_AO:
            lit[t.polygon_index] = True
    # Thin faces (bevel strips, masts, decal strips) are judged by their edge neighbours that have their own evidence:
    # hidden only when there are at least two such neighbours and all are hidden (a bevel strip between two hidden
    # faces). No propagation: chaining through thin faces deleted the visible top of a strip lying on a surface,
    # because its sides reached the hidden bottom. Thin faces with fewer judged neighbours are kept.
    judged = evidence >= MIN_EVIDENCE
    hidden = judged & ~lit
    edge_faces = {}
    for poly in me.polygons:
        for ek in poly.edge_keys:
            edge_faces.setdefault(ek, []).append(poly.index)
    neighbours = [set() for _ in me.polygons]
    for faces in edge_faces.values():
        for f in faces:
            neighbours[f].update(faces)
    thin_hidden = [i for i in np.flatnonzero(~judged)
                   if len(nj := [j for j in neighbours[i] if j != i and judged[j]]) >= 2 and all(hidden[j] for j in nj)]
    hidden[thin_hidden] = True
    doomed = [int(i) for i in np.flatnonzero(hidden)]
    if doomed:
        before = len(me.polygons)
        select_only(sc, [ob], ob)
        sc.tool_settings.mesh_select_mode = (False, False, True)
        with in_scene(sc, [ob], ob):
            bpy.ops.object.mode_set(mode='EDIT')
            bm = bmesh.from_edit_mesh(me)        # selection set on the edit mesh: object-mode flags do not survive
            bm.faces.ensure_lookup_table()
            for f in bm.faces:
                f.select_set(False)
            for i in doomed:
                bm.faces[i].select_set(True)
            bmesh.update_edit_mesh(me)
            bpy.ops.mesh.delete(type='FACE')
            bpy.ops.object.mode_set(mode='OBJECT')
        assert before - len(me.polygons) == len(doomed), (before, len(me.polygons), len(doomed))
    hidden = len(doomed)
    return hidden


def bake_ao(ob, tag, sc, occluders, stage, temps, res_scale=1.0):
    """Steps 2-3 on `ob`: probe bake, hidden-face culling, final unwrap and AO bake, the AO saved into the stage folder
    (the glTF exporter writes it from there). Materials and images made here go into `temps` for cleanup.
    Returns (per-asset materials, AO image, faces culled, AO resolution)."""
    make_uv1(ob, sc)
    probe = bpy.data.images.new(f"{tag}_probe", PROBE_PX, PROBE_PX, alpha=False)
    probe.colorspace_settings.name = 'Non-Color'
    temps["images"].append(probe)
    mats = per_asset_materials(ob, tag, probe)
    temps["mats"] += mats
    bake(ob, sc, PROBE_SAMPLES, occluders)
    culled = cull_hidden(ob, sc, probe)
    res = max(AO_MIN, int(ao_resolution(ob.data) * res_scale))
    make_uv1(ob, sc)
    ao = bpy.data.images.new(f"{tag}_ao", res, res, alpha=False)
    ao.colorspace_settings.name = 'Non-Color'
    temps["images"].append(ao)
    for m in mats:
        m.node_tree.nodes["AO"].image = ao
    bake(ob, sc, AO_SAMPLES, occluders)
    path = os.path.join(stage, f"{tag}_ao.png")
    ao.filepath_raw = path; ao.file_format = 'PNG'; ao.save()
    ao.filepath = path; ao.source = 'FILE'
    return mats, ao, culled, res


# ---------------- 3b. LODs (buildings) ----------------
def principled(m):
    return next(n for n in m.node_tree.nodes if n.bl_idname == 'ShaderNodeBsdfPrincipled')


def mean_base_colour(m):
    """Linear RGB mean of a material's base colour: its image's mean, or the constant."""
    import numpy as np
    s = principled(m).inputs['Base Color']
    if not s.is_linked:
        return np.array(s.default_value[:3], dtype=np.float64)
    n = s.links[0].from_node
    if n.bl_idname != 'ShaderNodeTexImage':
        raise ValueError(f"{m.name}: base colour from {n.bl_idname}; LOD2 needs an image or a constant")
    px = np.empty(len(n.image.pixels), dtype=np.float32)
    n.image.pixels.foreach_get(px)                      # stored values: sRGB-encoded for an sRGB image
    rgb = px.reshape(-1, 4)[:, :3].astype(np.float64)
    if n.image.colorspace_settings.name == 'sRGB':
        rgb = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    return rgb.mean(0)


def sample_bilinear(img, uv):
    """Red channel of a square image at UVs (n, 2), bilinear, texel centres at (i + 0.5) / n."""
    import numpy as np
    n = img.size[0]
    px = np.empty(n * n * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    r = px.reshape(n, n, 4)[..., 0]
    x = np.clip(uv[:, 0] * n - 0.5, 0, n - 1); y = np.clip(uv[:, 1] * n - 0.5, 0, n - 1)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    x1, y1 = np.minimum(x0 + 1, n - 1), np.minimum(y0 + 1, n - 1)
    fx, fy = x - x0, y - y0
    return ((r[y0, x0] * (1 - fx) + r[y0, x1] * fx) * (1 - fy) + (r[y1, x0] * (1 - fx) + r[y1, x1] * fx) * fy)


def meshopt_simplify(pos, nrm, col, idx, error_m, tmp):
    """Cut an indexed triangle mesh with gltfpack (meshoptimizer) at an absolute error in metres. Strict mode: seams
    (hard edges, colour steps) stay put, for ~15% more triangles than permissive (-sp).
    Returns (pos, nrm, col, idx) as numpy arrays."""
    import numpy as np
    arrays = [("POSITION", pos, "VEC3"), ("NORMAL", nrm, "VEC3"), ("COLOR_0", col, "VEC3")]
    blob, views, accs = bytearray(), [], []
    for _, a, typ in arrays:
        data = np.ascontiguousarray(a, dtype=np.float32).tobytes()
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)})
        accs.append({"bufferView": len(views) - 1, "componentType": 5126, "count": len(a), "type": typ})
        blob += data
    accs[0].update(min=pos.min(0).tolist(), max=pos.max(0).tolist())
    data = np.ascontiguousarray(idx, dtype=np.uint32).tobytes()
    views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)})
    accs.append({"bufferView": len(views) - 1, "componentType": 5125, "count": len(idx), "type": "SCALAR"})
    blob += data
    with open(os.path.join(tmp, "far_in.bin"), "wb") as f:
        f.write(blob)
    j = {"asset": {"version": "2.0"}, "buffers": [{"uri": "far_in.bin", "byteLength": len(blob)}],
         "bufferViews": views, "accessors": accs, "nodes": [{"mesh": 0}], "scenes": [{"nodes": [0]}], "scene": 0,
         "meshes": [{"primitives": [{"attributes": {name: i for i, (name, _, _) in enumerate(arrays)},
                                     "indices": len(accs) - 1}]}]}
    inp, out = os.path.join(tmp, "far_in.gltf"), os.path.join(tmp, "far_out.gltf")
    with open(inp, "w") as f:
        json.dump(j, f)
    extent = float((pos.max(0) - pos.min(0)).max())
    subprocess.run([GLTFPACK, "-i", inp, "-o", out, "-si", "0.001", "-se", f"{min(error_m / extent, 1.0):.6f}",
                    "-noq"], check=True, capture_output=True)
    o = json.load(open(out))
    bufs = [open(os.path.join(tmp, b["uri"]), "rb").read() for b in o["buffers"]]

    def read(ai):
        a = o["accessors"][ai]; v = o["bufferViews"][a["bufferView"]]
        dt = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8}[a["componentType"]]
        w = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[a["type"]]
        if v.get("byteStride", np.dtype(dt).itemsize * w) != np.dtype(dt).itemsize * w:
            raise ValueError("gltfpack wrote an interleaved stream")
        r = np.frombuffer(bufs[v["buffer"]], dtype=dt, count=a["count"] * w,
                          offset=v.get("byteOffset", 0) + a.get("byteOffset", 0))
        if a.get("normalized"):                     # gltfpack stores colours as unorm even with -noq
            r = r / np.iinfo(dt).max
        return r.reshape(-1, w) if w > 1 else r
    p = o["meshes"][0]["primitives"][0]
    return (read(p["attributes"]["POSITION"]), read(p["attributes"]["NORMAL"]), read(p["attributes"]["COLOR_0"])[:, :3],
            read(p["indices"]).astype(np.int64))


def far_material(tag, roughness):
    m = bpy.data.materials.new(f"{tag}:TH_Far")
    if m.node_tree is None:
        m.use_nodes = True
    nt = m.node_tree
    ca = nt.nodes.new('ShaderNodeVertexColor'); ca.layer_name = "Col"
    b = principled(m)
    nt.links.new(ca.outputs['Color'], b.inputs['Base Color'])
    b.inputs['Roughness'].default_value = roughness
    b.inputs['Metallic'].default_value = 0.0
    m.use_backface_culling = True
    return m


def far_lod(lod1, ao, tag, stage, temps):
    """LOD2 from LOD1: parts smaller than LOD2_DROP_M dropped, every corner coloured with its material's mean base
    colour x the AO under it, merged into one primitive, cut by meshoptimizer. Returns (object, error_m): the error
    bound counts the cut and the dropped parts."""
    import numpy as np
    me = lod1.data
    me.calc_loop_triangles()
    nt = len(me.loop_triangles)
    corners = np.empty(nt * 3, dtype=np.int64); me.loop_triangles.foreach_get("loops", corners)
    poly = np.empty(nt, dtype=np.int64); me.loop_triangles.foreach_get("polygon_index", poly)
    part = np.empty(len(me.polygons), dtype=np.float32); me.attributes[PART_M].data.foreach_get("value", part)
    mat = np.empty(len(me.polygons), dtype=np.int64); me.polygons.foreach_get("material_index", mat)
    vidx = np.empty(len(me.loops), dtype=np.int64); me.loops.foreach_get("vertex_index", vidx)
    co = np.empty(len(me.vertices) * 3, dtype=np.float32); me.vertices.foreach_get("co", co)
    cn = np.empty(len(me.loops) * 3, dtype=np.float32); me.corner_normals.foreach_get("vector", cn)
    uv = np.empty(len(me.loops) * 2, dtype=np.float32); me.uv_layers[UV1].data.foreach_get("uv", uv)
    keep = part[poly] >= LOD2_DROP_M
    dropped = float(part[poly[~keep]].max()) if (~keep).any() else 0.0
    c = corners.reshape(-1, 3)[keep].ravel()
    slots = [s.material for s in lod1.material_slots]
    base = np.array([mean_base_colour(m) for m in slots])
    col = base[mat[poly[keep]].repeat(3)] * sample_bilinear(ao, uv.reshape(-1, 2)[c])[:, None]
    pos, nrm = co.reshape(-1, 3)[vidx[c]], cn.reshape(-1, 3)[c]
    key = np.round(np.hstack([pos * 1e4, nrm * 1e3, col * 1e3])).astype(np.int64)
    _, first, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    pos, nrm, col, idx = meshopt_simplify(pos[first], nrm[first], col[first], inverse.ravel(), LOD2_ERROR_M, stage)
    # roughness: the materials' mean, weighted by the triangles they cover
    rough = (np.bincount(mat[poly[keep]], minlength=len(slots)) @
             np.array([principled(m).inputs['Roughness'].default_value for m in slots])) / keep.sum()
    m = far_material(tag, float(rough))
    temps["mats"].append(m)
    fm = bpy.data.meshes.new(tag)
    fm.from_pydata(pos.tolist(), [], idx.reshape(-1, 3).tolist())
    fm.polygons.foreach_set("use_smooth", [True] * len(fm.polygons))
    fm.normals_split_custom_set_from_vertices(nrm.tolist())
    ca = fm.color_attributes.new("Col", 'FLOAT_COLOR', 'POINT')
    ca.data.foreach_set("color", np.hstack([col, np.ones((len(col), 1))]).astype(np.float32).ravel())
    fm.materials.append(m)
    ob = bpy.data.objects.new(f"_x_{tag}", fm)
    export_col().objects.link(ob)
    return ob, max(dropped, LOD2_ERROR_M)

# ---------------- 4. glTF -> GLB packing ----------------
def ktx_args(role):
    """color: ETC1S/BasisLZ, sRGB (small, fine for albedo); normal: UASTC + RDO + Zstd (ETC1S block artefacts show
    in lighting); occlusion: ETC1S. Non-colour data gets --assign-tf linear: Blender tags every PNG it writes as
    sRGB, and without it ktx would 'convert' normals and AO to linear, i.e. corrupt them. RGB only: no material
    uses alpha. Mipmaps always: these textures tile and are seen far away."""
    fmt = ["--format", "R8G8B8_SRGB"] if role == "color" else ["--format", "R8G8B8_UNORM", "--assign-tf", "linear"]
    enc = (["--encode", "uastc", "--uastc-quality", "2", "--uastc-rdo", "--uastc-rdo-l", "1", "--zstd", "18"]
           if role == "normal" else ["--encode", "basis-lz", "--qlevel", "128", "--clevel", "2"])
    return fmt + enc + ["--generate-mipmap"]


def to_ktx2(png, role):
    """PNG bytes -> KTX2 bytes (Basis Universal), cached by source hash + encoder settings."""
    args = ktx_args(role)
    key = f"{sha(png)}_{role}_{sha(' '.join(args).encode())[:6]}"
    cached = os.path.join(KTX_CACHE, key + ".ktx2")
    if not os.path.exists(cached):
        os.makedirs(KTX_CACHE, exist_ok=True)
        src = os.path.join(KTX_CACHE, key + ".png")
        with open(src, 'wb') as f:
            f.write(png)
        subprocess.run([KTX_EXE, "create", *args, src, cached], check=True, capture_output=True)
        os.remove(src)
    return open(cached, 'rb').read()


def image_roles(j):
    """glTF image index -> 'color' | 'normal' | 'occlusion', from how the materials use it."""
    roles = {}
    def mark(ref, role):
        if ref is not None:
            roles[j["textures"][ref["index"]]["source"]] = role
    for m in j.get("materials", []):
        pbr = m.get("pbrMetallicRoughness", {})
        mark(pbr.get("baseColorTexture"), "color")
        mark(m.get("emissiveTexture"), "color")
        mark(m.get("normalTexture"), "normal")
        mark(m.get("occlusionTexture"), "occlusion")
        if pbr.get("metallicRoughnessTexture"):
            mark(pbr["metallicRoughnessTexture"], "occlusion")      # linear data, same encoding
    return roles


def pack_glb(gltf_path, shared_written, hidden=()):
    """Pack the exporter's .gltf + .bin into one GLB:
    - buffers: every buffer with data (uri) is concatenated into the GLB BIN chunk (buffer 0); data-less
      EXT_meshopt_compression fallback buffers are kept after it, and every reference is re-indexed;
    - images: KTX2 (KHR_texture_basisu, required, no PNG fallback). Shared library textures stay EXTERNAL
      under its <stem>-<rev> name (one file, one GPU texture for every asset); per-asset ones (AO) are embedded;
    - nodes named in `hidden` (LOD1+, leaves) get KHR_node_visibility {visible: false}."""
    base = os.path.dirname(gltf_path)
    j = json.load(open(gltf_path, encoding='utf8'))
    if hidden:
        nodes = {n.get("name"): n for n in j["nodes"]}
        for name in hidden:
            if "children" in nodes[name]:
                raise ValueError(f"{name}: only leaves may be hidden (the loader makes children inherit it)")
            nodes[name].setdefault("extensions", {})["KHR_node_visibility"] = {"visible": False}
        j["extensionsUsed"] = sorted(set(j.get("extensionsUsed", [])) | {"KHR_node_visibility"})
    bin_data, remap, rebase = bytearray(), {}, {}
    fallbacks = []
    for i, buf in enumerate(j.get("buffers", [])):
        if "uri" in buf:
            while len(bin_data) % 4:
                bin_data.append(0)
            remap[i], rebase[i] = 0, len(bin_data)
            bin_data += open(os.path.join(base, unquote(buf["uri"])), 'rb').read()
        else:
            fallbacks.append(i)
    for k, i in enumerate(fallbacks):
        remap[i], rebase[i] = 1 + k, 0
    for bv in j.get("bufferViews", []):
        old = bv["buffer"]; bv["buffer"] = remap[old]; bv["byteOffset"] = bv.get("byteOffset", 0) + rebase[old]
        mo = bv.get("extensions", {}).get("EXT_meshopt_compression")
        if mo:
            old = mo["buffer"]; mo["buffer"] = remap[old]; mo["byteOffset"] = mo.get("byteOffset", 0) + rebase[old]
    new_buffers = [{"byteLength": 0}] + [j["buffers"][i] for i in fallbacks]

    roles = image_roles(j)
    for idx, img in enumerate(j.get("images", [])):
        role = roles.get(idx, "color")
        if "uri" in img:
            src = os.path.normpath(os.path.join(base, unquote(img["uri"])))
            png = open(src, 'rb').read()
        else:
            v = j["bufferViews"][img["bufferView"]]
            png = bytes(bin_data[v["byteOffset"]:v["byteOffset"] + v["byteLength"]])
            src = None
        ktx = to_ktx2(png, role)
        img["mimeType"] = "image/ktx2"
        if src and os.path.normcase(src).startswith(TEX_DIR):    # shared library texture: external
            stem = os.path.splitext(os.path.basename(src))[0]
            name = texture_name(stem)
            publish(name, ktx)                                     # refused if this revision has other bytes
            shared_written.add(name)
            img.pop("bufferView", None); img["uri"] = name
        else:                                                      # per-asset (AO): embedded
            while len(bin_data) % 4:
                bin_data.append(0)
            j.setdefault("bufferViews", []).append({"buffer": 0, "byteOffset": len(bin_data), "byteLength": len(ktx)})
            bin_data += ktx
            img.pop("uri", None); img["bufferView"] = len(j["bufferViews"]) - 1
    for t in j.get("textures", []):
        t.setdefault("extensions", {})["KHR_texture_basisu"] = {"source": t.pop("source")}
    for key in ("extensionsUsed", "extensionsRequired"):
        j[key] = sorted(set(j.get(key, [])) | {"KHR_texture_basisu"})

    while len(bin_data) % 4:
        bin_data.append(0)
    new_buffers[0]["byteLength"] = len(bin_data)
    j["buffers"] = new_buffers
    js = json.dumps(j, separators=(",", ":")).encode('utf8')
    js += b" " * ((4 - len(js) % 4) % 4)
    total = 12 + 8 + len(js) + 8 + len(bin_data)
    return (struct.pack("<4sII", b"glTF", 2, total) + struct.pack("<I4s", len(js), b"JSON") + js
            + struct.pack("<I4s", len(bin_data), b"BIN\0") + bytes(bin_data))


# ---------------- 5. descriptor ----------------
def local_pose(root, o):
    m = root.matrix_world.inverted() @ o.matrix_world
    loc, rot, _ = m.decompose()
    return loc, rot


def collision_block(asset_id):
    R = collision_root(asset_id)
    if R is None:
        return None, None
    shapes, keys, colliders = [], {}, []
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for o in sorted((c for c in descendants(R) if "col_shape" in c), key=lambda c: c.name):
        t = o["col_shape"]
        if t == "box":
            dims = {"half": ghalf(o["col_half"])}
        elif t == "sphere":
            dims = {"radius": round(o["col_radius"], 5)}
        else:                                                     # cylinder / capsule, axis = local +Y (glTF)
            dims = {"radius": round(o["col_radius"], 5), "half_height": round(o["col_half_height"], 5)}
        key = (t, json.dumps(dims, sort_keys=True))
        if key not in keys:
            keys[key] = len(shapes); shapes.append({"type": t, **dims})
        loc, rot = local_pose(R, o)
        colliders.append({"shape": keys[key], "name": o.name[len(R.name) + 1:], "pos": gv(loc), "rot": gq(rot)})
        bb = [R.matrix_world.inverted() @ o.matrix_world @ Vector(c) for c in o.bound_box]
        for v in bb:
            lo = Vector(map(min, lo, v)); hi = Vector(map(max, hi, v))
    block = {"band_m": R.get("col_band_m"), "shapes": shapes, "colliders": colliders}
    return block, gaabb(lo, hi)


def descriptor(root, glb_name, me, materials, ao_res):
    aid = root["asset_id"]
    lo = Vector(map(min, *[v.co for v in me.vertices])) if me else Vector()
    hi = Vector(map(max, *[v.co for v in me.vertices])) if me else Vector()
    me.calc_loop_triangles()
    col, col_bounds = collision_block(aid)
    d = {
        "schema": SCHEMA, "id": aid, "kind": root.get("asset_kind", "prop"),
        "frame": "glTF: metres, +Y up, right-handed",
        "render": {"glb": glb_name, "triangles": len(me.loop_triangles), "materials": materials, "ao_px": ao_res},
        "bounds": {"render": gaabb(lo, hi), "collision": col_bounds},
        "collision": col, "portals": [], "spawns": [], "interactions": [], "children": [],
    }
    if root.get("asset_kind") == "building":
        d["foundation_depth"] = 1.0
    targets = {}                                                  # marker object name -> (object, its entry)
    for o in descendants(root):
        loc, rot = local_pose(root, o)
        if "portal_id" in o:
            key, e = "portals", {"id": o["portal_id"], "center": gv(loc), "rot": gq(rot), "half": ghalf(o["portal_half"]),
                                 "forward": FORWARD, "target": o["portal_target"], "target_spawn": o["portal_target_spawn"],
                                 "exit_spawn": bpy.data.objects[o["portal_exit_spawn"]]["spawn_id"]}
        elif "spawn_id" in o:
            key, e = "spawns", {"id": o["spawn_id"], "pos": gv(loc), "rot": gq(rot), "forward": FORWARD}
        elif "interact_kind" in o:
            key, e = "interactions", {"kind": o["interact_kind"], "pos": gv(loc), "rot": gq(rot), "forward": FORWARD,
                                      "radius": o.get("interact_radius")}
            if "interact_seat" in o:
                e["seat"] = o["interact_seat"]
        elif "seat_id" in o:
            key, e = "seats", {"id": o["seat_id"], "pos": gv(loc), "rot": gq(rot), "forward": FORWARD,
                               "pose": {"clip": o["seat_clip"], "at": o["seat_clip_at"]}}
        elif o.instance_type == 'COLLECTION' and "asset_ref" in o:
            key, e = "children", {"asset": o["asset_ref"], "pos": gv(loc), "rot": gq(rot)}
        else:
            continue
        d.setdefault(key, []).append(e)                           # seats[] only on assets that have one
        targets[o.name] = (o, e)
    for o in descendants(root):
        if o.type == 'CURVE' and "approach_for" in o:
            t, entry = targets.get(o["approach_for"], (None, None))
            if t is None or not ("portal_id" in t or "asset_ref" in t):
                raise ValueError(f"{o.name}: approach_for {o['approach_for']!r} is not a portal or child of {aid}")
            m = root.matrix_world.inverted() @ o.matrix_world
            path = [gv(m @ Vector(p.co[:3])) for s in o.data.splines for p in s.points]
            check_approach(o.name, d, path, entry, t, root)
            entry["approach"] = path
    return d


# ---------------- approach paths ----------------
# The player body as the server has it (TatooineData.PlayerBodyHalfM): an axis-aligned square in plan, half-extent
# 0.3 (placements turn by quarter turns, so asset-local axes stay world-aligned). Vertically the testbench motor's
# body: colliders lower than BODY_STEP above the feet are walked onto, the body ends at the descriptor capsule height.
BODY_HALF, BODY_STEP, BODY_HEIGHT = 0.3, 0.4, 1.65
APPROACH_SAMPLE_M = 0.05
APPROACH_PAST_M = 0.2       # a door path ends this far past the portal's mid-plane: the server fires on its crossing


def qmat(q):                                # glTF [x, y, z, w] -> 3x3 rotation, columns = local axes
    return np.array(Quaternion((q[3], q[0], q[1], q[2])).to_matrix())


def collider_obbs(col, pos=(0, 0, 0), rot=(0, 0, 0, 1)):
    """Descriptor collision block -> [(name, centre, axes, half)], placed by pos/rot. Round shapes as their box
    (conservative)."""
    P, R = np.array(pos), qmat(rot)
    out = []
    for c in col["colliders"]:
        s = col["shapes"][c["shape"]]
        if s["type"] == "box":
            h = s["half"]
        elif s["type"] == "sphere":
            h = [s["radius"]] * 3
        else:
            h = [s["radius"], s["half_height"] + (s["radius"] if s["type"] == "capsule" else 0.0), s["radius"]]
        cr = qmat(c["rot"])
        out.append((c["name"], P + R @ np.array(c["pos"]), R @ cr, np.array(h)))
    return out


def aabb_obb_overlap(ca, ha, cb, rb, hb, eps=1e-6):
    """Separating-axis test, axis-aligned box (ca, ha) against oriented box (cb, axes rb, hb)."""
    t = cb - ca
    ar = np.abs(rb) + 1e-9
    for i in range(3):
        if abs(t[i]) > ha[i] + ar[i] @ hb + eps:
            return False
    for j in range(3):
        if abs(t @ rb[:, j]) > ha @ ar[:, j] + hb[j] + eps:
            return False
    for i in range(3):
        i1, i2 = (i + 1) % 3, (i + 2) % 3
        for j in range(3):
            j1, j2 = (j + 1) % 3, (j + 2) % 3
            if abs(t[i2] * rb[i1, j] - t[i1] * rb[i2, j]) > (ha[i1] * ar[i2, j] + ha[i2] * ar[i1, j]
                                                            + hb[j1] * ar[i, j2] + hb[j2] * ar[i, j1] + eps):
                return False
    return True


def check_approach(name, d, path, entry, target, root):
    """Raises unless the path starts outside the collision footprint, ends at its target and the body clears every
    collider of the asset and its children along it."""
    if len(path) < 2:
        raise ValueError(f"{name}: an approach needs at least two points")
    (x0, _, z0), (x1, _, z1) = d["bounds"]["collision"]
    px, _, pz = path[0]
    if x0 - BODY_HALF < px < x1 + BODY_HALF and z0 - BODY_HALF < pz < z1 + BODY_HALF:
        raise ValueError(f"{name}: first point {path[0]} is inside the collision footprint (+{BODY_HALF})")
    end = np.array(path[-1])
    if "portal_id" in target:
        loc = qmat(entry["rot"]).T @ (end - np.array(entry["center"]))
        if abs(loc[0]) > entry["half"][0] or abs(loc[2]) > entry["half"][2]:
            raise ValueError(f"{name}: last point {path[-1]} is not inside portal {entry['id']!r}")
        if -loc[2] < APPROACH_PAST_M:                             # forward is local -Z
            raise ValueError(f"{name}: last point {path[-1]} is {-loc[2]:.3f} m past portal {entry['id']!r}'s "
                             f"mid-plane, needs {APPROACH_PAST_M}")
    else:
        src = target.instance_collection
        croot = next(o for o in src.objects if "asset_id" in o)
        stands = [o for o in src.objects if "interact_kind" in o]
        if len(stands) != 1:
            raise ValueError(f"{name}: child {croot['asset_id']} has {len(stands)} interactions, needs exactly one")
        m = (root.matrix_world.inverted() @ target.matrix_world @ Matrix.Translation(-src.instance_offset)
             @ stands[0].matrix_world)
        stand = np.array(gv(m.translation))
        if np.hypot(*(end - stand)[[0, 2]]) > 0.05:
            raise ValueError(f"{name}: last point {path[-1]} is not the stand point {stand.tolist()}")
    obbs = collider_obbs(d["collision"])
    for c in d["children"]:
        cc, _ = collision_block(c["asset"])
        if cc:
            obbs += collider_obbs(cc, c["pos"], c["rot"])
    ha = np.array([BODY_HALF, (BODY_HEIGHT - BODY_STEP) / 2, BODY_HALF])
    for a, b in zip(path, path[1:]):
        a, b = np.array(a), np.array(b)
        n = max(1, math.ceil(np.linalg.norm(b - a) / APPROACH_SAMPLE_M))
        for k in range(n + 1):
            f = a + (b - a) * (k / n)
            ca = f + [0.0, BODY_STEP + ha[1], 0.0]
            for cname, cb, rb, hb in obbs:
                if aabb_obb_overlap(ca, ha, cb, rb, hb):
                    raise ValueError(f"{name}: body at {np.round(f, 3).tolist()} hits collider {cname}")


# ---------------- driver ----------------
def export_asset(root, shared_written):
    aid = root["asset_id"]
    sc = bpy.data.scenes["Scene"]
    stage = os.path.join(STAGE_DIR, aid)
    shutil.rmtree(stage, ignore_errors=True); os.makedirs(stage)
    temps = {"mats": [], "images": []}
    kind = root.get("asset_kind")
    try:
        ob = build_render_mesh(root, sc)
        lights = lights_of(root, sc)
        occluders = [ground_plane(sc, FOUNDATION_D if kind == "building" else 0.0)] if kind != "interior" else []
        levels = []                                        # LOD1+: (object, error_m)
        with render_settings(sc, AO_DISTANCE.get(kind, AO_DISTANCE["default"])):
            # probe bake to drop faces no ray can reach (resting on/inside other parts, buried caps), then the final
            # unwrap + bake on what is left
            temp_mats, ao_img, culled, res = bake_ao(ob, aid, sc, occluders, stage, temps)
            if kind in LOD_KINDS:
                with bevels_disabled([o for o in descendants(root) if o.type == 'MESH']) as bevel_m:
                    lod1 = build_render_mesh(root, sc, "_LOD1")
                lod1.data.set_sharp_from_angle(angle=math.radians(30))   # LOD0's hard edges came from the bevels
                _, ao1, _, _ = bake_ao(lod1, f"{aid}_LOD1", sc, occluders, stage, temps, LOD1_AO_SCALE)
                lod2, far_m = far_lod(lod1, ao1, f"{aid}_LOD2", stage, temps)
                levels = [(lod1, bevel_m), (lod2, max(bevel_m, far_m))]
        for o in occluders:
            pm = o.data; bpy.data.objects.remove(o, do_unlink=True); bpy.data.meshes.remove(pm)

        meshes = [ob] + [o for o, _ in levels]
        for o in meshes:
            if UV0 in o.data.uv_layers:
                o.data.uv_layers.active = o.data.uv_layers[UV0]
                o.data.uv_layers[UV0].active_render = True
        select_only(sc, meshes + lights, ob)
        gltf_path = os.path.join(stage, f"{aid}.gltf")
        with in_scene(sc, meshes + lights, ob):
            bpy.ops.export_scene.gltf(filepath=gltf_path, export_format='GLTF_SEPARATE', use_selection=True,
                                      export_keep_originals=True, export_yup=True, export_apply=False,
                                      export_texcoords=True, export_normals=True, export_tangents=False,
                                      export_materials='EXPORT', export_lights=bool(lights), export_extras=False,
                                      # unitless: point W / 4pi, not x683 lm/W candela. The client renders at exposure
                                      # 1 with a sun of ~1.5, so physical candela (13,588 for a 250 W lamp) blows out
                                      export_import_convert_lighting_mode='COMPAT',
                                      export_meshopt_compression_enable=True,
                                      export_meshopt_extension='EXT_meshopt_compression')
        glb = pack_glb(gltf_path, shared_written, hidden=[o.name for o, _ in levels])
        glb_name = asset_name(aid, "glb")
        publish(glb_name, glb)
        d = descriptor(root, glb_name, ob.data, sorted({m.name.split(":", 1)[1] for m in temp_mats}), res)
        d["render"]["hidden_faces_removed"] = culled
        if levels:
            def tris(o):
                o.data.calc_loop_triangles()
                return len(o.data.loop_triangles)
            d["render"]["lods"] = [{"node": o.name, "triangles": tris(o), "error_m": math.ceil(e * 100 - 1e-6) / 100}
                                   for o, e in [(ob, 0.0)] + levels]
        return d
    finally:
        for o in list(export_col().objects):
            data = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if isinstance(data, bpy.types.Mesh) and data.users == 0:
                bpy.data.meshes.remove(data)
        for m in temps["mats"]:
            bpy.data.materials.remove(m)
        for img in temps["images"]:
            bpy.data.images.remove(img)


os.makedirs(OUT_DIR, exist_ok=True); os.makedirs(STAGE_DIR, exist_ok=True)
shared, entries, report = set(), {}, {}
with all_layer_collections_included():
    for root in asset_roots():
        aid = root["asset_id"]
        if ONLY and aid not in ONLY:
            continue
        d = export_asset(root, shared)
        desc_name = publish_descriptor(d)
        entries[aid] = (desc_name, d["render"]["glb"])
        report[aid] = {"glb": d["render"]["glb"], "tris": d["render"]["triangles"], "ao_px": d["render"]["ao_px"],
                       "colliders": len(d["collision"]["colliders"]) if d["collision"] else 0,
                       "shapes": len(d["collision"]["shapes"]) if d["collision"] else 0,
                       "portals": len(d["portals"]), "children": len(d["children"])}
cat = write_catalogue(entries, shared, merge_textures=bool(ONLY))
result = {"assets": report, "textures": sorted(shared), "catalogue_version": cat["version"]}
