using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The two long runs #1040 owes (16 § 7): AC-9's first half — an hour of entity churn with every balance held — and AC-11 — ten minutes of motion with every
/// sample inside the tolerance. Run by hand, unattended: they take minutes, not the suite's milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Server time, not wall time.</b> The harness ticks at 10 Hz and runs its ticks back to back, so an hour is 36 000 ticks and ten minutes 6 000. What the
/// criteria are about — whether something drifts, accumulates or leaks with the number of ticks — is a function of ticks; the wall clock only decides how long
/// the run takes. <c>TYPHON_SOAK_TICKS</c> overrides the count.
/// </para>
/// <para>
/// <b>The judge is the push oracle</b> (<see cref="OracleHarness"/>): seeded spawns, destroys, moves and teleports, sessions skipping frames at 0–90 %, and at
/// each comparison the client's world equal to the server's field for field — every held entity live, nothing it should hold missing, every position inside
/// the motion tolerance, and every identity the allocator counts held by a live entity or a lease (none leaked).
/// </para>
/// </remarks>
[TestFixture]
[Explicit("A soak: minutes long, run by hand")]
[Category("Manual")]
[NonParallelizable]
class SubscriptionSoakTests : TestBase<SubscriptionSoakTests>
{
    private static int Ticks(int fallback) => int.TryParse(Environment.GetEnvironmentVariable("TYPHON_SOAK_TICKS"), out var t) && t > 0 ? t : fallback;

    /// <summary>
    /// AC-9's first half: an hour of churn (36 000 ticks), four sessions skipping 0, 30, 60 and 90 % of their frames. Every simulated minute the oracle
    /// quiesces and compares — the enter/leave balance of each session, every identity accounted for — and the resources a leak would grow are sampled:
    /// identities live, replication blocks held, the managed heap after a collection. None may climb with the tick count.
    /// </summary>
    [Test]
    [Property("CacheSize", 256L * 1024 * 1024)]
    public void AnHourOfEntityChurnHoldsEveryBalance()
    {
        const int Minute = 600;
        var ticks = Ticks(36_000);
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 20261019, [0, 30, 60, 90],
            nameof(AnHourOfEntityChurnHoldsEveryBalance));
        var samples = new List<(int Tick, int Identities, int Blocks, long Heap, int Entities)>();
        for (var i = 1; i <= ticks; i++)
        {
            oracle.Step();
            if (i % Minute != 0)
            {
                continue;
            }

            oracle.Quiesce();
            oracle.AssertConverged($"after {i} churned ticks ({i / Minute} simulated minutes)");
            var blocks = 0;
            foreach (var state in oracle.Frames.Subscriptions.ReplicationStates)
            {
                blocks += state?.Directory.Count ?? 0;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            samples.Add((i, oracle.Frames.Replication.NetIds.LiveCount, blocks, GC.GetTotalMemory(forceFullCollection: true), oracle.LiveEntityCount));
        }

        foreach (var s in samples)
        {
            TestContext.Out.WriteLine($"tick {s.Tick,6}  entities {s.Entities,5}  identities live {s.Identities,5}  blocks {s.Blocks,4}  "
                + $"heap {s.Heap / 1024,7:N0} KiB");
        }

        // A leak grows with the tick count; churn moves the numbers around a level. Compared over the run's second half, against its first, so the warm-up
        // and the first-time growth of every pool are behind both.
        var half = samples.Count / 2;
        long FirstHalfMax(Func<(int Tick, int Identities, int Blocks, long Heap, int Entities), long> f)
        {
            var max = 0L;
            for (var k = 0; k < half; k++)
            {
                max = Math.Max(max, f(samples[k]));
            }

            return max;
        }

        long SecondHalfMax(Func<(int Tick, int Identities, int Blocks, long Heap, int Entities), long> f)
        {
            var max = 0L;
            for (var k = half; k < samples.Count; k++)
            {
                max = Math.Max(max, f(samples[k]));
            }

            return max;
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(1000), "the run churned too little to say anything about an hour");
            // The oracle's workload spawns a little more than it destroys, so its world grows through the hour (≈ 100 to ≈ 2 700 entities, measured
            // 2026-10-19); blocks follow the clusters the entities occupy. A leaked block shows as more blocks per entity, not as more blocks.
            Assert.That(SecondHalfMax(s => s.Blocks * 1000L / Math.Max(1, s.Entities)),
                Is.LessThanOrEqualTo(FirstHalfMax(s => s.Blocks * 1000L / Math.Max(1, s.Entities)) * 5 / 4),
                "replication blocks per entity climb with the ticks");
            Assert.That(SecondHalfMax(s => s.Heap), Is.LessThanOrEqualTo(FirstHalfMax(s => s.Heap) * 5 / 4), "the managed heap climbs with the ticks");
            Assert.That(SecondHalfMax(s => s.Identities - s.Entities), Is.LessThanOrEqualTo(FirstHalfMax(s => s.Identities - s.Entities) + 64),
                "identities beyond the live entities climb with the ticks");
        });
    }

    /// <summary>
    /// AC-11: ten minutes of motion (6 000 ticks) for a session served every frame, compared after EVERY tick — not at quiet points — so every sample of
    /// every held moving entity is inside 5 cm and the quanta (<see cref="OracleHarness.MotionToleranceM"/>) of where the server has it.
    /// </summary>
    [Test]
    [Property("CacheSize", 256L * 1024 * 1024)]
    public void TenMinutesOfMotionStayWithinTolerance()
    {
        var ticks = Ticks(6_000);
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1011, [0],
            nameof(TenMinutesOfMotionStayWithinTolerance));
        for (var i = 1; i <= ticks; i++)
        {
            oracle.Step();
            oracle.AssertWorldConverged($"motion, tick {i} of {ticks}");
        }

        TestContext.Out.WriteLine($"{oracle.Compared:N0} entity samples compared over {ticks} ticks, every one inside {OracleHarness.MotionToleranceM:F4} m");
        Assert.That(oracle.Compared, Is.GreaterThan(ticks * 10L), "too few samples to call it ten minutes of motion");
    }
}
