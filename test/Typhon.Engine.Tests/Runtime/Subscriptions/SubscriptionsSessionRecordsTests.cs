using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Profiler;
using Typhon.Profiler;
using Typhon.Profiler.Events;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// #1038 D-2 — push replication's per-session records (kinds 71-73) tell one session's story in a trace: opened, frames produced, frames skipped and why,
/// degraded, closed with its reason and code.
/// </summary>
/// <remarks>
/// <para>
/// The layouts are pinned without any flag by <c>TypedDtoRoundTripTests</c>; what only this fixture covers is that the engine EMITS them, at the points the
/// story happens. <b><c>[Category("TelemetryGated")]</c></b> for the reason <see cref="SubscriptionsOperatorTelemetryTests"/> gives: the gates are read in a
/// static constructor, so the merge gate runs this in its own process with <c>TYPHON__PROFILER__SUBSCRIPTIONS__ENABLED</c> set. Locally:
/// <code>
/// $env:TYPHON__PROFILER__SUBSCRIPTIONS__ENABLED = 'true'; dotnet test --filter "Category=TelemetryGated"
/// </code>
/// </para>
/// <para>
/// <b>The story is staged, not waited for:</b> a session that never acknowledges. It is served until its K in-flight slots fill, then every tick skips it;
/// the skip run degrades it, the degraded gate refuses its off ticks, and the run reaches the close bound. Every record kind and four of the six skip causes'
/// paths are on that one road, and nothing about it depends on timing.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class SubscriptionsSessionRecordsTests : TestBase<SubscriptionsSessionRecordsTests>
{
    private const string Profile = "god-world";

    [TearDown]
    public void DetachProfilerExporters()
    {
        try { TyphonProfiler.Stop(); } catch { /* the ungated case never starts one */ }
        TyphonProfiler.ResetForTests();
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    private static ProfilerSessionMetadata TraceMetadata() => new(
        systems: [], archetypes: [], componentTypes: [], workerCount: 0, baseTickRate: 10,
        startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(), stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
        startedUtc: DateTime.UtcNow);

    [Test]
    [Category("TelemetryGated")]
    public void ASessionThatNeverAcknowledgesTellsItsWholeStory()
    {
        Assume.That(TelemetryConfig.SubscriptionsSessionChangesActive && TelemetryConfig.SubscriptionsFrameSkippedActive
            && TelemetryConfig.SubscriptionsFrameProducedActive, Is.True,
            "TYPHON__PROFILER__SUBSCRIPTIONS__ENABLED must be set for this process — see the fixture's remarks");

        using var observer = new TraceRingObserver(ResourceRegistry.Profiler, captureRawBytes: true);
        TyphonProfiler.AttachExporter(observer);
        TyphonProfiler.Start(ResourceRegistry.Profiler, TraceMetadata());

        ulong id;
        long closedAt = -1;
        int closeBound;
        string diagnosis;
        try
        {
            using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, nameof(SubscriptionsSessionRecordsTests),
                new SubscriptionsOptions { MaxSessions = 4, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) });
            harness.RunFence = true;
            using (var tx = harness.Engine.CreateQuickTransaction())
            {
                for (var i = 0; i < 32; i++)
                {
                    var b = new ProjBounds { Bounds = new AABB2F { MinX = i * 10f, MinY = 0, MaxX = i * 10f, MaxY = 0 }, Speed = 1f };
                    tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in b), ProjCreature.Ai.Set(new ProjAi { Template = (byte)i }));
                }

                tx.Commit();
            }

            var session = harness.OpenSessions(1, Profile)[0];
            harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
            id = session.Value;
            closeBound = harness.Assembler.CloseBoundTicks;

            // Nothing is ever carried or acknowledged: the session's in-flight slots fill and stay full.
            for (long tick = 1; tick <= 4L * closeBound + 16 && closedAt < 0; tick++)
            {
                // Everything moves every tick, so every tick has something to say: an idle tick is not a skip, and a still world would never fill a slot.
                Move(harness, tick);
                harness.RunTick(tick);
                if (harness.Sessions.Row(session).PendingClose != 0)
                {
                    closedAt = tick;
                }
            }

            diagnosis = $"produced {harness.Assembler.FramesProduced}, skipped {harness.Assembler.FramesSkipped}, "
                + $"degraded {harness.Assembler.SessionsDegraded}, " +
                $"longest run {harness.Assembler.LongestSkipRun} of {closeBound}";

            // The runtime performs a requested close at the next tick's start, which is where the Closed record comes from; the harness leaves it to us.
            harness.Sessions.ApplyPendingCloses();
        }
        finally
        {
            TyphonProfiler.Stop();
        }

        Assert.That(closedAt, Is.GreaterThan(0), $"the session was never closed — the story this case reads has no end ({diagnosis})");

        var produced = new List<SubscriptionsFrameProducedEventDto>();
        var skipped = new List<SubscriptionsFrameSkippedEventDto>();
        var changed = new List<SubscriptionsSessionChangedEventDto>();
        // Filtered on this session's id: the profiler is process-global and a record another fixture left in a ring is delivered here too, so a neighbour
        // can only add rows with other ids, never remove this session's.
        foreach (var (kind, bytes) in observer.GetRecords())
        {
            switch (kind)
            {
                case TraceEventKind.SubscriptionsFrameProduced when SubscriptionsFrameProducedEventDto.Decode(bytes, 0, 1) is { } p && p.SessionId == id:
                    produced.Add(p);
                    break;
                case TraceEventKind.SubscriptionsFrameSkipped when SubscriptionsFrameSkippedEventDto.Decode(bytes, 0, 1) is { } k && k.SessionId == id:
                    skipped.Add(k);
                    break;
                case TraceEventKind.SubscriptionsSessionChanged when SubscriptionsSessionChangedEventDto.Decode(bytes, 0, 1) is { } c && c.SessionId == id:
                    changed.Add(c);
                    break;
            }
        }

        var changes = changed.Select(c => (SessionChange)c.Change).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(changes.First(), Is.EqualTo(SessionChange.Opened), $"the story opens with the session; changes: {string.Join(",", changes)}");
            Assert.That(changes.Last(), Is.EqualTo(SessionChange.Closed), "and ends with its close");
            Assert.That(changes.Count(c => c == SessionChange.Degraded), Is.EqualTo(SkipPolicy.MaxDegradeLevel), "down the whole ladder before the close");
            Assert.That(changed.Where(c => c.Change == (byte)SessionChange.Degraded).Select(c => c.DegradeLevel - 1), Is.EqualTo(new[] { 1, 2 }),
                "each degrade carries the level it reached");

            var close = changed.Last();
            Assert.That(close.CloseReason, Is.EqualTo((byte)SessionCloseReason.Lagging + 1), "closed for lagging, offset by one on the wire");
            Assert.That(close.CloseCode, Is.EqualTo(CloseCodes.TryAgainLater));

            Assert.That(produced, Is.Not.Empty, "frames were produced before the slots filled");
            Assert.That(produced[0].Reset, Is.EqualTo(1), "the first frame is a RESET");
            Assert.That(produced.Select(p => p.Sequence), Is.Ordered.Ascending.And.Unique, "sequences in publication order");
            Assert.That(produced.All(p => p.Bytes > 0), Is.True);
            Assert.That(produced.Count, Is.EqualTo(SessionSendState.K), "served exactly until its K in-flight slots were full");

            Assert.That(skipped.Any(k => k.Cause == (byte)FrameSkipCause.InFlightFull), Is.True, "skipped for in-flight slots");
            Assert.That(skipped.Any(k => k.Cause == (byte)FrameSkipCause.Gated), Is.True, "and, once degraded, by the degrade gate");
            // At least, not exactly: the sweep requests the close at the start of a tick, and the session is still open for that tick's production.
            Assert.That(skipped.Max(k => k.SkipRun), Is.GreaterThanOrEqualTo(closeBound), "the run the close bound was read against reached it");
            Assert.That(skipped.Select(k => k.SkipRun), Is.Ordered.Ascending, "a session that never recovers never resets its run");
            Assert.That(skipped.Where(k => k.Cause == (byte)FrameSkipCause.Gated).All(k => k.DegradeLevel > 0), Is.True,
                "a gated skip happens only to a degraded session");
        });
    }

    private static void Move(FrameHarness harness, long tick)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                var b = cluster.GetReadOnly(ProjCreature.Bounds, slot).Bounds;
                var x = b.MinX + (float)Math.Cos((tick * 0.7) + slot);
                var y = b.MinY + (float)Math.Sin((tick * 0.7) + slot);
                cluster.WriteSpatial(ProjCreature.Bounds, slot,
                    new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f });
            }
        }

        tx.Commit();
    }
}
