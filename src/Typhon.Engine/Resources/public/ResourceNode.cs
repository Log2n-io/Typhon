using JetBrains.Annotations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine;

[DebuggerDisplay("{Id}, Children: {_children.Count})")]
[PublicAPI]
public class ResourceNode : IResource
{
    public string Id { get; }
    public string Name { get; }

    /// <summary>
    /// Default: no count. Subclasses that wrap a countable resource (ComponentTable, Segments folder, Index, …) override this to surface a live count to the
    /// Workbench tree.
    /// </summary>
    public virtual int? Count => null;

    public ResourceType Type { get; }
    public IResource Parent { get; }
    public IEnumerable<IResource> Children => _children.Values;
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }

    /// <summary>
    /// Diagnostic metadata indicating how this resource responds when its capacity limit is reached.
    /// </summary>
    /// <remarks>
    /// <see cref="ExhaustionPolicy.None"/> for intermediate/structural nodes that don't own a bounded resource.
    /// </remarks>
    public ExhaustionPolicy ExhaustionPolicy { get; }

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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }
        foreach (var resource in _children.Values)
        {
            resource.Dispose();
        }
        _children.Clear();
    }
}