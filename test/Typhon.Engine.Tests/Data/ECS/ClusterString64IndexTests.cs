using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Versioned — String64 indexes are legal here. Its archetype is cluster-eligible (see the SV sibling below), so its indexes live on the
// ARCHETYPE, which is what needs the wider-stride second segment.
[Component("Typhon.Test.ECS.ClusterS64.Named", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ClusterS64Named
{
    [Index] public String64 Name;
    [Index] public int Score;

    public ClusterS64Named(String64 name, int score)
    {
        Name = name;
        Score = score;
    }
}

// SingleVersion sibling with no indexed field — its only job is to make the archetype cluster-eligible, which is what moves
// ClusterS64Named's indexes off the ComponentTable and onto the archetype.
[Component("Typhon.Test.ECS.ClusterS64.Tag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClusterS64Tag
{
    public int Marker;
    public ClusterS64Tag(int marker) => Marker = marker;
}

[Archetype]
class ClusterS64Arch : Archetype<ClusterS64Arch>
{
    public static readonly Comp<ClusterS64Named> Data = Register<ClusterS64Named>();
    public static readonly Comp<ClusterS64Tag> Tag = Register<ClusterS64Tag>();
}

// SingleVersion WITH an indexed String64 field — rejected at registration (see String64Index_OnInPlaceStorageMode_IsRejected).
[Component("Typhon.Test.ECS.ClusterS64.Illegal", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClusterS64Illegal
{
    [Index] public String64 Name;
}

/// <summary>
/// <c>String64</c> secondary indexes on CLUSTER-BACKED archetypes — issue #658.
/// </summary>
/// <remarks>
/// <para>
/// A segment serves exactly one node size; every B+Tree variant asserts <c>segment.Stride == sizeof(its node)</c>. The
/// <c>Index16/32/64Chunk</c> layouts are all 256 bytes, so one segment covers every numeric key type — but an
/// <c>IndexString64Chunk</c> is wider. The cluster path allocated only the 256-byte segment and handed it to every field type, so
/// indexing a <c>String64</c> field on a cluster-backed archetype tripped the assert in Debug and would have written past the chunk
/// into its neighbour in Release, where the assert is compiled out.
/// </para>
/// <para>
/// The fixture is deliberately a MIXED archetype — Versioned component + SingleVersion sibling. That is the shape where a Versioned
/// component's indexes move to the per-archetype home, and it is the shape no pre-existing fixture covered.
/// </para>
/// <para>
/// Asserted at the index level rather than through <c>WhereField</c>: <c>QueryResolverHelper.MapFieldTypeToKeyType</c> rejects
/// <c>String64</c> for query predicates on every storage mode, so a query cannot reach a String64 index at all. That is a separate,
/// pre-existing limitation of the predicate layer, unrelated to index ownership.
/// </para>
/// </remarks>
[TestFixture]
class ClusterString64IndexTests : TestBase<ClusterString64IndexTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClusterS64Named>();
        dbe.RegisterComponentFromAccessor<ClusterS64Tag>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe)
        => dbe._archetypeStates[Archetype<ClusterS64Arch>.Metadata.ArchetypeId].ClusterState;

    private static EntityId Spawn(Transaction tx, string name, int score)
        => tx.Spawn<ClusterS64Arch>(ClusterS64Arch.Data.Set(new ClusterS64Named((String64)name, score)), ClusterS64Arch.Tag.Set(new ClusterS64Tag(score)));

    /// <summary>Resolves the entity indexed under <paramref name="name"/>, or null when the key is absent.</summary>
    private static unsafe EntityId? IndexedEntityForName(DatabaseEngine dbe, String64 name)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var clusterState = ClusterState(dbe);
        ref var field = ref clusterState.IndexSlots[0].Fields[0];   // Name — first indexed field in declaration order

        var accessor = field.Index.Segment.CreateChunkAccessor();
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            var result = field.Index.TryGet(&name, ref accessor);
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

    [Test]
    public void String64Field_OnClusterArchetype_GetsItsOwnSegment()
    {
        using var dbe = SetupEngine();
        Assert.That(Archetype<ClusterS64Arch>.Metadata.HasClusterIndexes, Is.True, "fixture must be cluster-backed");

        var clusterState = ClusterState(dbe);
        Assert.That(clusterState.IndexSegmentString64, Is.Not.Null, "an archetype indexing a String64 field needs the wider-stride segment");

        ref var nameField = ref clusterState.IndexSlots[0].Fields[0];
        ref var scoreField = ref clusterState.IndexSlots[0].Fields[1];
        Assert.That(ReferenceEquals(nameField.Index.Segment, clusterState.IndexSegmentString64), Is.True, "Name must be routed to the String64 segment");
        Assert.That(ReferenceEquals(scoreField.Index.Segment, clusterState.IndexSegment), Is.True, "Score must stay on the 256-byte segment");
        Assert.That(nameField.ZoneMap, Is.Null, "a String64 key has no numeric min/max summary");
        Assert.That(scoreField.ZoneMap, Is.Not.Null, "numeric fields keep their zone map");
    }

    [Test]
    public void String64Index_SpawnManyEntities_AllKeysResolve()
    {
        using var dbe = SetupEngine();

        // Enough entities to force B+Tree node splits — a mis-strided node layout corrupts its neighbour once it has one.
        var ids = new EntityId[200];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = Spawn(tx, $"name_{i:D4}", i);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        for (var i = 0; i < ids.Length; i++)
        {
            Assert.That(IndexedEntityForName(dbe, (String64)$"name_{i:D4}"), Is.EqualTo(ids[i]), $"entity {i} must resolve through the String64 index");
        }

        Assert.That(IndexedEntityForName(dbe, (String64)"name_9999"), Is.Null, "absent key must not resolve");
    }

    [Test]
    public void String64Index_Mutation_MovesTheKey()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = Spawn(tx, "before", 1);
            Spawn(tx, "other", 2);
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(IndexedEntityForName(dbe, (String64)"before"), Is.EqualTo(id));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClusterS64Arch.Data) = new ClusterS64Named((String64)"after", 1);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexedEntityForName(dbe, (String64)"before"), Is.Null, "old key must be removed");
        Assert.That(IndexedEntityForName(dbe, (String64)"after"), Is.EqualTo(id), "new key must be present");
        Assert.That(IndexedEntityForName(dbe, (String64)"other"), Is.Not.Null, "an unrelated key must be untouched");
    }

    /// <summary>
    /// AC: the commit path's unchanged-field guard compares the WHOLE key, so a String64 mutation that leaves the first 8 bytes alone still moves the index.
    /// </summary>
    /// <remarks>
    /// The guard added by #665 short-circuits <c>ReconcileClusterIndexAndViews</c> when an indexed field did not change, which is what keeps an unrelated
    /// write off the B+Tree. Its flat twin <c>ReconcileFlatIndexAndViews</c> compares through <c>KeyBytes8</c> — legal there, because Commit discipline is
    /// SingleVersion-only and registration refuses a String64 index on any in-place storage mode. The per-archetype path has no such luxury: it is also the
    /// Versioned commit path, and Versioned is exactly where a String64 index IS legal. Comparing 8 of 64 bytes would silently skip the move and leave the
    /// entity indexed under a key it no longer has — an invisible corruption, since the query layer cannot reach a String64 index to contradict it.
    /// </remarks>
    [Test]
    public void String64Index_MutationSharingTheFirstEightBytes_StillMovesTheKey()
    {
        using var dbe = SetupEngine();

        // Premise: the guard must be told the field is 64 bytes wide. Same length so the comparison cannot be decided by a length prefix.
        var clusterState0 = ClusterState(dbe);
        Assert.That(clusterState0.IndexSlots[0].Fields[0].FieldSize, Is.EqualTo(64), "a String64 index field must report its full width to the guard");

        const string before = "shared_prefix_alpha";
        const string after = "shared_prefix_bravo";
        Assert.That(before.Length, Is.EqualTo(after.Length), "premise: equal length");
        Assert.That(before[..8], Is.EqualTo(after[..8]), "premise: the two keys are byte-identical over the first 8 characters");

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = Spawn(tx, before, 1);
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(IndexedEntityForName(dbe, (String64)before), Is.EqualTo(id), "premise: indexed under the original key");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClusterS64Arch.Data) = new ClusterS64Named((String64)after, 1);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.Multiple(() =>
        {
            Assert.That(IndexedEntityForName(dbe, (String64)after), Is.EqualTo(id), "the key must move even though the first 8 bytes are unchanged");
            Assert.That(IndexedEntityForName(dbe, (String64)before), Is.Null, "and the old key must be gone");
        });
    }

    [Test]
    public void String64Index_Destroy_RemovesTheKey()
    {
        using var dbe = SetupEngine();

        EntityId doomed, survivor;
        using (var tx = dbe.CreateQuickTransaction())
        {
            doomed = Spawn(tx, "doomed", 1);
            survivor = Spawn(tx, "survivor", 2);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(doomed);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexedEntityForName(dbe, (String64)"doomed"), Is.Null);
        Assert.That(IndexedEntityForName(dbe, (String64)"survivor"), Is.EqualTo(survivor));
    }

    [Test]
    public void String64Index_OnInPlaceStorageMode_IsRejected()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        try
        {
            // SingleVersion is mutated in place, so its index keys are captured into an 8-byte KeyBytes8 shadow slot. A 64-byte key would
            // memcpy past it and smash the stack, so registration must refuse rather than defer the failure to the first write.
            var ex = Assert.Throws<InvalidOperationException>(() => dbe.RegisterComponentFromAccessor<ClusterS64Illegal>());
            Assert.That(ex.Message, Does.Contain("String64"));
            Assert.That(ex.Message, Does.Contain("SingleVersion"));
            Assert.That(ex.Message, Does.Contain("667"), "the error must point at the tracking issue");
        }
        finally
        {
            dbe.Dispose();
        }
    }
}
