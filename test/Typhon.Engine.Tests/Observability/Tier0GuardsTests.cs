using NUnit.Framework;
using System.Threading;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Tier-0 always-on guards (#422): corruption/lock-protocol violations that were silently swallowed in Release
/// (the <c>Debug.Fail</c>/<c>Debug.Assert(false)</c> reaction was compiled out) now fail-fast or record.
///
/// <para>
/// The <c>ManagedPagedMMF</c> duplicate-segment-root guard (3 sites) throws through the same
/// <c>ThrowHelper.ThrowCorruption</c> path exercised here by the AccessControl cases; it is an allocator-internal
/// corruption path that cannot be reached through the public API without fault injection, so it is proven by the
/// shared mechanism rather than a dedicated trigger.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class Tier0GuardsTests
{
    [Test]
    public void ExitExclusiveAccess_FromWrongThread_ThrowsCorruption()
    {
        var control = new AccessControl();
        Assert.That(control.EnterExclusiveAccess(ref WaitContext.Null), Is.True);

        CorruptionException caught = null;
        var worker = new Thread(() =>
        {
            try
            {
                control.ExitExclusiveAccess();
            }
            catch (CorruptionException ex)
            {
                caught = ex;
            }
        });
        worker.Start();
        worker.Join();

        Assert.That(caught, Is.Not.Null, "Releasing an exclusive lock from a non-owning thread must fail-fast with CorruptionException.");
    }

    [Test]
    public void DemoteFromExclusiveAccess_FromWrongThread_ThrowsCorruption()
    {
        var control = new AccessControl();
        Assert.That(control.EnterExclusiveAccess(ref WaitContext.Null), Is.True);

        CorruptionException caught = null;
        var worker = new Thread(() =>
        {
            try
            {
                control.DemoteFromExclusiveAccess();
            }
            catch (CorruptionException ex)
            {
                caught = ex;
            }
        });
        worker.Start();
        worker.Join();

        Assert.That(caught, Is.Not.Null, "Demoting an exclusive lock from a non-owning thread must fail-fast with CorruptionException.");
    }

    [Test]
    public void RecordDfsStackOverflow_IncrementsCounter_AndNeverThrows()
    {
        long before = Interlocked.Read(ref SpatialRTreeDiagnostics.DfsStackOverflowCount);

        // Latch-safe record path: must never throw (an exception under the OLC read latch would deadlock).
        Assert.DoesNotThrow(() => SpatialRTreeDiagnostics.RecordDfsStackOverflow("unit-test"));

        long after = Interlocked.Read(ref SpatialRTreeDiagnostics.DfsStackOverflowCount);
        Assert.That(after, Is.GreaterThan(before), "The DFS-overflow record must increment the always-on process counter.");
    }
}
