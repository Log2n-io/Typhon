using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// Accumulator for a system's declared read/write access (RFC 07 — System Access Declarations + Auto-DAG). Populated by <see cref="SystemBuilder"/> declaration
/// methods, copied into <see cref="Dag.SystemRegistration"/>, then onto <see cref="SystemDefinition.Access"/> at <see cref="RuntimeSchedule.Build"/> time.
/// </summary>
/// <remarks>
/// Unit 2 of the auto-DAG migration: storage only. Conflict detection (W×W errors, R×W-plain errors, derived edges) lands in Unit 3. All sets default to
/// empty; entries are deduped automatically by the underlying <see cref="HashSet{T}"/>. Public so tooling (Workbench Schema Inspector, Profiler topology
/// surfacing — RFC 07 Unit 6) can project the declarations onto its DTO surface without bouncing through reflection.
/// </remarks>
public sealed class SystemAccessDescriptor
{
    /// <summary>Component types declared with <c>b.Reads&lt;T&gt;()</c>. Ambiguous about same-tick freshness — Unit 3 errors if a same-phase writer of T exists.</summary>
    public readonly HashSet<Type> Reads = [];

    /// <summary>Component types declared with <c>b.ReadsFresh&lt;T&gt;()</c>. Reader is ordered AFTER any same-phase writer of T (Unit 3).</summary>
    public readonly HashSet<Type> ReadsFresh = [];

    /// <summary>Component types declared with <c>b.ReadsSnapshot&lt;T&gt;()</c>. Reader is ordered BEFORE any same-phase writer of T (Unit 3) — sees previous-tick value.</summary>
    public readonly HashSet<Type> ReadsSnapshot = [];

    /// <summary>Component types read beyond the system's primary View input (cross-entity reads). A plain read for scheduling: the same AC-02 error and
    /// derived edges as <see cref="Reads"/> (#1650).</summary>
    public readonly HashSet<Type> AdditionalReads = [];

    /// <summary>Component types this system mutates via <c>EntityRefMut.Write&lt;T&gt;()</c>.</summary>
    public readonly HashSet<Type> Writes = [];

    /// <summary>Component types written through side transactions (<see cref="TickContext.CreateSideTransaction"/>). Surfaced in tooling but does NOT affect
    /// scheduler ordering; DV-01 accepts them through a side transaction only.</summary>
    public readonly HashSet<Type> SideWrites = [];

    /// <summary>Event queues this system publishes to. Producer→consumer edges are derived in Unit 3.</summary>
    public readonly HashSet<EventQueueBase> WritesEvents = [];

    /// <summary>Event queues this system consumes from.</summary>
    public readonly HashSet<EventQueueBase> ReadsEvents = [];

    /// <summary>Named resources this system mutates (e.g., a shared physics-world handle).</summary>
    public readonly HashSet<string> WritesResources = new(StringComparer.Ordinal);

    /// <summary>Named resources this system reads.</summary>
    public readonly HashSet<string> ReadsResources = new(StringComparer.Ordinal);

    /// <summary>When true, this system runs alone in its phase — no other system in the same phase may execute concurrently with it (Unit 3 enforces).</summary>
    public bool ExclusivePhase;

    private CompiledAccess _compiled;

    /// <summary>The DV-01 bitset form of the component sets (#1650), compiled on first use. <see cref="RuntimeSchedule.Build"/> recompiles it, so a
    /// declaration added before the build is always seen.</summary>
    internal CompiledAccess Compiled => Volatile.Read(ref _compiled) ?? Compile();

    /// <summary>Compiles the component sets into <see cref="Compiled"/>.</summary>
    internal CompiledAccess Compile()
    {
        var compiled = new CompiledAccess(this);
        Volatile.Write(ref _compiled, compiled);
        return compiled;
    }

    /// <summary>Returns true if any access has been declared. A system for which this is false is still checked by DV-01: every access it makes is
    /// undeclared.</summary>
    public bool HasAnyDeclaration => Reads.Count > 0 || ReadsFresh.Count > 0 || ReadsSnapshot.Count > 0 || AdditionalReads.Count > 0 || Writes.Count > 0 || 
                                     SideWrites.Count > 0 || WritesEvents.Count > 0 || ReadsEvents.Count > 0 || WritesResources.Count > 0 || 
                                     ReadsResources.Count > 0 || ExclusivePhase;
}
