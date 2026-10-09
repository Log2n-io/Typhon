using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// Collections in the .NET store (W34), from the committed tick-coll vector: an entity's and the controlled entity's lists overwrite whole, keep their
/// total when truncated, and keep their columns between sends.
/// </summary>
[TestFixture]
public class CollectionStoreTests
{
    [Test]
    public void TheTickCollVectorLandsInTheStore()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-coll")));
        var store = new WorldStore(plan);
        new FrameApplier(store).Apply(GoldenFiles.ReadBin("tick-coll"));

        var locker = plan.ArchetypeByName("Locker");
        var items = System.Array.Find(locker.Fields, f => f.Name == "items");
        var tags = System.Array.Find(locker.Fields, f => f.Name == "tags");
        Assert.That(store.TryLocate(1, out var archetype, out var one), Is.True);
        Assert.That(store.TryLocate(2, out _, out var two), Is.True);
        var lockers = store.Archetypes[archetype];

        Assert.Multiple(() =>
        {
            // netId 1 entered with no item and one tag, then a state sent one item: the list is that one.
            var first = lockers.Collections[items.Ordinal][one];
            Assert.That((first.Total, first.Count, first.Truncated), Is.EqualTo((1, 1, false)));
            Assert.That(first.Number(0, "id"), Is.EqualTo(65535));
            Assert.That(first.Number(0, "owner"), Is.EqualTo(4_000_000_000d), "an entityRef past 2³¹");
            Assert.That(first.Text(0, "name"), Is.EqualTo(string.Empty));
            Assert.That(lockers.Collections[tags.Ordinal][one].Number(0, "tag"), Is.EqualTo(9));

            // netId 2: four of nine sent, in the state's order — the enter's list is overwritten whole.
            var second = lockers.Collections[items.Ordinal][two];
            Assert.That((second.Total, second.Count, second.Truncated), Is.EqualTo((9, 4, true)));
            Assert.That(new[] { second.Text(0, "name"), second.Text(1, "name"), second.Text(2, "name"), second.Text(3, "name") },
                Is.EqualTo(new[] { "bouclier ø", "sword", "bouclier ø", "" }));
            Assert.That(new[] { second.Number(0, "stack"), second.Number(1, "stack"), second.Number(0, "lit"), second.Number(1, "lit") },
                Is.EqualTo(new double[] { 31, 3, 0, 1 }), "the element's pack");
            Assert.That(lockers.Collections[tags.Ordinal][two].Count, Is.EqualTo(3));

            // SELF: the owner collection of the controlled entity.
            var keys = store.Self.Collections[System.Array.Find(locker.OwnerFields, f => f.Name == "keys").Ordinal];
            Assert.That((keys.Total, keys.Count), Is.EqualTo((2, 2)));
            Assert.That(keys.Integer64(0, "code"), Is.EqualTo(ulong.MaxValue));
            Assert.That(keys.Number(0, "where"), Is.EqualTo(1.5));
        });
    }
}
