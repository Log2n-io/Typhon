using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-15: sessions that would encode the same bytes encode them once and copy.
/// </summary>
/// <remarks>
/// <para>
/// This is AC-1's argument made assertable. [02 § 7](../../../../claude/design/Subscriptions/02-execution.md) claims 110 identical <c>World</c> sessions
/// cost one encode and a copy each rather than 110 encodes, and until now that was a claim about an optimisation nobody had built. The counters make it a
/// number: a run either encodes once or it does not, and no timing has to be interpreted to find out.
/// </para>
/// <para>
/// <b>What is deliberately NOT asserted: a single shared block.</b> The design sketched a refcounted frame block released when the last send completes.
/// This builds the cheaper half — one encode, N copies into N pool blocks — which is the form [09 § 7 R7](../../../../claude/design/Subscriptions/09-phase1-build-plan.md)
/// names as the criterion's fallback and which avoids giving one piece of pool memory N owners on a path whose whole contract is that the producer owns it
/// alone. The saved memcpy is available later if a measurement ever shows it mattering.
/// </para>
/// <para>
/// <b>The correctness question is not "did it copy" but "was copying right".</b> Every negative case below exists because reusing bytes for a session that
/// should have had its own is silent: the client decodes a valid frame describing somebody else's view, and nothing anywhere reports an error. So each test
/// that expects no sharing asserts the encode count, and the positive case asserts the bytes as well as the counter.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class SharedFrameTests : TestBase<SharedFrameTests>
{
    private const string Profile = "god-world";
    private const string OtherProfile = "god-world-again";

    /// <summary>Identical sessions cost one encode and a copy each, and every one of them receives the same bytes.</summary>
    /// <remarks>
    /// The byte comparison is the half that matters. A counter saying "one encode, nine copies" is satisfied just as well by a bug that hands nine sessions
    /// the wrong frame, so the frames are compared to each other as well as counted.
    /// </remarks>
    [Test]
    public void IdenticalSessionsEncodeOnceAndCopy()
    {
        using var harness = Create();
        SpawnCreatures(harness, 12);
        var sessions = harness.OpenSessions(10, Profile);

        harness.PrimeBlocks();
        Settle(harness, sessions, fromTick: 2, ticks: 3);

        // A tick that changes something, so the frame is not empty and the encode is real work.
        DamageAll(harness, seed: 300);

        var before = (harness.Assembler.FramesEncoded, harness.Assembler.FramesCopied);
        harness.RunTick(6);
        var encoded = harness.Assembler.FramesEncoded - before.FramesEncoded;
        var copied = harness.Assembler.FramesCopied - before.FramesCopied;

        var frames = new List<byte[]>();
        foreach (var session in sessions)
        {
            frames.AddRange(harness.Collect(session));
        }

        Assert.Multiple(() =>
        {
            Assert.That(frames, Has.Count.EqualTo(sessions.Length), "every session should have produced exactly one frame");
            Assert.That(encoded, Is.EqualTo(1), $"ten identical sessions encoded {encoded} times; 02 § 7's whole claim is that they encode once");
            Assert.That(copied, Is.EqualTo(sessions.Length - 1), "every session after the first should have copied");
        });

        for (var i = 1; i < frames.Count; i++)
        {
            Assert.That(frames[i], Is.EqualTo(frames[0]).AsCollection, $"session {i} was handed bytes that differ from session 0's, so the reuse was wrong");
        }
    }

    /// <summary>A session still filling its view encodes its own frame, because its records are its own.</summary>
    /// <remarks>
    /// The case the enter budget creates: a session that joined late is working through its enters, so its frame carries a different record set from a
    /// session that finished long ago — and its <c>VIEW_COMPLETE</c> flag differs too. Sharing here would tell a half-filled client its view was complete.
    /// </remarks>
    [Test]
    public void ASessionStillFillingItsViewGetsItsOwnFrame()
    {
        using var harness = Create(enterBudget: 4);
        SpawnCreatures(harness, 24);
        var settled = harness.OpenSessions(2, Profile);

        harness.PrimeBlocks();
        Settle(harness, settled, fromTick: 2, ticks: 6);

        // A third session opens now and starts from nothing, with a budget too small to finish in one frame.
        var joining = harness.OpenSessions(1, Profile)[0];
        DamageAll(harness, seed: 400);

        var before = harness.Assembler.FramesEncoded;
        harness.RunTick(9);
        var encoded = harness.Assembler.FramesEncoded - before;

        var joiner = harness.Read(joining);
        Assert.Multiple(() =>
        {
            Assert.That(encoded, Is.GreaterThanOrEqualTo(2), "the joining session must encode its own frame, so this tick cannot be a single encode");
            Assert.That(joiner, Is.Not.Null, "the joining session received no frame at all");
            Assert.That(joiner.Flags.HasFlag(TickFlags.ViewComplete), Is.False, "the joining session's view cannot be complete under a budget of four");
        });
    }

    /// <summary>Two profiles never share, even when they select the same entities.</summary>
    /// <remarks>
    /// The two profiles here are identical in what they select, so the frames would in fact be byte-identical — and they still must not be shared, because
    /// the rule is a construction argument about the profile, not a comparison of its output. A future profile that differed would otherwise be shared on
    /// the strength of having once produced matching bytes.
    /// </remarks>
    [Test]
    public void DifferentProfilesDoNotShare()
    {
        using var harness = Create();
        SpawnCreatures(harness, 12);
        var first = harness.OpenSessions(2, Profile);
        var second = harness.OpenSessions(2, OtherProfile);

        harness.PrimeBlocks();
        Settle(harness, [.. first, .. second], fromTick: 2, ticks: 3);
        DamageAll(harness, seed: 500);

        var before = harness.Assembler.FramesEncoded;
        harness.RunTick(6);
        var encoded = harness.Assembler.FramesEncoded - before;

        Assert.That(encoded, Is.EqualTo(2), $"two profiles should cost two encodes, not {encoded} — sharing across profiles is a construction error");
    }

    /// <summary>A session that fell behind has a different baseline, so it carries a different record set and encodes its own.</summary>
    /// <remarks>
    /// The union frame of SUB-03: a session skipped for several ticks receives everything since ITS baseline, which is strictly more than a session that was
    /// drained every tick receives. Sharing the two would silently drop the records the skipped session missed — the exact permanent divergence SUB-03 exists
    /// to prevent, arrived at through an optimisation rather than through a baseline bug.
    /// </remarks>
    [Test]
    public void ASessionAtADifferentBaselineDoesNotShare()
    {
        using var harness = Create();
        SpawnCreatures(harness, 12);
        var sessions = harness.OpenSessions(2, Profile);
        var drained = sessions[0];
        var behind = sessions[1];

        harness.PrimeBlocks();
        Settle(harness, sessions, fromTick: 2, ticks: 3);

        // From here only one of them is drained, so the other's slots fill and its baseline stops advancing.
        for (var tick = 5L; tick <= 8; tick++)
        {
            DamageAll(harness, seed: (int)(600 + tick));
            harness.RunTick(tick);
            harness.Deliver(drained);
        }

        Assert.That(harness.Assembler.FramesSkipped, Is.GreaterThan(0), "the scenario has to actually skip the session it is about");

        // Let the behind session drain, then run a tick both will produce for — at different baselines.
        harness.Deliver(behind);
        DamageAll(harness, seed: 700);

        var before = harness.Assembler.FramesEncoded;
        harness.RunTick(9);
        var encoded = harness.Assembler.FramesEncoded - before;

        Assert.That(encoded, Is.EqualTo(2), $"a session at a different baseline must encode its own union frame; this tick encoded {encoded} time(s)");
    }

    /// <summary>Nothing is reused across ticks, however identical two ticks look.</summary>
    /// <remarks>
    /// The frame header carries the tick number, so a frame reused from the previous tick would tell a client the wrong time. The tick is part of the share
    /// key and the cache is dropped at the top of every tick; this asserts the outcome of both rather than either mechanism.
    /// </remarks>
    [Test]
    public void NothingIsSharedAcrossTicks()
    {
        using var harness = Create();
        SpawnCreatures(harness, 8);
        var sessions = harness.OpenSessions(3, Profile);

        harness.PrimeBlocks();
        Settle(harness, sessions, fromTick: 2, ticks: 3);

        var encodes = new List<long>();
        for (var tick = 5L; tick <= 7; tick++)
        {
            DamageAll(harness, seed: (int)(800 + tick));
            var before = harness.Assembler.FramesEncoded;
            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }

            encodes.Add(harness.Assembler.FramesEncoded - before);
        }

        Assert.That(encodes, Is.All.EqualTo(1), "each tick must pay its own encode; a zero would mean a frame was reused from the previous tick");
    }

    /// <summary>
    /// AC-1's shape: a hundred and ten sessions on one profile, joined at different ticks, still encode once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AC-1's 0.3 ms target is only arithmetically reachable if 110 sessions cost one encode and 109 copies; at 110 encodes it is asking the engine to run
    /// the codecs over the whole world a hundred and ten times in a third of a millisecond. So the first question to ask of a 30× miss is not "where is the
    /// time" but "how many encodes was it", and the second is whether joining at different moments — which is what a real population does — is enough to
    /// break the sharing.
    /// </para>
    /// <para>
    /// The sessions here join on staggered ticks deliberately. Ten sessions opened in one breath is the easy case and the fixture above already covers it.
    /// </para>
    /// </remarks>
    [TestCase(0, TestName = "ManySessionsEncodeOnce_JoinedTogether")]
    [TestCase(8, TestName = "ManySessionsEncodeOnce_JoinedInCohortsOfEight")]
    public void ManySessionsJoiningAtDifferentTimesStillEncodeOnce(int perCohort)
    {
        const int Sessions = 110;

        using var harness = Create();
        SpawnCreatures(harness, 12);

        var sessions = new List<SessionId>(Sessions);
        harness.PrimeBlocks();

        var tick = 2L;
        for (var i = 0; i < Sessions; i++)
        {
            sessions.AddRange(harness.OpenSessions(1, Profile));

            // Every few joins, a tick: the population arrives spread over time the way a connecting fleet does.
            if (perCohort > 0 && i % perCohort == perCohort - 1)
            {
                Settle(harness, sessions, tick, 1);
                tick++;
            }
        }

        // Everyone finishes filling and reaches the same baseline before the measurement.
        Settle(harness, sessions, tick, 6);
        tick += 6;

        // Four consecutive productive ticks, because the question is not only how many encodes the first one costs but whether the population RE-CONVERGES.
        // A transient split after a join is cheap; one that never heals is what turns AC-1's budget into a 30× miss.
        var perTick = new List<long>();
        long encoded = 0;
        long copied = 0;
        for (var i = 0; i < 4; i++)
        {
            DamageAll(harness, seed: 900 + i);
            var before = (harness.Assembler.FramesEncoded, harness.Assembler.FramesCopied);
            harness.RunTick(tick + i);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }

            encoded = harness.Assembler.FramesEncoded - before.FramesEncoded;
            copied = harness.Assembler.FramesCopied - before.FramesCopied;
            perTick.Add(encoded);
        }

        TestContext.Out.WriteLine($"encodes per tick: {string.Join(", ", perTick)}");

        TestContext.Out.WriteLine($"{Sessions} sessions, cohorts of {(perCohort == 0 ? Sessions : perCohort)}: {encoded} encodes, {copied} copies");

        Assert.That(encoded, Is.EqualTo(1),
            $"{Sessions} sessions on one profile cost {encoded} encodes. AC-1's 0.3 ms budget assumes one, so this is where a 30× miss would come from.");
        Assert.That(copied, Is.EqualTo(Sessions - 1), "every session after the first should have copied");
    }

    /// <summary>
    /// How often the changed-only gather is actually taken, which is what decides whether it is worth anything.
    /// </summary>
    /// <remarks>
    /// The fast path requires <c>Baseline == tick − 1</c>, and a session's baseline advances only on a tick it was PRODUCED for. A session with nothing to
    /// say has its frame abandoned and its baseline left behind, so every silent tick costs the session its fast path on the tick after. A world quiet
    /// enough to skip frames is therefore the world where the optimisation stops applying — which is the opposite of what one would assume, and the reason
    /// this is measured rather than reasoned about.
    /// </remarks>
    [Test]
    public void TheChangedOnlyGatherIsTakenInSteadyState()
    {
        using var harness = Create();
        SpawnCreatures(harness, 40);
        var sessions = harness.OpenSessions(8, Profile);

        harness.PrimeBlocks();
        Settle(harness, sessions, fromTick: 2, ticks: 4);

        var before = (harness.Assembler.ChangedOnlyGathers, harness.Assembler.FullGathers, harness.Assembler.UnprovenGathers);
        for (var tick = 6L; tick <= 25; tick++)
        {
            DamageAll(harness, seed: (int)(1000 + tick));
            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }

        var fast = harness.Assembler.ChangedOnlyGathers - before.ChangedOnlyGathers;
        var full = harness.Assembler.FullGathers - before.FullGathers;
        var unproven = harness.Assembler.UnprovenGathers - before.UnprovenGathers;
        TestContext.Out.WriteLine($"changed-only {fast}, full {full}, unproven {unproven}");

        Assert.Multiple(() =>
        {
            Assert.That(fast, Is.GreaterThan(full), $"the fast path was taken {fast} times against {full} full walks; in a steady state where every session "
                + "is produced for every tick it should be the common case, and if it is not the baseline is not advancing as assumed");
            Assert.That(unproven, Is.Zero, "a fast gather could not prove nothing had left and was redone — that is a double walk, and in a steady state "
                + "with no churn it should never happen");
        });
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
        subs.Profile(OtherProfile, p => p.World().Of<ProjCreature>());
    }

    private FrameHarness Create(int enterBudget = 500) => FrameHarness.Create(
        ProjectionTestSchema.SetupEngine(ServiceProvider),
        Declare,
        nameof(SharedFrameTests),
        new SubscriptionsOptions
        {
            MaxSessions = 256,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
            EnterBudgetPerFrame = enterBudget,
        });

    /// <summary>Runs ticks and drains every session, so they all reach a complete view at the same baseline.</summary>
    private static void Settle(FrameHarness harness, IReadOnlyList<SessionId> sessions, long fromTick, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            harness.RunTick(fromTick + i);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }
    }

    private static void SpawnCreatures(FrameHarness harness, int count)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = new ProjBounds
            {
                Bounds = new AABB2F { MinX = i * 6f, MinY = 10f, MaxX = (i * 6f) + 1f, MaxY = 11f },
                Speed = 1f,
            };

            var ai = new ProjAi { Template = (byte)(i + 1), Level = (ushort)(100 + i), Mode = ProjAiMode.Idle };
            var vitals = new ProjVitals { Health = 5, MaxHealth = 10 };
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
        }

        tx.Commit();
    }

    /// <summary>Changes the vitals group of every live creature, so the next tick has records to carry.</summary>
    private static void DamageAll(FrameHarness harness, int seed)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
                var ai = cluster.GetSpan(ProjCreature.Ai);
#pragma warning restore TYPHON009
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    ai[slot].Level = (ushort)(seed + slot);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }
}
