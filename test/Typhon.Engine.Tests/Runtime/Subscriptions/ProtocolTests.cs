using MemoryPack;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Engine.Tests;

[TestFixture]
public class ProtocolTests
{
    [Test]
    public void TickDeltaMessage_Roundtrip_EmptyMessage()
    {
        var original = new TickDeltaMessage
        {
            TickNumber = 42
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var deserialized = MemoryPackSerializer.Deserialize<TickDeltaMessage>(bytes);

        Assert.That(deserialized.TickNumber, Is.EqualTo(42));
        Assert.That(deserialized.Events, Is.Null);
        Assert.That(deserialized.Views, Is.Null);
    }

    [Test]
    public void TickDeltaMessage_Roundtrip_WithEvents()
    {
        var original = new TickDeltaMessage
        {
            TickNumber = 100,
            Events =
            [
                new SubscriptionEvent { ViewId = 1, Type = EventType.Subscribed, ViewName = "world_npcs" },
                new SubscriptionEvent { ViewId = 2, Type = EventType.Unsubscribed }
            ]
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var deserialized = MemoryPackSerializer.Deserialize<TickDeltaMessage>(bytes);

        Assert.That(deserialized.TickNumber, Is.EqualTo(100));
        Assert.That(deserialized.Events, Has.Length.EqualTo(2));
        Assert.That(deserialized.Events[0].ViewId, Is.EqualTo(1));
        Assert.That(deserialized.Events[0].Type, Is.EqualTo(EventType.Subscribed));
        Assert.That(deserialized.Events[0].ViewName, Is.EqualTo("world_npcs"));
        Assert.That(deserialized.Events[1].ViewId, Is.EqualTo(2));
        Assert.That(deserialized.Events[1].Type, Is.EqualTo(EventType.Unsubscribed));
        Assert.That(deserialized.Events[1].ViewName, Is.Null);
    }

    [Test]
    public void ViewDeltaMessage_Roundtrip_WithAddedModifiedRemoved()
    {
        var componentData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var fieldValues = new byte[] { 10, 20, 30, 40 };

        var original = new ViewDeltaMessage
        {
            ViewId = 7,
            Added =
            [
                new EntityDelta
                {
                    Id = 12345,
                    Components =
                    [
                        new ComponentSnapshot { ComponentId = 1, Data = componentData }
                    ]
                }
            ],
            Removed = [99999, 88888],
            Modified =
            [
                new EntityUpdate
                {
                    Id = 54321,
                    ChangedComponents =
                    [
                        new ComponentFieldUpdate
                        {
                            ComponentId = 2,
                            FieldDirtyBits = ~0UL,
                            FieldValues = fieldValues
                        }
                    ]
                }
            ]
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var deserialized = MemoryPackSerializer.Deserialize<ViewDeltaMessage>(bytes);

        Assert.That(deserialized.ViewId, Is.EqualTo(7));

        // Added
        Assert.That(deserialized.Added, Has.Length.EqualTo(1));
        Assert.That(deserialized.Added[0].Id, Is.EqualTo(12345));
        Assert.That(deserialized.Added[0].Components, Has.Length.EqualTo(1));
        Assert.That(deserialized.Added[0].Components[0].ComponentId, Is.EqualTo(1));
        Assert.That(deserialized.Added[0].Components[0].Data, Is.EqualTo(componentData));

        // Removed
        Assert.That(deserialized.Removed, Has.Length.EqualTo(2));
        Assert.That(deserialized.Removed[0], Is.EqualTo(99999));
        Assert.That(deserialized.Removed[1], Is.EqualTo(88888));

        // Modified
        Assert.That(deserialized.Modified, Has.Length.EqualTo(1));
        Assert.That(deserialized.Modified[0].Id, Is.EqualTo(54321));
        Assert.That(deserialized.Modified[0].ChangedComponents, Has.Length.EqualTo(1));
        Assert.That(deserialized.Modified[0].ChangedComponents[0].ComponentId, Is.EqualTo(2));
        Assert.That(deserialized.Modified[0].ChangedComponents[0].FieldDirtyBits, Is.EqualTo(~0UL));
        Assert.That(deserialized.Modified[0].ChangedComponents[0].FieldValues, Is.EqualTo(fieldValues));
    }

    [Test]
    public void ComponentFieldUpdate_AllFieldsDirty_V1Format()
    {
        // v1: all fields dirty, full component bytes
        var fullComponent = new byte[] { 0, 0, 128, 63, 0, 0, 0, 64, 0, 0, 64, 64 }; // float: 1.0, 2.0, 3.0

        var update = new ComponentFieldUpdate
        {
            ComponentId = 5,
            FieldDirtyBits = ~0UL,
            FieldValues = fullComponent
        };

        var bytes = MemoryPackSerializer.Serialize(update);
        var deserialized = MemoryPackSerializer.Deserialize<ComponentFieldUpdate>(bytes);

        Assert.That(deserialized.FieldDirtyBits, Is.EqualTo(~0UL), "v1: all fields should be marked dirty");
        Assert.That(deserialized.FieldValues, Is.EqualTo(fullComponent));
    }

    [Test]
    public void TickDeltaMessage_Roundtrip_FullMessage()
    {
        // Complete message with events and View deltas
        var original = new TickDeltaMessage
        {
            TickNumber = 500,
            Events =
            [
                new SubscriptionEvent { ViewId = 3, Type = EventType.SyncComplete }
            ],
            Views =
            [
                new ViewDeltaMessage
                {
                    ViewId = 1,
                    Added =
                    [
                        new EntityDelta
                        {
                            Id = 1000,
                            Components =
                            [
                                new ComponentSnapshot { ComponentId = 10, Data = [1, 2, 3] },
                                new ComponentSnapshot { ComponentId = 11, Data = [4, 5, 6, 7, 8] }
                            ]
                        }
                    ],
                    Removed = [2000, 3000],
                    Modified =
                    [
                        new EntityUpdate
                        {
                            Id = 4000,
                            ChangedComponents =
                            [
                                new ComponentFieldUpdate { ComponentId = 10, FieldDirtyBits = ~0UL, FieldValues = [9, 10, 11] }
                            ]
                        }
                    ]
                }
            ]
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var deserialized = MemoryPackSerializer.Deserialize<TickDeltaMessage>(bytes);

        Assert.That(deserialized.TickNumber, Is.EqualTo(500));
        Assert.That(deserialized.Events, Has.Length.EqualTo(1));
        Assert.That(deserialized.Views, Has.Length.EqualTo(1));
        Assert.That(deserialized.Views[0].Added, Has.Length.EqualTo(1));
        Assert.That(deserialized.Views[0].Added[0].Components, Has.Length.EqualTo(2));
        Assert.That(deserialized.Views[0].Removed, Has.Length.EqualTo(2));
        Assert.That(deserialized.Views[0].Modified, Has.Length.EqualTo(1));
    }
}
