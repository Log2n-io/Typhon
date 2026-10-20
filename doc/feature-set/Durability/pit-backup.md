---
uid: feature-durability-pit-backup
title: 'Point-in-Time Incremental Backup'
description: 'Online, forward-incremental page backups; each point is a frozen data-file image plus a short WAL tail, restored through crash recovery.'
---

# Point-in-Time Incremental Backup
> Online, forward-incremental page backups; each point is a frozen data-file image plus a short WAL tail, restored through crash recovery.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](./README.md)
**Assumes:** [Crash Recovery (RecoveryDriver)](crash-recovery/README.md)

## 🎯 What it solves

Without a backup, a lost disk, an accidental mass delete or a bad deploy is unrecoverable: the WAL is reclaimed after each
checkpoint, so the live data file is the only copy. A naive file copy is not enough either. A checkpointed data file is
only crash-consistent, so a copy taken while the engine runs holds half-applied transactions. PIT Backup gives Typhon
restorable points, taken while the engine runs, at a cost that follows the change rate. Restore reuses the engine's proven
crash recovery instead of inventing a second one.

## ⚙️ How it works (in brief)

Every page the engine writes to the data file sets a bit in a **change map**. A backup point **freezes** the data file at an
instant (a short write gate, never touching transactions). It then copies the pages written since the previous point. If a
checkpoint wants to overwrite one of those pages before it has been copied, the old page is copied first
(**copy-before-write**, on the checkpoint's cold path). The point also stores the **WAL tail**: the few milliseconds of log
the frozen image needs to be consistent. Everything goes, LZ4-compressed and CRC-covered, into one immutable `.tpack` file
in a backup **store**.

**Restoring** a point assembles the newest copy of every page along its chain, puts the tail in `wal/`, and opens the
database. The engine sees an unclean shutdown and runs ordinary crash recovery, so a restore is equivalent to a hard crash
at the freeze instant. **Compaction** folds a chain into a new base inside the store. **Retention** keeps a bounded,
always-restorable history. An optional **archiving** layer copies every sealed WAL segment to the store. That brings the
recovery point objective down to seconds and allows restoring to any LSN or time.

## 💻 Usage

The engine backs itself up on a schedule into its store, applies the retention policy after each point, and publishes its health:

```csharp
services.AddTyphon(o => o.DatabaseFile("world.typhon").Backup(new BackupOptions
{
    StorePath = "/backups/world",
    Interval  = TimeSpan.FromHours(1),                          // required: 0 = no schedule (on demand and requests only)
    Retention = new BackupRetentionPolicy                        // required (null = no automatic retention); every limit stated, 0 = none
    {
        MinKeep = 3, MaxAge = TimeSpan.FromDays(7), MaxCount = 0, MaxTotalBytes = 0, CompactThreshold = 24, KeepLastDrilled = true,
    },
    DrillWindow = TimeSpan.FromDays(1),                          // health warns when no drill is that recent; 0 = not monitored
}));

var point  = await engine.Backup.CreateAsync();                  // on demand, into the configured store
var health = engine.Backup.Status.Health;                         // Off, Ok, Warning, Degraded, Failing — and Status.ReasonText says why
services.AddTyphonBackupMetrics();                               // the Typhon.Backup meter: health, ages, chain depth, store bytes, RPO
```

The `typhon` tool does the rest, offline or by asking the running engine through its bundle directory:

```
typhon backup create  <bundle> [--store S] [--full] [--wait]   # asks the running engine, or captures a closed database
typhon backup status  <bundle> | --store S [--stale-after 1d]  # exit code = health: 0 ok, 1 warning/degraded, 3 failing/off
typhon backup list    --store S [--restorable]                 # points, chains, sizes, drill results
typhon backup verify  --store S [--level quick|full|deep]
typhon backup restore --store S [--point X | --lsn L | --time T [--timeline t]] --to <bundle> [--dry-run]
typhon backup log     --store S [--timeline t] [--lsn-from L1 --lsn-to L2 | --from T1 --to T2] [--commits]
typhon backup drill   --store S --scratch D                    # restore to scratch, open, check deep, record, delete
typhon backup compact --store S [--point X]
typhon backup prune   --store S --min-keep 3 --max-age 7d [--dry-run]
```

## ⚠️ Guarantees & limits

- **Not yet built:** WAL archiving (seconds of RPO, restore to an LSN or a time, `typhon backup log`) and the Workbench backup panel.
- **Zero transaction-path cost.** The only steady-state cost is one atomic bit-set per data-file page write; a backup's
  copy-before-write runs on the checkpoint's write path, never on a commit.
- **I/O proportional to change.** An incremental copies only the pages written since the previous point; a full or
  compacted base costs O(database size).
- **Restore ≡ crash recovery.** A restored point yields exactly the transactions committed up to its point LSN, via the
  unchanged recovery path.
- **Any doubt costs a full backup, never a wrong one.** After a crash, an offline repair or a copied bundle, the next point
  is full.
- **Every byte in the store is checksummed**; `verify` checks packs, chains and (deep) the database structure through the
  same checks `typhon check` uses.
- **Backup never endangers the engine.** A backup that cannot keep up aborts and retries; an unreachable store degrades
  health (and, with archiving, the recovery point), it does not stop the engine.
- **Health never looks better than the backups are.** Stale backups, failures in a row, an unreachable store, a failed
  drill each lower it; a stopped engine's last status degrades by itself as time passes.
- **A drill proves a point restores.** It restores into a scratch directory, opens the database (recovery runs), checks it
  deep and records the result in the store. The `typhon` tool drills a point whose WAL tail it can replay without the
  application's assemblies; a point whose tail holds the application's records is drilled in-process, by a host that
  passes its own opener (`BackupDrillOptions.OpenAndClose`).
- **Archiving never loses WAL silently.** A segment leaves the database un-archived only when the archive falls behind past
  its backlog limit; that range is then a recorded gap, which no restore crosses, and a new point is taken. A restore to an
  LSN or an instant ends at the last whole transaction at or below it, and never crosses a schema change or a bulk load
  (each starts a new timeline).
- **One store per database**, a local or mounted filesystem path. Remote object stores, encryption and logical
  (entity-level) export are out of scope for now.

## 🔗 Related

- Sibling: [Checkpoint v2 (SnapshotStore pipeline)](checkpoint-v2/README.md): a backup forces a checkpoint and shares its data-file write path
- Sibling: [Pluggable WAL I/O Backend (IWalFileIO seam)](../Hosting/wal-io-injection-seam.md): the seam the tail copy and the archiver read through

<!-- Overview: claude/overview/07-backup.md -->
<!-- Design (v2, current): claude/design/Durability/PitBackup/README.md (01 model · 02 capture · 03 format · 04 restore/verify · 05 lifecycle · 06 archiving · 07 ops loop · 08 rules/proofs) -->
<!-- Design (v1, archived — CoW shadow buffer, superseded): claude/archive/Durability/PitBackup-v1/README.md -->
<!-- ADRs: 075 — frozen image + WAL tail (claude/adr/075-backup-is-a-frozen-image-plus-wal-tail.md); 014 — revised by 075; 028 — superseded by 075; 029 — superseded; 030 — retention policy (kept) -->
