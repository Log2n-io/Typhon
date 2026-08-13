using Typhon.Engine;

namespace SimpleSpaceBattle;

/// <summary>
/// Partitions the archetype's active clusters across the chunks of a <c>ChunkedParallel</c> system.
///
/// <para><b>Why this exists instead of <c>QuerySystem</c>.</b> A <c>QuerySystem</c> needs an <c>EcsView</c> input,
/// and the runtime refreshes every pull-mode input view at tick start
/// (<c>TyphonRuntime.RefreshSystemInputViewsAtTickStart</c>, #718). That refresh was measured at <b>8.3 µs per
/// entity — 413 ms per tick at 50 000 ships, single-threaded</b> (issue #797; reproduce with
/// <c>SSB_VIEWBENCH=1</c>). It accounted for the entire per-tick residual and made the tick 83 % serial.</para>
///
/// <para>These systems iterate <i>clusters</i>, never <c>ctx.Entities</c>, so the entity view was pure cost. A
/// <c>ChunkedParallel</c> <c>CallbackSystem</c> takes no input view at all and therefore skips the refresh
/// entirely; the cluster range each chunk owns is computed here instead of by the runtime.</para>
///
/// <para><b>The transaction is load-bearing and not incidental.</b> <c>ClusterSpatialQuery</c> requires an ambient
/// <c>EpochGuard</c> scope, and <c>EpochGuard</c> is <c>internal</c> — game code cannot open one directly. A
/// <c>Transaction</c> constructs one under the hood, which is what makes the spatial query legal on a worker
/// thread. It doubles as the <c>EntityAccessor</c> the cluster enumerator hangs off, so it costs one object rather
/// than two. It is never committed: every write in these systems goes straight to cluster storage via
/// <c>GetSpan</c>.</para>
/// </summary>
internal readonly ref struct ClusterWork
{
    private ClusterWork(Transaction transaction, int startCluster, int endCluster)
    {
        Transaction = transaction;
        StartCluster = startCluster;
        EndCluster = endCluster;
    }

    public Transaction Transaction { get; }

    public int StartCluster { get; }

    public int EndCluster { get; }

    public bool IsEmpty => EndCluster <= StartCluster;

    /// <summary>
    /// Open this chunk's slice of the active cluster list. Ranges are computed with 64-bit intermediates so a large
    /// cluster count cannot overflow, and they tile the range exactly — chunk <c>i</c>'s end is chunk
    /// <c>i+1</c>'s start, so no cluster is processed twice or skipped.
    /// </summary>
    public static ClusterWork Open(BattleWorld world, in TickContext ctx)
    {
        Range(world, in ctx, out int start, out int end);
        return new ClusterWork(world.TransactionForWorker(ctx.WorkerId, ctx.TickNumber), start, end);
    }

    public ClusterEnumerator<Ship> Clusters() => Transaction.GetClusterEnumerator<Ship>(StartCluster, EndCluster);

    /// <summary>
    /// Deliberately does NOT dispose the transaction — it is owned by <see cref="BattleWorld"/> and shared by every
    /// chunk this worker runs during the tick. Disposing here would put us straight back to one transaction per
    /// chunk, which dotTrace measured at 42 % of ResolutionSystem in lock contention.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// The chunk's cluster range, without opening a transaction. For systems that issue no spatial query and
    /// therefore need no epoch scope — <see cref="MovementSystem"/> is the only one.
    /// <para>
    /// Worth the separate path: a transaction per chunk measured at <b>8.9 ms</b> of wall-clock for Movement, a
    /// system whose actual work is ~50 000 multiply-adds. Thirty workers allocating a TSN simultaneously contend on
    /// the transaction chain, so the cost is not the object but the serialisation.
    /// </para>
    /// </summary>
    public static void Range(BattleWorld world, in TickContext ctx, out int startCluster, out int endCluster)
    {
        int total = world.ActiveClusterCount;
        int chunkCount = Math.Max(1, ctx.ChunkCount);
        int chunkIndex = ctx.ChunkIndex;

        startCluster = (int)((long)chunkIndex * total / chunkCount);
        endCluster = (int)((long)(chunkIndex + 1) * total / chunkCount);
    }
}
