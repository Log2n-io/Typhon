using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The claim word (VIS-08, VIS-09, plan 06 steps 5.2–5.3): every slot claimed at run time, in memory beside the cluster. A slot is claimed from its claim to
/// its final release, while its page occupancy bit — the published set HEAD readers trust — is set only while the entity is published: a spawn placed in
/// PREPARE is claimed and not occupied until its PUBLISH, a destroyed entity of a Versioned archetype stays claimed and not occupied (hidden) until the
/// cleanup releases it. The claim word is never on a page, so a fuzzy checkpoint can persist a pending slot's bytes but never its occupancy bit, and a crash
/// rebuild from occupancy resurrects no uncommitted spawn (CM-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>One claim protocol: claimers CAS the claim word.</b> A slot is free when neither its claim bit nor its occupancy bit is set — occupancy covers the slots a
/// load or a recovery set without a run-time claim, whose claim bit is 0. Only a claim sets a claim bit, by CAS, and only the slot's owner clears it (an abort,
/// a release, the cleanup), so a stale claimer's CAS can only take a slot that is free again: no ABA. The occupancy word is written by owners alone, with
/// Interlocked — a claim publishes it at once unless the claimer is a spawn, which clears it again until its PUBLISH.
/// </para>
/// <para>
/// <b>Readers read occupancy first, then claims.</b> A slot claimed and not occupied (<see cref="PendingBits"/>) is probed — its record decides. A spawn's claim
/// bit stays set across its PUBLISH, so a reader whose occupancy read precedes the publish finds the slot claimed; one whose occupancy read follows it trusts
/// the bit. A hidden slot is released, its claim cleared, only once no snapshot can see the entity alive.
/// </para>
/// <para>
/// <b>Blocks never move.</b> The tables are arrays of fixed blocks, grown under a lock, and an element's address is stable for the archetype's life: an
/// <see cref="Interlocked"/> on a word can never land in an array a concurrent grow is copying away from (the hazard <c>ClusterAabbs</c> needs a stamp for).
/// </para>
/// </remarks>
internal sealed unsafe partial class ArchetypeClusterState
{
    private const int ClaimBlockShift = 10;
    private const int ClaimBlockSize = 1 << ClaimBlockShift;
    private long[][] _claimBlocks = [];
    private long[][] _pendingDeathBlocks = [];
    private readonly Lock _claimGrowLock = new();

    /// <summary>The run-time claimed slots of <paramref name="clusterChunkId"/> — pending, occupied or hidden; 0 for a cluster nothing claimed in.</summary>
    internal ulong ClaimedBits(int clusterChunkId) => (ulong)ReadBlockWord(_claimBlocks, clusterChunkId);

    /// <summary>
    /// The slots claimed and not occupied — a spawn placed and not yet published, or a destroyed entity hidden until the cleanup. <paramref name="occupancy"/> is
    /// read first, by the caller.
    /// </summary>
    internal ulong PendingBits(ulong occupancy, int clusterChunkId) => ClaimedBits(clusterChunkId) & ~occupancy;

    /// <summary>Taken slots — claimed or occupied. The occupancy word is read first, then the claim word.</summary>
    internal ulong TakenBits(ulong occupancy, int clusterChunkId) => occupancy | ClaimedBits(clusterChunkId);

    private static long ReadBlockWord(long[][] table, int clusterChunkId)
    {
        var blocks = Volatile.Read(ref table);
        var b = clusterChunkId >> ClaimBlockShift;
        if ((uint)b >= (uint)blocks.Length)
        {
            return 0;
        }

        var block = Volatile.Read(ref blocks[b]);
        return block == null ? 0 : Volatile.Read(ref block[clusterChunkId & (ClaimBlockSize - 1)]);
    }

    private ref long ClaimWord(int clusterChunkId) => ref BlockWord(ref _claimBlocks, clusterChunkId);

    private ref long BlockWord(ref long[][] table, int clusterChunkId)
    {
        var b = clusterChunkId >> ClaimBlockShift;
        var blocks = Volatile.Read(ref table);
        if ((uint)b >= (uint)blocks.Length || Volatile.Read(ref blocks[b]) == null)
        {
            lock (_claimGrowLock)
            {
                blocks = table;
                if ((uint)b >= (uint)blocks.Length)
                {
                    var grown = new long[b + 1][];
                    blocks.CopyTo(grown, 0);
                    blocks = grown;
                }

                blocks[b] ??= new long[ClaimBlockSize];
                Volatile.Write(ref table, blocks);
            }
        }

        return ref blocks[b][clusterChunkId & (ClaimBlockSize - 1)];
    }

    /// <summary>
    /// Claims one free slot of <paramref name="clusterChunkId"/> by CAS on its claim word and publishes its occupancy bit; -1 when none is free. Free is clear in
    /// both words: the occupancy word is read first, so a slot a load set occupied without a claim is never taken twice.
    /// </summary>
    private int ClaimFirstFree(ref ulong occupancy, int clusterChunkId, ulong fullMask)
    {
        ref var claims = ref ClaimWord(clusterChunkId);
        while (true)
        {
            var occupied = Volatile.Read(ref occupancy);
            var claimed = Volatile.Read(ref claims);
            var available = ~((ulong)claimed | occupied) & fullMask;
            if (available == 0)
            {
                return -1;
            }

            var slot = BitOperations.TrailingZeroCount(available);
            var bit = 1L << slot;
            if (Interlocked.CompareExchange(ref claims, claimed | bit, claimed) == claimed)
            {
                Interlocked.Or(ref Unsafe.As<ulong, long>(ref occupancy), bit);
                return slot;
            }
        }
    }

    /// <summary>Claims the named slot if it is free, publishing its occupancy bit; one CAS, no retry (see <c>TryClaimExactSlotInCluster</c>).</summary>
    private bool ClaimExact(ref ulong occupancy, int clusterChunkId, int slotIndex)
    {
        ref var claims = ref ClaimWord(clusterChunkId);
        var bit = 1L << slotIndex;
        var occupied = Volatile.Read(ref occupancy);
        var claimed = Volatile.Read(ref claims);
        if ((((ulong)claimed | occupied) & (ulong)bit) != 0 || Interlocked.CompareExchange(ref claims, claimed | bit, claimed) != claimed)
        {
            return false;
        }

        Interlocked.Or(ref Unsafe.As<ulong, long>(ref occupancy), bit);
        return true;
    }

    /// <summary>A freshly allocated cluster's slot 0, claimed and occupied before the cluster is published to any claimer.</summary>
    private void InitFreshCluster(byte* clusterBase, int clusterChunkId)
    {
        Volatile.Write(ref ClaimWord(clusterChunkId), 1L);
        Volatile.Write(ref *(ulong*)clusterBase, 1UL);
    }

    /// <summary>A released slot's claim bit cleared — after its occupancy bit and metadata — returning the claims left.</summary>
    private ulong ReleaseClaim(int clusterChunkId, int slotIndex) =>
        (ulong)Interlocked.And(ref ClaimWord(clusterChunkId), ~(1L << slotIndex)) & ~(1UL << slotIndex);

    /// <summary>
    /// The destroys of <paramref name="clusterChunkId"/>'s entities pending a commit (VIS-09, family 4): marked in PREPARE, their slots still occupied and
    /// their died watermark not folded yet. While it is non-zero the cluster gate denies (<see cref="IsClusterFullyVisibleAt(int, long)"/>): a reader whose
    /// snapshot is above such a destroy's stamp must not count, by the occupancy word, an entity the commit removed. A reader loads it BEFORE the occupancy
    /// word: the publish hides the slot, then lifts the count, so a zero read first means the occupancy read after it reflects the hide.
    /// </summary>
    internal long PendingDeaths(int clusterChunkId) => ReadBlockWord(_pendingDeathBlocks, clusterChunkId);

    /// <summary>A destroy marked pending (+1), or published or aborted (-1). The publish decrements after it folded the watermark and hid the slot.</summary>
    internal void AddPendingDeath(int clusterChunkId, int delta) => Interlocked.Add(ref BlockWord(ref _pendingDeathBlocks, clusterChunkId), delta);

    /// <summary>
    /// A spawn's slot, just claimed (claimed and occupied), taken out of the published set until its PUBLISH: the occupancy bit cleared, the claim kept.
    /// </summary>
    internal void ReserveClaimedSlot(byte* clusterBase, int clusterChunkId, int slotIndex) => Interlocked.And(ref *(long*)clusterBase, ~(1L << slotIndex));

    /// <summary>
    /// PUBLISH of a placed spawn's slot: the born watermark folds the commit stamp first (BIND-04), then the occupancy bit is set. The claim stays. The caller
    /// has written the EntityMap record's stamp before.
    /// </summary>
    internal void PublishReservedSlot(byte* clusterBase, int clusterChunkId, int slotIndex, long stamp)
    {
        NoteClusterBorn(clusterChunkId, stamp);
        Interlocked.Or(ref *(long*)clusterBase, 1L << slotIndex);
    }

    /// <summary>
    /// An aborted spawn gives its slot back (AP-07): the slot's entity id and enabled bits cleared, the claim released, the cell's count decremented, and a
    /// cluster left with nothing occupied or claimed finalised and freed as a drained one is.
    /// </summary>
    internal void UnreserveSlot(byte* clusterBase, int clusterChunkId, int slotIndex, SpatialGrid grid)
    {
        ClearSlotMetadata(clusterBase, slotIndex);
        DecrementCellEntityCountOnRelease(grid, clusterChunkId, true);
        FinishVacatedSlot(clusterBase, clusterChunkId, slotIndex, grid);
    }

    /// <summary>
    /// PUBLISH of a destroy in an archetype that promises snapshot isolation (VIS-09): the slot leaves HEAD — the change signals, the cell's count, the
    /// shrink flag, the occupancy bit — and stays claimed, its entity id and bytes in place, so a snapshot older than the destroy still finds the entity by
    /// scan until the cleanup releases it (<see cref="ReleaseHiddenSlot"/>) once no snapshot can see it alive. The claim is set first — a slot a load occupied
    /// has none. The died watermark is the caller's, folded before (BIND-04).
    /// </summary>
    internal void HideDestroyedSlot(byte* clusterBase, int clusterChunkId, int slotIndex, SpatialGrid grid)
    {
        var bit = 1L << slotIndex;
        if ((Volatile.Read(ref *(ulong*)clusterBase) & (ulong)bit) == 0)
        {
            return;   // not occupied: nothing of this entity to hide
        }

        NoteSlotsChanged(clusterChunkId, (ulong)bit);
        NoteStructureSlots(clusterChunkId, (ulong)bit);
        Interlocked.Or(ref ClaimWord(clusterChunkId), bit);
        Interlocked.And(ref *(long*)clusterBase, ~bit);

        // The slot is not reusable before the cleanup, so the claim cursor is left where it is.
        DecrementCellEntityCountOnRelease(grid, clusterChunkId, false);
        Interlocked.Increment(ref SlotReleasesThisTick);
        FlagClusterShrinkAxesOnly(clusterChunkId);
    }

    /// <summary>
    /// The cleanup's half of a hidden destroy (VIS-09): the slot's collection buffers released, its metadata cleared, its claim released — the slot claimable
    /// again — and a cluster left with nothing occupied or claimed finalised and freed.
    /// </summary>
    internal void ReleaseHiddenSlot(byte* clusterBase, int clusterChunkId, int slotIndex, ChangeSet changeSet, SpatialGrid grid)
    {
        if (CollectionSlots != null)
        {
            ReleaseSlotCollections(clusterBase, slotIndex, changeSet);
        }

        ClearSlotMetadata(clusterBase, slotIndex);
        FinishVacatedSlot(clusterBase, clusterChunkId, slotIndex, grid);
    }

    /// <summary>Whether <paramref name="slotIndex"/> is hidden: claimed and not occupied, its occupancy word at <paramref name="clusterBase"/>.</summary>
    internal bool IsHidden(byte* clusterBase, int clusterChunkId, int slotIndex) =>
        (PendingBits(Volatile.Read(ref *(ulong*)clusterBase), clusterChunkId) & (1UL << slotIndex)) != 0;

    /// <summary>The vacated slot's claim released, and a cluster left with nothing occupied or claimed finalised and freed.</summary>
    /// <remarks>
    /// The free is inline, as <c>ReleaseSlot</c>'s non-deferred path's is: a claimer that read this cluster as the free head before the drain can still CAS into
    /// it (#1315). The callers here are an abort and the cleanup.
    /// </remarks>
    private void FinishVacatedSlot(byte* clusterBase, int clusterChunkId, int slotIndex, SpatialGrid grid)
    {
        var claimsLeft = ReleaseClaim(clusterChunkId, slotIndex);
        var drained = claimsLeft == 0 && (Volatile.Read(ref *(ulong*)clusterBase) & Layout.FullMask) == 0;
        if (drained)
        {
            FinaliseEmptyClusterCellState(grid, clusterChunkId);
            RemoveFromActiveList(clusterChunkId);
            RetireClusterId(clusterChunkId);
            ClusterSegment?.FreeChunk(clusterChunkId);
            TransientSegment?.FreeChunk(clusterChunkId);
        }
        else if (FreeClusterHead < 0)
        {
            FreeClusterHead = clusterChunkId;
        }
    }

    /// <summary>A retired cluster id's run-time words, cleared — the id is about to be recyclable.</summary>
    private void ResetClaimWords(int clusterChunkId)
    {
        if (ClaimedBits(clusterChunkId) != 0)
        {
            Volatile.Write(ref ClaimWord(clusterChunkId), 0);
        }

        if (PendingDeaths(clusterChunkId) != 0)
        {
            Volatile.Write(ref BlockWord(ref _pendingDeathBlocks, clusterChunkId), 0);
        }
    }
}
