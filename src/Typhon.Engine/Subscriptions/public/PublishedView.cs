using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// A View that has been published for client subscriptions. Wraps a <see cref="ViewBase"/> with subscription metadata.
/// </summary>
/// <remarks>
/// <para>Shared Views use a single <see cref="ViewBase"/> instance for all subscribers. The delta is computed once and the serialized payload is memcpy'd to
/// each client's send buffer.</para>
/// <para>Per-client Views use a <see cref="ViewFactory"/> that creates a View per subscriber, parameterized by
/// <see cref="ClientContext"/>.</para>
/// </remarks>
[PublicAPI]
public sealed class PublishedView
{
    /// <summary>Human-readable name used by clients to identify this subscription.</summary>
    public string Name { get; }

    /// <summary>Published View identifier (derived from the underlying ViewBase.ViewId for shared, or auto-assigned for factories).</summary>
    public ushort PublishedId { get; }

    /// <summary>Subscription priority for overload throttling.</summary>
    public SubscriptionPriority Priority { get; }

    /// <summary>True if this is a shared View (single instance, all clients see same data).</summary>
    public bool IsShared { get; }

    /// <summary>The underlying shared View. Null for per-client Views.</summary>
    internal ViewBase SharedView { get; }

    /// <summary>Factory for creating per-client Views. Null for shared Views.</summary>
    internal Func<ClientContext, ViewBase> ViewFactory { get; }

    // Subscriber tracking (modified only during Output phase — no concurrent access)
    private readonly List<ClientConnection> _subscribers = [];

    /// <summary>Active subscribers for this published View.</summary>
    internal IReadOnlyList<ClientConnection> Subscribers => _subscribers;

    /// <summary>Number of active subscribers.</summary>
    public int SubscriberCount => _subscribers.Count;

    private static int NextPublishedId;

    private PublishedView(string name, SubscriptionPriority priority, bool isShared, ViewBase sharedView, Func<ClientContext, ViewBase> factory)
    {
        Name = name;
        Priority = priority;
        IsShared = isShared;
        SharedView = sharedView;
        ViewFactory = factory;
        PublishedId = (ushort)Interlocked.Increment(ref NextPublishedId);
    }

    /// <summary>Creates a shared published View.</summary>
    internal static PublishedView CreateShared(string name, ViewBase view, SubscriptionPriority priority) => new(name, priority, true, view, null);

    /// <summary>Creates a per-client published View with a factory.</summary>
    internal static PublishedView CreatePerClient(string name, Func<ClientContext, ViewBase> factory, SubscriptionPriority priority) =>
        new(name, priority, false, null, factory);

    internal void AddSubscriber(ClientConnection client) => _subscribers.Add(client);

    internal bool RemoveSubscriber(ClientConnection client) => _subscribers.Remove(client);
}
