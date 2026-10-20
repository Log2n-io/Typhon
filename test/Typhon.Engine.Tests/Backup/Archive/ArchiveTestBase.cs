using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Shared harness of the WAL archive fixtures: a disk WAL of small segments, archiving configured into a store per test, checkpoints forced by the test
/// (never by the clock), segments sealed by switch requests whose service the test waits for.
/// </summary>
abstract partial class ArchiveTestBase<T> : BackupTestBase<T> where T : ArchiveTestBase<T>
{
    /// <summary>Every segment of these fixtures has this declared size, so the backlog arithmetic is exact.</summary>
    protected const uint SegmentSize = 256 * 1024;

    /// <summary>Whether the engine opens with archiving configured.</summary>
    protected virtual bool ArchivingOn => true;

    /// <summary>Set to open restored bundles without archiving (read when an engine is resolved).</summary>
    protected bool SuppressArchiving { get; set; }

    /// <summary>The backlog limit; large by default, so nothing is released unless a test lowers it.</summary>
    protected virtual long BacklogLimit => 64L * SegmentSize;

    /// <summary>The store the archive is written to, beside the bundle, private to the test.</summary>
    protected static string StorePath => Path.Combine(TestStorage.Root, typeof(T).Name, CurrentDatabaseName + ".store");

    protected static string WalDirectoryOf(DatabaseEngine dbe) => dbe.WalDirectory;

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Wal.SegmentSize = SegmentSize;
        o.Wal.InitialSegmentSize = SegmentSize;
        o.Wal.PreAllocateSegments = 1;
        o.WalArchive = ArchivingOn && !SuppressArchiving
            ? new WalArchiveConfiguration
            {
                Options = new ArchiveOptions { MaxSegmentAge = TimeSpan.FromSeconds(5), BacklogLimitBytes = BacklogLimit },
                StorePath = StorePath,
            }
            : null;
    }

    public override void Setup()
    {
        DeleteStore();
        base.Setup();
    }

    public override void TearDown()
    {
        base.TearDown();
        DeleteStore();
    }

    private static void DeleteStore()
    {
        if (Directory.Exists(StorePath))
        {
            Directory.Delete(StorePath, true);
        }
    }

    /// <summary>Seals the active segment and waits, bounded, until the writer served the request.</summary>
    protected static void Switch(DatabaseEngine dbe)
    {
        var ticket = dbe.WalManager.RequestSegmentSwitch();
        var clock = Stopwatch.StartNew();
        while (dbe.WalManager.SwitchesServed < ticket)
        {
            Assert.That(clock.Elapsed, Is.LessThan(Budget), "the WAL writer did not serve the segment switch");
            Thread.Yield();
        }
    }

    /// <summary>Spawns <paramref name="count"/> entities durably, then seals the segment they are in; returns the sealed segment.</summary>
    protected static WalSealedSegment SpawnAndSeal(DatabaseEngine dbe, int count, int seed = 0)
    {
        Spawn(dbe, count, seed);
        Switch(dbe);
        var sealedSegments = Sealed(dbe);
        return sealedSegments[^1];
    }

    /// <summary>The sealed segments now, ascending.</summary>
    protected static List<WalSealedSegment> Sealed(DatabaseEngine dbe)
    {
        var result = new List<WalSealedSegment>();
        long through = 0;
        while (true)
        {
            var snapshot = dbe.WalManager.SegmentManager.ArchiveSnapshot(through, 0);
            if (snapshot.PendingCount == 0)
            {
                return result;
            }

            result.Add(snapshot.Next);
            through = snapshot.Next.LastLsn;
        }
    }

    protected static void ForceCheckpoint(DatabaseEngine dbe) => Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);

    /// <summary>The engine's archiver, its worker not started unless <paramref name="start"/>: the test drives Step and ArchivePending itself.</summary>
    protected static WalArchiver NewArchiver(DatabaseEngine dbe, IStoreFileSystem fs = null, bool start = false) =>
        dbe.CreateWalArchiver(dbe.ArchiveConfiguration.Options, StorePath, fs, start);

    /// <summary><paramref name="seconds"/> on the monotonic clock the archiver ages segments by.</summary>
    protected static long Mono(double seconds) => (long)(seconds * Stopwatch.Frequency);

    /// <summary>The wall clock matching <see cref="Mono"/> on the test's logical timeline.</summary>
    protected static DateTime Utc(double seconds) => new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc).AddTicks((long)(seconds * TimeSpan.TicksPerSecond));

    /// <summary>One scheduler tick at logical second <paramref name="seconds"/>, then the worker's pass, both on the test's thread.</summary>
    protected static void Tick(WalArchiver archiver, double seconds)
    {
        archiver.Step(Utc(seconds), Mono(seconds), default);
        archiver.ArchivePending(default);
    }

    /// <summary>The store's archived segments of the database's timeline, from the files.</summary>
    protected static ArchivedTimeline Archived(DatabaseEngine dbe) => WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, dbe.TimelineId, trustIndex: false);
}
