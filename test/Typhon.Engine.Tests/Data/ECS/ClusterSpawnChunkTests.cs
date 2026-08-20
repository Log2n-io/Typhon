using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// #839: a cluster-backed entity's SingleVersion and Transient bytes live in the cluster slot. The spawn path must not
/// also allocate a per-component content chunk for them.
/// <para>
/// The defect: <c>SpawnInternal</c> allocated a <c>ComponentSegment</c> chunk per component with no cluster-eligibility
/// check, <c>FinalizeSpawns</c> copied the payload into the cluster, and for SV/Transient the chunk then became
/// <b>structurally unreachable</b> — the persisted <c>ClusterEntityRecord</c> has no field capable of holding its id, so
/// nothing could ever free it. The file grew with CUMULATIVE spawns rather than live entities. Measured in the
/// SpaceBattle demo: 491,930 <c>Bullet</c> chunks against ~1,200 live shots, and a 282 MB data file holding ~1.8 MB of
/// live entity bytes.
/// </para>
/// <para>
/// <b>Versioned is deliberately excluded.</b> There the same chunk becomes the revision's content
/// (<c>elements[0].ComponentChunkId</c>) and the cluster slot is a HEAD cache over the chain, not its owner. That chunk
/// is correctly reclaimed today, and <see cref="VersionedSpawn_StillAllocatesItsRevisionContentChunk"/> is the guard
/// that keeps this fix away from it.
/// </para>
/// </summary>
class ClusterSpawnChunkTests : TestBase<ClusterSpawnChunkTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();
        dbe.RegisterComponentFromAccessor<CompSmVersionedMix>();
        dbe.RegisterComponentFromAccessor<CmPosition>();
        dbe.RegisterComponentFromAccessor<CmTeam>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// The defect's actual signature, and the headline test: allocation must track LIVE entities, not cumulative spawns.
    /// Spawn a batch, destroy it, repeat — the chunk count after the first round must be the count after the last.
    /// </summary>
    /// <remarks>
    /// The first round is excluded from the comparison on purpose: it pays the segment's genuine first-touch growth, and
    /// asserting against a pre-warm count would make this a test of the allocator's initial sizing rather than of the
    /// leak. Rounds 2..K allocate nothing new if and only if the defect is fixed — pre-fix each round added
    /// <c>EntitiesPerRound</c> chunks per component and never gave them back.
    /// </remarks>
    [Test]
    [VerifiesRule("STAGE-01")]
    public void SpawnDestroyChurn_DoesNotGrowTheSegments()
    {
        const int EntitiesPerRound = 32;
        const int Rounds = 4;

        using var dbe = SetupEngine();
        var svTable = dbe.GetComponentTable<CompSmSingleVersion>();
        var transTable = dbe.GetComponentTable<CompSmTransient>();

        int svAfterFirstRound = 0;
        int transAfterFirstRound = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var ids = new List<EntityId>(EntitiesPerRound);
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < EntitiesPerRound; i++)
                {
                    ids.Add(tx.Spawn<MixedModeArchetype>(
                        MixedModeArchetype.SV.Set(new CompSmSingleVersion(i)),
                        MixedModeArchetype.Trans.Set(new CompSmTransient(i)),
                        MixedModeArchetype.Versioned.Set(new CompSmVersionedMix(i))));
                }
                tx.Commit();
            }

            using (var tx = dbe.CreateQuickTransaction())
            {
                foreach (var id in ids)
                {
                    tx.Destroy(id);
                }
                tx.Commit();
            }

            if (round == 0)
            {
                svAfterFirstRound = svTable.ComponentSegment.AllocatedChunkCount;
                transAfterFirstRound = transTable.TransientComponentSegment.AllocatedChunkCount;
            }
        }

        Assert.That(svTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(svAfterFirstRound),
            $"SingleVersion content chunks must track live entities, not cumulative spawns. {Rounds - 1} further rounds "
            + $"of spawn+destroy ({EntitiesPerRound} entities each) allocated more chunks, which means each spawn is "
            + "leaving behind a chunk no ClusterEntityRecord can address and nothing can free (#839).");

        Assert.That(transTable.TransientComponentSegment.AllocatedChunkCount, Is.EqualTo(transAfterFirstRound),
            "the same holds for Transient — its staging chunk is just as unreachable once the payload is in the cluster");
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: reproduces the pre-fix behaviour by taking one content chunk per
    /// spawned entity, and requires the churn assertion to reject it.
    /// </summary>
    /// <remarks>
    /// Without this the verifier could be green because the segment never grows for some unrelated reason — an allocator
    /// that recycled aggressively, say — rather than because the spawn stopped allocating. The mutant allocates exactly
    /// what <c>SpawnInternal</c> used to and never frees it, which is precisely the defect: the chunk was unreachable the
    /// moment the payload reached the cluster.
    /// </remarks>
    [Test]
    [RuleMutant("STAGE-01")]
    public void SpawnsThatTakeAContentChunk_AreRejectedByTheChurnAssertion()
    {
        RuleMutants.AssertDetects("STAGE-01", "must track live entities, not cumulative spawns", () =>
        {
            using var dbe = SetupEngine();
            var svTable = dbe.GetComponentTable<CompSmSingleVersion>();

            var after = 0;
            for (var round = 0; round < 3; round++)
            {
                using (var tx = dbe.CreateQuickTransaction())
                {
                    for (var i = 0; i < 16; i++)
                    {
                        tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(new CompSmSingleVersion(i)));
                        svTable.ComponentSegment.AllocateChunk(false);   // the leak, reproduced verbatim
                    }
                    tx.Commit();
                }

                if (round == 0)
                {
                    after = svTable.ComponentSegment.AllocatedChunkCount;
                }
            }

            Assert.That(svTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(after),
                "SingleVersion content chunks must track live entities, not cumulative spawns");
        });
    }

    /// <summary>A single committed spawn must leave the SV and Transient segments exactly as it found them.</summary>
    [Test]
    public void ClusterSpawn_AllocatesNoContentChunk_ForSingleVersionOrTransient()
    {
        using var dbe = SetupEngine();
        var svTable = dbe.GetComponentTable<CompSmSingleVersion>();
        var transTable = dbe.GetComponentTable<CompSmTransient>();

        // Warm-up spawn: the very first entity pays any one-off segment growth, which is not what this asserts.
        using (var warm = dbe.CreateQuickTransaction())
        {
            warm.Spawn<MixedModeArchetype>(
                MixedModeArchetype.SV.Set(new CompSmSingleVersion(0)),
                MixedModeArchetype.Trans.Set(new CompSmTransient(0)),
                MixedModeArchetype.Versioned.Set(new CompSmVersionedMix(0)));
            warm.Commit();
        }

        var svBefore = svTable.ComponentSegment.AllocatedChunkCount;
        var transBefore = transTable.TransientComponentSegment.AllocatedChunkCount;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<MixedModeArchetype>(
                MixedModeArchetype.SV.Set(new CompSmSingleVersion(1)),
                MixedModeArchetype.Trans.Set(new CompSmTransient(1)),
                MixedModeArchetype.Versioned.Set(new CompSmVersionedMix(1)));
            tx.Commit();
        }

        Assert.That(svTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(svBefore),
            "a cluster-backed SingleVersion component's bytes live in the cluster slot — allocating a content chunk for "
            + "it produces one that nothing can address and nothing can free (#839)");
        Assert.That(transTable.TransientComponentSegment.AllocatedChunkCount, Is.EqualTo(transBefore),
            "same for Transient");
    }

    /// <summary>
    /// The guard that keeps #839 away from Versioned: its chunk is the revision's content, not staging, so the spawn
    /// must still allocate one. A fix that stopped allocating here would silently destroy MVCC history.
    /// </summary>
    [Test]
    public void VersionedSpawn_StillAllocatesItsRevisionContentChunk()
    {
        using var dbe = SetupEngine();
        var verTable = dbe.GetComponentTable<CompSmVersionedMix>();

        using (var warm = dbe.CreateQuickTransaction())
        {
            warm.Spawn<MixedModeArchetype>(
                MixedModeArchetype.SV.Set(new CompSmSingleVersion(0)),
                MixedModeArchetype.Trans.Set(new CompSmTransient(0)),
                MixedModeArchetype.Versioned.Set(new CompSmVersionedMix(0)));
            warm.Commit();
        }

        var before = verTable.ComponentSegment.AllocatedChunkCount;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<MixedModeArchetype>(
                MixedModeArchetype.SV.Set(new CompSmSingleVersion(1)),
                MixedModeArchetype.Trans.Set(new CompSmTransient(1)),
                MixedModeArchetype.Versioned.Set(new CompSmVersionedMix(1)));
            tx.Commit();
        }

        Assert.That(verTable.ComponentSegment.AllocatedChunkCount, Is.GreaterThan(before),
            "a Versioned spawn's content chunk IS the first revision's payload — the cluster slot is a HEAD cache over "
            + "the chain, not its owner. #839 must not touch this path.");
    }

    /// <summary>
    /// Spawn an entity with an INDEXED SingleVersion component and write it in the same transaction. The shadow-capture
    /// path this exercises is the one consumer that reads a pre-publish location WITHOUT going through the
    /// storage-mode-aware resolver, so it is where an arena handle would be dereferenced as a content chunk id.
    /// </summary>
    /// <remarks>
    /// The absence of this case is why the defect existed: every other spawn-then-write test used a component with no
    /// indexed field, so <c>HasShadowableIndexes</c> was false and <c>ShadowIndexedFields</c> never ran. It fails loudly
    /// only if the shadow buffer's contents are checked or the read lands outside the segment; the value assertions below
    /// are the cheap part, and the real guard is that the shadow path is now skipped for own-spawns entirely.
    /// </remarks>
    [Test]
    public void SpawnThenWrite_IndexedSingleVersion_DoesNotShadowThroughTheArenaHandle()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<CmTeam>();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<CmIdxEntity>(
                CmIdxEntity.Position.Set(new CmPosition(1, 2)),
                CmIdxEntity.Team.Set(new CmTeam { TeamId = 7, Rank = 1 }));

            ref var team = ref tx.OpenMut(id).Write(CmIdxEntity.Team);
            team.TeamId = 42;
            team.Rank = 9;
            tx.Commit();
        }

        Assert.That(table.ShadowBitmap.HasDirty, Is.False,
            "an own-spawn must not shadow indexed fields: it has no OLD key — FinalizeSpawns inserts its index entries "
            + "fresh — and its location is a spawn-arena handle, which the shadow path would dereference against the "
            + "ComponentSegment as though it were a chunk id (#839)");

        using var read = dbe.CreateQuickTransaction();
        var e = read.Open(id);
        Assert.That(e.Read(CmIdxEntity.Team).TeamId, Is.EqualTo(42), "the same-transaction write must reach the cluster");
        Assert.That(e.Read(CmIdxEntity.Team).Rank, Is.EqualTo(9));
        Assert.That(e.Read(CmIdxEntity.Position).X, Is.EqualTo(1f), "and must not have disturbed the sibling component");
    }

    /// <summary>
    /// A <c>ref</c> handed out by a write must stay valid across arbitrarily many later spawns in the same transaction.
    /// </summary>
    /// <remarks>
    /// This is a PREVENTION test, not a regression test: it passes before #839's fix, because the staging chunk lives in
    /// the page cache and is stable. It exists because the obvious implementation — the <c>_commitStagingBuffer</c>
    /// pattern, a single <c>NativeMemory.Realloc</c>'d block — would move the buffer under exactly this sequence and
    /// hand the caller a dangling ref. That buffer's own doc accepts the invalidation ("the common write-then-commit
    /// idiom is always safe"); for spawns it is not safe, because spawn-spawn-write is what <c>SpawnBatch</c> does.
    /// </remarks>
    [Test]
    public void WriteRefFromAnEarlierSpawn_SurvivesManyLaterSpawns()
    {
        const int LaterSpawns = 2048;

        using var dbe = SetupEngine();

        EntityId first;
        using (var tx = dbe.CreateQuickTransaction())
        {
            first = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(new CompSmSingleVersion(1)));

            ref var staged = ref tx.OpenMut(first).Write(SvTestArchetype.SvComp);
            staged.Value = 111;

            for (var i = 0; i < LaterSpawns; i++)
            {
                tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(new CompSmSingleVersion(i)));
            }

            Assert.That(staged.Value, Is.EqualTo(111),
                $"the ref from the first spawn must survive {LaterSpawns} later spawns in the same transaction — if the "
                + "staging store grows by reallocating, this ref points at freed memory and the read is a use-after-free");

            staged.Value = 222;
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Open(first).Read(SvTestArchetype.SvComp).Value, Is.EqualTo(222),
            "and the write through that ref must be the value that reaches the cluster");
    }
}
