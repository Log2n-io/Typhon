using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Engine.Tests;

[TestFixture]
public class SubscriptionTransitionTests
{
    private static PublishedView CreateTestView(string name) =>
        PublishedView.CreateShared(name, null, SubscriptionPriority.Normal);

    [Test]
    public void EmptyToNew_AllSubscribed()
    {
        var viewA = CreateTestView("A");
        var viewB = CreateTestView("B");

        var oldSet = new HashSet<PublishedView>();
        var newSet = new[] { viewA, viewB };
        var events = new List<SubscriptionEvent>();
        var toSub = new List<PublishedView>();
        var toUnsub = new List<PublishedView>();

        SubscriptionTransition.ComputeTransition(oldSet, newSet, events, toSub, toUnsub);

        Assert.That(toSub, Has.Count.EqualTo(2));
        Assert.That(toUnsub, Is.Empty);
        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0].Type, Is.EqualTo(EventType.Subscribed));
        Assert.That(events[1].Type, Is.EqualTo(EventType.Subscribed));
    }

    [Test]
    public void AllToEmpty_AllUnsubscribed()
    {
        var viewA = CreateTestView("A");
        var viewB = CreateTestView("B");

        var oldSet = new HashSet<PublishedView> { viewA, viewB };
        var newSet = Array.Empty<PublishedView>();
        var events = new List<SubscriptionEvent>();
        var toSub = new List<PublishedView>();
        var toUnsub = new List<PublishedView>();

        SubscriptionTransition.ComputeTransition(oldSet, newSet, events, toSub, toUnsub);

        Assert.That(toSub, Is.Empty);
        Assert.That(toUnsub, Has.Count.EqualTo(2));
        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0].Type, Is.EqualTo(EventType.Unsubscribed));
        Assert.That(events[1].Type, Is.EqualTo(EventType.Unsubscribed));
    }

    [Test]
    public void PartialOverlap_CorrectDiff()
    {
        var viewA = CreateTestView("A");
        var viewB = CreateTestView("B");
        var viewC = CreateTestView("C");
        var viewD = CreateTestView("D");

        var oldSet = new HashSet<PublishedView> { viewA, viewB, viewC };
        var newSet = new[] { viewB, viewC, viewD };
        var events = new List<SubscriptionEvent>();
        var toSub = new List<PublishedView>();
        var toUnsub = new List<PublishedView>();

        SubscriptionTransition.ComputeTransition(oldSet, newSet, events, toSub, toUnsub);

        Assert.That(toSub, Has.Count.EqualTo(1));
        Assert.That(toSub[0], Is.SameAs(viewD));
        Assert.That(toUnsub, Has.Count.EqualTo(1));
        Assert.That(toUnsub[0], Is.SameAs(viewA));
    }

    [Test]
    public void SameSet_NoChanges()
    {
        var viewA = CreateTestView("A");
        var viewB = CreateTestView("B");

        var oldSet = new HashSet<PublishedView> { viewA, viewB };
        var newSet = new[] { viewA, viewB };
        var events = new List<SubscriptionEvent>();
        var toSub = new List<PublishedView>();
        var toUnsub = new List<PublishedView>();

        SubscriptionTransition.ComputeTransition(oldSet, newSet, events, toSub, toUnsub);

        Assert.That(toSub, Is.Empty);
        Assert.That(toUnsub, Is.Empty);
        Assert.That(events, Is.Empty);
    }

    [Test]
    public void Subscribed_Event_IncludesViewName()
    {
        var view = CreateTestView("world_npcs");

        var oldSet = new HashSet<PublishedView>();
        var newSet = new[] { view };
        var events = new List<SubscriptionEvent>();
        var toSub = new List<PublishedView>();
        var toUnsub = new List<PublishedView>();

        SubscriptionTransition.ComputeTransition(oldSet, newSet, events, toSub, toUnsub);

        Assert.That(events[0].ViewName, Is.EqualTo("world_npcs"));
    }
}
