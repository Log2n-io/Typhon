---
uid: feature-foundation-crc32c-checksums
title: 'Hardware-Accelerated CRC32C Checksums'
description: 'SSE4.2/ARM-intrinsic CRC32C checksum primitive that backs every page and WAL-record integrity check.'
---

# Hardware-Accelerated CRC32C Checksums
> SSE4.2/ARM-intrinsic CRC32C checksum primitive that backs every page and WAL-record integrity check.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](./README.md)

## 🎯 What it solves

Storage media can silently corrupt data — torn writes on power loss, bit rot, firmware bugs writing the right bytes to the wrong place. None of these announce themselves; without a checksum, corrupted data is read back and trusted. Detecting this requires computing and verifying a checksum on every single page and WAL record, on every read and write, without becoming the bottleneck itself.

## ⚙️ How it works (in brief)

Every page header and WAL record carries a CRC32C (Castagnoli polynomial) checksum, computed over its bytes and verified on every load. The checksum field itself is excluded from its own computation (it can't checksum itself), so the primitive treats that region as zeros rather than requiring a separate buffer copy. Computation uses the CPU's native CRC32 instruction (SSE4.2 on x86/x64, CRC32C on ARM64) when available, falling back to a software lookup table on unsupported hardware — same result, different speed.

## 💻 Usage

This is transparent engine plumbing — every page write/read and WAL record write/replay is checksummed automatically, there's nothing application code calls directly:

```csharp
using var tx = dbe.CreateQuickTransaction();

EntityRef e = tx.OpenMut(entityId);
ref Position p = ref e.Write(Unit.Pos);
p.X += 1f;
tx.Commit();
// Pages touched by the commit get their CRC32C stamped on write;
// a future read verifies it and throws on mismatch — no app code involved.
```

## ⚠️ Guarantees & limits

- **~1.3µs per 8KB page** on SSE4.2 x64 (hardware path); software fallback is roughly 6x slower — negligible relative to I/O latency either way.
- Detects all single-bit and double-bit errors, and the large majority of multi-bit burst errors — standard error-detection properties of CRC32C.
- **Not cryptographic** — does not protect against intentional tampering, only accidental corruption. Not a threat model concern for an embedded database engine.
- Same algorithm and code path checksums both page headers and WAL records, so storage and durability share one integrity guarantee.
- Hardware acceleration requires SSE4.2 (x86, ubiquitous since 2008) or ARMv8-A; older hardware silently uses the slower software table with no behavior change.

## 🧪 Tests
- [Crc32CUtilTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/Crc32CUtilTests.cs) — canonical CRC32C test vectors (`"123456789"` → `0xE3069283`), empty-span zero result, determinism.

## 🔗 Related

- Sibling: [Page Checksums & Seqlock Snapshots](../Durability/page-checksums-seqlock.md) — pairs this CRC32C primitive with a lock-free seqlock for checkpoint page snapshots.

<!-- ADR: claude/adr/015-crc32c-page-checksums.md -->
<!-- Overview: claude/overview/03-storage.md, claude/overview/06-durability.md §6.1, §6.7 -->
<!-- Overview: claude/overview/11-utilities.md -->
