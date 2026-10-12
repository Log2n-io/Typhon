using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// A cluster's in-memory words: its claim word, its pending-death count and its two visibility watermarks (<see cref="ClusterMaxBorn"/>,
/// <see cref="ClusterMaxDied"/>), side by side in one 32-byte entry, so the cluster gate reads one entry (<see cref="SlotWords"/>).
/// <para>
/// The claim word (VIS-08, VIS-09, plan 06 steps 5.2–5.3): every slot claimed at run time, in memory beside the cluster. A slot is claimed from its claim to
/// its final release, while its page occupancy bit — the published set HEAD readers trust — is set only while the entity is published: a spawn placed in
/// PREPARE is claimed and not occupied until its PUBLISH, a destroyed entity of a Versioned archetype stays claimed and not occupied (hidden) until the
/// cleanup releases it. The claim word is never on a page, so a fuzzy checkpoint can persist a pending slot's bytes but never its occupancy bit, and a crash
/// rebuild from occupancy resurrects no uncommitted spawn (CM-01).
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>One claim protocol: claimers CAS the claim word.</b> A slot is free when neither its claim bit nor its occupancy bit is set — occupancy covers the slots a
/// load or a recovery set without a run-time claim, whose claim bit is 0. Only a claim sets a claim bit, by CAS, and only the slot's owner clears it (an abort,
/// a release, the cleanup), so a stale claimer's CAS can only take a slot that is free again: no ABA. The occupancy word is written by owners alone, with
/// Interlocked, and never by a claim: a spawn publishes it at its PUBLISH, any other claimer once the slot holds its data (<see cref="PublishClaimedSlot"/>).
/// </para>
/// <para>
/// <b>Readers read occupancy first, then claims.</b> A slot claimed and not occupied (<see cref="PendingBits"/>) is probed — its record decides. A spawn's claim
/// bit stays set across its PUBLISH, so a reader whose occupancy read precedes the publish finds the slot claimed; one whose occupancy read follows it trusts
/// the bit. A hidden slot is released, its claim cleared, only once no snapshot can see the entity alive.
/// </para>
/// <para>
/// <b>Blocks never move.</b> The tables are arrays of fixed blocks, grown under a lock, and an element's address is stable for the archetype's life: an
/// <see cref="Interlocked"/> on a word can never land in an array a concurrent grow is copying away from (the hazard <c>ClusterAabbs</c> needs a stamp for).
/// The watermarks lived in two arrays grown by copy, so a fold had to re-read the array after its CAS and the gate made three lookups per cluster; in a block a
/// fold lands where every reader looks, and the occupancy count and the scans read a cluster's four words through one lookup.
/// </para>
/// </remarks>
internal sealed unsafe partial class ArchetypeClusterState
{
    private const int ClaimBlockShift = 10;
    private const int ClaimBlockSize = 1 << ClaimBlockShift;
    private const int ClusterWordsShift = 2;   // four words a cluster
    private const int ClaimWordIndex = 0;
    private const int PendingDeathWordIndex = 1;
    private const int MaxBornWordIndex = 2;
    private const int MaxDiedWordIndex = 3;
    private long[][] _slotWordBlocks = [];
    private readonly Lock _claimGrowLock = new();

    /// <summary>
    /// The block table, by acquire. A loop over clusters reads it once, AFTER the active cluster list: a cluster is in that list only once its words exist, so
    /// the table read after it has a block for every id the list holds.
    /// </summary>
    internal long[][] SlotWordTable => Volatile.Read(ref _slotWordBlocks);

    /// <summary>
    /// The first of <paramref name="clusterChunkId"/>'s four words in <paramref name="table"/>, or a null ref when no block holds it: no claim, no pending
    /// death, an unestablished born watermark — the gate denies.
    /// </summary>
    internal static ref long SlotWords(long[][] table, int clusterChunkId)
    {
        var b = clusterChunkId >> ClaimBlockShift;
        if ((uint)b >= (uint)table.Length)
        {
            return ref Unsafe.NullRef<long>();
        }

        var block = Volatile.Read(ref table[b]);
        if (block == null)
        {
            return ref Unsafe.NullRef<long>();
        }

        return ref block[((clusterChunkId & (ClaimBlockSize - 1)) << ClusterWordsShift) + ClaimWordIndex];
    }

    /// <summary>The claim word at <paramref name="words"/> (<see cref="SlotWords"/>), by acquire.</summary>
    internal static ulong ClaimOf(ref long words) => Unsafe.IsNullRef(ref words) ? 0 : (ulong)Volatile.Read(ref words);

    /// <summary>The pending-death count at <paramref name="words"/> (<see cref="SlotWords"/>), by acquire.</summary>
    internal static long PendingDeathsOf(ref long words) =>
        Unsafe.IsNullRef(ref words) ? 0 : Volatile.Read(ref Unsafe.Add(ref words, PendingDeathWordIndex - ClaimWordIndex));

    /// <summary>The born watermark at <paramref name="words"/> (<see cref="SlotWords"/>), by acquire; <see cref="VisibilityUnknown"/> without a block.</summary>
    internal static long MaxBornOf(ref long words) =>
        Unsafe.IsNullRef(ref words) ? VisibilityUnknown : Volatile.Read(ref Unsafe.Add(ref words, MaxBornWordIndex - ClaimWordIndex));

    /// <summary>The died watermark at <paramref name="words"/> (<see cref="SlotWords"/>), by acquire; 0 without a block, where the born side denies.</summary>
    internal static long MaxDiedOf(ref long words) =>
        Unsafe.IsNullRef(ref words) ? 0 : Volatile.Read(ref Unsafe.Add(ref words, MaxDiedWordIndex - ClaimWordIndex));

    /// <summary>
    /// The cluster gate's watermark half at <paramref name="words"/>: every entity born at or before <paramref name="txTsn"/> and every recorded death visible
    /// to it. The caller has read the occupancy word first, by acquire (CLUSTERVIS-01), and decides the pending-death count itself.
    /// </summary>
    internal static bool WatermarksVisibleAt(ref long words, long txTsn)
    {
        var born = MaxBornOf(ref words);
        if (born == VisibilityUnknown || born > txTsn)
        {
            return false;
        }

        // 0 means no death has been recorded, and 0 <= txTsn for every real snapshot, so a cluster nothing has died in never blocks. A reader whose snapshot
        // has reached the last death sees an occupancy word that already reflects it.
        return MaxDiedOf(ref words) <= txTsn;
    }

    /// <summary>
    /// Per-cluster maximum <c>BornTSN</c> over the entities it holds, or <see cref="VisibilityUnknown"/> when no site has established it. A reader at snapshot
    /// <c>txTsn</c> may skip the per-entity born check for this cluster iff it is established and <c>&lt;= txTsn</c>.
    /// </summary>
    /// <remarks>
    /// In-memory only, like <see cref="ClusterAabbs"/> — a reopen rewrites every record with <c>BornTSN = 0</c> (committed before this open, visible at every
    /// snapshot), so the rebuild seeds 0 rather than persisting anything.
    /// </remarks>
    internal long ClusterMaxBorn(int clusterChunkId) => MaxBornOf(ref SlotWords(SlotWordTable, clusterChunkId));

    /// <summary>
    /// Per-cluster maximum <c>DiedTSN</c> over the entities it has ever held, or 0 when nothing has died there. A reader at snapshot <c>txTsn</c> may skip the
    /// per-entity died check for this cluster iff it is <c>&lt;= txTsn</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was a sticky bit until #722.</b> "Has anything ever died here?" is a question that can only be un-asked by a full re-scan, so the bit was never
    /// cleared and a churning archetype latched permanently onto the per-entity probe — a documented ceiling for <c>Count()</c>, and fatal for anything that
    /// runs every tick, which is what an unfiltered View's refresh does.
    /// </para>
    /// <para>
    /// The question a reader actually needs answered is not <i>whether</i> something died but <i>whether every death here is already visible to it</i>, and
    /// that is a maximum rather than a boolean. Occupancy drops a destroyed entity while its tombstone lives on the EntityMap record, so the only hazard a death
    /// creates is for a reader OLDER than it. A reader newer than every death in the cluster is exact. A maximum only ever rises, so it needs no re-scan to fall
    /// — it never falls — and the gate recovers on its own as snapshots advance past the last death.
    /// </para>
    /// </remarks>
    internal long ClusterMaxDied(int clusterChunkId) => MaxDiedOf(ref SlotWords(SlotWordTable, clusterChunkId));

    /// <summary>The born watermark's word, its block created if absent — for the folds and for fixtures that stage a summary.</summary>
    internal ref long MaxBornWord(int clusterChunkId) => ref BlockWord(clusterChunkId, MaxBornWordIndex);

    /// <summary>The died watermark's word, its block created if absent — for the folds and for fixtures that stage a summary.</summary>
    internal ref long MaxDiedWord(int clusterChunkId) => ref BlockWord(clusterChunkId, MaxDiedWordIndex);

    /// <summary>The cluster ids the blocks cover: every id below it has words; one above it has none, and reads as unestablished.</summary>
    internal int ClusterWordCapacity => SlotWordTable.Length << ClaimBlockShift;

    /// <summary>The run-time claimed slots of <paramref name="clusterChunkId"/> — pending, occupied or hidden; 0 for a cluster nothing claimed in.</summary>
    internal ulong ClaimedBits(int clusterChunkId) => ClaimOf(ref SlotWords(SlotWordTable, clusterChunkId));

    /// <summary>
    /// The slots claimed and not occupied — a spawn placed and not yet published, or a destroyed entity hidden until the cleanup. <paramref name="occupancy"/> is
    /// read first, by the caller.
    /// </summary>
    internal ulong PendingBits(ulong occupancy, int clusterChunkId) => ClaimedBits(clusterChunkId) & ~occupancy;

    /// <summary>Taken slots — claimed or occupied. The occupancy word is read first, then the claim word.</summary>
    internal ulong TakenBits(ulong occupancy, int clusterChunkId) => occupancy | ClaimedBits(clusterChunkId);

    private ref long ClaimWord(int clusterChunkId) => ref BlockWord(clusterChunkId, ClaimWordIndex);

    private ref long PendingDeathWord(int clusterChunkId) => ref BlockWord(clusterChunkId, PendingDeathWordIndex);

    private ref long BlockWord(int clusterChunkId, int wordIndex)
    {
        var b = clusterChunkId >> ClaimBlockShift;
        var blocks = Volatile.Read(ref _slotWordBlocks);
        if ((uint)b >= (uint)blocks.Length || Volatile.Read(ref blocks[b]) == null)
        {
            lock (_claimGrowLock)
            {
                blocks = _slotWordBlocks;
                if ((uint)b >= (uint)blocks.Length)
                {
                    var grown = new long[b + 1][];
                    blocks.CopyTo(grown, 0);
                    blocks = grown;
                }

                if (blocks[b] == null)
                {
                    // Every born watermark starts unestablished — 0 would read as "visible to everyone" — and the block is release-stored filled: a reader that
                    // finds it must find the sentinels.
                    var fresh = new long[ClaimBlockSize << ClusterWordsShift];
                    for (var i = MaxBornWordIndex; i < fresh.Length; i += 1 << ClusterWordsShift)
                    {
                        fresh[i] = VisibilityUnknown;
                    }

                    Volatile.Write(ref blocks[b], fresh);
                }

                Volatile.Write(ref _slotWordBlocks, blocks);
            }
        }

        return ref blocks[b][((clusterChunkId & (ClaimBlockSize - 1)) << ClusterWordsShift) + wordIndex];
    }

    /// <summary>
    /// Claims one free slot of <paramref name="clusterChunkId"/> by CAS on its claim word; -1 when none is free. Free is clear in both words: the occupancy word
    /// is read first, so a slot a load set occupied without a claim is never taken twice. The occupancy bit is NOT set: the slot is claimed and not occupied,
    /// which every reader decides through its record (VIS-08), until its owner publishes it — a spawn at its PUBLISH (<see cref="PublishReservedSlot"/>), any
    /// other claimer at once (<see cref="PublishClaimedSlot"/>). Set here, a snapshot reader past the claimer's TSN passed the cluster gate and counted a slot
    /// with no data and no record.
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
                return slot;
            }
        }
    }

    /// <summary>Claims the named slot if it is free; one CAS, no retry (see <c>TryClaimExactSlotInCluster</c>). The occupancy bit as in <see cref="ClaimFirstFree"/>.</summary>
    private bool ClaimExact(ref ulong occupancy, int clusterChunkId, int slotIndex)
    {
        ref var claims = ref ClaimWord(clusterChunkId);
        var bit = 1L << slotIndex;
        var occupied = Volatile.Read(ref occupancy);
        var claimed = Volatile.Read(ref claims);
        return (((ulong)claimed | occupied) & (ulong)bit) == 0 && Interlocked.CompareExchange(ref claims, claimed | bit, claimed) == claimed;
    }

    /// <summary>A freshly allocated cluster's slot 0 claimed, and its occupancy word empty, before the cluster is published to any claimer.</summary>
    private void InitFreshCluster(byte* clusterBase, int clusterChunkId)
    {
        Volatile.Write(ref ClaimWord(clusterChunkId), 1L);
        Volatile.Write(ref *(ulong*)clusterBase, 0UL);
    }

    /// <summary>
    /// A claimed slot published as occupied by a claimer other than a spawn — recovery, the open's rebuild, a migration — which publish at once, as the claim
    /// used to. A spawn publishes at its PUBLISH (<see cref="PublishReservedSlot"/>).
    /// </summary>
    internal void PublishClaimedSlot(byte* clusterBase, int slotIndex) => Interlocked.Or(ref *(long*)clusterBase, 1L << slotIndex);

    /// <summary>A released slot's claim bit cleared — after its occupancy bit and metadata — returning the claims left.</summary>
    private ulong ReleaseClaim(int clusterChunkId, int slotIndex) =>
        (ulong)Interlocked.And(ref ClaimWord(clusterChunkId), ~(1L << slotIndex)) & ~(1UL << slotIndex);

    /// <summary>
    /// The destroys of <paramref name="clusterChunkId"/>'s entities pending a commit (VIS-09, family 4): marked in PREPARE, their slots still occupied and
    /// their died watermark not folded yet. While it is non-zero the cluster gate denies (<see cref="IsClusterFullyVisibleAt(int, long)"/>): a reader whose
    /// snapshot is above such a destroy's stamp must not count, by the occupancy word, an entity the commit removed. A reader loads it BEFORE the occupancy
    /// word: the publish hides the slot, then lifts the count, so a zero read first means the occupancy read after it reflects the hide.
    /// </summary>
    internal long PendingDeaths(int clusterChunkId) => PendingDeathsOf(ref SlotWords(SlotWordTable, clusterChunkId));

    /// <summary>A destroy marked pending (+1), or published or aborted (-1). The publish decrements after it folded the watermark and hid the slot.</summary>
    internal void AddPendingDeath(int clusterChunkId, int delta) => Interlocked.Add(ref PendingDeathWord(clusterChunkId), delta);

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
            Volatile.Write(ref PendingDeathWord(clusterChunkId), 0);
        }
    }
}
