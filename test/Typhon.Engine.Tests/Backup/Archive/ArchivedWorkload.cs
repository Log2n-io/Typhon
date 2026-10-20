using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>The committed state of the workload once a given LSN was published: what a restore to that LSN must open to.</summary>
internal sealed record ArchiveShadow(long Lsn, Dictionary<EntityId, int> Versioned, Dictionary<EntityId, int> SingleVersion);

/// <summary>What an archived workload left: the store, the oracle's history, the points, and every frame the archive holds.</summary>
internal sealed class ArchivedWorkloadResult
{
    public required List<ArchiveShadow> History { get; init; }

    public required List<uint> Points { get; init; }

    public required Guid Timeline { get; init; }

    /// <summary>Every frame of every archived segment, ascending LSN: (last LSN, first record LSN, records).</summary>
    public required List<(long LastLsn, long FirstLsn, int Records)> Frames { get; init; }

    public required List<ArchiveTimeSample> Samples { get; init; }

    public ArchiveShadow Expected(long lsn) => History.Last(h => h.Lsn <= lsn);
}

abstract partial class ArchiveTestBase<T>
{
    private double _logicalSeconds;

    /// <summary>One archiver pass: a step on the logical clock (a sample), a switch, another step, the worker's pass.</summary>
    protected void ArchiveAll(DatabaseEngine dbe, WalArchiver archiver)
    {
        Tick(archiver, _logicalSeconds += 1);
        var durable = dbe.WalManager.DurableLsn;
        Switch(dbe);
        Tick(archiver, _logicalSeconds += 1);
        Tick(archiver, _logicalSeconds += 1);
        Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Archiving), $"premise: the archiver archives ({archiver.State.LastError})");
        Assert.That(archiver.ArchivedThroughLsn, Is.GreaterThanOrEqualTo(durable), "premise: everything durable before the switch is archived");
    }

    /// <summary>
    /// The archived workload of T12 (impl/1418 §8.2): a full online point, Versioned commits (spawns, updates, destroys, one transaction past one chunk) and
    /// SingleVersion updates made durable by fences, archived as it goes; a clean close and reopen; a hard crash and its recovery open; an offline point of
    /// the cleanly closed database; more commits. One timeline throughout. The history records the committed state after every unit.
    /// </summary>
    protected ArchivedWorkloadResult RunArchivedWorkload(int seed, int stepsPerPhase = 8)
    {
        var rng = new Random(seed);
        var history = new List<ArchiveShadow>();
        var versioned = new Dictionary<EntityId, int>();
        var singleVersion = new Dictionary<EntityId, int>();
        var points = new List<uint>();
        var next = 1;
        Guid timeline;
        string bundle, wal;

        void Record(DatabaseEngine dbe) => history.Add(new ArchiveShadow(dbe.WalManager.LastPublishedLsn, new(versioned), new(singleVersion)));

        void Steps(DatabaseEngine dbe, WalArchiver archiver, int count)
        {
            for (var s = 0; s < count; s++)
            {
                switch (rng.Next(5))
                {
                    case 0:
                    case 1:
                        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
                        {
                            for (var i = rng.Next(1, 4); i > 0; i--)
                            {
                                var v = next++;
                                versioned[t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(v)))] = v;
                            }

                            foreach (var id in versioned.Keys.OrderBy(_ => rng.Next()).Take(rng.Next(0, 4)).ToList())
                            {
                                var v = next++;
                                t.OpenMut(id).Set(CompAArch.A, new CompA(v));
                                versioned[id] = v;
                            }

                            t.Commit();
                        }

                        break;
                    case 2:
                        if (versioned.Count > 4)
                        {
                            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                            var victim = versioned.Keys.ElementAt(rng.Next(versioned.Count));
                            t.Destroy(victim);
                            t.Commit();
                            versioned.Remove(victim);
                        }

                        break;
                    case 3:
                        if (singleVersion.Count > 0)
                        {
                            using (var t = dbe.CreateQuickTransaction())
                            {
                                foreach (var id in singleVersion.Keys.ToList())
                                {
                                    var v = next++;
                                    t.OpenMut(id).Set(SvTestArchetype.SvComp, new CompSmSingleVersion(v));
                                    singleVersion[id] = v;
                                }

                                t.Commit();
                            }

                            dbe.WriteTickFence(next);
                        }

                        break;
                    default:
                        Tick(archiver, _logicalSeconds += 1);
                        break;
                }

                Record(dbe);
            }
        }

        // Session 1: the base point first, then commits, a transaction past one chunk, fences; archived as it goes.
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            timeline = dbe.TimelineId;
            bundle = dbe.MMF.BundleDirectory;
            wal = dbe.WalDirectory;
            var archiver = NewArchiver(dbe);
            Tick(archiver, _logicalSeconds += 1);
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 6; i++)
                {
                    singleVersion[t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(new CompSmSingleVersion(i + 1)))] = i + 1;
                }

                t.Commit();
            }

            // A SingleVersion spawn is durable at the checkpoint that writes it (CM-07): taken before the point, so every restore holds them.
            ForceCheckpoint(dbe);
            Record(dbe);
            points.Add(CapturePoint(dbe, BackupKind.Full));
            Record(dbe);
            Steps(dbe, archiver, stepsPerPhase);
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 1_500; i++)
                {
                    var v = next++;
                    versioned[t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(v)))] = v;
                }

                t.Commit();
            }

            Record(dbe);
            ArchiveAll(dbe, archiver);
            Steps(dbe, archiver, stepsPerPhase);
            ArchiveAll(dbe, archiver);
        }

        // Session 2: a clean reopen, commits, a hard crash.
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var archiver = NewArchiver(dbe);
            Steps(dbe, archiver, stepsPerPhase);
            ArchiveAll(dbe, archiver);
            dbe.SimulateHardCrash();
        }

        // Session 3: the crash open, commits, a clean close.
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var archiver = NewArchiver(dbe);
            Steps(dbe, archiver, stepsPerPhase);
            ArchiveAll(dbe, archiver);
        }

        // An offline point of the cleanly closed database: a clean image (WA-12).
        points.Add(BackupOperations.Create(bundle, StorePath, new BackupCreateOptions { WalDirectory = wal }).BackupId);

        // Session 4: more commits on top of it.
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var archiver = NewArchiver(dbe);
            Steps(dbe, archiver, stepsPerPhase);
            ArchiveAll(dbe, archiver);
        }

        var archived = WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, timeline, trustIndex: false);
        Assert.That(archived.Unreadable, Is.Empty);
        var frames = new List<(long, long, int)>();
        var samples = new List<ArchiveTimeSample>();
        foreach (var s in archived.Segments)
        {
            using var reader = ArchivedSegmentReader.Open(StoreFileSystem.Instance, Path.Combine(new WalArchivePaths(StorePath).Timeline(timeline), s.File));
            samples.AddRange(reader.ReadSamples());
            var bytes = new byte[reader.Footer.ArchiveLength];
            reader.Decode(bytes);
            foreach (var f in WalFrames.Read(bytes, out _).Where(f => f.LastLsn > 0))
            {
                frames.Add((f.LastLsn, f.Records.Count > 0 ? f.Records.Min(r => r.Lsn) : f.LastLsn, f.Records.Count));
            }
        }

        return new ArchivedWorkloadResult { History = history, Points = points, Timeline = timeline, Frames = frames, Samples = samples };
    }

    /// <summary>An online point into the fixture's store.</summary>
    protected static uint CapturePoint(DatabaseEngine dbe, BackupKind kind)
    {
        var task = dbe.BackupCoordinator.CaptureAsync(new FileSystemBackupStore(StorePath), kind, CancellationToken.None);
        Assert.That(task.Wait(Budget), Is.True, "the capture did not finish");
        return task.Result.BackupId;
    }

    /// <summary>Restores <paramref name="point"/> into a fresh directory and returns the restored state, read through an engine that opens the bundle.</summary>
    protected (BackupRestoreResult Result, Dictionary<EntityId, int> Versioned, Dictionary<EntityId, int> SingleVersion, bool CrashPath) RestoreAndRead(
        PointSelector point, string name)
    {
        var dir = Path.Combine(TestStorage.Root, typeof(T).Name, CurrentDatabaseName + ".r", name);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        var result = BackupOperations.Restore(StorePath, point, dir);
        SuppressArchiving = true;
        try
        {
            using var provider = ProviderFor(dir);
            using var scope = provider.CreateScope();
            var dbe = Open(scope);
            using var t = dbe.CreateQuickTransaction();
            var versioned = new Dictionary<EntityId, int>();
            foreach (var id in t.Query<CompAArch>().Execute())
            {
                versioned[id] = t.Open(id).Read(CompAArch.A).A;
            }

            var singleVersion = new Dictionary<EntityId, int>();
            foreach (var id in t.Query<SvTestArchetype>().Execute())
            {
                singleVersion[id] = t.Open(id).Read(SvTestArchetype.SvComp).Value;
            }

            return (result, versioned, singleVersion, dbe.CrashRecoveryAtOpen);
        }
        finally
        {
            SuppressArchiving = false;
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The restored state differs from the oracle's: the first difference, or null.</summary>
    protected static string Diff(ArchiveShadow expected, Dictionary<EntityId, int> versioned, Dictionary<EntityId, int> singleVersion)
    {
        string Compare(string what, Dictionary<EntityId, int> want, Dictionary<EntityId, int> got)
        {
            if (want.Count != got.Count)
            {
                return $"{what}: {got.Count} entities, expected {want.Count}";
            }

            foreach (var (id, v) in want)
            {
                if (!got.TryGetValue(id, out var g) || g != v)
                {
                    return $"{what}: entity {id} is {(got.ContainsKey(id) ? g.ToString() : "absent")}, expected {v}";
                }
            }

            return null;
        }

        return Compare("versioned", expected.Versioned, versioned) ?? Compare("single-version", expected.SingleVersion, singleVersion);
    }
}
