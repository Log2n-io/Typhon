using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// A cluster's in-memory words: its claim word, its pending-death count, its visibility watermarks (<see cref="ClusterMaxBorn"/>,
/// <see cref="ClusterMaxDied"/>, <see cref="ClusterMaxUpdated"/>) and its slots' index locks (<see cref="LockSlotIndexes"/>), side by side in
/// one entry, so the cluster gate reads one entry (<see cref="SlotWords"/>).
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
    private const int ClusterWords = 6;   // six words a cluster
    private const int ClaimWordIndex = 0;
    private const int PendingDeathWordIndex = 1;
    private const int MaxBornWordIndex = 2;
    private const int MaxDiedWordIndex = 3;
    private const int MaxUpdatedWordIndex = 4;
    private const int SlotIndexLockWordIndex = 5;
    private long[][] _slotWordBlocks = [];
    private readonly Lock _claimGrowLock = new();

    /// <summary>
    /// The block table, by acquire. A cluster can be listed before its block exists — a fresh or repaired cluster, one a clean reopen loaded — and a missing
    /// block reads as no claim, no pending death and an unestablished born watermark: the gate denies. A reader that must see a claim set after its first
    /// lookup — the snapshot scan, whose occupancy read follows it — resolves the words again when the first lookup found no block.
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

        return ref block[(clusterChunkId & (ClaimBlockSize - 1)) * ClusterWords + ClaimWordIndex];
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
    /// The update watermark at <paramref name="words"/> (<see cref="SlotWords"/>), by acquire: the highest stamp of a Versioned update that moved an index
    /// entry or the HEAD of an entity in the cluster (#674, VIS-12); 0 for none, or without a block — a cluster with no block holds nothing an update wrote.
    /// </summary>
    internal static long MaxUpdatedOf(ref long words) =>
        Unsafe.IsNullRef(ref words) ? 0 : Volatile.Read(ref Unsafe.Add(ref words, MaxUpdatedWordIndex - ClaimWordIndex));

    /// <summary>
    /// The cluster gate's watermark half at <paramref name="words"/>: every entity born at or before <paramref name="txTsn"/> and every recorded death visible
    /// to it. The caller has read the occupancy word first, by acquire (CLUSTERVIS-01), and decides the pending-death count itself.
    /// </summary>
    internal static bool WatermarksVisibleAt(ref long words, long txTsn)
    {
        // Died BEFORE born: a retire writes born = VisibilityUnknown, then died = 0 (ResetClusterVisibility), so a reader that sees the reset's died then reads
        // the reset's born and denies. Born first could pair the retiring cluster's old born with the new died = 0 and grant.
        var died = MaxDiedOf(ref words);
        var born = MaxBornOf(ref words);
        if (born == VisibilityUnknown || born > txTsn)
        {
            return false;
        }

        // 0 means no death has been recorded, and 0 <= txTsn for every real snapshot, so a cluster nothing has died in never blocks. A reader whose snapshot
        // has reached the last death sees an occupancy word that already reflects it.
        return died <= txTsn;
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

    /// <summary>
    /// Per-cluster highest stamp of a Versioned update that moved an entity's index entries or HEAD (#674, VIS-12). A reader whose snapshot is at or above it
    /// reads the cluster's HEAD bytes and tree keys as its own; below it, it reads the cluster's entities at its snapshot. Only rises; in memory only — a
    /// reopen starts every reader above every update.
    /// </summary>
    internal long ClusterMaxUpdated(int clusterChunkId) => MaxUpdatedOf(ref SlotWords(SlotWordTable, clusterChunkId));

    /// <summary>
    /// Folds a Versioned update's stamp into its cluster's update watermark (VIS-12), BEFORE the publish moves the entity's index entries or copies its value
    /// into the HEAD: a reader that finds either moved reads the watermark above its snapshot.
    /// </summary>
    internal void NoteClusterUpdated(int clusterChunkId, long stamp)
    {
        ref var word = ref BlockWord(clusterChunkId, MaxUpdatedWordIndex);
        while (true)
        {
            var current = Volatile.Read(ref word);
            if (stamp <= current || Interlocked.CompareExchange(ref word, stamp, current) == current)
            {
                return;
            }
        }
    }

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

    /// <summary>
    /// Taken slots — claimed, occupied, or owing the fence (<see cref="FenceOwedBits"/>). The occupancy word is read first, then the claim word, then the
    /// shadow word.
    /// </summary>
    internal ulong TakenBits(ulong occupancy, int clusterChunkId)
    {
        var taken = occupancy | ClaimedBits(clusterChunkId);
        return taken | FenceOwedBits(clusterChunkId);
    }

    /// <summary>
    /// The slots of <paramref name="clusterChunkId"/> marked in <see cref="ClusterShadowBitmap"/> — an entity written since the fence. For a released slot
    /// that means its index entries are still in the trees, left for the fence's drain to remove (IX-06), so the slot is not handed out before that fence:
    /// the drain would take the new occupant for the destroyed entity with changed keys and move the dead entity's entries onto it, and until then the
    /// stale entries would name the new occupant (#1188). The fence clears the bitmap after the drain.
    /// </summary>
    /// <remarks>
    /// The word itself, never gated on <see cref="DirtyBitmap.AnyTestAndSetSinceClear"/>: <see cref="DirtyBitmap.TestAndSet"/> raises that flag after
    /// its OR, so a writer preempted between the two leaves a bit a destroy on another thread can see — and defer on — while the flag still reads clear.
    /// A claimer gated on it would take the slot. The claim word's acquire, after the destroy released the claim, orders this read after the bit.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong FenceOwedBits(int clusterChunkId) => ClusterShadowBitmap?.ReadWord(clusterChunkId) ?? 0;

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
                    var fresh = new long[ClaimBlockSize * ClusterWords];
                    for (var i = MaxBornWordIndex; i < fresh.Length; i += ClusterWords)
                    {
                        fresh[i] = VisibilityUnknown;
                    }

                    Volatile.Write(ref blocks[b], fresh);
                }

                Volatile.Write(ref _slotWordBlocks, blocks);
            }
        }

        return ref blocks[b][(clusterChunkId & (ClaimBlockSize - 1)) * ClusterWords + wordIndex];
    }

    /// <summary>
    /// Claims one free slot of <paramref name="clusterChunkId"/> by CAS on its claim word; -1 when none is free. Free is clear in both words, and not owing the
    /// fence (<see cref="FenceOwedBits"/>): the occupancy word
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
            // After the claim word: a destroy whose release a claimer sees in it had seen its slot's shadow bit (#1188).
            var available = ~((ulong)claimed | occupied | FenceOwedBits(clusterChunkId)) & fullMask;
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
        return (((ulong)claimed | occupied | FenceOwedBits(clusterChunkId)) & (ulong)bit) == 0
               && Interlocked.CompareExchange(ref claims, claimed | bit, claimed) == claimed;
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

    /// <summary>
    /// A destroy marked pending (+1), or published or aborted (-1). The publish decrements after it folded the watermark and hid the slot. The archetype's
    /// total (<see cref="PendingDeathsTotal"/>) is raised before the cluster's count and lowered after it, so a zero total read first means every cluster
    /// count read after it is zero too.
    /// </summary>
    internal void AddPendingDeath(int clusterChunkId, int delta)
    {
        if (delta > 0)
        {
            Interlocked.Add(ref _pendingDeathsTotal.Value, delta);
            Interlocked.Add(ref PendingDeathWord(clusterChunkId), delta);
        }
        else
        {
            Interlocked.Add(ref PendingDeathWord(clusterChunkId), delta);
            Interlocked.Add(ref _pendingDeathsTotal.Value, delta);
        }
    }

    // Padded both sides like the destroy stamp: every destroying commit of the archetype adds to it twice, and every walk reads it once per cluster.
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct PaddedPendingDeaths
    {
        [FieldOffset(64)] public long Value;
    }

    private PaddedPendingDeaths _pendingDeathsTotal;

    /// <summary>
    /// Destroys of this archetype pending a commit, all clusters together, by acquire (#1496): a cluster walk reads it before a cluster's own count and skips
    /// that read — a second dependent miss on the slot-word line — while it is zero, which it is unless a destroy is in flight. A retired cluster's count is
    /// zeroed without this total (<see cref="ResetClaimWords"/>), which can only leave it high: the walk then reads the cluster's count, never misses a death.
    /// </summary>
    internal long PendingDeathsTotal => Volatile.Read(ref _pendingDeathsTotal.Value);

    /// <summary>
    /// Takes <paramref name="slotIndex"/>'s index lock (VIS-08): the right to maintain its entity's B+Tree entries and element-id tails, and the keys its
    /// slot holds. A spawn holds it from its placement in PREPARE — before the card makes the entity visible — to the end of its PUBLISH; a commit that
    /// moves an indexed component's entries, publishes a staged write of one, or removes a destroyed entity's entries holds it for that step, and decides
    /// under it whether the slot still holds the entity alive (<see cref="HoldsLive"/>). Spins, unbounded, for a holder: none waits for anything a waiter
    /// can hold — the order is chain lock, then slot lock, then the trees' latches, and a commit that takes an entity's chain lock first waits, holding
    /// none, for a spawn still publishing it (<see cref="WaitSlotIndexesUnlocked(int, int)"/>): the spawner's component publish, which runs before its spawns',
    /// takes chain locks. A spawn that fails its publish releases its locks too (the engine stops committing then).
    /// </summary>
    internal void LockSlotIndexes(int clusterChunkId, int slotIndex)
    {
        ref var word = ref BlockWord(clusterChunkId, SlotIndexLockWordIndex);
        var bit = 1L << slotIndex;
        var current = Volatile.Read(ref word);
        if ((current & bit) == 0 && Interlocked.CompareExchange(ref word, current | bit, current) == current)
        {
            return;
        }

        Interlocked.Increment(ref SlotIndexLockWaits);
        SlotIndexLockWaitProbe?.Invoke();
        var waiter = new AdaptiveWaiter();
        while (true)
        {
            current = Volatile.Read(ref word);
            if ((current & bit) == 0 && Interlocked.CompareExchange(ref word, current | bit, current) == current)
            {
                return;
            }

            waiter.Wait();
        }
    }

    /// <summary>Releases <see cref="LockSlotIndexes"/>. A full barrier: the next holder reads every write this one made.</summary>
    internal void UnlockSlotIndexes(int clusterChunkId, int slotIndex) =>
        Interlocked.And(ref BlockWord(clusterChunkId, SlotIndexLockWordIndex), ~(1L << slotIndex));

    /// <summary>
    /// Whether <paramref name="slotIndex"/>'s index lock is held (<see cref="LockSlotIndexes"/>) — a spawn still publishing, among others — by acquire.
    /// </summary>
    internal bool IsSlotIndexLocked(int clusterChunkId, int slotIndex)
    {
        ref var words = ref SlotWords(SlotWordTable, clusterChunkId);
        return !Unsafe.IsNullRef(ref words)
               && (Volatile.Read(ref Unsafe.Add(ref words, SlotIndexLockWordIndex - ClaimWordIndex)) & (1L << slotIndex)) != 0;
    }

    /// <summary>
    /// Waits, unbounded, until <paramref name="slotIndex"/>'s index lock is free, without taking it: before a commit takes the entity's chain lock, and for
    /// an in-place write whose keys the fence moves, which must not record keys a spawn still publishing has not indexed yet. One acquire load when free.
    /// </summary>
    internal void WaitSlotIndexesUnlocked(int clusterChunkId, int slotIndex)
    {
        if (!IsSlotIndexLocked(clusterChunkId, slotIndex))
        {
            return;
        }

        Interlocked.Increment(ref SlotIndexLockWaits);
        SlotIndexLockWaitProbe?.Invoke();
        var waiter = new AdaptiveWaiter();
        while (IsSlotIndexLocked(clusterChunkId, slotIndex))
        {
            waiter.Wait();
        }
    }

    /// <summary><see cref="WaitSlotIndexesUnlocked(int, int)"/>, bounded by <paramref name="wc"/>: false when it ran out first.</summary>
    internal bool WaitSlotIndexesUnlocked(int clusterChunkId, int slotIndex, ref WaitContext wc)
    {
        if (!IsSlotIndexLocked(clusterChunkId, slotIndex))
        {
            return true;
        }

        Interlocked.Increment(ref SlotIndexLockWaits);
        SlotIndexLockWaitProbe?.Invoke();
        var waiter = new AdaptiveWaiter();
        while (IsSlotIndexLocked(clusterChunkId, slotIndex))
        {
            if (!waiter.Wait(ref wc))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the slot at <paramref name="clusterBase"/> still holds <paramref name="entityId"/> alive: occupied, and its entity id the entity's. A destroy
    /// clears the occupancy bit as it removes the entity's entries (hides or releases the slot), and a released slot can hold another entity since. Read under
    /// the slot's index lock, it is stable.
    /// </summary>
    internal bool HoldsLive(byte* clusterBase, int slotIndex, EntityId entityId) =>
        (Volatile.Read(ref *(ulong*)clusterBase) & (1UL << slotIndex)) != 0
        && *(long*)(clusterBase + Layout.EntityIdsOffset + slotIndex * 8) == (long)entityId.RawValue;

    /// <summary>The waits <see cref="LockSlotIndexes"/> and <see cref="WaitSlotIndexesUnlocked(int, int)"/> made: a slot's index lock found held.</summary>
    internal long SlotIndexLockWaits;

    /// <summary>Test seam: fires when a slot's index lock is found held, before the wait. Null in production.</summary>
    internal Action SlotIndexLockWaitProbe;

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
    /// cluster left with nothing occupied or claimed recorded for the fence's reclamation, as a drained one is.
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
    /// again — and a cluster left with nothing occupied or claimed recorded for the fence's reclamation.
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

    /// <summary>The vacated slot's claim released, and a cluster left with nothing occupied or claimed recorded for the fence's reclamation.</summary>
    /// <remarks>
    /// The callers are an abort (<see cref="UnreserveSlot"/>) and the cleanup of a hidden destroy (<see cref="ReleaseHiddenSlot"/>), which run on any thread —
    /// the cleanup when a transaction is disposed, mid-dispatch included — so the cluster is never finalized here (#582, CLUSTERWALK-03). A claimer that read
    /// it as the free head before the drain can still CAS into it; the reclamation's seal then keeps it (#1315).
    /// </remarks>
    private void FinishVacatedSlot(byte* clusterBase, int clusterChunkId, int slotIndex, SpatialGrid grid)
    {
        var claimsLeft = ReleaseClaim(clusterChunkId, slotIndex);
        var drained = claimsLeft == 0 && (Volatile.Read(ref *(ulong*)clusterBase) & Layout.FullMask) == 0;
        if (drained)
        {
            RecordClusterDrain(clusterChunkId);
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
