using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Runtime safety net for declared system access (RFC 07 — Unit 4). The scheduler tags each worker thread with the currently-executing
/// system's <see cref="SystemAccessDescriptor"/> via <see cref="EnterSystem"/>; <see cref="EntityRef.Write{T}"/> calls
/// <see cref="AssertWrite{T}"/> to verify the type was declared. All three methods are runtime-gated by
/// <see cref="CheckConfig.DeclaredAccessActive"/> — strict mode's separate declared-access opt-in (#422), off by default. When off, each body is
/// JIT dead-code-eliminated (the gate is a <c>static readonly bool</c>), so the dispatch path takes zero overhead in production and the descriptor
/// stack is never populated (only <see cref="EnterSystem"/> writes it, and it early-returns).
/// </summary>
/// <remarks>
/// <para>
/// Storage is per-thread (<see cref="ThreadStaticAttribute"/>). Each worker maintains its own descriptor — multi-worker dispatch is
/// safe by construction. Push/pop semantics let inline successor execution restore the previous system's descriptor when a worker
/// runs multiple systems back-to-back.
/// </para>
/// <para>
/// Migration policy: a system whose access descriptor has <see cref="SystemAccessDescriptor.HasAnyDeclaration"/> = false is treated as
/// "undeclared" and the assertion silently passes — preserves backwards compatibility for systems that haven't migrated to declared access.
/// Once a developer adds any declaration, the validator activates fully (a <see cref="SystemBuilder.Writes{T}"/> not in the declared set throws).
/// </para>
/// </remarks>
internal static class SystemAccessValidator
{
    [ThreadStatic]
    private static SystemAccessDescriptor Current;

    [ThreadStatic]
    private static string CurrentSystemName;

    [ThreadStatic]
    private static Stack<(SystemAccessDescriptor Prev, string PrevName)> Frames;

    /// <summary>
    /// Push the given descriptor as the current thread's active system context.
    /// No-op unless <see cref="CheckConfig.DeclaredAccessActive"/> (JIT-folded away when off).
    /// Each call must be paired with a matching <see cref="LeaveSystem"/> in a finally block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnterSystem(SystemAccessDescriptor descriptor, string systemName)
    {
        // Fast/slow split: the tiny gate inlines into the caller so the folded-false gate erases the call entirely when off.
        if (CheckConfig.DeclaredAccessActive)
        {
            EnterSystemCore(descriptor, systemName);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnterSystemCore(SystemAccessDescriptor descriptor, string systemName)
    {
        var stack = Frames ??= new Stack<(SystemAccessDescriptor, string)>(8);
        stack.Push((Current, CurrentSystemName));
        Current = descriptor;
        CurrentSystemName = systemName;
    }

    /// <summary>Restore the previous descriptor + name. No-op unless <see cref="CheckConfig.DeclaredAccessActive"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LeaveSystem()
    {
        if (CheckConfig.DeclaredAccessActive)
        {
            LeaveSystemCore();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeaveSystemCore()
    {
        var (prev, prevName) = Frames.Pop();
        Current = prev;
        CurrentSystemName = prevName;
    }

    /// <summary>
    /// Assert that the currently-executing system declared <see cref="SystemBuilder.Writes{T}"/> or <see cref="SystemBuilder.SideWrites{T}"/>.
    /// No-op unless <see cref="CheckConfig.DeclaredAccessActive"/> (the one costly strict-mode check — 2 <c>HashSet</c> lookups per write — so it
    /// has its own opt-in). Skips the check when no system context is active (e.g., direct test code outside scheduler dispatch), or when the
    /// active descriptor has no declarations (transitional — system hasn't migrated yet).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertWrite<T>() where T : unmanaged
    {
        // Fast/slow split (#422): the gate is the ONLY thing on the per-Write<T> hot path. When DeclaredAccessActive is off
        // (the default) this inlines into EntityRef.Write and the folded-false gate erases the call — zero cost. The core (2
        // HashSet lookups + typeof) is too large to inline, hence NoInlining to keep the hot path small.
        if (CheckConfig.DeclaredAccessActive)
        {
            AssertWriteCore<T>();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertWriteCore<T>() where T : unmanaged
    {
        var current = Current;
        if (current == null)
        {
            return;
        }

        if (!current.HasAnyDeclaration)
        {
            return;
        }

        var t = typeof(T);
        if (current.Writes.Contains(t))
        {
            return;
        }

        if (current.SideWrites.Contains(t))
        {
            return;
        }

        throw new InvalidAccessException(CurrentSystemName ?? "<unknown>", t, SummarizeDeclared(current));
    }

    private static string SummarizeDeclared(SystemAccessDescriptor d)
    {
        if (d.Writes.Count == 0 && d.SideWrites.Count == 0)
        {
            return "(none)";
        }

        var parts = new List<string>(d.Writes.Count + d.SideWrites.Count);
        foreach (var w in d.Writes)
        {
            parts.Add($"Writes<{w.Name}>");
        }

        foreach (var w in d.SideWrites)
        {
            parts.Add($"SideWrites<{w.Name}>");
        }

        return string.Join(", ", parts);
    }
}
