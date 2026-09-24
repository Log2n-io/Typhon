using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The engine, the declarations and a built <see cref="SubscriptionsRuntime"/>, driven by hand rather than by a tick loop.
/// </summary>
/// <remarks>
/// <b>Deliberately not a live <see cref="TyphonRuntime"/>.</b> Driving the stages directly is what lets a test assert an exact number instead of a band
/// around whatever a tick loop happened to do. <see cref="FrameHarness"/> runs the whole track on top of this.
/// </remarks>
sealed unsafe class ReplicationHarness : IDisposable
{
    /// <summary>
    /// Ample: these fixtures watch a few hundred entities, and a pool that refused a block would be measuring the budget instead of the pass.
    /// </summary>
    private const long PoolBudgetBytes = 16L * 1024 * 1024;

    private readonly ResourceRegistry _resources;

    private ReplicationHarness(DatabaseEngine engine, ResourceRegistry resources, SubscriptionsRegistry declarations, SubscriptionsRuntime subscriptions)
    {
        Engine = engine;
        _resources = resources;
        Declarations = declarations;
        Subscriptions = subscriptions;
    }

    /// <summary>The engine whose clusters are replicated. Owned by the caller.</summary>
    public DatabaseEngine Engine { get; }

    /// <summary>The declarations the runtime was built from.</summary>
    public SubscriptionsRegistry Declarations { get; }

    /// <summary>Everything replication owns.</summary>
    public SubscriptionsRuntime Subscriptions { get; }

    /// <summary>The session table.</summary>
    public SessionTable Sessions => Subscriptions.Sessions;

    /// <summary>
    /// Builds the replication runtime over <paramref name="engine"/> from declarations the caller writes.
    /// </summary>
    /// <param name="engine">An engine whose archetypes are initialized — <c>ProjectionTestSchema.SetupEngine</c>.</param>
    /// <param name="declare">Writes the projections and profiles.</param>
    /// <param name="name">A name for the resource registry, so two fixtures do not share one.</param>
    /// <param name="options">Replication options, or <see langword="null"/> for the fixtures' ample defaults.</param>
    /// <param name="replicationCellM">
    /// The cell side the defaults declare; zero for <c>ProjectionTestSchema.ReplicationCellFor(0)</c>, the World fixtures' grid. Ignored when
    /// <paramref name="options"/> is given, which declares its own.
    /// </param>
    /// <returns>The harness.</returns>
    public static ReplicationHarness Create(DatabaseEngine engine, Action<SubscriptionsRegistry> declare, string name, SubscriptionsOptions options = null,
        double replicationCellM = 0)
    {
        if (options != null && replicationCellM > 0)
        {
            throw new ArgumentException("options declares its own ReplicationCellM; passing replicationCellM too would be ignored", nameof(replicationCellM));
        }

        var resources = new ResourceRegistry(new ResourceRegistryOptions { Name = name });
        try
        {
            var netIds = new NetIdAllocator("NetIds", resources.Runtime);
            var declarations = new SubscriptionsRegistry(options ?? new SubscriptionsOptions
            {
                MaxSessions = 256,
                StatePoolBudgetBytes = PoolBudgetBytes,
                ReplicationCellM = replicationCellM > 0 ? replicationCellM : ProjectionTestSchema.ReplicationCellFor(0),
            });
            declarations.Sessions.Kinds("god");
            declare(declarations);

            var subscriptions = new SubscriptionsRuntime(engine, declarations, new RuntimeOptions { BaseTickRate = 10 }, resources.Runtime, netIds,
                ["Test"]);

            return new ReplicationHarness(engine, resources, declarations, subscriptions);
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }

    /// <summary>Admits <paramref name="count"/> sessions, promotes them, and binds each to <paramref name="profile"/>.</summary>
    /// <param name="count">How many.</param>
    /// <param name="profile">The declared profile's name, or <see langword="null"/> to leave them unbound.</param>
    /// <returns>The identities.</returns>
    public SessionId[] OpenSessions(int count, string profile)
    {
        var sessions = new SessionId[count];
        for (var i = 0; i < count; i++)
        {
            var request = new AdmissionRequest("god", "token", 0, ReadOnlySpan<byte>.Empty, null, null, null, "fake");
            Assert.That(Sessions.TryAdmit(Declarations.Sessions, request, out sessions[i], out _, out _), Is.True, "the table refused a session");
        }

        // Admitted becomes Open only when the tick delivers the Opened event, which is the table's own recycling invariant. The stages read open sessions,
        // so they would otherwise see none.
        Sessions.BeginTick();

        if (profile != null)
        {
            foreach (var session in sessions)
            {
                Assert.That(Sessions.SetProfile(session, profile), Is.True, "a just-opened session should take a profile");
            }
        }

        return sessions;
    }

    /// <summary>The plan index of a replicated archetype, by wire name.</summary>
    /// <param name="name">The archetype's wire name.</param>
    /// <returns>The index.</returns>
    public int PlanIndex(string name)
    {
        var plans = Subscriptions.Plans;
        for (var i = 0; i < plans.Length; i++)
        {
            if (plans[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The cluster state behind a plan index.</summary>
    /// <param name="planIndex">The index.</param>
    /// <returns>The cluster state.</returns>
    public ArchetypeClusterState ClusterStateAt(int planIndex) =>
        Engine._archetypeStates[Subscriptions.Plans[planIndex].ArchetypeCatalogId].ClusterState;

    /// <summary>Every non-empty cluster of an archetype, with its occupancy word.</summary>
    /// <param name="planIndex">The archetype's plan index.</param>
    /// <returns>Chunk id and occupancy, in active-list order.</returns>
    public List<(int ChunkId, ulong Occupancy)> LiveClusters(int planIndex)
    {
        var result = new List<(int, ulong)>();
        var clusterState = ClusterStateAt(planIndex);
        var ids = clusterState.ReadActiveClusterList(out var count);
        if (ids == null)
        {
            return result;
        }

        var fullMask = Subscriptions.Plans[planIndex].ClusterLayout.FullMask;
        using (EpochGuard.Enter(Engine.EpochManager))
        {
            using var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
            for (var i = 0; i < count; i++)
            {
                var occupancy = *(ulong*)accessor.GetChunkAddress(ids[i]) & fullMask;
                if (occupancy != 0)
                {
                    result.Add((ids[i], occupancy));
                }
            }
        }

        return result;
    }

    /// <summary>Live entities of an archetype, counted from its occupancy words.</summary>
    /// <param name="planIndex">The archetype's plan index.</param>
    /// <returns>The count.</returns>
    public int LiveEntityCount(int planIndex)
    {
        var total = 0;
        foreach (var (_, occupancy) in LiveClusters(planIndex))
        {
            total += System.Numerics.BitOperations.PopCount(occupancy);
        }

        return total;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The replication states detach themselves from the engine's cluster states as they go, so the ECS is never left holding a disposed one. The registry
        // takes the identity allocator down by cascade.
        Subscriptions?.Dispose();
        _resources?.Dispose();
    }
}
