using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// The scoped causal id (#1517): spans begun in scope carry it, scopes nest and restore, it wins over the ambient Activity, and it crosses threads
/// only when carried by <see cref="CausalScope.Capture"/> / <see cref="CausalScope.Enter"/>.
/// </summary>
[TestFixture]
[NonParallelizable] // reads and writes the process-global profiler gates through the span factories
public class CausalScopeTests
{
    private static readonly CausalId Id = new(0x5459_0000_0000_0005UL, 6);

    [SetUp]
    public void ClearScope() => CausalScope.Clear();

    [Test]
    [Category("TelemetryGated")] // Correlation is off suite-wide; shard.py's T pass turns it on
    public void A_span_begun_in_scope_carries_the_scoped_id()
    {
        Assume.That(TelemetryConfig.ProfilerCorrelationActive, Is.True, "needs TYPHON__PROFILER__CORRELATION__ENABLED=true (shard.py T pass)");

        using (CausalScope.Enter(Id))
        {
            using var inside = TyphonEvent.BeginBTreeInsert();
            Assert.That((inside.Header.TraceIdHi, inside.Header.TraceIdLo), Is.EqualTo((Id.Hi, Id.Lo)));
        }

        using var after = TyphonEvent.BeginBTreeInsert();
        Assert.That((after.Header.TraceIdHi, after.Header.TraceIdLo), Is.EqualTo((0UL, 0UL)), "leaving the scope restores no id");
    }

    [Test]
    [Category("TelemetryGated")]
    public void Scopes_nest_and_restore_the_outer_id()
    {
        Assume.That(TelemetryConfig.ProfilerCorrelationActive, Is.True);

        var inner = new CausalId(1, 2);
        using (CausalScope.Enter(Id))
        {
            using (CausalScope.Enter(inner))
            {
                Assert.That(CausalScope.Current, Is.EqualTo(inner));
            }

            Assert.That(CausalScope.Current, Is.EqualTo(Id));
        }

        Assert.That(CausalScope.Current.IsNone, Is.True);
    }

    [Test]
    [Category("TelemetryGated")]
    public void The_scoped_id_wins_over_the_ambient_activity_and_the_activity_is_the_fallback()
    {
        Assume.That(TelemetryConfig.ProfilerCorrelationActive, Is.True);

        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var request = CausalId.FromTraceId(activity.TraceId);

        using (CausalScope.Enter(Id))
        {
            using var scoped = TyphonEvent.BeginBTreeInsert();
            Assert.That((scoped.Header.TraceIdHi, scoped.Header.TraceIdLo), Is.EqualTo((Id.Hi, Id.Lo)));
        }

        using var ambient = TyphonEvent.BeginBTreeInsert();
        Assert.That((ambient.Header.TraceIdHi, ambient.Header.TraceIdLo), Is.EqualTo((request.Hi, request.Lo)));
        Assert.That(CausalScope.Capture(), Is.EqualTo(request), "with nothing scoped, Capture takes the activity's trace id");
    }

    [Test]
    [Category("TelemetryGated")]
    public void A_captured_id_entered_on_another_thread_reaches_that_threads_spans()
    {
        Assume.That(TelemetryConfig.ProfilerCorrelationActive, Is.True);

        CausalId captured;
        using (CausalScope.Enter(Id))
        {
            captured = CausalScope.Capture();
        }

        ulong hi = 0, lo = 0, bareHi = 1;
        var worker = new Thread(() =>
        {
            using (var bare = TyphonEvent.BeginBTreeInsert())
            {
                bareHi = bare.Header.TraceIdHi;
            }

            using (CausalScope.Enter(captured))
            {
                using var span = TyphonEvent.BeginBTreeInsert();
                hi = span.Header.TraceIdHi;
                lo = span.Header.TraceIdLo;
            }
        });
        worker.Start();
        worker.Join();

        Assert.That(bareHi, Is.Zero, "a thread-static id does not cross threads on its own");
        Assert.That((hi, lo), Is.EqualTo((Id.Hi, Id.Lo)));
    }

    [Test]
    public void With_correlation_off_entering_a_scope_does_nothing()
    {
        Assume.That(TelemetryConfig.ProfilerCorrelationActive, Is.False, "the suite's default configuration");

        using (CausalScope.Enter(Id))
        {
            Assert.That(CausalScope.Current.IsNone, Is.True);
            Assert.That(CausalScope.Capture().IsNone, Is.True);
        }
    }
}
