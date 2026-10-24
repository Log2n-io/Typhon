# SwgTatooine.Assets

The 3D assets of the SWG Tatooine demo — buildings, interiors, terminals, vehicles, characters — and the pipeline that
builds them: a Blender scene, Python build scripts, and the published set the server serves and the client draws.

**Git holds only text**: the scripts, the format contract, `revisions.json` and the pins in `assets.lock.json`. The
bytes (the `.blend`, the textures, the external packs and the published set) live in GitHub Releases of this
repository, the tools come from their own official releases, and all of it is fetched into this folder, gitignored. The
demo is not allowed to make the repository heavy. Everything here runs on Windows, Linux and macOS.

| File | What it is |
|---|---|
| [`ASSET_FORMAT.md`](ASSET_FORMAT.md) | The contract between the pipeline and its readers (server, client). Read this before reading a published file |
| `revisions.json` | Every asset and shared texture with the revision to publish. **Bumped by hand**, never derived |
| `assets.lock.json` | The release each bundle is pinned to, and each tool's official download per platform, all with their SHA-256 |
| `scripts/assets.py` | Fetches the pinned bundles and tools; builds and uploads new bundles; writes the `.br` sidecars |
| `scripts/th_*.py` | The pipeline: scene builders (run in Blender), the exporter, the character publisher, checks |

Design and rationale (authoring conventions, LODs, culling, hosting, Babylon findings):
`claude/design/SwgTatooine/design/asset-pipeline.md`, in the `claude/` docs repository nested at the root of the Typhon
checkout.

## Getting the assets

```
python scripts/assets.py fetch              # everything: sources, tools, published
python scripts/assets.py fetch published    # only what the server serves
python scripts/assets.py status
```

| Kind | Unpacks into | Size | For |
|---|---|---:|---|
| `published` | `build/assets/` | ~10 MB | running the demo with real assets |
| `sources` | `blender/`, `textures/`, `externals/` | ~32 MB | editing or re-exporting assets |
| `tools` | `tools/<name>/bin/` | ~10 MB | re-exporting assets |

A fetched file you have since edited is kept and reported, never overwritten, unless `--force`.

`tools` downloads, for the platform it runs on, the official builds of KTX-Software 4.4.2 (`ktx`) and meshoptimizer 1.3
(`gltfpack`), checked against the SHA-256 pinned in the lock. Coverage:

| | Windows x64 / arm64 | Linux x64 | Linux arm64 | macOS arm64 | macOS x64 |
|---|---|---|---|---|---|
| `ktx` | ✔ (needs 7-Zip to unpack the installer) | ✔ | ✔ | ✔ (`pkgutil`) | ✔ (`pkgutil`) |
| `gltfpack` | ✔ (x64 build; emulated on arm64) | ✔ | build it yourself | ✔ | build it yourself |

Where upstream ships no build, compile `gltfpack` from meshoptimizer v1.3 and put it in `tools/gltfpack/bin/`.

Serve them with the demo server:

```
dotnet run --project ../SwgTatooine.Server -c Release -- --serve 5080
```

With `--serve` the server finds the set by itself: `--assets <dir>` if given, else `<binary dir>/assets`, else the
first `demo/SwgTatooine/SwgTatooine.Assets/build/assets` holding a `catalogue.json` up from where it runs. If none is
found it exits (code 2) and says to run `python scripts/assets.py fetch published`. Measured runs (no `--serve`) load
no assets unless `--assets` is given.

## Changing an asset

Revisions are decided, because the build is not bit-reproducible (1-ulp float noise in modifier evaluation, Cycles AO
noise): re-exporting an unchanged asset gives different bytes. And every published name is cached `immutable`, so a
name must never get new bytes. Hence:

1. **Bump** the asset's revision in `revisions.json` (a new asset starts at 1). For a shared texture, bump the texture
   **and** every asset whose GLB uses it: the GLB names the texture file.
   An icon has its own revision, under `"icons"`: re-rendering one means bumping that, not its asset's.
2. **Export only what you bumped:**
   ```
   # buildings, interiors, interactables, props, vehicles, weapons: headless Blender
   TH_EXPORT_ONLY=tat_shuttleport,shuttle blender -b blender/tatooine.blend --python scripts/th_export.py   # bash, zsh
   $env:TH_EXPORT_ONLY="tat_shuttleport,shuttle"; blender -b blender/tatooine.blend --python scripts/th_export.py  # PowerShell
   # characters: plain Python, no Blender
   python -I scripts/th_character.py casual2_male
   # creatures (their sources are .blend files, listed in scripts/th_creatures.py): headless Blender, no scene
   blender -b --factory-startup --python scripts/th_creature.py -- rat wasp
   # icons (revisions.json "icons", listed in scripts/th_icons.py): headless Blender
   TH_ICONS_ONLY=swoop blender -b blender/tatooine.blend --python scripts/th_icons.py
   ```
   Re-exporting a revision that is already published with other bytes is **refused** (`Unbumped`). Exporting an asset
   that is not in `revisions.json` is refused too. A publish writes `<id>-<rev>.glb/.json` and the next
   `catalogue-<N>.json`.
3. **Check** the shipped bytes: `blender -b --factory-startup --python scripts/th_verify_glb.py -- <file.glb> <out.png>`.
4. **Release and pin**, then commit `revisions.json`, `assets.lock.json` and any script change together:
   ```
   python scripts/assets.py release published            # dry run: compresses, builds dist/swg-assets-<N>.zip, reports
   python scripts/assets.py release published --upload   # creates the release with gh, rewrites the lock
   ```
   Exports write no `.br` sidecars (Blender's Python has no brotli). `release published` writes them first; to serve
   brotli locally before releasing, run `python scripts/assets.py compress`. Without them the server sends the plain
   files, which is correct, only larger.
5. Optionally `python -I scripts/th_gc_assets.py --apply` to drop the files no recent catalogue references.

After editing the `.blend` or the textures, `python scripts/assets.py release sources --upload` pins a new sources
bundle the same way. An unchanged bundle has an unchanged hash and is not released twice.

**Requirements:** Blender 5.2 (headless export; inside the UI the exporter refuses to run: overriding the scene there
crashed Blender). The commands above call it `blender`; the installers do not put it on the `PATH`, so add it or use its
full path (`C:\Program Files\Blender Foundation\Blender 5.2\blender.exe`,
`/Applications/Blender.app/Contents/MacOS/Blender`, or where Linux unpacked it). Also Python 3.11+,
`pip install brotli==1.2.0` for `compress` and `release published`, 7-Zip on Windows for `fetch tools`, and the GitHub CLI for `--upload`. The scene scripts find this
folder from the open `.blend` (when run through the [Blender MCP](https://www.blender.org/lab/mcp-server/), which has no `__file__`) or from their own location,
so open `blender/tatooine.blend` from here and save it before running them.

## Licenses

- **Third-party packs** live in `externals/<pack>/`, one folder per pack, each with its own license file next to its
  files. `assets.py` refuses to release, or accept on fetch, a pack folder without one. Today:
  - `QuaterniusUltimateCharacterPack`: Quaternius Ultimate Character Pack, by Quaternius, CC0 1.0 (`License.txt`).

  Adding a pack: unpack it into `externals/<pack>/` with its license, reference it from the scripts that consume it
  (`th_character.py`'s `PACKS` for characters), then `python scripts/assets.py release sources --upload`.
- **Tools** are downloaded from their publishers and not redistributed: KTX-Software (Apache-2.0), meshoptimizer
  (MIT).
- Everything else here (the scene, textures, scripts and published assets) is part of this repository.
