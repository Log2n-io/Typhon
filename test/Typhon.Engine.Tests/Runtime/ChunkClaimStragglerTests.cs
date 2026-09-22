using System;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// <c>CD-01</c> — a chunk is claimed only from the dispatch it belongs to: a worker preempted anywhere in its claim loop must never run a chunk of a dispatch
/// it was never part of.
/// </summary>
/// <remarks>
/// <para>One test per window the scheduler used to leave open, each deterministic: a probe parks one worker at the exact point, the scheduler moves on to the
/// next dispatch, and the worker is released into it. (1) The ready flag read in tick 1 and a claim counter reset to 0 read in tick 2 — the SWG x64/w16 runs
/// that aborted on <c>CellClusterPool</c>'s single-writer detector when a stale fence chunk ran under the next tick's systems. (2) A claim past the end of
/// one dispatch judged against the next dispatch's larger size — across a tick boundary, and within one tick across the #234 checkerboard re-dispatch.
/// (3) A drainer of a failed dispatch swallowing chunks of the next.</para>
/// <para>All are closed by one claim word per system (the dispatch's size and the next index in one <c>long</c>): a claim can only name a chunk of the
/// dispatch whose word it incremented.</para>
/// <para>Every wait is bounded at a few seconds and every probe is released before shutdown, so a regression fails the test instead of wedging the
/// suite. The events are not disposed: a worker left behind by a failed run may still touch them.</para>
/// </remarks>
[TestFixture]
public class ChunkClaimStragglerTests
{
    private const string StaleChunkMarker = "ran chunks of P in tick 2";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ChunkClaimStraggler" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    [Test]
    [VerifiesRule("CD-01")]
    public void AWorkerCaughtMidScan_DoesNotRunChunksOfASystemTheNextTickHasNotDispatched() => AssertNoStaleChunks(RunStraggler(reopenCounter: false));

    /// <summary>
    /// The same scenario with P's claims reopened for its finished tick-1 dispatch before the straggler is released — what <c>ResetTickState</c> used to
    /// do. The verifier's own assertion must reject it.
    /// </summary>
    [Test]
    [RuleMutant("CD-01")]
    public void ACounterOpenAcrossTheTickBoundary_IsCaughtByTheVerifier() =>
        RuleMutants.AssertDetects("CD-01", StaleChunkMarker, () => AssertNoStaleChunks(RunStraggler(reopenCounter: true)));

    // Plain asserts, not Assert.Multiple: the mutant runs this through RuleMutants.AssertDetects, which recognises the verifier by its message.
    private static void AssertNoStaleChunks((bool Caught, bool ReachedTickThree, int StaleChunks) outcome)
    {
        Assert.That(outcome.Caught, Is.True, "precondition: no idle worker was caught between P's ready flag and its claim counter in tick 1");
        Assert.That(outcome.ReachedTickThree, Is.True, "precondition: the scheduler did not get past tick 2");
        Assert.That(outcome.StaleChunks, Is.Zero, $"a worker caught in tick 1 {StaleChunkMarker}, before tick 2 had dispatched P");
    }

    private (bool Caught, bool ReachedTickThree, int StaleChunks) RunStraggler(bool reopenCounter)
    {
        const int workers = 4;
        var gateTicks = 0;
        var tickTwoHeld = 0;
        var armed = 0;
        var parallelIdx = -1;
        var stragglerThread = 0;
        var stragglerRescanned = 0;
        var chunksWhileUndispatched = 0;
        var caught = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        DagScheduler scheduler = null;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = workers, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Straggler");
        dag.CallbackSystem("Gate", _ =>
        {
            if (Interlocked.Increment(ref gateTicks) != 2)
            {
                return;
            }

            // Tick 2. P waits on this system, so it has not been dispatched: a chunk of P that runs now was claimed from a counter nobody published.
            if (reopenCounter)
            {
                var claims = (CacheLinePaddedLong[])typeof(DagScheduler)
                    .GetField("_claims", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(scheduler);
                Volatile.Write(ref claims[parallelIdx].Value, (long)workers << 32);
            }

            Volatile.Write(ref tickTwoHeld, 1);
            release.Set();
            SpinWait.SpinUntil(() => Volatile.Read(ref chunksWhileUndispatched) > 0 || Volatile.Read(ref stragglerRescanned) != 0, Wait);
            Volatile.Write(ref tickTwoHeld, 0);
        });
        dag.QuerySystem("P", _ => { }, after: "Gate", input: () => null, parallel: true);
        dag.CallbackSystem("Tail", _ =>
        {
            if (Volatile.Read(ref gateTicks) != 1)
            {
                return;
            }

            // Tick 1, P complete: hold the tick open until an idle worker has been caught between P's ready flag and its claim counter.
            Volatile.Write(ref armed, 1);
            caught.Wait(Wait);
        }, after: "P");

        using (scheduler = dag.Build(_registry.Runtime))
        {
            scheduler.ParallelQueryPrepareCallback = _ => workers;
            scheduler.ParallelQueryChunkCallback = (_, _, _, _) =>
            {
                if (Volatile.Read(ref tickTwoHeld) == 1)
                {
                    Interlocked.Increment(ref chunksWhileUndispatched);
                }
            };
            scheduler.ParallelQueryCleanupCallback = _ => false;
            scheduler.FindReadySystemProbe = sysIdx =>
            {
                var me = Environment.CurrentManagedThreadId;
                if (sysIdx < 0)
                {
                    // The caught worker has begun a NEW scan, so the one it was caught in ended without claiming anything.
                    if (release.IsSet && Volatile.Read(ref stragglerThread) == me)
                    {
                        Volatile.Write(ref stragglerRescanned, 1);
                    }

                    return;
                }

                // P is the only multi-chunk system, so a non-negative index is P with its ready flag just read as set.
                if (Volatile.Read(ref armed) == 1 && Interlocked.CompareExchange(ref stragglerThread, me, 0) == 0)
                {
                    parallelIdx = sysIdx;
                    caught.Set();
                    release.Wait(Wait);
                }
            };

            bool wasCaught, reachedTickThree;
            try
            {
                scheduler.Start();
                reachedTickThree = SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, Wait);
                wasCaught = caught.IsSet;
            }
            finally
            {
                release.Set();
                caught.Set();
                scheduler.Shutdown();
            }

            return (wasCaught, reachedTickThree, Volatile.Read(ref chunksWhileUndispatched));
        }
    }

    /// <summary>
    /// A claim that came back past the end of one dispatch must not be judged against the NEXT dispatch's size. P runs 4 chunks in tick 1 and 8 in tick 2;
    /// a worker parked between its tick-1 claim (index 4 or more) and the size check is released once tick 2's P is live, and tick 2 must still run each of
    /// its 8 chunks exactly once.
    /// </summary>
    [Test]
    [VerifiesRule("CD-01")]
    public void AClaimPastTheEndOfOneDispatch_DoesNotRunAChunkOfTheNext()
    {
        const int workers = 4;
        var tick = 0;
        var tickOneStarted = 0;
        var armed = 0;
        var parkedThread = 0;
        var stragglerMoved = 0;
        var tickTwoRuns = new int[8];
        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = workers, BaseTickRate = 1000 }).PublicTrack.DeclareDag("LateSizeCheck");
        dag.CallbackSystem("Gate", _ => Interlocked.Increment(ref tick));
        dag.QuerySystem("P", _ => { }, after: "Gate", input: () => null, parallel: true);
        dag.CallbackSystem("Tail", _ =>
        {
            // Tick 1 ends only once a worker is parked past P's last chunk.
            if (Volatile.Read(ref tick) == 1)
            {
                parked.Wait(Wait);
            }
        }, after: "P");

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.ParallelQueryPrepareCallback = _ => Volatile.Read(ref tick) == 1 ? 4 : 8;
        scheduler.ParallelQueryChunkCallback = (_, chunk, _, _) =>
        {
            if (Volatile.Read(ref tick) == 1)
            {
                // Each chunk waits for all four to be claimed, so every claim after them — the ones past the end — is made armed.
                if (Interlocked.Increment(ref tickOneStarted) == 4)
                {
                    Volatile.Write(ref armed, 1);
                }

                SpinWait.SpinUntil(() => Volatile.Read(ref armed) == 1, Wait);
                return;
            }

            if (Volatile.Read(ref tick) != 2)
            {
                return;
            }

            Interlocked.Increment(ref tickTwoRuns[chunk]);
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref parkedThread))
            {
                Volatile.Write(ref stragglerMoved, 1);
            }

            if (!release.IsSet)
            {
                release.Set();
                SpinWait.SpinUntil(() => Volatile.Read(ref stragglerMoved) != 0, Wait);
            }
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;
        scheduler.ClaimProbe = (_, point) =>
        {
            if (point == 1 && Volatile.Read(ref armed) == 1 && Interlocked.CompareExchange(ref parkedThread, Environment.CurrentManagedThreadId, 0) == 0)
            {
                parked.Set();
                release.Wait(Wait);
            }
        };
        scheduler.FindReadySystemProbe = sysIdx =>
        {
            if (sysIdx < 0 && release.IsSet && Environment.CurrentManagedThreadId == Volatile.Read(ref parkedThread))
            {
                Volatile.Write(ref stragglerMoved, 1);
            }
        };

        bool wasParked, reachedTickThree;
        try
        {
            scheduler.Start();
            reachedTickThree = SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, Wait);
            wasParked = parked.IsSet;
        }
        finally
        {
            release.Set();
            parked.Set();
            scheduler.Shutdown();
        }

        Assert.That(wasParked, Is.True, "precondition: no worker was parked past the end of P's tick-1 claims");
        Assert.That(reachedTickThree, Is.True, "precondition: the scheduler did not get past tick 2");
        Assert.That(tickTwoRuns, Is.All.EqualTo(1), $"tick 2 must run each of P's 8 chunks exactly once; ran {string.Join(", ", tickTwoRuns)}");
    }

    /// <summary>
    /// The same window within ONE tick: the #234 checkerboard re-dispatch. P's phase A runs 4 chunks and its cleanup asks for a phase B of 8; a worker parked
    /// between a claim past phase A's end and the size check is released once phase B is live, and phase B must run each of its 8 chunks exactly once.
    /// Nothing closes P's claims between the two dispatches — phase A's word is exhausted when phase B is prepared — and this is what shows that suffices.
    /// </summary>
    [Test]
    [VerifiesRule("CD-01")]
    public void AClaimPastTheEndOfPhaseA_DoesNotRunAChunkOfPhaseB()
    {
        const int workers = 4;
        var tick = 0;
        var phase = 0;
        var phaseAStarted = 0;
        var armed = 0;
        var parkedThread = 0;
        var stragglerMoved = 0;
        var phaseBRuns = new int[8];
        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = workers, BaseTickRate = 1000 }).PublicTrack.DeclareDag("PhaseStraggler");
        dag.CallbackSystem("Gate", _ => Interlocked.Increment(ref tick));
        dag.QuerySystem("P", _ => { }, after: "Gate", input: () => null, parallel: true);

        using var scheduler = dag.Build(_registry.Runtime);

        // Each tick: phase A (4 chunks), whose cleanup asks for phase B (8 chunks), whose cleanup ends the system.
        scheduler.ParallelQueryPrepareCallback = _ => Interlocked.Increment(ref phase) % 2 == 1 ? 4 : 8;
        scheduler.ParallelQueryCleanupCallback = _ => Volatile.Read(ref phase) % 2 == 1;
        scheduler.ParallelQueryChunkCallback = (_, chunk, _, _) =>
        {
            if (Volatile.Read(ref tick) != 1)
            {
                return;
            }

            if (Volatile.Read(ref phase) == 1)
            {
                // Phase A: every chunk waits for all four to be claimed, so each claim after them is past the end and made armed. Chunk 0 also waits for
                // the straggler to park, so phase A cannot complete — and phase B cannot open — before it has claimed.
                if (Interlocked.Increment(ref phaseAStarted) == 4)
                {
                    Volatile.Write(ref armed, 1);
                }

                SpinWait.SpinUntil(() => Volatile.Read(ref armed) == 1, Wait);
                if (chunk == 0)
                {
                    parked.Wait(Wait);
                }

                return;
            }

            // Phase B.
            Interlocked.Increment(ref phaseBRuns[chunk]);
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref parkedThread))
            {
                Volatile.Write(ref stragglerMoved, 1);
            }

            if (!release.IsSet)
            {
                release.Set();
                SpinWait.SpinUntil(() => Volatile.Read(ref stragglerMoved) != 0, Wait);
            }
        };
        scheduler.ClaimProbe = (_, point) =>
        {
            if (point == 1 && Volatile.Read(ref armed) == 1 && Volatile.Read(ref phase) == 1
                && Interlocked.CompareExchange(ref parkedThread, Environment.CurrentManagedThreadId, 0) == 0)
            {
                parked.Set();
                release.Wait(Wait);
            }
        };
        scheduler.FindReadySystemProbe = sysIdx =>
        {
            if (sysIdx < 0 && release.IsSet && Environment.CurrentManagedThreadId == Volatile.Read(ref parkedThread))
            {
                Volatile.Write(ref stragglerMoved, 1);
            }
        };

        bool wasParked, reachedTickThree;
        try
        {
            scheduler.Start();
            reachedTickThree = SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, Wait);
            wasParked = parked.IsSet;
        }
        finally
        {
            release.Set();
            parked.Set();
            scheduler.Shutdown();
        }

        Assert.That(wasParked, Is.True, "precondition: no worker was parked past the end of P's phase-A claims");
        Assert.That(reachedTickThree, Is.True, "precondition: the scheduler did not get past tick 2");
        Assert.That(phaseBRuns, Is.All.EqualTo(1), $"phase B must run each of its 8 chunks exactly once; ran {string.Join(", ", phaseBRuns)}");
    }

    /// <summary>
    /// A worker draining a failed system must not carry the failure into the next tick. P throws in tick 1; the worker that threw is parked before its next
    /// claim and released once tick 2's P is live. Tick 2 must run every one of P's chunks.
    /// </summary>
    [Test]
    [VerifiesRule("CD-01")]
    public void ADrainerParkedInAFailedTick_DoesNotSwallowChunksOfTheNext()
    {
        const int workers = 4;
        const int chunks = 16;
        var tick = 0;
        var throwerThread = 0;
        var parkedThread = 0;
        var stragglerClaimed = 0;
        var tickTwoRuns = new int[chunks];
        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = workers, BaseTickRate = 1000 }).PublicTrack.DeclareDag("DrainStraggler");
        dag.CallbackSystem("Gate", _ => Interlocked.Increment(ref tick));
        dag.QuerySystem("P", _ => { }, after: "Gate", input: () => null, parallel: true);

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.ParallelQueryPrepareCallback = _ => chunks;
        scheduler.ParallelQueryChunkCallback = (_, chunk, _, _) =>
        {
            var me = Environment.CurrentManagedThreadId;
            if (Volatile.Read(ref tick) == 1)
            {
                if (chunk == 0)
                {
                    Volatile.Write(ref throwerThread, me);
                    throw new InvalidOperationException("P fails in tick 1 on purpose");
                }

                // The other chunks finish only once the thrower is parked, so its decrement is not the last and it goes on to drain. (Waiting for the throw
                // alone is not enough: unwinding is slow, and the rest of the chunks can run before the failure flag is set.)
                parked.Wait(Wait);
                return;
            }

            if (Volatile.Read(ref tick) != 2)
            {
                return;
            }

            // Tick 2: every chunk waits until the parked worker has made its claim, so it claims while chunks are still left to claim.
            release.Set();
            SpinWait.SpinUntil(() => Volatile.Read(ref stragglerClaimed) != 0, Wait);
            Interlocked.Increment(ref tickTwoRuns[chunk]);
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;
        scheduler.ClaimProbe = (_, point) =>
        {
            var me = Environment.CurrentManagedThreadId;
            if (point == 0 && me == Volatile.Read(ref throwerThread) && Interlocked.CompareExchange(ref parkedThread, me, 0) == 0)
            {
                parked.Set();
                release.Wait(Wait);
            }
            else if (point == 1 && release.IsSet && me == Volatile.Read(ref parkedThread))
            {
                Volatile.Write(ref stragglerClaimed, 1);
            }
        };

        bool wasParked, reachedTickThree;
        try
        {
            scheduler.Start();
            reachedTickThree = SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, Wait);
            wasParked = parked.IsSet;
        }
        finally
        {
            release.Set();
            parked.Set();
            scheduler.Shutdown();
        }

        Assert.That(wasParked, Is.True, "precondition: the worker that threw was never parked before a claim");
        Assert.That(reachedTickThree, Is.True, "precondition: the scheduler did not get past tick 2");
        Assert.That(tickTwoRuns, Is.All.EqualTo(1), $"tick 2 must run each of P's {chunks} chunks exactly once; ran {string.Join(", ", tickTwoRuns)}");
    }
}
