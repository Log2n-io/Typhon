# Point-in-Time Incremental Backup
> Forward-incremental `.pack` backups scoped to changed pages; restore reassembles a base and heals it through crash recovery's RecoveryDriver.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](./README.md)
**Assumes:** [Crash Recovery (RecoveryDriver)](crash-recovery/README.md)

## 🎯 What it solves

A database that only ever recovers to "last checkpoint plus WAL replay" has no answer for disk failure, accidental
mass-delete, or wanting a snapshot from four hours ago — the WAL itself is recycled after each checkpoint
([ADR-014](../../../claude/adr/014-no-point-in-time-recovery.md)), so nothing but the live data file survives past
that point. A naive backup that copies the whole database every time doesn't scale: I/O cost is proportional to
database size, not to how much actually changed. PIT Backup gives Typhon an external, self-contained recovery
point whose cost tracks the change rate, without taxing the live transaction path to get it.

## ⚙️ How it works (in brief)

The checkpoint pipeline already iterates dirty pages every cycle; it OR's each flushed page's index into a
**persistent dirty bitmap** at near-zero cost. A backup point forces a checkpoint, reads that bitmap as the
changed-page set since the last backup, copies just those pages from the data file, and writes them — LZ4-compressed,
checksummed, page-indexed — into a self-contained `.pack` file. Successive `.pack` files chain forward; restoring a
point walks the chain **backward**, taking the newest copy of each page (first-hit-wins) to assemble a single backup
base. That base is then handed to the **same `RecoveryDriver` used for crash recovery** — it validates pages and
rebuilds every derived structure (indexes, EntityMap, occupancy bitmap, spatial/statistics) from scratch, exactly as
it does after a crash. Backups therefore never capture derived-structure pages at all, and restore carries no
backup-specific replay code. Periodic **compaction** collapses the chain into a fresh base to bound restore time; a
**retention policy** prunes old points.

> Design note: the original draft ([6-part series](../../../claude/design/Durability/PitBackup/README.md)) protected
> the live capture window with a scoped Copy-on-Write shadow buffer. That mechanism was dropped in the 2026-06-11
> redesign ([MinimalWal README §P3, decision D11](../../../claude/design/Durability/MinimalWal/README.md)) in favor
> of plain post-checkpoint reads — consistency is restored at restore time by RecoveryDriver instead of guaranteed at
> capture time, which is simpler and reuses an already-proven healing path.

## 💻 Usage

Not implemented — there is no API or CLI to create, restore, or manage backups today. The sketch below shows the
*intended* shape (a CLI tool plus an in-process retention policy) for planning purposes only; none of this exists yet:

```csharp
// Illustrative only — not a real/current Typhon API.

// Planned: configure automatic compaction/pruning alongside the engine.
// var policy = new BackupRetentionPolicy
// {
//     MaxAge           = TimeSpan.FromDays(7),
//     MaxTotalSize     = 20L * 1024 * 1024 * 1024,
//     CompactThreshold = 42,   // compact after 42 incrementals (~1 week at 4h intervals)
//     MinKeep          = 5,
// };
```

```
# Planned CLI surface (separate process / offline — not engine-embedded)
typhon-backup create   --db <path> --dest <backup-dir>
typhon-backup restore  --source <backup-dir> --target <db-path> [--point <id|datetime>]
typhon-backup compact  --source <backup-dir>
typhon-backup prune    --source <backup-dir> --before <id|datetime>
typhon-backup verify   --source <backup-dir> [--point <id|datetime>]
```

## ⚠️ Guarantees & limits

- **Not implemented.** No code exists under `src/Typhon.Engine/Backup` today; everything above is a design sketch, subject to change.
- **I/O proportional to change, not size** — a backup point costs O(changed pages); only periodic compaction costs O(database size), amortized over the chain (design target: ~15 GB per incremental vs. ~205 GB for a naive full-copy scheme, at 100 GB DB / 10% churn).
- **Self-contained per chain** — each `.pack` file is independently checksummed (header, footer, and per-page CRC32C); the base point needs no live WAL to restore.
- **Zero transaction-path overhead when idle** — the only steady-state cost is the checkpoint pipeline's one bit-OR per flushed page.
- **Restore heals, it doesn't trust** — the assembled base is run through `RecoveryDriver`, the identical mechanism crash recovery uses; derived structures are always rebuilt, never restored byte-for-byte.
- **One backup at a time** — compaction and pruning run offline against `.pack` files only and never touch the live engine.
- **Fine-grained point-in-time restore is a separate, optional layer** — retaining WAL segments past checkpoint (revising [ADR-014](../../../claude/adr/014-no-point-in-time-recovery.md)) is planned but not part of the base design; without it, restore granularity is "whichever backup point you took."

## 🔗 Related

- Sibling: [Checkpoint v2 (SnapshotStore pipeline)](checkpoint-v2/README.md) — a backup point forces a checkpoint cycle and reads its dirty-page bitmap
- Sibling: [Pluggable WAL I/O Backend (IWalFileIO seam)](../Hosting/wal-io-injection-seam.md) — the kind of file-system access seam backup tooling would need for its own I/O

<!-- Overview: claude/overview/07-backup.md -->
<!-- Design (original 6-part spec — file format, dirty bitmap, chain-walk reconstruction, compaction/pruning still current): claude/design/Durability/PitBackup/README.md -->
<!-- Design (current resolution — supersedes the CoW capture mechanism, adds RecoveryDriver-based restore): claude/design/Durability/MinimalWal/README.md §P3 / Decision D11 -->
<!-- ADRs: 014 — No PITR (claude/adr/014-no-point-in-time-recovery.md, revision planned), 028 — CoW snapshot backup (claude/adr/028-cow-snapshot-backup.md, capture mechanism superseded), 029 — Reverse-delta snapshots (claude/adr/029-reverse-delta-incremental-snapshots.md, superseded by forward incrementals), 030 — Dual-limit retention policy (claude/adr/030-dual-limit-retention-policy.md) -->
