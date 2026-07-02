# Page Integrity — CRC32C, Seqlock Snapshots & A/B Page Pairing
> Every 8 KiB page is checksummed, torn writes are detected, and structural pages survive a power cut without ever repairing a byte.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Storage](./README.md)

## 🎯 What it solves

An 8 KiB page spans two 4 KB device blocks; on commodity NVMe an 8 KiB write is not atomic, so a power cut mid-write can leave a page half-old, half-new. Left undetected, that torn page silently feeds wrong bytes into the engine. Detecting it requires a checksum verified on every load; *repairing* it without a doubled write cost requires either a before-image (the old Full-Page-Image design) or — Typhon's choice — rebuilding the page from elsewhere. Two page classes need different answers: derived pages (indexes, occupancy) can always be thrown away and recomputed, but a handful of structural pages (the file's root meta, segment directories) have no "elsewhere" to rebuild from and must instead never lose their last good copy.

## ⚙️ How it works (in brief)

Every page header carries a CRC32C checksum (`PageChecksum`) computed over the whole page except the checksum field itself, verified lazily the first time a page is touched after load. The checkpoint also needs to copy a page that a transaction might be mid-write on without blocking either side: a seqlock counter (`ModificationCounter`) is bumped odd→even around every exclusive-latched write, and the checkpoint's snapshot copy retries if the counter changed or was odd during the copy — so the copy it CRCs and persists is always self-consistent. For the structural pages that can't be derived-and-rebuilt (the root meta page, segment directory pages), a third mechanism — A/B slot pairing — keeps two physical copies and always writes to the *other* one, bumping a generation counter; the currently-valid slot is never touched, so a torn write can never destroy the only good copy.

## 💻 Usage

CRC verification and torn-page response are configured at engine startup; the slot-pairing and seqlock machinery are fully internal (no API surface):

```csharp
services
    .AddManagedPagedMMF(options =>
    {
        options.DatabaseName = "GameWorld";
    })
    .AddDatabaseEngine(options =>
    {
        // OnLoad (default): verify every page's CRC the first time it's touched after load.
        // RecoveryOnly: skip on normal operation, verify only during crash recovery (lower steady-state overhead).
        options.Resources.PageChecksumVerification = PageChecksumVerification.OnLoad;
    });

var dbe = services.BuildServiceProvider().GetRequiredService<DatabaseEngine>();

try
{
    // normal ECS/transaction work — CRC verification runs transparently on page load
}
catch (PageCorruptionException ex)
{
    // a primary page failed CRC outside of crash recovery — torn/corrupted, no on-load repair
    log.LogCritical(ex, "page {Page} CRC mismatch: stored=0x{Expected:X8} computed=0x{Computed:X8}",
        ex.PageIndex, ex.ExpectedCrc, ex.ComputedCrc);
}
```

| Option | Default | Effect |
|---|---|---|
| `ResourceOptions.PageChecksumVerification` | `OnLoad` | `OnLoad` verifies CRC on every cold page load (~0.4 µs/page, hardware CRC32C); `RecoveryOnly` skips it in normal operation and verifies only during crash recovery |

A third mode, `RecoverySuspect`, exists only on the crash-recovery path — the engine switches into it automatically while replaying and restores the configured mode once recovery completes; it is not a setting application code chooses.

## ⚠️ Guarantees & limits

- Every page's CRC32C is checked the first time it's accessed after a cold load (`OnLoad` mode, the default); a mismatch outside of recovery throws `PageCorruptionException` rather than silently serving torn bytes.
- CRC32C is hardware-accelerated (SSE4.2/ARMv8 CRC32 instruction) — roughly 0.4 µs per 8 KiB page; `RecoveryOnly` mode trades that cold-load cost away when an application is willing to defer all detection to crash recovery.
- The checkpoint never persists a torn snapshot of a page being concurrently written: the seqlock retry loop guarantees the copy it checksums and writes is from a single consistent write, not a half-old/half-new blend.
- Derived structures (secondary indexes, occupancy bitmap) are never repaired in place on a CRC failure — they're discarded and rebuilt from primary data during recovery; this is intentionally simpler and cheaper than per-page repair.
- The structural pages that *can't* be rebuilt (root meta, segment directories) are protected by A/B pairing instead: a torn write can corrupt only the non-current slot, so the currently-valid copy always survives. If — and only if — *both* slots of a pair are CRC-invalid, the open fails loudly rather than silently picking a corrupt one.
- A primary data page (component/revision content, EntityMap, etc.) that fails CRC during recovery and still backs live data is **not** silently healed — the open fails loudly naming the page, rather than serving possibly-wrong bytes. This is a deliberate trade: an uncovered torn primary page is genuinely lost data, and refusing to open is the honest outcome.
- There is no full-page-image (FPI) repair path — it was retired in favor of this rebuild-or-loud-fail model; checkpoint cost no longer includes a before-image write per dirtied page.

## 🧪 Tests

- [PageCrcVerificationTests](../../../test/Typhon.Engine.Tests/Durability/PageCrcVerificationTests.cs) — `OnLoad` vs `RecoveryOnly` verification modes, `PageCorruptionException` on a genuine mismatch
- [SeqlockProtocolTests](../../../test/Typhon.Engine.Tests/Durability/SeqlockProtocolTests.cs) — `ModificationCounter` odd/even latch protocol, checkpoint snapshot consistency under concurrent writers
- [DirectoryPairTests](../../../test/Typhon.Engine.Tests/Storage/DirectoryPairTests.cs) — A/B slot pairing for segment-directory pages: torn-slot reopen selects the sibling, both-slots-corrupt fails loudly
- [MetaPairTests](../../../test/Typhon.Engine.Tests/Durability/CrashRecovery/MetaPairTests.cs) — the same A/B pairing guarantees applied to the root meta page

## 🔗 Related

- Related feature: [Page Allocation & Occupancy Tracking](./page-allocation-occupancy.md) (an example derived structure healed by rebuild, not repair)

<!-- Overview: claude/overview/03-storage.md — Page Checksums, Seqlock & CRC Integration -->
<!-- Overview: claude/overview/06-durability.md §6.6 Torn-Page Safety, §6.7 Page Checksums & Seqlock Snapshots -->
<!-- ADR: claude/adr/015-crc32c-page-checksums.md -->
<!-- Rules: claude/rules/durability.md — modules CK-05 (A/B pairing), RB-01/RB-04 (rebuild-or-loud-fail), SQ-01..06 (seqlock) -->
