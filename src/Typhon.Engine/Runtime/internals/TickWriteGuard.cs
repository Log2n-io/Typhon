using System;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Which thread is inside the runtime's DAG, and which run it is executing (#1651). Entered at every system dispatch (beside
/// <see cref="SystemAccessValidator.EnterSystem(SystemAccessDescriptor, string, long)"/>, ungated) with one id per system run shared by all its chunks,
/// and around the runtime's serial points that run application code (OnFirstTick, OnShutdown, the in-tick queue). Always on: one thread-static store per
/// system run, not per access.
/// </summary>
internal static class DagThread
{
    [ThreadStatic]
    private static int _depth;

    [ThreadStatic]
    private static long[] _runs;

    [ThreadStatic]
    private static long _nextRun;

    /// <summary>True when this thread is executing a system or a runtime serial point.</summary>
    public static bool InDag
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _depth > 0;
    }

    /// <summary>The id of the innermost run on this thread, or 0 outside the DAG.</summary>
    public static long CurrentRun => _depth > 0 ? _runs[_depth - 1] : 0;

    /// <summary>
    /// The run id of system <paramref name="systemIndex"/> on tick <paramref name="tick"/>: the same for every chunk of one system run, whatever worker runs
    /// it, so a transaction opened in one chunk is live in the next chunk on that thread and dead once the run is over. Always positive.
    /// </summary>
    public static long SystemRun(long tick, int systemIndex) => ((tick & 0x3FFF_FFFF_FFFFL) << 16) | ((uint)systemIndex & 0xFFFF) | 1L << 62;

    /// <summary>Enter run <paramref name="runId"/> (a system run, see <see cref="SystemRun"/>).</summary>
    public static void Enter(long runId)
    {
        var runs = _runs ??= new long[8];
        if (_depth == runs.Length)
        {
            Array.Resize(ref _runs, runs.Length * 2);
            runs = _runs;
        }
        runs[_depth++] = runId;
    }

    /// <summary>Enter a serial point: a run id no system run can carry (negative), unique on this thread.</summary>
    public static void Enter() => Enter(long.MinValue | ((long)(uint)Environment.CurrentManagedThreadId << 32) | (uint)++_nextRun);

    /// <summary>Leave the innermost run.</summary>
    public static void Leave()
    {
        if (_depth > 0)
        {
            _runs[--_depth] = 0;
        }
    }

    /// <summary>True while <paramref name="run"/> is still executing on this thread.</summary>
    public static bool IsLive(long run)
    {
        if (run == 0)
        {
            return false;
        }
        var runs = _runs;
        for (var i = _depth - 1; i >= 0; i--)
        {
            if (runs[i] == run)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A runtime serial point (<c>using var _ = DagThread.Serial();</c>).</summary>
    public static Scope Serial()
    {
        Enter();
        return default;
    }

    public readonly ref struct Scope
    {
        public void Dispose() => Leave();
    }
}

/// <summary>
/// The off-tick write guard (#1651, rules EW-01 / EW-02): refuses, deterministically and at the call, the writes a thread outside the DAG must never make
/// while a runtime ticks. Decided by context, not timing: a transaction is stamped when it is created (<see cref="EntityAccessor.WriteGuard"/>) —
/// <see cref="Unbound"/> outside the DAG, <see cref="Bound"/> to the run that created it inside — and each write entry asks this class only while a
/// runtime ticks the engine and the stamp is set (<see cref="EntityAccessor.GuardActive"/>).
/// </summary>
internal static class TickWriteGuard
{
    /// <summary>Runtime-owned: a system's own transaction, a serial-point or in-tick transaction, the runtime's point-in-time workers.</summary>
    public const byte None = 0;

    /// <summary>Created on a thread running no system (EW-02): while a runtime ticks, refused every write that races the fence.</summary>
    public const byte Unbound = 1;

    /// <summary>Created inside a system run or a serial point (EW-01), side transactions included: legal while that run lasts, unbound after.</summary>
    public const byte Bound = 2;

    /// <summary>Stamp <paramref name="accessor"/>, being created on this thread now.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Stamp(EntityAccessor accessor)
    {
        var run = DagThread.CurrentRun;
        accessor.WriteGuard = run != 0 ? Bound : Unbound;
        accessor.SideRun = run;
    }

    /// <summary>A component write to <paramref name="slot"/> of <paramref name="meta"/> through <paramref name="via"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CheckSlotWrite(EntityAccessor via, ArchetypeMetadata meta, ArchetypeEngineState state, int slot)
    {
        if (!Applies(via))
        {
            return;
        }
        var refusal = Refusal(meta, state);
        if (refusal != null)
        {
            throw Refused(via, $"wrote a component of '{meta.Name}', {refusal}");
        }
        var table = state?.SlotToComponentTable[slot];
        if (table != null && table.StorageMode != StorageMode.Versioned)
        {
            throw Refused(via, $"wrote '{NameOf(table)}', a {table.StorageMode} component of '{meta.Name}'");
        }
    }

    /// <summary>
    /// A spawn, a destroy, or an enable / disable through <paramref name="via"/>: refused for a spatial or realm-keyed archetype, whose entities the fence
    /// moves between clusters. Any other archetype's is legal off-tick (EW-02): its commit claims and releases cluster slots, which the fence also writes,
    /// and <see cref="ExclusiveWindow.EnterStructuralWriter"/> keeps that commit out of the fence window.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CheckStructural(EntityAccessor via, ArchetypeMetadata meta, string verb)
    {
        if (meta == null || !Applies(via))
        {
            return;
        }
        var refusal = Refusal(meta, via.Engine?._archetypeStates[meta.ArchetypeId]);
        if (refusal != null)
        {
            throw Refused(via, $"{verb} an entity of '{meta.Name}', {refusal}");
        }
    }

    /// <summary>A bound transaction's commit: refused once the run that created it has ended (EW-01).</summary>
    public static void CheckBoundCommit(Transaction tx)
    {
        if (tx.WriteGuard == Bound && tx.Engine.RuntimeTicking && !DagThread.IsLive(tx.SideRun))
        {
            throw new InvalidOperationException(
                "A transaction opened inside a system was committed after that system returned (rule EW-01): it can land while the tick fence rewrites " +
                "the structures it writes. Commit and dispose a transaction inside the system that opened it.");
        }
    }

    /// <summary>Why an archetype's components may not be written off-tick at all, or null: the fence moves its entities between clusters.</summary>
    private static string Refusal(ArchetypeMetadata meta, ArchetypeEngineState state)
    {
        if (meta.HasClusterSpatial)
        {
            return "an archetype with a [SpatialIndex] field";
        }
        var tables = state?.SlotToComponentTable;
        if (tables != null)
        {
            foreach (var table in tables)
            {
                if (table?.SpatialIndex != null)
                {
                    return "an archetype with a [SpatialIndex] field";
                }
            }
        }
        return meta.RealmKeySlotMask != 0 ? "a realm-keyed archetype, whose entities the fence migrates" : null;
    }

    private static string NameOf(ComponentTable table) => table.Definition?.POCOType?.Name ?? table.Definition?.Name;

    /// <summary>The guard applies: a ticking runtime, and an unbound transaction or one whose run has ended.</summary>
    private static bool Applies(EntityAccessor via) =>
        via.Engine.RuntimeTicking && (via.WriteGuard == Unbound || (via.WriteGuard == Bound && !DagThread.IsLive(via.SideRun)));

    private static InvalidOperationException Refused(EntityAccessor via, string what)
    {
        var bound = via.WriteGuard == Bound;
        return new InvalidOperationException(bound
            ? $"A transaction opened inside a system {what} after that system returned (rule EW-01). Commit and dispose a transaction inside the system " +
              "that opened it."
            : $"A transaction opened outside the runtime's systems {what} while the runtime ticks (rule EW-02): such a write races the tick fence and " +
              "corrupts silently. Hand it to the tick with TyphonRuntime.WriteInTick / InTick, or make it from a system. Off the tick, only Versioned " +
              "components of an archetype that is neither spatial nor realm-keyed may be written, and only such an archetype's entities spawned, " +
              "destroyed, enabled or disabled.");
    }
}
