using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// The storage an entity's Versioned components hold — content chunks and revision chains — is freed exactly once, by its owner, whatever ends it:
/// destroy, destroy after a read or a write, a destroy racing a write, a rollback (REAP-02).
/// </summary>
/// <remarks>
/// <para>Found by <c>MarketHardeningTests</c>: within a few thousand consumes an item's entity read another item's data. A chunk freed twice is handed
/// out twice; the first free lets a spawn take it, the second takes it from under that entity, and the next spawn gets it too. Single-threaded a second
/// free of an already-free chunk is silently ignored, so it shows only once another thread allocates in between — hence the verifier here counts them
/// (<c>ChunkBasedSegment.DoubleFreeCount</c>) rather than waiting for the corruption.</para>
/// <para>The double frees this fixture found: a destroy freed the committed content chunk it had only read, which the revision GC freed again; the revision
/// GC and the entity cleanup both freed a lone tombstone's chain root; every rollback after a Versioned write, and every rolled-back spawn, freed its
/// chunk in the rollback and again at the reset.</para>
/// </remarks>
[TestFixture]
class DestroyRespawnSlotReuseTests : TestBase<DestroyRespawnSlotReuseTests>
{
    private const int Entities = 2_000;

    private DatabaseEngine SetupEngine(out EntityId[] ids)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        ids = new EntityId[Entities];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Entities; i++)
        {
            var a = new CompA(i, 0, 0);
            ids[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
        }

        tx.Commit();
        return dbe;
    }

    /// <summary>What the CompA and CompB segments hold and how often they were freed twice: the verifier's baseline.</summary>
    private readonly record struct Ledger(long ContentA, long RevisionsA, long ContentB, long RevisionsB, long DoubleFrees, long Foreign);

    private static Ledger Measure(DatabaseEngine dbe)
    {
        FlushAll(dbe);
        var a = dbe.GetComponentTable<CompA>();
        var b = dbe.GetComponentTable<CompB>();
        var doubleFrees = Interlocked.Read(ref a.ComponentSegment.DoubleFreeCount) + Interlocked.Read(ref a.CompRevTableSegment.DoubleFreeCount)
                          + Interlocked.Read(ref b.ComponentSegment.DoubleFreeCount) + Interlocked.Read(ref b.CompRevTableSegment.DoubleFreeCount);
        return new Ledger(a.ComponentSegment.AllocatedChunkCount, a.CompRevTableSegment.AllocatedChunkCount, b.ComponentSegment.AllocatedChunkCount,
            b.CompRevTableSegment.AllocatedChunkCount, doubleFrees, Interlocked.Read(ref dbe.DeferredCleanupManager.DestroyedChainsForeign));
    }

    /// <summary>
    /// The verifier: after every cleanup has run, nothing was freed twice, no cleanup met a chain that was not its own, and each segment holds exactly
    /// what it held before plus <paramref name="expectedChange"/> — the survivors' needs, nothing leaked.
    /// </summary>
    private static void AssertFreedExactlyOnce(DatabaseEngine dbe, Ledger before, int expectedChange, string what, bool compB = false)
    {
        var after = Measure(dbe);
        Assert.That(after.DoubleFrees - before.DoubleFrees, Is.Zero, $"{what}: chunks were freed twice — each a chunk two owners could get");
        Assert.That(after.Foreign - before.Foreign, Is.Zero, $"{what}: an entity's cleanup met a chain root that was not its own");
        Assert.That(after.ContentA - before.ContentA, Is.EqualTo(compB ? 0 : expectedChange), $"{what}: CompA content chunks leaked or were freed twice");
        Assert.That(after.RevisionsA - before.RevisionsA, Is.EqualTo(compB ? 0 : expectedChange), $"{what}: CompA revision chunks leaked or were freed twice");
        if (compB)
        {
            Assert.That(after.ContentB - before.ContentB, Is.EqualTo(expectedChange), $"{what}: CompB content chunks leaked or were freed twice");
            Assert.That(after.RevisionsB - before.RevisionsB, Is.EqualTo(expectedChange), $"{what}: CompB revision chunks leaked or were freed twice");
        }
    }

    /// <summary>Every live id reads the data it was given — its own number — and none reads another entity's.</summary>
    private static void AssertEveryIdReadsItsOwnData(DatabaseEngine dbe, EntityId[] ids, string what)
    {
        using var tx = dbe.CreateReadOnlyTransaction();
        var wrong = 0;
        string first = null;
        for (var i = 0; i < ids.Length; i++)
        {
            var a = tx.Open(ids[i]).Read(CompAArch.A).A;
            if (a != i)
            {
                wrong++;
                first ??= $"entity {ids[i]} (number {i}) reads number {a}";
            }
        }

        Assert.That(wrong, Is.Zero, $"{what}: {wrong} live ids read another entity's data; first: {first}");
    }

    private static void FlushAll(DatabaseEngine dbe)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            dbe.FlushDeferredCleanups();
            dbe.DeferredCleanupManager.FlushChunkFrees(dbe.EpochManager);
        }
    }

    private static void DestroyAndRespawn(DatabaseEngine dbe, EntityId[] ids, int i, int round, bool readFirst)
    {
        using var tx = dbe.CreateQuickTransaction();
        if (readFirst && tx.Open(ids[i]).Read(CompAArch.A).A != i)
        {
            Assert.Fail($"entity {ids[i]} (number {i}) read another entity's data before its destroy");
        }

        tx.Destroy(ids[i]);
        var a = new CompA(i, round, 0);
        var replacement = tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
        tx.Commit();
        ids[i] = replacement;
    }

    private static void Write(Transaction tx, EntityId id, int b)
    {
        var e = tx.OpenMut(id);
        var a = e.Read(CompAArch.A);
        a.B = b;
        e.Set(CompAArch.A, a);
    }

    [Test]
    [VerifiesRule("REAP-02")]
    public void DestroyAndRespawn_KeepsEveryIdOnItsOwnData()
    {
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        var rng = new Random(945);
        for (var round = 0; round < 1_000; round++)
        {
            DestroyAndRespawn(dbe, ids, rng.Next(Entities), round, readFirst: (round & 1) == 0);
        }

        AssertEveryIdReadsItsOwnData(dbe, ids, "after 1,000 destroy-and-respawn transactions");
        AssertFreedExactlyOnce(dbe, before, 0, "destroy and respawn");
        AssertEveryIdReadsItsOwnData(dbe, ids, "after the cleanup");
    }

    [Test]
    [VerifiesRule("REAP-02")]
    public void DestroyThenRespawnInTwoTransactions_KeepsEveryIdOnItsOwnData()
    {
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        var rng = new Random(945);
        for (var round = 0; round < 500; round++)
        {
            var i = rng.Next(Entities);
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.Destroy(ids[i]);
                tx.Commit();
            }

            using (var tx = dbe.CreateQuickTransaction())
            {
                var a = new CompA(i, round, 0);
                ids[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
                tx.Commit();
            }
        }

        AssertEveryIdReadsItsOwnData(dbe, ids, "after 500 destroy-then-respawn pairs");
        AssertFreedExactlyOnce(dbe, before, 0, "destroy then respawn");
    }

    /// <summary>Spawns on other threads take chunks between two cleanup steps: the window in which a second free took a live chunk.</summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void DestroyAndRespawnOnSeveralThreads_KeepsEveryIdOnItsOwnData()
    {
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        var locks = new object[Entities];
        for (var i = 0; i < Entities; i++)
        {
            locks[i] = new object();
        }

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, w =>
        {
            var rng = new Random(945 + w);
            for (var round = 0; round < 600; round++)
            {
                var i = rng.Next(Entities);
                lock (locks[i])
                {
                    DestroyAndRespawn(dbe, ids, i, round, readFirst: (round & 1) == 0);
                }
            }
        });

        AssertEveryIdReadsItsOwnData(dbe, ids, "after 4,800 destroy-and-respawn transactions on 8 threads");
        AssertFreedExactlyOnce(dbe, before, 0, "destroy and respawn on 8 threads");
    }

    /// <summary>
    /// Every shape of a transaction that ends an entity frees exactly what the entity held — after a read, after a write, with a replacement.
    /// </summary>
    [TestCase(false, false, TestName = "Destroy")]
    [TestCase(true, false, TestName = "WriteThenDestroy")]
    [TestCase(false, true, TestName = "DestroyAndRespawn")]
    [TestCase(true, true, TestName = "WriteThenDestroyAndRespawn")]
    [VerifiesRule("REAP-02")]
    public void EndingAnEntity_FreesExactlyWhatItHeld(bool writeFirst, bool respawn)
    {
        const int Rounds = 50;
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        for (var k = 0; k < Rounds; k++)
        {
            var i = k * 37;
            using var tx = dbe.CreateQuickTransaction();
            if (writeFirst)
            {
                Write(tx, ids[i], k);
            }

            tx.Destroy(ids[i]);
            if (respawn)
            {
                var fresh = new CompA(i, k, 1);
                ids[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in fresh));
            }

            tx.Commit();
        }

        AssertFreedExactlyOnce(dbe, before, respawn ? 0 : -Rounds, "ending entities");
        if (respawn)
        {
            AssertEveryIdReadsItsOwnData(dbe, ids, "after the cleanup");
        }
    }

    /// <summary>
    /// A rollback frees what the transaction allocated — a write's copy, a spawn's chunk and chain — once, and leaves the rest as it was.
    /// </summary>
    [TestCase("write", TestName = "RolledBackWrite")]
    [TestCase("write-dispose", TestName = "WriteDisposedUncommitted")]
    [TestCase("spawn", TestName = "RolledBackSpawn")]
    [TestCase("destroy", TestName = "RolledBackDestroy")]
    [TestCase("write-destroy", TestName = "RolledBackWriteThenDestroy")]
    [VerifiesRule("REAP-02")]
    public void ARollback_FreesWhatItAllocatedOnce(string shape)
    {
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        for (var k = 0; k < 20; k++)
        {
            var i = k * 53;
            using var tx = dbe.CreateQuickTransaction();
            if (shape.StartsWith("write"))
            {
                Write(tx, ids[i], -k);
            }

            if (shape == "spawn")
            {
                var fresh = new CompA(-1, k, 0);
                tx.Spawn<CompAArch>(CompAArch.A.Set(in fresh));
            }

            if (shape.EndsWith("destroy"))
            {
                tx.Destroy(ids[i]);
            }

            if (shape != "write-dispose")
            {
                tx.Rollback();
            }
        }

        AssertFreedExactlyOnce(dbe, before, 0, $"rolled back ({shape})");
        AssertEveryIdReadsItsOwnData(dbe, ids, "after the rollbacks");
    }

    /// <summary>
    /// A destroy that loses a race: the entity was written in this transaction, another transaction commits a write to it in between, and this one then
    /// destroys it. The commit relocates — or resolves the conflict of — a tombstone, which must stay a tombstone.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void ADestroyCommittedAfterAConcurrentWrite_StaysADestroy()
    {
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        const int Rounds = 20;
        for (var k = 0; k < Rounds; k++)
        {
            var i = k * 41;
#pragma warning disable TYPHON004 // two transactions open at once on purpose: the second commits between the first's write and its destroy
            var loser = dbe.CreateQuickTransaction();
#pragma warning restore TYPHON004
            Write(loser, ids[i], 1);
            using (var winner = dbe.CreateQuickTransaction())
            {
                Write(winner, ids[i], 2);
                winner.Commit();
            }

            loser.Destroy(ids[i]);
            loser.Commit();
            loser.Dispose();

            using var check = dbe.CreateReadOnlyTransaction();
            Assert.That(check.IsAlive(ids[i]), Is.False, $"round {k}: the destroy committed after a concurrent write did not destroy the entity");
        }

        AssertFreedExactlyOnce(dbe, before, -Rounds, "destroys committed after concurrent writes");
    }

    /// <summary>
    /// A live entity whose chain outgrew its root chunk — an old reader kept every revision alive — shrinks back to its root once the reader ends: the
    /// revision GC's compaction frees every overflow chunk of the old chain. It used to free none when the chain started at its first slot, the usual case.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void AChainThatOverflowed_ShrinksBackToItsRoot_OnceItsReaderEnds()
    {
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        var victim = ids[11];
        using (var reader = dbe.CreateReadOnlyTransaction())
        {
            _ = reader.Open(victim).Read(CompAArch.A);
            for (var k = 0; k < 3 * Typhon.Engine.Internals.ComponentRevisionManager.CompRevCountInRoot; k++)
            {
                using var tx = dbe.CreateQuickTransaction();
                Write(tx, victim, k);
                tx.Commit();
            }

            Assert.That(dbe.GetComponentTable<CompA>().CompRevTableSegment.AllocatedChunkCount, Is.GreaterThan(before.RevisionsA + 1),
                "premise: the old reader kept the chain long enough to need several overflow chunks");
        }

        // One more commit once the reader is gone: its cleanup compacts the chain to its head.
        using (var tx = dbe.CreateQuickTransaction())
        {
            Write(tx, victim, -1);
            tx.Commit();
        }

        AssertFreedExactlyOnce(dbe, before, 0, "a live entity's overflowed chain, compacted");
        using var check = dbe.CreateReadOnlyTransaction();
        Assert.That(check.Open(victim).Read(CompAArch.A).B, Is.EqualTo(-1), "the compacted chain lost its head");
    }

    /// <summary>
    /// The last entities of an archetype, destroyed just before a clean close, stay dead after the reopen. Their chains are freed deferred, past the
    /// transactions alive at the release; with none to follow, the close runs those frees — a chain root left allocated reads back as a live entity
    /// when the archetype's map reopens empty and is rebuilt from the chain heads.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void TheLastEntitiesDestroyedBeforeACleanClose_StayDeadAfterTheReopen()
    {
        var ids = new EntityId[8];
        long revisionsBefore;
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            RegisterComponents(dbe);
            dbe.InitializeArchetypes();
            revisionsBefore = dbe.GetComponentTable<CompA>().CompRevTableSegment.AllocatedChunkCount;
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var k = 0; k < ids.Length; k++)
                {
                    var a = new CompA(k, 0, 0);
                    ids[k] = tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
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
        }

        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            RegisterComponents(dbe);
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateReadOnlyTransaction();
            foreach (var id in ids)
            {
                Assert.That(tx.IsAlive(id), Is.False, $"destroyed entity {id} is alive after the reopen");
            }

            Assert.That(dbe.GetComponentTable<CompA>().CompRevTableSegment.AllocatedChunkCount, Is.EqualTo(revisionsBefore),
                "the destroyed entities' revision chains outlived the close");
        }
    }

    /// <summary>
    /// A compaction that keeps several revisions builds its new chain from freed chunks, which still hold their previous links: the chain must end where
    /// its length says. A stale link in its last chunk sent the next walk — the next compaction's, the destroyed entity's release — into a chunk of
    /// another chain, freed under it.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void ACompactedChainBuiltFromFreedChunks_EndsWhereItsLengthSays()
    {
        using var dbe = SetupEngine(out var ids);
        var perChunk = Typhon.Engine.Internals.ComponentRevisionManager.CompRevCountInNext;
        var a = ids[3];
        var b = ids[5];

        // Both chains outgrow their roots under an old reader; a second reader keeps B's newer half alive.
        var older = dbe.CreateReadOnlyTransaction();
        _ = older.Open(a).Read(CompAArch.A);
        for (var k = 0; k < 4 * perChunk; k++)
        {
            using var tx = dbe.CreateQuickTransaction();
            Write(tx, a, k);
            Write(tx, b, k);
            tx.Commit();
        }

        using var mid = dbe.CreateReadOnlyTransaction();
        _ = mid.Open(b).Read(CompAArch.A);
        for (var k = 0; k < 2 * perChunk; k++)
        {
            using var tx = dbe.CreateQuickTransaction();
            Write(tx, b, 100 + k);
            tx.Commit();
        }

        // First compaction: A shrinks to its head, B to what the second reader needs. Their old overflow chunks are freed with their links in them.
        older.Dispose();
        dbe.FlushDeferredCleanups(mid.TSN);
        dbe.DeferredCleanupManager.FlushChunkFrees(dbe.EpochManager);

        // Second compaction of B: its new chunks are the ones just freed.
        using (var tx = dbe.CreateQuickTransaction())
        {
            Write(tx, b, -1);
            tx.Commit();
        }

        dbe.FlushDeferredCleanups(mid.TSN);

        int root;
        unsafe
        {
            var state = dbe._stateByRouting[b.ArchetypeId];
            var buf = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            var map = state.EntityMap.Segment.CreateChunkAccessor();
            state.EntityMap.TryGet(b.EntityKey, buf, ref map);
            root = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(buf, 0);
            map.Dispose();
        }

        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var revisions = dbe.GetComponentTable<CompA>().CompRevTableSegment.CreateChunkAccessor();
            ref var header = ref revisions.GetChunk<CompRevStorageHeader>(root);
            var length = header.ChainLength;
            Assert.That(length, Is.GreaterThan(2), "premise: the compaction kept the chain over several chunks");
            var next = header.NextChunkId;
            for (var hop = 1; hop < length; hop++)
            {
                Assert.That(next, Is.Not.Zero, $"the chain of length {length} ends after {hop} chunk(s)");
                next = revisions.GetChunk<int>(next);
            }

            Assert.That(next, Is.Zero, $"the last chunk of a chain of length {length} links on to chunk {next}");
            revisions.Dispose();
        }
    }

    /// <summary>An entity spawned and destroyed in one committed transaction leaves nothing behind. It leaks its spawn's storage today (#1229).</summary>
    [Test]
    [Category("Quarantine")]
    public void SpawnAndDestroyInOneTransaction_LeavesNothingBehind()
    {
        using var dbe = SetupEngine(out _);
        var before = Measure(dbe);
        for (var k = 0; k < 50; k++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var a = new CompA(k, 0, 0);
            tx.Destroy(tx.Spawn<CompAArch>(CompAArch.A.Set(in a)));
            tx.Commit();
        }

        AssertFreedExactlyOnce(dbe, before, 0, "entities spawned and destroyed in one transaction");
    }

    /// <summary>
    /// A Versioned component enabled and destroyed in the same transaction: the chain enabling it created becomes a lone tombstone, freed whole.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void AComponentEnabledThenDestroyed_FreesItsChain()
    {
        using var dbe = SetupEngine(out _);
        var spawned = new EntityId[20];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < spawned.Length; k++)
            {
                var a = new CompA(k, 0, 0);
                spawned[k] = tx.Spawn<CompABArch>(CompABArch.A.Set(in a));
            }

            tx.Commit();
        }

        var before = Measure(dbe);
        foreach (var id in spawned)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.OpenMut(id).Enable(CompABArch.B, new CompB(7, 7));
            tx.Destroy(id);
            tx.Commit();
        }

        var after = Measure(dbe);
        Assert.That(after.DoubleFrees - before.DoubleFrees, Is.Zero, "chunks were freed twice");
        Assert.That(after.ContentB - before.ContentB, Is.Zero, "CompB content chunks leaked");
        Assert.That(after.RevisionsB - before.RevisionsB, Is.Zero, "CompB revision chunks leaked");
        Assert.That(after.ContentA - before.ContentA, Is.EqualTo(-spawned.Length), "CompA content chunks leaked or were freed twice");
    }

    /// <summary>
    /// An entity whose revision chain outgrew its root chunk — an old reader kept every revision alive — is destroyed: the overflow chunks go with it.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void ADestroyedEntityWhoseChainOverflowed_FreesEveryChunkOfIt()
    {
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        var victim = ids[7];
        using (var reader = dbe.CreateReadOnlyTransaction())
        {
            _ = reader.Open(victim).Read(CompAArch.A);
            for (var k = 0; k < 3 * Typhon.Engine.Internals.ComponentRevisionManager.CompRevCountInRoot; k++)
            {
                using var tx = dbe.CreateQuickTransaction();
                Write(tx, victim, k);
                tx.Commit();
            }

            Assert.That(dbe.GetComponentTable<CompA>().CompRevTableSegment.AllocatedChunkCount, Is.GreaterThan(before.RevisionsA),
                "premise: the old reader kept the chain long enough to overflow its root chunk");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(victim);
            tx.Commit();
        }

        AssertFreedExactlyOnce(dbe, before, -1, "a destroyed entity with an overflowed chain");
    }

    /// <summary>
    /// The verifier can fail: a content chunk freed while a live entity holds it — the shape of the bug — goes to the next spawn, and the entity that held
    /// it reads the newcomer's data.
    /// </summary>
    [Test]
    [RuleMutant("REAP-02")]
    public void Mutant_AChunkFreedTwice_IsReported()
    {
        using var dbe = SetupEngine(out var ids);
        var before = Measure(dbe);
        var table = dbe.GetComponentTable<CompA>();
        var free = table.ComponentSegment.AllocateChunk(false);
        table.ComponentSegment.FreeChunk(free);
        table.ComponentSegment.FreeChunk(free);   // the second free of the bug

        RuleMutants.AssertDetects("REAP-02", "freed twice", () => AssertFreedExactlyOnce(dbe, before, 0, "a chunk freed twice"));
    }
}
