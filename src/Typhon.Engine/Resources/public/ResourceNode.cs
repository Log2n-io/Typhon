using JetBrains.Annotations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine;

/// <summary>
/// Default <see cref="IResource"/> implementation: a concrete tree node backed by a thread-safe child collection. Subclass to override
/// <see cref="Count"/> or <see cref="DisposeWithParent"/>, or to add metrics via <see cref="IMetricSource"/>.
/// </summary>
[DebuggerDisplay("{Id}, Children: {_children.Count})")]
[PublicAPI]
public class ResourceNode : IResource
{
    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// Default: no count. Subclasses that wrap a countable resource (ComponentTable, Segments folder, Index, …) override this to surface a live count to the
    /// Workbench tree.
    /// </summary>
    public virtual int? Count => null;

    /// <inheritdoc />
    public ResourceType Type { get; }

    /// <inheritdoc />
    public IResource Parent { get; }

    /// <inheritdoc />
    public IEnumerable<IResource> Children => _children.Values;

    /// <inheritdoc />
    public DateTime CreatedAt { get; }

    /// <inheritdoc />
    public IResourceRegistry Owner { get; }

    /// <summary>
    /// Diagnostic metadata indicating how this resource responds when its capacity limit is reached.
    /// </summary>
    /// <remarks>
    /// <see cref="ExhaustionPolicy.None"/> for intermediate/structural nodes that don't own a bounded resource.
    /// </remarks>
    public ExhaustionPolicy ExhaustionPolicy { get; }

    /// <summary>
    /// Whether this node is disposed by its parent's <see cref="Dispose(bool)"/> cascade. Default <c>true</c>. A node whose lifecycle is owned by something
    /// other than the resource tree — e.g. a profiler exporter owned by <c>TyphonProfiler</c> — overrides this to <c>false</c>: it stays registered in the
    /// tree for display, but is disposed only by its real owner, never by an ancestor's teardown.
    /// </summary>
    public virtual bool DisposeWithParent => true;

    /// <inheritdoc />
    public bool RegisterChild(IResource child)
    {
        if (!_children.TryAdd(child.Id, child))
        {
            return false;
        }
        (Owner as ResourceRegistry)?.RaiseMutation(new ResourceMutationEventArgs
        {
            Kind = ResourceMutationKind.Added,
            NodeId = child.Id,
            ParentId = Id,
            Type = child.Type,
            Timestamp = DateTime.UtcNow
        });
        return true;
    }

    /// <inheritdoc />
    public bool RemoveChild(IResource resource)
    {
        if (!_children.TryRemove(resource.Id, out _))
        {
            return false;
        }
        (Owner as ResourceRegistry)?.RaiseMutation(new ResourceMutationEventArgs
        {
            Kind = ResourceMutationKind.Removed,
            NodeId = resource.Id,
            ParentId = Id,
            Type = resource.Type,
            Timestamp = DateTime.UtcNow
        });
        return true;
    }

    private readonly ConcurrentDictionary<string, IResource> _children = new();

    /// <summary>
    /// Creates a node and registers it under <paramref name="parent"/>, inheriting the parent's <see cref="Owner"/>.
    /// </summary>
    /// <param name="id">Stable identifier, unique among <paramref name="parent"/>'s children. Defaults to the runtime type name when <c>null</c>.</param>
    /// <param name="type">Resource classification.</param>
    /// <param name="parent">Parent node; must be non-null (the root is created internally). This node is added to its children.</param>
    /// <param name="exhaustionPolicy">
    /// Diagnostic metadata describing how this node reacts when its capacity is exhausted. <see cref="ExhaustionPolicy.None"/> for structural nodes.
    /// </param>
    /// <param name="name">Human-readable display label. Defaults to <paramref name="id"/> when <c>null</c>.</param>
    public ResourceNode(string id, ResourceType type, IResource parent, ExhaustionPolicy exhaustionPolicy = ExhaustionPolicy.None, string name = null)
    {
        Id = id ?? $"{GetType().Name}";
        Name = name ?? Id;
        Type = type;
        Parent = parent;
        Owner = parent.Owner;
        ExhaustionPolicy = exhaustionPolicy;
        CreatedAt = DateTime.UtcNow;
        // _children is already initialized (field initializer) so subscribers to NodeMutated
        // can safely walk our (empty) child set during the Added event.
        Parent.RegisterChild(this);
    }

    internal static ResourceNode CreateRoot(ResourceRegistry registry) => new(registry);

    private ResourceNode(ResourceRegistry registry)
    {
        Id = "Root";
        Name = "Root";
        Type = ResourceType.Node;
        Parent = null;
        Owner = registry;
        ExhaustionPolicy = ExhaustionPolicy.None;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>Disposes this node and, recursively, every child whose <see cref="DisposeWithParent"/> is <c>true</c>.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases child resources. When <paramref name="disposing"/> is <c>true</c>, cascades disposal to each child except those whose
    /// <see cref="DisposeWithParent"/> is <c>false</c> (their lifecycle is owned elsewhere), then clears the child set.
    /// </summary>
    /// <param name="disposing"><c>true</c> when called from <see cref="Dispose()"/>; <c>false</c> from a finalizer, in which case this is a no-op.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }
        foreach (var resource in _children.Values)
        {
            // Skip children whose lifecycle is owned elsewhere (e.g. profiler exporters owned by TyphonProfiler) — they are in the tree for display only.
            if (resource is ResourceNode node && !node.DisposeWithParent)
            {
                continue;
            }
            resource.Dispose();
        }
        _children.Clear();
    }
}