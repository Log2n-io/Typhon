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
