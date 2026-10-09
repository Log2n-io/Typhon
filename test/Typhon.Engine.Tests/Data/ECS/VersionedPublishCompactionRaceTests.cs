using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.ECS.PublishCompaction.Wallet", 1)]
[StructLayout(LayoutKind.Sequential)]
struct PcWallet
{
    public long Credits;
    public long Pad;
}

[Archetype]
class PcWalletArch : Archetype<PcWalletArch>
{
    public static readonly Comp<PcWallet> Wallet = Register<PcWallet>();
}

/// <summary>
/// Rule <b>AP-05</b> (<c>rules/durability.md</c>): a publish stamps the entry it prepared, wherever a compaction has moved it since (#1158).
/// </summary>
/// <remarks>
/// <para>
/// A Versioned commit adds its revision isolated, appends to the WAL, then publishes: stamp the TSN and clear IsolationFlag. Without a conflict handler
/// PREPARE holds no chain lock, and between PREPARE and PUBLISH another thread's deferred cleanup could compact the chain, which rewrites every kept entry —
/// the pending one included — from index 0. The publish then stamped the position PREPARE recorded: it cleared the flag on whatever had moved there and left
/// its own entry isolated for good. The commit returned success and was invisible to every later reader; under last-writer-wins the next read-modify-write
/// built on the old value, and the hardening run lost credits.
/// </para>
/// <para>
/// The staged tests drive the race on one thread: <see cref="DatabaseEngine.PublishComponentProbe"/> runs a real compaction of the chain at the exact
/// point the other thread's cleanup did. The concurrent test is the shape the defect was found in.
/// </para>
/// </remarks>
[TestFixture]
class VersionedPublishCompactionRaceTests : TestBase<VersionedPublishCompactionRaceTests>
{
    private const string Ap05Marker = "AP-05 violated: a committed revision is invisible";

    private static DatabaseEngine OpenEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PcWallet>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Commit(DatabaseEngine dbe, EntityId id, long credits)
    {
        using var tx = dbe.CreateQuickTransaction();
        var target = tx.OpenMut(id);
        var walletCopy = target.Read(PcWalletArch.Wallet);
        walletCopy.Credits = credits;
        target.Set(PcWalletArch.Wallet, walletCopy);
        tx.Commit();
    }

    /// <summary>Compacts one revision chain the way the deferred cleanup does, and returns how many entries it removed.</summary>
    private static int Compact(ComponentTable table, int firstChunkId)
    {
        var revisions = table.CompRevTableSegment.CreateChunkAccessor();
        var contents = table.ComponentSegment.CreateChunkAccessor();
        try
        {
            ref var header = ref revisions.GetChunk<CompRevStorageHeader>(firstChunkId, true);
            var before = header.ItemCount;
            Assert.That(header.Control.TryEnterExclusiveAccess(), Is.True, "nothing holds the chain at this point of the publish");
            try
            {
                ComponentRevisionManager.CleanUpUnusedEntriesCore(table, firstChunkId, long.MaxValue, ref revisions, ref contents);
            }
            finally
            {
                header = ref revisions.GetChunk<CompRevStorageHeader>(firstChunkId);
                header.Control.ExitExclusiveAccess();
            }

            return before - revisions.GetChunk<CompRevStorageHeader>(firstChunkId).ItemCount;
        }
        finally
        {
            contents.Dispose();
            revisions.Dispose();
        }
    }

    /// <summary>The shared assertion: the value a fresh transaction reads is the last one committed.</summary>
    private static void AssertSees(DatabaseEngine dbe, EntityId id, long expected)
    {
        using var r = dbe.CreateReadOnlyTransaction();
        var seen = r.Open(id).Read(PcWalletArch.Wallet).Credits;
        Assert.That(seen, Is.EqualTo(expected),
            $"{Ap05Marker}: the last commit wrote {expected}, a transaction begun after it reads {seen}. The publish stamped the position its PREPARE recorded, "
            + "a compaction had moved the entry since, so the commit's own entry stayed isolated — invisible to every reader, for good (#1158).");
    }

    /// <summary>The chain's bookkeeping, read under an epoch: entries still isolated, and the header's LCRI, first index, count and commit sequence.</summary>
    private static (int Isolated, short Lcri, short First, short Count, int CommitSequence) ReadChain(DatabaseEngine dbe, ComponentTable table, int root)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var revisions = table.CompRevTableSegment.CreateChunkAccessor();
        try
        {
            var isolated = 0;
            using (var en = new RevisionEnumerator(ref revisions, root, false, true, WaitContext.FromTimeout(TimeSpan.FromSeconds(5))))
            {
                while (en.MoveNext())
                {
                    isolated += en.Current.IsolationFlag ? 1 : 0;
                }
            }

            ref var h = ref revisions.GetChunk<CompRevStorageHeader>(root);
            return (isolated, h.LastCommitRevisionIndex, h.FirstItemIndex, h.ItemCount, h.CommitSequence);
        }
        finally
        {
            revisions.Dispose();
        }
    }

    /// <summary>
    /// Spawns an entity and commits <paramref name="priorUpdates"/> updates under a reader held open, so a cleanup can shorten the chain they build; then
    /// commits one more — an update, or a destroy — with a compaction staged between that commit's PREPARE and its PUBLISH.
    /// </summary>
    /// <remarks>
    /// One prior update leaves the pending entry in the chain's root chunk (index 2 of 3), so the publish's identity check on the recorded slot is what
    /// decides; two leave it in an overflow chunk, whose coordinates are never trusted. The destroy's entry carries no content chunk, and is matched by TSN.
    /// </remarks>
    private void RunStagedRace(bool prefixPublish, int priorUpdates, bool destroy)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        dbe.PublishTrustsPrepareCoordinatesForTest = prefixPublish;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<PcWalletArch>(PcWalletArch.Wallet.Set(new PcWallet { Credits = 0 }));
            tx.Commit();
        }

        var table = dbe.GetComponentTable<PcWallet>();
        var removed = -1;
        var root = 0;
        using (dbe.CreateReadOnlyTransaction())
        {
            for (var i = 1; i <= priorUpdates; i++)
            {
                Commit(dbe, id, i);
            }

            dbe.PublishComponentProbe = (t, r) =>
            {
                if (ReferenceEquals(t, table) && removed < 0)
                {
                    root = r;
                    removed = Compact(t, r);
                }
            };

            if (destroy)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Destroy(id);
                tx.Commit();
            }
            else
            {
                Commit(dbe, id, priorUpdates + 1);
            }

            dbe.PublishComponentProbe = null;

            Assert.That(removed, Is.GreaterThan(0), "the staging must actually move the pending entry, or the test proves nothing");
            var chain = ReadChain(dbe, table, root);
            Assert.That(chain.Isolated, Is.Zero,
                $"{Ap05Marker}: an entry of the chain is still isolated after its commit returned — the publish stamped another slot (#1158)");
            Assert.That(chain.Lcri, Is.EqualTo(chain.First + chain.Count - 1), "the last commit is the chain's last entry, and LCRI names it");
            Assert.That(chain.CommitSequence, Is.EqualTo(priorUpdates + 2), "one CommitSequence per commit: the spawn, the prior updates and the raced one");
        }

        if (destroy)
        {
            using var r = dbe.CreateReadOnlyTransaction();
            Assert.That(r.TryOpen(id, out _), Is.False, $"{Ap05Marker}: the destroy committed, yet a transaction begun after it still sees the entity");
        }
        else
        {
            AssertSees(dbe, id, priorUpdates + 1);
        }
    }

    [Test]
    [VerifiesRule("AP-05")]
    public void ACompactionBetweenPrepareAndPublish_LeavesTheCommitVisible() => RunStagedRace(prefixPublish: false, priorUpdates: 2, destroy: false);

    /// <summary>The pending entry in the root chunk: the recorded slot is checked, found vacated by the compaction, and the entry re-found.</summary>
    [Test]
    [VerifiesRule("AP-05")]
    public void ACompactionBetweenPrepareAndPublish_InTheRootChunk_LeavesTheCommitVisible() =>
        RunStagedRace(prefixPublish: false, priorUpdates: 1, destroy: false);

    /// <summary>A destroy's entry has no content chunk: the publish identifies it by its TSN.</summary>
    [Test]
    [VerifiesRule("AP-05")]
    public void ACompactionBetweenPrepareAndPublish_OfADestroy_LeavesTheEntityGone() =>
        RunStagedRace(prefixPublish: false, priorUpdates: 2, destroy: true);

    /// <summary>The <see cref="RuleMutantAttribute"/> companion: the same race against a publish that trusts its PREPARE coordinates, which the verifier
    /// must reject — in an overflow chunk and in the root chunk.</summary>
    [Test]
    [RuleMutant("AP-05")]
    public void APublishThatTrustsItsPrepareCoordinates_LosesTheCommit()
    {
        RuleMutants.AssertDetects("AP-05", Ap05Marker, () => RunStagedRace(prefixPublish: true, priorUpdates: 2, destroy: false));
        RuleMutants.AssertDetects("AP-05", Ap05Marker, () => RunStagedRace(prefixPublish: true, priorUpdates: 1, destroy: false));
    }

    /// <summary>
    /// The shape it was found in: threads take turns, under one lock per entity, reading an entity's credits in a fresh transaction and writing them back
    /// plus one. Every read must see the commit that preceded it under the same lock; before the fix about 1 read in 200 did not.
    /// </summary>
    [Test]
    public void SerializedReadModifyWrites_AcrossThreads_AlwaysSeeThePreviousCommit()
    {
        const int threads = 4;
        const int entities = 16;
        const int perThread = 1500;

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);

        var ids = new EntityId[entities];
        var expected = new long[entities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < entities; i++)
            {
                ids[i] = tx.Spawn<PcWalletArch>(PcWalletArch.Wallet.Set(new PcWallet()));
            }

            tx.Commit();
        }

        var locks = new object[entities];
        for (var i = 0; i < entities; i++)
        {
            locks[i] = new object();
        }

        var stale = 0;
        Exception failure = null;
        var workers = new Thread[threads];
        for (var w = 0; w < threads; w++)
        {
            var seed = 945 + w;
            workers[w] = new Thread(() =>
            {
                try
                {
                    var rng = new Random(seed);
                    for (var k = 0; k < perThread; k++)
                    {
                        var e = rng.Next(entities);
                        lock (locks[e])
                        {
                            using var tx = dbe.CreateQuickTransaction();
                            if (tx.Open(ids[e]).Read(PcWalletArch.Wallet).Credits != expected[e])
                            {
                                Interlocked.Increment(ref stale);
                            }

                            var opened = tx.OpenMut(ids[e]);
                            var wallet = opened.Read(PcWalletArch.Wallet);
                            wallet.Credits = expected[e] + 1;
                            opened.Set(PcWalletArch.Wallet, wallet);
                            tx.Commit();
                            expected[e]++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            }) { IsBackground = true };
        }

        foreach (var t in workers)
        {
            t.Start();
        }

        // Bounded: the publish waits for its chain lock without a deadline, so a regression that deadlocks must fail this test, not hang its shard.
        foreach (var t in workers)
        {
            Assert.That(t.Join(TimeSpan.FromSeconds(30)), Is.True, "a worker is stuck — the publish's unbounded chain-lock wait never ended");
        }

        Assert.That(failure, Is.Null);
        Assert.That(stale, Is.Zero, $"{stale} of {threads * perThread} transactions did not see the commit that preceded them under the same lock (#1158)");
    }
}
