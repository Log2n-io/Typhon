import hashlib, json, os

# Publishing into build/assets (ASSET_FORMAT.md 3-4): <name>-<rev>.<ext> files and the catalogue merge.
# Plain Python, no bpy: exec'd by th_export.py (inside Blender) and th_character.py (system Python), which set TH_ROOT.
# The .br sidecars are not written here: Blender's Python has no brotli module, so `scripts/assets.py compress`
# (system Python) writes them, and `assets.py release published` runs it first.
#
# Revisions are DECIDED, not derived: revisions.json lists every asset and shared texture with the revision to publish,
# and only a person bumps it. Two facts make that the only safe rule:
#   - the build is not bit-reproducible (1-ulp float noise in modifier evaluation, Cycles AO noise), so a name derived
#     from bytes would change on every export of an unchanged asset;
#   - every published name is served `immutable`, so a name must never get new bytes either: publish() refuses to
#     overwrite a file with different content. Re-exporting an asset without bumping it fails there, before any cache
#     can hold the wrong bytes. The catalogue's sha256 map records what each name was published with.

ROOT_DIR = TH_ROOT                     # set by the caller before exec
OUT_DIR = os.path.join(ROOT_DIR, "build", "assets")
REVISIONS = os.path.join(ROOT_DIR, "revisions.json")
SCHEMA = 1                             # descriptors
CATALOGUE_SCHEMA = 2                   # 2: <name>-<rev> files, integer version, sha256 map
POINTER = "catalogue.json"


class Unbumped(Exception):
    """A published name was about to get new bytes."""


def sha(data):
    """Short content hash, for cache keys (th_export's KTX cache). Never a published name."""
    return hashlib.sha256(data).hexdigest()[:12]


def revision(kind, key):
    """The revision revisions.json assigns to an asset ("assets") or a shared texture stem ("textures")."""
    revs = json.load(open(REVISIONS, encoding="utf8"))[kind]
    if key not in revs:
        raise KeyError(f'"{key}" has no revision in revisions.json: add it under "{kind}" (a new one starts at 1)')
    return revs[key]


def asset_name(aid, ext):
    return f"{aid}-{revision('assets', aid)}.{ext}"


def texture_name(stem):
    return f"{stem}-{revision('textures', stem)}.ktx2"


def icon_name(aid, size):
    """An asset's icon at one size: <asset_id>_icon<size>-<rev>.png, rev from revisions.json "icons"."""
    return f"{aid}_icon{size}-{revision('icons', aid)}.png"


def tool(name):
    """A pipeline tool fetched by `scripts/assets.py fetch tools` for this platform: tools/<name>/bin/<name>[.exe]."""
    path = os.path.join(ROOT_DIR, "tools", name, "bin", name + (".exe" if os.name == "nt" else ""))
    if not os.path.isfile(path):
        raise FileNotFoundError(f"{path} is missing: run `python scripts/assets.py fetch tools`")
    return path


def _write(path, data):
    with open(path, 'wb') as f:
        f.write(data)


def publish(name, data):
    """Write an immutable file of the published set (build/assets).
    The same bytes under the same name again is a no-op; different bytes is refused."""
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, name)
    if os.path.exists(path):
        if open(path, 'rb').read() == data:
            return
        raise Unbumped(f"{name} is already published with different bytes. A published name never changes: bump its "
                       f"revision in revisions.json (for a texture, also bump and re-export every asset that uses it).")
    _write(path, data)


def publish_descriptor(d):
    """Descriptor dict -> <id>-<rev>.json; returns the file name."""
    name = asset_name(d["id"], "json")
    publish(name, json.dumps(d, indent=2).encode('utf8'))
    return name


def read_catalogue():
    cat_path = os.path.join(OUT_DIR, POINTER)
    return json.load(open(cat_path, encoding='utf8')) if os.path.exists(cat_path) else {"assets": {}, "textures": []}


def write_catalogue(entries, shared, merge_textures, remove=(), icons=None):
    """Merge {asset_id: (descriptor, glb)} into the catalogue, at the revisions of revisions.json.
    merge_textures: keep the previous texture list (partial publish) instead of replacing it with `shared`.
    remove: asset ids to drop, with their icons (their files become orphans for th_gc_assets.py).
    icons: {asset_id: {size: file name}} to merge (th_icons.py); the previous icons are kept otherwise.
    A catalogue that changed gets the next version, published as catalogue-<version>.json (immutable: what servers pin
    and clients fetch); catalogue.json is the mutable pointer to it, for tools and the server's startup."""
    prev = read_catalogue()
    assets = {k: v for k, v in prev.get("assets", {}).items() if k not in set(remove)}
    for aid, (desc_name, glb_name) in entries.items():
        assets[aid] = {"rev": revision("assets", aid), "descriptor": desc_name, "glb": glb_name}
    textures = sorted(set(prev.get("textures", [])) | shared if merge_textures else shared)
    icon_map = {k: v for k, v in prev.get("icons", {}).items() if k in assets}
    for aid, files in (icons or {}).items():
        if aid not in assets:
            raise KeyError(f"icon for {aid}: the asset is not in the catalogue")
        icon_map[aid] = {"rev": revision("icons", aid), "png": {str(k): v for k, v in sorted(files.items())}}
    names = ([a[k] for a in assets.values() for k in ("descriptor", "glb")] + textures
             + [f for i in icon_map.values() for f in i["png"].values()])
    digests = {n: hashlib.sha256(open(os.path.join(OUT_DIR, n), 'rb').read()).hexdigest() for n in sorted(names)}
    content = {"assets": dict(sorted(assets.items())), "textures": textures, "sha256": digests}
    if icon_map:                                       # absent when there are none: a catalogue without icons is unchanged
        content["icons"] = dict(sorted(icon_map.items()))
    same = prev.get("schema") == CATALOGUE_SCHEMA and all(prev.get(k) == v for k, v in content.items())
    if same:
        return prev
    version = prev["version"] + 1 if prev.get("schema") == CATALOGUE_SCHEMA else 1
    body = {"schema": CATALOGUE_SCHEMA, "version": version, **content}
    blob = json.dumps(body, indent=2).encode('utf8')
    publish(f"catalogue-{version}.json", blob)
    _write(os.path.join(OUT_DIR, POINTER), blob)       # last: a pointer never names a catalogue that is not there
    return body
