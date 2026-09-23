using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// One record of one session's frame, as the frame stage collects it: what it names, where its pre-encoded bytes live, and the key it is ordered by.
/// </summary>
/// <remarks>
/// <para>
/// <b>It names a block slot, not bytes.</b> Every byte an <c>ENTITIES</c> record carries was encoded once by S1 into the entity's replication block — the
/// motion segment and the group bodies in the hot entry, the static position and the <c>onEnter</c> body in the cold one — so a record is a pointer pair
/// and a mask rather than a copy. That is what makes a frame a sequence of <c>memcpy</c>s (02 § 4, 03 § 12.2).
/// </para>
/// <para>
/// <b>Twenty-four bytes, and the first four are the key.</b> The netId is the wire's order, and the sort reads nothing else.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 24)]
internal struct FrameRecord
{
    /// <summary>The entity's network identity, which is the order every sub-list travels in (03 § 5).</summary>
    public uint NetId;

    /// <summary>The entity's replication block. An <see cref="nint"/> so the record can live in either managed or native storage.</summary>
    public nint Block;

    /// <summary>The archetype's plan index, which decides the <c>ENTITIES</c> block this record travels in.</summary>
    public ushort Archetype;

    /// <summary>The slot the entity occupies in that block. A cluster holds at most 64, so a byte is the honest width.</summary>
    public byte Slot;

    /// <summary>For a state record, the groups it carries as the wire's <c>u8</c> mask (W14); zero otherwise.</summary>
    public byte GroupMask;

}

/// <summary>Orders records by netId, which is what the wire's gap encoding requires.</summary>
internal readonly struct FrameRecordNetIdKey : IRadixKey<FrameRecord>
{
    /// <inheritdoc />
    public static ulong Key(in FrameRecord item) => item.NetId;
}

/// <summary>
/// The frame stage's sort: insertion below <see cref="InsertionSortMaxCount"/> records, the engine's <see cref="RadixSort"/> above it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sort exists at all.</b> Records are gathered in spatial order — cell by cell — and the wire's records are gap-encoded against an ascending
/// netId (03 § 5). Nothing upstream can produce that order: a netId is handed out when an entity is first projected, so a cluster's slots carry arbitrary
/// identities, and two clusters interleave.
/// </para>
/// <para>
/// <b>Why the threshold.</b> A radix pass pays a fixed cost for its histogram and prefix sum — ~0.15 µs at 256 buckets — which is more than an insertion
/// sort spends on a few dozen nearly-sorted records. <see cref="RadixSort"/> already adapts its digit width at 1 024 items; this threshold is the second
/// half of the same argument, at the other end. Sixty-four is the design's number (09 § P1-13b), not a measured optimum, and the measurement that would move
/// it is the one Q-M6 asks for.
/// </para>
/// <para>
/// <b>Stable and in place.</b> A sort of an already-sorted list is a no-op — <see cref="RadixSort"/> skips a digit every key shares, so a list that is
/// already ascending in a narrow range costs one pass over the keys and nothing else.
/// </para>
/// </remarks>
internal static class RecordSorter
{
    /// <summary>At or below this many records the sort is an insertion sort; above it, a radix sort.</summary>
    public const int InsertionSortMaxCount = 64;

    /// <summary>Histogram slots <see cref="SortByNetId"/> needs when it reaches the radix path.</summary>
    public const int HistogramSlots = RadixSort.Buckets;

    /// <summary>Sorts <paramref name="records"/> ascending by netId.</summary>
    /// <param name="records">The sub-list. Sorted in place.</param>
    /// <param name="scratch">Ping-pong scratch, at least as long as <paramref name="records"/> on the radix path; unused below the threshold.</param>
    /// <param name="counts">Histogram scratch, at least <see cref="HistogramSlots"/> long; unused below the threshold.</param>
    public static void SortByNetId(Span<FrameRecord> records, Span<FrameRecord> scratch, Span<int> counts)
    {
        if (records.Length <= InsertionSortMaxCount)
        {
            InsertionSortByNetId(records);
            return;
        }

        RadixSort.Sort<FrameRecord, FrameRecordNetIdKey>(records, scratch, counts);
    }

    private static void InsertionSortByNetId(Span<FrameRecord> records)
    {
        for (var i = 1; i < records.Length; i++)
        {
            var item = records[i];
            var j = i - 1;
            while (j >= 0 && records[j].NetId > item.NetId)
            {
                records[j + 1] = records[j];
                j--;
            }

            records[j + 1] = item;
        }
    }

}
