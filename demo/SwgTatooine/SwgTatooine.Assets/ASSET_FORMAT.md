# Asset format

The contract between the asset pipeline (`scripts/th_export.py`, `scripts/th_character.py`, `scripts/th_creature.py`)
and everything that reads `build/assets/`: SwgTatooine.Server (`AssetOrigin`, `AssetColliders`), SwgTatooine.Client and
the asset testbench.

**Format revision 7** (2026-10-23). Every change to what is published gets a revision and an entry in the
[changelog](#changelog) **before** it is published. A consumer that knows revision N reads the entries after N and
nothing else.

How it is produced and why it looks like this: `claude/design/SwgTatooine/design/asset-pipeline.md`. Where the bytes
live and how to get them: [README.md](README.md). This file only says what is there and how to read it.

---

## 1. Rules for readers

1. **`schema` is the breaking-change number**, carried by every JSON file and counted per kind of file: **2** for the
   catalogue, **1** for descriptors. It changes only when a field is removed, renamed, changes type or changes meaning.
   **Refuse a file whose `schema` you do not know.**
2. **Additive changes keep `schema`**: a new optional field, a new array, a new enum value. **Ignore fields you do not
   know.**
3. **Do not guess unknown enum values** (`kind`, `interactions[].kind`, `shapes[].type`, …): skip that entry and log
   it. Never fall back to a default meaning.
4. **Arrays have a fixed arity and order, listed here, forever.** A vec3 is 3 numbers, a quaternion 4, an AABB two
   vec3. New data is a new *named* field, never an extra element appended to an existing array.
5. **Absent and empty are the same** for optional arrays (`seats`, `approach`, `lods`): treat a missing one as `[]`.

## 2. Frames and units

- **Everything is in the glTF frame**, the same as the GLB: metres, +Y up, right-handed, and each descriptor's values
  are asset-local (origin = the asset origin; the placement's transform applies on top).
- **A left-handed reader** (Babylon) mirrors **every** position and rotation it reads, not only the meshes and
  colliders: portals, spawns, interactions, seats, children, approaches, nav. Mirroring part of it puts the doors at
  the mirror image of the drawn doorways.
- **Quaternions** are `[x, y, z, w]`.
- **Box half extents** are `[hx, hy, hz]` in the box's local axes.
- **Fronts:** buildings, vehicles and props face **+Z** (their nose / door side); characters declare it (`forward`).
- **Markers** (portals, spawns, interactions, seats) carry `"forward": "-Z"`. That names the marker's **LOCAL** axis,
  not a world direction: rotate it by the marker's `rot`, then by the placement. Interior portals have a 180° yaw, so
  their forward in the asset frame is +Z.
- **Placements turn by quarter turns** about +Y. Consumers rely on it (the player body is an axis-aligned square,
  asset-pipeline.md §8).

## 3. Files (`build/assets/`, one flat folder)

| File | Mutable | Content |
|---|---|---|
| `catalogue-<version>.json` | no | The published set. What a server pins and announces |
| `catalogue.json` | yes | A copy of the latest catalogue, for tools and the server's startup |
| `<asset_id>-<rev>.json` | no | Descriptor (section 5) |
| `<asset_id>-<rev>.glb` | no | Render (section 6) |
| `<texture>_<basecolor\|normal>-<rev>.ktx2` | no | Shared texture, referenced by URI from GLBs |
| `<file>.br` | no | Brotli-11 copy of every `.json` and `.glb` (not of `.ktx2`) |

`<rev>` is the revision `revisions.json` assigns to that asset or texture. Revisions are decided by a person and bumped
by hand; they are not derived from the bytes, because the build is not bit-reproducible. **A published name never gets
new bytes**: the publisher refuses to overwrite one, so every file can be cached `immutable`, and the catalogue's
`sha256` map records what each name was published with. Asset ids and texture stems are snake_case, so the `-` before
the revision is unambiguous. A file named in a catalogue exists before that catalogue does.

## 4. Catalogue

```json
{ "schema": 2,
  "version":  12,
  "assets":   { "<asset_id>": { "rev": 6, "descriptor": "<asset_id>-6.json", "glb": "<asset_id>-6.glb" } },
  "textures": [ "<texture>_<basecolor|normal>-1.ktx2" ],
  "sha256":   { "<file name>": "<64 hex digits>" } }
```

- `version` is a positive integer, the suffix of `catalogue-<version>.json`. It goes up by one each time the
  published set changes; a publish that changes nothing writes no new catalogue.
- `rev` is the asset's revision, the one in its file names.
- `textures` lists the shared textures any GLB of this catalogue references.
- `sha256` has one entry per descriptor, GLB and texture the catalogue names (not the `.br` copies): the SHA-256 of the
  bytes that name was published with. A reader may verify a file against it; the server and the client do not.

## 5. Descriptor

### 5.1 Common fields (every kind)

| Field | Type | Content |
|---|---|---|
| `schema` | int | 1 |
| `id` | string | Asset id (snake_case, = catalogue key) |
| `kind` | enum | `building`, `interior`, `interactable`, `prop`, `vehicle`, `character`, `creature` |
| `frame` | string | `"glTF: metres, +Y up, right-handed"`, informative |
| `render.glb` | string | GLB file name |
| `render.triangles` | int | LOD0 triangles |
| `render.materials` | string[] | Material names, one primitive (draw call) each at LOD0 |
| `render.ao_px` | int | Baked AO map size (not on characters or creatures) |
| `render.hidden_faces_removed` | int | Informative |
| `render.lods` | object[] | Optional. Levels, LOD0 first: `{node, triangles, error_m?}`. `node` is the GLB node name. `error_m` (buildings): upper bound of the level's deviation from LOD0 in metres; switch when `error_m × pixels_per_metre / distance` falls under ~1 px. Characters have no `error_m` yet |
| `bounds.render` | AABB | `[[minX, minY, minZ], [maxX, maxY, maxZ]]` of the LOD0 mesh |
| `bounds.collision` | AABB | Same, of the colliders |
| `collision` | object | Section 5.2 |
| `portals` | object[] | Section 5.3 |
| `spawns` | object[] | Section 5.4 |
| `interactions` | object[] | Section 5.5 |
| `children` | object[] | Section 5.6 |
| `nav` | object | Interiors: section 5.9 |

### 5.2 Collision

```json
"collision": { "band_m": 2.0,
               "shapes":    [ {"type": "box", "half": [hx, hy, hz]},
                              {"type": "sphere", "radius": r},
                              {"type": "cylinder", "radius": r, "half_height": h},
                              {"type": "capsule",  "radius": r, "half_height": h} ],
               "colliders": [ {"shape": 0, "name": "EntryRamp", "pos": [x, y, z], "rot": [x, y, z, w]} ] }
```

- Typed primitives only, deduplicated in `shapes`; each collider references one by index and places it.
- Cylinder and capsule axes are the shape's local +Y; a capsule's `half_height` excludes the end caps.
- A tilted slab (ramp, steps) is a rotated box: its walkable top is the box's local +Y face, not its AABB top.
- `band_m`: colliders exist only up to this height above the floor they stand on (2 m). `null` on characters and
  creatures.
- Characters: one capsule, feet at y = 0.
- Creatures: one box `Body`, axis-aligned in the asset frame: the bounds of every frame of the idle clip. A flier's
  box covers the bob of its hover, which its idle clip holds (the origin stays on the ground).

### 5.3 Portals

`{id, center, rot, half, forward, target, target_spawn, exit_spawn, approach?}`

- An oriented box (`center`, `rot`, `half`). **It fires on a directed crossing of its mid-plane** (the plane through
  `center` normal to `forward`), moving along `forward`, between two positions, within the box's extent. Not on
  containment: a running body crosses the 0.8 m box in one 10 Hz tick.
- The box stands 0.2 m off the facade (exteriors) or the inner door wall (interiors), so a 0.3 m body stopped by the
  wall is 0.3 m past the mid-plane.
- `target`: the asset id entered. `target_spawn`: the spawn id there. `exit_spawn`: this asset's spawn id where one
  arrives when coming back out.
- Today: `tat_house_small.front` ↔ `tat_house_small_interior.front`, `tat_house_large.front` ↔
  `tat_house_large_interior.front`. One portal per asset today; the server reads only the first, so a second door
  needs reader work.

### 5.4 Spawns

`{id, pos, rot, forward}`: where a body arrives, at feet height, facing `forward`.

- `creature_nest`: `creature.0` to `creature.3`, on the ground 1.2 m outside its rim, facing out: where the creatures it
  releases appear. Which creatures, how many and how often is gameplay, not asset data.

### 5.5 Interactions

`{kind, pos, rot, forward, radius, seat?}`: the stand point (feet height) a user occupies, facing `forward`, usable
within `radius` metres.

| `kind` | Asset | Meaning |
|---|---|---|
| `shuttle_ticket` | ticket_terminal | Opens the shuttle destination menu |
| `mount` | vehicles | Get on (and off) here. `seat`: the id of the `seats[]` entry it puts the user in |

### 5.6 Children

`{asset, pos, rot, approach?}`: another asset (by id) instantiated with this one at that pose. Its descriptor applies
in this frame.

### 5.7 Approach paths (`portals[].approach`, `children[].approach`)

`[[x, y, z], …]`, asset frame, feet height: how a bot reaches the portal, or the child's interaction. Every interior
portal has one: the way out.

1. The first point is on open ground, outside `bounds.collision` by at least 0.3 m. Reach it with open-ground
   navigation. (Whether it is free in the *world* depends on the neighbours: the placer checks that.)
   **Interiors:** the first point is one of `nav.nodes` (5.9). Reach it through the graph.
2. Walk the points in order (one per change of slope or heading).
3. The last point is:
   - **portal:** inside the box and at least 0.2 m past its mid-plane, so walking it fires the crossing;
   - **child:** the child's interaction stand point (exactly one interaction on that child).
4. `y` is informative: follow the ground.

The exporter refuses a path whose ends break 1 or 3, or along which a 0.3 m square body (0.4 m above the feet to
1.65 m) overlaps a collider of the asset or its children.

### 5.9 Interior navigation (`nav`)

How a bot anywhere inside an interior reaches any other point, the exit portal's approach first among them.
Interiors only; every interior has it. The floor is flat (`y` 0).

```json
"nav": { "nodes": [[x, y, z], …],
         "edges": [[i, j], …],
         "cells": {"origin": [x, z], "size": 0.5, "cols": C, "rows": R, "node": [n, …]} }
```

- `nodes`: waypoints, asset frame, feet height.
- `edges`: undirected, by node index. The body (the 5.7 one) walks straight between the two without touching a
  collider. The graph is connected. Cost = distance.
- `cells`: a grid over the floor in the x-z plane. Cell `(col, row)` spans `x` from `origin[0] + col × size`, `z`
  from `origin[1] + row × size`; its node is `node[row × cols + col]`. From **every** position in a cell where the
  body stands, walking straight to that node touches no collider (checked every 0.1 m). `-1`: no such position (wall,
  furniture, outside the walls).
- Grid positions outside the `cols × rows` range, and `-1` cells, are not navigable. A body at a `-1` cell stands
  against an obstacle near the cell's edge: use the nearest cell with a node.

To go from `a` to `b`:

1. Look up both cells: nodes `na` and `nb`.
2. Walk from `a` straight to `na`.
3. Walk the shortest edge path from `na` to `nb` (Dijkstra; the graphs have about a dozen nodes).
4. Walk from `nb` straight to `b`. Same cell guarantee, read backwards.

To leave: walk to the exit portal's `approach[0]` this way, then the approach itself. Its last point crosses the
portal.

The exporter refuses a node inside a collider, a disconnected graph, and a cell where the body stands but no node
serves every position.

### 5.8 Kind-specific fields

| Field | Kinds | Content |
|---|---|---|
| `foundation_depth` | building | 1.0: geometry continues this far below y = 0 so uneven terrain shows no gap |
| `seats` | vehicle | `[{id, pos, rot, forward, pose}]`. Put the rider's root at `pos`, turned so the character's own `forward` axis points along the seat's forward; it then moves with the vehicle. `pose`: `{clip, at}`, the character clip to show, `at: "end"` = its last frame, held |
| `forward` | character, creature | The local axis the model faces (`"+Z"`) |
| `animations` | character | `clips: [{name, duration (s), loop}]`; `roles: {role: clip name, or a list for attack}` (idle, walk, run, jump, roll, attack, hit, death, victory). Clip names are identical across the characters |
| `animations` | creature | `clips` as for characters; `roles: {role: clip name}`, always `idle`, `walk`, `attack`, `death`, sometimes `run`, `jump`. One clip may serve two roles (the frog hops: `walk` = `jump` = `Jump`; the wasp: `idle` = `walk` = `Flying`). Clip names are per creature |
| `render.skinned`, `render.joints` | character, creature | `true`; joint count (characters: 23, one skeleton shared by every character; creatures: one skeleton each, 15 to 39 joints) |
| `flies` | creature | `true` when its idle and walk clips hold it in the air (the hover is in the animation; the origin stays on the ground) |
| `source` | character, creature | `{pack, file, license, scale}`: provenance and the uniform scale applied |

## 6. GLB

- glTF 2.0 binary, one scene, the same frame as the descriptor.
- **Required extensions:** `EXT_meshopt_compression`, `KHR_texture_basisu` (no PNG fallback); interiors also
  `KHR_lights_punctual`; characters and creatures `KHR_mesh_quantization` (creatures have no texture, so no
  `KHR_texture_basisu`). Used, not required: `KHR_node_visibility`, `KHR_materials_emissive_strength`.
- **Nodes:** LOD0 is `_x_<asset_id>` (characters and creatures: `Body`). Extra levels are sibling leaf nodes named in `render.lods`,
  hidden on load with `KHR_node_visibility`, so a reader unaware of LODs shows LOD0 only.
- **Textures:** shared ones by relative URI, the `<texture>-<rev>.ktx2` files of the catalogue, same folder; the
  per-asset AO map is embedded, on texture coordinate set 1, as `occlusionTexture`. Base colour and normals use set 0.
- **Lights** (interiors): point lights, intensity in the exporter's unitless convention (watts / 4π).
- **Characters:** one skin, 23 joints; the animations are the descriptor's `clips`, by name; they play in place.
- **Creatures:** one skin, one primitive per flat-colour material (base colour factor, no texture, no AO); the root
  node carries the publish scale and turn; the animations are the descriptor's `clips`, by name; they play in place.

---

## Changelog

Newest first. *Additive*: readers may ignore it. *Semantic*: a value's meaning or a rule changed, same fields.
*Breaking*: `schema` changes. Each entry names the first catalogue carrying it and what a reader has to do.

### Revision 7: 2026-10-23, catalogue `4` (additive)
- `nav` on interiors (5.9): waypoint graph plus a cell lookup. Exit portals carry an `approach` that starts on a nav
  node (5.7). `tat_house_small_interior-5`, `tat_house_large_interior-5`.
- Reader: for bots that must move inside an interior or leave it, read `nav` and the exit portal's `approach`.
  Otherwise nothing.

### Revision 6: 2026-10-23, catalogue `3` (additive)
- Kind `creature` (5.1, 5.2, 5.8, 6): `rat`, `frog`, `snake`, `snake_angry`, `spider`, `wasp`. Skinned and animated
  like characters, but one skeleton and one clip set each, a box collider, creature roles and `flies`.
- Not a format change, also new: `creature_nest` (kind `prop`, catalogue `2`), with spawns `creature.0`-`3` (5.4).
- Reader: to show or simulate creatures, read kind `creature`. Otherwise nothing (rule 3: skip the unknown kind).

### Revision 5: 2026-10-23, catalogue `1` (breaking: catalogue `schema` 2)
- Files are named `<id>-<rev>.<ext>` instead of by content hash; the revisions come from `revisions.json` (3).
- The catalogue's `version` is a positive integer and its file is `catalogue-<version>.json`; the catalogue carries a
  `sha256` map (4). Descriptors are unchanged and stay at `schema` 1, except that `render.glb` holds the new name.
- Converted without re-exporting: catalogue `1` is catalogue `754a71ad6aab` under the new names, with the asset
  revisions kept and every shared texture at revision 1. GLBs changed only in the image URIs of their JSON chunk.
- Reader: refuse catalogue `schema` 1; build the immutable catalogue's name as `catalogue-<version>.json`.

### Revision 4: 2026-10-22, catalogue `754a71ad6aab` (additive)
- `seats[]` on vehicles (5.8); `interactions[].seat` and interaction kind `mount` (5.5). First vehicle with them:
  `swoop`.
- Reader: to support riding, read `seats` and `mount`. Otherwise nothing.

### Revision 3: 2026-10-22, catalogue `5ab1c2eab2fd`; interiors in `cdab4caba0cf` (semantic)
- Portal boxes moved 0.2 m off their wall (exteriors outwards, interiors into the room); the last point of a portal's
  `approach` is at least 0.2 m past the mid-plane. Same fields, new `center` values.
- Reader: fire portals on the directed mid-plane crossing (5.3), and rotate `forward` by the portal's `rot` (2).

### Revision 2: 2026-10-22, catalogue `2264fe087e26` (additive)
- `portals[].approach` and `children[].approach` (5.7).
- Reader: bots that walk to a door or terminal can follow them. Otherwise nothing.

### Revision 1: 2026-10-22, catalogue `f131e3e086ab` (additive)
- Buildings: `render.lods` with `error_m`, LOD nodes hidden with `KHR_node_visibility` (5.1, 6).
- Reader: nothing for LOD0; pick a level by projected error to use them.

### Revision 0: baseline, catalogue `dd7c11021d4b`
- Everything above that no later entry introduced.
