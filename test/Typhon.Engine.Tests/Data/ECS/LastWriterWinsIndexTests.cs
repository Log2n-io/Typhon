using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Rule <b>AP-06</b> (<c>rules/durability.md</c>): an entity's index entries follow its newest committed revision, whatever the order of two
/// last-writer-wins updates (#1263).
/// </summary>
/// <remarks>
/// Without a conflict handler two transactions may update one entity concurrently, each from the revision it read. Each publish moved the entity's index
/// entries from the READ value. The first moved them off it, so the second found nothing to move and changed nothing: the entity read the second writer's
/// value while its unique key stayed the first writer's — unreachable by its value, and the first writer's key held by an entity that no longer had it.
/// </remarks>
[TestFixture]
class LastWriterWinsIndexTests : TestBase<LastWriterWinsIndexTests>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private DatabaseEngine OpenEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId Seed(DatabaseEngine dbe, CompD value)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<CompDArch>(CompDArch.D.Set(value));
        tx.Commit();
        return id;
    }

    private static void Write(Transaction tx, EntityId id, CompD value) => tx.OpenMut(id).Set(CompDArch.D, value);

    private static List<long> UniqueHolders(DatabaseEngine dbe, int b)
    {
        using var tx = dbe.CreateQuickTransaction();
        var found = new List<long>();
        using var e = tx.EnumerateIndex<CompD, int>(dbe.GetIndexRef<CompD, int>(x => x.B), b, b);
        while (e.MoveNext())
        {
            found.Add(e.Current.EntityPK);
        }

        return found;
    }

    private static List<long> MultiHolders(DatabaseEngine dbe, double c)
    {
        using var tx = dbe.CreateQuickTransaction();
        var found = new List<long>();
        using var e = tx.EnumerateIndex<CompD, double>(dbe.GetIndexRef<CompD, double>(x => x.C), c, c);
        while (e.MoveNext())
        {
            found.Add(e.Current.EntityPK);
        }

        return found;
    }

    /// <summary>The archetype's tree indexing the <see cref="CompD"/> field at <paramref name="offset"/>.</summary>
    private static BTreeBase<PersistentStore> TreeAt(DatabaseEngine dbe, int offset)
    {
        foreach (var ixSlot in dbe._archetypeStates[Archetype<CompDArch>.Metadata.ArchetypeId].ClusterState.IndexSlots)
        {
            foreach (var field in ixSlot.Fields)
            {
                if (field.FieldOffset == offset)
                {
                    return field.Index;
                }
            }
        }

        throw new InvalidOperationException($"no index at offset {offset}");
    }

    /// <summary>The raw elements under <paramref name="c"/> in the tree on <see cref="CompD.C"/> — a dead entity's included, which a query drops.</summary>
    private static unsafe int RawMultiEntries(DatabaseEngine dbe, double c)
    {
        var tree = TreeAt(dbe, 8);
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tree.Segment.CreateChunkAccessor();
        try
        {
            var buffer = tree.TryGetMultiple(&c, ref accessor);
            try
            {
                var n = 0;
                if (buffer.IsValid)
                {
                    do
                    {
                        n += buffer.ReadOnlyElements.Length;
                    }
                    while (buffer.NextChunk());
                }

                return n;
            }
            finally
            {
                buffer.Dispose();
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>The raw elements under <paramref name="tier"/> in <see cref="StatsArch"/>'s tree on <see cref="StatsRanked.Tier"/>.</summary>
    private static int RawTierEntries(DatabaseEngine dbe, int tier) => RawFirstFieldEntries<StatsArch>(dbe, tier);

    /// <summary>The raw elements under <paramref name="key"/> in the tree of <typeparamref name="TArch"/>'s first indexed field (AllowMultiple int).</summary>
    private static unsafe int RawFirstFieldEntries<TArch>(DatabaseEngine dbe, int key) where TArch : Archetype<TArch>
    {
        var tier = key;
        var tree = dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState.IndexSlots[0].Fields[0].Index;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tree.Segment.CreateChunkAccessor();
        try
        {
            var buffer = tree.TryGetMultiple(&tier, ref accessor);
            try
            {
                var n = 0;
                if (buffer.IsValid)
                {
                    do
                    {
                        n += buffer.ReadOnlyElements.Length;
                    }
                    while (buffer.NextChunk());
                }

                return n;
            }
            finally
            {
                buffer.Dispose();
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Whether the tree on <see cref="CompD.B"/> holds <paramref name="b"/> at all — for a dead entity too, which a query drops.</summary>
    private static unsafe bool RawUniqueHeld(DatabaseEngine dbe, int b)
    {
        var tree = TreeAt(dbe, 4);
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tree.Segment.CreateChunkAccessor();
        try
        {
            return tree.TryGet(&b, ref accessor).IsSuccess;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Both read B=60; T1 commits 30, then T2 commits 40. The entity reads 40, and the unique index holds it under 40 alone.</summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void TwoUpdatesFromOneRead_LeaveTheUniqueIndexOnTheLastWritersKey()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        var pk = (long)x.RawValue;
        using (var t1 = dbe.CreateQuickTransaction())
        using (var t2 = dbe.CreateQuickTransaction())
        {
            Write(t1, x, new CompD(1f, 30, 1.0));
            Write(t2, x, new CompD(1f, 40, 1.0));
            Assert.That(t1.Commit() && t2.Commit(), Is.True, "premise: without a handler both commit, the last writer winning");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(x).Read(CompDArch.D).B, Is.EqualTo(40), "premise: the entity reads the last writer's value");
        }

        Assert.That(UniqueHolders(dbe, 40), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(UniqueHolders(dbe, 30), Is.Empty, "the first writer's key is still held by an entity that no longer has it");
        Assert.That(UniqueHolders(dbe, 60), Is.Empty, "the read key is still held");
    }

    /// <summary>The same on an AllowMultiple index: both read C=6; T1 commits 3, then T2 commits 4. The entity is under 4 alone.</summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void TwoUpdatesFromOneRead_LeaveTheMultiValueIndexOnTheLastWritersKey()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 6.0));
        var pk = (long)x.RawValue;
        using (var t1 = dbe.CreateQuickTransaction())
        using (var t2 = dbe.CreateQuickTransaction())
        {
            Write(t1, x, new CompD(1f, 60, 3.0));
            Write(t2, x, new CompD(1f, 60, 4.0));
            Assert.That(t1.Commit() && t2.Commit(), Is.True, "premise: without a handler both commit, the last writer winning");
        }

        Assert.That(MultiHolders(dbe, 4.0), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(MultiHolders(dbe, 3.0), Is.Empty, "the first writer's key still names the entity");
        Assert.That(MultiHolders(dbe, 6.0), Is.Empty, "the read key still names the entity");
    }

    /// <summary>
    /// Both read B=60; T1 commits 30, then T2 commits its fields as it read them. The keys T2 writes are its read ones, so its PREPARE owes no index move —
    /// but the newest committed key is T1's 30, and the entity now reads 60.
    /// </summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void AnUpdateThatKeepsItsReadKey_MovesTheEntryBackFromAnInterveningCommit()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        var pk = (long)x.RawValue;
        using (var t1 = dbe.CreateQuickTransaction())
        using (var t2 = dbe.CreateQuickTransaction())
        {
            Write(t1, x, new CompD(1f, 30, 1.0));
            Write(t2, x, new CompD(1f, 60, 1.0));
            Assert.That(t1.Commit() && t2.Commit(), Is.True, "premise: without a handler both commit, the last writer winning");
        }

        Assert.That(UniqueHolders(dbe, 60), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(UniqueHolders(dbe, 30), Is.Empty, "the intervening commit's key is still held");
    }

    /// <summary>
    /// X moves from 60, the largest key, to 30; a spawn then takes 60. The tree's last-key cache still said 60, the append read it as a duplicate past the
    /// commit's append, and the engine stopped (IXW-11).
    /// </summary>
    [Test]
    [VerifiesRule("IXW-11")]
    public void ASpawnOntoTheLargestKeyAnUpdateFreed_Commits()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        using (var tx = dbe.CreateQuickTransaction())
        {
            Write(tx, x, new CompD(1f, 30, 1.0));
            Assert.That(tx.Commit(), Is.True, "premise: X moves to 30");
        }

        var y = Seed(dbe, new CompD(1f, 60, 1.0));
        Assert.That(dbe.CommitPipelineFailure, Is.Null, "the engine stopped committing");
        Assert.That(UniqueHolders(dbe, 60), Is.EqualTo(new[] { (long)y.RawValue }), "Y is not under its key");
        Assert.That(UniqueHolders(dbe, 30), Is.EqualTo(new[] { (long)x.RawValue }), "X is not under its key");
    }

    /// <summary>
    /// T2 reads X at B=60 and writes it unchanged; T1 then moves X to 30 and Y takes 60. T2's commit moves X back to 60, which Y holds: it is rejected before
    /// its append, as any write onto a held key is, and the engine goes on. Measured against its read, T2 moved no key, nothing checked 60, and the publish's
    /// backstop threw past the point of no return, which stops the engine (AP-03).
    /// </summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void AnUpdateBackOntoAKeyTakenSinceItsRead_IsRejectedBeforeTheAppend()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        EntityId y;
        using (var t2 = dbe.CreateQuickTransaction())
        {
            Write(t2, x, new CompD(1f, 60, 1.0));
            using (var t1 = dbe.CreateQuickTransaction())
            {
                Write(t1, x, new CompD(1f, 30, 1.0));
                Assert.That(t1.Commit(), Is.True, "premise: T1 moves X to 30");
            }

            y = Seed(dbe, new CompD(1f, 60, 1.0));
            Assert.Throws<UniqueConstraintViolationException>(() => t2.Commit(), "a write back onto a key taken since its read is not rejected before the append");
        }

        Assert.That(dbe.CommitPipelineFailure, Is.Null, "the engine stopped committing");
        Assert.That(UniqueHolders(dbe, 30), Is.EqualTo(new[] { (long)x.RawValue }), "X is not under the key it reads");
        Assert.That(UniqueHolders(dbe, 60), Is.EqualTo(new[] { (long)y.RawValue }), "Y is not under its key");
    }

    /// <summary>
    /// T2 reads X at B=60 and writes it unchanged; between its PREPARE and its append, T1 moves X to 30 and commits. T2's PREPARE saw 60 as the newest, owed
    /// no move, and is the newest commit: its publish must find T1's 30 and move X back to 60.
    /// </summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void ACommitLandingBetweenPrepareAndPublish_IsMovedBackFrom()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        var pk = (long)x.RawValue;
        Exception failure = null;
        var fired = false;
        dbe.CommitBeforeAppendProbe = () =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            var other = new Thread(() =>
            {
                try
                {
                    using var t1 = dbe.CreateQuickTransaction();
                    Write(t1, x, new CompD(1f, 30, 1.0));
                    if (!t1.Commit())
                    {
                        throw new InvalidOperationException("premise: T1 commits");
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }) { IsBackground = true };
            other.Start();
            if (!other.Join(Patience))
            {
                failure = new TimeoutException("T1 did not commit while T2 sat before its append");
            }
        };

        try
        {
            using var t2 = dbe.CreateQuickTransaction();
            Write(t2, x, new CompD(1f, 60, 1.0));
            Assert.That(t2.Commit(), Is.True, "premise: T2 commits");
        }
        finally
        {
            dbe.CommitBeforeAppendProbe = null;
        }

        Assert.That(fired && failure == null, Is.True, $"premise: T1 committed between T2's PREPARE and its append ({failure?.Message})");
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(x).Read(CompDArch.D).B, Is.EqualTo(60), "premise: T2's value is the newest committed");
        }

        Assert.That(UniqueHolders(dbe, 60), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(UniqueHolders(dbe, 30), Is.Empty, "T1's key is still held");
    }

    /// <summary>What a racer staged inside a spawn's publish did (<see cref="SpawnRacedBy"/>).</summary>
    private sealed class SpawnRace
    {
        public bool Staged;
        public bool Joined;
        public bool Committed;
        public Exception Failure;
    }

    /// <summary>
    /// Spawns <paramref name="spawned"/> in T1 and, inside T1's publish — its record stamped and its slot published, its B+Tree entries and tails not yet
    /// written — runs <paramref name="racer"/> on its own thread. T1 goes on once the racer waits for the spawn's publish to finish, or has finished.
    /// </summary>
    private static EntityId SpawnRacedBy(DatabaseEngine dbe, CompD spawned, Func<EntityId, bool> racer, out SpawnRace race)
    {
        var cs = dbe._archetypeStates[Archetype<CompDArch>.Metadata.ArchetypeId].ClusterState;
        var parked = new ManualResetEventSlim();
        var r = new SpawnRace();
        Thread thread = null;
        cs.SlotIndexLockWaitProbe = () => parked.Set();
        dbe.SpawnBeforeIndexesProbe = id =>
        {
            if (thread != null)
            {
                return;
            }

            thread = new Thread(() =>
            {
                try
                {
                    r.Committed = racer(id);
                }
                catch (Exception ex)
                {
                    r.Failure = ex;
                }
                finally
                {
                    parked.Set();
                }
            }) { IsBackground = true };
            thread.Start();
            r.Staged = parked.Wait(Patience);
        };

        EntityId e;
        try
        {
            using var t1 = dbe.CreateQuickTransaction();
            e = t1.Spawn<CompDArch>(CompDArch.D.Set(spawned));
            Assert.That(t1.Commit(), Is.True, "premise: T1 commits");
        }
        finally
        {
            dbe.SpawnBeforeIndexesProbe = null;
        }

        r.Joined = thread != null && thread.Join(Patience);
        cs.SlotIndexLockWaitProbe = null;
        race = r;
        return e;
    }

    /// <summary>
    /// A spawn is visible from its commit's card, before its PUBLISH writes its B+Tree entries and element-id tails. An update committed in that window,
    /// T2's, must leave the entity indexed once, under T2's keys (#1370, #1369).
    /// </summary>
    /// <remarks>
    /// T2's move from the spawn's keys missed — the tail not written — and reindexed under T2's; the spawn's PUBLISH then indexed the slot's bytes, which
    /// T2's cluster copy had made T2's: two entries under T2's multi-value key, the buffer's count off by one. The racer runs from inside T1's publish.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-08")]
    public void AnUpdateOfASpawnBetweenItsStampAndItsIndexes_LeavesItIndexedOnce()
    {
        using var dbe = OpenEngine();
        Seed(dbe, new CompD(1f, 10, 9.0));   // the archetype's trees and buffers exist before the race

        var e = SpawnRacedBy(dbe, new CompD(1f, 80, 3.0), id =>
        {
            using var tx = dbe.CreateQuickTransaction();
            if (!tx.TryOpen(id, out _))
            {
                throw new InvalidOperationException("premise: the publishing spawn is visible to a new transaction");
            }

            Write(tx, id, new CompD(1f, 81, 4.0));
            return tx.Commit();
        }, out var race);

        Assert.That(race.Staged && race.Joined && race.Failure == null && race.Committed, Is.True,
            $"premise: T2 updated the spawn inside its publish ({race.Failure?.Message})");
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(e).Read(CompDArch.D).B, Is.EqualTo(81), "premise: T2's value is the newest committed");
        }

        var pk = (long)e.RawValue;
        Assert.Multiple(() =>
        {
            Assert.That(MultiHolders(dbe, 4.0), Is.EqualTo(new[] { pk }), "indexed once, under T2's multi-value key");
            Assert.That(RawMultiEntries(dbe, 4.0), Is.EqualTo(1), "one element under T2's multi-value key");
            Assert.That(RawMultiEntries(dbe, 3.0), Is.Zero, "not left under the spawn's multi-value key");
            Assert.That(UniqueHolders(dbe, 81), Is.EqualTo(new[] { pk }), "indexed under T2's unique key");
            Assert.That(RawUniqueHeld(dbe, 80), Is.False, "not left under the spawn's unique key");
        });
    }

    /// <summary>
    /// An update committed while the spawn it updates has published its slot and entries but not yet its revisions: the update must not deadlock with the
    /// spawner, and moves the entity off the spawn's keys (#1370).
    /// </summary>
    /// <remarks>
    /// The spawner publishes its revisions before its spawns. A writer that saw the spawn through the card and waited for the spawn's publish holding the
    /// entity's chain lock — taken at its publish, or retained from its PREPARE with a conflict handler — deadlocked with the spawner's revision publish,
    /// which waits for that lock: so it waits before taking it. T1 runs on its own thread, so a deadlock fails the test instead of hanging it.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-08")]
    public void AnUpdateOfASpawnWhoseRevisionsAreNotPublishedYet_NeitherDeadlocksNorLeavesTheSpawnsEntry([Values] bool withHandler)
    {
        using var dbe = OpenEngine();
        Seed(dbe, new CompD(1f, 10, 9.0));
        var table = dbe.GetComponentTable<CompD>();
        var cs = dbe._archetypeStates[Archetype<CompDArch>.Metadata.ArchetypeId].ClusterState;

        var parked = new ManualResetEventSlim();
        var race = new SpawnRace();
        Thread t2 = null;
        var spawned = default(EntityId);
        cs.SlotIndexLockWaitProbe = () => parked.Set();
        dbe.PublishComponentProbe = (t, firstChunkId) =>
        {
            // T1's revision publish: its spawn's slot and entries are published, its revision not yet stamped.
            if (!ReferenceEquals(t, table) || t2 != null || spawned == default)
            {
                return;
            }

            t2 = new Thread(() =>
            {
                try
                {
                    using var tx = dbe.CreateQuickTransaction();
                    if (!tx.TryOpen(spawned, out _))
                    {
                        throw new InvalidOperationException("premise: the publishing spawn is visible to a new transaction");
                    }

                    Write(tx, spawned, new CompD(1f, 81, 4.0));
                    race.Committed = withHandler ? tx.Commit(static (ref ConcurrencyConflictSolver _) => { }) : tx.Commit();
                }
                catch (Exception ex)
                {
                    race.Failure = ex;
                }
                finally
                {
                    parked.Set();
                }
            }) { IsBackground = true };
            t2.Start();
            race.Staged = parked.Wait(Patience);
        };

        var t1Committed = false;
        Exception t1Failure = null;
        var t1 = new Thread(() =>
        {
            try
            {
                using var tx = dbe.CreateQuickTransaction();
                spawned = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 80, 3.0)));
                t1Committed = tx.Commit();
            }
            catch (Exception ex)
            {
                t1Failure = ex;
            }
        }) { IsBackground = true };
        t1.Start();
        var t1Done = t1.Join(Patience);
        race.Joined = t2 != null && t2.Join(Patience);
        dbe.PublishComponentProbe = null;
        cs.SlotIndexLockWaitProbe = null;

        Assert.That(t1Done, Is.True, "T1's commit did not finish: deadlocked with T2");
        Assert.That(t1Failure == null && t1Committed, Is.True, $"premise: T1 commits ({t1Failure?.Message})");
        Assert.That(race.Staged && race.Joined && race.Failure == null && race.Committed, Is.True,
            $"premise: T2 updated the spawn inside T1's revision publish ({race.Failure?.Message})");
        var pk = (long)spawned.RawValue;
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(spawned).Read(CompDArch.D).B, Is.EqualTo(81), "premise: T2's value is the newest committed");
        }

        Assert.Multiple(() =>
        {
            Assert.That(MultiHolders(dbe, 4.0), Is.EqualTo(new[] { pk }), "indexed once, under T2's multi-value key");
            Assert.That(RawMultiEntries(dbe, 4.0), Is.EqualTo(1), "one element under T2's multi-value key");
            Assert.That(RawMultiEntries(dbe, 3.0), Is.Zero, "not left under the spawn's multi-value key");
            Assert.That(UniqueHolders(dbe, 81), Is.EqualTo(new[] { pk }), "indexed under T2's unique key");
            Assert.That(RawUniqueHeld(dbe, 80), Is.False, "not left under the spawn's unique key");
        });
    }

    /// <summary>
    /// A destroy committed while the spawn it destroys is still publishing leaves no index entry and no occupied slot behind (#1370).
    /// </summary>
    /// <remarks>
    /// The destroy removed entries the spawn had not written yet and hid a slot not occupied yet; the spawn's PUBLISH then set the occupancy bit and added
    /// its entries, for an entity already dead: counted by every newer scan, found by its keys.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-08")]
    public void ADestroyOfASpawnStillPublishing_LeavesNoEntryAndNoOccupiedSlot()
    {
        using var dbe = OpenEngine();
        Seed(dbe, new CompD(1f, 10, 9.0));

        var e = SpawnRacedBy(dbe, new CompD(1f, 80, 3.0), id =>
        {
            using var tx = dbe.CreateQuickTransaction();
            if (!tx.TryOpen(id, out _))
            {
                throw new InvalidOperationException("premise: the publishing spawn is visible to a new transaction");
            }

            tx.Destroy(id);
            return tx.Commit();
        }, out var race);

        Assert.That(race.Staged && race.Joined && race.Failure == null && race.Committed, Is.True,
            $"premise: T2 destroyed the spawn inside its publish ({race.Failure?.Message})");
        var pk = (long)e.RawValue;
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.TryOpen(e, out _), Is.False, "premise: the spawn is destroyed");
        Assert.Multiple(() =>
        {
            Assert.That(tx.Query<CompDArch>().Count(), Is.EqualTo(1), "the seed alone is alive: the destroyed spawn's slot is not occupied");
            Assert.That(RawMultiEntries(dbe, 3.0), Is.Zero, "no multi-value entry left behind");
            Assert.That(RawUniqueHeld(dbe, 80), Is.False, "no unique entry left behind");
        });
    }

    /// <summary>
    /// An update stamped after a destroy of the same entity, prepared before it and published between the destroy's tombstone and its index removal, leaves
    /// no entry behind (#1370, #1369).
    /// </summary>
    /// <remarks>
    /// The update's publish rightly moved no index entry over the tombstone — the destroy owns them — but it copied its value into the destroyed entity's
    /// cluster slot all the same, and the destroy reads the keys to remove from that slot. It removed the update's keys, which the index never held: a unique
    /// entry left for good, and an AllowMultiple element taken out of whatever buffer its chunk now belonged to, that buffer's count off by one. Staged: the
    /// update (T2) is held between its PREPARE and its append while the destroy (T1) commits; T1 releases it after its revisions are published, before its
    /// destroy's index removal. Prepared after T1's, T2 would not copy: the destroy is pending by then.
    /// </remarks>
    [Test]
    [VerifiesRule("AP-06")]
    public void AnUpdateStampedAfterADestroy_PublishedBeforeItsIndexRemoval_LeavesNoEntryBehind()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));

        using var t2Prepared = new ManualResetEventSlim();
        using var t2Go = new ManualResetEventSlim();
        Exception t2Failure = null;
        var t2Committed = false;
        var t2ThreadId = 0;
        dbe.CommitBeforeAppendProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref t2ThreadId))
            {
                t2Prepared.Set();
                t2Go.Wait(Patience);
            }
        };

        var t2 = new Thread(() =>
        {
            try
            {
                Volatile.Write(ref t2ThreadId, Environment.CurrentManagedThreadId);
                using var tx = dbe.CreateQuickTransaction();
                Write(tx, x, new CompD(1f, 70, 2.0));   // read alive: T1 has not committed
                t2Committed = tx.Commit();
            }
            catch (Exception ex)
            {
                t2Failure = ex;
            }
        }) { IsBackground = true };
        t2.Start();

        var t2Ran = false;
        try
        {
            Assert.That(t2Prepared.Wait(Patience), Is.True, "premise: T2 prepared before T1 committed");
            dbe.DestroysBeforeIndexRemovalProbe = () =>
            {
                // T1's tombstone is published, its index removal not yet run; T2 appends after T1 did, so its stamp is the higher.
                dbe.DestroysBeforeIndexRemovalProbe = null;
                t2Go.Set();
                t2Ran = t2.Join(Patience);
            };

            using var t1 = dbe.CreateQuickTransaction();
            t1.Destroy(x);
            Assert.That(t1.Commit(), Is.True, "premise: T1 commits");
        }
        finally
        {
            dbe.DestroysBeforeIndexRemovalProbe = null;
            dbe.CommitBeforeAppendProbe = null;
            t2Go.Set();
        }

        Assert.That(t2Ran && t2Failure == null && t2Committed, Is.True,
            $"premise: T2 committed between T1's tombstone and its index removal ({t2Failure?.Message})");
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpen(x, out _), Is.False, "premise: x is destroyed");
        }

        Assert.Multiple(() =>
        {
            Assert.That(RawUniqueHeld(dbe, 60), Is.False, "the destroyed entity's unique entry was left behind");
            Assert.That(RawMultiEntries(dbe, 1.0), Is.Zero, "the destroyed entity's multi-value entry was left behind");
            Assert.That(RawUniqueHeld(dbe, 70), Is.False, "T2's unique key, which the index never held");
            Assert.That(RawMultiEntries(dbe, 2.0), Is.Zero, "T2's multi-value key, which the index never held");
        });
    }

    /// <summary>
    /// A destroy of a SingleVersion entity committed while its spawn is still publishing leaves no index entry and no occupied slot behind (#1370).
    /// </summary>
    /// <remarks>
    /// The Versioned twin above is covered twice over — its destroy's tombstone publish waits for the spawn too. A SingleVersion destroy has no tombstone: its
    /// index removal alone must wait for the spawn's PUBLISH, or it removes entries not written yet and releases a slot not occupied yet, which the spawn then
    /// indexes and occupies for a dead entity.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-08")]
    public void ADestroyOfASingleVersionSpawnStillPublishing_LeavesNoEntryAndNoOccupiedSlot()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AxSvMulti>();
        dbe.InitializeArchetypes();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(new AxSvMulti { Key = 1, Bucket = 7 }));
            Assert.That(tx.Commit(), Is.True, "premise: the seed commits");
        }

        var cs = dbe._archetypeStates[Archetype<AxPureSvMulti>.Metadata.ArchetypeId].ClusterState;
        var parked = new ManualResetEventSlim();
        var race = new SpawnRace();
        Thread t2 = null;
        cs.SlotIndexLockWaitProbe = () => parked.Set();
        dbe.SpawnBeforeIndexesProbe = id =>
        {
            if (t2 != null)
            {
                return;
            }

            t2 = new Thread(() =>
            {
                try
                {
                    using var tx = dbe.CreateQuickTransaction();
                    if (!tx.TryOpen(id, out _))
                    {
                        throw new InvalidOperationException("premise: the publishing spawn is visible to a new transaction");
                    }

                    tx.Destroy(id);
                    race.Committed = tx.Commit();
                }
                catch (Exception ex)
                {
                    race.Failure = ex;
                }
                finally
                {
                    parked.Set();
                }
            }) { IsBackground = true };
            t2.Start();
            race.Staged = parked.Wait(Patience);
        };

        EntityId e;
        try
        {
            using var t1 = dbe.CreateQuickTransaction();
            e = t1.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(new AxSvMulti { Key = 2, Bucket = 3 }));
            Assert.That(t1.Commit(), Is.True, "premise: T1 commits");
        }
        finally
        {
            dbe.SpawnBeforeIndexesProbe = null;
        }

        race.Joined = t2 != null && t2.Join(Patience);
        cs.SlotIndexLockWaitProbe = null;
        Assert.That(race.Staged && race.Joined && race.Failure == null && race.Committed, Is.True,
            $"premise: T2 destroyed the spawn inside its publish ({race.Failure?.Message})");
        using var check = dbe.CreateQuickTransaction();
        Assert.That(check.TryOpen(e, out _), Is.False, "premise: the spawn is destroyed");
        Assert.Multiple(() =>
        {
            Assert.That(check.Query<AxPureSvMulti>().Count(), Is.EqualTo(1), "the seed alone is alive: the destroyed spawn's slot is not occupied");
            Assert.That(RawFirstFieldEntries<AxPureSvMulti>(dbe, 3), Is.Zero, "no entry left behind");
        });
    }

    /// <summary>
    /// A first supply prepared before a destroy of its entity and published after the destroy removed the entity's entries leaves no entry behind (#1370).
    /// </summary>
    /// <remarks>
    /// The entity was spawned without the component, so the destroy wrote no tombstone in its chain — there was none — and removed the entry the spawn made
    /// under the absent value's key. The supply then moved from that key, missed, and reindexed the dead entity under its own: an entry naming a hidden slot,
    /// inherited by the next entity placed there. Staged: the supply (T2) is held between its PREPARE and its append while the destroy (T1) commits.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-08")]
    public void AFirstSupplyPublishedAfterADestroyOfItsEntity_LeavesNoEntryBehind()
    {
        // StatsRanked: Versioned, its tier indexed AllowMultiple and nothing unique — the shape a spawn may leave absent (CompD's unique key may not be).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(1)));   // Ranked absent: indexed under the slot's bytes, tier 0
            Assert.That(tx.Commit(), Is.True, "premise: the absent spawn commits");
        }

        Assert.That(RawTierEntries(dbe, 0), Is.EqualTo(1), "premise: the absent component is indexed under the slot's bytes");

        using var t2Prepared = new ManualResetEventSlim();
        using var t2Go = new ManualResetEventSlim();
        Exception t2Failure = null;
        var t2Committed = false;
        var t2ThreadId = 0;
        dbe.CommitBeforeAppendProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref t2ThreadId))
            {
                t2Prepared.Set();
                t2Go.Wait(Patience);
            }
        };

        var t2 = new Thread(() =>
        {
            try
            {
                Volatile.Write(ref t2ThreadId, Environment.CurrentManagedThreadId);
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(x).Enable(StatsArch.Ranked, new StatsRanked(4, 1));   // a first supply: x is alive at T2's snapshot
                t2Committed = tx.Commit();
            }
            catch (Exception ex)
            {
                t2Failure = ex;
            }
        }) { IsBackground = true };
        t2.Start();

        bool t2Ran;
        try
        {
            Assert.That(t2Prepared.Wait(Patience), Is.True, "premise: T2 prepared before T1 committed");
            using (var t1 = dbe.CreateQuickTransaction())
            {
                t1.Destroy(x);
                Assert.That(t1.Commit(), Is.True, "premise: T1 commits");
            }
        }
        finally
        {
            dbe.CommitBeforeAppendProbe = null;
            t2Go.Set();
            t2Ran = t2.Join(Patience);
        }

        Assert.That(t2Ran && t2Failure == null && t2Committed, Is.True, $"premise: T2's supply committed after T1's destroy ({t2Failure?.Message})");
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpen(x, out _), Is.False, "premise: x is destroyed");
        }

        Assert.Multiple(() =>
        {
            Assert.That(RawTierEntries(dbe, 4), Is.Zero, "the supply's key indexes a destroyed entity");
            Assert.That(RawTierEntries(dbe, 0), Is.Zero, "the absent value's entry was left behind");
        });
    }

    /// <summary>
    /// A destroy whose index removal runs between a first supply's index move and its slot copy removes the supply's entry, not the absent value's (#1370).
    /// </summary>
    /// <remarks>
    /// The destroy prepared before the supply, so it wrote no tombstone — the component was absent — and nothing made it wait for the supply's chain lock. It
    /// read the key to remove from the slot, still the absent value's, and the tail, already the supply's: it took the supply's element out of the absent
    /// value's buffer accounting and left the supply's key indexing a dead entity. Staged: the destroy (T2) is held between its PREPARE and its append, and
    /// released from inside the supply's publish (T1), after its move and before its copy.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-08")]
    public void ADestroyBetweenAFirstSupplysMoveAndItsSlotCopy_LeavesNoEntryBehind()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(1)));
            Assert.That(tx.Commit(), Is.True, "premise: the absent spawn commits");
        }

        var cs = dbe._archetypeStates[Archetype<StatsArch>.Metadata.ArchetypeId].ClusterState;
        using var t2Prepared = new ManualResetEventSlim();
        using var t2Go = new ManualResetEventSlim();
        var parked = new ManualResetEventSlim();
        Exception t2Failure = null;
        var t2Committed = false;
        var t2ThreadId = 0;
        dbe.CommitBeforeAppendProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref t2ThreadId))
            {
                t2Prepared.Set();
                t2Go.Wait(Patience);
            }
        };

        var t2 = new Thread(() =>
        {
            try
            {
                Volatile.Write(ref t2ThreadId, Environment.CurrentManagedThreadId);
                using var tx = dbe.CreateQuickTransaction();
                tx.Destroy(x);   // x alive, its Ranked absent: no tombstone
                t2Committed = tx.Commit();
            }
            catch (Exception ex)
            {
                t2Failure = ex;
            }
            finally
            {
                parked.Set();
            }
        }) { IsBackground = true };
        t2.Start();

        var mainThread = Environment.CurrentManagedThreadId;
        var staged = false;
        bool t2Ran;
        try
        {
            Assert.That(t2Prepared.Wait(Patience), Is.True, "premise: T2 prepared before T1");
            cs.SlotIndexLockWaitProbe = () => parked.Set();
            dbe.PublishAfterIndexesProbe = () =>
            {
                // T1's supply has moved the entry and holds the slot's index lock; its slot copy comes next.
                if (Environment.CurrentManagedThreadId != mainThread || staged)
                {
                    return;
                }

                staged = true;
                t2Go.Set();
                parked.Wait(Patience);
            };

            using var t1 = dbe.CreateQuickTransaction();
            t1.OpenMut(x).Enable(StatsArch.Ranked, new StatsRanked(4, 1));
            Assert.That(t1.Commit(), Is.True, "premise: T1's supply commits");
        }
        finally
        {
            dbe.PublishAfterIndexesProbe = null;
            dbe.CommitBeforeAppendProbe = null;
            t2Go.Set();
            t2Ran = t2.Join(Patience);
            cs.SlotIndexLockWaitProbe = null;
        }

        Assert.That(staged && t2Ran && t2Failure == null && t2Committed, Is.True,
            $"premise: T2's destroy committed from inside T1's supply publish ({t2Failure?.Message})");
        Assert.Multiple(() =>
        {
            Assert.That(RawTierEntries(dbe, 4), Is.Zero, "the supply's key indexes a destroyed entity");
            Assert.That(RawTierEntries(dbe, 0), Is.Zero, "the absent value's entry was left behind");
        });
    }

    /// <summary>A transaction run on its own thread and held between its PREPARE and its append until released (<see cref="Release"/>).</summary>
    private sealed class HeldCommit : IDisposable
    {
        private readonly ManualResetEventSlim _prepared = new();
        private readonly ManualResetEventSlim _go = new();
        private readonly ManualResetEventSlim _done = new();
        private readonly Thread _thread;
        private int _threadId;
        public Exception Failure;
        public bool Committed;

        public HeldCommit(DatabaseEngine dbe, Action<Transaction> body)
        {
            var previous = dbe.CommitBeforeAppendProbe;
            dbe.CommitBeforeAppendProbe = () =>
            {
                previous?.Invoke();
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId))
                {
                    _prepared.Set();
                    _go.Wait(Patience);
                }
            };
            _thread = new Thread(() =>
            {
                try
                {
                    Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
                    using var tx = dbe.CreateQuickTransaction();
                    body(tx);
                    Committed = tx.Commit();
                }
                catch (Exception ex)
                {
                    Failure = ex;
                }
                finally
                {
                    _prepared.Set();
                    _done.Set();
                }
            }) { IsBackground = true };
            _thread.Start();
            Assert.That(_prepared.Wait(Patience), Is.True, "premise: the held transaction prepared");
        }

        public WaitHandle DoneHandle => _done.WaitHandle;

        public void Release() => _go.Set();

        public bool Join() => _thread.Join(Patience);

        public void Dispose()
        {
            _go.Set();
            _thread.Join(Patience);
        }
    }

    private DatabaseEngine OpenStatsEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// A destroy prepared before a component was supplied removes the entity's entries while an update of that component is between its move and its
    /// slot copy: it waits for the update and removes the update's entry (#1370).
    /// </summary>
    /// <remarks>
    /// The destroy wrote no tombstone in the supplied chain — it did not exist at its snapshot — so its tombstone publish never met the update's chain lock.
    /// It read the key to remove from the slot, still the supply's, and the tail, already the update's, and left the update's key indexing a dead entity. Its
    /// index removal now takes the chain lock of each chain it did not tombstone. Staged: the destroy (D) held before its append while the supply commits
    /// and the update (T1) publishes; D released from inside T1's publish, after its move.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-09")]
    public void ADestroyBetweenAnUpdatesMoveAndItsSlotCopy_OnAChainItDidNotTombstone_WaitsForTheUpdate()
    {
        using var dbe = OpenStatsEngine();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(1)));
            Assert.That(tx.Commit(), Is.True, "premise: the absent spawn commits");
        }

        using var d = new HeldCommit(dbe, tx => tx.Destroy(x));   // Ranked absent at D's snapshot: no tombstone for it
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(x).Enable(StatsArch.Ranked, new StatsRanked(4, 1));
            Assert.That(tx.Commit(), Is.True, "premise: the supply commits");
        }

        var parked = new ManualResetEventSlim();
        dbe.DestroyChainLockProbe = () => parked.Set();
        var mainThread = Environment.CurrentManagedThreadId;
        var staged = false;
        dbe.PublishAfterIndexesProbe = () =>
        {
            if (Environment.CurrentManagedThreadId != mainThread || staged)
            {
                return;
            }

            staged = true;
            d.Release();
            // D about to wait for this chain, or done without it.
            WaitHandle.WaitAny([parked.WaitHandle, new ManualResetEvent(false)], Patience);
        };
        try
        {
            using var t1 = dbe.CreateQuickTransaction();
            t1.OpenMut(x).Set(StatsArch.Ranked, new StatsRanked(2, 1));   // an update of the supplied component: not a supply
            Assert.That(t1.Commit(), Is.True, "premise: T1 commits");
        }
        finally
        {
            dbe.PublishAfterIndexesProbe = null;
            dbe.DestroyChainLockProbe = null;
            d.Release();
        }

        Assert.That(staged && d.Join() && d.Failure == null && d.Committed, Is.True, $"premise: D committed from inside T1's publish ({d.Failure?.Message})");
        Assert.Multiple(() =>
        {
            Assert.That(RawTierEntries(dbe, 2), Is.Zero, "the update's key indexes a destroyed entity");
            Assert.That(RawTierEntries(dbe, 4), Is.Zero, "the supply's entry was left behind");
        });
    }

    /// <summary>
    /// An update of a component supplied after a destroy's snapshot, prepared before the destroy published and published after it, moves nothing (#1370).
    /// </summary>
    /// <remarks>
    /// No tombstone in the chain — the destroy did not see it — so the update was the chain's newest and moved the entry the destroy had removed: it missed
    /// and reindexed the dead entity under the update's key. Under its chain lock it now sees the slot no longer holds the entity.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-09")]
    public void AnUpdatePublishedAfterADestroyThatDidNotTombstoneItsChain_MovesNothing()
    {
        using var dbe = OpenStatsEngine();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(1)));
            Assert.That(tx.Commit(), Is.True, "premise: the absent spawn commits");
        }

        var d = new HeldCommit(dbe, tx => tx.Destroy(x));
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(x).Enable(StatsArch.Ranked, new StatsRanked(4, 1));
            Assert.That(tx.Commit(), Is.True, "premise: the supply commits");
        }

        var m = new HeldCommit(dbe, tx => tx.OpenMut(x).Set(StatsArch.Ranked, new StatsRanked(2, 1)));
        try
        {
            d.Release();
            Assert.That(d.Join() && d.Failure == null && d.Committed, Is.True, $"premise: D committed ({d.Failure?.Message})");
            m.Release();
            Assert.That(m.Join() && m.Failure == null && m.Committed, Is.True, $"premise: the update committed after D ({m.Failure?.Message})");
        }
        finally
        {
            dbe.CommitBeforeAppendProbe = null;
            d.Dispose();
            m.Dispose();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpen(x, out _), Is.False, "premise: x is destroyed");
        }

        Assert.Multiple(() =>
        {
            Assert.That(RawTierEntries(dbe, 2), Is.Zero, "the update's key indexes a destroyed entity");
            Assert.That(RawTierEntries(dbe, 4), Is.Zero, "the supply's entry was left behind");
        });
    }

    /// <summary>
    /// A first supply that takes the slot's index lock after a destroy's index removal began waiting for nothing, and moves before the removal reads the
    /// slot's keys, is waited for by that removal (#1370).
    /// </summary>
    /// <remarks>
    /// The destroy's wait for a spawn still publishing found the slot free; the supply then locked it and moved its entry. Without a lock of its own the
    /// removal read the absent value's key from the slot and the supply's tail, and left the supply's key indexing a dead entity. Staged: the destroy (D)
    /// held past that wait, the supply (T1) releasing it from inside its publish, after its move.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-09")]
    public void ADestroyWhoseRemovalBeganBeforeASupplyLockedTheSlot_WaitsForTheSupply()
    {
        using var dbe = OpenStatsEngine();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(1)));
            Assert.That(tx.Commit(), Is.True, "premise: the absent spawn commits");
        }

        var cs = dbe._archetypeStates[Archetype<StatsArch>.Metadata.ArchetypeId].ClusterState;
        using var arrived = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var parked = new ManualResetEventSlim();
        var d = new HeldCommit(dbe, tx => tx.Destroy(x));   // Ranked absent at D's snapshot
        var dThread = 0;
        dbe.DestroyAfterSpawnWaitProbe = () =>
        {
            dThread = Environment.CurrentManagedThreadId;
            arrived.Set();
            go.Wait(Patience);
        };
        cs.SlotIndexLockWaitProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == dThread)
            {
                parked.Set();
            }
        };
        var mainThread = Environment.CurrentManagedThreadId;
        var staged = false;
        dbe.PublishAfterIndexesProbe = () =>
        {
            if (Environment.CurrentManagedThreadId != mainThread || staged)
            {
                return;
            }

            staged = true;
            go.Set();
            WaitHandle.WaitAny([parked.WaitHandle, d.DoneHandle], Patience);
        };
        try
        {
            using var t1 = dbe.CreateQuickTransaction();
            t1.OpenMut(x).Enable(StatsArch.Ranked, new StatsRanked(4, 1));   // x alive at T1's snapshot: before D's append
            d.Release();
            Assert.That(arrived.Wait(Patience), Is.True, "premise: D's removal passed its wait for a spawn");
            Assert.That(t1.Commit(), Is.True, "premise: T1's supply commits");
        }
        finally
        {
            dbe.PublishAfterIndexesProbe = null;
            dbe.DestroyAfterSpawnWaitProbe = null;
            dbe.CommitBeforeAppendProbe = null;
            cs.SlotIndexLockWaitProbe = null;
            go.Set();
            d.Dispose();
        }

        Assert.That(staged && d.Failure == null && d.Committed, Is.True, $"premise: D committed ({d.Failure?.Message})");
        Assert.Multiple(() =>
        {
            Assert.That(RawTierEntries(dbe, 4), Is.Zero, "the supply's key indexes a destroyed entity");
            Assert.That(RawTierEntries(dbe, 0), Is.Zero, "the absent value's entry was left behind");
        });
    }

    /// <summary>
    /// A second destroy of an entity, published after the first released its slot to another entity, leaves that entity alone (#1370).
    /// </summary>
    /// <remarks>
    /// A SingleVersion archetype releases a destroyed entity's slot at the publish (VIS-09), and the next spawn may take it. The second destroy found the
    /// slot through the entity's record, still there until the cleanup, and removed the new occupant's index entries by its keys and tails, then released
    /// its slot: the new entity gone from the index and from every scan. Staged: the second destroy (T2) is held between its PREPARE and its append while
    /// the first (T1) commits and a spawn (T3) takes the slot.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-08")]
    public void ASecondDestroyPublishedAfterTheSlotWasReused_LeavesTheNewOccupantAlone()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AxSvMulti>();
        dbe.InitializeArchetypes();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(new AxSvMulti { Key = 1, Bucket = 7 }));   // keeps the cluster from draining
            x = tx.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(new AxSvMulti { Key = 2, Bucket = 3 }));
            Assert.That(tx.Commit(), Is.True, "premise: the spawns commit");
        }

        int xLocation;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var r = tx.Open(x);
            xLocation = r._clusterChunkId * 64 + r._clusterSlotIndex;
        }

        using var t2Prepared = new ManualResetEventSlim();
        using var t2Go = new ManualResetEventSlim();
        Exception t2Failure = null;
        var t2Committed = false;
        var t2ThreadId = 0;
        dbe.CommitBeforeAppendProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref t2ThreadId))
            {
                t2Prepared.Set();
                t2Go.Wait(Patience);
            }
        };

        var t2 = new Thread(() =>
        {
            try
            {
                Volatile.Write(ref t2ThreadId, Environment.CurrentManagedThreadId);
                using var tx = dbe.CreateQuickTransaction();
                tx.Destroy(x);   // x is alive at T2's snapshot
                t2Committed = tx.Commit();
            }
            catch (Exception ex)
            {
                t2Failure = ex;
            }
        }) { IsBackground = true };
        t2.Start();

        EntityId y;
        bool t2Ran;
        try
        {
            Assert.That(t2Prepared.Wait(Patience), Is.True, "premise: T2 prepared before T1 committed");
            using (var t1 = dbe.CreateQuickTransaction())
            {
                t1.Destroy(x);
                Assert.That(t1.Commit(), Is.True, "premise: T1 commits");
            }

            using (var t3 = dbe.CreateQuickTransaction())
            {
                y = t3.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(new AxSvMulti { Key = 3, Bucket = 5 }));
                Assert.That(t3.Commit(), Is.True, "premise: T3 commits");
            }
        }
        finally
        {
            dbe.CommitBeforeAppendProbe = null;
            t2Go.Set();
            t2Ran = t2.Join(Patience);
        }

        Assert.That(t2Ran && t2Failure == null && t2Committed, Is.True, $"premise: T2's destroy committed after the slot was reused ({t2Failure?.Message})");
        using var check = dbe.CreateQuickTransaction();
        var ry = check.Open(y);
        Assert.That(ry._clusterChunkId * 64 + ry._clusterSlotIndex, Is.EqualTo(xLocation), "premise: y took x's slot");
        Assert.Multiple(() =>
        {
            Assert.That(check.Query<AxPureSvMulti>().Count(), Is.EqualTo(2), "y's slot was released by x's second destroy");
            Assert.That(check.Query<AxPureSvMulti>().WhereField<AxSvMulti>(v => v.Bucket == 5).Count(), Is.EqualTo(1),
                "y's index entry was removed by x's second destroy");
        });
    }

    /// <summary>
    /// A Commit-discipline write staged before a destroy of its entity, published after the slot went to another entity, leaves that entity alone (#1370).
    /// </summary>
    /// <remarks>
    /// The staged write keeps the slot it was staged against. A SingleVersion destroy released it at its publish and a spawn took it; the staged publish then
    /// moved the new occupant's index entry by its keys and copied the dead entity's value over its HEAD. Staged: the write's transaction (T2) is held
    /// between its PREPARE and its append while the destroy (T1) commits and the spawn (T3) takes the slot.
    /// </remarks>
    [Test]
    [VerifiesRule("VIS-08")]
    public void AStagedWritePublishedAfterTheSlotWasReused_LeavesTheNewOccupantAlone()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AxSvMulti>();
        dbe.InitializeArchetypes();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(new AxSvMulti { Key = 1, Bucket = 7 }));   // keeps the cluster from draining
            x = tx.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(new AxSvMulti { Key = 2, Bucket = 3 }));
            Assert.That(tx.Commit(), Is.True, "premise: the spawns commit");
        }

        int xLocation;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var r = tx.Open(x);
            xLocation = r._clusterChunkId * 64 + r._clusterSlotIndex;
        }

        using var t2Prepared = new ManualResetEventSlim();
        using var t2Go = new ManualResetEventSlim();
        Exception t2Failure = null;
        var t2Committed = false;
        var t2ThreadId = 0;
        dbe.CommitBeforeAppendProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref t2ThreadId))
            {
                t2Prepared.Set();
                t2Go.Wait(Patience);
            }
        };

        var t2 = new Thread(() =>
        {
            try
            {
                Volatile.Write(ref t2ThreadId, Environment.CurrentManagedThreadId);
                using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit);
                tx.OpenMut(x).Set(AxPureSvMulti.P, new AxSvMulti { Key = 2, Bucket = 9 });   // staged: x is alive at T2's snapshot
                t2Committed = tx.Commit();
            }
            catch (Exception ex)
            {
                t2Failure = ex;
            }
        }) { IsBackground = true };
        t2.Start();

        EntityId y;
        bool t2Ran;
        try
        {
            Assert.That(t2Prepared.Wait(Patience), Is.True, "premise: T2 prepared before T1 committed");
            using (var t1 = dbe.CreateQuickTransaction())
            {
                t1.Destroy(x);
                Assert.That(t1.Commit(), Is.True, "premise: T1 commits");
            }

            using (var t3 = dbe.CreateQuickTransaction())
            {
                y = t3.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(new AxSvMulti { Key = 3, Bucket = 5 }));
                Assert.That(t3.Commit(), Is.True, "premise: T3 commits");
            }
        }
        finally
        {
            dbe.CommitBeforeAppendProbe = null;
            t2Go.Set();
            t2Ran = t2.Join(Patience);
        }

        Assert.That(t2Ran && t2Failure == null, Is.True, $"premise: T2's staged write committed after the slot was reused ({t2Failure?.Message})");
        using var check = dbe.CreateQuickTransaction();
        var ry = check.Open(y);
        Assert.That(ry._clusterChunkId * 64 + ry._clusterSlotIndex, Is.EqualTo(xLocation), "premise: y took x's slot");
        var yBucket = ry.Read(AxPureSvMulti.P).Bucket;
        Assert.Multiple(() =>
        {
            Assert.That(yBucket, Is.EqualTo(5), "x's staged value was copied over y's");
            Assert.That(RawFirstFieldEntries<AxPureSvMulti>(dbe, 5), Is.EqualTo(1), "y's index entry was moved by x's staged write");
            Assert.That(RawFirstFieldEntries<AxPureSvMulti>(dbe, 9), Is.Zero, "x's staged key indexes y");
        });
        _ = t2Committed;
    }

    /// <summary>
    /// The older update — the lower stamp — is held at its publish while the newer one commits. The entity reads the newer value; the older publish, last,
    /// must move nothing.
    /// </summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void AnOlderUpdatePublishedLast_LeavesTheIndexOnTheNewerKey()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        var pk = (long)x.RawValue;
        var table = dbe.GetComponentTable<CompD>();
        using var olderAtPublish = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        var publishes = 0;
        dbe.PublishComponentProbe = (t, _) =>
        {
            if (ReferenceEquals(t, table) && Interlocked.Increment(ref publishes) == 1)
            {
                olderAtPublish.Set();
                releaseOlder.Wait(Patience);
            }
        };

        Exception failure = null;
        var older = new Thread(() => Run(30)) { IsBackground = true };
        var newer = new Thread(() => Run(40)) { IsBackground = true };
        try
        {
            older.Start();
            Assert.That(olderAtPublish.Wait(Patience), Is.True, "premise: the older update reaches its publish");
            newer.Start();
            Assert.That(newer.Join(Patience), Is.True, "premise: the newer update commits while the older one is held at its publish");
        }
        finally
        {
            releaseOlder.Set();
        }

        Assert.That(older.Join(Patience), Is.True, "the older update finishes its publish once released");
        dbe.PublishComponentProbe = null;
        Assert.That(failure, Is.Null);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(x).Read(CompDArch.D).B, Is.EqualTo(40), "premise: the newer update's value is the newest committed");
        }

        Assert.That(UniqueHolders(dbe, 40), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(UniqueHolders(dbe, 30), Is.Empty, "the older publish moved the entry back to its own key");

        void Run(int b)
        {
            try
            {
                using var tx = dbe.CreateQuickTransaction();
                Write(tx, x, new CompD(1f, b, 1.0));
                if (!tx.Commit())
                {
                    throw new InvalidOperationException($"premise: the update to {b} commits");
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        }
    }
}
