using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-12 — the open constraint of <c>design/Subscriptions/foundation/03-subscriptions-track.md § 6</c> item 4, decided by measurement.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question.</b> The track's stages run on pool workers that never enrol in the fence, inside an open EW-01 window, so their
/// <c>ExclusiveWindow.FenceThreadDepth</c> is zero — and <see cref="ExclusiveWindow.NoteMutation"/> throws for exactly that combination. S2a's job is
/// "mark the hit entities watched". If marking reaches a fence-owned structure — a cluster B+Tree, the <c>EntityMap</c>, a per-cell index — the stage must
/// take <c>FenceWindow.EnterWorker()</c> around its chunk, as <c>FencePhaseExecSystemBase</c> does. If it touches only replication's own blocks, it must not:
/// enrolment makes a thread a legal writer of everything the fence owns, and handing that licence to a stage that does not need it is precisely what EW-01
/// exists to withhold.
/// </para>
/// <para>
/// <b>The answer: no enrolment.</b> At eight workers with <c>EnableParallelFence</c> on, a hundred ticks of a populated world with sessions watching it
/// produce zero violations. What the marking touches is the replication block header's watched mask, through one
/// <see cref="Interlocked.Or(ref ulong, ulong)"/> per cluster, and nothing else.
/// </para>
/// <para>
/// <b>And the zero is not vacuous.</b> Two things make it mean something: the pass counts the chunks it executed with the window open, and the fixture's
/// second case shows the detector firing for a non-enrolled thread inside a window it opened itself. Without the first, a stage that never ran would pass;
/// without the second, an engine whose detector had been switched off would.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class InterestEpochScopeTests : TestBase<InterestEpochScopeTests>
{
    /// <summary>The width open item 4 names, and the one that makes several workers reach the same cluster at once.</summary>
    private const int Workers = 8;

    /// <summary>Fast enough that a dozen ticks cost milliseconds; slow enough that the tick is not the thing under measurement.</summary>
    private const int TickRateHz = 200;

    private const int CreatureCount = 420;

    /// <summary>What the observer saw, copied on the driver thread after the track drained.</summary>
    private sealed class Observation
    {
        public long Ticks;
        public long Hits;
        public long ChunksInOpenWindow;
        public int WatchedBlocks;
        public int Violations;
        public string FirstViolationSite;
        public bool Faulted;
    }

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void Populate(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < CreatureCount; i++)
        {
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(10f + (i % 40), 10f + (i / 40))));
        }

        tx.Commit();
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("world", p => p.World().Of<ProjCreature>());
    }

    /// <summary>
    /// Marking an entity watched never reaches <see cref="ExclusiveWindow.NoteMutation"/>, so S2a takes the epoch scope and no fence enrolment.
    /// </summary>
    [Test]
    public void MarkingWatchedInsideTheFenceWindowRaisesNoExclusiveWindowViolation()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = Workers, BaseTickRate = TickRateHz, EnableParallelFence = true });

        Declare(runtime.Subscriptions);

        // Cumulative for the life of the engine otherwise, and this fixture's own spawn ran before the window was ever opened.
        dbe.EpochManager.FenceWindow.ResetCounters();

        Observation captured = null;
        var ticks = 0L;

        runtime.SubscriptionsJournalObserver = ctx =>
        {
            var subscriptions = ctx.Subscriptions;
            var interest = subscriptions?.Interest;
            if (interest == null)
            {
                return;
            }

            // Stands in for the blocks step, which P1-09 owns: the driver thread, after the track has drained and before the next tick dispatches, is the
            // same single-threaded point in the order that stage occupies. Without it no block ever exists and the atomic this test is about never runs.
            for (var w = 0; w < interest.ArenaCount; w++)
            {
                foreach (var packed in interest.Arena(w).NewBlocks)
                {
                    subscriptions.ReplicationStates[HitArena.NewBlockArchetype(packed)].TryAttachBlock(HitArena.NewBlockChunkId(packed), out _);
                }
            }

            var window = dbe.EpochManager.FenceWindow;
            var observation = new Observation
            {
                Ticks = Interlocked.Increment(ref ticks),
                Hits = interest.HitCount,
                ChunksInOpenWindow = interest.ChunksInsideOpenFenceWindow,
                WatchedBlocks = interest.WatchedBlockCount,
                Violations = window.Violations,
                FirstViolationSite = window.FirstViolationSite,
                Faulted = ctx.Faulted,
            };

            // Release store, paired with the test thread's acquire below: the Observation's fields are written before the reference is published.
            Volatile.Write(ref captured, observation);
        };

        runtime.Start();

        var subscriptions = runtime.SubscriptionsContextForTest.Subscriptions;
        var sessions = OpenSessions(subscriptions.Sessions, runtime.Subscriptions.Sessions, count: 6);

        foreach (var session in sessions)
        {
            Assert.That(subscriptions.Sessions.SetProfile(session, "world"), Is.True, "a session that is open should take its profile");
        }

        // Two ticks to create the blocks, then enough more that several workers repeatedly hit the same clusters at once.
        var target = runtime.CurrentTickNumber + 20;
        Assert.That(SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(10)), Is.True, "the runtime did not tick");

        var last = Volatile.Read(ref captured);
        runtime.Shutdown();
        runtime.SubscriptionsJournalObserver = null;

        Assert.That(last, Is.Not.Null, "no tick sealed an observation");

        Assert.Multiple(() =>
        {
            Assert.That(last.Violations, Is.Zero,
                $"marking watched reached a fence-owned structure at {last.FirstViolationSite}; the stage would have to take FenceWindow.EnterWorker()");
            Assert.That(last.Faulted, Is.False, "a stage threw — an ExclusiveWindow violation is a throw at the mutation site");

            // Non-vacuity, first half: the stage really executed, really produced hits, and really did so with the window open.
            Assert.That(last.ChunksInOpenWindow, Is.GreaterThan(0), "no chunk ran inside an open EW-01 window, so the zero above means nothing");
            Assert.That(last.Hits, Is.GreaterThan(0), "the pass produced no hits at all");
            Assert.That(last.WatchedBlocks, Is.GreaterThan(0), "no block was ever marked, so the atomic under test never ran");
        });
    }

    /// <summary>
    /// Non-vacuity, second half: the detector does fire for a thread like the stage's — not enrolled, inside an open window.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed, because "zero violations" and "the detector is off" are the same reading. This opens the window on the test thread,
    /// which enrols it, and then mutates from a thread that was never enrolled — exactly the combination a replication stage would be in if its marking ever
    /// touched an index.
    /// </remarks>
    [Test]
    public void ANonEnrolledThreadMutatingInsideTheWindowIsCaught()
    {
        using var dbe = SetupEngine();
        var window = dbe.EpochManager.FenceWindow;
        window.ResetCounters();

        using (window.Open())
        {
            Assert.That(window.IsOpen, Is.True);

            // A different thread, which has never entered the window: the shape of every Engine-Subscriptions chunk.
            Exception thrown = null;
            Task.Run(() =>
            {
                try
                {
                    window.NoteMutation("InterestEpochScopeTests.foreign");
                }
                catch (Exception e)
                {
                    thrown = e;
                }
            }).Wait(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(thrown, Is.InstanceOf<InvalidOperationException>(), "a foreign thread's mutation inside an open window must throw");
                Assert.That(thrown?.Message, Does.Contain("EW-01"));
                Assert.That(window.Violations, Is.EqualTo(1));
                Assert.That(window.FirstViolationSite, Is.EqualTo("InterestEpochScopeTests.foreign"));
            });
        }

        // And the enrolled thread's own mutation is legal, which is the other half of what the detector distinguishes.
        using (window.Open())
        {
            Assert.DoesNotThrow(() => window.NoteMutation("InterestEpochScopeTests.enrolled"));
            Assert.That(window.ObservedFenceMutation, Is.True);
        }

        Assert.That(window.Violations, Is.EqualTo(1), "an enrolled thread's mutation is not a violation");
    }

    /// <summary>Admits and waits for <paramref name="count"/> sessions to be promoted by a tick.</summary>
    private static SessionId[] OpenSessions(SessionTable table, SubscriptionsSessions declarations, int count)
    {
        var sessions = new SessionId[count];
        for (var i = 0; i < count; i++)
        {
            var request = new AdmissionRequest("god", "token", 0, ReadOnlySpan<byte>.Empty, null, null, null, "fake");
            Assert.That(table.TryAdmit(declarations, request, out sessions[i], out _, out _), Is.True, "the table refused a session");
        }

        // Admitted becomes Open when the tick delivers the Opened event, which the ingress stage does on Engine-Pre.
        foreach (var session in sessions)
        {
            Assert.That(SpinWait.SpinUntil(() => table.IsOpen(session), TimeSpan.FromSeconds(5)), Is.True, $"{session} was never promoted by a tick");
        }

        return sessions;
    }
}
