using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// #1362 — replacing a stored collection's content (Replace / Clear) through the handle's accessor, and the ownership defects found on the way:
// an accessor that wrote its buffer back only on Dispose, a SingleVersion write that was never recorded, a Commit-discipline write that edited the
// committed buffer, and a Set that overwrote collection handles.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.CcReplace.Versioned", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CcrVersioned
{
    [Field]
    public int A;

    [Field]
    public ComponentCollection<int> Items;
}

[Component("Typhon.Test.CcReplace.Single", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CcrSingle
{
    [Field]
    public int A;

    [Field]
    public ComponentCollection<int> Items;
}

[Archetype]
partial class CcrVersionedUnit : Archetype<CcrVersionedUnit>
{
    public static readonly Comp<CcrVersioned> C = Register<CcrVersioned>();
}

[Archetype]
partial class CcrSingleUnit : Archetype<CcrSingleUnit>
{
    public static readonly Comp<CcrSingle> C = Register<CcrSingle>();
}

[Archetype]
partial class CcrPairUnit : Archetype<CcrPairUnit>
{
    public static readonly Comp<CcrVersioned> V = Register<CcrVersioned>();
    public static readonly Comp<CcrSingle> S = Register<CcrSingle>();
}

[TestFixture]
class ComponentCollectionReplaceTests : TestBase<ComponentCollectionReplaceTests>
{
    private static readonly int[] Seed = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
    private static readonly int[] Replacement = [70, 80, 90];

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CcrVersioned>();
        dbe.RegisterComponentFromAccessor<CcrSingle>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId SpawnVersioned(DatabaseEngine dbe, int[] items, out int bufferId)
    {
        using var t = dbe.CreateQuickTransaction();
        var v = new CcrVersioned { A = 1 };
        using (var cca = t.CreateComponentCollectionAccessor(ref v.Items))
        {
            cca.Replace(items);
        }
        bufferId = v.Items._bufferId;
        var id = t.Spawn<CcrVersionedUnit>(CcrVersionedUnit.C.Set(in v));
        Assert.That(t.Commit(), Is.True, "spawn commit");
        return id;
    }

    private static EntityId SpawnSingle(DatabaseEngine dbe, int[] items, out int bufferId)
    {
        using var t = dbe.CreateQuickTransaction();
        var s = new CcrSingle { A = 1 };
        using (var cca = t.CreateComponentCollectionAccessor(ref s.Items))
        {
            cca.Replace(items);
        }
        bufferId = s.Items._bufferId;
        var id = t.Spawn<CcrSingleUnit>(CcrSingleUnit.C.Set(in s));
        Assert.That(t.Commit(), Is.True, "spawn commit");
        return id;
    }

    private static int[] Elements(Transaction t, ComponentCollection<int> field)
    {
        using var cca = t.CreateComponentCollectionAccessor(ref field);
        return cca.GetAllElements();
    }

    private static int[] VersionedElements(DatabaseEngine dbe, EntityId id, out int bufferId)
    {
        using var t = dbe.CreateQuickTransaction();
        var v = t.Open(id).Read(CcrVersionedUnit.C);
        bufferId = v.Items._bufferId;
        return Elements(t, v.Items);
    }

    private static int[] SingleElements(DatabaseEngine dbe, EntityId id, out int bufferId)
    {
        using var t = dbe.CreateQuickTransaction();
        var s = t.Open(id).Read(CcrSingleUnit.C);
        bufferId = s.Items._bufferId;
        return Elements(t, s.Items);
    }

    /// <summary>
    /// The buffer's reference count, 0 once its root is no longer live (freed, or held in the free quarantine) — never read from a freed chunk's header. Every
    /// probe also asserts that the segment has seen no double free, which a refcount alone cannot show once the chunk is gone.
    /// </summary>
    private static int RefCount(DatabaseEngine dbe, int bufferId)
    {
        var segment = dbe.GetComponentCollectionVSBS<int>().Segment;
        Assert.That(segment.DoubleFreeCount, Is.Zero, "a collection buffer chunk was freed twice");
        if (!segment.IsChunkLive(bufferId))
        {
            return 0;
        }

        using var t = dbe.CreateQuickTransaction();
        var field = new ComponentCollection<int> { _bufferId = bufferId };
        return t.GetComponentCollectionRefCounter(ref field);
    }

    // ── Versioned ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Replace_Versioned_OlderSnapshotKeepsTheOldContent_UntilItsRevisionIsCollected()
    {
        using var dbe = SetupEngine();
        var id = SpawnVersioned(dbe, Seed, out var oldBuffer);

        var reader = dbe.CreateReadOnlyTransaction();   // a snapshot taken before the replace
        int newBuffer;
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrVersionedUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrVersionedUnit.C, ref copy, ref copy.Items))
            {
                cca.Replace(Replacement);
            }
            newBuffer = copy.Items._bufferId;
            Assert.That(t.Commit(), Is.True);   // no Set: the accessor stored the new buffer
        }

        Assert.That(newBuffer, Is.Not.EqualTo(oldBuffer), "a replace writes a fresh buffer, never the shared one");
        var old = reader.Open(id).Read(CcrVersionedUnit.C);
        Assert.That(Elements(reader, old.Items), Is.EqualTo(Seed), "the older snapshot must still read the content it saw");
        Assert.That(VersionedElements(dbe, id, out var stored), Is.EqualTo(Replacement));
        Assert.That(stored, Is.EqualTo(newBuffer));
        Assert.That(RefCount(dbe, oldBuffer), Is.EqualTo(1), "the replace released the new revision's reference only; the old revision keeps its own");

        reader.Dispose();
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(RefCount(dbe, oldBuffer), Is.EqualTo(0), "the old buffer leaked once its revision was collected");
        Assert.That(RefCount(dbe, newBuffer), Is.EqualTo(1));
    }

    [Test]
    public void Clear_Versioned_StoresNoBuffer_AndTheOldRevisionKeepsItsOwn()
    {
        using var dbe = SetupEngine();
        var id = SpawnVersioned(dbe, Seed, out var oldBuffer);

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrVersionedUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrVersionedUnit.C, ref copy, ref copy.Items))
            {
                cca.Clear();
                Assert.That(cca.ElementCount, Is.Zero);
            }
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(VersionedElements(dbe, id, out var stored), Is.Empty);
        Assert.That(stored, Is.Zero, "a cleared collection holds no buffer");
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(RefCount(dbe, oldBuffer), Is.EqualTo(0), "the cleared buffer leaked");
    }

    /// <summary>
    /// The write-back happens when the accessor moves to another buffer, not when it is disposed. Before, a transaction rolled back past an accessor that
    /// never disposed left the new revision naming the original buffer, whose extra reference the move had already given back; the rollback then released
    /// what the new revision named — the committed revision's buffer, freed under it.
    /// </summary>
    [Test]
    [TestCase(true, TestName = "RolledBack_WithoutDispose_KeepsTheCommittedBuffer(Replace)")]
    [TestCase(false, TestName = "RolledBack_WithoutDispose_KeepsTheCommittedBuffer(Add)")]
    [VerifiesRule("EP-03")]
    public void Versioned_RolledBack_WithoutDispose_KeepsTheCommittedBuffer(bool replace)
    {
        using var dbe = SetupEngine();
        var id = SpawnVersioned(dbe, Seed, out var committedBuffer);

        int movedTo;
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrVersionedUnit.C);
            var cca = entity.CreateComponentCollectionAccessor(CcrVersionedUnit.C, ref copy, ref copy.Items);
            if (replace)
            {
                cca.Replace(Replacement);
            }
            else
            {
                cca.Add(99);
            }
            movedTo = copy.Items._bufferId;
            // Never disposed, never committed: the transaction rolls back on dispose.
        }

        Assert.That(movedTo, Is.Not.EqualTo(committedBuffer));
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(1), "the rollback released the committed revision's buffer");
        Assert.That(RefCount(dbe, movedTo), Is.EqualTo(0), "the rolled-back buffer leaked");
        Assert.That(VersionedElements(dbe, id, out var stored), Is.EqualTo(Seed));
        Assert.That(stored, Is.EqualTo(committedBuffer));
    }

    [Test]
    public void AddThenReplace_SameTransaction_FreesTheClone()
    {
        using var dbe = SetupEngine();
        var id = SpawnVersioned(dbe, Seed, out var committedBuffer);

        int clone;
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrVersionedUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrVersionedUnit.C, ref copy, ref copy.Items))
            {
                cca.Add(99);
                clone = copy.Items._bufferId;
                cca.Replace(Replacement);
            }
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(RefCount(dbe, clone), Is.EqualTo(0), "the clone the replace dropped leaked");
        Assert.That(VersionedElements(dbe, id, out _), Is.EqualTo(Replacement));
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(0));
    }

    [Test]
    public void Replace_SpanningManyChunks_KeepsTheOrder_FromAndToEmpty()
    {
        using var dbe = SetupEngine();
        var id = SpawnVersioned(dbe, [], out var spawned);
        Assert.That(spawned, Is.Zero, "an empty replace allocates nothing");

        var big = new int[5000];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = big.Length - i;
        }

        foreach (var content in new int[][] { big, [], Replacement })
        {
            using (var t = dbe.CreateQuickTransaction())
            {
                var entity = t.OpenMut(id);
                var copy = entity.Read(CcrVersionedUnit.C);
                using (var cca = entity.CreateComponentCollectionAccessor(CcrVersionedUnit.C, ref copy, ref copy.Items))
                {
                    cca.Replace(content);
                }
                Assert.That(t.Commit(), Is.True);
            }

            Assert.That(VersionedElements(dbe, id, out _), Is.EqualTo(content), $"content of {content.Length} elements");
        }
    }

    // ── SingleVersion, TickFence ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Replace_SingleVersion_FreesTheOldBufferAtOnce_WithoutASet()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out var oldBuffer);

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrSingleUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrSingleUnit.C, ref copy, ref copy.Items))
            {
                cca.Replace(Replacement);
            }
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(SingleElements(dbe, id, out var stored), Is.EqualTo(Replacement));
        Assert.That(RefCount(dbe, oldBuffer), Is.EqualTo(0), "the sole owner's previous buffer leaked");
        Assert.That(RefCount(dbe, stored), Is.EqualTo(1));

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrSingleUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrSingleUnit.C, ref copy, ref copy.Items))
            {
                cca.Clear();
            }
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(SingleElements(dbe, id, out var cleared), Is.Empty);
        Assert.That(cleared, Is.Zero);
        Assert.That(RefCount(dbe, stored), Is.EqualTo(0), "the cleared buffer leaked");
    }

    /// <summary>
    /// An in-place SingleVersion append through the handle is a write: the entity is marked dirty, so the fence logs it and change filters see it. Before,
    /// only a Versioned component got its write recorded when the accessor was made, and an append without a <c>Set</c> left nothing for the fence to emit.
    /// </summary>
    [Test]
    [VerifiesRule("EP-03")]
    public void Add_SingleVersion_ThroughTheHandle_MarksTheEntityDirty()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out _);
        var clusterState = dbe._archetypeStates[Archetype<CcrSingleUnit>.Metadata.ArchetypeId].ClusterState;
        dbe.WriteTickFence(1);
        clusterState.ClusterDirtyBitmap.Snapshot();
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "precondition: nothing dirty after the fence");

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrSingleUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrSingleUnit.C, ref copy, ref copy.Items))
            {
                cca.Add(99);
            }
            Assert.That(t.Commit(), Is.True);   // no Set
        }

        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "the append was not recorded: the fence would emit nothing for it");
    }

    // ── SingleVersion, Commit discipline ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Under the Commit discipline a SingleVersion write is staged and published at commit. The collection must follow: an append clones the buffer the
    /// committed value names instead of editing it, so another transaction sees the old content until the commit, and a rollback leaves it whole.
    /// </summary>
    [Test]
    [VerifiesRule("EP-03")]
    public void Add_CommitDiscipline_LeavesTheCommittedBufferAlone_UntilTheCommit()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out var committedBuffer);

        int clone;
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit))
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrSingleUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrSingleUnit.C, ref copy, ref copy.Items))
            {
                cca.Add(99);
            }
            clone = copy.Items._bufferId;
            Assert.That(clone, Is.Not.EqualTo(committedBuffer), "the committed buffer was edited in place");
            Assert.That(SingleElements(dbe, id, out _), Is.EqualTo(Seed), "another transaction saw the append before the commit");
            // rolled back on dispose
        }

        Assert.That(SingleElements(dbe, id, out var stored), Is.EqualTo(Seed), "the rollback did not undo the append");
        Assert.That(stored, Is.EqualTo(committedBuffer));
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(1));
        Assert.That(RefCount(dbe, clone), Is.EqualTo(0), "the rolled-back clone leaked");
    }

    [Test]
    [VerifiesRule("EP-03")]
    public void Replace_CommitDiscipline_PublishesTheNewBuffer_AndReleasesTheCommittedOne()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out var committedBuffer);

        int newBuffer;
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit))
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrSingleUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrSingleUnit.C, ref copy, ref copy.Items))
            {
                cca.Replace(Replacement);
                cca.Add(100);   // in place: the new buffer is this transaction's
            }
            newBuffer = copy.Items._bufferId;
            Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(1), "the committed buffer was released before the publish");
            Assert.That(SingleElements(dbe, id, out _), Is.EqualTo(Seed), "another transaction saw the replace before the commit");
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(SingleElements(dbe, id, out var stored), Is.EqualTo(new[] { 70, 80, 90, 100 }));
        Assert.That(stored, Is.EqualTo(newBuffer));
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(0), "the publish did not release the replaced buffer");
        Assert.That(RefCount(dbe, newBuffer), Is.EqualTo(1));
    }

    [Test]
    [VerifiesRule("EP-03")]
    public void Replace_CommitDiscipline_RolledBack_KeepsTheCommittedBuffer_AndFreesTheNewOne()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out var committedBuffer);

        int newBuffer;
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit))
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrSingleUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrSingleUnit.C, ref copy, ref copy.Items))
            {
                cca.Replace(Replacement);
            }
            newBuffer = copy.Items._bufferId;
            Assert.That(t.Rollback(), Is.True);
        }

        Assert.That(SingleElements(dbe, id, out var stored), Is.EqualTo(Seed));
        Assert.That(stored, Is.EqualTo(committedBuffer));
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(1));
        Assert.That(RefCount(dbe, newBuffer), Is.EqualTo(0), "the staged value's buffer leaked with the rollback");
    }

    [Test]
    [VerifiesRule("EP-03")]
    public void Replace_CommitDiscipline_ThenDestroy_SameTransaction_FreesBothBuffers()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out var committedBuffer);

        int newBuffer;
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit))
        {
            var entity = t.OpenMut(id);
            var copy = entity.Read(CcrSingleUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrSingleUnit.C, ref copy, ref copy.Items))
            {
                cca.Replace(Replacement);
            }
            newBuffer = copy.Items._bufferId;
            t.Destroy(id);
            Assert.That(t.Commit(), Is.True);
        }

        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(0), "the destroy did not free the committed buffer");
        Assert.That(RefCount(dbe, newBuffer), Is.EqualTo(0), "the never-published staged buffer leaked");
    }

    /// <summary>
    /// Two Commit-discipline transactions stage the same SingleVersion entity: one replaces its collection, the other only sets a scalar. SingleVersion has
    /// no isolation, so the last publish wins — but each staged value holds its own reference, so neither publish frees a buffer the other still names. A
    /// release decided from HEAD's state at publish time released the first writer's buffer and left HEAD naming a freed one.
    /// </summary>
    [Test]
    [VerifiesRule("EP-03")]
    public void TwoCommitWriters_OnOneEntity_TheLastPublishWins_AndNothingIsFreedTwice()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out var committedBuffer);

        var replacer = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
        var setter = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
        int replaced;
        {
            var entity = replacer.OpenMut(id);
            var copy = entity.Read(CcrSingleUnit.C);
            using (var cca = entity.CreateComponentCollectionAccessor(CcrSingleUnit.C, ref copy, ref copy.Items))
            {
                cca.Replace(Replacement);
            }
            replaced = copy.Items._bufferId;
        }
        setter.OpenMut(id).Set(CcrSingleUnit.C, new CcrSingle { A = 5 });

        Assert.That(replacer.Commit(), Is.True);
        Assert.That(SingleElements(dbe, id, out _), Is.EqualTo(Replacement));
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(1), "the setter's staged value still holds the buffer it was seeded with");

        Assert.That(setter.Commit(), Is.True);
        replacer.Dispose();
        setter.Dispose();

        Assert.That(SingleElements(dbe, id, out var stored), Is.EqualTo(Seed), "the last publish wins");
        Assert.That(stored, Is.EqualTo(committedBuffer));
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(1), "HEAD names a buffer freed under it");
        Assert.That(RefCount(dbe, replaced), Is.EqualTo(0), "the overwritten replacement leaked");
    }

    // ── accessor misuse ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A copy read before an accessor moved the collection names a buffer whose reference is no longer the stored value's: opening an accessor on it is
    /// refused. Appended to, it edited a buffer an older revision owns; replaced, it released a reference the value did not hold.
    /// </summary>
    [Test]
    [VerifiesRule("EP-03")]
    public void Accessor_OnAStaleCopy_Throws()
    {
        using var dbe = SetupEngine();
        var id = SpawnVersioned(dbe, Seed, out var committedBuffer);

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var stale = entity.Read(CcrVersionedUnit.C);
            var copy = stale;
            using (var cca = entity.CreateComponentCollectionAccessor(CcrVersionedUnit.C, ref copy, ref copy.Items))
            {
                cca.Add(99);
            }

            Assert.Throws<InvalidOperationException>(() =>
            {
                var e = t.OpenMut(id);
                using var cca = e.CreateComponentCollectionAccessor(CcrVersionedUnit.C, ref stale, ref stale.Items);
                cca.Replace(Replacement);
            });
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(VersionedElements(dbe, id, out _), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 99 }));
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(0));
    }

    /// <summary>
    /// The transaction's accessor cannot tell a value being built from a copy of a stored one: a <c>Replace</c> over a collection it did not build is
    /// refused, rather than releasing — and freeing — the buffer the committed value still names.
    /// </summary>
    [Test]
    public void Replace_ThroughTheTransactionsAccessor_OnAStoredCollection_Throws()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out var committedBuffer);

        using (var t = dbe.CreateQuickTransaction())
        {
            var copy = t.Open(id).Read(CcrSingleUnit.C);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var cca = t.CreateComponentCollectionAccessor(ref copy.Items);
                cca.Clear();
            });
        }

        Assert.That(SingleElements(dbe, id, out _), Is.EqualTo(Seed));
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(1));
    }

    /// <summary>
    /// <c>Enable(comp, value)</c> supplies a component the spawn did not: a collection built for that value is adopted, as a spawn's is — the one store
    /// that may give a collection to a component holding none.
    /// </summary>
    [Test]
    public void EnableWithValue_OnAnUnsuppliedComponent_AdoptsItsCollection()
    {
        using var dbe = SetupEngine();

        EntityId id;
        int built;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<CcrPairUnit>(CcrPairUnit.V.Set(new CcrVersioned { A = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var s = new CcrSingle { A = 2 };
            using (var cca = t.CreateComponentCollectionAccessor(ref s.Items))
            {
                cca.Replace(Seed);
            }
            built = s.Items._bufferId;
            t.OpenMut(id).Enable(CcrPairUnit.S, in s);
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var s = t.Open(id).Read(CcrPairUnit.S);
            Assert.That(s.Items._bufferId, Is.EqualTo(built), "the supplied collection was dropped");
            Assert.That(Elements(t, s.Items), Is.EqualTo(Seed));
        }
        Assert.That(RefCount(dbe, built), Is.EqualTo(1));
    }

    // ── Set keeps the stored collection ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>Set</c> stores bytes and counts no reference: an empty handle in the value used to orphan the stored buffer. It now keeps the collection.
    /// </summary>
    [Test]
    [TestCase(true, TestName = "Set_WithAnEmptyHandle_KeepsTheCollection(Versioned)")]
    [TestCase(false, TestName = "Set_WithAnEmptyHandle_KeepsTheCollection(SingleVersion)")]
    [VerifiesRule("EP-03")]
    public void Set_WithAnEmptyHandle_KeepsTheCollection(bool versioned)
    {
        using var dbe = SetupEngine();
        var id = versioned ? SpawnVersioned(dbe, Seed, out var buffer) : SpawnSingle(dbe, Seed, out buffer);

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            if (versioned)
            {
                entity.Set(CcrVersionedUnit.C, new CcrVersioned { A = 5 });
            }
            else
            {
                entity.Set(CcrSingleUnit.C, new CcrSingle { A = 5 });
            }
            Assert.That(t.Commit(), Is.True);
        }

        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        int stored;
        var elements = versioned ? VersionedElements(dbe, id, out stored) : SingleElements(dbe, id, out stored);
        Assert.That(stored, Is.EqualTo(buffer), "the Set dropped the collection");
        Assert.That(elements, Is.EqualTo(Seed));
        Assert.That(RefCount(dbe, buffer), Is.EqualTo(1));

        using (var t = dbe.CreateQuickTransaction())
        {
            var a = versioned ? t.Open(id).Read(CcrVersionedUnit.C).A : t.Open(id).Read(CcrSingleUnit.C).A;
            Assert.That(a, Is.EqualTo(5), "the rest of the value is stored");
        }
    }

    /// <summary>
    /// The cluster-iteration store applies the same rule as <c>EntityRefMut.Set</c>: an empty handle keeps the collection (it used to orphan the buffer),
    /// one naming another buffer is refused before anything is stored.
    /// </summary>
    [Test]
    [VerifiesRule("EP-03")]
    public void ClusterRefSet_KeepsTheCollection_AndRefusesAForeignOne()
    {
        using var dbe = SetupEngine();
        var id = SpawnSingle(dbe, Seed, out var buffer);
        _ = SpawnSingle(dbe, Replacement, out var other);

        using (var t = dbe.CreateQuickTransaction())
        {
            var accessor = t.For<CcrSingleUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (cluster.GetEntityId(i) != id)
                    {
                        continue;
                    }

                    var foreign = new CcrSingle { A = 6, Items = new ComponentCollection<int> { _bufferId = other } };
                    try
                    {
                        cluster.Set(CcrSingleUnit.C, i, in foreign);
                        Assert.Fail("a value naming another entity's collection was stored");
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    cluster.Set(CcrSingleUnit.C, i, new CcrSingle { A = 5 });
                }
            }

            accessor.Dispose();
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(SingleElements(dbe, id, out var stored), Is.EqualTo(Seed));
        Assert.That(stored, Is.EqualTo(buffer), "the cluster store dropped the collection");
        Assert.That(RefCount(dbe, buffer), Is.EqualTo(1));
        Assert.That(RefCount(dbe, other), Is.EqualTo(1));
    }

    /// <summary>
    /// A copy read before the collection moved names a buffer whose reference belongs to the older revision. Storing it used to free that buffer under the
    /// new revision once the old one was collected; it is refused.
    /// </summary>
    [Test]
    [VerifiesRule("EP-03")]
    public void Set_WithAStaleCopy_Throws_AndKeepsTheMovedCollection()
    {
        using var dbe = SetupEngine();
        var id = SpawnVersioned(dbe, Seed, out var committedBuffer);

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            var stale = entity.Read(CcrVersionedUnit.C);
            var copy = stale;
            using (var cca = entity.CreateComponentCollectionAccessor(CcrVersionedUnit.C, ref copy, ref copy.Items))
            {
                cca.Add(99);
            }

            stale.A = 5;
            var ex = Assert.Throws<InvalidOperationException>(() => t.OpenMut(id).Set(CcrVersionedUnit.C, stale));
            Assert.That(ex.Message, Does.Contain("collection"));
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(VersionedElements(dbe, id, out _), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 99 }));
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(RefCount(dbe, committedBuffer), Is.EqualTo(0));
    }
}
