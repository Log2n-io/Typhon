using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1495 through a restore: a restored database's world clock is the clock of the last write the restore brings back — not the source's clock at the time
/// of the restore, and not zero.
/// </summary>
[NonParallelizable]
class WorldTickRestoreTests : ArchiveTestBase<WorldTickRestoreTests>
{
    private static DatabaseEngine Register(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // Session ticks [from, from + count), each committing one durable spawn during the tick, then its fence.
    private static void TickWriting(DatabaseEngine dbe, int from, int count)
    {
        for (var t = from; t < from + count; t++)
        {
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(t)));
                Assert.That(tx.Commit(), Is.True);
            }

            dbe.WriteTickFence(t);
        }
    }

    /// <summary>
    /// Ten ticks writing, a full point, five more ticks writing: the restored database resumes at world tick 9 — the last write the point holds — though
    /// the source had reached 15.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("TICK-01")]
    public void ARestoredDatabaseResumesAtTheClockOfTheLastWriteItRestores()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope);
            TickWriting(dbe, 0, 10);
            CapturePoint(dbe, BackupKind.Full);
            TickWriting(dbe, 10, 5);
            Assert.That(dbe.NextWorldTick, Is.EqualTo(15L), "premise: the source ran past the point");
        }

        var dir = Path.Combine(TestStorage.Root, nameof(WorldTickRestoreTests), CurrentDatabaseName + ".restored");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        BackupOperations.Restore(StorePath, PointSelector.Latest, dir);
        SuppressArchiving = true;
        using var provider = ProviderFor(dir);
        using var restoredScope = provider.CreateScope();
        var restored = Register(restoredScope);
        Assert.That(restored.WorldTickBase, Is.EqualTo(9L), "the clock of the last write the point holds");
    }
}
