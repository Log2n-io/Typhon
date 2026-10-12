using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Numerics;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Plan 06 step 3.3 (<c>rules/data-engine.md</c> VIS-01, 04, 05, 06, 07): revisions resolve by commit stamp and owner. A commit's revisions are visible at
/// once to every snapshot above its stamp — a reader that finds one still flagged resolves it through its owner's commit card — and the newest revision
/// of an entity is the highest stamp, wherever it sits in the chain: in the walk, in the cluster HEAD, after a compaction and after a crash.
/// </summary>
[TestFixture]
[NonParallelizable] // Commit-stamp counters and engine probes; one engine at a time keeps the staged interleavings exact.
class CommitStampVisibilityTests : TestBase<CommitStampVisibilityTests>
{
    private const string Marker = "VIS violated";
    private const int Count = 20;

    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    private static DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CsWallet>();
        dbe.RegisterComponentFromAccessor<CsKeyed>();
        dbe.RegisterComponentFromAccessor<EvoCcBagV1>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId[] Spawn(DatabaseEngine dbe, int count)
    {
        var ids = new EntityId[count];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids[i] = tx.Spawn<CsWalletArch>(CsWalletArch.Wallet.Set(new CsWallet { Credits = 100 + i }));
        }

        Assert.That(tx.Commit(), Is.True);
        return ids;
    }

    private static void Set(Transaction tx, EntityId id, long credits) => tx.OpenMut(id).Set(CsWalletArch.Wallet, new CsWallet { Credits = credits });

    private static long Read(Transaction tx, EntityId id) => tx.Open(id).Read(CsWalletArch.Wallet).Credits;

    private static long ReadNew(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateReadOnlyTransaction();
        return Read(tx, id);
    }

    /// <summary>The value the cluster's HEAD copy holds for the archetype's one entity — what bulk iteration reads.</summary>
    private static long ClusterHead(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        using var accessor = tx.For<CsWalletArch>();
        long value = -1;
        var seen = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var wallets = cluster.GetReadOnlySpan(CsWalletArch.Wallet);
            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                value = wallets[idx].Credits;
                seen++;
            }
        }

        Assert.That(seen, Is.EqualTo(1), "premise: one entity of the archetype");
        return value;
    }

    /// <summary>A commit run on its own thread; its transaction is created there (thread affinity).</summary>
    private sealed class BackgroundCommit
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _go = new(false);
        public readonly ManualResetEventSlim Written = new(false);
        public readonly ManualResetEventSlim Done = new(false);
        public int ThreadId;
        public long Tsn;
        public long Stamp;
        public Exception Error;

        public BackgroundCommit(DatabaseEngine dbe, Action<Transaction> write, CommitDiscipline discipline = CommitDiscipline.TickFence)
        {
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                try
                {
                    using var tx = discipline == CommitDiscipline.TickFence
                        ? dbe.CreateQuickTransaction()
                        : dbe.CreateQuickTransaction(DurabilityMode.Deferred, discipline);
                    Tsn = tx.TSN;
                    write(tx);
                    Written.Set();
                    _go.Wait();
                    Assert.That(tx.Commit(), Is.True);
                    Stamp = tx.CommitStamp;
                }
                catch (Exception e)
                {
                    Error = e;
                    Written.Set();
                }
                finally
                {
                    Done.Set();
                }
            }) { IsBackground = true };
            _thread.Start();
            Assert.That(Written.Wait(TimeSpan.FromSeconds(10)), Is.True, "the background writer did not write");
        }

        public void Go() => _go.Set();

        public void Join()
        {
            Assert.That(Done.Wait(TimeSpan.FromSeconds(20)), Is.True, "the background commit did not finish");
            _thread.Join();
            Assert.That(Error, Is.Null, $"the background commit failed: {Error}");
        }
    }

    /// <summary>A gate one chosen thread stops at: the probe signals it reached, then waits for the release.</summary>
    private sealed class Gate
    {
        private readonly ManualResetEventSlim _reached = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        public volatile int ThreadId = -1;
        private int _armed = 1;

        public Action Probe => () =>
        {
            if (Environment.CurrentManagedThreadId == ThreadId && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _reached.Set();
                _release.Wait(TimeSpan.FromSeconds(20));
            }
        };

        public void AwaitReached() => Assert.That(_reached.Wait(TimeSpan.FromSeconds(10)), Is.True, "the writer did not reach its gate");

        public void Release() => _release.Set();
    }

    private static int Changed(Transaction reader, EntityId[] ids, long newBase)
    {
        var changed = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            changed += Read(reader, ids[i]) == newBase + i ? 1 : 0;
        }

        return changed;
    }

    /// <summary>
    /// The case that defines option C: a reader begun after a writer's commit point, before its publish, sees ALL of the writer's revisions — they are
    /// still flagged, and each resolves through the writer's commit card, whose stamp is below the reader's snapshot. Not some, and not none: none was the
    /// pre-C reading (a flagged revision skipped), half a commit the per-entity publish's. Read again after the publish, the same.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-01")]
    [VerifiesRule("VIS-02")]
    [VerifiesRule("VIS-03")]
    [CancelAfter(30_000)]
    public void AReaderBegunBetweenTheAppendAndThePublish_SeesTheWholeCommit()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, Count);
        var gate = new Gate();
        dbe.CommitAfterAppendProbe = gate.Probe;
        try
        {
            var writer = new BackgroundCommit(dbe, tx =>
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    Set(tx, ids[i], 500 + i);
                }
            });
            gate.ThreadId = writer.ThreadId;
            writer.Go();
            gate.AwaitReached();

            using var reader = dbe.CreateReadOnlyTransaction();
            var seen = Changed(reader, ids, 500);
            Assert.That(seen, Is.EqualTo(Count), $"{Marker}: a reader begun past the writer's commit point saw {seen} of its {Count} revisions");

            gate.Release();
            writer.Join();
            Assert.That(reader.TSN, Is.GreaterThan(writer.Stamp), "premise: the reader's snapshot is above the writer's stamp");
            Assert.That(Changed(reader, ids, 500), Is.EqualTo(Count), $"{Marker}: the reader's values moved with the publish");
        }
        finally
        {
            gate.Release();
            dbe.CommitAfterAppendProbe = null;
        }
    }

    /// <summary>The reserved (pending-spawn) slots and the occupied ones of the archetype's clusters (VIS-08).</summary>
    private static (int Reserved, int Occupied) SlotCensus(DatabaseEngine dbe)
    {
        var state = dbe._archetypeStates[Archetype<CsWalletArch>.Metadata.ArchetypeId].ClusterState;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var ids = state.ReadActiveClusterList(out var count);
        var accessor = state.ClusterSegment.CreateChunkAccessor();
        try
        {
            int reserved = 0, occupied = 0;
            for (var c = 0; c < count; c++)
            {
                reserved += BitOperations.PopCount(state.ReservedBits(ids[c]));
                unsafe
                {
                    occupied += BitOperations.PopCount(*(ulong*)accessor.GetChunkAddress(ids[c]));
                }
            }

            return (reserved, occupied);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// VIS-08's defining case: a reader begun after a writer's commit point, before its publish, sees the entity the commit spawned together with the
    /// revision it wrote — by id, by count and by enumeration. The spawn was placed in PREPARE with a pending born and a reserved slot; the reader resolves
    /// it through the writer's commit card, as it resolves the revision. Placed at publish, the spawn was absent while the revision was visible: half a
    /// commit. Read again after the publish, the same.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-08")]
    [CancelAfter(30_000)]
    public void AReaderBegunBetweenTheAppendAndThePublish_SeesTheSpawnWithTheRevision()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var y = Spawn(dbe, 1)[0];
        var gate = new Gate();
        dbe.CommitAfterAppendProbe = gate.Probe;
        try
        {
            var x = EntityId.Null;
            var writer = new BackgroundCommit(dbe, tx =>
            {
                Set(tx, y, 777);
                x = tx.Spawn<CsWalletArch>(CsWalletArch.Wallet.Set(new CsWallet { Credits = 999 }));
            });
            gate.ThreadId = writer.ThreadId;
            writer.Go();
            gate.AwaitReached();
            Assert.That(SlotCensus(dbe).Reserved, Is.EqualTo(1), "premise: the spawn holds a reserved slot between the append and the publish");

            using var reader = dbe.CreateReadOnlyTransaction();
            void AssertWholeCommit(string when)
            {
                Assert.That(Read(reader, y), Is.EqualTo(777), $"premise ({when}): the reader sees the commit's revision");
                Assert.That(reader.TryOpen(x, out var e), Is.True, $"{Marker} ({when}): the reader sees the revision and not the entity the commit spawned");
                Assert.That(e.Read(CsWalletArch.Wallet).Credits, Is.EqualTo(999), $"{Marker} ({when}): the spawn reads incomplete");
                Assert.That(reader.Query<CsWalletArch>().Count(), Is.EqualTo(2), $"{Marker} ({when}): the count misses the spawn");
                Assert.That(reader.Query<CsWalletArch>().Execute(), Does.Contain(x), $"{Marker} ({when}): the enumeration misses the spawn");
            }

            AssertWholeCommit("before the publish");
            gate.Release();
            writer.Join();
            Assert.That(reader.TSN, Is.GreaterThan(writer.Stamp), "premise: the reader's snapshot is above the writer's stamp");
            AssertWholeCommit("after the publish");
            Assert.That(SlotCensus(dbe), Is.EqualTo((0, 2)), "the publish left a reservation, or no occupancy bit");
        }
        finally
        {
            gate.Release();
            dbe.CommitAfterAppendProbe = null;
        }
    }

    /// <summary>
    /// The same window through a predicate query on the SoA scan path: the spawn's slot is reserved, not occupied, and the scan visits it and resolves it
    /// through the writer's card — its cluster not pruned, the zone maps having been widened at the placement. The index path is HEAD-valued (design 05's
    /// non-goal, #674): its entries are the publish's.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-08")]
    [CancelAfter(30_000)]
    public void AScanBetweenTheAppendAndThePublish_FindsTheSpawnInItsReservedSlot()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId y;
        using (var tx = dbe.CreateQuickTransaction())
        {
            y = tx.Spawn<CsKeyedArch>(CsKeyedArch.Keyed.Set(new CsKeyed { Group = 1, Value = 1 }));
            Assert.That(tx.Commit(), Is.True);
        }

        var gate = new Gate();
        dbe.CommitAfterAppendProbe = gate.Probe;
        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        try
        {
            var x = EntityId.Null;
            var writer = new BackgroundCommit(dbe, tx =>
            {
                tx.OpenMut(y).Set(CsKeyedArch.Keyed, new CsKeyed { Group = 1, Value = 2 });
                x = tx.Spawn<CsKeyedArch>(CsKeyedArch.Keyed.Set(new CsKeyed { Group = 5, Value = 9 }));
            });
            gate.ThreadId = writer.ThreadId;
            writer.Go();
            gate.AwaitReached();

            using var reader = dbe.CreateReadOnlyTransaction();
            Assert.That(reader.Open(y).Read(CsKeyedArch.Keyed).Value, Is.EqualTo(2), "premise: the reader sees the commit's revision");
            var found = reader.Query<CsKeyedArch>().WhereField<CsKeyed>(k => k.Group == 5).Execute();
            Assert.That(found, Does.Contain(x), $"{Marker}: the scan misses the spawn in its reserved slot");
            Assert.That(found, Does.Not.Contain(y), "the committed entity is in another group");
            gate.Release();
            writer.Join();
        }
        finally
        {
            QueryPathProbe.Forced = ClusterScanPath.Planner;
            gate.Release();
            dbe.CommitAfterAppendProbe = null;
        }
    }

    /// <summary>
    /// VIS-09's window: a reader begun after a writer's commit point, before its publish, sees the entity the commit destroyed gone, together with the
    /// revision it wrote — by id, by count and by enumeration. The destroy's died was pending from PREPARE; the reader resolves it through the writer's card.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    [CancelAfter(30_000)]
    public void AReaderBegunBetweenTheAppendAndThePublish_SeesTheDestroyWithTheRevision()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 2);
        var (y, x) = (ids[0], ids[1]);
        var gate = new Gate();
        dbe.CommitAfterAppendProbe = gate.Probe;
        try
        {
            var writer = new BackgroundCommit(dbe, tx =>
            {
                Set(tx, y, 777);
                tx.Destroy(x);
            });
            gate.ThreadId = writer.ThreadId;
            writer.Go();
            gate.AwaitReached();

            using var reader = dbe.CreateReadOnlyTransaction();
            void AssertWholeCommit(string when)
            {
                Assert.That(Read(reader, y), Is.EqualTo(777), $"premise ({when}): the reader sees the commit's revision");
                Assert.That(reader.TryOpen(x, out _), Is.False, $"{Marker} ({when}): the reader sees the revision and the entity the commit destroyed");
                Assert.That(reader.Query<CsWalletArch>().Count(), Is.EqualTo(1), $"{Marker} ({when}): the count holds the destroyed entity");
                Assert.That(reader.Query<CsWalletArch>().Execute(), Does.Not.Contain(x), $"{Marker} ({when}): the enumeration holds the destroyed entity");
            }

            AssertWholeCommit("before the publish");
            gate.Release();
            writer.Join();
            AssertWholeCommit("after the publish");
        }
        finally
        {
            gate.Release();
            dbe.CommitAfterAppendProbe = null;
        }
    }

    /// <summary>
    /// A snapshot older than a destroy still finds the entity after the destroy published — by id, by count, by enumeration and by the SoA scan — because
    /// its slot is held, hidden from HEAD and reserved, until the cleanup; the cleanup releases it once that snapshot ends, and the slot is claimable again
    /// (VIS-09). Released at publish, the scans lost an entity the snapshot holds.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    [CancelAfter(30_000)]
    public void AnOlderSnapshot_StillFindsADestroyedEntity_UntilTheCleanupReleasesItsSlot()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId y, x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            y = tx.Spawn<CsKeyedArch>(CsKeyedArch.Keyed.Set(new CsKeyed { Group = 1, Value = 1 }));
            x = tx.Spawn<CsKeyedArch>(CsKeyedArch.Keyed.Set(new CsKeyed { Group = 1, Value = 2 }));
            Assert.That(tx.Commit(), Is.True);
        }

        var older = dbe.CreateReadOnlyTransaction();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(x);
            Assert.That(tx.Commit(), Is.True);
        }

        var state = dbe._archetypeStates[Archetype<CsKeyedArch>.Metadata.ArchetypeId].ClusterState;
        using (var newer = dbe.CreateReadOnlyTransaction())
        {
            Assert.That(newer.TryOpen(x, out _), Is.False, "premise: the destroy is committed");
            Assert.That(newer.Query<CsKeyedArch>().Count(), Is.EqualTo(1), "premise: a newer snapshot counts one");
        }

        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        try
        {
            Assert.That(older.TryOpen(x, out _), Is.True, $"{Marker}: an older snapshot lost the entity by id");
            Assert.That(older.Query<CsKeyedArch>().Count(), Is.EqualTo(2), $"{Marker}: an older snapshot's count lost the entity");
            Assert.That(older.Query<CsKeyedArch>().WhereField<CsKeyed>(k => k.Group == 1).Execute(), Does.Contain(x),
                $"{Marker}: an older snapshot's scan lost the entity — its slot was released at the destroy's publish");
        }
        finally
        {
            QueryPathProbe.Forced = ClusterScanPath.Planner;
        }

        int reservedBefore, reservedAfter;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var ids = state.ReadActiveClusterList(out var count);
            reservedBefore = 0;
            for (var c = 0; c < count; c++)
            {
                reservedBefore += BitOperations.PopCount(state.ReservedBits(ids[c]));
            }
        }

        older.Dispose();
        dbe.FlushDeferredCleanups();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var ids = state.ReadActiveClusterList(out var count);
            reservedAfter = 0;
            for (var c = 0; c < count; c++)
            {
                reservedAfter += BitOperations.PopCount(state.ReservedBits(ids[c]));
            }
        }

        Assert.That(reservedBefore, Is.EqualTo(1), "premise: the destroyed entity's slot is held, reserved, while an older snapshot lives");
        Assert.That(reservedAfter, Is.Zero, $"{Marker}: the cleanup did not release the hidden slot");
    }

    /// <summary>The pending destroys the wallet archetype's clusters hold (VIS-09).</summary>
    private static long PendingDeaths(DatabaseEngine dbe)
    {
        var state = dbe._archetypeStates[Archetype<CsWalletArch>.Metadata.ArchetypeId].ClusterState;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var ids = state.ReadActiveClusterList(out var count);
        long pending = 0;
        for (var c = 0; c < count; c++)
        {
            pending += state.PendingDeaths(ids[c]);
        }

        return pending;
    }

    /// <summary>
    /// The cleanup of a destroyed entity is keyed by the destroy's commit STAMP (VIS-06, VIS-09): a snapshot begun after the destroyer was created and
    /// before its commit point still holds the entity once the destroyer is gone and the drain runs at its dispose. Keyed by the destroyer's TSN, below that
    /// snapshot, the drain removed the record and released the slot under it.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    [VerifiesRule("VIS-06")]
    [CancelAfter(30_000)]
    public void ASnapshotBetweenADestroyersTsnAndItsStamp_KeepsTheEntityThroughTheCleanup()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var x = Spawn(dbe, 1)[0];
        var destroyer = dbe.CreateQuickTransaction();
        using var reader = dbe.CreateReadOnlyTransaction();
        destroyer.Destroy(x);
        Assert.That(destroyer.Commit(), Is.True);
        Assert.That(destroyer.CommitStamp, Is.GreaterThan(reader.TSN), "premise: the destroy's stamp is above the reader's snapshot, its TSN below");
        destroyer.Dispose();
        dbe.FlushDeferredCleanups(reader.TSN);

        Assert.That(reader.TryOpen(x, out _), Is.True, $"{Marker}: the cleanup took an entity a live snapshot holds");
        Assert.That(reader.Query<CsWalletArch>().Count(), Is.EqualTo(1), $"{Marker}: the cleanup dropped an entity from a live snapshot's count");
    }

    /// <summary>A commit that aborts after marking its destroys pending, before its commit point, leaves the entity alive and its died clear (AP-07).</summary>
    [Test]
    [VerifiesRule("VIS-09")]
    [VerifiesRule("AP-07")]
    [CancelAfter(30_000)]
    public void AnAbortAfterTheDestroyIsMarked_LeavesTheEntityAlive()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var x = Spawn(dbe, 1)[0];
        var state = dbe._archetypeStates[Archetype<CsWalletArch>.Metadata.ArchetypeId];
        bool marked = false;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(x);
            dbe.CommitBeforeAppendProbe = () =>
            {
                using var guard = EpochGuard.Enter(dbe.EpochManager);
                var map = state.EntityMap.Segment.CreateChunkAccessor();
                try
                {
                    unsafe
                    {
                        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
                        Assert.That(state.EntityMap.TryGet(x.EntityKey, record, ref map), Is.True);
                        marked = EntityRecordAccessor.GetHeader(record).IsDiedPending;
                    }
                }
                finally
                {
                    map.Dispose();
                }

                throw new InvalidOperationException("staged: an abort after the destroy was marked, before the append");
            };
            try
            {
                Assert.Throws<InvalidOperationException>(() => tx.Commit());
            }
            finally
            {
                dbe.CommitBeforeAppendProbe = null;
            }

            Assert.That(PendingDeaths(dbe), Is.Zero, $"{Marker}: the aborted destroy still holds its cluster's gate shut");
        }

        Assert.That(marked, Is.True, "premise: the destroy's died was pending when the commit aborted");
        using var reader = dbe.CreateReadOnlyTransaction();
        Assert.That(reader.TryOpen(x, out _), Is.True, $"{Marker}: the aborted destroy took the entity");
        Assert.That(reader.Query<CsWalletArch>().Count(), Is.EqualTo(1), $"{Marker}: the aborted destroy dropped the entity from the count");
    }

    /// <summary>
    /// The other side: a reader begun before the writer's commit point sees neither the spawn nor the revision, before the publish and after it — the
    /// pending born resolves to a stamp above its snapshot.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-08")]
    [CancelAfter(30_000)]
    public void AReaderBegunBeforeTheCommitPoint_SeesNeitherTheSpawnNorTheRevision()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var y = Spawn(dbe, 1)[0];
        var gate = new Gate();
        dbe.CommitAfterAppendProbe = gate.Probe;
        try
        {
            var x = EntityId.Null;
            var writer = new BackgroundCommit(dbe, tx =>
            {
                Set(tx, y, 777);
                x = tx.Spawn<CsWalletArch>(CsWalletArch.Wallet.Set(new CsWallet { Credits = 999 }));
            });
            gate.ThreadId = writer.ThreadId;
            using var reader = dbe.CreateReadOnlyTransaction();
            writer.Go();
            gate.AwaitReached();

            void AssertNone(string when)
            {
                Assert.That(Read(reader, y), Is.EqualTo(100), $"premise ({when}): the older reader keeps the old revision");
                Assert.That(reader.TryOpen(x, out _), Is.False, $"{Marker} ({when}): an older reader sees a spawn committed after its snapshot");
                Assert.That(reader.Query<CsWalletArch>().Count(), Is.EqualTo(1), $"{Marker} ({when}): the older reader counts the spawn");
            }

            AssertNone("before the publish");
            gate.Release();
            writer.Join();
            AssertNone("after the publish");
        }
        finally
        {
            gate.Release();
            dbe.CommitAfterAppendProbe = null;
        }
    }

    /// <summary>
    /// A commit that aborts after its spawns were placed, before its commit point, leaves no trace of them (AP-07): no record, no reserved slot, the
    /// cluster the spawn opened given back — and no reader ever saw them.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-08")]
    [VerifiesRule("AP-07")]
    [CancelAfter(30_000)]
    public void AnAbortAfterThePlacement_LeavesNoTraceOfTheSpawn()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var y = Spawn(dbe, 1)[0];
        var before = SlotCensus(dbe);
        (int, int) placed = default;
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            Set(tx, y, 777);
            x = tx.Spawn<CsWalletArch>(CsWalletArch.Wallet.Set(new CsWallet { Credits = 999 }));
            dbe.CommitBeforeAppendProbe = () =>
            {
                placed = SlotCensus(dbe);
                throw new InvalidOperationException("staged: an abort after the placement, before the append");
            };
            try
            {
                Assert.Throws<InvalidOperationException>(() => tx.Commit());
            }
            finally
            {
                dbe.CommitBeforeAppendProbe = null;
            }

            Assert.That(placed.Item1, Is.EqualTo(1), "premise: the spawn was placed — its slot reserved — when the commit aborted");
            Assert.That(SlotCensus(dbe), Is.EqualTo(before), $"{Marker}: the aborted spawn's slot is still reserved, or occupied");
        }

        using var reader = dbe.CreateReadOnlyTransaction();
        Assert.That(reader.TryOpen(x, out _), Is.False, $"{Marker}: the aborted spawn is in the EntityMap");
        Assert.That(Read(reader, y), Is.EqualTo(100), "the aborted revision");
        Assert.That(reader.Query<CsWalletArch>().Count(), Is.EqualTo(1), $"{Marker}: the aborted spawn is counted");
        using var next = dbe.CreateQuickTransaction();
        var z = next.Spawn<CsWalletArch>(CsWalletArch.Wallet.Set(new CsWallet { Credits = 5 }));
        Assert.That(next.Commit(), Is.True, "the engine spawns on after the abort");
        Assert.That(SlotCensus(dbe), Is.EqualTo((0, 2)), "the next spawn reuses the given-back slot");
    }

    /// <summary>
    /// A HEAD reader — one with no snapshot: subscriptions, references — takes a pending born for not alive: the spawn's commit may still abort. Published,
    /// the entity is alive; destroyed, it is not.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-08")]
    public void APendingBorn_IsNotAliveForAHeadReader()
    {
        var header = new EntityRecordHeader { BornTSN = EntityRecordHeader.PendingBorn(42) };
        Assert.That(header.IsBornPending, Is.True, "premise");
        Assert.That(header.IsAlive, Is.False, $"{Marker}: a pending spawn reads alive to a HEAD reader");
        Assert.That(header.IsVisibleAt(TransactionChain.TsnRefusalThreshold), Is.False, "a pending born, to a plain test, is not born at any TSN");
        header.BornTSN = 43;
        Assert.That(header.IsAlive, Is.True, "published");
        header.DiedTSN = EntityRecordHeader.PendingBorn(44);
        Assert.That(header.IsAlive, Is.True, $"{Marker}: a pending destroy reads dead to a HEAD reader — its commit may still abort (VIS-09)");
        header.DiedTSN = 45;
        Assert.That(header.IsAlive, Is.False, "destroyed");
    }

    /// <summary>
    /// What a checkpoint can persist of a placed spawn — its record, born pending, and its cluster page — never says the slot is occupied: the slot is held
    /// by the in-memory reservation, the page's occupancy bit is clear (VIS-08, CM-01). The crash open re-derives the EntityMap from occupancy, so a crash
    /// before the append reopens with no trace of the spawn. Had the claim's occupancy bit stayed set, that rebuild would resurrect an entity no commit made.
    /// Checked on the page itself: a checkpoint does not complete while the committing transaction holds its pages, so the crash cannot be staged here.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-08")]
    [CancelAfter(30_000)]
    public void APlacedSpawn_IsNeverOccupiedOnItsPage_OnlyReserved()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 1);
        var state = dbe._archetypeStates[Archetype<CsWalletArch>.Metadata.ArchetypeId];
        bool pending = false, onPage = true, reserved = false;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var x = tx.Spawn<CsWalletArch>(CsWalletArch.Wallet.Set(new CsWallet { Credits = 999 }));
            dbe.CommitBeforeAppendProbe = () =>
            {
                using var guard = EpochGuard.Enter(dbe.EpochManager);
                var map = state.EntityMap.Segment.CreateChunkAccessor();
                var clusters = state.ClusterState.ClusterSegment.CreateChunkAccessor();
                try
                {
                    unsafe
                    {
                        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
                        Assert.That(state.EntityMap.TryGet(x.EntityKey, record, ref map), Is.True, "premise: the placed spawn has its record");
                        pending = EntityRecordAccessor.GetHeader(record).IsBornPending;
                        var cluster = ClusterEntityRecordAccessor.GetClusterChunkId(record);
                        var bit = 1UL << ClusterEntityRecordAccessor.GetSlotIndex(record);
                        onPage = (*(ulong*)clusters.GetChunkAddress(cluster) & bit) != 0;
                        reserved = (state.ClusterState.ReservedBits(cluster) & bit) != 0;
                    }
                }
                finally
                {
                    clusters.Dispose();
                    map.Dispose();
                }
            };
            try
            {
                Assert.That(tx.Commit(), Is.True);
            }
            finally
            {
                dbe.CommitBeforeAppendProbe = null;
            }
        }

        Assert.That(pending, Is.True, $"{Marker}: the placed spawn's record is not born pending");
        Assert.That(onPage, Is.False, $"{Marker}: the placed spawn's occupancy bit is on its page, where a checkpoint persists it");
        Assert.That(reserved, Is.True, $"{Marker}: nothing holds the placed spawn's slot");
    }

    /// <summary>
    /// A reader begun half-way through the writer's publish — half its revisions stamped, half still flagged — sees the whole commit: the card is held until
    /// the last revision is stamped (VIS-04). Released before, the flagged half would read as uncommitted.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-04")]
    [CancelAfter(30_000)]
    public void AReaderBegunMidPublish_SeesTheWholeCommit()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, Count);
        var gate = new Gate();
        var calls = 0;
        var writerThread = -1;
        dbe.PublishComponentProbe = (_, _) =>
        {
            if (Environment.CurrentManagedThreadId == writerThread && Interlocked.Increment(ref calls) == Count / 2)
            {
                gate.Probe();
            }
        };
        try
        {
            var writer = new BackgroundCommit(dbe, tx =>
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    Set(tx, ids[i], 700 + i);
                }
            });
            writerThread = writer.ThreadId;
            gate.ThreadId = writer.ThreadId;
            writer.Go();
            gate.AwaitReached();

            using (var reader = dbe.CreateReadOnlyTransaction())
            {
                var seen = Changed(reader, ids, 700);
                Assert.That(seen, Is.EqualTo(Count), $"{Marker}: a reader begun mid-publish saw {seen} of the commit's {Count} revisions");
            }

            gate.Release();
            writer.Join();
        }
        finally
        {
            gate.Release();
            dbe.PublishComponentProbe = null;
        }
    }

    /// <summary>Causality: a transaction begun after <c>Commit()</c> returned sees every revision of that commit.</summary>
    [Test]
    [VerifiesRule("VIS-04")]
    [CancelAfter(15_000)]
    public void ATransactionBegunAfterTheCommitReturned_SeesTheWholeCommit()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, Count);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                Set(tx, ids[i], 900 + i);
            }

            Assert.That(tx.Commit(), Is.True);
        }

        using var after = dbe.CreateReadOnlyTransaction();
        Assert.That(Changed(after, ids, 900), Is.EqualTo(Count));
    }

    /// <summary>
    /// A committer past its commit point and not yet published is a conflict to a handler commit whose snapshot is below its stamp (plan 06 §3.10) — the
    /// CommitSequence test missed it, being bumped at publish. The handler commit's stamp is the higher: its value is the newest, in the walk and in the
    /// cluster HEAD, though the other one publishes last.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-05")]
    [CancelAfter(30_000)]
    public void ACommitterPastItsCommitPoint_IsAConflictToAHandlerCommit()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var id = Spawn(dbe, 1)[0];
        using var handlerTx = dbe.CreateQuickTransaction();
        Set(handlerTx, id, 2);

        var gate = new Gate();
        dbe.CommitAfterAppendProbe = gate.Probe;
        try
        {
            var other = new BackgroundCommit(dbe, tx => Set(tx, id, 1));
            gate.ThreadId = other.ThreadId;
            other.Go();
            gate.AwaitReached();

            var invoked = 0;
            Assert.That(handlerTx.Commit((ref ConcurrencyConflictSolver _) => invoked++), Is.True);
            Assert.That(invoked, Is.EqualTo(1), $"{Marker}: a registered, unpublished commit past the handler's snapshot was not seen as a conflict");

            gate.Release();
            other.Join();
            Assert.That(other.Stamp, Is.LessThan(handlerTx.CommitStamp), "premise: the handler commit drew the later stamp");
        }
        finally
        {
            gate.Release();
            dbe.CommitAfterAppendProbe = null;
        }

        Assert.That(ReadNew(dbe, id), Is.EqualTo(2), $"{Marker}: the walk's newest is not the highest stamp");
        Assert.That(ClusterHead(dbe), Is.EqualTo(2), $"{Marker}: the cluster HEAD is not the highest stamp");
    }

    /// <summary>
    /// Two writers of one entity, no handler, publishing against their stamp order: W1 claims its WAL slot first and is held before its commit point; W2
    /// commits entirely; W1 then draws the later stamp and publishes last. Chain order (W1's entry first), LSN order (W1 first) and stamp order (W2 first)
    /// disagree. One newest, the highest stamp — W1's — in the walk, in the cluster HEAD, and after a crash (the replay by stamp, the scrub by stamp).
    /// </summary>
    [Test]
    [VerifiesRule("VIS-05")]
    [CancelAfter(30_000)]
    public void TwoWritersPublishingAgainstTheirStampOrder_LeaveOneNewest_TheHighestStamp()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            id = Spawn(dbe, 1)[0];
            var log = (DurabilityLog)dbe.DurabilityLog;
            var gate = new Gate();
            log.AfterFloorProbe = gate.Probe;
            long w1Stamp, w2Stamp;
            try
            {
                var w1 = new BackgroundCommit(dbe, tx => Set(tx, id, 1));
                gate.ThreadId = w1.ThreadId;
                w1.Go();
                gate.AwaitReached();

                using (var w2 = dbe.CreateQuickTransaction())
                {
                    Set(w2, id, 2);
                    Assert.That(w2.Commit(), Is.True);
                    w2Stamp = w2.CommitStamp;
                }

                gate.Release();
                w1.Join();
                w1Stamp = w1.Stamp;
            }
            finally
            {
                gate.Release();
                log.AfterFloorProbe = null;
            }

            Assert.That(w1Stamp, Is.GreaterThan(w2Stamp), "premise: the writer that claimed first drew the later stamp");
            Assert.That(ReadNew(dbe, id), Is.EqualTo(1), $"{Marker}: the walk's newest is not the highest stamp");
            Assert.That(ClusterHead(dbe), Is.EqualTo(1), $"{Marker}: the cluster HEAD is not the highest stamp");

            // Both records durable, then the power cut.
            using (var flush = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                flush.Spawn<CsWalletArch>(CsWalletArch.Wallet.Set(new CsWallet { Credits = -1 }));
                Assert.That(flush.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        var reopened = Open(scope2);
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        Assert.That(ReadNew(reopened, id), Is.EqualTo(1), $"{Marker}: the recovery's newest is not the highest stamp (replayed in LSN order?)");
    }

    /// <summary>
    /// Compaction by stamp (VIS-06): the chain <c>[A(s1), B(s2)]</c> with s1 &gt; s2 — A's entry first, its stamp the later — compacted past both: the
    /// highest-stamp revision below the cutoff is A, and it is kept. The chain-order sentinel freed it and kept B, the older value.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-06")]
    [CancelAfter(30_000)]
    public void ACompactionKeepsTheHighestStampBelowTheCutoff_NotTheLastInChainOrder()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var id = Spawn(dbe, 1)[0];
        var log = (DurabilityLog)dbe.DurabilityLog;
        var gate = new Gate();
        log.AfterFloorProbe = gate.Probe;
        long aStamp, bStamp;
        try
        {
            var a = new BackgroundCommit(dbe, tx => Set(tx, id, 12));
            gate.ThreadId = a.ThreadId;
            a.Go();
            gate.AwaitReached();
            using (var b = dbe.CreateQuickTransaction())
            {
                Set(b, id, 10);
                Assert.That(b.Commit(), Is.True);
                bStamp = b.CommitStamp;
            }

            gate.Release();
            a.Join();
            aStamp = a.Stamp;
        }
        finally
        {
            gate.Release();
            log.AfterFloorProbe = null;
        }

        Assert.That(aStamp, Is.GreaterThan(bStamp), "premise: A's stamp is the later");
        var table = dbe.GetComponentTable<CsWallet>();
        var root = RootOf(dbe, id);
        Compact(dbe, table, root, aStamp + 1);
        Assert.That(ReadNew(dbe, id), Is.EqualTo(12), $"{Marker}: the compaction freed the highest-stamp revision below its cutoff");
    }

    /// <summary>
    /// VIS-07: the open's scrub drops every revision still flagged with an owner — staged by a transaction that never committed — and keeps the committed
    /// head, so no transaction of the reopened engine, whatever its TSN, can take a dead owner's staged revision for its own or resolve it through a card.
    /// Driven on the scrub itself, over a chain holding a live transaction's staged entry: a checkpoint does not write a page while its writer is active.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-07")]
    [CancelAfter(30_000)]
    public void TheOpensScrub_DropsAStagedRevision()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var id = Spawn(dbe, 1)[0];
        using (var committed = dbe.CreateQuickTransaction())
        {
            Set(committed, id, 50);
            Assert.That(committed.Commit(), Is.True);
        }

        using var staged = dbe.CreateQuickTransaction();
        Set(staged, id, 66);
        var root = RootOf(dbe, id);
        Assert.That(Isolated(dbe, root), Is.EqualTo(1), "premise: the staged revision is in the chain, flagged");

        var table = dbe.GetComponentTable<CsWallet>();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var cs = dbe.MMF.CreateChangeSet();
            var rev = table.CompRevTableSegment.CreateChunkAccessor(cs);
            var content = table.ComponentSegment.CreateChunkAccessor(cs);
            var dropped = new System.Collections.Generic.List<int>();
            ComponentRevisionManager.ScrubChainToHead(table, root, ref rev, ref content, dropped, [], out _);
            rev.Dispose();
            content.Dispose();
            cs.SaveChanges();
        }

        Assert.That(Isolated(dbe, root), Is.Zero, $"{Marker}: the scrub kept a staged revision");
        Assert.That(ReadNew(dbe, id), Is.EqualTo(50), "the committed head is kept");
    }

    /// <summary>
    /// The compaction's skip phase over an out-of-order pair (VIS-06): <c>[spawn, A(s1), B(s2), F]</c>, s1 &gt; s2, F a staged entry. B, below the
    /// cutoff and older than the sentinel A, frees itself; F, flagged, ends the skip phase and keeps the sentinel. The chain-order sentinel kept B and
    /// freed A — the read after it is A's.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-06")]
    [CancelAfter(30_000)]
    public void ACompactionsSkipPhase_FreesAnOlderStampBehindTheSentinel()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var id = Spawn(dbe, 1)[0];
        var log = (DurabilityLog)dbe.DurabilityLog;
        var gate = new Gate();
        log.AfterFloorProbe = gate.Probe;
        long aStamp;
        try
        {
            var a = new BackgroundCommit(dbe, tx => Set(tx, id, 12));
            gate.ThreadId = a.ThreadId;
            a.Go();
            gate.AwaitReached();
            using (var b = dbe.CreateQuickTransaction())
            {
                Set(b, id, 10);
                Assert.That(b.Commit(), Is.True);
            }

            gate.Release();
            a.Join();
            aStamp = a.Stamp;
        }
        finally
        {
            gate.Release();
            log.AfterFloorProbe = null;
        }

        using var staged = dbe.CreateQuickTransaction();
        Set(staged, id, 99);
        var table = dbe.GetComponentTable<CsWallet>();
        Compact(dbe, table, RootOf(dbe, id), aStamp + 1);
        Assert.That(ReadNew(dbe, id), Is.EqualTo(12), $"{Marker}: the skip phase freed the highest-stamp revision for an older one");
    }

    private static void SetBag(Transaction tx, EntityId id, int item)
    {
        var target = tx.OpenMut(id);
        var bag = target.Read(EvoCcUnit.Bag);
        bag.A = item;
        using (var cca = target.CreateComponentCollectionAccessor(EvoCcUnit.Bag, ref bag, ref bag.Items))
        {
            cca.Add(item);
        }

        target.Set(EvoCcUnit.Bag, bag);
    }

    private static (int A, int[] Items) ReadBag(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        var bag = tx.Open(id).Read(EvoCcUnit.Bag);
        using var cca = tx.CreateComponentCollectionAccessor(ref bag.Items);
        var items = new int[cca.ElementCount];
        cca.GetAllElements(items);
        return (bag.A, items);
    }

    /// <summary>The value the cluster's HEAD copy holds for the bag archetype's one entity.</summary>
    private static int BagClusterHead(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        using var accessor = tx.For<EvoCcUnit>();
        var value = -1;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var bags = cluster.GetReadOnlySpan(EvoCcUnit.Bag);
            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                value = bags[idx].A;
            }
        }

        return value;
    }

    /// <summary>
    /// Two writers of one entity whose stamps invert their LSNs (W1 claims first, stamps last), each writing the value and its collection, then a crash
    /// (VIS-05). The recovered value, its collection and the cluster HEAD copy are all W1's: the replay keeps the highest stamp for the collection's fold
    /// as for the value. (A checkpoint between the two — W1 in the base, W2 replayed — cannot be staged for one entity: the cycle does not write a page an
    /// active writer holds. The applier's guard for it, a HEAD copy only above the chain's stamps, is defence in depth.) Under the Commit discipline the
    /// batch flags every record Committed, Versioned ones included: they still replay by stamp — Versioned is the table's storage mode, not the flag.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-05")]
    [CancelAfter(60_000)]
    public void InvertedWritersWithACollection_RecoverTheHighestStamp_ValueCollectionAndHead(
        [Values(CommitDiscipline.TickFence, CommitDiscipline.Commit)] CommitDiscipline discipline)
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            using (var spawn = dbe.CreateQuickTransaction())
            {
                id = spawn.Spawn<EvoCcUnit>(EvoCcUnit.Bag.Set(new EvoCcBagV1(0)));
                Assert.That(spawn.Commit(), Is.True);
            }

            var log = (DurabilityLog)dbe.DurabilityLog;
            var floorGate = new Gate();
            var appendGate = new Gate();
            log.AfterFloorProbe = floorGate.Probe;
            dbe.CommitAfterAppendProbe = appendGate.Probe;
            try
            {
                var w1 = new BackgroundCommit(dbe, tx => SetBag(tx, id, 1), discipline);
                floorGate.ThreadId = w1.ThreadId;
                w1.Go();
                floorGate.AwaitReached();

                var w2 = new BackgroundCommit(dbe, tx => SetBag(tx, id, 2), discipline);
                appendGate.ThreadId = w2.ThreadId;
                w2.Go();
                appendGate.AwaitReached();

                floorGate.Release();
                w1.Join();
                Assert.That(w1.Stamp, Is.GreaterThan(w2.Stamp == 0 ? 0 : w2.Stamp), "premise: W1 stamped after W2");
                appendGate.Release();
                w2.Join();
                Assert.That(w1.Stamp, Is.GreaterThan(w2.Stamp), "premise: the writer that claimed first drew the later stamp");
            }
            finally
            {
                floorGate.Release();
                appendGate.Release();
                log.AfterFloorProbe = null;
                dbe.CommitAfterAppendProbe = null;
            }

            Assert.That(ReadBag(dbe, id), Is.EqualTo((1, new[] { 1 })), "premise: the live newest is W1's");
            Assert.That(BagClusterHead(dbe), Is.EqualTo(1), "premise: the live HEAD copy is W1's");
            using (var flush = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                flush.Spawn<CsWalletArch>(CsWalletArch.Wallet.Set(new CsWallet { Credits = -1 }));
                Assert.That(flush.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        var reopened = Open(scope2);
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        Assert.That(ReadBag(reopened, id), Is.EqualTo((1, new[] { 1 })), $"{Marker}: the recovered value and collection are not the highest stamp's");
        Assert.That(BagClusterHead(reopened), Is.EqualTo(1), $"{Marker}: the recovered cluster HEAD copy is not the highest stamp's");
    }

    private static int Isolated(DatabaseEngine dbe, int root)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var revisions = dbe.GetComponentTable<CsWallet>().CompRevTableSegment.CreateChunkAccessor();
        try
        {
            var n = 0;
            using var en = new RevisionEnumerator(ref revisions, root, false, true, WaitContext.FromTimeout(TimeSpan.FromSeconds(5)));
            while (en.MoveNext())
            {
                n += !en.Current.IsVoid && en.Current.IsolationFlag ? 1 : 0;
            }

            return n;
        }
        finally
        {
            revisions.Dispose();
        }
    }

    private static void Compact(DatabaseEngine dbe, ComponentTable table, int root, long nextMinTsn)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var revisions = table.CompRevTableSegment.CreateChunkAccessor();
        var contents = table.ComponentSegment.CreateChunkAccessor();
        try
        {
            ref var header = ref revisions.GetChunk<CompRevStorageHeader>(root, true);
            Assert.That(header.Control.TryEnterExclusiveAccess(), Is.True, "premise: nothing holds the chain");
            try
            {
                ComponentRevisionManager.CleanUpUnusedEntriesCore(table, root, nextMinTsn, ref revisions, ref contents);
            }
            finally
            {
                revisions.GetChunk<CompRevStorageHeader>(root).Control.ExitExclusiveAccess();
            }
        }
        finally
        {
            contents.Dispose();
            revisions.Dispose();
        }
    }

    private static unsafe int RootOf(DatabaseEngine dbe, EntityId entityId)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var meta = dbe.GetMetaByRouting(entityId.ArchetypeId);
            var es = dbe._archetypeStates[meta.ArchetypeId];
            var record = stackalloc byte[meta._entityRecordSize];
            var accessor = es.EntityMap.Segment.CreateChunkAccessor();
            var found = es.EntityMap.TryGet(entityId.EntityKey, record, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True, "the entity's record");
            return ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, es.ClusterState.Layout.SlotToVersionedIndex[0]);
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }
}
