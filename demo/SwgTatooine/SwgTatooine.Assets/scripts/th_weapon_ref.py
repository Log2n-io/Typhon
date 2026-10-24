import bpy, math, os, json  # TH_ROOT: the open .blend's root (MCP-run code has no __file__), else this script's
TH_ROOT = os.path.dirname(os.path.dirname(bpy.data.filepath or os.path.abspath(__file__)))
from mathutils import Vector, Matrix

# Held-weapon check for th_weapons.py: one character per weapon, imported from the character pack at the published
# scale, frozen at the frame of its weapon's attack clip where the right fist reaches furthest, the weapon instanced
# into the fist. Reference only: nothing here is exported. Re-runnable: it replaces what it made.
#
# The grip transform comes from the weapon's root (th_weapons.py: grip_joint, grip_pos_m, grip_basis), in the joint's
# frame (glTF node axes = Blender bone axes with the importer's BLENDER bone heuristic, checked against the inverse
# bind matrices). One transform per weapon fits every character: they share one skeleton.

PACK = os.path.join(TH_ROOT, "externals", "QuaterniusUltimateCharacterPack")
COL = "TatooineWeaponRef"
ROW = (-29.5, 40.0)                 # world: first character, the row runs along +X, characters face -Y
GAP = 1.3

# weapon root -> the outfit holding it
HOLDERS = [("ASSET_SwordVibroblade", "Ninja_Male.gltf"), ("ASSET_SwordCurved", "Kimono_Female.gltf"),
           ("ASSET_SwordRyyk", "Ninja_Sand.gltf"), ("ASSET_BlasterDL44", "Casual2_Male.gltf"),
           ("ASSET_BlasterDH17", "Soldier_Female.gltf"), ("ASSET_BlasterScout", "BlueSoldier_Male.gltf"),
           ("ASSET_Vibroknuckler", "Casual_Male.gltf")]

sc, vl = bpy.context.scene, bpy.context.view_layer
col = bpy.data.collections.get(COL)
if col is None:
    col = bpy.data.collections.new(COL)
    sc.collection.children.link(col)
for o in list(col.objects):
    bpy.data.objects.remove(o, do_unlink=True)
for a in [a for a in bpy.data.actions if a.get("th_weapon_ref")]:
    bpy.data.actions.remove(a)
for d in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials, bpy.data.images):
    for x in [x for x in d if x.users == 0 and x.get("th_weapon_ref")]:
        d.remove(x)

catalogue = json.load(open(os.path.join(TH_ROOT, "build", "assets", "catalogue.json"), encoding="utf8"))["assets"]


def pack_scale(file):
    aid = "".join("_" + ch.lower() if ch.isupper() and i else ch.lower() for i, ch in enumerate(file[:-5])).replace("__", "_")
    d = json.load(open(os.path.join(TH_ROOT, "build", "assets", catalogue[aid]["descriptor"]), encoding="utf8"))
    return d["source"]["scale"]


def layer_col(lc, name):
    if lc.collection.name == name:
        return lc
    for ch in lc.children:
        f = layer_col(ch, name)
        if f:
            return f


def fist_reach(arm, frame):
    sc.frame_set(frame)
    p = arm.matrix_world @ arm.pose.bones["Fist.R"].head
    return (p - arm.matrix_world.translation).xy.length


report, frame0, active0 = {}, sc.frame_current, vl.active_layer_collection
vl.active_layer_collection = layer_col(vl.layer_collection, COL)
try:
    for i, (wname, file) in enumerate(HOLDERS):
        w = bpy.data.objects[wname]
        before_o, before_a = set(bpy.data.objects), set(bpy.data.actions)
        before_d = {d: set(getattr(bpy.data, d)) for d in ("meshes", "armatures", "materials", "images")}
        bpy.ops.import_scene.gltf(filepath=os.path.join(PACK, file), bone_heuristic='BLENDER')
        new = [o for o in bpy.data.objects if o not in before_o]
        for d, s in before_d.items():
            for x in getattr(bpy.data, d):
                if x not in s:
                    x["th_weapon_ref"] = True
        acts = {a.name.split(".")[0]: a for a in bpy.data.actions if a not in before_a}
        for a in acts.values():
            a["th_weapon_ref"] = True
        arm = next(o for o in new if o.type == 'ARMATURE')
        for o in new:                                    # the pack's stray helper objects
            if o is not arm and o.parent is not arm:
                bpy.data.objects.remove(o, do_unlink=True)
        for o in [o for o in bpy.data.objects if o.parent is arm]:
            if o.name not in col.objects:
                for c in list(o.users_collection):
                    c.objects.unlink(o)
                col.objects.link(o)
        if arm.name not in col.objects:
            for c in list(arm.users_collection):
                c.objects.unlink(arm)
            col.objects.link(arm)
        short = file[:-5].replace("_", "")
        arm.name = f"WREF_{short}"
        s = pack_scale(file)
        arm.scale = (s, s, s)
        x, y = ROW[0] + i * GAP, ROW[1]
        hit, loc, *_ = sc.ray_cast(bpy.context.evaluated_depsgraph_get(), Vector((x, y, 100.0)), Vector((0, 0, -1)))
        arm.location = (x, y, loc.z if hit else 0.0)

        # freeze the attack clip at its furthest reach, then drop the animation (the pose stays)
        ad = arm.animation_data or arm.animation_data_create()
        for t in list(ad.nla_tracks):
            ad.nla_tracks.remove(t)
        clip, joint = w["weapon_clip"], w["grip_joint"]
        ad.action = acts[clip]
        if ad.action.slots:
            ad.action_slot = ad.action.slots[0]
        f0, f1 = (int(v) for v in ad.action.frame_range)
        best = max(range(f0, f1 + 1), key=lambda f: fist_reach(arm, f))
        sc.frame_set(best)
        ad.action = None

        # the weapon, instanced into the fist: parented to the joint, world pose = joint x grip x unscale
        vl.update()
        e = bpy.data.objects.new(f"WREF_{short}_{wname[6:]}", None)
        e.instance_type, e.instance_collection = 'COLLECTION', w.users_collection[0]
        e.empty_display_size = 0.05
        col.objects.link(e)
        e.parent, e.parent_type, e.parent_bone = arm, 'BONE', joint
        vl.update()
        basis = Matrix([w["grip_basis"][k:k + 3] for k in (0, 3, 6)]).to_4x4()
        e.matrix_world = (arm.matrix_world @ arm.pose.bones[joint].matrix @ Matrix.Scale(1 / s, 4)
                          @ Matrix.Translation(w["grip_pos_m"]) @ basis)
        report[wname[6:]] = {"holder": file, "clip": clip, "frame": best, "of": [f0, f1]}
finally:
    sc.frame_set(frame0)
    vl.active_layer_collection = active0

result = report
