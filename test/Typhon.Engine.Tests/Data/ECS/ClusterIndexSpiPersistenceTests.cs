using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Versioned, with BOTH an indexed String64 and an indexed numeric field, so the archetype owns two index segments with different node strides (#658) — two
// durable roots to carry, not one.
[Component("Typhon.Test.ECS.SpiIdx.Named", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct SpiIdxNamed
{
    [Index] public String64 Name;
    [Index] public int Score;

    public SpiIdxNamed(String64 name, int score)
    {
        Name = name;
        Score = score;
    }
}

// SingleVersion sibling with no indexed field — its only job is to make the archetype cluster-eligible, which is what moves SpiIdxNamed's indexes off the
// ComponentTable and onto the archetype.
[Component("Typhon.Test.ECS.SpiIdx.Tag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpiIdxTag
{
    public int Marker;
    public SpiIdxTag(int marker) => Marker = marker;
}

[Archetype]
class SpiIdxArch : Archetype<SpiIdxArch>
{
    public static readonly Comp<SpiIdxNamed> Data = Register<SpiIdxNamed>();
    public static readonly Comp<SpiIdxTag> Tag = Register<SpiIdxTag>();
}

/// <summary>
/// The per-archetype index segment roots are durable pointers, and they belong in the archetype's own <see cref="ArchetypeR1"/> row — issue #661.
/// </summary>
/// <remarks>
/// <para>
/// They lived in the bootstrap dictionary under <c>clusterindex.{ArchetypeId}</c>. Every other archetype-keyed durable pointer — <c>EntityMapSPI</c>,
/// <c>ClusterSegmentSPI</c> — lives in the name-matched row, and <c>ComponentR1</c> already carried the equivalent pair for the per-ComponentTable index
/// home. The per-archetype home was the sole exception, with three consequences: the key is built from a catalog id the struct documents as not stable across
/// processes; CK-10 named the other two SPIs but not these; and each entry consumed ~22 B of a fixed 8016 B bootstrap page, an estimated ~350-archetype
/// ceiling whose overflow throws from inside <c>PersistMetaNow</c> — under <c>_metaLock</c>, mid-checkpoint.
/// </para>
/// <para>
/// <b>What these tests can and cannot prove.</b> The cross-process symptom is not reproducible here: <c>ArchetypeRegistry</c> memoizes catalog ids by name for
/// the process lifetime, so an archetype cannot receive a different id in a second open of the same process. What is testable is the mechanism — the root is
/// carried by the row, resolved by name, never consults the catalog id, survives a stale one, and is persisted independently of its former skip guard. Two of
/// the tests below are round-trip guards rather than defect repros, and say so.
/// </para>
/// </remarks>
[TestFixture]
class ClusterIndexSpiPersistenceTests : TestBase<ClusterIndexSpiPersistenceTests>
{
    private const int Count = 40;

    private static DatabaseEngine OpenEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpiIdxNamed>();
        dbe.RegisterComponentFromAccessor<SpiIdxTag>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe)
        => dbe._archetypeStates[Archetype<SpiIdxArch>.Metadata.ArchetypeId].ClusterState;

    private static string Name(int i) => $"spi_{i:D4}";

    /// <summary>Spawns <see cref="Count"/> entities across both indexed fields and closes the engine cleanly, so the SPIs are persisted.</summary>
    private void WriteSession()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < Count; i++)
            {
                tx.Spawn<SpiIdxArch>(SpiIdxArch.Data.Set(new SpiIdxNamed((String64)Name(i), i)), SpiIdxArch.Tag.Set(new SpiIdxTag(i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);
    }

    /// <summary>Resolves an entity through one of the archetype's own index trees, or null when the key is absent.</summary>
    /// <remarks>
    /// Reads the tree directly rather than issuing a query: at these entity counts the planner takes the zone-map path and never touches the B+Tree, so a
    /// query would report the component DATA and pass straight over an index that failed to load.
    /// </remarks>
    private static unsafe EntityId? IndexedEntity<TKey>(DatabaseEngine dbe, int fieldOrdinal, TKey key) where TKey : unmanaged
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var clusterState = ClusterState(dbe);
        ref var field = ref clusterState.IndexSlots[0].Fields[fieldOrdinal];

        var accessor = field.Index.Segment.CreateChunkAccessor();
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            var result = field.Index.TryGet(&key, ref accessor);
            if (!result.IsSuccess)
            {
                return null;
            }

            var clusterLocation = result.Value;
            var clusterBase = clusterAccessor.GetChunkAddress(clusterLocation >> 6);
            var raw = *(long*)(clusterBase + clusterState.Layout.EntityIdsOffset + (clusterLocation & 0x3F) * 8);
            return EntityId.FromRaw(raw);
        }
        finally
        {
            clusterAccessor.Dispose();
            accessor.Dispose();
        }
    }

    /// <summary>Reads this archetype's persisted <see cref="ArchetypeR1"/> row straight off disk, in its own session.</summary>
    private (int ChunkId, ArchetypeR1 Arch) ReadPersistedRow()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        if (dbe.GetComponentTable<ArchetypeR1>() == null)
        {
            dbe.RegisterComponentFromAccessor<ArchetypeR1>();
        }

        var table = dbe.GetComponentTable<ArchetypeR1>();
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity; chunkId++)
        {
            if (segment.IsChunkAllocated(chunkId)
                && SystemCrud.Read(table, chunkId, out ArchetypeR1 arch, dbe.EpochManager)
                && arch.Name.AsString == nameof(SpiIdxArch))
            {
                return (chunkId, arch);
            }
        }

        Assert.Fail($"no persisted ArchetypeR1 row for {nameof(SpiIdxArch)}");
        return default;
    }

    /// <summary>Rewrites this archetype's persisted row, fabricating a state the current process cannot otherwise produce.</summary>
    private void MutatePersistedRow(RefAction mutate)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        if (dbe.GetComponentTable<ArchetypeR1>() == null)
        {
            dbe.RegisterComponentFromAccessor<ArchetypeR1>();
        }

        var table = dbe.GetComponentTable<ArchetypeR1>();
        var segment = table.ComponentSegment;
        var cs = dbe.MMF.CreateChangeSet();
        for (var chunkId = 1; chunkId < segment.ChunkCapacity; chunkId++)
        {
            if (segment.IsChunkAllocated(chunkId)
                && SystemCrud.Read(table, chunkId, out ArchetypeR1 arch, dbe.EpochManager)
                && arch.Name.AsString == nameof(SpiIdxArch))
            {
                mutate(ref arch);
                SystemCrud.Update(table, chunkId, ref arch, dbe.EpochManager, cs);
                break;
            }
        }
        cs.SaveChanges();
    }

    private delegate void RefAction(ref ArchetypeR1 arch);

    /// <summary>Guards the premise: without two segments this fixture is only testing one of the two SPIs.</summary>
    [Test]
    public void Fixture_ArchetypeHasBothIndexSegments()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        Assert.That(Archetype<SpiIdxArch>.Metadata.HasClusterIndexes, Is.True, "fixture must be cluster-backed");

        var clusterState = ClusterState(dbe);
        Assert.Multiple(() =>
        {
            Assert.That(clusterState.IndexSegment, Is.Not.Null, "the 256-byte-stride segment carries the numeric index");
            Assert.That(clusterState.IndexSegmentString64, Is.Not.Null, "the wider-stride segment carries the String64 index");
        });
    }

    /// <summary>
    /// AC: reopen resolves both index segments from the archetype row. A <b>round-trip guard</b>, not a defect repro — the bootstrap scheme also round-trips
    /// within one process, since the catalog id it keys on is stable there.
    /// </summary>
    [Test]
    public void Reopen_LoadsBothIndexSegmentsFromTheArchetypeRow()
    {
        WriteSession();

        var persisted = ReadPersistedRow();
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Arch.ClusterIndexSPI, Is.GreaterThan(0), "the numeric index root must be recorded in the row");
            Assert.That(persisted.Arch.ClusterString64IndexSPI, Is.GreaterThan(0), "the String64 index root must be recorded in the row");
            Assert.That(persisted.Arch.ClusterIndexSPI, Is.Not.EqualTo(persisted.Arch.ClusterString64IndexSPI), "two segments, two distinct roots");
        });

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        // Without this the rest proves nothing: a rebuild from cluster data also produces correct trees, so correct query answers are not evidence that the
        // persisted roots were found.
        Assert.That(dbe.LastOpenClusterIndexRebuildCount, Is.EqualTo(0), "the reopen must LOAD both persisted segments, not rebuild them");

        for (var i = 0; i < Count; i++)
        {
            Assert.That(IndexedEntity(dbe, 0, (String64)Name(i)), Is.Not.Null, $"String64 key {Name(i)} must resolve after reopen");
            Assert.That(IndexedEntity(dbe, 1, i), Is.Not.Null, $"numeric key {i} must resolve after reopen");
        }
    }

    /// <summary>
    /// AC: the bootstrap keys are retired — deleted, not merely unread. Every surviving entry occupies part of a fixed 8016-byte page whose overflow throws
    /// mid-checkpoint, so leaving dead keys behind would preserve the ceiling for exactly the databases the move was meant to relieve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A database created by this build never has these keys, so asserting their absence after a plain round-trip would pass without the removal code ever
    /// running. The middle session seeds them by hand, fabricating the pre-#661 database that is the only thing there is to migrate. The control key — a
    /// bootstrap key outside the retired prefixes — is what separates "removed" from "never survived the close".
    /// </para>
    /// <para>
    /// The stale id is not a stray: the sweep is by PREFIX, so it also reclaims entries written under catalog ids this process no longer assigns — which is
    /// the only way a database whose numbering has shifted can be cleaned at all.
    /// </para>
    /// </remarks>
    [Test]
    public void Reopen_LegacyBootstrapKeysAreRetired()
    {
        WriteSession();

        const string controlKey = "spitest.control";
        var id = Archetype<SpiIdxArch>.Metadata.ArchetypeId;
        const int staleId = 4094;   // an id this process never assigns — stands in for a database written under different catalog numbering

        using (var seedScope = ServiceProvider.CreateScope())
        {
            using var seedDbe = OpenEngine(seedScope);
            seedDbe.MMF.Bootstrap.SetInt($"clusterindex.{id}", 4242);
            seedDbe.MMF.Bootstrap.SetInt($"clusterindexs64.{id}", 4343);
            seedDbe.MMF.Bootstrap.SetInt($"clusterindex.{staleId}", 4545);
            seedDbe.MMF.Bootstrap.SetInt(controlKey, 4444);
        }

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        Assert.Multiple(() =>
        {
            Assert.That(dbe.MMF.Bootstrap.ContainsKey(controlKey), Is.True, "control: a seeded bootstrap key must survive the close, or the rest is vacuous");
            Assert.That(dbe.MMF.Bootstrap.ContainsKey($"clusterindex.{id}"), Is.False, "the legacy numeric-index key must be removed");
            Assert.That(dbe.MMF.Bootstrap.ContainsKey($"clusterindexs64.{id}"), Is.False, "the legacy String64-index key must be removed");
            Assert.That(dbe.MMF.Bootstrap.ContainsKey($"clusterindex.{staleId}"), Is.False,
                "a key under a catalog id this process never assigns must go too — otherwise a shifted database keeps it forever");
        });
    }

    /// <summary>
    /// AC: reopen is unaffected by the persisted <see cref="ArchetypeR1.ArchetypeId"/>. A <b>round-trip guard</b>: it fabricates the row state a process with
    /// different catalog numbering would leave behind, but cannot change this process's own catalog id, which is what the bootstrap key was built from.
    /// </summary>
    [Test]
    public void Reopen_WithStalePersistedArchetypeId_StillResolves()
    {
        WriteSession();

        var before = ReadPersistedRow().Arch;
        MutatePersistedRow((ref ArchetypeR1 a) => a.ArchetypeId = 4094);

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        Assert.That(dbe.LastOpenClusterIndexRebuildCount, Is.EqualTo(0), "resolution is by Name — a stale catalog id must not cost a rebuild");

        // Read the live segments rather than re-reading the row: only one engine may hold the database at a time, and the loaded roots are the more direct
        // evidence anyway — they are what the row's SPIs were supposed to point at.
        var clusterState = ClusterState(dbe);
        Assert.Multiple(() =>
        {
            Assert.That(clusterState.IndexSegment.RootPageIndex, Is.EqualTo(before.ClusterIndexSPI), "the same numeric-index segment must be reused");
            Assert.That(clusterState.IndexSegmentString64.RootPageIndex, Is.EqualTo(before.ClusterString64IndexSPI),
                "the same String64-index segment must be reused");
        });

        for (var i = 0; i < Count; i++)
        {
            Assert.That(IndexedEntity(dbe, 0, (String64)Name(i)), Is.Not.Null, $"String64 key {Name(i)} must resolve");
            Assert.That(IndexedEntity(dbe, 1, i), Is.Not.Null, $"numeric key {i} must resolve");
        }
    }

    /// <summary>
    /// AC: the index SPIs are persisted on any cycle where THEY changed, independent of the EntityMap / cluster-segment / NextEntityKey skip guard they used
    /// to sit below.
    /// </summary>
    /// <remarks>
    /// Zeroing the row's index SPIs forces the next open to allocate fresh segments and rebuild — so on that open the index roots change while the other
    /// three guard fields do not. If the index SPIs were written below the guard rather than being part of it, that open would persist nothing and the one
    /// after it would rebuild all over again, forever.
    /// </remarks>
    [Test]
    public void Checkpoint_IndexSpiChangedAlone_IsStillPersisted()
    {
        WriteSession();
        var original = ReadPersistedRow().Arch;

        MutatePersistedRow((ref ArchetypeR1 a) =>
        {
            a.ClusterIndexSPI = 0;
            a.ClusterString64IndexSPI = 0;
        });

        // Open (allocates fresh segments, rebuilds the trees) and close. Only the index roots changed this cycle.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenEngine(scope);
            Assert.That(dbe.LastOpenClusterIndexRebuildCount, Is.GreaterThan(0), "zeroed roots must force a rebuild — otherwise nothing changed this cycle");
        }

        var rewritten = ReadPersistedRow().Arch;
        Assert.Multiple(() =>
        {
            Assert.That(rewritten.ClusterIndexSPI, Is.GreaterThan(0), "the new numeric-index root must have been persisted");
            Assert.That(rewritten.ClusterString64IndexSPI, Is.GreaterThan(0), "the new String64-index root must have been persisted");
            Assert.That(rewritten.EntityMapSPI, Is.EqualTo(original.EntityMapSPI), "the guard's other fields must be unchanged — that is the point");
            Assert.That(rewritten.ClusterSegmentSPI, Is.EqualTo(original.ClusterSegmentSPI));
            Assert.That(rewritten.NextEntityKey, Is.EqualTo(original.NextEntityKey));
        });

        // And the proof it stuck: the NEXT open loads rather than rebuilding again.
        using var verifyScope = ServiceProvider.CreateScope();
        using var verifyDbe = OpenEngine(verifyScope);
        Assert.That(verifyDbe.LastOpenClusterIndexRebuildCount, Is.EqualTo(0), "the re-persisted roots must be found on the next open");

        for (var i = 0; i < Count; i++)
        {
            Assert.That(IndexedEntity(verifyDbe, 0, (String64)Name(i)), Is.Not.Null, $"String64 key {Name(i)} must resolve");
            Assert.That(IndexedEntity(verifyDbe, 1, i), Is.Not.Null, $"numeric key {i} must resolve");
        }
    }
}
