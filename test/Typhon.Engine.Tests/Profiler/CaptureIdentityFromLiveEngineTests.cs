using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Feature #614 (F1) AC5 — the capture identity, sourced from a <b>real engine and runtime</b> rather than hand-built metadata. The wire-format fixtures
/// prove the fields survive a round trip; this proves the right values are put into them in the first place.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>What this fixture can and cannot prove about the routing id.</b> In a freshly-created database, archetypes are registered and routed in the same
/// order, so catalog id and routing id come out equal. Any test written against a fixture therefore passes just as happily if the code echoes the catalog id
/// back — which is precisely §5.3's landmine, and precisely why it survives test suites and fails on real data.
/// </para>
/// <para>
/// So the assertion here is deliberately about <i>provenance</i>, not about the numbers differing: every recorded routing id must equal what the engine's own
/// routing table returns for that catalog id. That pins the value to <c>RoutingIdForCatalog</c> and would fail the moment someone "simplified" it to
/// <c>def.ArchetypeId</c>. Observing the two id spaces actually diverge needs a database built by one process and captured by another, where registration
/// order and persisted routing order genuinely differ — that belongs in end-to-end verification, not here, and the limitation is stated rather than papered
/// over with a green test.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CaptureIdentityFromLiveEngineTests : TestBase<CaptureIdentityFromLiveEngineTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static TyphonRuntime CreateIdleRuntime(DatabaseEngine dbe) =>
        TyphonRuntime.Create(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", static _ => { }),
            new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

    [Test]
    public void SessionMetadata_CarriesTheEnginesOwnIdentityAndTransactionWindow()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);

        var metadata = ProfilerSessionMetadataBuilder.Build(runtime, samplingSessionStartQpc: 0);

        Assert.Multiple(() =>
        {
            Assert.That(metadata.DatabaseId, Is.EqualTo(dbe.DatabaseId));
            Assert.That(metadata.DatabaseId, Is.Not.EqualTo(Guid.Empty), "a live engine always has a minted identity by this point");
            Assert.That(metadata.DatabaseName, Is.EqualTo(CurrentDatabaseName), "the bundle's own name, so a trace found outside its bundle still says where it came from");
            Assert.That(metadata.TsnAtStart, Is.EqualTo(dbe.TransactionChain.NextFreeId));
            Assert.That(metadata.SchemaFingerprint, Is.Not.Zero, "a registered schema must fingerprint to something — 0 is the 'no engine' value");
        });
    }

    [Test]
    public void ArchetypeTable_TakesItsRoutingIdsFromTheEnginesRoutingTable()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);

        var metadata = ProfilerSessionMetadataBuilder.Build(runtime, samplingSessionStartQpc: 0);

        Assert.That(metadata.Archetypes, Is.Not.Empty, "the fixture registers archetypes, so the table must not be empty");
        Assert.Multiple(() =>
        {
            foreach (var record in metadata.Archetypes)
            {
                var expected = dbe.RoutingIdForCatalog(record.ArchetypeId);
                Assert.That(record.RoutingId, Is.EqualTo(expected),
                    $"'{record.Name}' must carry the DATABASE's routing id for catalog {record.ArchetypeId}, not the catalog id itself");
                Assert.That(record.RoutingId, Is.Not.Zero, "routing id 0 is reserved for the null EntityId sentinel");
            }
        });
    }

    [Test]
    public void SchemaFingerprint_IsIdenticalAcrossTwoCapturesOfTheSameSchema()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateIdleRuntime(dbe);

        var first = ProfilerSessionMetadataBuilder.Build(runtime, 0).SchemaFingerprint;
        var second = ProfilerSessionMetadataBuilder.Build(runtime, 0).SchemaFingerprint;

        Assert.That(second, Is.EqualTo(first), "two captures of an unchanged schema must not read as drift");
    }

    [Test]
    public void CaptureWindow_ClosesOnTheEnginesFinalTsn()
    {
        Guid databaseId;
        long tsnAfterWrites;

        using (var dbe = SetupEngine())
        {
            using var runtime = CreateIdleRuntime(dbe);
            databaseId = dbe.DatabaseId;

            TyphonProfiler.ResetForTests();
            ProfilerCaptureCounters.BeginCapture(dbe.TransactionChain.NextFreeId, runtime.CurrentTickNumber);

            CreateNoiseCompA(dbe, count: 5);
            tsnAfterWrites = dbe.TransactionChain.NextFreeId;
            Assert.That(tsnAfterWrites, Is.GreaterThan(0), "the writes above must have advanced the TSN counter");
        }
        // Engine disposed — PersistEngineState has now published its final TSN, which is the whole point of the handoff:
        // the trace header is patched from the storage DisposingEvent, after this engine is gone.

        var (tsnMax, _) = ProfilerCaptureCounters.SnapshotAtClose();

        Assert.That(databaseId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(tsnMax, Is.GreaterThanOrEqualTo(tsnAfterWrites),
            "the engine must publish its final TSN on the way out, or every capture's transaction window would end where it began");
    }
}
