using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// <see cref="TickEntities"/>, the value <see cref="TickContext.Entities"/> now is: a pooled list, a parallel chunk's slice, or a cluster/partition view,
/// carried without boxing (#1623 residual: a boxed list per system run was the shard's last avoidable allocation).
/// </summary>
[TestFixture]
class TickEntitiesTests
{
    private static EntityId[] Ids(int n) => Enumerable.Range(1, n).Select(i => EntityId.FromRawValue((ulong)i)).ToArray();

    private static long ForeachAllocation(TickEntities set, out ulong sum)
    {
        sum = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var id in set)
        {
            sum += id.RawValue;
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public void AListASliceAndAViewEnumerateTheSameEntities()
    {
        var ids = Ids(10);
        TickEntities list = new PooledEntityList(ids, 6);
        TickEntities slice = new PooledEntitySlice(ids, 3, 4);
        var view = TickEntities.Of(new List<EntityId>(ids[..5]));

        Assert.Multiple(() =>
        {
            Assert.That(list.ToArray(), Is.EqualTo(ids[..6]));
            Assert.That(list.Count, Is.EqualTo(6));
            Assert.That(list[5], Is.EqualTo(ids[5]));
            Assert.That(list.AsSpan().ToArray(), Is.EqualTo(ids[..6]));

            Assert.That(slice.ToArray(), Is.EqualTo(ids[3..7]), "a slice starts at its own offset");
            Assert.That(slice[0], Is.EqualTo(ids[3]));
            Assert.That(slice.Count, Is.EqualTo(4));

            Assert.That(view.ToArray(), Is.EqualTo(ids[..5]));
            Assert.That(view.Count, Is.EqualTo(5));
            Assert.That(view.IsArrayBacked, Is.False);
            Assert.That(view.AsSpan().IsEmpty, Is.True);
            Assert.That(() => view[0], Throws.TypeOf<NotSupportedException>(), "a view has no random access");

            Assert.That(TickEntities.Empty.Count, Is.Zero);
            Assert.That(TickEntities.Empty.ToArray(), Is.Empty);
            Assert.That(() => list[6], Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void ForeachOverAnArrayBackedSetAllocatesNothing()
    {
        var ids = Ids(64);
        TickEntities list = new PooledEntityList(ids, 64);
        TickEntities slice = new PooledEntitySlice(ids, 8, 16);
        ForeachAllocation(list, out _);   // JIT

        Assert.Multiple(() =>
        {
            Assert.That(ForeachAllocation(list, out var a), Is.Zero);
            Assert.That(a, Is.EqualTo(64UL * 65 / 2));
            Assert.That(ForeachAllocation(slice, out _), Is.Zero);
            Assert.That(ForeachAllocation(TickEntities.Empty, out _), Is.Zero);
        });
    }

    [Test]
    public void WrappingAPooledListAllocatesNothing()
    {
        // The regression itself: TickContext.Entities used to be an interface, so handing it a PooledEntityList boxed it on every system run.
        var list = new PooledEntityList(Ids(8), 8);
        TickContext Make() => new() { Entities = list };
        Make();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var ctx = Make();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(ctx.Entities.Count, Is.EqualTo(8));
    }
}
