using System;
using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Bridges <see cref="SystemDefinition"/> (engine) to <see cref="SystemDefinitionRecord"/> (wire format) — single source of truth so every host
/// (AntHill, IOProfileRunner, future production embeddings) emits identical RFC 07 access declarations without copy-pasting projection code.
/// </summary>
/// <remarks>
/// Component type names are taken from <see cref="Type.FullName"/> so the Workbench can correlate them against <see cref="ComponentTypeRecord.Name"/> entries
/// (which use the same convention). Event-queue and resource names are passed through verbatim from <see cref="SystemAccessDescriptor"/>.
/// </remarks>
internal static class SystemDefinitionRecordBuilder
{
    /// <summary>
    /// Builds the wire records for an entire DAG. Predecessors are derived from each system's <see cref="SystemDefinition.Successors"/>
    /// — the SOA the engine maintains internally — so callers don't need a separate predecessor map.
    /// </summary>
    public static SystemDefinitionRecord[] BuildAll(SystemDefinition[] systems)
    {
        ArgumentNullException.ThrowIfNull(systems);
        if (systems.Length == 0)
        {
            return [];
        }

        // Invert successors → predecessors. O(systems + edges); cheap at session-start scale.
        var predecessors = new List<ushort>[systems.Length];
        for (var i = 0; i < systems.Length; i++)
        {
            predecessors[i] = [];
        }
        for (var i = 0; i < systems.Length; i++)
        {
            foreach (var succ in systems[i].Successors)
            {
                predecessors[succ].Add((ushort)i);
            }
        }

        var records = new SystemDefinitionRecord[systems.Length];
        for (var i = 0; i < systems.Length; i++)
        {
            records[i] = Build(systems[i], predecessors[i].ToArray());
        }
        return records;
    }

    /// <summary>
    /// Builds a single wire record. Lower-level escape hatch — most callers should use <see cref="BuildAll"/>.
    /// </summary>
    public static SystemDefinitionRecord Build(SystemDefinition sys, ushort[] predecessors)
    {
        ArgumentNullException.ThrowIfNull(sys);

        var succUshort = new ushort[sys.Successors.Length];
        for (var s = 0; s < sys.Successors.Length; s++)
        {
            succUshort[s] = (ushort)sys.Successors[s];
        }

        var access = sys.Access;

        return new SystemDefinitionRecord
        {
            Index = (ushort)sys.Index,
            Name = sys.Name,
            Type = (byte)sys.Type,
            Priority = (byte)sys.Priority,
            IsParallel = sys.IsParallelQuery,
            TierFilter = (byte)sys.TierFilter,
            Predecessors = predecessors ?? [],
            Successors = succUshort,
            PhaseName = sys.Phase.Name ?? string.Empty,
            IsExclusivePhase = access.ExclusivePhase,
            Reads = TypeNames(access.Reads),
            ReadsFresh = TypeNames(access.ReadsFresh),
            ReadsSnapshot = TypeNames(access.ReadsSnapshot),
            AdditionalReads = TypeNames(access.AdditionalReads),
            Writes = TypeNames(access.Writes),
            SideWrites = TypeNames(access.SideWrites),
            WritesEvents = QueueNames(access.WritesEvents),
            ReadsEvents = QueueNames(access.ReadsEvents),
            WritesResources = ToArray(access.WritesResources),
            ReadsResources = ToArray(access.ReadsResources),
            // RFC 07 §Q5 explicit edges aren't separately tracked on Access today (they fold into Successors at Build time);
            // surface as empty until the descriptor exposes them. Workbench treats empty arrays as "no explicit hint".
            ExplicitAfter = [],
            ExplicitBefore = [],
            DagId = (ushort)sys.DagId,
        };
    }

    private static string[] TypeNames(HashSet<Type> set)
    {
        if (set.Count == 0)
        {
            return [];
        }
        var arr = new string[set.Count];
        var i = 0;
        foreach (var t in set)
        {
            arr[i++] = t.FullName ?? t.Name;
        }
        return arr;
    }

    private static string[] QueueNames(HashSet<EventQueueBase> set)
    {
        if (set.Count == 0)
        {
            return [];
        }
        var arr = new string[set.Count];
        var i = 0;
        foreach (var q in set)
        {
            arr[i++] = q.Name;
        }
        return arr;
    }

    private static string[] ToArray(HashSet<string> set)
    {
        if (set.Count == 0)
        {
            return [];
        }
        var arr = new string[set.Count];
        set.CopyTo(arr);
        return arr;
    }
}
