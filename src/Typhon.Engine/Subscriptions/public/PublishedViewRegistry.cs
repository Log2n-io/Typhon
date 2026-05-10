using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Registry of published Views available for client subscriptions. Thread-safe for registration (setup phase);
/// iteration during Output phase is uncontested (all systems have completed).
/// </summary>
[PublicAPI]
public sealed class PublishedViewRegistry
{
    private readonly Dictionary<string, PublishedView> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, PublishedView> _byId = new();
    private readonly Lock _lock = new();

    // Snapshot for iteration during Output phase (copy-on-write)
    private PublishedView[] _snapshot = [];

    /// <summary>All published Views (snapshot for lock-free iteration during Output phase).</summary>
    internal PublishedView[] PublishedViews => _snapshot;

    /// <summary>Number of published Views.</summary>
    public int Count => _snapshot.Length;

    /// <summary>Register a shared View for subscription.</summary>
    /// <exception cref="ArgumentException">A View with the same name is already published.</exception>
    /// <exception cref="InvalidOperationException">The View is already used as a system input.</exception>
    internal PublishedView RegisterShared(string name, ViewBase view, SubscriptionPriority priority)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(view);

        if (view.IsPublished)
        {
            throw new InvalidOperationException($"View (ViewId={view.ViewId}) is already published. Each View can only be published once.");
        }

        if (view.IsSystemInput)
        {
            throw new InvalidOperationException(
                $"Cannot publish View (ViewId={view.ViewId}) — it is already used as a system input. " +
                "Published Views must be separate instances. Create a new View with the same query for subscriptions.");
        }

        var published = PublishedView.CreateShared(name, view, priority);
        AddToRegistry(name, published);
        view.IsPublished = true;
        return published;
    }

    /// <summary>Register a per-client View factory for subscription.</summary>
    /// <exception cref="ArgumentException">A View with the same name is already published.</exception>
    internal PublishedView RegisterPerClient(string name, Func<ClientContext, ViewBase> factory, SubscriptionPriority priority)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(factory);

        var published = PublishedView.CreatePerClient(name, factory, priority);
        AddToRegistry(name, published);
        return published;
    }

    private void AddToRegistry(string name, PublishedView published)
    {
        lock (_lock)
        {
            if (_byName.ContainsKey(name))
            {
                throw new ArgumentException($"A View named '{name}' is already published.", nameof(name));
            }

            _byName[name] = published;
            _byId[published.PublishedId] = published;

            // Rebuild snapshot for lock-free iteration
            var newSnapshot = new PublishedView[_byName.Count];
            _byName.Values.CopyTo(newSnapshot, 0);
            _snapshot = newSnapshot;
        }
    }

    /// <summary>Look up a published View by name.</summary>
    internal PublishedView GetByName(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Look up a published View by published ID.</summary>
    internal PublishedView GetById(ushort id) => _byId.GetValueOrDefault(id);
}
