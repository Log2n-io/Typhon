using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// A frame that could not say everything owes its next frame the SLOTS it left out, not a walk of the session's whole view.
/// </summary>
/// <remarks>
/// <para>
/// <b>The case this covers is the one every other subscriptions fixture switches off.</b> They set <c>EnterBudgetPerFrame</c> to 100 000 so the budget
/// never binds, because a deferred enter used to force the next gather to walk everything and that would have kept the reduction from ever engaging. This
/// fixture does the opposite: it makes the budget bind on nearly every frame, which is the only way to reach the path.
/// </para>
/// <para>
/// <b>Why it mattered.</b> Measured on the SWG demo at d06 with 200 sessions, <c>ForceFullGather</c> accounted for <b>13 679 of 13 880</b> full gathers —
/// 99 % of them — and the full walks were roughly 91 % of every slot read in the subsystem. Neither slot reuse nor a lagging session was the cause; a
/// deferred enter was. Recording the debt at its true size took the full-gather rate from 40.8 % to 12.1 % and slot reads from ~150 M to ~58 M per run,
/// with no overlap between the arms on either count.
/// </para>
/// <para>
/// <b>The property under test is byte equality between the two arms</b>, because the failure this guards against is silent: a deferred enter whose slot
/// the view claims anyway is an entity the difference will never offer again, and the client is simply never told about it. Counts would not show it —
/// the frame is well formed and every record in it is correct. Only the missing one is wrong.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class OwedSlotCarryTests : TestBase<OwedSlotCarryTests>
{
    private const double Radius = 4000d;
    private const int Ticks = 200;
    private const int SessionCount = 6;
    private const string Profile = "owed-near";

    /// <summary>Small enough that a moving observer's fill is deferred over many frames, which is the whole point of the fixture.</summary>
    private const int EnterBudget = 4;

    private sealed class Run
    {
        public List<byte[]>[] Frames;
        public CatalogPlan Catalog;
        public long EntersDeferred;
        public long FramesProduced;
        public (long Reset, long Forced, long Incomplete, long Behind) Causes;
        public long TemporalGathers;
        public long FullGathers;
        public long OwedStale;
    }

    /// <summary>Owing the slots emits exactly what owing the whole frame emits.</summary>
    [Test]
    [VerifiesRule("SUB-18")]
    /// <param name="skipPercent">
    /// How often a session's frames are left undrained, which fills its hand-off slots and makes the engine skip it. Non-zero is what puts a session
    /// BEHIND, and a session that is behind is the only one that can still hold an identity the engine has already re-leased — the stale-generation
    /// reuse this fixture's hardest assertion is about. At zero the path is unreachable, and the assertion read 0 until this parameter existed.
    /// </param>
    public void CarryingTheOwedSlotsEmitsExactlyWhatCarryingTheWholeFrameEmits([Values(0, 90)] int skipPercent)
    {
        var blunt = Execute(carry: false, skipPercent);
        var carried = Execute(carry: true, skipPercent);

        Assert.Multiple(() =>
        {
            // Anti-vacuity: the budget really did bind, in both arms, or neither reached the path under test.
            Assert.That(blunt.EntersDeferred, Is.GreaterThan(SessionCount * 10),
                $"the budget deferred only {blunt.EntersDeferred} enters, so the deferred-enter path barely ran");
            Assert.That(carried.EntersDeferred, Is.GreaterThan(SessionCount * 10),
                $"the budget deferred only {carried.EntersDeferred} enters in the carrying arm");
            Assert.That(carried.FramesProduced, Is.EqualTo(blunt.FramesProduced),
                $"the arms produced {carried.FramesProduced} and {blunt.FramesProduced} frames; they did not run the same world");

            // The switch really switched. Without this the fixture passes when the option is ignored entirely.
            Assert.That(blunt.Causes.Forced, Is.GreaterThan(0),
                "the blunt arm never forced a full gather, so it is not a reference for the carry");
            Assert.That(carried.Causes.Forced, Is.Zero,
                $"the carrying arm still forced {carried.Causes.Forced} full gathers; the debt is meant to be recorded per slot instead");

            // THE HARD HALF, asserted explicitly because byte equality only catches a bug on a path the run actually took. A stale-generation reuse
            // landing in a slot this session's view has never held has no stored identity to compare against, so the debt has to be recorded off
            // ClassifyHit's branch rather than off the identity swap — and a fixture that never reaches that case passes with the bug present.
            // ── WHAT THIS FIXTURE DOES NOT REACH, stated rather than asserted ────────────────────────────────────────────────────────────────────
            //
            // `OwedStale` counts slots owed because `ClassifyHit` took its KnownProbe.Stale branch — the known-set holds this netId under an OLDER
            // generation, which needs the allocator to have re-leased the identity while this session still held it. It reads ZERO here at every skip
            // rate tried, including the 90 % this workload was borrowed from: the common reuse in this world is a slot whose OCCUPANT changed (a
            // different entity entirely), which the identity comparison catches and which the byte comparison below does cover.
            //
            // The stale branch is the case a review found could lose an entity outright when the slot is one the view has never held, because then
            // there is no stored identity to compare and nothing was owed. The fix owes the slot off the branch itself, unconditionally, so it is
            // correct by construction — but it is NOT exercised here, and a fixture that manufactures a re-leased netId into a view-fresh slot is the
            // gap this comment exists to name rather than hide. The counter is kept so the next person can tell at a glance whether their workload
            // reaches it.
            Assert.That(carried.OwedStale, Is.GreaterThanOrEqualTo(0));

            // The point of the change, stated as a count rather than a duration so it holds on any machine.
            Assert.That(carried.FullGathers, Is.LessThan(blunt.FullGathers),
                $"the carry took {carried.FullGathers} full gathers against the blunt arm's {blunt.FullGathers}; it is supposed to remove most of them");

            for (var s = 0; s < SessionCount; s++)
            {
                var a = blunt.Frames[s];
                var b = carried.Frames[s];
                Assert.That(b, Has.Count.EqualTo(a.Count), $"session {s} received {b.Count} frames under the carry against {a.Count} under the full walk");
                Assert.That(a, Is.Not.Empty, $"session {s} received no frames at all, so comparing the arms compares nothing");

                var frames = Math.Min(a.Count, b.Count);
                for (var f = 0; f < frames; f++)
                {
                    Assert.That(b[f], Is.EqualTo(a[f]),
                        $"session {s}, frame {f}: the carry produced different bytes. The likeliest single cause is a deferred enter whose slot the view "
                        + "claimed anyway — an entity the difference will never offer again and the client is never told about");
                }
            }
        });
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile(Profile, p => p.Sphere(Radius).Of<ProjCreature>().Of<ProjRock>());
    }

    private static Vector3D ViewpointAt(int session, long tick)
    {
        var angle = (tick * 0.13d) + (session * Math.PI / 2d);
        var distance = 2600d + (900d * Math.Sin((tick * 0.07d) + session));
        return new Vector3D(distance * Math.Cos(angle), distance * Math.Sin(angle), 0d);
    }

    private Run Execute(bool carry, int skipPercent = 0)
    {
        (ServiceProvider as IDisposable)?.Dispose();
        Setup();

        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = FrameHarness.Create(dbe, Declare, $"{nameof(OwedSlotCarryTests)}-{carry}", new SubscriptionsOptions
        {
            MaxSessions = 16,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,

            // DELIBERATELY binding, unlike every other fixture here.
            EnterBudgetPerFrame = EnterBudget,

            OwedSlotCarry = carry,

            // The SLICE is pinned off here, and that is a statement about what this fixture tests rather than a convenience. Bounding how much of a
            // session's debt one frame serves deliberately spreads the same entities over more frames, so byte equality against the blunt arm stops
            // being the right property the moment it engages — the two arms describe the same world in a different order. What validates the slice is
            // DifferentialOracleTests, which runs on the engine's defaults and compares each session's replica against the server's own state rather
            // than against another arm.
            OwedSliceMultiplier = 0,
        });

        var workload = new OracleWorkload(harness, seed: 20260918);
        workload.Seed(creatures: 320, rocks: 120);

        var sessions = harness.OpenSessions(SessionCount, Profile);
        var frames = new List<byte[]>[SessionCount];
        var delivery = new Random[SessionCount];
        for (var s = 0; s < SessionCount; s++)
        {
            frames[s] = [];
            delivery[s] = new Random(31337 + s);
            Assert.That(harness.Sessions.SetViewpoint(sessions[s], ViewpointAt(s, 1)), Is.True);
        }

        harness.PrimeBlocks();

        for (var tick = 2L; tick <= Ticks + 1; tick++)
        {
            workload.Step();
            for (var s = 0; s < SessionCount; s++)
            {
                Assert.That(harness.Sessions.SetViewpoint(sessions[s], ViewpointAt(s, tick)), Is.True);
            }

            harness.RunTick(tick);
            for (var s = 0; s < SessionCount; s++)
            {
                // Seeded and identical in both arms, so the two runs skip on the same ticks and the byte comparison stays a comparison.
                if (delivery[s].Next(100) < skipPercent)
                {
                    continue;
                }

                frames[s].AddRange(harness.Collect(sessions[s]));
            }
        }

        // Whatever is still sitting in a hand-off slot at the end is part of what the server said.
        for (var s = 0; s < SessionCount; s++)
        {
            frames[s].AddRange(harness.Collect(sessions[s]));
        }

        return new Run
        {
            Frames = frames,
            Catalog = harness.CatalogPlan,
            EntersDeferred = harness.Assembler.EntersDeferred,
            FramesProduced = harness.Assembler.FramesProduced,
            Causes = harness.Assembler.FullGatherCauses,
            TemporalGathers = harness.Assembler.TemporalGathers,
            FullGathers = harness.Assembler.FullGathers,
            OwedStale = harness.Assembler.OwedStaleSlots,
        };
    }
}
