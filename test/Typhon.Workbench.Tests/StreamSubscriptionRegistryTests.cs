using NUnit.Framework;
using Typhon.Workbench.Services;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Unit tests for <see cref="StreamSubscriptionRegistry"/> (#308 Phase C). Pin the membership
/// semantics that the unified-stream multiplexer depends on — a regression that returns
/// <c>true</c> from <see cref="StreamSubscriptionRegistry.IsSubscribed"/> for an unregistered
/// streamId would silently fan out events to disconnected clients.
/// </summary>
[TestFixture]
public sealed class StreamSubscriptionRegistryTests
{
    [Test]
    public void IsSubscribed_UnknownStreamId_ReturnsFalse()
    {
        var reg = new StreamSubscriptionRegistry();
        Assert.That(reg.IsSubscribed(Guid.NewGuid(), "tick"), Is.False);
    }

    [Test]
    public void IsSubscribed_RegisteredButEmpty_ReturnsFalse()
    {
        var reg = new StreamSubscriptionRegistry();
        var id = Guid.NewGuid();
        reg.Register(id);
        Assert.That(reg.IsSubscribed(id, "tick"), Is.False,
            "default subscription set is empty — bootstrap events bypass the filter elsewhere");
    }

    [Test]
    public void Subscribe_AddsEvents_IsSubscribedReturnsTrue()
    {
        var reg = new StreamSubscriptionRegistry();
        var id = Guid.NewGuid();
        reg.Register(id);

        Assert.That(reg.Subscribe(id, ["tick", "log"]), Is.True);
        Assert.That(reg.IsSubscribed(id, "tick"), Is.True);
        Assert.That(reg.IsSubscribed(id, "log"), Is.True);
        Assert.That(reg.IsSubscribed(id, "error"), Is.False, "untouched events remain unsubscribed");
    }

    [Test]
    public void Subscribe_UnknownStreamId_ReturnsFalse()
    {
        var reg = new StreamSubscriptionRegistry();
        Assert.That(reg.Subscribe(Guid.NewGuid(), ["tick"]), Is.False,
            "client racing the connection close gets a `false` rather than a thrown exception");
    }

    [Test]
    public void Subscribe_Idempotent()
    {
        var reg = new StreamSubscriptionRegistry();
        var id = Guid.NewGuid();
        reg.Register(id);

        reg.Subscribe(id, ["tick"]);
        reg.Subscribe(id, ["tick"]);

        Assert.That(reg.IsSubscribed(id, "tick"), Is.True);
        Assert.That(reg.Snapshot(id), Has.Length.EqualTo(1));
    }

    [Test]
    public void Unsubscribe_RemovesEvent()
    {
        var reg = new StreamSubscriptionRegistry();
        var id = Guid.NewGuid();
        reg.Register(id);
        reg.Subscribe(id, ["tick", "log"]);

        Assert.That(reg.Unsubscribe(id, ["tick"]), Is.True);
        Assert.That(reg.IsSubscribed(id, "tick"), Is.False);
        Assert.That(reg.IsSubscribed(id, "log"), Is.True, "untouched events remain subscribed");
    }

    [Test]
    public void Unsubscribe_UnknownEvent_NoOp()
    {
        var reg = new StreamSubscriptionRegistry();
        var id = Guid.NewGuid();
        reg.Register(id);

        Assert.That(reg.Unsubscribe(id, ["never-subscribed"]), Is.True,
            "method returns whether the streamId is known, not whether the event was previously subscribed");
    }

    [Test]
    public void Unregister_DropsAllSubscriptions()
    {
        var reg = new StreamSubscriptionRegistry();
        var id = Guid.NewGuid();
        reg.Register(id);
        reg.Subscribe(id, ["tick"]);

        reg.Unregister(id);

        Assert.That(reg.IsSubscribed(id, "tick"), Is.False);
        Assert.That(reg.Subscribe(id, ["log"]), Is.False, "unregistered streamId no longer accepts subscriptions");
    }

    [Test]
    public void Register_Idempotent_DoesNotResetSubscriptions()
    {
        var reg = new StreamSubscriptionRegistry();
        var id = Guid.NewGuid();
        reg.Register(id);
        reg.Subscribe(id, ["tick"]);

        reg.Register(id);

        Assert.That(reg.IsSubscribed(id, "tick"), Is.True,
            "double-register must NOT clear an already-built subscription set");
    }

    [Test]
    public void Snapshot_ReturnsCurrentEvents()
    {
        var reg = new StreamSubscriptionRegistry();
        var id = Guid.NewGuid();
        reg.Register(id);
        reg.Subscribe(id, ["tick", "log"]);

        var snap = reg.Snapshot(id);

        Assert.That(snap, Is.EquivalentTo(new[] { "tick", "log" }));
    }
}
