using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Derives DAG edges from declared system access (RFC 07 — Unit 3). Validates that conflicts within a phase are either explicitly resolved
/// (via <c>.After()</c>/<c>.Before()</c>) or expressed declaratively (<see cref="SystemBuilder.ReadsFresh{T}"/> / <see cref="SystemBuilder.ReadsSnapshot{T}"/>).
/// </summary>
/// <remarks>
/// <para>
/// Inputs: per-system info (name, resolved phase index, access descriptor) + the explicit edges already declared via <c>.After()</c>, <c>.Before()</c>, and
/// <c>.AfterAll()</c>. Output: a list of derived edges to add to the DAG.
/// </para>
/// <para>
/// Conflict detection (hard errors at <c>Build()</c>):
/// <list type="bullet">
///   <item>W×W same phase, no explicit ordering between the writers.</item>
///   <item>R×W plain (<c>b.Reads&lt;T&gt;()</c>) with a same-phase writer of T — must upgrade to <c>ReadsFresh</c> or <c>ReadsSnapshot</c>.</item>
///   <item>Resource W×W same phase, no explicit ordering.</item>
///   <item><c>ExclusivePhase()</c> declared on a system that shares its phase with another system.</item>
/// </list>
/// </para>
/// <para>
/// Edge derivation (intra-phase):
/// <list type="bullet">
///   <item>R×W fresh: writer → reader (reader sees this-tick value).</item>
///   <item>R×W snapshot: reader → writer (reader sees previous-tick value, parallelism enabled).</item>
///   <item>Event producer → consumer in same phase.</item>
///   <item>Resource R×W: writer → reader (resource snapshot semantics not yet expressed).</item>
/// </list>
/// </para>
/// <para>
/// Cross-phase edges (2026-05-07: switched from all-to-all to conflict-driven). Phases remain a
/// logical ordering contract, but a phase-(N+1) system that has no read/write/event/resource
/// conflict with a phase-N system can run concurrently with it. The deriver emits an edge
/// <c>sys_a → sys_b</c> (where <c>phase(sys_a) &lt; phase(sys_b)</c>) iff:
/// <list type="bullet">
///   <item><c>sys_a.Writes&lt;T&gt;</c> intersects <c>sys_b</c>'s reads or writes of T (writer first).</item>
///   <item><c>sys_a</c> reads T (any flavour) and <c>sys_b.Writes&lt;T&gt;</c> (reader first).</item>
///   <item><c>sys_a.WritesEvents(Q)</c> intersects <c>sys_b.ReadsEvents(Q)</c> (producer→consumer).</item>
///   <item>Any resource access pair with at least one writer (no Fresh/Snapshot for resources in v1).</item>
///   <item>Explicit <c>.After()/.Before()</c> declarations spanning phases — preserved verbatim
///       by the explicit-edge merge and never elided here.</item>
/// </list>
/// W×W across phases needs no AC-01 disambiguation: phase order is the disambiguator. The
/// previous "all-to-all" cross-phase chain caused stragglers in phase N to gate the entire pool;
/// see <c>claude/design/Runtime/07-system-access-declarations.md</c> §"Amendment 2026-05-07".
/// </para>
/// <para>
/// Semantic note: cross-phase <see cref="SystemBuilder.ReadsSnapshot{T}"/> in a later phase observes the
/// earlier-phase writer's *this-tick* value (not previous-tick), because phase order forces
/// writer-first. This differs from intra-phase <c>ReadsSnapshot</c> by design — see the same
/// design doc for the rationale.
/// </para>
/// <para>
/// Systems with <c>PhaseIndex == -1</c> (no phase declared) and no access declarations are ignored — they participate in the DAG only via explicit edges.
/// This preserves backwards compatibility during the migration window.
/// </para>
/// </remarks>
internal static class AccessDagDeriver
{
    /// <summary>Lightweight view of a system passed to the deriver. Avoids leaking <c>SystemRegistration</c> outside <see cref="RuntimeSchedule"/>.</summary>
    public readonly struct SystemInfo
    {
        public readonly string Name;
        public readonly int PhaseIndex;
        public readonly SystemAccessDescriptor Access;

        public SystemInfo(string name, int phaseIndex, SystemAccessDescriptor access)
        {
            Name = name;
            PhaseIndex = phaseIndex;
            Access = access;
        }
    }

    /// <summary>
    /// Validates declared access and derives DAG edges. Returns the list of edges to add to the DAG.
    /// Throws <see cref="InvalidOperationException"/> on conflict with a copy-paste-ready suggestion.
    /// </summary>
    public static List<(string From, string To)> DeriveAndValidate(IReadOnlyList<SystemInfo> systems, IReadOnlyList<(string From, string To)> explicitEdges)
    {
        var derived = new List<(string From, string To)>();

        // Build lookup: explicit direct edges (used to check W×W resolution).
        // We use direct adjacency only — transitive reachability is not checked. If transitive ordering is needed, the user should add an explicit edge.
        var explicitAdjacency = new HashSet<(string, string)>();
        foreach (var (from, to) in explicitEdges)
        {
            explicitAdjacency.Add((from, to));
        }

        // Group systems by phase. PhaseIndex == -1 is the "no phase" sentinel — should be unreachable post-Unit-5 (RuntimeSchedule.Build
        // assigns RuntimeOptions.DefaultPhase to undeclared registrations) but kept as a defensive skip for systems built outside the
        // RuntimeSchedule path.
        var byPhase = new Dictionary<int, List<SystemInfo>>();
        foreach (var sys in systems)
        {
            if (sys.PhaseIndex < 0)
            {
                continue;
            }

            if (!byPhase.TryGetValue(sys.PhaseIndex, out var list))
            {
                list = [];
                byPhase[sys.PhaseIndex] = list;
            }

            list.Add(sys);
        }

        // ── Per-phase conflict detection + intra-phase edge derivation ─────
        foreach (var (phaseIdx, phaseSystems) in byPhase)
        {
            DerivePhase(phaseIdx, phaseSystems, explicitAdjacency, derived);
        }

        // ── Cross-phase: conflict-driven edges (replaces former all-to-all chain) ──
        DeriveCrossPhase(byPhase, derived);

        return derived;
    }

    /// <summary>
    /// Walks every (earlier-phase, later-phase) system pair and emits an edge iff the pair has a real read/write/event/resource conflict. Replaces the former
    /// all-to-all bipartite chain between consecutive phases — see the class-level remarks for the motivation.
    ///
    /// We iterate ordered phase indices and, for each ordered pair (p_a, p_b) with a &lt; b, run the conflict matrix between every (sys_a ∈ p_a) × (sys_b ∈ p_b).
    /// Quadratic in the system count of each pair of phases, but called once at <c>Build()</c>; cost is irrelevant against the runtime savings of not
    /// serialising independent phases.
    /// </summary>
    private static void DeriveCrossPhase(Dictionary<int, List<SystemInfo>> byPhase, List<(string From, string To)> derived)
    {
        var phaseIndices = new List<int>(byPhase.Keys);
        phaseIndices.Sort();

        for (var i = 0; i < phaseIndices.Count - 1; i++)
        {
            var earlier = byPhase[phaseIndices[i]];
            for (var j = i + 1; j < phaseIndices.Count; j++)
            {
                var later = byPhase[phaseIndices[j]];

                foreach (var sysA in earlier)
                {
                    foreach (var sysB in later)
                    {
                        if (sysA.Access == null || sysB.Access == null)
                        {
                            // No access declarations on at least one side — nothing to derive.
                            // Phase ordering is the only intent the developer expressed; without a declared dependency we let the systems overlap. (Explicit
                            // .After/.Before edges, if any, are merged separately by RuntimeSchedule.Build and survive verbatim.)
                            continue;
                        }

                        if (HasCrossPhaseConflict(sysA.Access, sysB.Access))
                        {
                            derived.Add((sysA.Name, sysB.Name));
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns true iff <paramref name="a"/> (in an earlier phase) and <paramref name="b"/> (in a later phase) have any access pair that requires serialisation.
    /// The direction of the edge is always earlier-phase → later-phase regardless of which side carries the writer — phase order is the disambiguator
    /// (see ED-05f in <c>claude/rules/runtime-scheduling.md</c>).
    ///
    /// Conflict triggers (any one is sufficient):
    /// <list type="bullet">
    ///   <item><b>ED-05a</b>: a.Writes ∩ (b.Writes ∪ b.Reads ∪ b.ReadsFresh ∪ b.ReadsSnapshot) ≠ ∅</item>
    ///   <item><b>ED-05b</b>: (a.Reads ∪ a.ReadsFresh ∪ a.ReadsSnapshot) ∩ b.Writes ≠ ∅</item>
    ///   <item><b>ED-05c</b>: a.WritesEvents ∩ b.ReadsEvents ≠ ∅</item>
    ///   <item><b>ED-05d</b>: any resource access pair with at least one writer</item>
    /// </list>
    ///
    /// Symmetry note: events are inherently directional (producer→consumer) so we do NOT also flag a.ReadsEvents ∩ b.WritesEvents — that would mean the later
    /// phase produces something the earlier phase already drained, which is fine (the consumer in the earlier phase reads whatever the queue had at the start
    /// of its run; the later producer writes for next tick).
    /// </summary>
    private static bool HasCrossPhaseConflict(SystemAccessDescriptor a, SystemAccessDescriptor b)
    {
        // ED-05a: a.Writes vs b.{Writes,Reads,ReadsFresh,ReadsSnapshot}
        if (a.Writes.Count > 0)
        {
            foreach (var t in a.Writes)
            {
                if (b.Writes.Contains(t) || b.Reads.Contains(t) ||
                    b.ReadsFresh.Contains(t) || b.ReadsSnapshot.Contains(t))
                {
                    return true;
                }
            }
        }

        // ED-05b: a.{Reads,ReadsFresh,ReadsSnapshot} vs b.Writes
        if (b.Writes.Count > 0)
        {
            foreach (var t in b.Writes)
            {
                if (a.Reads.Contains(t) || a.ReadsFresh.Contains(t) || a.ReadsSnapshot.Contains(t))
                {
                    return true;
                }
            }
        }

        // ED-05c: events — producer in earlier phase, consumer in later phase
        if (a.WritesEvents.Count > 0 && b.ReadsEvents.Count > 0)
        {
            foreach (var q in a.WritesEvents)
            {
                if (b.ReadsEvents.Contains(q)) return true;
            }
        }

        // ED-05d: resources — any pair with at least one writer
        if (a.WritesResources.Count > 0)
        {
            foreach (var r in a.WritesResources)
            {
                if (b.WritesResources.Contains(r) || b.ReadsResources.Contains(r))
                {
                    return true;
                }
            }
        }
        if (b.WritesResources.Count > 0 && a.ReadsResources.Count > 0)
        {
            foreach (var r in b.WritesResources)
            {
                if (a.ReadsResources.Contains(r)) return true;
            }
        }

        return false;
    }

    private static void DerivePhase(int phaseIdx, List<SystemInfo> phaseSystems, HashSet<(string, string)> explicitAdjacency, List<(string From, string To)> derived)
    {
        // ── ExclusivePhase enforcement ──
        SystemInfo? exclusive = null;
        foreach (var sys in phaseSystems)
        {
            if (sys.Access != null && sys.Access.ExclusivePhase)
            {
                if (exclusive.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Multiple systems declare ExclusivePhase() in the same phase (index {phaseIdx}): " +
                        $"'{exclusive.Value.Name}' and '{sys.Name}'. Only one system may claim exclusivity per phase.");
                }

                if (phaseSystems.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"System '{sys.Name}' declared ExclusivePhase() but other systems share its phase (index {phaseIdx}). " +
                        "Either move the other systems to a different phase, or remove ExclusivePhase() from this system.");
                }

                exclusive = sys;
            }
        }

        // ── Group access by component / event / resource ──
        var writers = new Dictionary<Type, List<SystemInfo>>();
        var readsPlain = new Dictionary<Type, List<SystemInfo>>();
        var readsFresh = new Dictionary<Type, List<SystemInfo>>();
        var readsSnapshot = new Dictionary<Type, List<SystemInfo>>();
        var eventProducers = new Dictionary<EventQueueBase, List<SystemInfo>>();
        var eventConsumers = new Dictionary<EventQueueBase, List<SystemInfo>>();
        var resourceWriters = new Dictionary<string, List<SystemInfo>>(StringComparer.Ordinal);
        var resourceReaders = new Dictionary<string, List<SystemInfo>>(StringComparer.Ordinal);

        foreach (var sys in phaseSystems)
        {
            var access = sys.Access;
            if (access == null)
            {
                continue;
            }

            foreach (var t in access.Writes)
            {
                Bucket(writers, t, sys);
            }

            foreach (var t in access.Reads)
            {
                Bucket(readsPlain, t, sys);
            }

            foreach (var t in access.ReadsFresh)
            {
                Bucket(readsFresh, t, sys);
            }

            foreach (var t in access.ReadsSnapshot)
            {
                Bucket(readsSnapshot, t, sys);
            }

            foreach (var q in access.WritesEvents)
            {
                Bucket(eventProducers, q, sys);
            }

            foreach (var q in access.ReadsEvents)
            {
                Bucket(eventConsumers, q, sys);
            }

            foreach (var r in access.WritesResources)
            {
                Bucket(resourceWriters, r, sys);
            }

            foreach (var r in access.ReadsResources)
            {
                Bucket(resourceReaders, r, sys);
            }
        }

        // ── W×W: hard error unless EXACTLY one explicit direction is declared ──
        // We require XOR not OR: declaring both `(a→b)` and `(b→a)` produces a cycle, and the user's intent is genuinely
        // ambiguous (the cycle detector would later complain anyway with a less-specific message).
        // Limitation: only direct adjacency is consulted — transitive reachability is not. With 3+ writers of the same component,
        // each pair must be directly ordered. A linear chain `A.Before(B).Before(C)` does not implicitly resolve `(A,C)`.
        foreach (var (compType, writerList) in writers)
        {
            if (writerList.Count <= 1)
            {
                continue;
            }

            for (var i = 0; i < writerList.Count; i++)
            {
                for (var j = i + 1; j < writerList.Count; j++)
                {
                    var a = writerList[i];
                    var b = writerList[j];

                    var hasAB = explicitAdjacency.Contains((a.Name, b.Name));
                    var hasBA = explicitAdjacency.Contains((b.Name, a.Name));

                    if (hasAB && hasBA)
                    {
                        throw new InvalidOperationException(
                            $"Systems '{a.Name}' and '{b.Name}' both declare Writes<{compType.Name}> in phase index {phaseIdx} AND have explicit edges in both directions " +
                            "(would form a cycle). Pick one direction: either `.After(...)` or `.Before(...)`, not both.");
                    }

                    if (!hasAB && !hasBA)
                    {
                        throw new InvalidOperationException(
                            $"Systems '{a.Name}' and '{b.Name}' both declare Writes<{compType.Name}> in phase index {phaseIdx}. " +
                            $"Resolution: add `.After(\"{a.Name}\")` on `{b.Name}`, or `.Before(\"{b.Name}\")` on `{a.Name}`, or move one system to a different phase.");
                    }
                }
            }
        }

        // ── R×W plain: hard error if same-phase writer exists ──
        foreach (var (compType, readers) in readsPlain)
        {
            if (!writers.TryGetValue(compType, out var writerList) || writerList.Count == 0)
            {
                continue;
            }

            var writerName = writerList[0].Name;
            var reader = readers[0];

            throw new InvalidOperationException(
                $"System '{reader.Name}' declares Reads<{compType.Name}> in phase index {phaseIdx}, but system '{writerName}' writes the same component in this phase. " +
                $"Upgrade the read to `ReadsFresh<{compType.Name}>()` (run after writers, see this-tick value) or `ReadsSnapshot<{compType.Name}>()` (run before writers, see previous-tick value).");
        }

        // ── R×W fresh: derive writer → reader edge ──
        foreach (var (compType, freshReaders) in readsFresh)
        {
            if (!writers.TryGetValue(compType, out var writerList))
            {
                continue;
            }

            foreach (var writer in writerList)
            {
                foreach (var reader in freshReaders)
                {
                    if (writer.Name == reader.Name)
                    {
                        continue;
                    }

                    derived.Add((writer.Name, reader.Name));
                }
            }
        }

        // ── R×W snapshot: derive reader → writer edge ──
        foreach (var (compType, snapshotReaders) in readsSnapshot)
        {
            if (!writers.TryGetValue(compType, out var writerList))
            {
                continue;
            }

            foreach (var reader in snapshotReaders)
            {
                foreach (var writer in writerList)
                {
                    if (writer.Name == reader.Name)
                    {
                        continue;
                    }

                    derived.Add((reader.Name, writer.Name));
                }
            }
        }

        // ── Event producer → consumer edges (same phase only — cross-phase handled by phase ordering) ──
        foreach (var (queue, producers) in eventProducers)
        {
            if (!eventConsumers.TryGetValue(queue, out var consumers))
            {
                continue;
            }

            foreach (var producer in producers)
            {
                foreach (var consumer in consumers)
                {
                    if (producer.Name == consumer.Name)
                    {
                        continue;
                    }

                    derived.Add((producer.Name, consumer.Name));
                }
            }
        }

        // ── Resource W×W: hard error unless EXACTLY one explicit direction is declared (same XOR rule as component W×W) ──
        foreach (var (resourceName, writerList) in resourceWriters)
        {
            if (writerList.Count <= 1)
            {
                continue;
            }

            for (var i = 0; i < writerList.Count; i++)
            {
                for (var j = i + 1; j < writerList.Count; j++)
                {
                    var a = writerList[i];
                    var b = writerList[j];

                    var hasAB = explicitAdjacency.Contains((a.Name, b.Name));
                    var hasBA = explicitAdjacency.Contains((b.Name, a.Name));

                    if (hasAB && hasBA)
                    {
                        throw new InvalidOperationException(
                            $"Systems '{a.Name}' and '{b.Name}' both declare WritesResource(\"{resourceName}\") in phase index {phaseIdx} AND have explicit edges in both directions " +
                            "(would form a cycle). Pick one direction: either `.After(...)` or `.Before(...)`, not both.");
                    }

                    if (!hasAB && !hasBA)
                    {
                        throw new InvalidOperationException(
                            $"Systems '{a.Name}' and '{b.Name}' both declare WritesResource(\"{resourceName}\") in phase index {phaseIdx}. " +
                            $"Resolution: add `.After(\"{a.Name}\")` on `{b.Name}`, or `.Before(\"{b.Name}\")` on `{a.Name}`, or move one system to a different phase.");
                    }
                }
            }
        }

        // ── Resource R×W: derive writer → reader edge (no Fresh/Snapshot distinction for resources in v1) ──
        foreach (var (resourceName, readers) in resourceReaders)
        {
            if (!resourceWriters.TryGetValue(resourceName, out var writerList))
            {
                continue;
            }

            foreach (var writer in writerList)
            {
                foreach (var reader in readers)
                {
                    if (writer.Name == reader.Name)
                    {
                        continue;
                    }

                    derived.Add((writer.Name, reader.Name));
                }
            }
        }
    }

    private static void Bucket<TKey>(Dictionary<TKey, List<SystemInfo>> map, TKey key, SystemInfo sys)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(sys);
    }
}
