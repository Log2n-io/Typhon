using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The pending-slot reservation (VIS-08, plan 06 step 5.2): a spawn is placed in PREPARE, before its commit point, so its slot is taken from then on — and
/// its occupancy bit stays clear until PUBLISH. The reservation word that holds it lives in memory only, beside the cluster: a page never carries it, so a
/// fuzzy checkpoint can persist a pending slot's bytes but never its occupancy bit, and a crash rebuild from occupancy resurrects no uncommitted spawn (CM-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>One claim protocol.</b> Every claimer still CASes the occupancy word, as before; a spawn then MOVES its bit into the reservation word: the reservation
/// set first, the occupancy bit cleared second. A slot is therefore taken in <c>occupancy | reserved</c> at every instant, and a claimer that reads the
/// occupancy word, then the reservation word, sees it taken: either its occupancy read precedes the clear, or its reservation read follows the set. The move
/// puts the occupancy word back to a value it had — an ABA a claimer that read before the claim and CASes after the clear would win — so every claim
/// re-reads the reservation after its CAS and backs off (<see cref="LostToAReservation"/>). PUBLISH moves the bit back the same way — occupancy set first,
/// reservation cleared second.
/// </para>
/// <para>
/// <b>Blocks never move.</b> The table is an array of fixed blocks, grown under a lock, and an element's address is stable for the archetype's life: an
/// <see cref="Interlocked"/> on a reservation word can never land in an array a concurrent grow is copying away from (the hazard
/// <c>ClusterAabbs</c> needs a stamp for).
/// </para>
/// </remarks>
internal sealed unsafe partial class ArchetypeClusterState
{
    private const int ReservationBlockShift = 10;
    private const int ReservationBlockSize = 1 << ReservationBlockShift;
    private long[][] _reservedBlocks = [];
    private readonly Lock _reservationGrowLock = new();

    /// <summary>The reserved (pending-spawn) slots of <paramref name="clusterChunkId"/>; 0 for a cluster nothing ever reserved in.</summary>
    internal ulong ReservedBits(int clusterChunkId)
    {
        var blocks = Volatile.Read(ref _reservedBlocks);
        var b = clusterChunkId >> ReservationBlockShift;
        if ((uint)b >= (uint)blocks.Length)
        {
            return 0;
        }

        var block = Volatile.Read(ref blocks[b]);
        return block == null ? 0 : (ulong)Volatile.Read(ref block[clusterChunkId & (ReservationBlockSize - 1)]);
    }

    private ref long ReservationWord(int clusterChunkId)
    {
        var b = clusterChunkId >> ReservationBlockShift;
        var blocks = Volatile.Read(ref _reservedBlocks);
        if ((uint)b >= (uint)blocks.Length || Volatile.Read(ref blocks[b]) == null)
        {
            lock (_reservationGrowLock)
            {
                blocks = _reservedBlocks;
                if ((uint)b >= (uint)blocks.Length)
                {
                    var grown = new long[b + 1][];
                    blocks.CopyTo(grown, 0);
                    blocks = grown;
                }

                blocks[b] ??= new long[ReservationBlockSize];
                Volatile.Write(ref _reservedBlocks, blocks);
            }
        }

        return ref blocks[b][clusterChunkId & (ReservationBlockSize - 1)];
    }

    /// <summary>
    /// Turns a slot the caller has just claimed into a reservation: taken, and not occupied. The reservation is set before the occupancy bit is cleared.
    /// </summary>
    internal void ReserveClaimedSlot(byte* clusterBase, int clusterChunkId, int slotIndex)
    {
        var bit = 1L << slotIndex;
        Interlocked.Or(ref ReservationWord(clusterChunkId), bit);
        Interlocked.And(ref *(long*)clusterBase, ~bit);
    }

    /// <summary>
    /// PUBLISH of a reserved slot: the born watermark folds the commit stamp first (BIND-04), then the occupancy bit is set, then the reservation cleared.
    /// The caller has written the EntityMap record's stamp before.
    /// </summary>
    internal void PublishReservedSlot(byte* clusterBase, int clusterChunkId, int slotIndex, long stamp)
    {
        var bit = 1L << slotIndex;
        NoteClusterBorn(clusterChunkId, stamp);
        Interlocked.Or(ref *(long*)clusterBase, bit);
        Interlocked.And(ref ReservationWord(clusterChunkId), ~bit);
    }

    /// <summary>
    /// An aborted spawn gives its reserved slot back (AP-07): the slot's entity id and enabled bits cleared, the reservation dropped, the cell's count
    /// decremented, and a cluster left with nothing occupied or reserved finalised and freed as a drained one is.
    /// </summary>
    internal void UnreserveSlot(byte* clusterBase, int clusterChunkId, int slotIndex, SpatialGrid grid)
    {
        ClearSlotMetadata(clusterBase, slotIndex);
        var bit = 1L << slotIndex;
        var prevReserved = (ulong)Interlocked.And(ref ReservationWord(clusterChunkId), ~bit);
        DecrementCellEntityCountOnRelease(grid, clusterChunkId, true);

        var drained = (prevReserved & ~(ulong)bit) == 0 && (Volatile.Read(ref *(ulong*)clusterBase) & Layout.FullMask) == 0;
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

    /// <summary>
    /// The occupancy CAS a claim just won can be an ABA: a spawn's move puts the word back to the value the claimer read — claim CAS, reservation set,
    /// occupancy bit cleared — with the slot now reserved. Re-read after the CAS (whose full fence orders it), the reservation tells; the claim then gives
    /// the bit back and tries again. The mover sets the reservation before it clears the bit, so a claim whose CAS lands after the clear sees it.
    /// </summary>
    private bool LostToAReservation(ref ulong occupancy, int clusterChunkId, int slotIndex)
    {
        var bit = 1UL << slotIndex;
        if ((ReservedBits(clusterChunkId) & bit) == 0)
        {
            return false;
        }

        Interlocked.And(ref Unsafe.As<ulong, long>(ref occupancy), ~(long)bit);
        return true;
    }

    /// <summary>Taken slots — occupied or reserved. The occupancy word is read first, then the reservation word (see the type's remarks).</summary>
    internal ulong TakenBits(ulong occupancy, int clusterChunkId) => occupancy | ReservedBits(clusterChunkId);
}
