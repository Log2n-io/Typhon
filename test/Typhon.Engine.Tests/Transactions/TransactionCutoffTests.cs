using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// TXC-01: the revision cleanup's cutoff never passes the snapshot of a transaction that exists or is being created. A creator is held inside its creation
/// window — between the two steps that register it — while a writer commits and the tail's disposal trims the writer's entity: every snapshot must still
/// read the value it should.
/// </summary>
[TestFixture]
[NonParallelizable] // The creation probe is per engine, but holds a creator for up to seconds; keep the timing undisturbed.
class TransactionCutoffTests : TestBase<TransactionCutoffTests>
{
    private const int Before = 100, After = 101;

    /// <summary>Where the held creator sits relative to the writer and to the tail's cleanup.</summary>
    public enum Interleaving
    {
        /// <summary>The creator is still in its window while the tail trims.</summary>
        HeldAcrossTheCleanup,

        /// <summary>The creator leaves its window before the tail trims; the writer commits before the younger reader begins.</summary>
        ReleasedBeforeTheCleanup_WriterFirst,

        /// <summary>The creator leaves its window before the tail trims; the younger reader begins before the writer.</summary>
        ReleasedBeforeTheCleanup_ReaderFirst,
    }

    [Test]
    [VerifiesRule("TXC-01")]
    [CancelAfter(20_000)]
    public void ACutoffNeverPassesASnapshot([Values] Interleaving interleaving) => AssertNoSnapshotIsTrimmed(interleaving, cutoffPastEverySnapshot: false);

    [Test]
    [RuleMutant("TXC-01")]
    [CancelAfter(20_000)]
    public void Mutant_ACutoffPastEverySnapshot_TrimsOneStillRead() =>
        RuleMutants.AssertDetects("TXC-01", "a cutoff passed a live snapshot",
            () => AssertNoSnapshotIsTrimmed(Interleaving.ReleasedBeforeTheCleanup_ReaderFirst, cutoffPastEverySnapshot: true));

    /// <param name="interleaving">Where the held creator sits relative to the writer and the tail's cleanup.</param>
    /// <param name="cutoffPastEverySnapshot">
    /// The mutant: the tail hands the cleanup a cutoff above every TSN issued — what a walker computes when it misses a reader — instead of the one the chain
    /// gives.
    /// </param>
    private void AssertNoSnapshotIsTrimmed(Interleaving interleaving, bool cutoffPastEverySnapshot)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        EntityId entity;
        using (var seed = dbe.CreateQuickTransaction())
        {
            entity = seed.Spawn<CompAArch>(CompAArch.A.Set(new CompA(Before)));
            seed.Commit();
        }

        // Transactions are thread-affine: the tail and the held creator each live on their own thread, the writer and the younger reader on this one.
        using var tailThread = new Actor("tail");
        using var heldThread = new Actor("held creator");
        var tx = new Cast();
        Assert.That(tailThread.Run(() => tx.Tail = dbe.CreateQuickTransaction()).Wait(TimeSpan.FromSeconds(5)), Is.True, "the tail was not created");

        using var inWindow = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var fired = 0;
        dbe.TransactionChain.CreationWindowProbe = () =>
        {
            if (Environment.CurrentManagedThreadId == heldThread.ManagedThreadId && Interlocked.Exchange(ref fired, 1) == 0)
            {
                inWindow.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        if (cutoffPastEverySnapshot)
        {
            dbe.TransactionChain.CutoffProbe = _ => dbe.TransactionChain.NextFreeId + 1;
        }

        var problems = new List<string>();
        try
        {
            var heldCreated = heldThread.Run(() => tx.Held = dbe.CreateReadOnlyTransaction());
            Assert.That(inWindow.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: the creator is held in its window");

            if (interleaving == Interleaving.ReleasedBeforeTheCleanup_ReaderFirst)
            {
                tx.Young = dbe.CreateReadOnlyTransaction();
            }

            long writerTsn;
            using (var writer = dbe.CreateQuickTransaction())
            {
                writer.OpenMut(entity).Set(CompAArch.A, new CompA(After));
                Assert.That(writer.Commit(), Is.True);
                writerTsn = writer.TSN;
            }

            tx.Young ??= dbe.CreateReadOnlyTransaction();
            if (interleaving == Interleaving.ReleasedBeforeTheCleanup_ReaderFirst)
            {
                Assert.That(tx.Young.TSN, Is.LessThan(writerTsn), "premise: the younger reader's snapshot precedes the writer");
            }

            var cleanedBefore = dbe.DeferredCleanupManager.ProcessedTotal;
            Task tailGone;
            if (interleaving == Interleaving.HeldAcrossTheCleanup)
            {
                // The tail computes its cutoff while the creator is held: either it finishes (it did not see the creator), or it waits on the creator's
                // pending TSN. Staged on one or the other, never on a delay, before the creator is let go.
                var waitsBefore = dbe.TransactionChain.PendingNodeWaits;
                tailGone = tailThread.Run(() => tx.Tail.Dispose());
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!tailGone.IsCompleted && dbe.TransactionChain.PendingNodeWaits == waitsBefore && waited.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Yield();
                }

                Assert.That(tailGone.IsCompleted || dbe.TransactionChain.PendingNodeWaits > waitsBefore, Is.True,
                    "premise: the tail computed its cutoff while the creator was held");
                release.Set();
            }
            else
            {
                release.Set();
                Assert.That(heldCreated.Wait(TimeSpan.FromSeconds(5)), Is.True, "the held creator never finished");
                tailGone = tailThread.Run(() => tx.Tail.Dispose());
            }

            Assert.That(tailGone.Wait(TimeSpan.FromSeconds(5)), Is.True, "the tail's disposal never finished");
            tx.Tail = null;
            Assert.That(heldCreated.Wait(TimeSpan.FromSeconds(5)), Is.True, "the held creator never finished");
            Assert.That(dbe.DeferredCleanupManager.ProcessedTotal, Is.GreaterThan(cleanedBefore), "premise: the tail's disposal trimmed the entity's chain");

            Check(problems, "the younger reader", tx.Young, entity, writerTsn);
            heldThread.Run(() => Check(problems, "the held creator", tx.Held, entity, writerTsn)).Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            dbe.TransactionChain.CreationWindowProbe = null;
            dbe.TransactionChain.CutoffProbe = null;
            heldThread.Run(() => tx.Held?.Dispose()).Wait(TimeSpan.FromSeconds(5));
            tx.Young?.Dispose();
            tailThread.Run(() => tx.Tail?.Dispose()).Wait(TimeSpan.FromSeconds(5));
        }

        Assert.That(problems, Is.Empty, "a cutoff passed a live snapshot");
    }

    /// <summary>
    /// TXC-02: a transaction pushed into the chain that a Remove is emptying becomes the tail. The Remove of the lone tail is held just before or just after
    /// its head CAS, and a transaction is pushed meanwhile.
    /// </summary>
    [Test]
    [VerifiesRule("TXC-02")]
    [CancelAfter(20_000)]
    public void APushIntoAnEmptyingChain_BecomesItsTail([Values] bool afterTheHeadCas) =>
        AssertAPushIntoAnEmptyingChainBecomesItsTail(afterTheHeadCas, clearTheTailAfterTheCas: false);

    [Test]
    [RuleMutant("TXC-02")]
    [CancelAfter(20_000)]
    public void Mutant_TheTailClearedAfterTheHeadCas_LeavesTheChainWithoutATail() =>
        RuleMutants.AssertDetects("TXC-02", "has no tail",
            () => AssertAPushIntoAnEmptyingChainBecomesItsTail(afterTheHeadCas: true, clearTheTailAfterTheCas: true));

    /// <param name="afterTheHeadCas">
    /// The removal is held after its CAS emptied the head — the push then finds the chain empty and must find the tail cleared — or before it, and the push
    /// makes that CAS fail: the removal re-scans and makes the pushed transaction the tail.
    /// </param>
    /// <param name="clearTheTailAfterTheCas">
    /// The mutant: the removal clears the tail after its CAS instead of before (<c>TailClearedAfterCasMutantForTest</c>).
    /// </param>
    private void AssertAPushIntoAnEmptyingChainBecomesItsTail(bool afterTheHeadCas, bool clearTheTailAfterTheCas)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var leavingThread = new Actor("leaving");
        var tx = new Cast();
        using var inWindow = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var fired = 0;
        dbe.TransactionChain.TailClearedAfterCasMutantForTest = clearTheTailAfterTheCas;
        dbe.TransactionChain.RemoveTailProbe = casDone =>
        {
            if (casDone == afterTheHeadCas && Environment.CurrentManagedThreadId == leavingThread.ManagedThreadId && Interlocked.Exchange(ref fired, 1) == 0)
            {
                inWindow.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        try
        {
            Assert.That(leavingThread.Run(() => tx.Tail = dbe.CreateReadOnlyTransaction()).Wait(TimeSpan.FromSeconds(5)), Is.True,
                "the leaving one was not created");
            Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(1), "premise: the leaving transaction is alone in the chain");
            var gone = leavingThread.Run(() => tx.Tail.Dispose());
            Assert.That(inWindow.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: the removal is held at its head CAS");
            tx.Young = dbe.CreateReadOnlyTransaction();
            release.Set();
            Assert.That(gone.Wait(TimeSpan.FromSeconds(5)), Is.True, "the removal never finished");
            tx.Tail = null;

            Assert.That(dbe.TransactionChain.Tail, Is.SameAs(tx.Young), "a chain holding a transaction has no tail: no cutoff will ever be computed in it");
            Assert.That(dbe.TransactionChain.MinTSN, Is.EqualTo(tx.Young.TSN), "the tail's TSN");
        }
        finally
        {
            release.Set();
            dbe.TransactionChain.RemoveTailProbe = null;
            dbe.TransactionChain.TailClearedAfterCasMutantForTest = false;
            leavingThread.Run(() => tx.Tail?.Dispose()).Wait(TimeSpan.FromSeconds(5));
            tx.Young?.Dispose();
        }
    }

    /// <summary>Reads the entity in <paramref name="reader"/>: the value its snapshot must see, or a problem.</summary>
    private static void Check(List<string> problems, string who, Transaction reader, EntityId entity, long writerTsn)
    {
        var expected = reader.TSN >= writerTsn ? After : Before;
        string problem;
        try
        {
            if (!reader.TryOpen(entity, out var e))
            {
                problem = $"{who} (snapshot {reader.TSN}) no longer finds the entity; expected {expected}";
            }
            else
            {
                var read = e.Read(CompAArch.A).A;
                problem = read == expected
                    ? null
                    : $"{who} (snapshot {reader.TSN}) read {read}, expected {expected} (the writer's TSN is {writerTsn})";
            }
        }
        catch (Exception ex)
        {
            problem = $"{who} (snapshot {reader.TSN}) failed to read: {ex.GetType().Name}: {ex.Message}";
        }

        if (problem != null)
        {
            lock (problems)
            {
                problems.Add(problem);
            }
        }
    }

    /// <summary>The test's transactions, each used only on the thread that created it.</summary>
#pragma warning disable TYPHON005 // Each is disposed by the test's finally on its own thread: a transaction is thread-affine.
    private sealed class Cast
    {
        public Transaction Tail, Held, Young;
    }
#pragma warning restore TYPHON005

    /// <summary>A dedicated thread that runs the actions given to it, in order: what a thread-affine transaction needs to be driven from a test.</summary>
    private sealed class Actor : IDisposable
    {
        private readonly BlockingCollection<(Action Action, TaskCompletionSource Done)> _work = new();
        private readonly Thread _thread;

        public Actor(string name)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = name };
            _thread.Start();
        }

        public int ManagedThreadId => _thread.ManagedThreadId;

        public Task Run(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add((action, done));
            return done.Task;
        }

        private void Loop()
        {
            foreach (var (action, done) in _work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                    done.SetResult();
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            }
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(10));
            _work.Dispose();
        }
    }
}
