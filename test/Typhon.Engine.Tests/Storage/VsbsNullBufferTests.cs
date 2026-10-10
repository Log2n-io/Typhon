using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Buffer id 0 is "no buffer": a collection never added to holds it, and chunk 0 of the buffer segment is the reserved sentinel behind it. A read of id 0
/// touches no lock of chunk 0 (#1291).
/// </summary>
[TestFixture]
internal sealed class VsbsNullBufferTests : TestBase<VsbsNullBufferTests>
{
    private DatabaseEngine _dbe;

    [SetUp]
    public void SetUpEngine()
    {
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<CompE_Eng>();
        RegisterComponents(_dbe);
        _dbe.InitializeArchetypes();
    }

    [TearDown]
    public void TearDownEngine()
    {
        _dbe?.Dispose();
        _dbe = null;
    }

    /// <summary>Chunk 0's shared lock holds, as a buffer root would carry them.</summary>
    private int ChunkZeroSharedHolds()
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var accessor = _dbe.GetComponentCollectionVSBS<int>().Segment.CreateChunkAccessor();
        try
        {
            ref var header = ref accessor.GetChunk<VariableSizedBufferRootHeader>(0);
            return header.Lock.SharedUsedCounter;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private EntityId SpawnWithAnEmptyCollection()
    {
        using var t = _dbe.CreateQuickTransaction();
        var a = new CompA(2);
        var e = new CompE_Eng(1);
        var id = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
        Assert.That(t.Commit(), Is.True);
        return id;
    }

    /// <summary>
    /// A read of a collection never added to leaves no hold behind. Each one took a shared hold on chunk 0 that nothing gave back: the eight-bit count of
    /// shared holders overflowed into its waiters after 255 reads, and a release of id 0 waited on the leaked holds for its exclusive lock.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void AReadOfAnEmptyCollection_LeavesNoHold()
    {
        var id = SpawnWithAnEmptyCollection();
        for (var i = 0; i < 3; i++)
        {
            using var t = _dbe.CreateReadOnlyTransaction();
            var e = t.Open(id).Read(CompAEArch.E);
            Assert.That(e.Collection._bufferId, Is.Zero, "premise: the collection has no buffer");
            using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);
            Assert.That(cca.ElementCount, Is.Zero);
            Assert.That(cca.GetAllElements(), Is.Empty);
            Assert.That(t.GetComponentCollectionRefCounter(ref e.Collection), Is.Zero);
            foreach (var _ in t.GetReadOnlyCollectionEnumerator(ref e.Collection))
            {
                Assert.Fail("an empty collection enumerated an element");
            }
        }

        Assert.That(ChunkZeroSharedHolds(), Is.Zero, "chunk 0's shared holds");
    }
}
