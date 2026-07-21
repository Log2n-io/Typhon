using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind tests for the generated <c>BeginX</c> factories. The golden-bytes equivalence tests
/// (<see cref="TraceEventEncodeEquivalenceTests"/>) construct events via object initializer; they don't exercise the factory parameter
/// plumbing. This fixture covers the gap by calling each <c>BeginX(args)</c> and asserting the resulting struct's payload fields match the
/// passed arguments — catches generator bugs in factory parameter ordering, casting (enum-typed factory params), and field assignment.
/// </summary>
/// <remarks>
/// Factories return <c>default</c> when the prologue gate trips; we unsuppress the affected kinds so the on-path runs. Each test is
/// self-contained and re-suppresses on teardown.
/// </remarks>
[TestFixture]
public sealed class BeginFactoryParameterTests
{
    // ── Span events with simple value-typed factory params ──

    // NOTE: EcsSpawn / EcsDestroy / BTreeInsert / TransactionCommit / etc. are NOT in the default deny-list, so
    // we run them directly. Calling SuppressKind in a finally block on an originally-unsuppressed kind would leave
    // the global state polluted for downstream tests (verified by DefaultState_OnlyExtremeKindsSuppressed_OthersOpen
    // failing when this fixture preceded it).

    [Test]
    public void BeginEcsSpawn_AssignsArchetypeId()
    {
        using var ev = TyphonEvent.BeginEcsSpawn(archetypeId: 0xCAFE);
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL), "On-path factory must produce a real span");
        Assert.That(ev.ArchetypeId, Is.EqualTo((ushort)0xCAFE));
    }

    [Test]
    public void BeginEcsDestroy_AssignsEntityId()
    {
        using var ev = TyphonEvent.BeginEcsDestroy(entityId: 0xDEAD_BEEF_AAAA_BBBBUL);
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL));
        Assert.That(ev.EntityId, Is.EqualTo(0xDEAD_BEEF_AAAA_BBBBUL));
    }

    [Test]
    public void BeginTransactionCommit_AssignsTsn()
    {
        using var ev = TyphonEvent.BeginTransactionCommit(tsn: 0x1234_5678L);
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL));
        Assert.That(ev.Tsn, Is.EqualTo(0x1234_5678L));
    }

    [Test]
    public void BeginTransactionCommitComponent_AssignsBothRequiredFields()
    {
        using var ev = TyphonEvent.BeginTransactionCommitComponent(tsn: 999L, componentTypeId: 42);
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL));
        Assert.That(ev.Tsn, Is.EqualTo(999L));
        Assert.That(ev.ComponentTypeId, Is.EqualTo(42));
    }

    [Test]
    public void BeginClusterMigration_AssignsThreeFieldsInOrder()
    {
        using var ev = TyphonEvent.BeginClusterMigration(archetypeId: 7, migrationCount: 13, componentCount: 99);
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL));
        Assert.That(ev.ArchetypeId, Is.EqualTo((ushort)7));
        Assert.That(ev.MigrationCount, Is.EqualTo(13));
        Assert.That(ev.ComponentCount, Is.EqualTo(99));
    }

    [Test]
    public void BeginBTreeInsert_NoPayload_ReturnsRealSpan()
    {
        using var ev = TyphonEvent.BeginBTreeInsert();
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL));
        Assert.That(ev.Header.StartTimestamp, Is.Not.EqualTo(0L));
    }

    // ── Enum-cast factory params ([BeginParam(ParamType = "...")]) ──

    [Test]
    public void BeginCheckpointCycle_CastsCheckpointReasonEnum()
    {
        // CheckpointCycleEvent.Reason is `byte` but the factory takes CheckpointReason via [BeginParam(ParamType = "CheckpointReason")].
        // Verifies the generator's explicit-cast path: Field = (byte)reason.
        using var ev = TyphonEvent.BeginCheckpointCycle(targetLsn: 0x1111L, reason: CheckpointReason.Forced);
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL));
        Assert.That(ev.TargetLsn, Is.EqualTo(0x1111L));
        Assert.That(ev.Reason, Is.EqualTo((byte)CheckpointReason.Forced));
    }

    [Test]
    public void BeginRuntimePhase_CastsTickPhaseEnum_AndUsesCustomFactoryName()
    {
        // RuntimePhaseSpanEvent uses both FactoryName = "BeginRuntimePhase" (override of Begin<KindName>) AND
        // [BeginParam(ParamType = "TickPhase")]. Doubles as a test for the FactoryName attribute property.
        using var ev = TyphonEvent.BeginRuntimePhase(phase: TickPhase.UowFlush);
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL));
        Assert.That(ev.Phase, Is.EqualTo((byte)TickPhase.UowFlush));
    }

    // ── TransactionPersist uses FactoryName = "BeginTransactionPersist" because Kind is TransactionRollback ──

    [Test]
    public void BeginTransactionPersist_AssignsTsn()
    {
        using var ev = TyphonEvent.BeginTransactionPersist(tsn: 0xABCDEF12L);
        Assert.That(ev.Header.SpanId, Is.Not.EqualTo(0UL));
        Assert.That(ev.Tsn, Is.EqualTo(0xABCDEF12L));
    }

    // ── Factories that return default when ProfilerActive is on but the kind is suppressed ──

    [Test]
    public void BeginPageCacheFetch_WhenSuppressed_ReturnsDefault()
    {
        // PageCacheFetch is suppressed by default in the static deny-list.
        using var ev = TyphonEvent.BeginPageCacheFetch(filePageIndex: 42);
        Assert.That(ev.Header.SpanId, Is.EqualTo(0UL), "Suppressed kind must return default with SpanId=0");
        Assert.That(ev.FilePageIndex, Is.EqualTo(0), "Suppressed factory must NOT assign payload fields");
    }
}
