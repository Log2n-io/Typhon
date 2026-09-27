using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class TickTelemetryRingTests
{
    [Test]
    public void NewRing_HasNoTicks()
    {
        var ring = new TickTelemetryRing(16, 3);

        Assert.That(ring.TotalTicksRecorded, Is.EqualTo(0));
        Assert.That(ring.OldestAvailableTick, Is.EqualTo(-1));
        Assert.That(ring.NewestTick, Is.EqualTo(-1));
    }

    [Test]
    public void RecordAndRead_SingleTick()
    {
        var ring = new TickTelemetryRing(16, 2);
        var tick = new TickTelemetry { TickNumber = 0, ActualDurationMs = 1.5f, ActiveSystemCount = 2 };
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[2];
        systems[0] = new SystemTelemetry { SystemIndex = 0, TransitionLatencyUs = 0.3f };
        systems[1] = new SystemTelemetry { SystemIndex = 1, DurationUs = 100f };

        ring.Record(in tick, systems);

        Assert.That(ring.TotalTicksRecorded, Is.EqualTo(1));
        Assert.That(ring.OldestAvailableTick, Is.EqualTo(0));
        Assert.That(ring.NewestTick, Is.EqualTo(0));

        ref readonly var readTick = ref ring.GetTick(0);
        Assert.That(readTick.ActualDurationMs, Is.EqualTo(1.5f));
        Assert.That(readTick.ActiveSystemCount, Is.EqualTo(2));

        var readSystems = ring.GetSystemMetrics(0);
        Assert.That(readSystems.Length, Is.EqualTo(2));
        Assert.That(readSystems[0].TransitionLatencyUs, Is.EqualTo(0.3f));
        Assert.That(readSystems[1].DurationUs, Is.EqualTo(100f));
    }

    [Test]
    public void RingWraps_OldestOverwritten()
    {
        const int capacity = 4;
        var ring = new TickTelemetryRing(capacity, 1);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[1];

        // Write 6 ticks into a ring of capacity 4
        for (var i = 0; i < 6; i++)
        {
            var tick = new TickTelemetry { TickNumber = i, ActualDurationMs = i * 1.0f };
            systems[0] = new SystemTelemetry { SystemIndex = 0, DurationUs = i * 10f };
            ring.Record(in tick, systems);
        }

        Assert.That(ring.TotalTicksRecorded, Is.EqualTo(6));
        Assert.That(ring.OldestAvailableTick, Is.EqualTo(2)); // 6 - 4 = 2
        Assert.That(ring.NewestTick, Is.EqualTo(5));

        // Ticks 0 and 1 are overwritten
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.GetTick(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.GetTick(1));

        // Ticks 2-5 are available
        for (var i = 2; i <= 5; i++)
        {
            ref readonly var t = ref ring.GetTick(i);
            Assert.That(t.TickNumber, Is.EqualTo(i));
            Assert.That(t.ActualDurationMs, Is.EqualTo(i * 1.0f));
        }
    }

    [Test]
    public void MultipleTicks_ReadBackConsistent()
    {
        var ring = new TickTelemetryRing(64, 3);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[3];

        for (var i = 0; i < 50; i++)
        {
            var tick = new TickTelemetry
            {
                TickNumber = i,
                ActualDurationMs = i * 0.5f,
                TargetDurationMs = 16.67f,
                OverrunRatio = (i * 0.5f) / 16.67f,
                ActiveWorkerCount = 8,
                ActiveSystemCount = 3
            };

            for (var s = 0; s < 3; s++)
            {
                systems[s] = new SystemTelemetry
                {
                    SystemIndex = s,
                    TransitionLatencyUs = s * 0.1f + i * 0.01f,
                    DurationUs = s * 100f + i
                };
            }

            ring.Record(in tick, systems);
        }

        Assert.That(ring.TotalTicksRecorded, Is.EqualTo(50));

        // Verify last tick
        ref readonly var last = ref ring.GetTick(49);
        Assert.That(last.TickNumber, Is.EqualTo(49));
        Assert.That(last.ActiveSystemCount, Is.EqualTo(3));

        var lastSystems = ring.GetSystemMetrics(49);
        Assert.That(lastSystems[2].SystemIndex, Is.EqualTo(2));
    }

    [Test]
    public void GetTick_FutureTick_Throws()
    {
        var ring = new TickTelemetryRing(16, 1);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[1];
        ring.Record(new TickTelemetry { TickNumber = 0 }, systems);

        Assert.Throws<ArgumentOutOfRangeException>(() => ring.GetTick(1));
    }

    [Test]
    public void GetTick_NoTicksRecorded_Throws()
    {
        var ring = new TickTelemetryRing(16, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => ring.GetTick(0));
    }

    [Test]
    public void Capacity_MustBePowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new TickTelemetryRing(3, 1));
        Assert.Throws<ArgumentException>(() => new TickTelemetryRing(0, 1));
        Assert.DoesNotThrow(() => new TickTelemetryRing(1, 1));
        Assert.DoesNotThrow(() => new TickTelemetryRing(1024, 1));
    }

    [Test]
    public void ExactCapacityFill_AllReadable()
    {
        const int capacity = 8;
        var ring = new TickTelemetryRing(capacity, 1);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[1];

        for (var i = 0; i < capacity; i++)
        {
            ring.Record(new TickTelemetry { TickNumber = i }, systems);
        }

        Assert.That(ring.OldestAvailableTick, Is.EqualTo(0));
        Assert.That(ring.NewestTick, Is.EqualTo(7));

        for (var i = 0; i < capacity; i++)
        {
            Assert.That(ring.GetTick(i).TickNumber, Is.EqualTo(i));
        }
    }

    /// <summary>
    /// <see cref="TickTelemetryRing.TryGetRange"/> refuses an empty ring, clamps up to the oldest retained tick, and never yields a tick
    /// <see cref="TickTelemetryRing.GetTick"/> would refuse.
    /// </summary>
    /// <remarks>
    /// The property under test is the one three shipped window passes got wrong: both bounds read -1 while the ring is empty, so
    /// <c>Math.Max(oldest, tick - window)</c> is -1 and the loop's first call is <c>GetTick(-1)</c>. The cross-check is exhaustive over every <c>from</c> a
    /// caller could plausibly compute rather than a few spot values, because the whole value of the helper is that no tick it returns can throw — asserting
    /// that directly is cheaper than reasoning about which offsets are interesting.
    /// </remarks>
    [Test]
    [VerifiesRule("TR-01")]
    public void TryGetRange_RefusesAnEmptyRing_AndNeverYieldsATickGetTickWouldRefuse()
    {
        var ring = new TickTelemetryRing(4, 1);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[1];

        Assert.That(ring.TryGetRange(0, out _, out _), Is.False, "an empty ring has no readable range");
        Assert.That(ring.TryGetRange(-100, out _, out _), Is.False, "and asking from before the beginning does not conjure one");

        for (var recorded = 1; recorded <= 6; recorded++)
        {
            ring.Record(new TickTelemetry { TickNumber = recorded - 1 }, systems);

            for (var from = -3L; from <= recorded + 1; from++)
            {
                if (!ring.TryGetRange(from, out var first, out var last))
                {
                    continue;
                }

                Assert.That(first, Is.GreaterThanOrEqualTo(ring.OldestAvailableTick));
                Assert.That(last, Is.EqualTo(ring.NewestTick), "the range always ends at the newest tick");
                for (var t = first; t <= last; t++)
                {
                    var at = t;
                    Assert.That(() => ring.GetTick(at), Throws.Nothing, $"recorded={recorded} from={from} t={at}");
                    Assert.That(() => ring.GetSystemMetrics(at), Throws.Nothing, $"recorded={recorded} from={from} t={at}");
                }
            }
        }

        Assert.That(ring.OldestAvailableTick, Is.EqualTo(2), "the premise: the ring has wrapped, so the clamp is doing real work");
        Assert.That(ring.TryGetRange(0, out var clamped, out _), Is.True);
        Assert.That(clamped, Is.EqualTo(2), "a `from` older than the ring retains is clamped up, not refused");
        Assert.That(ring.TryGetRange(ring.NewestTick + 1, out _, out _), Is.False, "a range starting past the newest tick is empty, not a one-tick window");
    }

    /// <summary>
    /// <see cref="TickTelemetryRing.TryGetTick"/> and <see cref="TickTelemetryRing.TryGetSystemMetrics"/> answer false where their throwing peers throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a range from <see cref="TickTelemetryRing.TryGetRange"/> is not enough.</b> The clamp is a snapshot and the validation is live. On a window as
    /// wide as the ring — <c>TyphonRuntime.ReadStats</c> takes <c>windowTicks</c> from its caller, unbounded — the oldest end clamps EXACTLY to
    /// <see cref="TickTelemetryRing.OldestAvailableTick"/>, so one tick recorded by the driver between resolving the range and reading its first element
    /// evicts it and <see cref="TickTelemetryRing.GetTick"/> throws out of a public API. A reader walking a range wants that tick skipped; losing the oldest
    /// sample of a percentile is the tearing it already accepts by reading a live ring.
    /// </para>
    /// <para>
    /// Asserted against the throwing peers on the same tick numbers, so the pair cannot drift: the Try forms must answer false exactly where the others raise.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("TR-01")]
    public void TryGetTickAndTryGetSystemMetrics_AnswerFalseWhereTheirThrowingPeersThrow()
    {
        var ring = new TickTelemetryRing(4, 1);
        Span<SystemTelemetry> systems = stackalloc SystemTelemetry[1];

        Assert.Multiple(() =>
        {
            Assert.That(ring.TryGetTick(0, out _), Is.False, "an empty ring holds nothing");
            Assert.That(ring.TryGetSystemMetrics(0, out _), Is.False);
            Assert.That(ring.TryGetTick(-1, out _), Is.False, "and the negative the trap produces is not a tick either");
        });

        for (var i = 0; i < 6; i++)
        {
            ring.Record(new TickTelemetry { TickNumber = i, ActualDurationMs = i + 1f }, systems);
        }

        Assert.That(ring.OldestAvailableTick, Is.EqualTo(2), "the premise: ticks 0 and 1 have been evicted");

        foreach (var evicted in (ReadOnlySpan<long>)[-1, 0, 1, 6, 7])
        {
            var at = evicted;
            Assert.Multiple(() =>
            {
                Assert.That(ring.TryGetTick(at, out var gone), Is.False, $"tick {at} is not held");
                Assert.That(gone.TickNumber, Is.Zero, "and the out parameter is default rather than a stale slot's contents");
                Assert.That(ring.TryGetSystemMetrics(at, out var noSystems), Is.False);
                Assert.That(noSystems.Length, Is.Zero);
                Assert.That(() => ring.GetTick(at), Throws.TypeOf<ArgumentOutOfRangeException>(), "the peer raises on exactly this tick");
            });
        }

        for (var held = 2L; held <= 5; held++)
        {
            var at = held;
            Assert.Multiple(() =>
            {
                Assert.That(ring.TryGetTick(at, out var tick), Is.True);
                Assert.That(tick.TickNumber, Is.EqualTo(at), "and it is the tick asked for, not a neighbour");
                Assert.That(tick.ActualDurationMs, Is.EqualTo(at + 1f));
                Assert.That(ring.TryGetSystemMetrics(at, out var metrics), Is.True);
                Assert.That(metrics.Length, Is.EqualTo(1));
            });
        }
    }
}
