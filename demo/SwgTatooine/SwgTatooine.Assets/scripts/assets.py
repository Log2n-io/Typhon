"""The SWG Tatooine asset bundles and tools: everything this folder needs that does not live in git.

    python scripts/assets.py fetch    [sources] [tools] [published] [--force] [--from DIR]
    python scripts/assets.py release  (sources | published) [--upload]
    python scripts/assets.py compress
    python scripts/assets.py status

Two bundles, each a zip attached to its own release of this repository and pinned in assets.lock.json by tag and
SHA-256:

    sources    blender/tatooine.blend, textures/tatooine_common/*.png and every external pack under externals/
               (one folder per pack, each carrying its own license file): what the pipeline builds FROM
    published  build/assets at one catalogue: what the server serves (--assets) and the client draws

And the tools, downloaded from their official releases for THIS platform (Windows, Linux, macOS; x64 or arm64), pinned
in assets.lock.json by URL and SHA-256, and installed as tools/<name>/bin/<name>[.exe]:

    ktx        KTX-Software (KTX2 encoding, th_export.py; transcoding back, th_verify_glb.py)
    gltfpack   meshoptimizer (geometry compression and LOD cuts, th_export.py and th_character.py)

fetch downloads what is pinned, checks it against the lock, and unpacks it into this folder. A bundle file that is
already there with different bytes is a local edit: it is reported and kept, unless --force. --from DIR takes the
bundle zips from a folder instead of the network (a release not uploaded yet, or offline).

release builds a bundle's zip into dist/ deterministically (sorted entries, fixed timestamps), so an unchanged bundle
has an unchanged hash and is not released twice. Without --upload it only reports what it would publish. With
--upload it creates the release with `gh` and rewrites assets.lock.json; commit the lock afterwards. sources takes the
next number; published takes the catalogue's version, so release N is catalogue-N.json.

compress writes the brotli-11 sidecar (<file>.br) of every published .glb and .json that lacks a correct one; the
server hands them to browsers that accept br. It runs here rather than in the exporter because Blender's Python has no
brotli module. `release published` runs it first.

Python 3.11+. Standard library only, except `compress` (pip install brotli==1.2.0) and `release --upload` (the GitHub
CLI). Unpacking the KTX-Software Windows installer needs 7-Zip; the macOS one uses the system's pkgutil.
"""
import argparse, glob, hashlib, io, json, os, platform, shutil, stat, subprocess, sys, tarfile, tempfile
import urllib.request, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCK = os.path.join(ROOT, "assets.lock.json")
DIST = os.path.join(ROOT, "dist")
STAMP = os.path.join(DIST, "fetched.json")
PUBLISHED = os.path.join(ROOT, "build", "assets")
EXTERNALS = os.path.join(ROOT, "externals")
TOOLS = os.path.join(ROOT, "tools")
EXE = ".exe" if os.name == "nt" else ""


def rel(paths):
    return sorted(os.path.relpath(p, ROOT).replace(os.sep, "/") for p in paths)


def sources_files():
    packs = rel(p for p in glob.glob(os.path.join(EXTERNALS, "**", "*"), recursive=True) if os.path.isfile(p))
    files = ["blender/tatooine.blend"] + rel(glob.glob(os.path.join(ROOT, "textures", "tatooine_common", "*.png"))) + packs
    return files, None


def unlicensed_packs():
    """The folders of externals/ with no license file of their own (License.txt, LICENSE, LICENSE.md, ...).
    Third-party content is redistributed in a public release, so a pack without its terms is not shipped."""
    if not os.path.isdir(EXTERNALS):
        return []
    return sorted(d for d in os.listdir(EXTERNALS) if os.path.isdir(os.path.join(EXTERNALS, d))
                  and not any(f.lower().startswith("licen") for f in os.listdir(os.path.join(EXTERNALS, d))))


def published_files():
    """The pointer, the catalogue it names, and every file that catalogue lists, each with its .br sidecar if any."""
    cat = json.load(open(os.path.join(PUBLISHED, "catalogue.json"), encoding="utf8"))
    names = ["catalogue.json", f"catalogue-{cat['version']}.json"] + sorted(cat["sha256"])
    names += [n + ".br" for n in names if os.path.exists(os.path.join(PUBLISHED, n + ".br"))]
    return ["build/assets/" + n for n in sorted(names)], cat["version"]


BUNDLES = {
    "sources": {"tag": "swg-assets-sources-{v}", "files": sources_files, "required": ["blender/tatooine.blend"],
                "title": "SWG Tatooine asset sources {v}",
                "notes": "Sources of the SWG Tatooine demo's asset pipeline (demo/SwgTatooine/SwgTatooine.Assets): the "
                         "Blender scene, the baked texture library and the third-party packs under externals/, each "
                         "with its license file inside its folder. Fetched by `scripts/assets.py fetch sources`."},
    "published": {"tag": "swg-assets-{v}", "files": published_files, "required": ["build/assets/catalogue.json"],
                  "title": "SWG Tatooine assets, catalogue {v}",
                  "notes": "The published asset set of the SWG Tatooine demo at catalogue {v}: serve "
                           "`demo/SwgTatooine/SwgTatooine.Assets/build/assets` with the server's --assets. "
                           "Fetched by `scripts/assets.py fetch published`."},
}
KINDS = ["sources", "tools", "published"]


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def load(path, default):
    return json.load(open(path, encoding="utf8")) if os.path.exists(path) else default


def save(path, value):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf8", newline="\n") as f:
        f.write(json.dumps(value, indent=2) + "\n")


def download(url, digest, cache_name):
    """The bytes at url, cached in dist/ and checked against the pinned digest."""
    cached = os.path.join(DIST, cache_name)
    if os.path.exists(cached) and sha256(open(cached, "rb").read()) == digest:
        return open(cached, "rb").read()
    print(f"  downloading {url}")
    with urllib.request.urlopen(url) as r:
        data = r.read()
    if sha256(data) != digest:
        sys.exit(f"{url}: sha256 {sha256(data)}, the lock pins {digest}. Refusing it.")
    os.makedirs(os.path.dirname(cached), exist_ok=True)
    open(cached, "wb").write(data)
    return data


# ---------------------------------------------------------------------------------------------------------------- bundles

def build_zip(files):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name in sorted(files):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, open(os.path.join(ROOT, name), "rb").read(), compresslevel=9)
    return buf.getvalue()


def release(kind, upload):
    spec, lock = BUNDLES[kind], load(LOCK, {})
    if kind == "published":
        compress()
    files, version = spec["files"]()
    missing = [f for f in files + spec["required"] if not os.path.isfile(os.path.join(ROOT, f))]
    if missing:
        sys.exit(f"{kind}: missing {', '.join(missing)}")
    if kind == "sources" and unlicensed_packs():
        sys.exit(f"sources: no license file in externals/{', externals/'.join(unlicensed_packs())}; add the pack's terms first")
    data = build_zip(files)
    digest, pin = sha256(data), lock.get(kind)
    if pin and pin["sha256"] == digest:
        print(f"{kind}: unchanged since {pin['tag']}, nothing to release")
        return
    if version is None:
        version = pin["version"] + 1 if pin else 1
    elif pin and pin["version"] == version:
        sys.exit(f"published: catalogue {version} is already released as {pin['tag']} with other bytes. "
                 f"Publish a new catalogue (bump a revision and export) instead of changing this one.")
    tag = spec["tag"].format(v=version)
    path = os.path.join(DIST, tag + ".zip")
    os.makedirs(DIST, exist_ok=True)
    open(path, "wb").write(data)
    print(f"{kind}: {tag}.zip, {len(files)} files, {len(data) / 1e6:.1f} MB, sha256 {digest}")
    if not upload:
        print("  dry run: --upload creates the release and updates assets.lock.json")
        return
    repo = lock["repo"]
    subprocess.run(["gh", "release", "create", tag, path, "-R", repo, "--title", spec["title"].format(v=version),
                    "--notes", spec["notes"].format(v=version), "--latest=false", "--prerelease"], check=True)
    # --latest=false alone is ignored while the repo has no other full release: a pre-release never takes "Latest"
    lock[kind] = {"version": version, "tag": tag, "file": tag + ".zip", "sha256": digest}
    save(LOCK, lock)
    print(f"  released {repo}@{tag}; assets.lock.json updated, commit it")


def fetch_bundle(kind, pin, repo, force, source):
    """Unpacks a pinned bundle; returns False when local files that differ were kept."""
    if source:
        data = open(os.path.join(source, pin["file"]), "rb").read()
        if sha256(data) != pin["sha256"]:
            sys.exit(f"{pin['file']}: sha256 {sha256(data)}, the lock pins {pin['sha256']}. Refusing it.")
    else:
        data = download(f"https://github.com/{repo}/releases/download/{pin['tag']}/{pin['file']}", pin["sha256"], pin["file"])
    kept, written = [], 0
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        for name in z.namelist():
            if name.startswith("/") or ".." in name.split("/"):
                sys.exit(f"{pin['file']}: refusing entry {name}")
            body, dest = z.read(name), os.path.join(ROOT, *name.split("/"))
            if os.path.exists(dest):
                if open(dest, "rb").read() == body:
                    continue
                if not force:
                    kept.append(name)
                    continue
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            open(dest, "wb").write(body)
            written += 1
    absent = [f for f in BUNDLES[kind]["required"] if not os.path.isfile(os.path.join(ROOT, f))]
    if absent:
        sys.exit(f"{kind}: {pin['file']} does not provide {', '.join(absent)}")
    if kind == "sources" and unlicensed_packs():
        sys.exit(f"sources: no license file in externals/{', externals/'.join(unlicensed_packs())}")
    if kind == "published":
        verify_published()
    print(f"  {written} files written")
    if kept:
        print(f"  {len(kept)} local files differ from {pin['tag']} and were kept (--force overwrites): "
              + ", ".join(kept[:8]) + (" ..." if len(kept) > 8 else ""))
    return not kept


def verify_published():
    """Every file the catalogue lists is there, with the bytes it was published with."""
    cat = json.load(open(os.path.join(PUBLISHED, "catalogue.json"), encoding="utf8"))
    bad = [n for n, h in cat["sha256"].items()
           if not os.path.exists(os.path.join(PUBLISHED, n)) or sha256(open(os.path.join(PUBLISHED, n), "rb").read()) != h]
    if bad:
        sys.exit(f"published: {len(bad)} files do not match catalogue {cat['version']}: {', '.join(bad[:8])}")


def compress():
    """Writes the .br sidecar of every published .glb / .json whose sidecar is missing or holds other content. A correct
    sidecar is kept as it is, whatever encoder made it, so a released catalogue's bundle never changes underneath it."""
    try:
        import brotli
    except ImportError:
        sys.exit("compress needs the brotli module: pip install brotli==1.2.0")
    cat = json.load(open(os.path.join(PUBLISHED, "catalogue.json"), encoding="utf8"))
    names = ["catalogue.json", f"catalogue-{cat['version']}.json"] + [n for n in cat["sha256"] if n.endswith((".glb", ".json"))]
    written = 0
    for name in names:
        src = os.path.join(PUBLISHED, name)
        data = open(src, "rb").read()
        if os.path.exists(src + ".br"):
            try:
                if brotli.decompress(open(src + ".br", "rb").read()) == data:
                    continue
            except brotli.error:
                pass
        open(src + ".br", "wb").write(brotli.compress(data, quality=11))
        written += 1
    print(f"compress: {written} sidecars written, {len(names) - written} already correct")


# ------------------------------------------------------------------------------------------------------------------ tools

def platform_key():
    system = {"win32": "windows", "linux": "linux", "darwin": "macos"}.get(sys.platform)
    arch = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower())
    return f"{system}-{arch}" if system and arch else f"{sys.platform}-{platform.machine()}"


def seven_zip():
    for candidate in ("7z", "7za", r"C:\Program Files\7-Zip\7z.exe", r"C:\Program Files (x86)\7-Zip\7z.exe"):
        found = shutil.which(candidate) or (candidate if os.path.isfile(candidate) else None)
        if found:
            return found
    sys.exit("unpacking the KTX-Software Windows installer needs 7-Zip (https://www.7-zip.org): install it, then fetch again")


def install_tool(name, version, entry, key):
    """Unpacks the pinned archive of one tool into tools/<name>/, with its executable at tools/<name>/bin/<name>."""
    archive = entry["url"].rsplit("/", 1)[1]
    data = download(entry["url"], entry["sha256"], os.path.join("tools", archive))
    dest = os.path.join(TOOLS, name)
    shutil.rmtree(dest, ignore_errors=True)
    with tempfile.TemporaryDirectory() as tmp:
        src = os.path.join(tmp, archive)
        open(src, "wb").write(data)
        out = os.path.join(tmp, "out")
        fmt = entry["format"]
        if fmt == "zip":                                     # gltfpack: the executable alone, at the archive root
            with zipfile.ZipFile(src) as z:
                z.extractall(os.path.join(dest, "bin"))
        elif fmt == "tar":                                   # KTX-Software Linux: <root>/{bin,lib}, relative symlinks
            with tarfile.open(src) as t:
                t.extractall(out, filter="data")
            (top,) = os.listdir(out)
            shutil.copytree(os.path.join(out, top), dest, symlinks=True)
        elif fmt == "nsis":                                  # KTX-Software Windows: an installer; only its bin/ is needed
            subprocess.run([seven_zip(), "x", "-y", f"-o{out}", src, "bin"], check=True, capture_output=True)
            shutil.copytree(os.path.join(out, "bin"), os.path.join(dest, "bin"))
        elif fmt == "pkg":                                   # KTX-Software macOS: a flat package; keep the prefix's bin + lib
            subprocess.run(["pkgutil", "--expand-full", src, out], check=True, capture_output=True)
            hits = [p for p in glob.glob(os.path.join(out, "**", "bin", name), recursive=True) if os.path.isfile(p)]
            if not hits:
                sys.exit(f"{name}: no bin/{name} inside {archive}")
            prefix = os.path.dirname(os.path.dirname(hits[0]))
            for sub in ("bin", "lib"):
                if os.path.isdir(os.path.join(prefix, sub)):
                    shutil.copytree(os.path.join(prefix, sub), os.path.join(dest, sub), symlinks=True)
        else:
            sys.exit(f"{name}: unknown archive format {fmt}")
    exe = os.path.join(dest, "bin", name + EXE)
    if not os.path.isfile(exe):
        sys.exit(f"{name}: {archive} did not provide bin/{name}{EXE}")
    if os.name != "nt":
        for f in os.listdir(os.path.join(dest, "bin")):
            p = os.path.join(dest, "bin", f)
            if os.path.isfile(p) and not os.path.islink(p):
                os.chmod(p, os.stat(p).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    probe = subprocess.run([exe, "--version" if name == "ktx" else "-v"], capture_output=True, text=True)
    print(f"  {name} {version} ({key}): {(probe.stdout or probe.stderr).strip().splitlines()[0]}")


def fetch_tools(lock, stamp, force):
    key, ok = platform_key(), True
    for name, spec in lock.get("tools", {}).items():
        entry = spec["platforms"].get(key)
        if entry is None:
            print(f"  {name}: no official {spec['version']} build for {key} "
                  f"(pinned: {', '.join(spec['platforms'])}); {spec.get('fallback', '')}")
            ok = False
            continue
        mark = f"{spec['version']}:{key}:{entry['sha256']}"
        if stamp.get("tools/" + name) == mark and os.path.isfile(os.path.join(TOOLS, name, "bin", name + EXE)) and not force:
            print(f"  {name} {spec['version']} ({key}) already installed")
            continue
        install_tool(name, spec["version"], entry, key)
        stamp["tools/" + name] = mark
        save(STAMP, stamp)
    return ok


# ------------------------------------------------------------------------------------------------------------------- main

def fetch(kinds, force, source):
    lock, stamp, failed = load(LOCK, {}), load(STAMP, {}), False
    for kind in kinds:
        if kind == "tools":
            print(f"tools: {platform_key()}")
            failed |= not fetch_tools(lock, stamp, force)
            continue
        pin = lock.get(kind)
        if not pin:
            print(f"{kind}: not released yet (no pin in assets.lock.json)")
            failed = True
            continue
        if stamp.get(kind) == pin["sha256"] and not force:
            print(f"{kind}: {pin['tag']} already fetched")
            continue
        print(f"{kind}: {pin['tag']}")
        if fetch_bundle(kind, pin, lock["repo"], force, source):
            stamp[kind] = pin["sha256"]
            save(STAMP, stamp)
        else:
            failed = True
    sys.exit(1 if failed else 0)


def status():
    lock, stamp, key = load(LOCK, {}), load(STAMP, {}), platform_key()
    for kind in BUNDLES:
        pin = lock.get(kind)
        here = "fetched" if pin and stamp.get(kind) == pin["sha256"] else "not fetched"
        print(f"{kind:10} {pin['tag'] if pin else '(not released)':28} {here}")
    for name, spec in lock.get("tools", {}).items():
        entry = spec["platforms"].get(key)
        here = ("installed" if entry and stamp.get("tools/" + name, "").endswith(entry["sha256"])
                else "not installed" if entry else f"no build for {key}")
        print(f"{name:10} {spec['version'] + ' (' + key + ')':28} {here}")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="command", required=True)
    f = sub.add_parser("fetch")
    f.add_argument("kinds", nargs="*", metavar="KIND", help=f"any of {', '.join(KINDS)} (default: all)")
    f.add_argument("--force", action="store_true", help="overwrite local files that differ, reinstall tools")
    f.add_argument("--from", dest="source", help="take the bundle zips from this folder instead of downloading them")
    r = sub.add_parser("release")
    r.add_argument("kind", choices=list(BUNDLES))
    r.add_argument("--upload", action="store_true", help="create the GitHub release and update assets.lock.json")
    sub.add_parser("compress")
    sub.add_parser("status")
    args = ap.parse_args()
    if args.command == "fetch":
        unknown = [k for k in args.kinds if k not in KINDS]
        if unknown:
            ap.error(f"unknown kind {', '.join(unknown)}")
        fetch(args.kinds or KINDS, args.force, args.source)
    elif args.command == "release":
        release(args.kind, args.upload)
    elif args.command == "compress":
        compress()
    else:
        status()


if __name__ == "__main__":
    main()
