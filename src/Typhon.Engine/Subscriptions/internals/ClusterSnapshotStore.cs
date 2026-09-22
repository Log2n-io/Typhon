using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// One archetype's clusters as the interest stage read them THIS tick — occupancy and entity bounds — opened once and shared by every interest cell.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this removes is the redundant open, and it is most of the stage.</b> Every interest cell runs its own broad query, and adjacent cells' enlarged
/// discs overlap heavily: profiled at d06 with a thousand sessions, about 165 cells reached each cluster roughly fifteen times a tick, and opening the
/// cluster's page — a page-cache request per open — was about half of the interest stage's time. The per-entity kernel it fed was about six per cent.
/// This store opens each cluster at most once per tick and every other cell reads what that open produced.
/// </para>
/// <para>
/// <b>Filled by the tick's pre-fill wave, or lazily by whichever worker reaches the cluster first.</b> The tick stamp is claimed with a compare-exchange to
/// its negation, the winner reads the page and publishes the stamp with a release store. A worker that arrives meanwhile is told
/// <see cref="SnapshotClaim.Busy"/> and reads the page itself: it never waits. Waiting cost about 4 500 waits a tick, some 10 ms of CPU, at d06 with a
/// thousand sessions — the wait yields, and on a saturated pool a yield hands the core to a queued thread, so a reader lost its core behind a fill that
/// took nanoseconds (design 23 § 1). Reading privately costs one page open and writes nothing shared.
/// </para>
/// <para>
/// <b>Valid for one tick, and stamped rather than cleared.</b> A stamp from an earlier tick is simply not equal to the current one, so nothing is swept
/// between ticks. Interest runs after the fence, when no cluster is written, so a snapshot taken at any point during the stage is the state for the whole
/// of it.
/// </para>
/// <para>
/// <b>Dense by chunk id and managed</b>: indexed with refs and spans, never through a raw pointer, so nothing here addresses GC memory by address. Grown in
/// the stage's serial prologue and never shrunk, so a steady-state tick allocates nothing.
/// </para>
/// <para>
/// <b>A cluster's stamp, occupancy and block share one 32-byte entry</b>, so a read touches one cache line rather than three, and a claim invalidates the
/// line for one neighbouring cluster rather than seven.
/// </para>
/// </remarks>
internal sealed unsafe class ClusterSnapshotStore
{
    /// <summary>What <see cref="TryGet"/> handed back.</summary>
    internal enum SnapshotClaim
    {
        /// <summary>The cluster is in the store for this tick; the occupancy is returned and its boxes are readable.</summary>
        Ready,

        /// <summary>The caller claimed the fill and must call <see cref="Fill"/> — or, beyond the store, open the cluster privately.</summary>
        Fill,

        /// <summary>Another worker is filling it: the caller reads the cluster's page itself and writes nothing here.</summary>
        Busy,
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct Entry
    {
        public long Tick;
        public ulong Occupancy;

        // The cluster's replication block, resolved once by the filling worker — every session that reaches the cluster this tick needs it, and the
        // directory is a hash map. Zero when the cluster has no block yet. Stable for the tick: blocks are created in the blocks step, after this stage.
        public nint Block;

        // The last tick a cell's broad phase read this cluster, so only its first reader a tick lists it for the next tick's pre-fill wave. Read by MarkRead
        // alone. Plain accesses: racing readers store the same value, and two that both list the cluster cost the wave one lost claim.
        public long ReadTick;
    }

    private Entry[] _entries = [];

    /// <summary>
    /// The boxes as columns — per cluster, MinX[64], MinY[64], MaxX[64], MaxY[64] — which is what <see cref="InterestBandKernel"/> reads: a sixteen-entity
    /// block is four loads and no transpose (design 23, phase 2).
    /// </summary>
    private float[] _columns = [];

    /// <summary>Grows the store to cover every chunk id below <paramref name="chunkCapacity"/>. Serial: called from the stage's prologue.</summary>
    /// <param name="chunkCapacity">One past the highest chunk id the archetype can hold this tick.</param>
    public void EnsureCapacity(int chunkCapacity)
    {
        if (chunkCapacity <= _entries.Length)
        {
            return;
        }

        var grown = Math.Max(chunkCapacity, Math.Max(64, _entries.Length * 2));
        Array.Resize(ref _entries, grown);
        Array.Resize(ref _columns, grown * InterestBandKernel.ColumnFloats);
    }

    /// <summary>
    /// The cluster's occupancy for <paramref name="tick"/> if it is already in the store; otherwise claims the fill for the caller, or reports it busy when
    /// another worker holds the claim. It never waits: a busy caller reads the cluster's page itself.
    /// </summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="tick">The tick.</param>
    /// <param name="claim">What the caller must do next.</param>
    /// <returns>The occupancy when <paramref name="claim"/> is <see cref="SnapshotClaim.Ready"/>; zero otherwise.</returns>
    public ulong TryGet(int chunkId, long tick, out SnapshotClaim claim)
    {
        if (tick <= 0 || (uint)chunkId >= (uint)_entries.Length)
        {
            // Beyond what the prologue sized — a cluster created after it — or a tick the stamps cannot tell from "never filled" (they start at zero, and
            // -0 is 0). Opened privately by the caller, never shared.
            claim = SnapshotClaim.Fill;
            return 0UL;
        }

        ref var entry = ref _entries[chunkId];
        ref var stamp = ref entry.Tick;
        while (true)
        {
            var seen = Volatile.Read(ref stamp);
            if (seen == tick)
            {
                claim = SnapshotClaim.Ready;
                return entry.Occupancy;
            }

            if (seen == -tick)
            {
                claim = SnapshotClaim.Busy;
                return 0UL;
            }

            // Unclaimed for this tick — never filled, last tick's, or a claim its holder abandoned. Retried on a lost race rather than waited on.
            if (Interlocked.CompareExchange(ref stamp, -tick, seen) == seen)
            {
                claim = SnapshotClaim.Fill;
                return 0UL;
            }
        }
    }

    /// <summary>
    /// Releases a claim whose holder failed before <see cref="Fill"/> — the page could not be opened — so a waiter re-claims instead of spinning on a fill
    /// that will never come.
    /// </summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="tick">The tick the claim was taken for.</param>
    public void Abandon(int chunkId, long tick)
    {
        if (tick > 0 && (uint)chunkId < (uint)_entries.Length)
        {
            Interlocked.CompareExchange(ref _entries[chunkId].Tick, 0, -tick);
        }
    }

    /// <summary>Stamps a cluster as read on <paramref name="tick"/>.</summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="tick">The tick.</param>
    /// <returns><see langword="true"/> for the first read this tick, as far as this caller can tell — two racing first readers may both see it.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MarkRead(int chunkId, long tick)
    {
        if ((uint)chunkId >= (uint)_entries.Length)
        {
            return false;
        }

        ref var readTick = ref _entries[chunkId].ReadTick;
        if (readTick == tick)
        {
            return false;
        }

        readTick = tick;
        return true;
    }

    /// <summary>The cluster's replication block as resolved by this tick's fill, when the cluster was filled on <paramref name="tick"/>.</summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="tick">This tick.</param>
    /// <param name="block">The block, or zero when the cluster has none.</param>
    /// <returns><see langword="true"/> when the cache is this tick's; otherwise the caller resolves the block itself.</returns>
    public bool TryBlockOf(int chunkId, long tick, out nint block)
    {
        if (tick > 0 && (uint)chunkId < (uint)_entries.Length)
        {
            ref var entry = ref _entries[chunkId];
            if (Volatile.Read(ref entry.Tick) == tick)
            {
                block = entry.Block;
                return true;
            }
        }

        block = 0;
        return false;
    }

    /// <summary>Reads a cluster's page into the store and publishes it for <paramref name="tick"/>.</summary>
    /// <param name="chunkId">The cluster, whose fill the caller claimed through <see cref="TryGet"/>.</param>
    /// <param name="tick">The tick.</param>
    /// <param name="clusterBase">The cluster's base address, from the enumerator's own accessor.</param>
    /// <param name="fieldsOffset">Byte offset of the spatial column from the base.</param>
    /// <param name="stride">The spatial column's stride.</param>
    /// <param name="block">The cluster's replication block, or zero when it has none.</param>
    /// <returns>The occupancy.</returns>
    /// <remarks>
    /// The stamp is published in a <c>finally</c>, so a fault while reading the page can never leave another worker spinning on a claim nobody will
    /// release. A cluster beyond the store's capacity is not published at all — it was never claimed, only opened.
    /// </remarks>
    public ulong Fill(int chunkId, long tick, byte* clusterBase, int fieldsOffset, int stride, nint block)
    {
        var occupancy = Volatile.Read(ref *(ulong*)clusterBase);
        if ((uint)chunkId >= (uint)_entries.Length)
        {
            return occupancy;
        }

        ref var entry = ref _entries[chunkId];
        try
        {
            Transpose(clusterBase + fieldsOffset, stride, occupancy,
                _columns.AsSpan(chunkId * InterestBandKernel.ColumnFloats, InterestBandKernel.ColumnFloats));
            entry.Occupancy = occupancy;
            entry.Block = block;
        }
        finally
        {
            Volatile.Write(ref entry.Tick, tick);
        }

        return occupancy;
    }

    /// <summary>
    /// Copies the occupied slots' boxes of a cluster column in page memory into four 64-float columns. Slots not in <paramref name="occupancy"/> are left
    /// as they were: every reader masks by the occupancy.
    /// </summary>
    /// <param name="fields">Slot 0's spatial field in the cluster's page.</param>
    /// <param name="stride">Bytes between two slots' fields.</param>
    /// <param name="occupancy">The slots to copy.</param>
    /// <param name="columns">Destination: <see cref="InterestBandKernel.ColumnFloats"/> floats.</param>
    internal static void Transpose(byte* fields, int stride, ulong occupancy, Span<float> columns)
    {
        var bits = occupancy;
        while (bits != 0UL)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            var box = (AABB2F*)(fields + (slot * stride));
            columns[slot] = box->MinX;
            columns[64 + slot] = box->MinY;
            columns[128 + slot] = box->MaxX;
            columns[192 + slot] = box->MaxY;
        }
    }

    /// <summary>The first float of a cluster's columns, as this tick's fill left them.</summary>
    /// <param name="chunkId">The cluster.</param>
    /// <returns>A reference to MinX[0]; MinY, MaxX and MaxY follow at 64-float steps.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref float Columns(int chunkId) => ref _columns[chunkId * InterestBandKernel.ColumnFloats];

    /// <summary>Whether <paramref name="chunkId"/> is inside the store, so its boxes can be read from it rather than from a page.</summary>
    /// <param name="chunkId">The cluster.</param>
    /// <returns><see langword="true"/> when the store covers it.</returns>
    public bool Covers(int chunkId) => (uint)chunkId < (uint)_entries.Length;

    /// <summary>
    /// The occupancy as last filled, without a claim. Current only for a cluster this tick's broad phase filled or read; telemetry uses it for the clusters
    /// the member's kernel just reported, which all came through that broad phase.
    /// </summary>
    /// <param name="chunkId">The cluster.</param>
    /// <returns>The occupancy, or zero beyond the store.</returns>
    public ulong OccupancyOf(int chunkId) => (uint)chunkId < (uint)_entries.Length ? _entries[chunkId].Occupancy : 0UL;
}
