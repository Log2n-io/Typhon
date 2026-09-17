using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The engine, the declarations and a built <see cref="SubscriptionsRuntime"/>, driven by hand rather than by a tick loop — what the P1-12 fixtures resolve
/// interest against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a live <see cref="TyphonRuntime"/>.</b> S2a's inputs are a populated archetype, an open session and a bound profile, and its outputs
/// are counts — probes, runs, hits, watched bits. Every one of those is exact, and driving the pass directly is what lets a test assert the exact number
/// instead of a band around whatever a tick loop happened to do. <see cref="InterestEpochScopeTests"/> is the one fixture that needs a real runtime, because
/// the thing it asks about is a property of the threads the stage runs on.
/// </para>
/// <para>
/// <b>It stands in for the blocks step</b> (<see cref="CreateRequestedBlocks"/>). P1-12 produces the per-worker new-block lists; P1-09's <c>Project</c> stage
/// is what turns them into blocks, and it does not exist yet. Doing it here, single-threaded between passes, is the same act at the same point in the order.
/// </para>
/// </remarks>
sealed unsafe class InterestHarness : IDisposable
{
    /// <summary>
    /// Ample: these fixtures watch a few hundred entities, and a pool that refused a block would be measuring the budget instead of the pass.
    /// </summary>
    private const long PoolBudgetBytes = 16L * 1024 * 1024;

    private readonly ResourceRegistry _resources;

    private InterestHarness(DatabaseEngine engine, ResourceRegistry resources, SubscriptionsRegistry declarations, SubscriptionsRuntime subscriptions)
    {
        Engine = engine;
        _resources = resources;
        Declarations = declarations;
        Subscriptions = subscriptions;
    }

    /// <summary>The engine whose clusters the observers walk. Owned by the caller.</summary>
    public DatabaseEngine Engine { get; }

    /// <summary>The declarations the runtime was built from.</summary>
    public SubscriptionsRegistry Declarations { get; }

    /// <summary>Everything replication owns, including the pass under test.</summary>
    public SubscriptionsRuntime Subscriptions { get; }

    /// <summary>The pass under test.</summary>
    public InterestPass Interest => Subscriptions.Interest;

    /// <summary>The session table, which is the pass's per-tick input.</summary>
    public SessionTable Sessions => Subscriptions.Sessions;

    /// <summary>
    /// Builds the replication runtime over <paramref name="engine"/> from declarations the caller writes.
    /// </summary>
    /// <param name="engine">An engine whose archetypes are initialized — <c>ProjectionTestSchema.SetupEngine</c>.</param>
    /// <param name="declare">Writes the projections and profiles.</param>
    /// <param name="name">A name for the resource registry, so two fixtures do not share one.</param>
    /// <param name="options">Replication options, or <see langword="null"/> for the fixtures' ample defaults.</param>
    /// <returns>The harness.</returns>
    public static InterestHarness Create(DatabaseEngine engine, Action<SubscriptionsRegistry> declare, string name, SubscriptionsOptions options = null)
    {
        var resources = new ResourceRegistry(new ResourceRegistryOptions { Name = name });
        try
        {
            var netIds = new NetIdAllocator("NetIds", resources.Runtime);
            var declarations = new SubscriptionsRegistry(options ?? new SubscriptionsOptions { MaxSessions = 256, StatePoolBudgetBytes = PoolBudgetBytes });
            declarations.Sessions.Kinds("god");
            declare(declarations);

            var subscriptions = new SubscriptionsRuntime(engine, declarations, new RuntimeOptions { BaseTickRate = 10 }, resources.Runtime, netIds,
                ["Test"]);

            return new InterestHarness(engine, resources, declarations, subscriptions);
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

        // Admitted becomes Open only when the tick delivers the Opened event, which is the table's own recycling invariant. The pass reads open sessions, so
        // it would otherwise see none.
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

    /// <summary>Runs one whole pass: the prologue, then every chunk, as the stage would.</summary>
    /// <param name="tick">The tick number.</param>
    /// <param name="workers">Worker-pool width.</param>
    /// <returns>Chunks the pass asked for.</returns>
    public int RunPass(long tick, int workers = 1)
    {
        var chunks = Interest.BeginTick(tick, workers);
        for (var c = 0; c < chunks; c++)
        {
            // The scope the stage's base class takes around every chunk: reading a cluster column is page access, and PS-02 requires every one to be inside an
            // EpochGuard. Driving the pass without it would exercise a code path the engine never runs.
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                Interest.ExecuteChunk(c, chunks);
            }
        }

        return chunks;
    }

    /// <summary>Creates the blocks the last pass asked for — the blocks step, which P1-09 owns and which does not exist yet.</summary>
    /// <returns>How many blocks were created.</returns>
    public int CreateRequestedBlocks()
    {
        var created = 0;
        for (var w = 0; w < Interest.ArenaCount; w++)
        {
            foreach (var packed in Interest.Arena(w).NewBlocks)
            {
                var state = Subscriptions.ReplicationStates[HitArena.NewBlockArchetype(packed)];
                if (state.TryAttachBlock(HitArena.NewBlockChunkId(packed), out _))
                {
                    created++;
                }
            }
        }

        return created;
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

    /// <summary>Slots marked watched in an archetype's blocks, right now.</summary>
    /// <param name="planIndex">The archetype's plan index.</param>
    /// <returns>The count.</returns>
    public int WatchedSlotCount(int planIndex)
    {
        var directory = Subscriptions.ReplicationStates[planIndex].Directory;
        var total = 0;
        foreach (var (chunkId, _) in LiveClusters(planIndex))
        {
            if (directory.TryGetBlock(chunkId, out var block))
            {
                total += System.Numerics.BitOperations.PopCount(block->WatchedMask);
            }
        }

        return total;
    }

    /// <summary>
    /// Asserts that every live cluster of an archetype carries a block whose watched mask is exactly its occupancy word.
    /// </summary>
    /// <param name="planIndex">The archetype's plan index.</param>
    /// <param name="because">What the caller is proving.</param>
    /// <remarks>
    /// Equality, not containment: "every live entity is watched" and "nothing dead is watched" are the same assertion from two sides, and only the second one
    /// catches a mask that was never cleared.
    /// </remarks>
    public void AssertWatchedMatchesOccupancy(int planIndex, string because)
    {
        var directory = Subscriptions.ReplicationStates[planIndex].Directory;
        foreach (var (chunkId, occupancy) in LiveClusters(planIndex))
        {
            Assert.That(directory.TryGetBlock(chunkId, out var block), Is.True, $"cluster {chunkId} carries no block — {because}");
            Assert.That(block->WatchedMask, Is.EqualTo(occupancy), $"cluster {chunkId}'s watched mask is not its occupancy — {because}");
        }
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
