"""Garbage-collect build/assets (asset-pipeline.md 6.4, claude/ docs): keep catalogue.json and the newest KEEP immutable
catalogues, plus every file they reference (descriptors, GLBs, shared KTX2, icons) and their .br sidecars; delete the rest.
Plain Python, no Blender. Dry run unless --apply.

    python scripts/th_gc_assets.py [--keep N] [--apply]
"""
import argparse, glob, json, os, re

OUT_DIR = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "build", "assets"))

ap = argparse.ArgumentParser()
ap.add_argument("--keep", type=int, default=1, help="immutable catalogues to keep, newest first")
ap.add_argument("--apply", action="store_true", help="actually delete (default: list only)")
args = ap.parse_args()


def number(path):
    return int(re.fullmatch(r"catalogue-(\d+)\.json", os.path.basename(path)).group(1))


pinned = sorted(glob.glob(os.path.join(OUT_DIR, "catalogue-*.json")), key=number, reverse=True)
catalogues = [os.path.join(OUT_DIR, "catalogue.json")] + pinned[:args.keep]
keep = set()
for cat in catalogues:
    c = json.load(open(cat, encoding="utf8"))
    keep.add(os.path.basename(cat))
    keep.update(c["textures"])
    for a in c["assets"].values():
        keep.update((a["descriptor"], a["glb"]))
    for i in c.get("icons", {}).values():
        keep.update(i["png"].values())
keep |= {k + ".br" for k in keep}

doomed = sorted(f for f in os.listdir(OUT_DIR) if os.path.isfile(os.path.join(OUT_DIR, f)) and f not in keep)
freed = sum(os.path.getsize(os.path.join(OUT_DIR, f)) for f in doomed)
for f in doomed:
    print(("delete " if args.apply else "would delete ") + f)
    if args.apply:
        os.remove(os.path.join(OUT_DIR, f))
print(f"{len(doomed)} files, {freed / 1024:.0f} KB {'freed' if args.apply else 'reclaimable'}; "
      f"kept {len(catalogues)} catalogue(s): {', '.join(os.path.basename(c) for c in catalogues)}")
