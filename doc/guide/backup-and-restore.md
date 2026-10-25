---
uid: guide-backup-and-restore
title: 'Backup & restore in production'
description: 'Run Typhon with backups you can trust: set up a store, watch its health, prove points restore, mirror it off-site, and the procedures for a lost volume, a bad deploy, damaged data and a failing backup.'
---

# Backup & restore in production

The engine backs itself up while it runs: it takes a point on a schedule, keeps a bounded history, archives its WAL if you ask it to, and
reports its health. The `typhon` tool reads the same store to list, verify, drill and restore. This page is the operator's side: how to set
it up, how to know it works, and what to do when something goes wrong.

How it works, and its guarantees: [Point-in-Time Incremental Backup](../feature-set/Durability/pit-backup.md).

---

## 1. 🧰 Set up

**Put the store on another volume**, ideally another machine (a mounted share) or a synced directory. A store on the database's own disk
protects against a bad deploy, not against losing the disk.

```csharp
services.AddTyphon(o => o.DatabaseFile("/data/world.typhon").Backup(new BackupOptions
{
    StorePath = "/backups/world",
    Interval  = TimeSpan.FromHours(1),        // 0 = no schedule: points only on demand or by request
    Retention = new BackupRetentionPolicy     // every limit stated; 0 = no limit
    {
        MinKeep = 3, MaxAge = TimeSpan.FromDays(14), MaxCount = 0, MaxTotalBytes = 0,
        CompactThreshold = 24,                // fold a chain into a new base once a restore would read 24 packs; 0 = never
        KeepLastDrilled = true,               // never delete the newest point a drill proved
    },
    DrillWindow = TimeSpan.FromDays(1),       // health warns when no drill passed within it; 0 = not monitored
    Archive   = new ArchiveOptions            // optional: archive every WAL segment, restore to any LSN or instant
    {
        MaxSegmentAge     = TimeSpan.FromSeconds(30),   // the recovery point: at most this much commit history is at risk
        BacklogLimitBytes = 8L << 30,                   // past this much un-archived WAL, the oldest is released as a gap
    },
}));
services.AddTyphonBackupMetrics();            // the Typhon.Backup meter: health, ages, chain depth, store bytes, archive lag
```

- The first point is **full**; the following ones copy only the pages written since. Any doubt (a crash, an offline repair, a copied bundle,
  a restore) makes the next point full again: never a wrong incremental.
- **Without `Archive`**, the recovery point is `Interval`: a lost volume loses at most the commits since the last point. **With it**, the
  recovery point is `MaxSegmentAge`, and you can restore to any LSN or instant the archive covers.
- **Size the store for the WAL rate** when archiving: a store slower than the WAL falls behind, and past `BacklogLimitBytes` the oldest
  segments are released as a recorded gap (health Degraded, a new point is taken at once). No restore crosses a gap.
- Install the tool where you operate: `dotnet tool install --global Typhon.Cli --prerelease`.

## 2. 📟 Watch it

`typhon backup status` turns the engine's published status into an **exit code**, so any scheduler or monitoring agent can alert on it:

| Exit | Health | Meaning |
|---|---|---|
| 0 | Ok | Points are recent, the store is reachable, the last drill passed |
| 1 | Warning or Degraded | Still restorable, with less margin: a stale point, a failure, a suspect page, an archive gap or lag, no recent drill, a kept point a compaction found damaged |
| 3 | Failing or Off | No restorable recent point, the store is unreachable, failures in a row, the engine refuses to capture, a kept point retention broke, or backup is not configured |
| 2 | — | The command itself failed (bad path, unreadable status file) |

The text output, or `--format json`, gives the reason. A stopped engine's last status degrades by itself as time passes, so a dead process
does not look healthy.

**Off is not the same answer everywhere, on purpose.** `BackupHealthCheck`, the `ITyphonHealthCheck` an application exposes on its own health
endpoint, maps Off to **Healthy**: an engine with no backup configured is not a failing service. `typhon backup status` exits **3** for Off: a
database you monitor *for backups* that has none is not healthy. Alert on the one that asks your question. Every other health maps the same
way in both (Ok healthy; Warning and Degraded degraded, exit 1; Failing unhealthy, exit 3).

```bash
# Linux (cron): every 5 minutes, alert on anything but 0
*/5 * * * * typhon backup status /data/world.typhon --format json > /var/log/typhon/backup-status.json || /usr/local/bin/alert "typhon backup: $?"
```

```powershell
# Windows (Task Scheduler): a script whose exit code your monitoring agent reads
schtasks /Create /TN "Typhon backup status" /SC MINUTE /MO 5 /RU SYSTEM `
  /TR "typhon backup status D:\data\world.typhon --format json"
```

For a store whose database runs elsewhere, or only offline: `typhon backup status --store /backups/world --stale-after 1d`.

## 3. ✅ Prove it: drills

A backup that was never restored is a hope. A **drill** restores a point into a scratch directory, opens the database (its WAL tail
replays), checks it deep, records the verdict in the store, and deletes the scratch. Health and retention read those records.

Opening a point replays its WAL tail, and replaying the application's records needs the application's components registered. So:

- **From the application** (the general case): drill in-process with the application's registrations.

  ```csharp
  var report = BackupOperations.Drill("/backups/world", PointSelector.Latest, new BackupDrillOptions
  {
      ScratchDirectory = "/scratch/typhon-drill",       // outside the store; needs room for one whole image
      OpenAndClose     = bundle => BackupOperations.OpenAndCloseRestored(bundle, o => o.Register<Transform>().Register<Health>()),   // the app's own registrations
  });
  // report.Outcome: Passed, PassedWithWarnings, Failed (the point is not trustworthy), Error (the drill could not run)
  ```

  Run it daily, from an admin command or a small tool built with the application.
- **From the `typhon` tool**: `typhon backup drill --store /backups/world --scratch /scratch/typhon-drill` drills a point without the
  application's assemblies. That works for a point with nothing to replay (an offline point of a cleanly closed database). For a point whose
  tail holds the application's records it ends in **Error** (exit 2), naming the component it could not replay: that says nothing about the
  point, only that the tool cannot open it.

Drill exit codes: 0 passed, 1 passed with warnings, 3 failed, 2 could not run.

## 4. 🌍 Mirror it off-site

Packs never change once published, the catalog is rebuilt from them, and every byte is checksummed, so a plain mirror is a valid copy.
Skip temporaries (`*.tmp`) and the lock files.

```bash
rsync -a --delete --exclude='*.tmp' --exclude='store.lock' --exclude='archive.lock' /backups/world/ offsite:/backups/world/
```

```powershell
robocopy D:\backups\world \\nas\backups\world /MIR /XF *.tmp store.lock archive.lock
```

`--delete` / `/MIR` propagate retention's deletions. Leave them out for an append-only off-site copy, and prune that copy separately
(`typhon backup prune --store … --min-keep … --max-age …`). Keep the drill scratch outside the store, or the mirror copies a whole image
mid-drill (the drill refuses a scratch inside the store).

## 5. 🚑 Procedures

### The volume is lost

```bash
typhon backup restore --store /backups/world --to /data/                       # the newest restorable point
typhon backup restore --store /backups/world --time 2026-10-21T14:03:00Z --to /data/   # with Archive: an instant
```

Start the application: the open runs ordinary crash recovery. Then `typhon check /data/world.typhon`. The engine's next point is full, on a
new timeline.

### A bad deploy or a mass delete at time T

1. Find the last good LSN. With `Archive`: `typhon backup log --store /backups/world --from 2026-10-21T13:58:00Z --to 2026-10-21T14:04:00Z --commits`
   lists every frame of the range with its LSN, transaction and approximate time.
2. Restore beside the live database, never over it: `typhon backup restore --store /backups/world --lsn 48211907 --to /data/restored/`
   (`--dry-run` first shows the point, the archived segments and the LSN it would end at).
3. Inspect the restored bundle in the Workbench, then swap it in (`--replace`) or extract what you need.

A restore never crosses a schema change or a bulk load: each starts a new timeline, and an LSN before it restores from the parent timeline.

### `typhon check` reports data loss

Repair works from within; data that is gone comes back from a backup. Verify the candidates first:
`typhon backup verify --store /backups/world --point 42 --level deep`, then restore the newest point that predates the damage. A point
captured after the damage holds it faithfully: its deep verify reports it as a warning, "the image carries damage the live database had".

### Health is Degraded or Failing

`typhon backup status /data/world.typhon` names the reason. Fix the store (space, mount, permissions), then confirm it returns to `Ok`. If
the reason is a broken chain, take a full point now: `typhon backup create /data/world.typhon --full --wait` asks the running engine (it
refuses a `--store` that is not the engine's own: the engine writes only to its store).

`KeptPointDamaged` means a compaction, the one routine full read of a kept chain, found one of its packs damaged: `verify --level full` the
store, drill the points of that chain, and take a full point. `typhon backup prune` reports the same case with exit 3. `KeptPointBroken`
(Failing) means a retention left a point it promised to keep unrestorable: a defect, file a bug with the log.

### A drill failed

Treat it as an incident: the backups are not trustworthy until proven otherwise. Keep the drill's report, run
`typhon backup verify --store /backups/world --level deep`, drill the previous points, and file a bug with the report.

---

## 🧩 Key concepts & types

**Concepts:** [Point-in-Time Incremental Backup](../feature-set/Durability/pit-backup.md) · [WAL & checkpoint](../key-concepts/wal-checkpoint.md) ·
[Crash recovery](../feature-set/Durability/crash-recovery/README.md).

**Exact calls:** `BackupOptions` (`StorePath`, `Interval`, `Retention`, `DrillWindow`, `Archive`, `AcceptRequests`) · `BackupRetentionPolicy` ·
`ArchiveOptions` · `engine.Backup.CreateAsync` / `engine.Backup.Status` · `AddTyphonBackupMetrics` · `BackupOperations` (`Restore`, `Verify`,
`Drill`, `OpenAndCloseRestored`, `PlanRestore`, `ReadLog`) · `PointSelector` (`Latest`, `ById`, `AtLsn`, `AtTime`) ·
`typhon backup create | status | list | verify | restore | log | drill | compact | prune`.
