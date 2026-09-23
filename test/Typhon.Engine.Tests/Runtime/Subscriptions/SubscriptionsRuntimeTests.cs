using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-03 — what <c>Start</c> builds and what <c>Dispose</c> takes down: the compiled plan, the catalog, the session table and one replication state per
/// replicated archetype, attached to that archetype's cluster state.
/// </summary>
/// <remarks>
/// <para>
/// <b>The attachment is the debt this closes.</b> <see cref="ArchetypeReplicationState"/>, <see cref="ReplicationBlockPool"/> and
/// <see cref="ReplicationDirectory"/> were complete primitives that nothing in <c>src/</c> ever constructed, so
/// <c>ArchetypeClusterState.ReplicationState</c> was null in every production path and the ECS drain hook could not run. Asserting that a started runtime
/// has attached one per archetype is what makes that reachable rather than merely buildable.
/// </para>
/// <para>
/// <b>And the teardown is the other half of it.</b> The directory holds raw pointers into the pool's native slabs and the ECS holds a reference to the state
/// itself; a runtime that disposed in the wrong order, or not at all, would leave a drain pointing at freed memory. That is asserted on the pool's own
/// counters, not inferred from the absence of a crash.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class SubscriptionsRuntimeTests : TestBase<SubscriptionsRuntimeTests>
{
    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static RuntimeOptions Options() => new()
    {
        WorkerCount = 1, BaseTickRate = 1000, Subscriptions = new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
    };

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe) => TyphonRuntime.Create(dbe, schedule =>
    {
        schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
    }, Options());

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclarePlayer(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile("god-world", p => p.World().Of<ProjCreature>().Of<ProjPlayer>().Of<ProjRock>());
    }

    private static SubscriptionsRuntime SubscriptionsOf(TyphonRuntime runtime) => runtime.SubscriptionsContextForTest.Subscriptions;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe, CompiledProjectionPlan plan) =>
        dbe._archetypeStates[plan.ArchetypeCatalogId].ClusterState;

    /// <summary>
    /// A runtime whose application declared nothing builds nothing: no plan, no catalog, and above all no session table, which is half a megabyte of native
    /// memory at the default <see cref="SubscriptionsOptions.MaxSessions"/>.
    /// </summary>
    [Test]
    public void ARuntimeWithNoDeclarationsBuildsNothing()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);

        runtime.Start();
        var subscriptions = SubscriptionsOf(runtime);
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(subscriptions, Is.Not.Null, "the owner exists even when it owns nothing, so a stage never has to null-check the field");
            Assert.That(subscriptions.IsActive, Is.False);
            Assert.That(subscriptions.Plans, Is.Empty);
            Assert.That(subscriptions.Catalog, Is.Null);
            Assert.That(subscriptions.Sessions, Is.Null, "an unused subsystem commits nothing");
            Assert.That(subscriptions.ReplicationStates, Is.Empty);
        });
    }

    /// <summary>
    /// A declared runtime compiles its plan, builds its catalog and commits its session table — once, at <c>Start</c>.
    /// </summary>
    [Test]
    public void StartCompilesThePlanAndBuildsTheCatalog()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);

        runtime.Start();
        var subscriptions = SubscriptionsOf(runtime);
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(subscriptions.IsActive, Is.True);
            Assert.That(subscriptions.Plans, Has.Length.EqualTo(3));
            Assert.That(subscriptions.PlanNamed(nameof(ProjCreature)), Is.Not.Null);
            Assert.That(subscriptions.Catalog, Is.Not.Null);
            Assert.That(subscriptions.Catalog.Utf8, Is.Not.Empty);
            Assert.That(subscriptions.Catalog.Hash, Is.EqualTo(CatalogSerializer.HashBytes(subscriptions.Catalog.Utf8)));
            Assert.DoesNotThrow(() => CatalogSerializer.FromUtf8(subscriptions.Catalog.Utf8), "a client would refuse the catalog this runtime serves");
            Assert.That(subscriptions.Sessions, Is.Not.Null);
            Assert.That(subscriptions.Sessions.Capacity, Is.EqualTo(runtime.Subscriptions.Options.MaxSessions));

            // The ladder's ceiling, not BaseTickRate / MinTickRateHz: at 1 000 Hz base the ratio is 100 and the ladder still stops at 6 (finding F1).
            Assert.That(subscriptions.LargestTickMultiplier, Is.EqualTo(6));
            Assert.That(subscriptions.NominalTickPeriodSeconds, Is.EqualTo(1.0 / 1000).Within(1e-12));
        });
    }

    /// <summary>
    /// The catalog is built once and never again: ticks pass, and the bytes a client would be handed are the same object.
    /// </summary>
    /// <remarks>
    /// Asserted on identity rather than on an allocation delta, and that is the stronger statement: a byte comparison would pass on a catalog rebuilt every
    /// tick, and an allocation counter over a running runtime measures everything else the tick does as well.
    /// </remarks>
    [Test]
    public void TheCatalogIsBuiltOnceAndCostsNothingPerTick()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);

        runtime.Start();
        var subscriptions = SubscriptionsOf(runtime);
        var export = subscriptions.Catalog;
        var bytes = export.Utf8;

        var startTick = runtime.CurrentTickNumber;
        Assert.That(SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= startTick + 20, TimeSpan.FromSeconds(5)), Is.True, "the runtime did not tick");
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(SubscriptionsOf(runtime), Is.SameAs(subscriptions));
            Assert.That(subscriptions.Catalog, Is.SameAs(export), "the export was rebuilt");
            Assert.That(subscriptions.Catalog.Utf8, Is.SameAs(bytes), "the bytes were re-serialized");
        });
    }

    /// <summary>
    /// Every replicated archetype gets a replication state, carved to the layout its plan computed, and that state is published to the archetype's cluster
    /// state — which is what makes the ECS drain hook reachable from production for the first time.
    /// </summary>
    [Test]
    public void StartAttachesAReplicationStatePerReplicatedArchetype()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);

        runtime.Start();
        var subscriptions = SubscriptionsOf(runtime);
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(subscriptions.ReplicationStates, Has.Length.EqualTo(subscriptions.Plans.Length));
            for (var i = 0; i < subscriptions.Plans.Length; i++)
            {
                var plan = subscriptions.Plans[i];
                var state = subscriptions.ReplicationStates[i];

                Assert.That(state, Is.Not.Null, $"'{plan.Name}' has no replication state");
                Assert.That(state.Layout.SlotCount, Is.EqualTo(plan.BlockLayout.SlotCount), $"'{plan.Name}' blocks hold a cluster's worth of entries");
                Assert.That(state.Layout.BlockSize, Is.EqualTo(plan.BlockLayout.BlockSize), $"'{plan.Name}' blocks are carved to its plan's layout");
                Assert.That(state.Layout.OwnerEntrySize, Is.EqualTo(plan.BlockLayout.OwnerEntrySize), $"'{plan.Name}' owner entries are its plan's size");
                Assert.That(state.NetIds, Is.SameAs(subscriptions.NetIds), "netIds are global: one allocator for every replicated archetype");
                Assert.That(ClusterStateOf(dbe, plan).ReplicationState, Is.SameAs(state), $"'{plan.Name}' never reached its cluster state");
                Assert.That(subscriptions.StateOf(plan.ArchetypeCatalogId), Is.SameAs(state));

                // Nothing is committed until a cluster is watched: an unused subsystem allocates nothing.
                Assert.That(state.Pool.CommittedBytes, Is.Zero);
                Assert.That(state.WatchedClusterCount, Is.Zero);
            }
        });
    }

    /// <summary>
    /// Disposing the runtime returns every rented block, detaches every state from its cluster state and gives the pools' native slabs back.
    /// </summary>
    /// <remarks>
    /// The rented blocks are attached by hand rather than by the projection pass, which does not exist yet: what is under test is the teardown, and a block
    /// rented through <see cref="ArchetypeReplicationState.TryAttachBlock"/> is the same block the pass would hold.
    /// </remarks>
    [Test]
    public void DisposeReleasesEveryPooledBlockAndDetachesFromTheEcs()
    {
        using var dbe = SetupEngine();
        var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var subscriptions = SubscriptionsOf(runtime);
        runtime.Shutdown();

        var states = subscriptions.ReplicationStates;
        var plans = subscriptions.Plans;
        for (var i = 0; i < states.Length; i++)
        {
            for (var chunkId = 0; chunkId < 4; chunkId++)
            {
                Assert.That(states[i].TryAttachBlock(chunkId, out _), Is.True, $"'{plans[i].Name}' could not rent a block");
            }

            Assert.That(states[i].Pool.BlockCount - states[i].Pool.FreeBlockCount, Is.EqualTo(4), "the fixture must hold rented blocks, or it asserts nothing");
            Assert.That(states[i].Pool.CommittedBytes, Is.GreaterThan(0));
        }

        runtime.Dispose();

        Assert.Multiple(() =>
        {
            for (var i = 0; i < states.Length; i++)
            {
                Assert.That(states[i].Pool.BlockCount - states[i].Pool.FreeBlockCount, Is.Zero, $"'{plans[i].Name}' leaked a rented block");
                Assert.That(states[i].Pool.CommittedBytes, Is.Zero, $"'{plans[i].Name}' kept its native slabs");
                Assert.That(states[i].DrainFaults, Is.Zero);

                // Left set, the ECS would call into a disposed state on the next cluster drain — from a path that must never throw.
                Assert.That(ClusterStateOf(dbe, plans[i]).ReplicationState, Is.Null, $"'{plans[i].Name}' stayed attached to its cluster state");
            }
        });
    }

    /// <summary>Disposing twice is a no-op, which is what makes a failed <c>Start</c> safe to unwind through the same path.</summary>
    [Test]
    public void DisposingTwiceIsSafe()
    {
        using var dbe = SetupEngine();
        var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();
        runtime.Shutdown();

        runtime.Dispose();
        Assert.DoesNotThrow(runtime.Dispose);
    }

    /// <summary>
    /// A declaration the engine cannot resolve fails <c>Start</c> by name, before the scheduler runs — a runtime with no workers has nothing to unwind.
    /// </summary>
    [Test]
    public void AnUnresolvableDeclarationFailsStartByName()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);

        // ProjRock has no Vitals component, so the field cannot resolve to a column in its cluster layout.
        runtime.Subscriptions.Archetype<ProjRock>(a => a
            .Position(ProjRock.Bounds)
            .Field(ProjCreature.Vitals, v => v.Health, Codec.U16, name: "hp"));

        var ex = Assert.Throws<InvalidOperationException>(runtime.Start);

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain(nameof(ProjRock)));
            Assert.That(ex.Message, Does.Contain(nameof(ProjVitals)));
            Assert.That(runtime.CurrentTickNumber, Is.Zero, "the scheduler started anyway");
        });
    }
}
