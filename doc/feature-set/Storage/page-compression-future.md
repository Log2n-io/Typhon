# Page Compression (Future)
> Planned LZ4-style adapter for cold/historical data and backups — deliberately absent from v1 to protect microsecond-latency paths.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Storage](./README.md)

## 🎯 What it solves

Uncompressed pages cost disk space and I/O bandwidth proportional to raw data size. For cold/historical data, string-heavy tables, and backup snapshots, that cost is real money and real restore time — and none of it sits on the real-time hot path. Today Typhon stores every page as-is; there is no way to trade CPU for size on the data that would actually benefit (rarely-touched archives, large string tables, offline backups) without also paying that cost on the live game-state pages that must stay within microsecond latency budgets.

## ⚙️ How it works (in brief)

The planned design slots a `CompressionAdapter` between `ChunkBasedSegment`/`PagedMMF` and the OS file layer: pages are compressed (LZ4-class) just before the write to disk and decompressed just after the read, with the adapter maintaining the logical-page → compressed-block mapping. The page cache, segments, and chunk accessors above the adapter are unaware it exists — they keep working with full, uncompressed 8 KiB pages in memory; compression only ever touches the on-disk representation. The intent is opt-in, scoped to specific segments or storage tiers (cold data, backups) rather than a database-wide switch, so hot real-time tables are never forced through a compress/decompress cycle.

## 💻 Usage

Not implemented in the current release — there is no API to enable or configure compression today. Pages are always stored uncompressed, on every segment, with no opt-in switch. The sketch below shows the *intended* shape of the feature once built, for planning purposes only; none of this compiles against today's API:

```csharp
// Illustrative only — not a real/current Typhon API.
// services
//     .AddManagedPagedMMF(options =>
//     {
//         options.DatabaseName = "GameWorld";
//     })
//     .AddDatabaseEngine(options =>
//     {
//         // Planned: per-segment or per-tier opt-in, not a global flag —
//         // so hot real-time tables stay uncompressed by default.
//         // options.Storage.Compression = CompressionPolicy.ColdTierOnly;
//     });
```

## ⚠️ Guarantees & limits

- **Not implemented.** Nothing in `src/Typhon.Engine/Storage` performs compression today; every page is written and read as raw bytes.
- When built, it is expected to be opt-in per segment/tier — real-time game-state paths (small, high-entropy components like position/velocity/health) are expected to stay uncompressed by default, since LZ4-class ratios on that kind of data are poor (roughly 1.1–1.3x) for measurable added latency.
- Expected costs once implemented, order of magnitude: +1–5 µs/page on read (decompression), +2–10 µs/page on write (compression) — acceptable for batch/cold/backup paths, not for the live hot path.
- Best-fit candidates: cold/historical data, string-heavy tables (3–5x expected ratio), backup/snapshot storage. Not intended for live component pages.
- No file-format or API commitment exists yet — names, option shapes, and the exact adapter boundary in this document are a design sketch, not a spec; treat them as subject to change when this is actually designed.

## 🔗 Related

- Related feature: [Memory-Mapped Page Cache & Clock-Sweep Eviction](./page-cache.md) (the layer the adapter would slot beneath)
- Related feature: [String Table Storage](./string-table.md) (a candidate use case — high expected compression ratio)

<!-- Overview: claude/overview/03-storage.md §3.10 Compression (Future) -->
