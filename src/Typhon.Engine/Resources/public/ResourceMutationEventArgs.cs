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
    /// <summary>Whether the node was added or removed (<see cref="ResourceMutationKind.Mutated"/> is a reserved forward-compat slot, not raised).</summary>
    public ResourceMutationKind Kind { get; init; }

    /// <summary><see cref="IResource.Id"/> of the node that was added or removed.</summary>
    public string NodeId { get; init; }

    /// <summary><see cref="IResource.Id"/> of the parent the node was attached to or detached from.</summary>
    public string ParentId { get; init; }

    /// <summary><see cref="ResourceType"/> of the affected node.</summary>
    public ResourceType Type { get; init; }

    /// <summary>UTC time at which the mutation was raised.</summary>
    public DateTime Timestamp { get; init; }
}
