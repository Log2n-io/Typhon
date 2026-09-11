using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The drain's destination-cell sort (Prep's since #910) as a SITE of <see cref="RadixSort"/> (#889 lead F, made generic in #891): the queue's scratch that
/// <see cref="ArchetypeClusterState.RadixSortByDestCellKey"/> owns and grows, the key struct's order, and the public entry point. The algorithm itself —
/// stability, digit widths, skipped digits, signed keys — is pinned by <c>RadixSortTests</c> and is not repeated here.
/// </summary>
[TestFixture]
class MigrationDestCellRadixSortTests : TestBase<MigrationDestCellRadixSortTests>
{
    private ArchetypeClusterState NewState()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<DirKeyAlpha>();
        dbe.RegisterComponentFromAccessor<DirKeyBeta>();
        dbe.InitializeArchetypes();
        return dbe._archetypeStates[DirKeyArch.Metadata.ArchetypeId].ClusterState;
    }

    private static MigrationRequest[] Build(Random rng, int count, Func<Random, int> key)
    {
        var items = new MigrationRequest[count + 7];   // slack past `count`, which the sort must leave untouched
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new MigrationRequest(sourceClusterChunkId: i, sourceSlotIndex: i & 63, destCellKey: key(rng), destClusterChunkId: rng.Next(100));
        }

        return items;
    }

    /// <summary>The reference: a STABLE order by key — LINQ's OrderBy is documented stable — so equal keys keep their enqueue order.</summary>
    private static MigrationRequest[] Reference(MigrationRequest[] items, int count)
        => items.Take(count).OrderBy(r => r.DestCellKey).ToArray();

    private static void AssertSameSequence(MigrationRequest[] actual, MigrationRequest[] expected, int count, string what)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.That(actual[i].DestCellKey, Is.EqualTo(expected[i].DestCellKey), $"{what}: key at {i}");
            Assert.That(actual[i].SourceClusterChunkId, Is.EqualTo(expected[i].SourceClusterChunkId),
                $"{what}: a stable sort keeps the enqueue order inside cell {expected[i].DestCellKey}, position {i}");
            Assert.That(actual[i].DestClusterChunkId, Is.EqualTo(expected[i].DestClusterChunkId), $"{what}: the whole request travels with its key at {i}");
        }
    }

    /// <summary>
    /// The site's key is the destination cell, signed, and the site sorts only the first <c>count</c> entries: colliding cells over signed keys, with a tail
    /// past the count that must come back untouched.
    /// </summary>
    [Test]
    public void RadixSortByDestCellKey_OrdersByDestinationCell_Stably_LeavingTheTailAlone()
    {
        var state = NewState();
        var rng = new Random(889);
        var items = Build(rng, 3_000, r => r.Next(-64, 64) << 7);   // 128 cells, signed, ~23 requests each
        var expected = Reference(items, 3_000);
        var tail = items[3_000..];

        state.RadixSortByDestCellKey(items, 3_000);

        AssertSameSequence(items, expected, 3_000, "signed colliding cells");
        Assert.That(items[0].DestCellKey, Is.LessThan(0), "sanity: negative cells came first");
        for (var i = 0; i < tail.Length; i++)
        {
            Assert.That((items[3_000 + i].SourceClusterChunkId, items[3_000 + i].DestCellKey), Is.EqualTo((tail[i].SourceClusterChunkId, tail[i].DestCellKey)),
                $"entry {3_000 + i}, past `count`, is not the sort's to touch");
        }
    }

    /// <summary>The queue grows across ticks; the site's scratch must follow it rather than sort against a stale, shorter partner.</summary>
    [Test]
    public void Scratch_Grows_With_The_Queue()
    {
        var state = NewState();
        var rng = new Random(11);
        var small = Build(rng, 100, r => r.Next(1 << 16));
        state.RadixSortByDestCellKey(small, 100);
        AssertSameSequence(small, Reference(small, 100), 100, "first, small");

        var large = Build(rng, 20_000, r => r.Next(1 << 16));
        var expected = Reference(large, 20_000);
        state.RadixSortByDestCellKey(large, 20_000);
        AssertSameSequence(large, expected, 20_000, "then, two hundred times larger");
    }

    /// <summary>
    /// Through the entry point the fence uses (#910): the drain PREFIX is sorted in place, and a request past it stays exactly where it was — a sort over
    /// the whole queue would carry it into the drain (CR-01).
    /// </summary>
    [Test]
    [VerifiesRule("CR-01")]
    public void OrderDrainAndMeasureArrivals_SortsThePrefixAndLeavesTheTail()
    {
        var state = NewState();
        var rng = new Random(5);
        try
        {
            var items = Build(rng, 1_500, r => r.Next(1 << 18));
            var tail = items.AsSpan(1_000, 500).ToArray();
            state.PendingMigrations = items;
            state.PendingMigrationCount = 1_500;
            state.PendingMigrationDrainCount = 1_000;

            state.OrderDrainAndMeasureArrivals();

            var keys = new List<int>(1_000);
            for (var i = 0; i < 1_000; i++)
            {
                keys.Add(state.PendingMigrations[i].DestCellKey);
            }

            Assert.That(keys, Is.Ordered.Ascending, "the prefix must be ascending by destination cell");
            Assert.That(state.PendingMigrations.AsSpan(1_000, 500).ToArray(), Is.EqualTo(tail), "a request past the prefix must not move");
        }
        finally
        {
            state.PendingMigrations = null;
            state.PendingMigrationCount = 0;
            state.PendingMigrationDrainCount = 0;
        }
    }
}
