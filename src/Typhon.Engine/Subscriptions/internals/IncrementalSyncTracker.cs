using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Manages incremental sync for new subscriptions to large Views. Batches the initial entity snapshot across multiple ticks, tracking per-client per-View
/// cursor state.
/// </summary>
internal static class IncrementalSyncTracker
{
    /// <summary>
    /// Begin incremental sync for a client subscribing to a shared View.
    /// Captures the current entity set as a snapshot for batched delivery.
    /// </summary>
    internal static void BeginSync(ViewSubscriptionState state, ViewBase view)
    {
        state.Phase = SubscriptionPhase.Syncing;
        state.SyncCursor = 0;

        // Snapshot the current entity set for incremental delivery
        var count = view.Count;
        if (count == 0)
        {
            // Empty View — sync completes immediately
            state.SyncSnapshot = null;
            return;
        }

        var snapshot = new long[count];
        var idx = 0;
        foreach (var pk in view)
        {
            snapshot[idx++] = pk;
        }

        state.SyncSnapshot = snapshot;
    }

    /// <summary>
    /// Build the next batch of Added entities for an in-progress sync.
    /// Returns the batch as EntityDelta[], and a bool indicating if sync is complete.
    /// </summary>
    /// <param name="state">Per-client per-View sync state.</param>
    /// <param name="tx">Read-only Transaction.</param>
    /// <param name="batchSize">Max entities per batch.</param>
    /// <param name="snapshotBuffer">Reusable buffer for component snapshots (avoids per-call allocation).</param>
    internal static (EntityDelta[] batch, bool isComplete) BuildSyncBatch(ViewSubscriptionState state, Transaction tx, int batchSize, 
        List<ComponentSnapshot> snapshotBuffer)
    {
        var snapshot = state.SyncSnapshot;
        if (snapshot == null || state.SyncCursor >= snapshot.Length)
        {
            return (null, true);
        }

        var remaining = snapshot.Length - state.SyncCursor;
        var count = Math.Min(remaining, batchSize);
        var batch = new EntityDelta[count];

        for (var i = 0; i < count; i++)
        {
            var pk = snapshot[state.SyncCursor + i];
            snapshotBuffer.Clear();
            EntitySnapshotReader.ReadAllComponents(tx, EntityId.FromRaw(pk), snapshotBuffer);
            batch[i] = new EntityDelta
            {
                Id = pk,
                Components = snapshotBuffer.ToArray()
            };
        }

        state.SyncCursor += count;
        var isComplete = state.SyncCursor >= snapshot.Length;

        if (isComplete)
        {
            state.SyncSnapshot = null; // Free snapshot memory
        }

        return (batch, isComplete);
    }
}
