using System;
using System.Buffers.Binary;
using NUnit.Framework;
using SwgTatooine.Dashboard;
using Typhon.Engine;
using Typhon.Subscriptions.AspNetCore;

namespace SwgTatooine.Tests;

/// <summary>
/// The dashboard's per-system timeline (<see cref="SystemTimeline"/>, claude/scratch/swg-dashboard-system-timeline.md): fed the engine's own feed bodies
/// (<see cref="SystemTicksFeed"/>), it buckets ticks into seconds by tick number, keeps ten minutes, hands a circuit only the seconds that changed, gives a
/// second's ticks for the drill-down, and starts again when the server does.
/// </summary>
[TestFixture]
internal sealed class SystemTimelineChecks
{
    private const int Systems = 4;
    private const int Tps = 10;
    private static readonly string[] Names = ["A", "B", "C", "D"];

    // A tick's CPU for a system: distinct per tick and system, so a sum taken over the wrong ticks shows.
    private static float Cpu(long tick, int system) => 100f + (tick * 7 % 13) + (system * 50f);

    /// <summary>The feed's body for ticks [<paramref name="from"/>, <paramref name="to"/>), as the server would answer a poll after from − 1.</summary>
    private static byte[] Body(long from, long to, bool names)
    {
        var ring = new TickTelemetryRing(8192, Systems);
        var metrics = new SystemTelemetry[Systems];
        for (var t = 0L; t < to; t++)
        {
            for (var s = 0; s < Systems; s++)
            {
                metrics[s] = new SystemTelemetry { SystemIndex = s, DurationUs = Cpu(t, s), WorkUs = 0f, WorkersTouched = 1 };
            }

            var tick = new TickTelemetry { TickNumber = t, ActualDurationMs = 5f + (t % Tps) };
            ring.Record(in tick, metrics);
        }

        var buffer = new byte[SystemTicksFeed.MaxBytes(Names, SystemTicksFeed.TicksAfter(ring, from - 1), names)];
        var length = SystemTicksFeed.Write(buffer, ring, Names, 8, from - 1, names);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static SystemTimeline Fed(long ticks)
    {
        var timeline = new SystemTimeline();
        for (var from = 0L; from < ticks; from += 1000)
        {
            Assert.That(timeline.Ingest(Body(from, Math.Min(ticks, from + 1000), timeline.NeedsNames), Tps, timeline.NeedsNames), Is.True);
        }

        return timeline;
    }

    /// <summary>
    /// AC-2: a second's stacked CPU — every system, not the top ten — is the mean over that second's ticks of their Σ CPU; and each system's bar is its own
    /// mean over them. Seconds are tick ÷ ticks-per-second: second 1 is ticks 10-19.
    /// </summary>
    [Test]
    public void ASecond_IsTheMeanOfItsTicks()
    {
        var timeline = Fed(30);
        var mean = new float[Systems];
        Assert.That(timeline.TrySecond(1, mean, out var ticks), Is.True);
        var expectedTotal = 0d;
        for (var t = 10; t < 20; t++)
        {
            for (var s = 0; s < Systems; s++)
            {
                expectedTotal += Cpu(t, s) / 10d;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(ticks, Is.EqualTo(Tps));
            Assert.That(mean[2], Is.EqualTo((float)(Sum(10, 20, 2) / 10d)).Within(1e-3f), "a system's own mean");
            Assert.That(mean[0] + mean[1] + mean[2] + mean[3], Is.EqualTo((float)expectedTotal).Within(1e-2f), "the stack is the mean of Σ CPU");
            Assert.That(timeline.Names, Is.EqualTo(Names));
            Assert.That((timeline.Workers, timeline.TicksPerSecond, timeline.LastTick), Is.EqualTo((8, Tps, 29L)));
        });

        static double Sum(int from, int to, int s)
        {
            var sum = 0d;
            for (var t = from; t < to; t++)
            {
                sum += Cpu(t, s);
            }

            return sum;
        }
    }

    /// <summary>
    /// A circuit gets every second the first time, then only what changed: a poll that adds the rest of second 2 and all of second 3 sends seconds 2 and 3,
    /// and a poll with nothing new sends nothing.
    /// </summary>
    [Test]
    public void ACircuit_IsSentOnlyTheSecondsThatChanged()
    {
        var timeline = new SystemTimeline();
        timeline.Ingest(Body(0, 25, names: true), Tps, hasNames: true);
        var all = timeline.CopySeconds(0, out var v1);
        timeline.Ingest(Body(25, 40, names: false), Tps, hasNames: false);
        var changed = timeline.CopySeconds(v1, out var v2);
        var nothing = timeline.CopySeconds(v2, out _);
        var size = SystemTimeline.SecondBytes(Systems);
        Assert.Multiple(() =>
        {
            Assert.That(all.Length / size, Is.EqualTo(3), "seconds 0, 1 and the start of 2");
            Assert.That(changed.Length / size, Is.EqualTo(2), "second 2 completed, second 3 new");
            Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(changed), Is.EqualTo(2d), "oldest first");
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(24)), Is.EqualTo(Tps), "second 2 now has all its ticks");
            Assert.That(nothing, Is.Empty);
        });
    }

    /// <summary>AC-4: the drill-down is the second's ticks, oldest first, each with every system's CPU as the feed gave it.</summary>
    [Test]
    public void TheDrillDown_IsTheSecondsTicks()
    {
        var timeline = Fed(30);
        var ticks = timeline.CopyTicks(1);
        var size = SystemTimeline.TickBytes(Systems);
        Assert.That(ticks.Length, Is.EqualTo(Tps * size));
        Assert.Multiple(() =>
        {
            for (var k = 0; k < Tps; k++)
            {
                var record = ticks.AsSpan(k * size, size);
                Assert.That(BinaryPrimitives.ReadDoubleLittleEndian(record), Is.EqualTo(10d + k));
                Assert.That(BinaryPrimitives.ReadSingleLittleEndian(record[(16 + 30)..]), Is.EqualTo(Cpu(10 + k, 3)), "system 3's CPU at tick 10 + k");
                Assert.That(record[16 + 8], Is.EqualTo(10), "a serial system: one core");
            }
        });
    }

    /// <summary>AC-2: ten minutes are kept — 610 seconds fed, the first ten gone, the last 600 held.</summary>
    [Test]
    public void TenMinutes_AreKept()
    {
        var timeline = Fed(610 * Tps);
        var mean = new float[Systems];
        Assert.Multiple(() =>
        {
            Assert.That(timeline.TrySecond(9, mean, out _), Is.False, "older than ten minutes");
            Assert.That(timeline.TrySecond(10, mean, out _), Is.True);
            Assert.That(timeline.TrySecond(609, mean, out _), Is.True);
            Assert.That(timeline.CopySeconds(0, out _).Length / SystemTimeline.SecondBytes(Systems), Is.EqualTo(SystemTimeline.Seconds));
            Assert.That(timeline.CopyTicks(9), Is.Empty);
        });
    }

    /// <summary>
    /// AC-6: a game server that restarted answers tick numbers behind the newest held — the timeline starts again from them; one that answers no names when
    /// the timeline asked for none after a clear is ignored until the next poll asks.
    /// </summary>
    [Test]
    public void ARestart_StartsTheTimelineAgain()
    {
        var timeline = Fed(300);
        timeline.Ingest(Body(0, 15, names: false), Tps, hasNames: false);
        var mean = new float[Systems];
        var cleared = new SystemTimeline();
        cleared.Ingest(Body(0, 10, names: false), Tps, hasNames: false);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.LastTick, Is.EqualTo(14), "the restarted server's ticks");
            Assert.That(timeline.TrySecond(20, mean, out _), Is.False, "the old run's seconds are gone");
            Assert.That(timeline.TrySecond(1, mean, out var ticks) && ticks == 5, Is.True, "second 1 is the new run's ticks 10-14");
            Assert.That((cleared.NeedsNames, cleared.LastTick), Is.EqualTo((true, -1L)), "no names, no data: wait for the poll that asks");
        });
    }
}
