using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// A <c>SavePages</c> batch and the write gate: the batch's shared hold survives no throw between its enter and its continuation (a leaked hold makes
/// every later freeze time out), and the batch's runs share one copy-before-write budget (BKP-12).
/// </summary>
class SavePagesGateTests : BackupTestBase<SavePagesGateTests>
{
    [Test]
    [CancelAfter(10_000)]
    public void ABatchThatThrowsBeforeIssuingItsWrites_ReleasesTheGate()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var mmf = dbe.MMF;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var mem = SegmentPages(mmf, guard.Epoch, 4).Skip(1).ToArray();   // data pages only: the root is a directory page, written alone and in sync

        mmf.DataFileWriteProbe = (_, _) => throw new InvalidOperationException("probe");
        try
        {
            Assert.That(() => mmf.SavePages(mem).Wait(), Throws.InstanceOf<Exception>(), "premise: the batch failed before issuing its writes");
        }
        finally
        {
            mmf.DataFileWriteProbe = null;
        }

        Assert.That(mmf.WriteGate.SharedHolders(), Is.Zero, "the failed batch leaked its shared hold on the write gate");
        Assert.That(mmf.TryEnterWriteGateExclusive(TimeSpan.FromMilliseconds(200)), Is.True, "a freeze must still get the gate");
        mmf.ExitWriteGateExclusive();
    }

    [Test]
    [CancelAfter(10_000)]
    public void EveryRunOfABatch_SharesOneCopyBeforeWriteBudget()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var mmf = dbe.MMF;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var mem = SegmentPages(mmf, guard.Epoch, 8);
        var everyOther = mem.Skip(1).Where((_, i) => i % 2 == 0).ToArray();   // apart on file: one run each (the root, a directory page, writes alone)

        var hook = new RecordingHook();
        Assert.That(mmf.TryEnterWriteGateExclusive(TimeSpan.FromSeconds(1)), Is.True);
        mmf.InstallWriteHook(hook);
        mmf.ExitWriteGateExclusive();
        try
        {
            mmf.SavePages(everyOther).Wait();
        }
        finally
        {
            Assert.That(mmf.TryEnterWriteGateExclusive(TimeSpan.FromSeconds(1)), Is.True);
            mmf.InstallWriteHook(null);
            mmf.ExitWriteGateExclusive();
        }

        Assert.That(hook.Runs, Is.GreaterThan(1), "premise: the batch has several runs");
        Assert.That(hook.Deadlines.Distinct().Count(), Is.EqualTo(1), "BKP-12: each run of one batch started its own budget");
    }

    private static int[] SegmentPages(ManagedPagedMMF mmf, long epoch, int count)
    {
        var cs = mmf.CreateChangeSet();
        var segment = mmf.AllocateSegment(PageBlockType.None, count, cs);
        cs.SaveChanges();
        return segment.Pages.ToArray().Select(p =>
        {
            mmf.RequestPageEpoch(p, epoch, out var memPage);
            return memPage;
        }).ToArray();
    }

    private sealed class RecordingHook : IDataFileWriteHook
    {
        private long _next;

        public List<long> Deadlines { get; } = [];

        public int Runs => Deadlines.Count;

        public void BeforeWrite(int firstPage, int count, ref long deadline)
        {
            if (deadline == 0)
            {
                deadline = ++_next;
            }

            Deadlines.Add(deadline);
        }
    }
}
