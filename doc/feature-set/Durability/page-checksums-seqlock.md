# Page Checksums & Seqlock Snapshots
> CRC32C torn-page detection on every page, paired with a lock-free seqlock so checkpoints snapshot live pages without blocking writers.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](./README.md)

## 🎯 What it solves

An 8 KB page can be physically torn by a power loss mid-write (consumer NVMe write-atomicity unit is typically smaller than 8 KB), or silently bit-rot on the storage medium. Without a checksum, that corruption is invisible until the bad bytes are interpreted as live data. Separately, the checkpoint needs to copy a page's bytes to disk while application transactions may be actively writing that same page — a snapshot taken mid-write would itself encode torn data with a checksum that looks valid. Typhon needs both corruption detection and a copy mechanism that is provably consistent, without making writers wait on the checkpoint.

## ⚙️ How it works (in brief)

Every page carries a CRC32C checksum (hardware-accelerated via the SSE4.2/ARMv8 CRC32 instruction) covering its contents. The checksum is recomputed whenever a page is written and verified whenever a page is read, depending on the configured mode. To let the checkpoint copy a page without locking it against writers, each page also carries a *seqlock* counter: a writer bumps it to odd before mutating the page and back to even after, under the page's exclusive latch. The checkpoint reads the counter, copies the page, then re-reads the counter — if it changed or was caught odd, the copy is discarded and retried (or the page is skipped for that cycle). A torn-page failure is never silently repaired in place: on-load mismatches throw, and a torn page reachable only through crash recovery is healed or fails the open loudly — see the Crash Recovery and Checkpoint entries.

## 💻 Usage

CRC verification is on by default and requires no code changes. The only knob is *when* it runs:

```csharp
services.AddScopedDatabaseEngine(o =>
{
    // Default: verify CRC on every page load — catches corruption at first access.
    o.Resources.PageChecksumVerification = PageChecksumVerification.OnLoad;

    // Lower overhead: skip on-load checks, verify only during crash recovery.
    // o.Resources.PageChecksumVerification = PageChecksumVerification.RecoveryOnly;
});
```

A mismatch under `OnLoad` surfaces as a normal exception you can catch at the call site that touched the page:

```csharp
try
{
    using var tx = uow.CreateTransaction();
    var view = tx.Open(soldier);
    _ = view.Read(Unit.Health);
}
catch (PageCorruptionException ex)
{
    // ex.PageIndex, ex.ExpectedCrc, ex.ComputedCrc — log and escalate (restore from backup).
}
```

| Option | Default | Effect |
|---|---|---|
| `PageChecksumVerification.OnLoad` | default | Verify CRC on every page load from disk; throws `PageCorruptionException` on mismatch. |
| `PageChecksumVerification.RecoveryOnly` | opt-in | Skip CRC checks during normal operation; verify only while replaying crash recovery. |

## ⚠️ Guarantees & limits

- **Detection, not repair** — a CRC mismatch under `OnLoad` is reported, never silently patched; there is no in-place page repair (the old Full-Page-Image mechanism was retired — see ADR for the rationale). Recovery from a confirmed bad page means restoring from backup or letting crash recovery rebuild what it can.
- **Whole-page coverage** — the checksum covers the entire page except the 4-byte checksum field itself; any single- or multi-bit corruption in the page is detected.
- **Non-blocking checkpoint snapshots** — the seqlock lets the checkpoint copy a page that is concurrently being written without taking a lock writers must wait on; a page with a writer in flight for an unreasonably long time is skipped for that checkpoint cycle rather than stalling it.
- **Hardware-accelerated** — CRC32C uses the CPU's native CRC32 instruction; cost is roughly constant per 8 KB page and negligible next to the I/O it accompanies.
- **`RecoveryOnly` trades safety for overhead** — normal reads skip verification entirely, so a torn page introduced outside the recovery window can go undetected until it's next touched during a future recovery pass.
- **Same mechanism backs WAL records** — WAL record framing uses the same CRC32C algorithm, so a single, consistently-tested corruption-detection primitive covers both pages and the log.

## 🧪 Tests

- [PageCrcVerificationTests](../../../test/Typhon.Engine.Tests/Durability/PageCrcVerificationTests.cs) — lazy CRC verification on page load (`OnLoad` mode), mismatch throws `PageCorruptionException`
- [SeqlockProtocolTests](../../../test/Typhon.Engine.Tests/Durability/SeqlockProtocolTests.cs) — modification-counter seqlock protocol, checkpoint CRC stamping, concurrent snapshot consistency
- [Crc32CUtilTests](../../../test/Typhon.Engine.Tests/Durability/Crc32CUtilTests.cs) — the CRC32C algorithm itself against known test vectors

## 🔗 Related

- Sibling: [Page Integrity — CRC32C, Seqlock Snapshots & A/B Page Pairing](../Storage/page-integrity.md) — storage-layer description of this same CRC32C + seqlock mechanism
- Sibling: [Checkpoint v2 (SnapshotStore pipeline)](checkpoint-v2/README.md) — checkpoint is the primary consumer of the seqlock snapshot copy

<!-- Deep dive: claude/overview/06-durability.md §6.7 -->
<!-- ADR: 015-crc32c-page-checksums (claude/adr/015-crc32c-page-checksums.md) -->
<!-- Rules: claude/rules/durability.md — modules Seqlock, Page Safety -->
