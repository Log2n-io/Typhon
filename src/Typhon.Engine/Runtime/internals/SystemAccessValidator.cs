using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Debug-only runtime safety net for declared system access (RFC 07 — Unit 4). The scheduler tags each worker thread with the currently-executing
/// system's <see cref="SystemAccessDescriptor"/> via <see cref="EnterSystem"/>; <see cref="EntityRef.Write{T}"/> calls
/// <see cref="AssertWrite{T}"/> to verify the type was declared. All three public methods are <see cref="ConditionalAttribute"/>-stripped from
/// RELEASE builds — call sites disappear at compile time, so the dispatch path takes zero overhead in production.
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
    /// Compiled out in RELEASE — call sites strip entirely (zero dispatch-path overhead in production).
    /// Each call must be paired with a matching <see cref="LeaveSystem"/> in a finally block.
    /// </summary>
    [Conditional("DEBUG")]
    public static void EnterSystem(SystemAccessDescriptor descriptor, string systemName)
    {
        var stack = Frames ??= new Stack<(SystemAccessDescriptor, string)>(8);
        stack.Push((Current, CurrentSystemName));
        Current = descriptor;
        CurrentSystemName = systemName;
    }

    /// <summary>Restore the previous descriptor + name. Compiled out in RELEASE.</summary>
    [Conditional("DEBUG")]
    public static void LeaveSystem()
    {
        var (prev, prevName) = Frames.Pop();
        Current = prev;
        CurrentSystemName = prevName;
    }

    /// <summary>
    /// Assert that the currently-executing system declared <see cref="SystemBuilder.Writes{T}"/> or <see cref="SystemBuilder.SideWrites{T}"/>.
    /// Compiled out in RELEASE. Skips the check when no system context is active (e.g., direct test code outside scheduler dispatch),
    /// or when the active descriptor has no declarations (transitional — system hasn't migrated yet).
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertWrite<T>() where T : unmanaged
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
