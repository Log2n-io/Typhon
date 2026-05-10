using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Computes the diff between old and new subscription sets for a client, producing <see cref="SubscriptionEvent"/>s.
/// </summary>
internal static class SubscriptionTransition
{
    /// <summary>
    /// Diff old and new subscription sets. Produces Subscribed events for new Views, Unsubscribed events for removed Views.
    /// Views present in both sets are kept without interruption.
    /// </summary>
    internal static void ComputeTransition(HashSet<PublishedView> oldSet, PublishedView[] newSet, List<SubscriptionEvent> events, 
        List<PublishedView> toSubscribe, List<PublishedView> toUnsubscribe)
    {
        // Find Views to subscribe (in new but not in old)
        for (var i = 0; i < newSet.Length; i++)
        {
            if (!oldSet.Contains(newSet[i]))
            {
                toSubscribe.Add(newSet[i]);
                events.Add(new SubscriptionEvent
                {
                    ViewId = newSet[i].PublishedId,
                    Type = EventType.Subscribed,
                    ViewName = newSet[i].Name
                });
            }
        }

        // Find Views to unsubscribe (in old but not in new)
        var newLookup = new HashSet<PublishedView>(newSet);
        foreach (var view in oldSet)
        {
            if (!newLookup.Contains(view))
            {
                toUnsubscribe.Add(view);
                events.Add(new SubscriptionEvent
                {
                    ViewId = view.PublishedId,
                    Type = EventType.Unsubscribed
                });
            }
        }
    }
}
