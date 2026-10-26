"""The creatures (kind "creature", ASSET_FORMAT revision 6 once published): the one table both the scene size check
(th_creature_ref.py, in Blender) and the publisher read. Plain Python, no bpy: exec it.

Source: externals/QuaterniusEasyEnemyPack/<file> (Blender 2.79, CC0 1.0, its License.txt). Each file holds one
armature and one skinned mesh, flat colour materials (no UVs, no textures) and one action per clip, named
"<Creature>_<Clip>" (Snake_Angry reuses Snake's rig and names). Every source is 24 fps.

Per creature:
  file      the source .blend
  size      (metric, metres): the size Loic checks in the scene, measured on the rest pose after `yaw`, with the
            source's own transforms (Rat's armature carries 0.3955): "span" = largest horizontal extent, "height" =
            vertical extent. The publish scale is metres / measured, multiplied on top of the source's transforms.
            ("Snake", None) = take that creature's scale (same rig).
  yaw       degrees about up, turning the source to face Blender -Y = glTF +Z (every source faces -Y except the Wasp)
  clips     source clip suffix -> published clip name, and whether it loops
  roles     gameplay role -> published clip name. idle, walk, attack and death are required; run and jump optional
  flies     the idle/walk clips hold it in the air (the hover is in the animation; the origin stays on the ground)
  box       the collider, glTF frame, asset-local (origin = the rig's root on the ground, +Z front, feet at y = 0):
            [[minX, minY, minZ], [maxX, maxY, maxZ]], the bounds of every frame of the idle clip, rounded outwards to
            the centimetre. Derived from size and yaw: th_creature_ref.py recomputes it and reports any drift, re-run
            it after changing either.

Descriptor to publish (beyond the common fields): kind "creature", forward "+Z", collision {band_m: null, one box
collider "Body" at the box's centre}, animations {clips: [{name, duration, loop}], roles}, render.skinned/joints,
flies, source {pack, file, license, scale}.
"""

CREATURE_PACK = {"dir": "QuaterniusEasyEnemyPack", "name": "Quaternius Easy Animated Enemy Pack (Jan 2019)",
                 "license": "CC0 1.0"}

CREATURES = {
    "rat": {
        "file": "Rat.blend", "size": ("span", 1.2), "yaw": 0, "flies": False,
        "clips": {"Idle": True, "Walk": True, "Run": True, "Jump": False, "Attack": False, "Death": False},
        "roles": {"idle": "Idle", "walk": "Walk", "run": "Run", "jump": "Jump", "attack": "Attack", "death": "Death"},
        "box": [[-0.13, 0.00, -0.84], [0.13, 0.40, 0.36]],
    },
    "frog": {
        "file": "Frog.blend", "size": ("span", 0.9), "yaw": 0, "flies": False,
        # it has no walk: it hops, so the Jump clip is its walk cycle and loops
        "clips": {"Idle": True, "Jump": True, "Attack": False, "Death": False},
        "roles": {"idle": "Idle", "walk": "Jump", "jump": "Jump", "attack": "Attack", "death": "Death"},
        "box": [[-0.46, -0.02, -0.40], [0.45, 0.38, 0.45]],
    },
    "snake": {
        "file": "Snake.blend", "size": ("height", 1.0), "yaw": 0, "flies": False,
        "clips": {"Idle": True, "Walk": True, "Jump": False, "Attack": False, "Death": False},
        "roles": {"idle": "Idle", "walk": "Walk", "jump": "Jump", "attack": "Attack", "death": "Death"},
        "box": [[-0.20, -0.01, -0.43], [0.18, 1.05, 0.56]],
    },
    "snake_angry": {
        "file": "Snake_Angry.blend", "size": ("snake", None), "yaw": 0, "flies": False,
        "clips": {"Idle": True, "Walk": True, "Jump": False, "Attack": False, "Death": False},
        "roles": {"idle": "Idle", "walk": "Walk", "jump": "Jump", "attack": "Attack", "death": "Death"},
        "box": [[-0.20, -0.01, -0.43], [0.18, 1.06, 0.56]],
    },
    "spider": {
        "file": "Spider.blend", "size": ("span", 1.8), "yaw": 0, "flies": False,
        "clips": {"Idle": True, "Walk": True, "Jump": False, "Attack": False, "Death": False},
        "roles": {"idle": "Idle", "walk": "Walk", "jump": "Jump", "attack": "Attack", "death": "Death"},
        "box": [[-0.91, 0.00, -0.72], [0.90, 0.62, 0.83]],
    },
    "wasp": {
        "file": "Wasp.blend", "size": ("span", 1.2), "yaw": -90, "flies": True,
        # Flying is its idle and its walk; it bobs 0.19-1.63 m above its origin, and the box covers the bob
        "clips": {"Flying": True, "Attack": False, "Death": False},
        "roles": {"idle": "Flying", "walk": "Flying", "attack": "Attack", "death": "Death"},
        "box": [[-0.45, 0.18, -0.42], [0.45, 1.63, 0.87]],
    },
}
CREATURE_REQUIRED_ROLES = ("idle", "walk", "attack", "death")
