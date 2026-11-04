using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The page trace (I7 of the huge-scan assessment): one record per page request while a capture runs, written with the storage type of every file page
/// for <c>scripts/page-trace-replay.py</c>. Off — the suite's default — the request paths carry no trace code; on, every field is recorded.
/// </summary>
[TestFixture]
class PageTraceTests : TestBase<PageTraceTests>
{
    private string _path;

    [SetUp]
    public void NewPath() => _path = Path.Combine(Path.GetTempPath(), $"typhon-pagetrace-{Guid.NewGuid():N}.tptr");

    [TearDown]
    public void DeletePath()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private DatabaseEngine BuildEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawns <paramref name="count"/> entities, then reads each one twice: the spawns load pages, the second read hits them.</summary>
    private static void Work(DatabaseEngine dbe, int count)
    {
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                ids.Add(tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        for (var pass = 0; pass < 2; pass++)
        {
            using var read = dbe.CreateReadOnlyTransaction();
            foreach (var id in ids)
            {
                read.Open(id).Read(ClAnt.Position);
            }
        }
    }

    /// <summary>
    /// Spawns <paramref name="count"/> entities in a first engine and closes it; reopens the file in a second, starts a capture and reads every entity
    /// twice: the first reads load their pages from the file (misses), the second find them resident (hits). Returns the reopened engine's scope.
    /// </summary>
    private IServiceScope ReopenAndRead(int count, long capacity, out DatabaseEngine dbe)
    {
        List<EntityId> ids;
        using (var first = ServiceProvider.CreateScope())
        {
            var created = first.ServiceProvider.GetRequiredService<DatabaseEngine>();
            created.RegisterComponentFromAccessor<ClPosition>();
            created.RegisterComponentFromAccessor<ClMovement>();
            created.InitializeArchetypes();
            ids = Spawn(created, count);
        }

        var scope = ServiceProvider.CreateScope();
        dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.InitializeArchetypes();
        dbe.StartPageTrace(capacity);
        for (var pass = 0; pass < 2; pass++)
        {
            using var read = dbe.CreateReadOnlyTransaction();
            foreach (var id in ids)
            {
                read.Open(id).Read(ClAnt.Position);
            }
        }

        return scope;
    }

    private static List<EntityId> Spawn(DatabaseEngine dbe, int count)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids.Add(tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0))));
        }

        Assert.That(tx.Commit(), Is.True);
        return ids;
    }

    private readonly record struct TraceRecord(long Timestamp, int FilePage, int Operation, ushort Thread, PageTraceKind Kind, bool Miss);

    private static (int Slots, int Pages, long Records, long Dropped, byte[] Types, List<TraceRecord> Rows) Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        Assert.That(reader.ReadUInt32(), Is.EqualTo(PageTraceSession.Magic), "magic");
        Assert.That(reader.ReadInt32(), Is.EqualTo(PageTraceSession.Version), "version");
        var slots = reader.ReadInt32();
        var pages = reader.ReadInt32();
        var records = reader.ReadInt64();
        var dropped = reader.ReadInt64();
        reader.ReadInt64();   // Stopwatch frequency
        var types = reader.ReadBytes(pages);
        var rows = new List<TraceRecord>();
        for (var i = 0L; i < records; i++)
        {
            var ts = reader.ReadInt64();
            var page = reader.ReadInt32();
            var op = reader.ReadInt32();
            var thread = reader.ReadUInt16();
            var kind = (PageTraceKind)reader.ReadByte();
            var flags = reader.ReadByte();
            reader.ReadInt32();
            rows.Add(new TraceRecord(ts, page, op, thread, kind, (flags & 1) != 0));
        }

        Assert.That(reader.BaseStream.Position, Is.EqualTo(reader.BaseStream.Length), "nothing after the records");
        return (slots, pages, records, dropped, types, rows);
    }

    /// <summary>Flag off: a capture records nothing — the request paths do not read the session — and still writes a valid, empty trace.</summary>
    [Test]
    public void TheFlagOff_EmitsNothing()
    {
        Assume.That(TelemetryConfig.ProfilerPageTraceEnabled, Is.False, "the suite runs with the page trace off; the gated pass turns it on");
        using var dbe = BuildEngine();
        dbe.StartPageTrace(10_000);
        Work(dbe, 200);

        Assert.That(dbe.WritePageTrace(_path), Is.Zero, "no record with the flag off");
        var trace = Read(_path);
        Assert.That(trace.Records, Is.Zero);
        Assert.That(trace.Pages, Is.EqualTo(dbe.MMF.StorageFilePageCount), "one storage type per file page");
    }

    /// <summary>No capture running: writing returns -1 and creates nothing.</summary>
    [Test]
    public void WritingWithoutACapture_WritesNothing()
    {
        using var dbe = BuildEngine();
        Assert.That(dbe.WritePageTrace(_path), Is.EqualTo(-1));
        Assert.That(File.Exists(_path), Is.False);
    }

    /// <summary>
    /// Flag on: every field of every record — the page within the file, the operation, this thread, the request's kind — and both hits and misses.
    /// </summary>
    [Test]
    [Category("TelemetryGated")]
    public void TheFlagOn_RecordsEveryField()
    {
        Assume.That(TelemetryConfig.ProfilerPageTraceEnabled, Is.True, "runs in the gated pass, TYPHON__PROFILER__PAGETRACE__ENABLED=true");
        using var scope = ReopenAndRead(5_000, 1_000_000, out var dbe);
        var written = dbe.WritePageTrace(_path);

        var trace = Read(_path);
        Assert.That(trace.Records, Is.EqualTo(written).And.GreaterThan(0));
        Assert.That(trace.Dropped, Is.Zero);
        Assert.That(trace.Slots, Is.GreaterThan(0));
        Assert.That(trace.Types, Has.Length.GreaterThanOrEqualTo(dbe.MMF.StorageFilePageCount), "a storage type per file page, and past its end");

        var thread = (ushort)Environment.CurrentManagedThreadId;
        var mine = 0;
        long previous = 0;
        foreach (var r in trace.Rows)
        {
            Assert.That(r.FilePage, Is.InRange(0, trace.Pages - 1), "a page of the file");
            Assert.That(r.Kind, Is.InRange(PageTraceKind.Epoch, PageTraceKind.NoSweep));
            Assert.That(r.Timestamp, Is.GreaterThan(0));
            if (r.Thread == thread)
            {
                mine++;
                Assert.That(r.Timestamp, Is.GreaterThanOrEqualTo(previous), "one thread's records in order");
                previous = r.Timestamp;
            }
        }

        Assert.That(mine, Is.GreaterThan(0), "this thread's requests are recorded under its id");
        Assert.That(trace.Rows.Exists(r => r.Kind == PageTraceKind.Epoch && r.Operation != 0), Is.True, "an epoch-tagged request carries its operation");
        Assert.That(trace.Rows.Exists(r => r.Miss), Is.True, "the first reads after the reopen load pages");
        Assert.That(trace.Rows.Exists(r => !r.Miss), Is.True, "the second reads are hits");
    }

    /// <summary>Flag on, after a reopen: each page is missed once, by the first read that loads it, and the reads after it are hits.</summary>
    [Test]
    [Category("TelemetryGated")]
    public void HitsAndMisses_AreRecordedApart()
    {
        Assume.That(TelemetryConfig.ProfilerPageTraceEnabled, Is.True, "runs in the gated pass");
        using var scope = ReopenAndRead(5_000, 1_000_000, out var dbe);
        dbe.WritePageTrace(_path);

        var seenMiss = new HashSet<int>();
        var hitsAfterMiss = 0;
        foreach (var r in Read(_path).Rows)
        {
            if (r.Miss)
            {
                Assert.That(seenMiss.Add(r.FilePage), Is.True, $"page {r.FilePage} missed twice: nothing here evicts");
            }
            else if (seenMiss.Contains(r.FilePage))
            {
                hitsAfterMiss++;
            }
        }

        Assert.That(hitsAfterMiss, Is.GreaterThan(0), "a page loaded during the capture is then hit");
    }

    /// <summary>Flag on: past its capacity a capture keeps the records it has and counts the rest.</summary>
    [Test]
    [Category("TelemetryGated")]
    public void ACaptureBeyondItsCapacity_CountsWhatItDropped()
    {
        Assume.That(TelemetryConfig.ProfilerPageTraceEnabled, Is.True, "runs in the gated pass");
        using var dbe = BuildEngine();
        dbe.StartPageTrace(10);
        Work(dbe, 500);
        Assert.That(dbe.WritePageTrace(_path), Is.EqualTo(10));
        var trace = Read(_path);
        Assert.That(trace.Records, Is.EqualTo(10));
        Assert.That(trace.Dropped, Is.GreaterThan(0));
    }
}
