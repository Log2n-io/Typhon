using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Kind of mutation applied to the resource graph. Emitted by <see cref="IResourceRegistry.NodeMutated"/>.
/// </summary>
[PublicAPI]
public enum ResourceMutationKind
{
    /// <summary>A new resource was registered under a parent.</summary>
    Added,

    /// <summary>A resource was removed from its parent.</summary>
    Removed,

    /// <summary>An existing resource's observable state changed (not emitted in Phase 6 — forward-compat slot).</summary>
    Mutated
}

/// <summary>
/// Event args for <see cref="IResourceRegistry.NodeMutated"/>. Raised on Add/Remove; carries the minimal identification needed to invalidate subscribers'
/// caches without forcing a full graph copy.
/// </summary>
[PublicAPI]
public readonly struct ResourceMutationEventArgs
{
    public ResourceMutationKind Kind { get; init; }
    public string NodeId { get; init; }
    public string ParentId { get; init; }
    public ResourceType Type { get; init; }
    public DateTime Timestamp { get; init; }
}
