using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// One bit per cluster chunk id, marking the clusters a single query has already scanned.
/// </summary>
/// <remarks>
/// <para><b>This exists to dedup the CAUSE rather than the symptom.</b> The ray and frustum walks used to guard against reporting an entity twice by
/// re-scanning everything already written to the result buffer — an <c>O(N²)</c> comparison over the whole result set, per entity, which is invisible at the
/// handful of hits a picking ray returns and quadratic at the thousands a frustum returns. Wiring those queries into <c>EcsQuery</c> makes the large case
/// reachable from user code, so the guard had to move.</para>
/// <para><b>The invariant it actually enforces.</b> An entity lives in exactly one cluster slot, so a duplicated entity id can only come from a cluster being
/// scanned twice — through both halves of one cell (which <c>C13</c> forbids) or through two cells (which the grid forbids). Marking the cluster is therefore
/// strictly stronger than comparing entity ids, costs one bit test per cluster instead of one pass per entity, and states the invariant it is defending.</para>
/// <para><b>Sizing and lifetime.</b> The bitmap is rented for the duration of one query and covers <c>[0, clusterCapacity)</c>; a chunk id outside that range
/// is the caller's bounds check to make, not this type's. Rented arrays come back dirty, so the words in use are cleared on rent — <c>capacity/64</c> words,
/// which at 100 K clusters is 1 563 words or <b>12.5 KB</b> of zeroing, and disappears against a query that then touches those clusters' pages. (The figure
/// read "~1.5 KB" until review checked it; a number quoted as a justification has to be right or it is not one.)</para>
/// <para><b>Deliberately not a <c>ref struct</c>.</b> It holds the rented array and a length rather than a <see cref="Span{T}"/> over it, so it can be passed
/// <c>ref</c> into methods that also hold <c>stackalloc</c> spans without tripping ref-escape analysis (CS9080). The array reference is the only state that
/// matters; every visit mutates through it, so a by-value copy would work too.</para>
/// </remarks>
internal struct ClusterVisitSet
{
    private readonly ulong[] _rented;
    private readonly int _wordCount;

    private ClusterVisitSet(ulong[] rented, int wordCount)
    {
        _rented = rented;
        _wordCount = wordCount;
    }

    /// <summary>Rent a set covering cluster chunk ids <c>[0, clusterCapacity)</c>.</summary>
    public static ClusterVisitSet Rent(int clusterCapacity)
    {
        var wordCount = (clusterCapacity + 63) >> 6;
        if (wordCount <= 0)
        {
            return new ClusterVisitSet(null, 0);
        }

        var rented = ArrayPool<ulong>.Shared.Rent(wordCount);
        Array.Clear(rented, 0, wordCount);
        return new ClusterVisitSet(rented, wordCount);
    }

    /// <summary>
    /// Marks <paramref name="clusterChunkId"/> as visited and reports whether this call was the first to do so.
    /// </summary>
    /// <returns><c>true</c> the first time an id is passed, <c>false</c> on every later call for the same id.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryVisit(int clusterChunkId)
    {
        var word = clusterChunkId >> 6;
        if ((uint)word >= (uint)_wordCount)
        {
            // Out of the set's range — report it as unvisited so the caller's own bounds check stays the authority on what is scannable.
            return true;
        }

        var bit = 1UL << (clusterChunkId & 63);
        ref var slot = ref _rented[word];
        if ((slot & bit) != 0UL)
        {
            return false;
        }

        slot |= bit;
        return true;
    }

    public void Dispose()
    {
        if (_rented != null)
        {
            ArrayPool<ulong>.Shared.Return(_rented);
        }
    }
}
