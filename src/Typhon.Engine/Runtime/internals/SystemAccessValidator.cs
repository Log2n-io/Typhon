using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Runtime check of declared system access, rule DV-01 (RFC 07 Unit 4, widened by #1650). The scheduler tags each worker thread with the executing
/// system's <see cref="SystemAccessDescriptor"/> via <see cref="EnterSystem(SystemAccessDescriptor, string, long)"/>; every component read, write, spawn, destroy, event write and event drain
/// then asks this class whether the system declared it, and throws <see cref="InvalidAccessException"/> when it did not. An undeclared access derives no
/// DAG edge (ED-01..05), so it races silently: this is the only place that sees it.
/// </summary>
/// <remarks>
/// <para>
/// Gated by <see cref="CheckConfig.DeclaredAccessActive"/>, a <c>static readonly bool</c>: when off every entry point is JIT dead-code-eliminated and the
/// descriptor stack is never populated. When on, a component check is one thread-static load, one array load and a bit test over
/// <see cref="CompiledAccess"/>.
/// </para>
/// <para>
/// Storage is per-thread (<see cref="ThreadStaticAttribute"/>), so multi-worker dispatch is safe by construction; push/pop lets inline successor execution
/// restore the previous system's descriptor. Code outside any system (no <see cref="EnterSystem(SystemAccessDescriptor, string, long)"/> on this thread) is not checked. A class-form system
/// with no declaration at all is checked like any other: every access it makes is undeclared. A lambda-form system (<see cref="Dag.CallbackSystem"/> and
/// siblings) has no declaration surface and no descriptor, and is not checked: it is ordered by its explicit edges alone.
/// </para>
/// <para>
/// <see cref="SystemBuilder.SideWrites{T}"/> grants reads and writes of <c>T</c> through a side transaction only (<see cref="Transaction.IsSideTransaction"/>):
/// through the system's own transaction the write would escape AC-01.
/// </para>
/// </remarks>
internal static class SystemAccessValidator
{
    [ThreadStatic]
    private static SystemAccessDescriptor Current;

    [ThreadStatic]
    private static CompiledAccess CurrentCompiled;

    [ThreadStatic]
    private static string CurrentSystemName;

    [ThreadStatic]
    private static Stack<(SystemAccessDescriptor Prev, CompiledAccess PrevCompiled, string PrevName)> Frames;

    /// <summary>
    /// Push the given descriptor as the current thread's active system context. No-op unless <see cref="CheckConfig.DeclaredAccessActive"/>. Each call must
    /// be paired with a matching <see cref="LeaveSystem"/> in a finally block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnterSystem(SystemAccessDescriptor descriptor, string systemName, long runId)
    {
        DagThread.Enter(runId);   // always on (#1651): the off-tick guard's "this thread runs this system run"
        if (CheckConfig.DeclaredAccessActive)
        {
            EnterSystemCore(descriptor, systemName);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnterSystemCore(SystemAccessDescriptor descriptor, string systemName)
    {
        var stack = Frames ??= new Stack<(SystemAccessDescriptor, CompiledAccess, string)>(8);
        stack.Push((Current, CurrentCompiled, CurrentSystemName));
        Current = descriptor;
        CurrentCompiled = descriptor?.Compiled;   // null: a lambda-form system, which has no declarations to check against
        CurrentSystemName = systemName;
    }

    /// <summary><see cref="EnterSystem(SystemAccessDescriptor, string, long)"/> outside any scheduler (direct tests): run 0 of system 0.</summary>
    public static void EnterSystem(SystemAccessDescriptor descriptor, string systemName) =>
        EnterSystem(descriptor, systemName, DagThread.SystemRun(0, 0));

    /// <summary>Restore the previous descriptor + name. No-op unless <see cref="CheckConfig.DeclaredAccessActive"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LeaveSystem()
    {
        DagThread.Leave();
        if (CheckConfig.DeclaredAccessActive)
        {
            LeaveSystemCore();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeaveSystemCore()
    {
        var (prev, prevCompiled, prevName) = Frames.Pop();
        Current = prev;
        CurrentCompiled = prevCompiled;
        CurrentSystemName = prevName;
    }

    /// <summary>Assert the executing system may write <typeparamref name="T"/>; <paramref name="via"/> is the accessor the write goes through.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertWrite<T>(EntityAccessor via) where T : unmanaged
    {
        if (CheckConfig.DeclaredAccessActive)
        {
            AssertWriteCore(AccessTypeId<T>.Value, via);
        }
    }

    /// <summary>Assert the executing system may read <typeparamref name="T"/>; <paramref name="via"/> is the accessor the read goes through.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertRead<T>(EntityAccessor via) where T : unmanaged
    {
        if (CheckConfig.DeclaredAccessActive)
        {
            AssertReadCore(AccessTypeId<T>.Value, via);
        }
    }

    /// <summary>
    /// Assert the executing system may spawn or destroy an entity of <paramref name="meta"/>: it must be allowed to write every component of the archetype
    /// and, for a destroy, of every archetype the delete cascades to.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertStructural(ArchetypeMetadata meta, bool destroy, EntityAccessor via)
    {
        if (CheckConfig.DeclaredAccessActive)
        {
            AssertStructuralCore(meta, destroy, via);
        }
    }

    /// <summary>Assert the executing system declared <c>WritesEvents(queue)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertEventWrite(EventQueueBase queue)
    {
        if (CheckConfig.DeclaredAccessActive)
        {
            AssertEventCore(queue, write: true);
        }
    }

    /// <summary>Assert the executing system declared <c>ReadsEvents(queue)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertEventRead(EventQueueBase queue)
    {
        if (CheckConfig.DeclaredAccessActive)
        {
            AssertEventCore(queue, write: false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertWriteCore(int id, EntityAccessor via)
    {
        var c = CurrentCompiled;
        if (c == null || AccessTypeIds.Has(c.Writable, id) || (AccessTypeIds.Has(c.SideWritable, id) && IsSide(via)))
        {
            return;
        }
        throw Undeclared(id, "wrote", via, c);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertReadCore(int id, EntityAccessor via)
    {
        var c = CurrentCompiled;
        if (c == null || AccessTypeIds.Has(c.Readable, id) || (AccessTypeIds.Has(c.SideWritable, id) && IsSide(via)))
        {
            return;
        }
        throw Undeclared(id, "read", via, c);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertStructuralCore(ArchetypeMetadata meta, bool destroy, EntityAccessor via)
    {
        var c = CurrentCompiled;
        if (c == null || meta == null)
        {
            return;
        }
        var footprint = destroy ? meta.DestroyAccessFootprint : meta.SpawnAccessFootprint;
        var side = IsSide(via);
        for (var w = 0; w < footprint.Length; w++)
        {
            var need = footprint[w];
            var have = (w < c.Writable.Length ? c.Writable[w] : 0) | (side && w < c.SideWritable.Length ? c.SideWritable[w] : 0);
            var missing = need & ~have;
            if (missing != 0)
            {
                var id = (w << 6) | BitOperations.TrailingZeroCount(missing);
                throw Undeclared(id, destroy ? $"destroyed an entity of '{meta.Name}', which writes" : $"spawned an entity of '{meta.Name}', which writes",
                    via, c);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertEventCore(EventQueueBase queue, bool write)
    {
        var d = Current;
        if (d == null || queue == null)
        {
            return;
        }
        if (write ? d.WritesEvents.Contains(queue) : d.ReadsEvents.Contains(queue))
        {
            return;
        }
        var name = CurrentSystemName ?? "<unknown>";
        throw new InvalidAccessException(name, null,
            $"System '{name}' {(write ? "wrote to" : "drained")} event queue '{queue.Name}' but did not declare it. Add " +
            $"`b.{(write ? "WritesEvents" : "ReadsEvents")}(queue)` in Configure. Currently declared: {Summarize(d)}.", true);
    }

    private static bool IsSide(EntityAccessor via) => via is Transaction { IsSideTransaction: true };

    private static InvalidAccessException Undeclared(int id, string verb, EntityAccessor via, CompiledAccess c)
    {
        var type = AccessTypeIds.TypeOf(id);
        var name = CurrentSystemName ?? "<unknown>";
        var typeName = type?.Name ?? $"#{id}";
        string fix;
        if (AccessTypeIds.Has(c.SideWritable, id))
        {
            fix = $"It declares `SideWrites<{typeName}>()`, which covers a side transaction (`ctx.CreateSideTransaction`) only: go through one, or declare " +
                  $"`b.Writes<{typeName}>()`.";
        }
        else if (verb == "read")
        {
            fix = $"Add `b.Reads<{typeName}>()` (or ReadsFresh / ReadsSnapshot / AdditionalReads) in Configure.";
        }
        else
        {
            fix = $"Add `b.Writes<{typeName}>()` (or `b.SideWrites<{typeName}>()` for a side transaction) in Configure.";
        }
        return new InvalidAccessException(name, type,
            $"System '{name}' {verb} component '{typeName}' but did not declare it. {fix} Currently declared: {Summarize(Current)}.", true);
    }

    private static string Summarize(SystemAccessDescriptor d)
    {
        if (d == null || !d.HasAnyDeclaration)
        {
            return "(none)";
        }

        var parts = new List<string>();
        Add(parts, "Reads", d.Reads);
        Add(parts, "ReadsFresh", d.ReadsFresh);
        Add(parts, "ReadsSnapshot", d.ReadsSnapshot);
        Add(parts, "AdditionalReads", d.AdditionalReads);
        Add(parts, "Writes", d.Writes);
        Add(parts, "SideWrites", d.SideWrites);
        foreach (var q in d.WritesEvents)
        {
            parts.Add($"WritesEvents({q.Name})");
        }
        foreach (var q in d.ReadsEvents)
        {
            parts.Add($"ReadsEvents({q.Name})");
        }
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    private static void Add(List<string> parts, string kind, HashSet<Type> types)
    {
        foreach (var t in types)
        {
            parts.Add($"{kind}<{t.Name}>");
        }
    }
}
