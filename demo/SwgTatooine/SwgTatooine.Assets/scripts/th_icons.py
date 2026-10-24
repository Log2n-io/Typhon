import bpy, os  # TH_ROOT: the open .blend's root, else this script's
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
import math
from mathutils import Vector, Matrix

# Asset icons (ASSET_FORMAT.md 3-4, catalogue "icons"): each listed asset rendered alone, RGBA PNG at every size of
# SIZES, published as <asset_id>_icon<size>-<rev>.png (rev from revisions.json "icons", bumped by hand like an asset's:
# renders are not bit-reproducible) and merged into the catalogue. Headless, never saves the .blend:
#   blender -b blender/tatooine.blend --python scripts/th_icons.py
#   TH_ICONS_ONLY=swoop,blaster_dl44 ... renders a subset (the others keep their published icons)
# Look: orthographic, transparent film, rendered at the icon size (Blender's own anti-aliasing, no downscale, no
# outline); camera-relative sun lights (strong key from top-left, weak fill, strong rim from behind) with the scene's sky
# dimmed, since its ambient light flattens the shading; AgX Punchy for contrast. A crop zooms on an asset's readable part
# where the whole would be too small at 32 px (the swoop: its rear, the nose clipped).

exec(open(os.path.join(TH_ROOT, "scripts", "th_publish.py")).read(), globals())   # publish, icon_name, write_catalogue
STAGE_DIR = os.path.join(ROOT_DIR, "build", "_stage", "icons")
SIZES = (32, 48)
MARGIN = 0.06                    # empty border, fraction of the framed extent
SKY = 0.15                       # world strength factor while rendering
# asset id -> (direction the camera looks along, Blender frame; camera roll in degrees; crop = the (start, end)
# fraction of the asset's horizontal extent in the image to frame, None for all of it)
ICONS = {
    "swoop": (Vector((1.0, 0.9, -0.55)), -40.0, (0.0, 0.6)),
    "sword_vibroblade": (Vector((1.0, 0.0, 0.0)), 45.0, None),     # weapons: side view of the grip frame (th_weapons.py)
    "blaster_dl44": (Vector((1.0, 0.0, 0.0)), 0.0, None),
    "vibroknuckler": (Vector((1.0, 0.0, 0.0)), 0.0, None),
}
LIGHTS = (("key", 6.0, (0.55, -0.55, -0.63)), ("fill", 0.7, (-0.75, 0.15, -0.65)), ("rim", 5.0, (-0.25, -0.55, 0.8)))
ONLY = os.environ.get("TH_ICONS_ONLY", "").split(",") if os.environ.get("TH_ICONS_ONLY") else None
if not bpy.app.background:
    raise RuntimeError("th_icons.py runs headless: blender -b blender/tatooine.blend --python scripts/th_icons.py")


def walk(lc):
    yield lc
    for c in lc.children:
        yield from walk(c)


def desc(o):
    for c in o.children:
        yield c
        yield from desc(c)


sc = bpy.data.scenes["Scene"]
for lc in walk(sc.view_layers[0].layer_collection):       # weapon collections are excluded (instanced only)
    lc.exclude = False
r = sc.render
r.engine = 'BLENDER_EEVEE'
r.resolution_percentage = 100
r.film_transparent = True
r.filter_size = 1.2
sc.eevee.taa_render_samples = 128
r.image_settings.file_format = 'PNG'; r.image_settings.color_mode = 'RGBA'; r.image_settings.color_depth = '8'
sc.view_settings.view_transform = 'AgX'; sc.view_settings.look = 'AgX - Punchy'
sc.view_settings.exposure = 0.3; sc.view_settings.gamma = 1.0
for n in (sc.world.node_tree.nodes if sc.world and sc.world.node_tree else []):
    if n.type == 'BACKGROUND':
        n.inputs['Strength'].default_value *= SKY

cam_d = bpy.data.cameras.new("_icon_cam"); cam_d.type = 'ORTHO'; cam_d.clip_start = 0.01; cam_d.clip_end = 500
cam = bpy.data.objects.new("_icon_cam", cam_d); sc.collection.objects.link(cam); sc.camera = cam
lights = []                                               # (object, direction it travels in camera space)
for name, energy, d in LIGHTS:
    ld = bpy.data.lights.new(f"_icon_{name}", 'SUN'); ld.energy = energy; ld.angle = math.radians(5)
    lo = bpy.data.objects.new(f"_icon_{name}", ld); sc.collection.objects.link(lo)
    lights.append((lo, Vector(d).normalized()))
roots = {o["asset_id"]: o for o in bpy.data.objects if "asset_id" in o}
os.makedirs(STAGE_DIR, exist_ok=True)

published, report = {}, {}
for aid, (view, roll, crop) in ICONS.items():
    if ONLY and aid not in ONLY:
        continue
    root = roots[aid]
    keep = {root} | set(desc(root))
    meshes = [o for o in keep if o.type == 'MESH']
    for o in sc.objects:
        o.hide_render = not (o in keep or o is cam or any(o is lo for lo, _ in lights))
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    centre = sum(pts, Vector()) / len(pts)
    rot = view.to_track_quat('-Z', 'Y').to_matrix().to_4x4() @ Matrix.Rotation(math.radians(roll), 4, 'Z')
    cam.matrix_world = Matrix.Translation(centre) @ rot @ Matrix.Translation((0, 0, 50.0))
    for lo, d in lights:
        lo.rotation_euler = (rot.to_3x3() @ d).to_track_quat('-Z', 'Y').to_euler()
    bpy.context.view_layer.update()
    inv = cam.matrix_world.inverted()
    dg = bpy.context.evaluated_depsgraph_get()
    vs = []
    for o in meshes:                                      # frame on the evaluated vertices in camera space
        e = o.evaluated_get(dg); me = e.to_mesh()
        vs += [inv @ (o.matrix_world @ v.co) for v in me.vertices]
        e.to_mesh_clear()
    xs = [v.x for v in vs]
    x0, x1 = min(xs), max(xs)
    if crop:
        x0, x1 = x0 + crop[0] * (x1 - x0), x0 + crop[1] * (x1 - x0)
        vs = [v for v in vs if x0 <= v.x <= x1]
    ys = [v.y for v in vs]
    y0, y1 = min(ys), max(ys)
    cam_d.ortho_scale = max(x1 - x0, y1 - y0) * (1 + 2 * MARGIN)
    cam_d.shift_x = (x0 + x1) / 2 / cam_d.ortho_scale
    cam_d.shift_y = (y0 + y1) / 2 / cam_d.ortho_scale
    files = {}
    for size in SIZES:
        r.resolution_x = r.resolution_y = size
        name = icon_name(aid, size)
        r.filepath = os.path.join(STAGE_DIR, name)
        bpy.ops.render.render(write_still=True)
        publish(name, open(r.filepath, 'rb').read())     # refused if this revision has other bytes
        files[size] = name
    published[aid] = files
    report[aid] = files

cat = write_catalogue({}, set(), merge_textures=True, icons=published)
result = {"icons": report, "catalogue_version": cat["version"]}
print("ICONS", result)
