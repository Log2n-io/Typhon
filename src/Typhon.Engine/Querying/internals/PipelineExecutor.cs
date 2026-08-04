using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// The two helpers that outlived the plan-scanning executor this class used to be.
/// </summary>
/// <remarks>
/// <para>
/// Everything else here — <c>Execute</c> / <c>ExecuteOrdered</c> / <c>Count</c> and the ~950 lines of typed B+Tree scan machinery under them — read the shared
/// per-ComponentTable index home, which no longer exists (#629). Every archetype keeps its indexes on the ARCHETYPE, and the scans that answer queries now live
/// in <c>EcsQuery.ScanAllArchetypes</c> / <c>ExecuteOrderedClustered</c>.
/// </para>
/// <para>
/// Deleted on evidence rather than on reading the guards: the entry points were instrumented and the full suite run, and not one of them was entered. What
/// remains is used only by the navigation and cascade paths, which never depended on that home.
/// </para>
/// </remarks>
internal static class PipelineExecutor
{
    /// <summary>Reads <typeparamref name="T"/> for <paramref name="pk"/> at the transaction's snapshot and evaluates every field predicate against it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool EvaluateFilters<T>(FieldEvaluator[] evaluators, Transaction tx, long pk) where T : unmanaged
    {
        if (!tx.QueryRead<T>(pk, out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the FK field's position among <paramref name="ct"/>'s indexed fields, by matching its offset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the ORDINAL rather than the <c>IndexedFieldInfo</c> because the number indexes the per-ARCHETYPE trees:
    /// <c>clusterState.IndexSlots[s].Fields[o]</c>. <see cref="ComponentTable.IndexedFieldInfos"/> survives as the field METADATA the ordinal is derived from —
    /// offsets, sizes, element-id slots — even though it no longer owns a tree of its own.
    /// </para>
    /// <para>
    /// <b>The two lists are positionally aligned by construction</b>: <c>ComponentTable.BuildIndexedFieldInfo</c> and
    /// <c>ArchetypeClusterState.InitializeIndexes</c> both walk the component definition in field-id order and append on <c>HasIndex</c>, using the same
    /// counter. Nothing enforces that beyond the two loops agreeing, and a divergence would MISROUTE every cluster FK lookup rather than fail — so the
    /// alignment is asserted at the point of use, in <c>FkReverseLookup.ScanClusterArchetype</c>.
    /// </para>
    /// </remarks>
    internal static int FindFKIndexOrdinal(ComponentTable ct, int fkFieldOffset)
    {
        var expectedOffset = ct.ComponentOverhead + fkFieldOffset;

        for (var i = 0; i < ct.IndexedFieldInfos.Length; i++)
        {
            if (ct.IndexedFieldInfos[i].OffsetToField == expectedOffset)
            {
                return i;
            }
        }

        throw new InvalidOperationException("FK field index not found. Ensure the FK field has [Index(AllowMultiple = true)].");
    }
}
