using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.PinMeasure.SvRow", 1, StorageMode = StorageMode.SingleVersion)]
public struct PmSvRow
{
    [Index(AllowMultiple = true)] public long Owner;
    public long Seq, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11;
}

[Archetype]
class PmSvArch : Archetype<PmSvArch>
{
    public static readonly Comp<PmSvRow> Row = Register<PmSvRow>();
}

[Component("Typhon.Test.PinMeasure.VRow", 1)]
public struct PmVRow
{
    [Index(AllowMultiple = true)] public long Owner;
    public long Seq, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11;
}

[Archetype]
class PmVArch : Archetype<PmVArch>
{
    public static readonly Comp<PmVRow> Row = Register<PmVRow>();
}

/// <summary>
/// #1532 measurement, not a verifier: what a transaction's index range enumeration keeps unevictable while it runs, against the same reads made as point
/// opens. A background thread makes short point reads meanwhile, as concurrent requests do.
/// </summary>
[TestFixture]
[Explicit("measurement for #1532")]
[Category("Manual")] // a measurement that prints numbers and asserts no bound: nothing for CI to gate on
class IndexScanPinMeasure : TestBase<IndexScanPinMeasure>
{
    private const int Rows = 40_000, Owners = 8;

    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 64L * 1024 * 1024)]
    public void Measure([Values] bool versioned, [Values] bool viaIndex)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PmSvRow>();
        dbe.RegisterComponentFromAccessor<PmVRow>();
        dbe.InitializeArchetypes();

        var ids = new EntityId[Rows];
        for (var start = 0; start < Rows; start += 2_000)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = start; i < start + 2_000; i++)
            {
                ids[i] = versioned
                    ? tx.Spawn<PmVArch>(PmVArch.Row.Set(new PmVRow { Owner = i % Owners, Seq = i }))
                    : tx.Spawn<PmSvArch>(PmSvArch.Row.Set(new PmSvRow { Owner = i % Owners, Seq = i }));
            }

            tx.Commit();
        }

        // Settle: a checkpoint, then measure from a quiet engine.
        dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(30));
        var mmf = dbe.MMF;
        var em = dbe.EpochManager;
        var stop = 0;
        long backgroundReads = 0;
        Exception backgroundError = null;
        var background = new Thread(() =>
        {
            try
            {
                ReadRandomly(dbe, ids, versioned, ref stop, ref backgroundReads);
            }
            catch (Exception ex)
            {
                backgroundError = ex;
            }
        }) { IsBackground = true };

        var samples = new List<string>();
        var maxProtected = 0;
        var read = 0;

        void Sample()
        {
            var p = mmf.CountEpochProtectedPages();
            maxProtected = Math.Max(maxProtected, p);
            samples.Add($"{read}:{p}e/{mmf.CountPagesWithSlotReferences()}s/{em.GlobalEpoch - em.CurrentThreadPinnedEpoch}lag");
        }

        var quiet = mmf.CountEpochProtectedPages();
        background.Start();
        var sw = Stopwatch.StartNew();
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            if (viaIndex && versioned)
            {
                using var e = tx.EnumerateIndex<PmVRow, long>(dbe.GetIndexRef<PmVRow, long>(x => x.Owner), 3, 3);
                while (e.MoveNext())
                {
                    _ = e.Current.Component.Seq;
                    if (++read % 1000 == 0) { Sample(); }
                }
            }
            else if (viaIndex)
            {
                using var e = tx.EnumerateIndex<PmSvRow, long>(dbe.GetIndexRef<PmSvRow, long>(x => x.Owner), 3, 3);
                while (e.MoveNext())
                {
                    _ = e.Current.Component.Seq;
                    if (++read % 1000 == 0) { Sample(); }
                }
            }
            else
            {
                for (var i = 3; i < Rows; i += Owners)
                {
                    _ = versioned ? tx.Open(ids[i]).Read(PmVArch.Row).Seq : tx.Open(ids[i]).Read(PmSvArch.Row).Seq;
                    if (++read % 1000 == 0) { Sample(); }
                }
            }
        }
        finally
        {
            sw.Stop();
            Volatile.Write(ref stop, 1);
            Assert.That(background.Join(TimeSpan.FromSeconds(30)), Is.True, "the background reader never stopped");
        }

        Assert.That(backgroundError, Is.Null, $"background reader failed: {backgroundError}");
        var after = mmf.CountEpochProtectedPages();
        TestContext.Out.WriteLine($"MEASURE {(versioned ? "Versioned" : "SingleVersion")} {(viaIndex ? "EnumerateIndex" : "point opens")}: {read} rows "
                                  + $"in {sw.Elapsed.TotalMilliseconds:F1} ms, cache {mmf.CacheSlotCount} pages; epoch-protected quiet {quiet}, "
                                  + $"peak {maxProtected}, after {after}; background reads {backgroundReads}; "
                                  + $"samples [e=epoch-protected s=slot-ref lag=reader pin age]: {string.Join(" ", samples)}");
    }

    /// <summary>Short point reads of random rows, in a transaction of 16 at a time, until <paramref name="stop"/> is set: concurrent requests.</summary>
    private static void ReadRandomly(DatabaseEngine dbe, EntityId[] ids, bool versioned, ref int stop, ref long reads)
    {
        var rng = new Random(7);
        while (Volatile.Read(ref stop) == 0)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var k = 0; k < 16; k++)
            {
                var id = ids[rng.Next(Rows)];
                _ = versioned ? tx.Open(id).Read(PmVArch.Row).Seq : tx.Open(id).Read(PmSvArch.Row).Seq;
            }

            Interlocked.Increment(ref reads);
        }
    }
}
