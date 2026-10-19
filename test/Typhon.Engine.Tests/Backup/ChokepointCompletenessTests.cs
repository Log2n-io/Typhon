using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Schema.UnitTest.BkpChoke", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct BkpChokeV1
{
    [Index(AllowMultiple = true)]
    public int A;
    public int B;
}

[Component("Typhon.Schema.UnitTest.BkpChoke", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct BkpChokeV2
{
    [Index(AllowMultiple = true)]
    public int A;
    public int B;
    public int C;
}

[Archetype]
class BkpChokeV1Arch : Archetype<BkpChokeV1Arch>
{
    public static readonly Comp<BkpChokeV1> Comp = Register<BkpChokeV1>();
}

[Archetype]
class BkpChokeV2Arch : Archetype<BkpChokeV2Arch>
{
    public static readonly Comp<BkpChokeV2> Comp = Register<BkpChokeV2>();
}

/// <summary>
/// T3 (BKP-01): every page the engine writes to the data file is in the change map. The witness is independent of the chokepoint: the data file's
/// bytes before and after each session, so a write that bypasses the chokepoint is caught as a changed page the map does not hold. Sessions cover
/// create, a workload with forced checkpoints, a hard crash, a recovery open that migrates a schema, and two clean closes.
/// </summary>
class ChokepointCompletenessTests : TestBase<ChokepointCompletenessTests>
{
    private const string MissedMarker = "BKP-01 change map missed";

    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-01")]
    public void EveryWriteOfALifecycle_IsInTheMap()
    {
        string path;

        // Session 1: create, workload, checkpoint, more workload, hard crash.
        var session = OpenSession(null, register: dbe => dbe.RegisterComponentFromAccessor<BkpChokeV1>());
        path = session.Engine.MMF.DataFilePath;
        Spawn(session.Engine, 0, 200);
        Assert.That(session.Engine.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the checkpoint ran");
        Spawn(session.Engine, 200, 50);
        session.Engine.SimulateHardCrash();
        session.Scope.Dispose();
        AssertMapCoversChanges("create + crash", session, [], ReadFile(path));

        // Session 2: recovery open with a schema migration, workload, checkpoint, clean close.
        var before = ReadFile(path);
        session = OpenSession(path, register: dbe => dbe.RegisterComponentFromAccessor<BkpChokeV2>());
        Assert.That(session.Engine.CrashRecoveryAtOpen, Is.True, "premise: the second open recovers");
        using (var t = session.Engine.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < 50; i++)
            {
                t.Spawn<BkpChokeV2Arch>(BkpChokeV2Arch.Comp.Set(new BkpChokeV2 { A = i, B = i, C = i }));
            }

            t.Commit();
        }

        Assert.That(session.Engine.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
        session.Scope.Dispose();
        AssertMapCoversChanges("recovery open + migration + clean close", session, before, ReadFile(path));

        // Session 3: clean reopen, then clean close.
        before = ReadFile(path);
        session = OpenSession(path, register: dbe => dbe.RegisterComponentFromAccessor<BkpChokeV2>());
        session.Scope.Dispose();
        AssertMapCoversChanges("clean reopen + clean close", session, before, ReadFile(path));
    }

    /// <summary>A page changed behind the engine's back must be reported: the witness has to be able to fail.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-01")]
    public void Mutant_ARawWrite_IsMissingFromTheMap()
    {
        var session = OpenSession(null, register: dbe => dbe.RegisterComponentFromAccessor<BkpChokeV1>());
        var path = session.Engine.MMF.DataFilePath;
        Spawn(session.Engine, 0, 20);
        session.Scope.Dispose();

        var before = ReadFile(path);
        session = OpenSession(path, register: dbe => dbe.RegisterComponentFromAccessor<BkpChokeV1>());
        session.Scope.Dispose();

        // The bypass: one page written straight to the file, as an offline tool or a stray RandomAccess.Write would.
        var after = ReadFile(path);
        var rogue = (after.Length / PagedMMF.PageSize) - 1;
        while (session.Map.IsSet(rogue))
        {
            rogue--;
        }

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Position = rogue * (long)PagedMMF.PageSize;
            fs.Write(FilledPage(0x5A));
        }

        RuleMutants.AssertDetects("BKP-01", MissedMarker,
            () => AssertMapCoversChanges("raw write", session, before, ReadFile(path)));
    }

    private sealed record Session(IServiceScope Scope, DatabaseEngine Engine, PageChangeMap Map, List<(int First, int Count)> Probed);

    private Session OpenSession(string path, Action<DatabaseEngine> register)
    {
        var scope = ServiceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        var probed = new List<(int, int)>();
        dbe.MMF.DataFileWriteProbe = (first, count) =>
        {
            lock (probed)
            {
                probed.Add((first, count));
            }
        };
        register(dbe);
        dbe.InitializeArchetypes();
        return new Session(scope, dbe, dbe.MMF.ChangeMap, probed);
    }

    private static void Spawn(DatabaseEngine dbe, int from, int count)
    {
        using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = from; i < from + count; i++)
        {
            t.Spawn<BkpChokeV1Arch>(BkpChokeV1Arch.Comp.Set(new BkpChokeV1 { A = i % 7, B = i }));
        }

        t.Commit();
    }

    private static void AssertMapCoversChanges(string label, Session session, byte[] before, byte[] after)
    {
        var pages = after.Length / PagedMMF.PageSize;
        var changed = 0;
        var missed = new List<int>();
        for (var p = 0; p < pages; p++)
        {
            var now = after.AsSpan(p * PagedMMF.PageSize, PagedMMF.PageSize);
            var was = (p + 1) * PagedMMF.PageSize <= before.Length ? before.AsSpan(p * PagedMMF.PageSize, PagedMMF.PageSize) : default;
            var differs = was.IsEmpty ? now.IndexOfAnyExcept((byte)0) >= 0 : !now.SequenceEqual(was);
            if (!differs)
            {
                continue;
            }

            changed++;
            if (!session.Map.IsSet(p))
            {
                missed.Add(p);
            }
        }

        var unmapped = new List<int>();
        lock (session.Probed)
        {
            foreach (var (first, count) in session.Probed)
            {
                for (var p = first; p < first + count; p++)
                {
                    if (!session.Map.IsSet(p))
                    {
                        unmapped.Add(p);
                    }
                }
            }
        }

        Assert.That(changed, Is.GreaterThan(0), $"{label}: premise: the session changed the data file");
        Assert.That(missed, Is.Empty, $"{MissedMarker} {missed.Count} of {changed} changed pages in '{label}': {string.Join(", ", missed)}");
        Assert.That(unmapped, Is.Empty, $"{MissedMarker} pages the chokepoint probe saw written in '{label}': {string.Join(", ", unmapped)}");
    }

    private static byte[] ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] FilledPage(byte value)
    {
        var page = new byte[PagedMMF.PageSize];
        page.AsSpan().Fill(value);
        return page;
    }
}
