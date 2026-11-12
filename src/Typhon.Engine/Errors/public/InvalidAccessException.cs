using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Thrown when a system reads, writes, spawns, destroys or exchanges events through something it did not declare in its access set (rule DV-01). Runs
/// when declared-access checking is on: <c>Typhon:Checks:DeclaredAccess</c>, on by default when the entry assembly is a Debug build.
/// </summary>
/// <remarks>
/// Indicates declaration drift: the system body's actual access diverged from what was registered via <see cref="SystemBuilder.Reads{T}"/>,
/// <see cref="SystemBuilder.Writes{T}"/>, <see cref="SystemBuilder.SideWrites{T}"/>, <see cref="SystemBuilder.WritesEvents"/> and their siblings. An
/// undeclared access derives no scheduling edge, so it races with the systems that do declare it. Fix by adding the missing declaration.
/// </remarks>
[PublicAPI]
public sealed class InvalidAccessException : TyphonException
{
    /// <summary>
    /// Creates a new <see cref="InvalidAccessException"/> for a system that wrote an undeclared component.
    /// </summary>
    /// <param name="systemName">Name of the offending system.</param>
    /// <param name="undeclaredType">The component type that was written without being declared.</param>
    /// <param name="declaredSummary">Human-readable summary of the access set the system actually declared.</param>
    public InvalidAccessException(string systemName, Type undeclaredType, string declaredSummary) : base(TyphonErrorCode.InvalidSystemAccess,
            $"System '{systemName}' wrote component '{undeclaredType.Name}' but did not declare it. Add `b.Writes<{undeclaredType.Name}>()`" +
            $" (or `b.SideWrites<{undeclaredType.Name}>()` for side-transactions) in Configure. Currently declared: {declaredSummary}.")
    {
        SystemName = systemName;
        UndeclaredType = undeclaredType;
    }

    internal InvalidAccessException(string systemName, Type undeclaredType, string message, bool _) : base(TyphonErrorCode.InvalidSystemAccess, message)
    {
        SystemName = systemName;
        UndeclaredType = undeclaredType;
    }

    /// <summary>Name of the system that performed the undeclared access.</summary>
    public string SystemName { get; }

    /// <summary>The component type the system accessed without declaring it; null for an undeclared event queue.</summary>
    public Type UndeclaredType { get; }
}
