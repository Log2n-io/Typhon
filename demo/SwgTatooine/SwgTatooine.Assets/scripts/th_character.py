"""Publish skinned, animated characters from external glTF packs into build/assets (asset-pipeline.md 4.4, claude/ docs).
Plain Python, no Blender (a Blender round trip would re-orient bones and resample the animations):
    python -I scripts/th_character.py [character_id ...]          (default: every entry of CHARACTERS)

Per character:
  1. the source .gltf is scaled by its PACK's scale (a uniform scale on the scene root node(s); nothing is baked):
     one reference model of the pack is brought to the body height, and every model of the pack gets the same factor,
     so bodies match and hats or hair stay on top instead of shrinking the person under them
  2. LODs (see LODS below): <node>_LOD1/_LOD2 skinned mesh nodes bound to the same skin, cut per primitive by
     meshoptimizer at one absolute error, hidden on load (KHR_node_visibility)
  3. gltfpack (meshoptimizer 1.3): EXT_meshopt_compression + KHR_mesh_quantization for geometry, skin and
     animations (resampled at 30 Hz); named nodes and materials are kept (-kn -km) so bones and clips stay addressable
  4. descriptor, kind "character": capsule collider (glTF frame, feet at y = 0, body height), forward axis, the clip
     list with durations and loop flags, the gameplay roles resolved to clip names (first candidate the model has), and
     render.lods [{node, triangles}] indexed by level
  5. published like every asset: <id>-<rev> GLB + descriptor (+ .br) at the revision of revisions.json, catalogue
     entry merged. Republishing a revision with different bytes is refused: bump it first
"""
import base64, fnmatch, json, os, re, shutil, struct, subprocess, sys, tempfile

TH_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
exec(open(os.path.join(TH_ROOT, "scripts", "th_publish.py")).read(), globals())

GLTFPACK = tool("gltfpack")                                                     # meshoptimizer 1.3
BODY_HEIGHT = 1.65            # metres, feet to top of the head, hats and hair excluded
CAPSULE_RADIUS = 0.3
LOOPING = {"Idle", "Walk", "Run", "Walk_Carry", "Run_Carry"}

# pack id -> folder, display name, license, reference model (bare head: its height IS the body height)
PACKS = {
    "quaternius_ucp": {
        "dir": os.path.join(ROOT_DIR, "externals", "QuaterniusUltimateCharacterPack"),
        "name": "Quaternius Ultimate Character Pack", "license": "CC0 1.0", "reference": "BaseCharacter.gltf",
        "forward": "+Z",              # Quaternius rigs face Blender -Y, i.e. glTF +Z
    },
}

# Gameplay role -> candidate clips, first present wins ("attack" takes every candidate present: the graph cycles them).
# Casual_Bald has no "Run": it runs with Run_Carry. Optional roles (victory) are left out when no candidate exists.
ROLE_CANDIDATES = {
    "idle": ["Idle"], "walk": ["Walk"], "run": ["Run", "Run_Carry"], "jump": ["Jump"], "roll": ["Roll"],
    "attack": ["Punch", "SwordSlash"], "hit": ["RecieveHit"], "death": ["Death"], "victory": ["Victory"],
}
OPTIONAL_ROLES = {"victory"}

# Which files of each pack are published, by glob; the asset id is the file stem in snake_case. This list is the
# source of truth: a full run drops from the catalogue any character no longer selected.
SELECTION = {
    "quaternius_ucp": ["BaseCharacter.gltf", "BlueSoldier*.gltf", "Casual*.gltf", "Kimono*.gltf", "Ninja*.gltf",
                       "Soldier*.gltf"],
}
ID_OVERRIDES = {"BaseCharacter.gltf": "base_character"}


def asset_id(file):
    if file in ID_OVERRIDES:
        return ID_OVERRIDES[file]
    stem = os.path.splitext(file)[0]
    return re.sub(r"(?<=[a-z0-9])(?=[A-Z])", "_", stem).lower()


def characters():
    out = {}
    for pack, globs in SELECTION.items():
        files = sorted(os.listdir(PACKS[pack]["dir"]))
        for g in globs:
            for f in fnmatch.filter(files, g):
                out.setdefault(asset_id(f), {"pack": pack, "file": f})
    return out


CHARACTERS = characters()


def bounds(j):
    acc = j["accessors"]
    prims = [p for m in j["meshes"] for p in m["primitives"]]
    lo = [min(acc[p["attributes"]["POSITION"]]["min"][k] for p in prims) for k in range(3)]
    hi = [max(acc[p["attributes"]["POSITION"]]["max"][k] for p in prims) for k in range(3)]
    return lo, hi


def pack_scale(pack):
    p = PACKS[pack]
    lo, hi = bounds(json.load(open(os.path.join(p["dir"], p["reference"]), encoding="utf8")))
    return BODY_HEIGHT / (hi[1] - lo[1])


def resolve_roles(cid, names):
    roles = {}
    for role, cands in ROLE_CANDIDATES.items():
        present = [c for c in cands if c in names]
        if not present:
            if role in OPTIONAL_ROLES:
                continue
            raise ValueError(f"{cid}: no clip for role {role} among {cands}")
        roles[role] = present if role == "attack" else present[0]
    return roles


def glb_json(b):
    n = struct.unpack("<I", b[12:16])[0]
    return json.loads(b[20:20 + n])


# --- LODs -------------------------------------------------------------------------------------------------------------
# Extra skinned mesh nodes <node>_LOD<n> beside the LOD0 node, bound to the same skin (same joints, inverse bind
# matrices and clips), so one baked bone texture serves every level. Each level is cut by meshoptimizer (gltfpack -si/-se)
# per primitive at ONE absolute error for the whole character (gltfpack's error is relative to the primitive's extents,
# so it is rescaled per primitive): dense parts (hair) lose most, the body least, and the error is searched until the
# total fits the budget. Edge collapse only moves vertices onto existing ones, so weights are the source weights.
# Permissive mode (-sp) is required: the packs are flat-shaded, every edge is a normal seam.
LODS = [{"triangles": 1500, "cap": 0.7}, {"triangles": 400, "cap": 0.2}]   # LOD1, LOD2: budget, or cap x LOD0 if lower
LOD_ERROR_SCALE = {"Hair": 2.0}     # material -> error multiplier: what may lose detail first
LOD_KEEP = 64       # primitives this small stay whole: thin rings (belts, bands) collapse to nothing and open the seams
                    # they cover
COMPONENT = {5120: "b", 5121: "B", 5122: "h", 5123: "H", 5125: "I", 5126: "f"}     # struct codes
WIDTH = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def gltf_buffers(j, base):
    out = []
    for b in j["buffers"]:
        u = b["uri"]
        out.append(base64.b64decode(u.split(",", 1)[1]) if u.startswith("data:") else
                   open(os.path.join(base, u), "rb").read())
    return out


def read_accessor(j, bufs, ai):
    """(rows, componentType, type, normalized): rows as a list of tuples (plain Python, no numpy needed)."""
    a = j["accessors"][ai]
    bv = j["bufferViews"][a["bufferView"]]
    row = struct.Struct("<" + COMPONENT[a["componentType"]] * WIDTH[a["type"]])
    stride = bv.get("byteStride", row.size)
    off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    buf = bufs[bv["buffer"]]
    rows = [row.unpack_from(buf, off + i * stride) for i in range(a["count"])]
    return rows, a["componentType"], a["type"], a.get("normalized", False)


def skinned_node(j):
    nodes = [i for i, n in enumerate(j["nodes"]) if "mesh" in n and "skin" in n]
    if len(nodes) != 1:
        raise ValueError(f"expected one skinned mesh node, found {len(nodes)}")
    return nodes[0]


def simplify_primitive(j, prim, error, tmp):
    """The primitive cut by gltfpack at a relative error: {attribute: accessor rows/meta}, or None when it vanished."""
    k = {key: v for key, v in j.items() if key != "animations"}
    k["meshes"] = [dict(m) for m in j["meshes"]]
    k["meshes"][j["nodes"][skinned_node(j)]["mesh"]]["primitives"] = [prim]
    inp, out = os.path.join(tmp, "lod_in.gltf"), os.path.join(tmp, "lod_out.gltf")
    with open(inp, "w", encoding="utf8") as f:
        json.dump(k, f)
    subprocess.run([GLTFPACK, "-i", inp, "-o", out, "-si", "0.001", "-se", f"{min(error, 1.0):.6f}", "-sp",
                    "-noq", "-kn", "-km"], check=True, capture_output=True)
    o = json.load(open(out, encoding="utf8"))
    if "meshes" not in o:
        return None
    ob = gltf_buffers(o, tmp)
    node = o["nodes"][skinned_node(o)]
    src_joints = [j["nodes"][x]["name"] for x in j["skins"][j["nodes"][skinned_node(j)]["skin"]]["joints"]]
    if [o["nodes"][x]["name"] for x in o["skins"][node["skin"]]["joints"]] != src_joints:
        raise ValueError("gltfpack reordered the skin joints")
    p = o["meshes"][node["mesh"]]["primitives"][0]
    data = {name: read_accessor(o, ob, ai) for name, ai in p["attributes"].items()}
    data["indices"] = read_accessor(o, ob, p["indices"])
    return data


def lod_primitives(j, bufs, target, tmp):
    """Every primitive cut at one absolute error, the smallest error whose total fits target: [(material, data)], tris."""
    prims = j["meshes"][j["nodes"][skinned_node(j)]["mesh"]]["primitives"]
    scale = []
    for p in prims:
        pos = read_accessor(j, bufs, p["attributes"]["POSITION"])[0]
        extent = max(max(v[k] for v in pos) - min(v[k] for v in pos) for k in range(3))
        scale.append(LOD_ERROR_SCALE.get(j["materials"][p["material"]]["name"], 1.0) / extent)

    def cut(err):
        out = [(p["material"], simplify_primitive(j, p, 0.0 if j["accessors"][p["indices"]]["count"] <= 3 * LOD_KEEP
                                                  else err * s, tmp)) for p, s in zip(prims, scale)]
        out = [(m, d) for m, d in out if d is not None and d["indices"][0]]
        return out, sum(len(d["indices"][0]) // 3 for _, d in out)

    lo, hi = 1e-4, 0.5                  # absolute error, source units (bodies are ~3 units tall before the pack scale)
    for _ in range(14):
        mid = (lo * hi) ** 0.5
        if cut(mid)[1] > target:
            lo = mid
        else:
            hi = mid
    return cut(hi)


def regroup_lods(glb, expected):
    """gltfpack drops the names of skinned mesh nodes and regroups their primitives into meshes as it likes (identical
    primitives of two levels get shared, a level may be split over several nodes). Each level has at most one primitive
    per material, so every packed primitive is assigned back to the level whose (material, triangles) it matches, and
    each level becomes one named mesh node again. Only the JSON chunk changes: accessors and the BIN chunk are untouched.
    expected: [(node name, {material name: triangles})], LOD0 first."""
    g = glb_json(glb)
    tris = lambda p: g["accessors"][p["indices"]]["count"] // 3
    old = [i for i, n in enumerate(g["nodes"]) if "mesh" in n]
    keys = ("translation", "rotation", "scale", "skin")
    ref = {k: g["nodes"][old[0]][k] for k in keys if k in g["nodes"][old[0]]}
    if any({k: g["nodes"][i][k] for k in keys if k in g["nodes"][i]} != ref for i in old) or "skin" not in ref:
        raise ValueError("packed mesh nodes differ in transform or skin; their primitives cannot be regrouped")
    groups = [{} for _ in expected]
    for i in old:
        for p in g["meshes"][g["nodes"][i]["mesh"]]["primitives"]:
            m = g["materials"][p["material"]]["name"]
            level = next((lv for lv, (_, sig) in enumerate(expected) if sig.get(m) == tris(p) and m not in groups[lv]),
                         None)
            if level is None:
                raise ValueError(f"packed primitive {m} ({tris(p)} triangles) matches no LOD of {expected}")
            groups[level][m] = p
    for (name, sig), grp in zip(expected, groups):
        if set(grp) != set(sig):
            raise ValueError(f"{name}: packed primitives {sorted(grp)} instead of {sorted(sig)}")

    # new nodes beside the old ones (same parent, or scene roots), then the old ones go and indices are compacted
    parent = next((n for n in g["nodes"] if old[0] in n.get("children", [])), None)
    for level, ((name, _), grp) in enumerate(zip(expected, groups)):
        g["meshes"].append({"name": name, "primitives": list(grp.values())})
        g["nodes"].append(dict(ref, name=name, mesh=len(g["meshes"]) - 1))
        if level:       # LOD1+ load hidden (Babylon's built-in loader honours it), so clients unaware of LODs see LOD0
            g["nodes"][-1]["extensions"] = {"KHR_node_visibility": {"visible": False}}
        (parent["children"] if parent else g["scenes"][g.get("scene", 0)]["nodes"]).append(len(g["nodes"]) - 1)
    if any(g["nodes"][i].get("children") for i in old):
        raise ValueError("packed mesh nodes have children")
    remap = {}
    for i in range(len(g["nodes"])):
        if i not in old:
            remap[i] = len(remap)
    g["nodes"] = [n for i, n in enumerate(g["nodes"]) if i in remap]
    for n in g["nodes"]:
        if "children" in n:
            n["children"] = [remap[c] for c in n["children"] if c in remap]
    for sc in g["scenes"]:
        sc["nodes"] = [remap[c] for c in sc["nodes"] if c in remap]
    for sk in g.get("skins", []):
        sk["joints"] = [remap[c] for c in sk["joints"]]
        if "skeleton" in sk:
            sk["skeleton"] = remap[sk["skeleton"]]
    for an in g.get("animations", []):
        for ch in an["channels"]:
            ch["target"]["node"] = remap[ch["target"]["node"]]
    used = sorted({n["mesh"] for n in g["nodes"] if "mesh" in n})
    g["meshes"] = [g["meshes"][m] for m in used]
    for n in g["nodes"]:
        if "mesh" in n:
            n["mesh"] = used.index(n["mesh"])

    if len(expected) > 1:
        g["extensionsUsed"] = sorted(set(g.get("extensionsUsed", [])) | {"KHR_node_visibility"})
    chunk = json.dumps(g, separators=(",", ":")).encode()
    chunk += b" " * (-len(chunk) % 4)
    rest = glb[20 + struct.unpack("<I", glb[12:16])[0]:]
    return struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(chunk) + len(rest)) +         struct.pack("<II", len(chunk), 0x4E4F534A) + chunk + rest


def add_lods(j, levels):
    """Append each level as a mesh + node <node>_LOD<n> (same skin, same parent as the LOD0 node) in a new buffer."""
    base = skinned_node(j)
    parent = next(i for i, n in enumerate(j["nodes"]) if base in n.get("children", []))
    blob = bytearray()

    def accessor(rows, ctype, atype, normalized, target):
        while len(blob) % 4:
            blob.append(0)
        fmt = "<" + COMPONENT[ctype] * WIDTH[atype]
        start = len(blob)
        for r in rows:
            blob.extend(struct.pack(fmt, *r))
        j["bufferViews"].append({"buffer": len(j["buffers"]), "byteOffset": start, "byteLength": len(blob) - start,
                                 "target": target})
        a = {"bufferView": len(j["bufferViews"]) - 1, "componentType": ctype, "count": len(rows), "type": atype}
        if normalized:
            a["normalized"] = True
        if atype == "VEC3" and target == 34962:
            a["min"] = [min(r[k] for r in rows) for k in range(3)]
            a["max"] = [max(r[k] for r in rows) for k in range(3)]
        j["accessors"].append(a)
        return len(j["accessors"]) - 1

    names = []
    for level, prims in enumerate(levels, start=1):
        mesh = {"name": f"{j['nodes'][base]['name']}_LOD{level}", "primitives": []}
        for material, d in prims:
            indices = accessor(*d.pop("indices"), target=34963)
            attrs = {name: accessor(*v, target=34962) for name, v in d.items()}
            mesh["primitives"].append({"attributes": attrs, "material": material, "indices": indices})
        j["meshes"].append(mesh)
        j["nodes"].append({"name": mesh["name"], "mesh": len(j["meshes"]) - 1, "skin": j["nodes"][base]["skin"]})
        j["nodes"][parent]["children"].append(len(j["nodes"]) - 1)
        names.append(mesh["name"])
    j["buffers"].append({"byteLength": len(blob),
                         "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()})
    return names


def build(cid, spec):
    pack = PACKS[spec["pack"]]
    src = os.path.join(pack["dir"], spec["file"])
    j = json.load(open(src, encoding="utf8"))
    acc = j["accessors"]
    lo, hi = bounds(j)
    s = pack_scale(spec["pack"])
    clips = [{"name": a["name"], "duration": round(max(acc[x["input"]]["max"][0] for x in a["samplers"]), 4),
              "loop": a["name"] in LOOPING} for a in j.get("animations", [])]
    names = {c["name"] for c in clips}
    roles = resolve_roles(cid, names)

    tmp = tempfile.mkdtemp(prefix="th_char_")
    try:
        # LODs are cut from the unscaled source, then appended; the pack scale goes on the root like for LOD0
        bufs = gltf_buffers(j, pack["dir"])
        n0 = sum(acc[p["indices"]]["count"] // 3 for p in j["meshes"][j["nodes"][skinned_node(j)]["mesh"]]["primitives"])
        levels = [lod_primitives(j, bufs, min(lv["triangles"], int(lv["cap"] * n0)), tmp)[0] for lv in LODS]
        base = j["nodes"][skinned_node(j)]
        expected = [(base["name"], {j["materials"][p["material"]]["name"]: acc[p["indices"]]["count"] // 3
                                    for p in j["meshes"][base["mesh"]]["primitives"]})]
        sigs = [{j["materials"][m]["name"]: len(d["indices"][0]) // 3 for m, d in prims} for prims in levels]
        expected += list(zip(add_lods(j, levels), sigs))
        lod_nodes = [name for name, _ in expected]
        for i in j["scenes"][j.get("scene", 0)]["nodes"]:
            node = j["nodes"][i]
            node["scale"] = [c * s for c in node.get("scale", [1, 1, 1])]
        scaled, out = os.path.join(tmp, "scaled.gltf"), os.path.join(tmp, "out.glb")
        with open(scaled, "w", encoding="utf8") as f:
            json.dump(j, f)
        subprocess.run([GLTFPACK, "-i", scaled, "-o", out, "-cc", "-kn", "-km"], check=True, capture_output=True)
        glb = regroup_lods(open(out, "rb").read(), expected)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    g = glb_json(glb)
    packed = sorted(a["name"] for a in g.get("animations", []))
    if packed != sorted(names):
        raise ValueError(f"{cid}: gltfpack changed the clip set: {packed}")
    gacc = g["accessors"]
    node_tris = {n["name"]: sum(gacc[p["indices"]]["count"] // 3 for p in g["meshes"][n["mesh"]]["primitives"])
                 for n in g["nodes"] if "mesh" in n}
    if sorted(node_tris) != sorted(lod_nodes):
        raise ValueError(f"{cid}: gltfpack changed the LOD nodes: {sorted(node_tris)}")
    if {n.get("skin") for n in g["nodes"] if "mesh" in n} != {0} or len(g.get("skins", [])) != 1:
        raise ValueError(f"{cid}: LOD nodes no longer share one skin")
    tris = node_tris[lod_nodes[0]]
    h, r = BODY_HEIGHT, CAPSULE_RADIUS
    glb_name = asset_name(cid, "glb")
    publish(glb_name, glb)
    d = {
        "schema": SCHEMA, "id": cid, "kind": "character",
        "frame": "glTF: metres, +Y up, right-handed",
        "forward": pack["forward"],
        "render": {"glb": glb_name, "triangles": tris, "materials": sorted(m.get("name", "") for m in g.get("materials", [])),
                   "skinned": True, "joints": sum(len(sk["joints"]) for sk in g.get("skins", [])),
                   # index = level; every node is bound to the one skin, the client shows one level at a time
                   "lods": [{"node": n, "triangles": node_tris[n]} for n in lod_nodes]},
        "bounds": {"render": [[round(lo[k] * s, 4) for k in range(3)], [round(hi[k] * s, 4) for k in range(3)]],
                   "collision": [[-r, 0.0, -r], [r, h, r]]},
        "collision": {"band_m": None, "shapes": [{"type": "capsule", "radius": r, "half_height": round(h / 2 - r, 5)}],
                      "colliders": [{"shape": 0, "name": "Body", "pos": [0.0, h / 2, 0.0], "rot": [0.0, 0.0, 0.0, 1.0]}]},
        "animations": {"clips": clips, "roles": roles},
        "source": {"pack": pack["name"], "file": spec["file"], "license": pack["license"], "scale": round(s, 6)},
        "portals": [], "spawns": [], "interactions": [], "children": [],
    }
    return d, glb_name, len(glb)


if __name__ == "__main__":
    ids = sys.argv[1:] or list(CHARACTERS)
    entries, report = {}, {}
    for cid in ids:
        d, glb_name, size = build(cid, CHARACTERS[cid])
        entries[cid] = (publish_descriptor(d), glb_name)
        report[cid] = {"glb": glb_name, "bytes": size, "triangles": d["render"]["triangles"],
                       "scale": d["source"]["scale"], "clips": len(d["animations"]["clips"])}
    # A full run makes the catalogue's characters exactly the selection: published characters not selected anymore go.
    stale = []
    if not sys.argv[1:]:
        for aid, e in read_catalogue().get("assets", {}).items():
            d = json.load(open(os.path.join(OUT_DIR, e["descriptor"]), encoding="utf8"))
            if d.get("kind") == "character" and aid not in CHARACTERS:
                stale.append(aid)
    cat = write_catalogue(entries, set(), merge_textures=True, remove=stale)
    report["_removed"] = sorted(stale)
    print(json.dumps({"characters": report, "catalogue_version": cat["version"]}, indent=1))
