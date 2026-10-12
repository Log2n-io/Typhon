using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.CommitStamp.Wallet", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CsWallet
{
    public long Credits;
    public long Pad;
}

[Archetype]
class CsWalletArch : Archetype<CsWalletArch>
{
    public static readonly Comp<CsWallet> Wallet = Register<CsWallet>();
}

/// <summary>
/// Plan 06 step 3.2, rule VIS-03 (<c>rules/data-engine.md</c>): the commit-stamp infrastructure. A committer reserves a commit card before PREPARE, registers
/// and draws its stamp at the commit point, writes the stamp on TxCommit, and gives the card back after its publish or on any exit before the commit point.
/// Visibility is unchanged in this step: these tests check the protocol's mechanics, the wire and recovery.
/// </summary>
[TestFixture]
[NonParallelizable] // Probes and residue-staged TSNs: one engine at a time keeps the staged interleavings exact.
class CommitStampTests : TestBase<CommitStampTests>
{
    private const string Marker = "VIS-03 violated";

    // The reopen must find the WAL it crashed with. No periodic checkpoint: a crash test's window is the whole session.
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
        dbe.RetainRecoveryCommitStampsForTest = true;
        dbe.RegisterComponentFromAccessor<CsWallet>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private DatabaseEngine Open() => Open(ServiceProvider.CreateScope());

    private static EntityId[] SpawnWallets(DatabaseEngine dbe, int count)
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

    private static void SetCredits(Transaction tx, EntityId id, long credits) =>
        tx.OpenMut(id).Set(CsWalletArch.Wallet, new CsWallet { Credits = credits });

    private static long ReadCredits(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateReadOnlyTransaction();
        return tx.Open(id).Read(CsWalletArch.Wallet).Credits;
    }

    private static int CardsHeld(DatabaseEngine dbe) => dbe.CommitCards.CountHeld();

    /// <summary>Sets the TSN counter so the next transaction's TSN is the first one above it in <paramref name="residue"/>'s class.</summary>
    private static void NextTsnAtResidue(DatabaseEngine dbe, int residue)
    {
        var mask = dbe.CommitCards.Size - 1;
        var next = dbe.TransactionChain.NextFreeId + 1;
        next += (residue - (next & mask) + mask + 1) & mask;
        dbe.TransactionChain.SetNextFreeIdForTest(next - 1);
    }

    private static Transaction CreateAtResidue(DatabaseEngine dbe, object gate, int residue)
    {
        lock (gate)
        {
            NextTsnAtResidue(dbe, residue);
            return dbe.CreateQuickTransaction();
        }
    }

    // ── The wire and recovery ───────────────────────────────────────────────

    /// <summary>
    /// The stamp a commit draws rides on its TxCommit record, and a crash open reads it back for that commit's identity TSN: a no-handler and a handler commit,
    /// each with TSNs drawn between its creation and its commit point, so its stamp is not its TSN.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(30_000)]
    public void TheStampOnTxCommit_IsTheStampRecovered()
    {
        var expected = new Dictionary<long, long>();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var ids = SpawnWallets(dbe, 2);
            for (var i = 0; i < 2; i++)
            {
                using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                SetCredits(tx, ids[i], 500 + i);
                for (var k = 0; k < 3; k++)
                {
                    dbe.CreateReadOnlyTransaction().Dispose();
                }

                Assert.That(i == 0 ? tx.Commit() : tx.Commit(static (ref ConcurrencyConflictSolver _) => { }), Is.True);
                Assert.That(tx.CommitStamp, Is.GreaterThan(tx.TSN + 3), "premise: TSNs were drawn between the transaction's creation and its commit point");
                expected[tx.TSN] = tx.CommitStamp;
            }

            Assert.That(CardsHeld(dbe), Is.Zero, $"{Marker}: a card is held after every commit returned");
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        var reopened = Open(scope2);
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        var recovered = reopened.LastWalV2RecoveryResult.CommitStamps;
        foreach (var (tsn, stamp) in expected)
        {
            Assert.That(recovered.TryGetValue(tsn, out var got), Is.True, $"the commit of TSN {tsn} was not recovered as committed");
            Assert.That(got, Is.EqualTo(stamp), $"the stamp recovered for TSN {tsn}");
        }
    }

    /// <summary>
    /// The codec: the stamp rides after the TxCommit record's body, outside its <c>BodyLength</c>, and only there; a TxCommit record whose trailer is cut
    /// off is a torn record.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    public void TheStampTrailer_RoundTrips_AndACutOneIsATornRecord()
    {
        var b = new CommitBatchBuilder(new CommitBatchArena(), tsn: 40, uowEpoch: 1);
        b.AddSlot(1, 0, new byte[16]);
        b.AddSlot(2, 0, new byte[16]);
        b.CommitStamp = 47;
        var size = RecordCodec.Measure(b, out _, out _);
        var buf = new byte[size];
        Assert.That(RecordCodec.Write(buf, b, firstLsn: 100), Is.EqualTo(size), "Write consumes exactly what Measure counted, the trailer included");

        var body = buf.AsSpan(WalChunkHeader.SizeInBytes, size - WalChunkHeader.SizeInBytes - WalChunkFooter.SizeInBytes);
        Assert.That(RecordCodec.TryReadRecord(body, 0, out var first, out var v1), Is.True);
        Assert.That(v1.IsTxCommit, Is.False);
        Assert.That(v1.CommitStamp, Is.Zero, "only the TxCommit record carries the stamp");
        Assert.That(RecordCodec.TryReadRecord(body, first, out var second, out var v2), Is.True);
        Assert.That(v2.IsTxCommit, Is.True);
        Assert.That(v2.CommitStamp, Is.EqualTo(47), "the stamp read back");
        Assert.That(v2.Tsn, Is.EqualTo(40), "the identity is unchanged");
        Assert.That(first + second, Is.EqualTo(body.Length), "the trailer is the record's last 8 bytes");
        Assert.That(RecordCodec.TryReadRecord(body[..^1], first, out _, out _), Is.False, "a TxCommit record with a cut trailer is torn");
    }

    /// <summary>
    /// RB-05 over the stamps: a commit whose stamp is above every TSN the log names — its own and every write's — and a crash. The reopened counter resumes
    /// above the stamp, or a TSN drawn after the open would equal a commit point.
    /// </summary>
    [Test]
    [VerifiesRule("RB-05")]
    [VerifiesRule("VIS-03")]
    [CancelAfter(30_000)]
    public void AfterACrash_TheNextTsnIsAboveEveryStamp()
    {
        long stamp;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var ids = SpawnWallets(dbe, 1);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                SetCredits(tx, ids[0], 777);
                for (var k = 0; k < 10; k++)
                {
                    dbe.CreateReadOnlyTransaction().Dispose();
                }

                Assert.That(tx.Commit(), Is.True);
                stamp = tx.CommitStamp;
                Assert.That(stamp, Is.GreaterThan(tx.TSN + 2), "premise: the stamp is above every TSN a record carries, past the resumption's own margin");
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        var reopened = Open(scope2);
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        Assert.That(reopened.LastWalV2RecoveryResult.MaxCommitStamp, Is.EqualTo(stamp), "the stamp scanned");
        using var next = reopened.CreateReadOnlyTransaction();
        Assert.That(next.TSN, Is.GreaterThan(stamp), "RB-05 violated: a TSN drawn after the crash open is at or below a recovered commit stamp");
    }

    // ── The full window ─────────────────────────────────────────────────────

    /// <summary>One committing thread: creates its transaction at a given residue, writes, then commits when told to.</summary>
    private sealed class Committer
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _go = new(false);
        public readonly ManualResetEventSlim Created = new(false);
        public readonly ManualResetEventSlim Done = new(false);
        public long Tsn;
        public bool Result;
        public Exception Error;
        public int ThreadId;

        public Committer(DatabaseEngine dbe, object creationGate, int residue, EntityId id, long credits, bool handler, TimeSpan commitTimeout = default)
        {
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                Transaction tx;
                lock (creationGate)
                {
                    NextTsnAtResidue(dbe, residue);
                    tx = dbe.CreateQuickTransaction();
                }

                try
                {
                    Tsn = tx.TSN;
                    SetCredits(tx, id, credits);
                    Created.Set();
                    _go.Wait();
                    var ctx = UnitOfWorkContext.FromTimeout(commitTimeout == default ? TimeSpan.FromSeconds(10) : commitTimeout);
                    Result = handler ? tx.Commit(ref ctx, static (ref ConcurrencyConflictSolver _) => { }) : tx.Commit(ref ctx);
                }
                catch (Exception e)
                {
                    Error = e;
                }
                finally
                {
                    tx.Dispose();
                    Done.Set();
                }
            }) { IsBackground = true };
        }

        public Committer Start()
        {
            _thread.Start();
            Assert.That(Created.Wait(TimeSpan.FromSeconds(10)), Is.True, "a committer did not create its transaction");
            return this;
        }

        public void Go() => _go.Set();

        public void Join()
        {
            Assert.That(Done.Wait(TimeSpan.FromSeconds(20)), Is.True, "a committer did not finish");
            _thread.Join();
        }
    }

    /// <summary>Holds chosen threads at a commit probe until released, and says when each got there.</summary>
    private sealed class ProbeHolds
    {
        private readonly ConcurrentDictionary<int, (ManualResetEventSlim Reached, ManualResetEventSlim Release)> _holds = new();

        public Action Probe => () =>
        {
            if (_holds.TryGetValue(Environment.CurrentManagedThreadId, out var h))
            {
                h.Reached.Set();
                h.Release.Wait(TimeSpan.FromSeconds(20));
            }
        };

        public void Hold(Committer c) => _holds[c.ThreadId] = (new ManualResetEventSlim(false), new ManualResetEventSlim(false));

        public void AwaitReached(Committer c) =>
            Assert.That(_holds[c.ThreadId].Reached.Wait(TimeSpan.FromSeconds(10)), Is.True, "a held committer did not reach its probe");

        public void Release(Committer c) => _holds[c.ThreadId].Release.Set();
    }

    /// <summary>
    /// A full window: four committers whose TSNs share a residue hold the window's four cards, one of them on the handler path stopped after its append
    /// with its chain lock held and its card registered, three stopped after their reservation. A fifth of the same residue, the oldest epoch pin, waits at
    /// its reservation: holding no chain lock (a handler commit of its entity completes meanwhile), no WAL claim (that commit is durable), its epoch
    /// refreshed. Released, all five commit.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(60_000)]
    public void AFullWindow_WaitsAtTheReservation_HoldingNoLockAndNoClaim()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 6);
        const int residue = 1000;
        var gate = new object();
        var atReserve = new ProbeHolds();
        var atAppend = new ProbeHolds();
        dbe.CommitAfterReserveProbe = atReserve.Probe;
        dbe.CommitAfterAppendProbe = atAppend.Probe;
        try
        {
            // The fifth first: its epoch is the oldest. Then a bump, so the owners pin a later one.
            var fifth = new Committer(dbe, gate, residue, ids[4], 504, handler: true).Start();
            var fifthEpoch = dbe.EpochManager.GlobalEpoch;
            dbe.EpochManager.BumpEpochForRetire();

            var owners = new Committer[4];
            for (var i = 0; i < 4; i++)
            {
                owners[i] = new Committer(dbe, gate, residue, ids[i], 500 + i, handler: i == 0).Start();
            }

            atAppend.Hold(owners[0]);
            for (var i = 1; i < 4; i++)
            {
                atReserve.Hold(owners[i]);
            }

            foreach (var o in owners)
            {
                o.Go();
            }

            atAppend.AwaitReached(owners[0]);
            for (var i = 1; i < 4; i++)
            {
                atReserve.AwaitReached(owners[i]);
            }

            var window = dbe.CommitCards;
            Assert.That(new[] { 0, 1, 2, 3 }, Has.Some.Matches<int>(i => window.OwnerOfSlot(residue + i) == owners[0].Tsn),
                "premise: the handler committer is registered on a card of the window");
            Assert.That(CardsHeld(dbe), Is.EqualTo(4), "premise: the window is full");

            var waitsBefore = Interlocked.Read(ref dbe.CommitCards.FullWindowWaits);
            fifth.Go();
            Assert.That(SpinWait.SpinUntil(() => Interlocked.Read(ref dbe.CommitCards.FullWindowWaits) > waitsBefore, TimeSpan.FromSeconds(10)), Is.True,
                $"{Marker}: the fifth committer did not wait for a card");

            // No chain lock: a handler commit of the fifth's entity takes that chain's lock in PREPARE. No WAL claim: an unpublished frame would hold the
            // writer, and this commit's durable wait with it.
            using (var other = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                SetCredits(other, ids[4], 999);
                var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(5));
                Assert.That(other.Commit(ref ctx, static (ref ConcurrencyConflictSolver _) => { }), Is.True,
                    $"{Marker}: a commit of the waiting committer's entity did not complete");
            }

            Assert.That(SpinWait.SpinUntil(() => dbe.EpochManager.MinActiveEpoch > fifthEpoch, TimeSpan.FromSeconds(10)), Is.True,
                $"{Marker}: the committer waiting for a card kept its epoch: {dbe.EpochManager.DescribePinnedThreads()}");
            Assert.That(fifth.Done.IsSet, Is.False, "premise: the fifth still waits");

            atAppend.Release(owners[0]);
            for (var i = 1; i < 4; i++)
            {
                atReserve.Release(owners[i]);
            }

            foreach (var c in owners)
            {
                c.Join();
            }

            fifth.Join();
            foreach (var c in owners)
            {
                Assert.That(c.Error, Is.Null);
                Assert.That(c.Result, Is.True);
            }

            Assert.That(fifth.Error, Is.Null, $"{Marker}: the fifth committer failed");
            Assert.That(fifth.Result, Is.True);
            for (var i = 0; i < 4; i++)
            {
                Assert.That(ReadCredits(dbe, ids[i]), Is.EqualTo(500 + i));
            }

            Assert.That(CardsHeld(dbe), Is.Zero, $"{Marker}: a card is held after every commit returned");
        }
        finally
        {
            dbe.CommitAfterReserveProbe = null;
            dbe.CommitAfterAppendProbe = null;
        }
    }

    /// <summary>
    /// A full window and a committer whose unit of work's deadline passes while it waits for a card: a clean abort, before anything is staged. It holds no
    /// card, its entity keeps its committed value, and the same transaction commits once the window frees.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(60_000)]
    public void TheReservationWait_GivesUpAtTheDeadline_AndTheTransactionCommitsLater()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 5);
        const int residue = 2000;
        var gate = new object();
        var atReserve = new ProbeHolds();
        dbe.CommitAfterReserveProbe = atReserve.Probe;
        try
        {
            var owners = new Committer[4];
            for (var i = 0; i < 4; i++)
            {
                owners[i] = new Committer(dbe, gate, residue, ids[i], 500 + i, handler: false).Start();
                atReserve.Hold(owners[i]);
                owners[i].Go();
                atReserve.AwaitReached(owners[i]);
            }

            using (var late = CreateAtResidue(dbe, gate, residue))
            {
                SetCredits(late, ids[4], 4444);
                var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromMilliseconds(100));
                Assert.Throws<LockTimeoutException>(() => late.Commit(ref ctx), $"{Marker}: the reservation wait ignored the deadline");
                Assert.That(late.State, Is.EqualTo(Transaction.TransactionState.InProgress), $"{Marker}: the deadline's abort was not clean");
                Assert.That(CardsHeld(dbe), Is.EqualTo(4), $"{Marker}: the timed-out committer holds a card");
                Assert.That(ReadCredits(dbe, ids[4]), Is.EqualTo(104), $"{Marker}: the timed-out commit left an effect");

                foreach (var o in owners)
                {
                    atReserve.Release(o);
                    o.Join();
                    Assert.That(o.Result, Is.True);
                }

                Assert.That(late.Commit(), Is.True, "the transaction commits once the window frees");
            }

            Assert.That(ReadCredits(dbe, ids[4]), Is.EqualTo(4444));
            Assert.That(CardsHeld(dbe), Is.Zero);
        }
        finally
        {
            dbe.CommitAfterReserveProbe = null;
        }
    }

    // ── Every exit before the commit point gives the card back ──────────────

    /// <summary>
    /// A commit that fails after its reservation and before PREPARE — the transaction stays open — and is retried, with three of its window's four cards
    /// held: a leaked reservation would fill the fourth, and the retry would wait on itself.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(60_000)]
    public void ARetriedCommit_DoesNotWaitOnItsOwnReservation() => RunRetriedCommit(leakTheReservation: false);

    /// <summary>The verifier above, against a leaked reservation (the card the failed commit should have given back, taken for it): the retry waits on it.</summary>
    [Test]
    [RuleMutant("VIS-03")]
    [CancelAfter(60_000)]
    public void Mutant_ALeakedReservation_MakesTheRetryWaitOnItself() =>
        RuleMutants.AssertDetects("VIS-03", "the retried commit did not complete", () => RunRetriedCommit(leakTheReservation: true));

    private void RunRetriedCommit(bool leakTheReservation)
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 4);
        const int residue = 3000;
        var gate = new object();
        var atReserve = new ProbeHolds();
        dbe.CommitAfterReserveProbe = atReserve.Probe;
        Committer[] owners = [];
        try
        {
            owners = new Committer[3];
            for (var i = 0; i < 3; i++)
            {
                owners[i] = new Committer(dbe, gate, residue, ids[i], 500 + i, handler: false).Start();
                atReserve.Hold(owners[i]);
                owners[i].Go();
                atReserve.AwaitReached(owners[i]);
            }

            using (var tx = CreateAtResidue(dbe, gate, residue))
            {
                SetCredits(tx, ids[3], 3333);
                var probe = dbe.CommitAfterReserveProbe;
                var failOnce = 1;
                dbe.CommitAfterReserveProbe = () =>
                {
                    if (Environment.CurrentManagedThreadId == owners[0].ThreadId || Environment.CurrentManagedThreadId == owners[1].ThreadId
                        || Environment.CurrentManagedThreadId == owners[2].ThreadId)
                    {
                        probe();
                    }
                    else if (Interlocked.Exchange(ref failOnce, 0) == 1)
                    {
                        throw new InvalidOperationException("staged: a failure after the reservation, before PREPARE");
                    }
                };

                Assert.Throws<InvalidOperationException>(() => tx.Commit());
                Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.InProgress), "premise: the failure left the transaction open");
                Assert.That(CardsHeld(dbe), Is.EqualTo(3), $"{Marker}: the failed commit kept its reservation");
                if (leakTheReservation)
                {
                    Assert.That(dbe.CommitCards.TryReserve(tx.TSN, out _), Is.True, "premise: the mutant takes the window's last card");
                }

                var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromMilliseconds(leakTheReservation ? 200 : 2_000));
                bool committed;
                try
                {
                    committed = tx.Commit(ref ctx);
                }
                catch (LockTimeoutException)
                {
                    committed = false;
                }

                Assert.That(committed, Is.True, $"{Marker}: the retried commit did not complete");
            }

            Assert.That(ReadCredits(dbe, ids[3]), Is.EqualTo(3333));
        }
        finally
        {
            foreach (var o in owners)
            {
                atReserve.Release(o);
                o.Join();
            }

            dbe.CommitAfterReserveProbe = null;
        }

        Assert.That(CardsHeld(dbe), Is.Zero);
    }

    /// <summary>A conflict handler that throws ends the commit in PREPARE: the card goes back.</summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(15_000)]
    public void AConflictThatAbortsTheCommit_GivesTheCardBack()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 1);
        using var loser = dbe.CreateQuickTransaction();
        SetCredits(loser, ids[0], 1);
        using (var winner = dbe.CreateQuickTransaction())
        {
            SetCredits(winner, ids[0], 2);
            Assert.That(winner.Commit(), Is.True);
        }

        Assert.Throws<InvalidOperationException>(() => loser.Commit(static (ref ConcurrencyConflictSolver _) => throw new InvalidOperationException("conflict")));
        Assert.That(CardsHeld(dbe), Is.Zero, $"{Marker}: an aborted conflict kept its card");
    }

    /// <summary>
    /// A handler commit whose chain lock is held by another committer past the deadline: a lock timeout in PREPARE, and the card goes back. The failed
    /// commit's rollback waits for that same lock with no deadline (#1273), so the holder is let go on a timer, and no card may be held once both are done.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(30_000)]
    public void AChainLockTimeout_GivesTheCardBack()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 1);
        var atAppend = new ProbeHolds();
        dbe.CommitAfterAppendProbe = atAppend.Probe;

        // Written before the holder takes the chain's lock: the write's own read of the chain would wait on it.
        using var tx = dbe.CreateQuickTransaction();
        SetCredits(tx, ids[0], 22);
        var holder = new Committer(dbe, new object(), 4000, ids[0], 11, handler: true).Start();
        try
        {
            atAppend.Hold(holder);
            holder.Go();
            atAppend.AwaitReached(holder);
            Assert.That(CardsHeld(dbe), Is.EqualTo(1), "premise: the holder is registered");

            using var letGo = new Timer(_ => atAppend.Release(holder), null, 500, Timeout.Infinite);
            var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromMilliseconds(100));
            var thrown = Assert.Throws<LockTimeoutException>(() => tx.Commit(ref ctx, static (ref ConcurrencyConflictSolver _) => { }));
            Assert.That(thrown!.ResourceName, Is.EqualTo("RevisionChain/CommitConflict"), "premise: the commit timed out on the chain lock in PREPARE");
        }
        finally
        {
            atAppend.Release(holder);
            holder.Join();
            dbe.CommitAfterAppendProbe = null;
        }

        Assert.That(holder.Result, Is.True);
        Assert.That(CardsHeld(dbe), Is.Zero, $"{Marker}: a commit that timed out on a chain lock kept its card");
    }

    /// <summary>A spawn onto a held unique key is rejected before the append: the card goes back.</summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(15_000)]
    public void AUniqueCollision_GivesTheCardBack()
    {
        using var dbe = Open();
        using (var seed = dbe.CreateQuickTransaction())
        {
            seed.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 75, 1.0)));
            Assert.That(seed.Commit(), Is.True);
        }

        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 75, 1.0)));
        Assert.Throws<UniqueConstraintViolationException>(() => tx.Commit());
        Assert.That(CardsHeld(dbe), Is.Zero, $"{Marker}: a unique collision kept its card");
    }

    /// <summary>
    /// A commit that fails at its WAL claim — back-pressure, or any abort at APPEND, staged where the claim is attempted — is before its commit point: the
    /// card goes back, unregistered, and no stamp was drawn.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(15_000)]
    public void AnAbortAtTheWalClaim_GivesTheCardBack_WithNoStampDrawn()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 1);
        var log = (DurabilityLog)dbe.DurabilityLog;
        var committer = Environment.CurrentManagedThreadId;
        using var tx = dbe.CreateQuickTransaction();
        SetCredits(tx, ids[0], 5);
        log.BeforeClaimProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == committer)
            {
                throw new WalBackPressureTimeoutException(64, TimeSpan.FromMilliseconds(1));
            }
        };
        try
        {
            var before = dbe.TransactionChain.NextFreeId;
            Assert.Throws<WalBackPressureTimeoutException>(() => tx.Commit());
            Assert.That(dbe.TransactionChain.NextFreeId, Is.EqualTo(before), $"{Marker}: a commit that failed at its claim drew a stamp");
            Assert.That(tx.CommitStamp, Is.Zero);
        }
        finally
        {
            log.BeforeClaimProbe = null;
        }

        Assert.That(CardsHeld(dbe), Is.Zero, $"{Marker}: an abort at the WAL claim kept its card");
        Assert.That(ReadCredits(dbe, ids[0]), Is.EqualTo(100));
    }

    /// <summary>
    /// A commit stalled after its reservation holds nothing the WAL needs: other commits' frames are published and made durable meanwhile, and the stalled
    /// commit then completes.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(30_000)]
    public void AStalledReservingCommitter_DoesNotHoldTheWalDrain()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 3);
        var atReserve = new ProbeHolds();
        dbe.CommitAfterReserveProbe = atReserve.Probe;
        var stalled = new Committer(dbe, new object(), 5000, ids[0], 1, handler: false).Start();
        try
        {
            atReserve.Hold(stalled);
            stalled.Go();
            atReserve.AwaitReached(stalled);

            for (var i = 1; i < 3; i++)
            {
                using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                SetCredits(tx, ids[i], 900 + i);
                var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(5));
                Assert.That(tx.Commit(ref ctx), Is.True, $"{Marker}: a durable commit did not complete beside a stalled reserving committer");
            }

            Assert.That(dbe.DurabilityLog.DurableLsn, Is.GreaterThanOrEqualTo(dbe.DurabilityLog.LastAppendedLsn), "the other commits are durable");
        }
        finally
        {
            atReserve.Release(stalled);
            stalled.Join();
            dbe.CommitAfterReserveProbe = null;
        }

        Assert.That(stalled.Result, Is.True);
        Assert.That(ReadCredits(dbe, ids[0]), Is.EqualTo(1));
    }

    /// <summary>
    /// An abort between the WAL claim and the commit point — the engine's stop re-checked once the claim holds (#1269) — is before the commit point:
    /// the claim is abandoned, the card goes back, no stamp is drawn.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(15_000)]
    public void AnAbortAfterTheClaimBeforeTheCommitPoint_GivesTheCardBack_WithNoStampDrawn()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 1);
        var log = (DurabilityLog)dbe.DurabilityLog;
        var committer = Environment.CurrentManagedThreadId;
        using var tx = dbe.CreateQuickTransaction();
        SetCredits(tx, ids[0], 5);
        log.AfterFloorProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == committer)
            {
                throw new InvalidOperationException("staged: a failure once the claim holds, before the commit point");
            }
        };
        try
        {
            var before = dbe.TransactionChain.NextFreeId;
            Assert.Throws<InvalidOperationException>(() => tx.Commit());
            Assert.That(dbe.TransactionChain.NextFreeId, Is.EqualTo(before), $"{Marker}: a commit that failed before its commit point drew a stamp");
        }
        finally
        {
            log.AfterFloorProbe = null;
        }

        Assert.That(CardsHeld(dbe), Is.Zero, $"{Marker}: the abort kept its card");
        Assert.That(ReadCredits(dbe, ids[0]), Is.EqualTo(100));
        using var after = dbe.CreateQuickTransaction();
        SetCredits(after, ids[0], 6);
        Assert.That(after.Commit(), Is.True, "the engine commits on: the abandoned claim does not hold the log");
    }

    /// <summary>
    /// VIS-04: a publish that fails past the append keeps its card registered — what it left flagged belongs to a commit whose records are in the log,
    /// and a reader resolves it by the card's stamp — and the engine commits no more (AP-03).
    /// </summary>
    [Test]
    [VerifiesRule("VIS-04")]
    [CancelAfter(30_000)]
    public void APublishThatFailsPastTheAppend_KeepsItsCardRegistered()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 2);
        var committer = Environment.CurrentManagedThreadId;
        var calls = 0;
        dbe.PublishComponentProbe = (_, _) =>
        {
            if (Environment.CurrentManagedThreadId == committer && ++calls == 2)
            {
                throw new InvalidOperationException("staged: a publish that fails past the append");
            }
        };
        long tsn;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            tsn = tx.TSN;
            SetCredits(tx, ids[0], 7);
            SetCredits(tx, ids[1], 8);
            Assert.Throws<CommitPublishIncompleteException>(() => tx.Commit());
        }
        finally
        {
            dbe.PublishComponentProbe = null;
        }

        Assert.That(CardsHeld(dbe), Is.EqualTo(1), $"{Marker}: a failed publish gave its card back");
        var window = dbe.CommitCards;
        var start = (int)(tsn & (window.Size - 1));
        Assert.That(new[] { 0, 1, 2, 3 }, Has.Some.Matches<int>(i => window.OwnerOfSlot((start + i) & (window.Size - 1)) == tsn),
            $"{Marker}: the card is not registered with the failed commit");
        Assert.That(window.TryGetStamp(tsn, out var stamp), Is.True);
        Assert.That(stamp, Is.GreaterThan(tsn));
        Assert.That(dbe.CommitPipelineFailure, Is.Not.Null, "premise: the engine stopped committing");
        dbe.SimulateHardCrash();
    }

    /// <summary>A commit with no WAL record still draws a stamp, at the end of PREPARE, and gives its card back.</summary>
    [Test]
    [VerifiesRule("VIS-03")]
    [CancelAfter(15_000)]
    public void ACommitWithNoWalRecord_DrawsAStamp()
    {
        using var dbe = Open();
        var ids = SpawnWallets(dbe, 1);
        using var tx = dbe.CreateQuickTransaction();
        _ = tx.OpenMut(ids[0]);
        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        Assert.That(tx.Commit(), Is.True);
        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), "premise: the commit appended nothing");
        Assert.That(tx.CommitStamp, Is.GreaterThan(tx.TSN), $"{Marker}: a commit with no WAL record drew no stamp");
        Assert.That(CardsHeld(dbe), Is.Zero);
    }
}
