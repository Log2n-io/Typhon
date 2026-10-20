using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// An in-place sort of a span by a STRUCT comparer that allocates nothing: insertion sort for short spans, heapsort above.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>span.Sort(comparer)</c>.</b> <c>MemoryExtensions.Sort&lt;T, TComparer&gt;</c> hands the comparer to the runtime's sort helper as an
/// <see cref="IComparer{T}"/>, which boxes a struct comparer, and the helper then builds a <see cref="Comparison{T}"/> delegate from it: two allocations a call.
/// On the replication track that is a call per session per frame — the group admission sorts each session's met regions, the group fill each key's members —
/// and it is what AC-6's group-path measurement found (77 B/tick, 2026-10-20).
/// </para>
/// <para>
/// Here the comparer is a struct type argument, so the JIT specializes the method for it and inlines <c>Compare</c>: no box, no delegate.
/// Not stable, which neither caller needs: both orders are total over the values they sort, or break ties they do not depend on.
/// </para>
/// </remarks>
internal static class SpanSort
{
    /// <summary>Spans this short are insertion-sorted; longer ones are heapsorted.</summary>
    internal const int InsertionMaxCount = 24;

    /// <summary>Sorts <paramref name="span"/> in place by <paramref name="comparer"/>.</summary>
    /// <param name="span">The values.</param>
    /// <param name="comparer">The order.</param>
    public static void Sort<T, TComparer>(Span<T> span, TComparer comparer) where TComparer : struct, IComparer<T>
    {
        if (span.Length <= InsertionMaxCount)
        {
            InsertionSort(span, comparer);
            return;
        }

        HeapSort(span, comparer);
    }

    private static void InsertionSort<T, TComparer>(Span<T> span, TComparer comparer) where TComparer : struct, IComparer<T>
    {
        for (var i = 1; i < span.Length; i++)
        {
            var value = span[i];
            var j = i - 1;
            while (j >= 0 && comparer.Compare(span[j], value) > 0)
            {
                span[j + 1] = span[j];
                j--;
            }

            span[j + 1] = value;
        }
    }

    private static void HeapSort<T, TComparer>(Span<T> span, TComparer comparer) where TComparer : struct, IComparer<T>
    {
        var n = span.Length;
        for (var i = (n >> 1) - 1; i >= 0; i--)
        {
            SiftDown(span, i, n, comparer);
        }

        for (var end = n - 1; end > 0; end--)
        {
            (span[0], span[end]) = (span[end], span[0]);
            SiftDown(span, 0, end, comparer);
        }
    }

    private static void SiftDown<T, TComparer>(Span<T> span, int root, int count, TComparer comparer) where TComparer : struct, IComparer<T>
    {
        var value = span[root];
        while (true)
        {
            var child = (2 * root) + 1;
            if (child >= count)
            {
                break;
            }

            if (child + 1 < count && comparer.Compare(span[child], span[child + 1]) < 0)
            {
                child++;
            }

            if (comparer.Compare(value, span[child]) >= 0)
            {
                break;
            }

            span[root] = span[child];
            root = child;
        }

        span[root] = value;
    }
}
