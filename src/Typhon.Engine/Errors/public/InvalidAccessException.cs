using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Thrown when a system attempts to mutate a component or resource that it did not declare in its access set (RFC 07 — Unit 4). DEBUG builds only;
/// <see cref="SystemAccessValidator"/> compiles out in RELEASE.
/// </summary>
/// <remarks>
/// Indicates declaration drift: the system body's actual writes diverged from what was registered
/// via <see cref="SystemBuilder.Writes{T}"/> / <see cref="SystemBuilder.SideWrites{T}"/>. Fix by adding the missing declaration.
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

    /// <summary>Name of the system that performed the undeclared write.</summary>
    public string SystemName { get; }

    /// <summary>The component type the system wrote without declaring it in its access set.</summary>
    public Type UndeclaredType { get; }
}
