using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-slot change stamps (#1247), two a slot: the VALUE stamp, the highest commit stamp of a change a reader older than it must not read from the HEAD — a
/// destroy (VIS-09), an update that copied the entity's HEAD bytes (VIS-12); and the KEY stamp, that of a change it must not read from the index trees — a
/// destroy, which takes the entity's entries out, or an update that moved them. The cluster watermarks say WHETHER a cluster holds such a change; these
/// say WHICH slots, so a reader older than a change reads only those entities at its snapshot. Most updates move no key: an index reader older than them
/// takes their rows from the trees at their own keys, and reads their values through their chains.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written before the cluster watermark, read after it.</b> A writer folds the slot's stamp, by CAS max, before it folds the cluster's died or update
/// watermark (<see cref="NoteDestroyPublished"/>, <see cref="NoteUpdatePublished"/>); a reader that read the watermark by acquire finds every slot stamp at or
/// below it. A reader that finds a change by its effect on the HEAD bytes or the trees reads the stamps after that effect, as it reads the watermark.
/// </para>
/// <para>
/// <b>Never cleared.</b> A stamp only rises, so a reader needs no protocol against a clearer: a stale stamp — a slot released and reused since, its old
/// entity's change still recorded — reads as a change, and the reader evaluates that slot's entity at its snapshot, which answers what the HEAD would have.
/// Conservative upward, as the watermarks are. In memory only: a reopen starts every reader above every change.
/// </para>
/// <para>
/// Blocks of 64 clusters × 64 slots × 2 stamps (64 KB), created on the first change in their range under <c>_claimGrowLock</c> — a leaf: the writers hold
/// chain and slot locks when they get here — and never moved: the value stamps first, the key stamps after.
/// </para>
/// </remarks>
internal sealed unsafe partial class ArchetypeClusterState
{
    private const int StampBlockShift = 6;
    private const int StampBlockClusters = 1 << StampBlockShift;
    private const int KeyStampOffset = StampBlockClusters * 64;
    private long[][] _changeStampBlocks = [];

    /// <summary>
    /// Records a change to <paramref name="slotIndex"/>'s entity: its value stamp raised to <paramref name="valueStamp"/> and its key stamp to
    /// <paramref name="keyStamp"/>, each a CAS max, BEFORE the caller folds the cluster's watermark. A non-positive stamp — none, or a carried slot that never
    /// changed — records nothing.
    /// </summary>
    internal void NoteSlotChanged(int clusterChunkId, int slotIndex, long valueStamp, long keyStamp)
    {
        if (valueStamp <= 0 && keyStamp <= 0)
        {
            return;
        }

        ref var value = ref StampWord(clusterChunkId, slotIndex);
        if (valueStamp > 0)
        {
            RaiseStamp(ref value, valueStamp);
        }

        if (keyStamp > 0)
        {
            RaiseStamp(ref Unsafe.Add(ref value, KeyStampOffset), keyStamp);
        }
    }

    /// <summary>The value stamp recorded for <paramref name="slotIndex"/>, by acquire; 0 for none.</summary>
    internal long SlotChangeStamp(int clusterChunkId, int slotIndex) => SlotStamp(clusterChunkId, slotIndex, 0);

    /// <summary>The key stamp recorded for <paramref name="slotIndex"/>, by acquire; 0 for none.</summary>
    internal long SlotKeyStamp(int clusterChunkId, int slotIndex) => SlotStamp(clusterChunkId, slotIndex, KeyStampOffset);

    /// <summary>
    /// The slots of <paramref name="clusterChunkId"/> whose value stamp is above <paramref name="txTsn"/>: their HEAD bytes are newer than the snapshot. The
    /// caller has read the cluster's watermark (or the effect it guards) by acquire before, so the stamps — written before it — are read plainly after; on
    /// arm64 the acquire keeps them after it.
    /// </summary>
    internal ulong ChangedSlotsAfter(int clusterChunkId, long txTsn) => SlotsAbove(clusterChunkId, txTsn, 0);

    /// <summary>
    /// The slots of <paramref name="clusterChunkId"/> whose key stamp is above <paramref name="txTsn"/>: their index entries are gone from the trees or at
    /// newer keys. Read as <see cref="ChangedSlotsAfter"/> is.
    /// </summary>
    internal ulong KeyChangedSlotsAfter(int clusterChunkId, long txTsn) => SlotsAbove(clusterChunkId, txTsn, KeyStampOffset);

    /// <summary>A CAS max: <paramref name="word"/> raised to <paramref name="stamp"/>, never lowered.</summary>
    internal static void RaiseStamp(ref long word, long stamp)
    {
        while (true)
        {
            var current = Volatile.Read(ref word);
            if (stamp <= current || Interlocked.CompareExchange(ref word, stamp, current) == current)
            {
                return;
            }
        }
    }

    private long SlotStamp(int clusterChunkId, int slotIndex, int offset)
    {
        var block = StampBlock(clusterChunkId);
        return block == null ? 0 : Volatile.Read(ref block[offset + ((clusterChunkId & (StampBlockClusters - 1)) << 6) + slotIndex]);
    }

    private ulong SlotsAbove(int clusterChunkId, long txTsn, int offset)
    {
        var block = StampBlock(clusterChunkId);
        if (block == null)
        {
            return 0;
        }

        ref var stamps = ref block[offset + ((clusterChunkId & (StampBlockClusters - 1)) << 6)];
        var mask = 0UL;
        if (Vector256.IsHardwareAccelerated)
        {
            var tsn = Vector256.Create(txTsn);
            for (var i = 0; i < 64; i += 4)
            {
                var above = Vector256.GreaterThan(Vector256.LoadUnsafe(ref stamps, (nuint)i), tsn);
                mask |= (ulong)above.ExtractMostSignificantBits() << i;
            }

            return mask;
        }

        for (var i = 0; i < 64; i++)
        {
            if (Unsafe.Add(ref stamps, i) > txTsn)
            {
                mask |= 1UL << i;
            }
        }

        return mask;
    }

    private long[] StampBlock(int clusterChunkId)
    {
        var b = clusterChunkId >> StampBlockShift;
        var blocks = Volatile.Read(ref _changeStampBlocks);
        return (uint)b < (uint)blocks.Length ? Volatile.Read(ref blocks[b]) : null;
    }

    private ref long StampWord(int clusterChunkId, int slotIndex)
    {
        var block = StampBlock(clusterChunkId);
        if (block == null)
        {
            var b = clusterChunkId >> StampBlockShift;
            lock (_claimGrowLock)
            {
                var blocks = _changeStampBlocks;
                if ((uint)b >= (uint)blocks.Length)
                {
                    var grown = new long[b + 1][];
                    blocks.CopyTo(grown, 0);
                    blocks = grown;
                }

                block = blocks[b];
                if (block == null)
                {
                    block = new long[StampBlockClusters * 64 * 2];
                    Volatile.Write(ref blocks[b], block);
                }

                Volatile.Write(ref _changeStampBlocks, blocks);
            }
        }

        return ref block[((clusterChunkId & (StampBlockClusters - 1)) << 6) + slotIndex];
    }
}
