using System.Linq;
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// String1024 out of line (#1094). One archetype per storage mode, each with a component holding TWO String1024 fields: before #1094 the inline 1 024 bytes
// made every one of them fail InitializeArchetypes (N = 8 entities per cluster no longer fits a page). The text now lives in a 1 024-byte chunk of a text
// segment and the component keeps a 4-byte handle, so the archetype clusters like any small one.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Text.Sv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxtSv
{
    [Field] public int Id;
    [Field] public String1024 Title;
    [Field] public String1024 Body;
}

[Component("Typhon.Test.Text.V", 1)] // Versioned (default)
[StructLayout(LayoutKind.Sequential)]
struct TxtV
{
    [Field] public int Id;
    [Field] public String1024 Title;
    [Field] public String1024 Body;
}

[Component("Typhon.Test.Text.T", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct TxtT
{
    [Field] public int Id;
    [Field] public String1024 Title;
    [Field] public String1024 Body;
}

[Archetype]
partial class TxtSvUnit : Archetype<TxtSvUnit>
{
    public static readonly Comp<TxtSv> Doc = Register<TxtSv>();
}

[Archetype]
partial class TxtVUnit : Archetype<TxtVUnit>
{
    public static readonly Comp<TxtV> Doc = Register<TxtV>();
}

[Archetype]
partial class TxtTUnit : Archetype<TxtTUnit>
{
    public static readonly Comp<TxtT> Doc = Register<TxtT>();
}

// ── Refused declarations: a String1024 is only ever a top-level, single, unindexed field ──

[Component("Typhon.Test.Text.Indexed", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxtIndexed
{
    [Field] [Index] public String1024 Name;
}

[StructLayout(LayoutKind.Sequential)]
struct TxtElement
{
    public int A;
    public String1024 Note;
}

[Component("Typhon.Test.Text.InCollection", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxtInCollection
{
    [Field] public ComponentCollection<TxtElement> Items;
}

[Component("Typhon.Test.Text.Nested", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxtNestedInner
{
    [Field] public String1024 Inner;
}

[Component("Typhon.Test.Text.Outer", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxtNestedOuter
{
    [Field] public TxtNestedInner Nested;
}

// ── An archetype whose inline data cannot fit eight entities per page: 16 × String64 = 1 024 bytes per entity ──

// Never spawned: the archetype exists to fail InitializeArchetypes. TYPHON013 refuses spawning a component past 112 bytes, which this one is on purpose.
#pragma warning disable TYPHON013
[Component("Typhon.Test.Text.Wide", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxtWide
{
    [Field] public String64 S0;
    [Field] public String64 S1;
    [Field] public String64 S2;
    [Field] public String64 S3;
    [Field] public String64 S4;
    [Field] public String64 S5;
    [Field] public String64 S6;
    [Field] public String64 S7;
    [Field] public String64 S8;
    [Field] public String64 S9;
    [Field] public String64 S10;
    [Field] public String64 S11;
    [Field] public String64 S12;
    [Field] public String64 S13;
    [Field] public String64 S14;
    [Field] public String64 S15;
}

[Archetype]
partial class TxtWideUnit : Archetype<TxtWideUnit>
{
    public static readonly Comp<TxtWide> W = Register<TxtWide>();
}
#pragma warning restore TYPHON013

[TestFixture]
[NonParallelizable]
class TextFieldTests : TestBase<TextFieldTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TxtSv>();
        dbe.RegisterComponentFromAccessor<TxtV>();
        dbe.RegisterComponentFromAccessor<TxtT>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static string Long(char c, int n) => new(c, n);

    [Test]
    public void EveryStorageMode_InitializesAnArchetypeWithTwoTextFields()
    {
        using var dbe = SetupEngine();
        foreach (var meta in new[] { Archetype<TxtSvUnit>.Metadata, Archetype<TxtVUnit>.Metadata, Archetype<TxtTUnit>.Metadata })
        {
            var layout = dbe._archetypeStates[meta.ArchetypeId].ClusterState.Layout;
            // Three 4-byte fields: the same cluster as a component of three ints, the densest the page allows.
            Assert.That(layout.ClusterSize, Is.EqualTo(ArchetypeClusterInfo.Compute(1, [12]).ClusterSize), meta.ArchetypeType.Name);
        }

        Assert.That(dbe.GetComponentTable<TxtSv>().HasText, Is.True);
        Assert.That(dbe.GetComponentTable<TxtSv>().TextSegment, Is.Not.Null);
        Assert.That(dbe.GetComponentTable<TxtT>().TransientTextSegment, Is.Not.Null);
    }

    [Test]
    public void SingleVersion_SetOverwriteClearDestroy_ReusesOneChunkAndGivesItBack()
    {
        using var dbe = SetupEngine();
        var segment = dbe.GetComponentTable<TxtSv>().TextSegment;
        var baseline = segment.AllocatedChunkCount;

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv { Id = 7 }));
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(baseline), "a spawn with no text costs no chunk");

        int first;
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtSvUnit.Doc);
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Title, "hello");
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Body, Long('b', 2000));
            first = v.Title._chunkId;
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(baseline + 2));
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtSvUnit.Doc);
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Title), Is.EqualTo("hello"));
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Body), Is.EqualTo(Long('b', String1024.MaxUtf8Bytes)), "cut at 1 023 bytes");
            Assert.That(v.Id, Is.EqualTo(7));
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtSvUnit.Doc);
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Title, "world");
            Assert.That(v.Title._chunkId, Is.EqualTo(first), "in place: the same chunk");
            e.ClearText(TxtSvUnit.Doc, ref v, ref v.Body);
            Assert.That(v.Body.IsEmpty, Is.True);
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(segment.AllocatedChunkCount - segment.QuarantinedFreeCount, Is.EqualTo(baseline + 1),
            "the cleared chunk is freed (held until a checkpoint)");
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtSvUnit.Doc);
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Title), Is.EqualTo("world"));
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Body), Is.Empty);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            Assert.That(t.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        Assert.That(segment.AllocatedChunkCount - segment.QuarantinedFreeCount, Is.EqualTo(baseline), "destroy gives the last chunk back");
    }

    [Test]
    public void Transient_SetOverwriteClearDestroy_ReusesOneChunkAndGivesItBack()
    {
        using var dbe = SetupEngine();
        var segment = dbe.GetComponentTable<TxtT>().TransientTextSegment;
        var baseline = segment.AllocatedChunkCount;

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtTUnit>(TxtTUnit.Doc.Set(new TxtT { Id = 3 }));
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtTUnit.Doc);
            e.SetText(TxtTUnit.Doc, ref v, ref v.Title, "é😀x");
            var first = v.Title._chunkId;
            e.SetText(TxtTUnit.Doc, ref v, ref v.Title, "again");
            Assert.That(v.Title._chunkId, Is.EqualTo(first));
            Assert.That(e.ReadText(TxtTUnit.Doc, in v.Title), Is.EqualTo("again"));
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(baseline + 1));
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            Assert.That(t.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(baseline), "destroy gives the chunk back");
    }

    [Test]
    public void Versioned_OlderSnapshotReadsTheOldText()
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtVUnit>(TxtVUnit.Doc.Set(new TxtV { Id = 1 }));
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtVUnit.Doc);
            e.SetText(TxtVUnit.Doc, ref v, ref v.Title, "first");
            Assert.That(t.Commit(), Is.True);
        }

        using var reader = dbe.CreateQuickTransaction();
        var old = reader.Open(id).Read(TxtVUnit.Doc);

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtVUnit.Doc);
            var before = v.Title._chunkId;
            e.SetText(TxtVUnit.Doc, ref v, ref v.Title, "second");
            Assert.That(v.Title._chunkId, Is.Not.EqualTo(before), "copy on write: a new chunk");
            var again = v.Title._chunkId;
            e.SetText(TxtVUnit.Doc, ref v, ref v.Title, "third");
            Assert.That(v.Title._chunkId, Is.EqualTo(again), "the transaction's own chunk is rewritten in place");
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(reader.Open(id).ReadText(TxtVUnit.Doc, in old.Title), Is.EqualTo("first"), "the older snapshot keeps its text");
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtVUnit.Doc);
            Assert.That(e.ReadText(TxtVUnit.Doc, in v.Title), Is.EqualTo("third"));
        }
    }

    private static int LiveChunks(Typhon.Engine.Internals.ChunkBasedSegment<Typhon.Engine.Internals.PersistentStore> segment) =>
        segment.AllocatedChunkCount - segment.QuarantinedFreeCount;

    [Test]
    public void CommitDiscipline_HeadKeepsItsTextUntilCommit_PublishFreesTheReplacedOne()
    {
        using var dbe = SetupEngine();
        var segment = dbe.GetComponentTable<TxtSv>().TextSegment;
        var baseline = LiveChunks(segment);
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtSvUnit.Doc);
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Title, "committed");
            Assert.That(t.Commit(), Is.True);
        }

        Assert.That(LiveChunks(segment), Is.EqualTo(baseline + 1));
        using (var writer = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            var e = writer.OpenMut(id);
            var v = e.Read(TxtSvUnit.Doc);
            var head = v.Title._chunkId;
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Title, "staged");
            Assert.That(v.Title._chunkId, Is.Not.EqualTo(head), "HEAD's chunk is not touched before commit (CM-01)");
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Title), Is.EqualTo("staged"), "read your own write");

            using (var other = dbe.CreateQuickTransaction())
            {
                var o = other.Open(id);
                var ov = o.Read(TxtSvUnit.Doc);
                Assert.That(o.ReadText(TxtSvUnit.Doc, in ov.Title), Is.EqualTo("committed"), "another transaction reads HEAD");
            }

            Assert.That(writer.Commit(), Is.True);
        }

        Assert.That(LiveChunks(segment), Is.EqualTo(baseline + 1), "publish freed HEAD's replaced chunk");
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtSvUnit.Doc);
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Title), Is.EqualTo("staged"));
        }

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtSvUnit.Doc);
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Title, "rolled back");
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Body, "rolled back too");
            Assert.That(LiveChunks(segment), Is.EqualTo(baseline + 3));
            t.Rollback();
        }

        Assert.That(LiveChunks(segment), Is.EqualTo(baseline + 1), "rollback freed the transaction's chunks");
        Assert.That(segment.DoubleFreeCount, Is.Zero);
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtSvUnit.Doc);
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Title), Is.EqualTo("staged"));
            Assert.That(v.Body.IsEmpty, Is.True);
        }
    }

    [Test]
    public void Versioned_RollbackFreesOnlyTheTransactionsChunks()
    {
        using var dbe = SetupEngine();
        var segment = dbe.GetComponentTable<TxtV>().TextSegment;
        var baseline = LiveChunks(segment);
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtVUnit>(TxtVUnit.Doc.Set(new TxtV()));
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtVUnit.Doc);
            e.SetText(TxtVUnit.Doc, ref v, ref v.Title, "kept");
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtVUnit.Doc);
            v.Id = 99;
            e.Set(TxtVUnit.Doc, v);                                  // shares Title's chunk with the committed revision
            e.SetText(TxtVUnit.Doc, ref v, ref v.Body, "dropped");   // a chunk of its own
            t.Rollback();
        }

        Assert.That(LiveChunks(segment), Is.EqualTo(baseline + 1));
        Assert.That(segment.DoubleFreeCount, Is.Zero);
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtVUnit.Doc);
            Assert.That(e.ReadText(TxtVUnit.Doc, in v.Title), Is.EqualTo("kept"));
        }
    }

    [Test]
    [VerifiesRule("TXT-01")]
    public void Versioned_RevisionCleanupFreesEachReplacedTextOnce()
    {
        using var dbe = SetupEngine();
        var segment = dbe.GetComponentTable<TxtV>().TextSegment;
        var baseline = LiveChunks(segment);
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtVUnit>(TxtVUnit.Doc.Set(new TxtV()));
            Assert.That(t.Commit(), Is.True);
        }

        // Text changes interleaved with writes that keep it: each text is shared by a run of revisions, and must be freed once, when the run is gone.
        for (var i = 0; i < 6; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var e = t.OpenMut(id);
            var v = e.Read(TxtVUnit.Doc);
            if (i % 2 == 0)
            {
                e.SetText(TxtVUnit.Doc, ref v, ref v.Title, $"text {i}");
            }
            else
            {
                v.Id = i;
                e.Set(TxtVUnit.Doc, v);
            }

            Assert.That(t.Commit(), Is.True);
        }

        // Drain the cleanup: no transaction is alive, so every superseded revision can go.
        for (var i = 0; i < 3; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var e = t.OpenMut(id);
            var v = e.Read(TxtVUnit.Doc);
            v.Id = 100 + i;
            e.Set(TxtVUnit.Doc, v);
            Assert.That(t.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        Assert.That(LiveChunks(segment), Is.EqualTo(baseline + 1), "only the current text is left");
        Assert.That(segment.DoubleFreeCount, Is.Zero);
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtVUnit.Doc);
            Assert.That(e.ReadText(TxtVUnit.Doc, in v.Title), Is.EqualTo("text 4"));
        }
    }

    [Test]
    [VerifiesRule("TXT-02")]
    public void Spawn_WithATextHandle_IsRefused()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();
        var forged = new TxtSv();
        forged.Title._chunkId = 1;
        Assert.Throws<InvalidOperationException>(() => t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(in forged)));
    }

    [Test]
    [VerifiesRule("TXT-02")]
    public void Set_CarryingAnotherEntitysText_IsRefused()
    {
        using var dbe = SetupEngine();
        EntityId a, b;
        using (var t = dbe.CreateQuickTransaction())
        {
            a = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            b = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            Assert.That(t.Commit(), Is.True);
        }

        using var tx = dbe.CreateQuickTransaction();
        var ea = tx.OpenMut(a);
        var va = ea.Read(TxtSvUnit.Doc);
        ea.SetText(TxtSvUnit.Doc, ref va, ref va.Title, "a's");
        var vb = tx.OpenMut(b).Read(TxtSvUnit.Doc);
        vb.Title = va.Title;
        Assert.Throws<InvalidOperationException>(() => tx.OpenMut(b).Set(TxtSvUnit.Doc, vb), "two entities would own one chunk");

        // A value carrying an empty handle keeps the stored text.
        var keep = ea.Read(TxtSvUnit.Doc);
        keep.Title = default;
        keep.Id = 5;
        ea.Set(TxtSvUnit.Doc, keep);
        var back = ea.Read(TxtSvUnit.Doc);
        Assert.That(ea.ReadText(TxtSvUnit.Doc, in back.Title), Is.EqualTo("a's"));
    }

    [Test]
    public void Registration_RefusesAMisplacedString1024()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Assert.That(Assert.Throws<InvalidOperationException>(() => dbe.RegisterComponentFromAccessor<TxtIndexed>()).Message, Does.Contain("cannot be indexed"));
        Assert.That(Assert.Throws<InvalidOperationException>(() => dbe.RegisterComponentFromAccessor<TxtInCollection>()).Message,
            Does.Contain("collection whose element"));
        Assert.That(Assert.Throws<InvalidOperationException>(() => dbe.RegisterComponentFromAccessor<TxtNestedOuter>()).Message,
            Does.Contain("nested component"));
    }

    [Test]
    public void AnArchetypeTooLargeForACluster_IsNamedWithItsComponents()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TxtWide>();
        var e = Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
        Assert.That(e.Message, Does.Contain("TxtWideUnit").And.Contain("1024 B").And.Contain("String1024"));
    }

    [Test]
    public void Versioned_Destroy_ReclaimsTheTextThroughTheRevisionCleanup()
    {
        using var dbe = SetupEngine();
        var segment = dbe.GetComponentTable<TxtV>().TextSegment;
        var baseline = LiveChunks(segment);
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtVUnit>(TxtVUnit.Doc.Set(new TxtV()));
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtVUnit.Doc);
            e.SetText(TxtVUnit.Doc, ref v, ref v.Title, "gone soon");
            e.SetText(TxtVUnit.Doc, ref v, ref v.Body, "this too");
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            Assert.That(t.Commit(), Is.True);
        }

        // No transaction is alive: the destroyed entity is past every snapshot, and its chain — texts included — is released whole.
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(LiveChunks(segment), Is.EqualTo(baseline), "the destroyed entity's texts are given back");
        Assert.That(segment.DoubleFreeCount, Is.Zero);
    }

    [Test]
    public void ClusterIteration_ReadsTextsThroughTheClusterRef()
    {
        using var dbe = SetupEngine();
        using (var t = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 20; i++)
            {
                var id = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv { Id = i }));
                Assert.That(id.IsNull, Is.False);
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            foreach (var entity in t.Query<TxtSvUnit>())
            {
                var e = t.OpenMut(entity.Id);
                var v = e.Read(TxtSvUnit.Doc);
                e.SetText(TxtSvUnit.Doc, ref v, ref v.Title, $"title {v.Id}");
            }

            Assert.That(t.Commit(), Is.True);
        }

        var seen = 0;
        using (var t = dbe.CreateQuickTransaction())
        {
            foreach (var cluster in t.GetClusterEnumerator<TxtSvUnit>())
            {
                var docs = cluster.GetReadOnlySpan(TxtSvUnit.Doc);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    Assert.That(cluster.ReadText(TxtSvUnit.Doc, in docs[i].Title), Is.EqualTo($"title {docs[i].Id}"));
                    seen++;
                }
            }
        }

        Assert.That(seen, Is.EqualTo(20));
    }

    [Test]
    [VerifiesRule("TXT-01")]
    public void IntegrityCheck_FindsNoTextIssueOnAHealthyDatabase_AndBothWhenBroken()
    {
        using var dbe = SetupEngine();
        EntityId sv, v;
        using (var t = dbe.CreateQuickTransaction())
        {
            sv = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            v = t.Spawn<TxtVUnit>(TxtVUnit.Doc.Set(new TxtV()));
            Assert.That(t.Commit(), Is.True);
        }

        int svChunk;
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(sv);
            var value = e.Read(TxtSvUnit.Doc);
            e.SetText(TxtSvUnit.Doc, ref value, ref value.Title, "sv");
            e.SetText(TxtSvUnit.Doc, ref value, ref value.Body, "cleared");
            e.ClearText(TxtSvUnit.Doc, ref value, ref value.Body);
            svChunk = value.Title._chunkId;
            var ev = t.OpenMut(v);
            var vv = ev.Read(TxtVUnit.Doc);
            ev.SetText(TxtVUnit.Doc, ref vv, ref vv.Title, "versioned");
            Assert.That(t.Commit(), Is.True);
        }

        static bool HasText(StorageIntegrityReport r, StorageIntegrityIssueKind k) => r.Issues.Any(i => i.Kind == k);
        var healthy = dbe.RunStorageIntegrityCheck();
        Assert.That(HasText(healthy, StorageIntegrityIssueKind.TextHandleDangling) || HasText(healthy, StorageIntegrityIssueKind.TextChunkOrphan), Is.False,
            string.Join("; ", healthy.Issues.Select(i => i.Detail)));

        // Broken on purpose: a chunk nobody names, and a chunk freed under the row that names it.
        var segment = dbe.GetComponentTable<TxtSv>().TextSegment;
        segment.AllocateChunk(false);
        segment.FreeChunkUnheld(svChunk);
        var broken = dbe.RunStorageIntegrityCheck();
        Assert.That(HasText(broken, StorageIntegrityIssueKind.TextHandleDangling), Is.True);
        Assert.That(HasText(broken, StorageIntegrityIssueKind.TextChunkOrphan), Is.True);
    }

    [Test]
    [VerifiesRule("TXT-01")]
    public void CommitDiscipline_TwoPublishesOverOneEntity_KeepHeadOnALiveChunk()
    {
        using var dbe = SetupEngine();
        var segment = dbe.GetComponentTable<TxtSv>().TextSegment;
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtSvUnit.Doc);
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Title, "first");
            Assert.That(t.Commit(), Is.True);
        }

        // A sets the text, B changes another field; both staged against the same HEAD, A publishes first. B's staged value still names HEAD's old chunk,
        // which A's publish freed: B must publish HEAD's current text, not the one it staged.
        using var a = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit);
        using var b = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit);
        var ea = a.OpenMut(id);
        var va = ea.Read(TxtSvUnit.Doc);
        ea.SetText(TxtSvUnit.Doc, ref va, ref va.Title, "from A");
        var eb = b.OpenMut(id);
        var vb = eb.Read(TxtSvUnit.Doc);
        vb.Id = 42;
        eb.Set(TxtSvUnit.Doc, vb);
        Assert.That(a.Commit(), Is.True);
        Assert.That(b.Commit(), Is.True);

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtSvUnit.Doc);
            Assert.That(v.Id, Is.EqualTo(42));
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Title), Is.EqualTo("from A"), "B did not touch the text: HEAD's current one stands");
            Assert.That(segment.IsChunkAllocated(v.Title._chunkId) && !segment.IsFreeHeld(v.Title._chunkId), Is.True, "HEAD names a live chunk");
        }

        Assert.That(LiveChunks(segment), Is.EqualTo(2), "chunk 0 and the one text");
        Assert.That(segment.DoubleFreeCount, Is.Zero);
    }

    [Test]
    [VerifiesRule("TXT-02")]
    public void SpawnBatch_WithASharedTextHandle_IsRefused()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();
        var shared = new TxtSv();
        shared.Body._chunkId = 1;
        var ids = new EntityId[4];
        Assert.Throws<InvalidOperationException>(() => t.SpawnBatch<TxtSvUnit>(ids, TxtSvUnit.Doc.Set(in shared)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ASpawnThatNeverLands_GivesItsTextsBack(bool destroyInsteadOfRollback)
    {
        using var dbe = SetupEngine();
        var svSegment = dbe.GetComponentTable<TxtSv>().TextSegment;
        var transient = dbe.GetComponentTable<TxtT>().TransientTextSegment;
        var svBaseline = LiveChunks(svSegment);
        var tBaseline = transient.AllocatedChunkCount;
        using (var t = dbe.CreateQuickTransaction())
        {
            var sv = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            var v = t.Spawn<TxtVUnit>(TxtVUnit.Doc.Set(new TxtV()));
            var tr = t.Spawn<TxtTUnit>(TxtTUnit.Doc.Set(new TxtT()));

            var e1 = t.OpenMut(sv);
            var x1 = e1.Read(TxtSvUnit.Doc);
            e1.SetText(TxtSvUnit.Doc, ref x1, ref x1.Title, "sv");
            var e2 = t.OpenMut(v);
            var x2 = e2.Read(TxtVUnit.Doc);
            e2.SetText(TxtVUnit.Doc, ref x2, ref x2.Title, "v");
            var e3 = t.OpenMut(tr);
            var x3 = e3.Read(TxtTUnit.Doc);
            e3.SetText(TxtTUnit.Doc, ref x3, ref x3.Title, "t");

            if (destroyInsteadOfRollback)
            {
                t.Destroy(sv);
                t.Destroy(v);
                t.Destroy(tr);
                Assert.That(t.Commit(), Is.True);
            }
            else
            {
                t.Rollback();
            }
        }

        dbe.FlushDeferredCleanups();
        Assert.That(LiveChunks(svSegment), Is.EqualTo(svBaseline), "the persistent texts are given back");
        Assert.That(transient.AllocatedChunkCount, Is.EqualTo(tBaseline), "the Transient one too");
        Assert.That(svSegment.DoubleFreeCount, Is.Zero);
    }

    /// <summary>
    /// A conflict handler that keeps the committed value drops the text this transaction set: the new revision names the committed chunk, and the chunk the
    /// transaction took is named by nothing (TXT-01). Without the release in ResolveConflictUnderLock the commit adopted it and it leaked.
    /// </summary>
    [Test]
    [VerifiesRule("TXT-01")]
    public void Versioned_AConflictHandlerKeepingTheCommittedText_FreesTheTransactionsOwn()
    {
        using var dbe = SetupEngine();
        var segment = dbe.GetComponentTable<TxtV>().TextSegment;
        var baseline = LiveChunks(segment);
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtVUnit>(TxtVUnit.Doc.Set(new TxtV()));
            Assert.That(t.Commit(), Is.True);
        }

        using var mine = dbe.CreateQuickTransaction();
        var em = mine.OpenMut(id);
        var vm = em.Read(TxtVUnit.Doc);
        em.SetText(TxtVUnit.Doc, ref vm, ref vm.Title, "mine");

        using (var theirs = dbe.CreateQuickTransaction())
        {
            var et = theirs.OpenMut(id);
            var vt = et.Read(TxtVUnit.Doc);
            et.SetText(TxtVUnit.Doc, ref vt, ref vt.Title, "theirs");
            Assert.That(theirs.Commit(), Is.True);
        }

        var handled = false;
        void KeepCommitted(ref ConcurrencyConflictSolver solver)
        {
            handled = true;
            solver.ToCommitData<TxtV>() = solver.CommittedData<TxtV>();
        }

        Assert.That(mine.Commit(KeepCommitted), Is.True);
        Assert.That(handled, Is.True, "the two writes conflict");

        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.Open(id);
            var v = e.Read(TxtVUnit.Doc);
            Assert.That(e.ReadText(TxtVUnit.Doc, in v.Title), Is.EqualTo("theirs"));
        }

        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(LiveChunks(segment), Is.EqualTo(baseline + 1), "only the committed text is left: the one the handler dropped is freed");
        Assert.That(segment.DoubleFreeCount, Is.Zero);
    }

    [Test]
    public void GetSpan_OnAComponentWithAText_IsRefused()
    {
        using var dbe = SetupEngine();
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            Assert.That(t.Commit(), Is.True);
        }

        using var tx = dbe.CreateQuickTransaction();
        var refused = false;
        foreach (var cluster in tx.GetClusterEnumerator<TxtSvUnit>())
        {
            try
            {
#pragma warning disable TYPHON009
                cluster.GetSpan(TxtSvUnit.Doc);
#pragma warning restore TYPHON009
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
        }

        Assert.That(refused, Is.True, "a store through the span could drop or copy a text handle");
    }

    [Test]
    public void Utf8Overload_CutsOnACodePointBoundary()
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            Assert.That(t.Commit(), Is.True);
        }

        // 1 022 ASCII bytes then a 2-byte 'é': the 'é' straddles the 1 023-byte limit and must be dropped whole.
        var utf8 = Encoding.UTF8.GetBytes(Long('a', 1022) + "é");
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(id);
            var v = e.Read(TxtSvUnit.Doc);
            e.SetText(TxtSvUnit.Doc, ref v, ref v.Title, utf8);
            Span<byte> back = stackalloc byte[String1024.MaxUtf8Bytes];
            Assert.That(e.ReadText(TxtSvUnit.Doc, in v.Title, back), Is.EqualTo(1022));
            Assert.That(t.Commit(), Is.True);
        }
    }

    [Test]
    public void SetText_StaleCopy_IsRefused()
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<TxtSvUnit>(TxtSvUnit.Doc.Set(new TxtSv()));
            Assert.That(t.Commit(), Is.True);
        }

        using var tx = dbe.CreateQuickTransaction();
        var stale = tx.OpenMut(id).Read(TxtSvUnit.Doc);
        var fresh = tx.OpenMut(id).Read(TxtSvUnit.Doc);
        tx.OpenMut(id).SetText(TxtSvUnit.Doc, ref fresh, ref fresh.Title, "x");
        Assert.Throws<InvalidOperationException>(() => tx.OpenMut(id).SetText(TxtSvUnit.Doc, ref stale, ref stale.Title, "y"));
    }
}
