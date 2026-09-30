using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// #1083 — an attach session earns <see cref="SessionCapability.Realms"/> from its own stream, which is the only place the answer exists.
/// </summary>
/// <remarks>
/// <para>
/// The engine declares no realm count in its Init frame and there is no handshake field for one, so the capability cannot be read at connect time the way
/// <c>schema</c> is. What it can be read from is the telemetry: <see cref="TraceEventKind.SpatialRealmTelemetry"/> is emitted once per <i>runnable</i> realm
/// per archetype, so one record is proof both that this engine has realms and that its spatial subtree is reporting them — exactly the two preconditions for
/// the live realm board having anything to draw.
/// </para>
/// <para>
/// <b>Why this fixture exists at all.</b> Without it the realm board shipped unreachable: the view is scoped on the realms capability in
/// <c>viewRegistry</c>, an attach session had only <c>profiler</c> (+<c>schema</c>), so the View menu item and the palette command were both absent and no
/// gesture could open the panel. "A panel can be complete, tested, and impossible to open" is WB-02's lesson; this is the server half of it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AttachSessionRealmCapabilityTests
{
    private static CancellationToken Timeout10s => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    /// <summary>One realm-telemetry record, laid out inside the harness's synthesized timeline so it reads as a normal tick-body record.</summary>
    private static byte[] RealmTelemetry(int tickIndex) => MockRecordFactory.GenericSpan(
        TraceEventKind.SpatialRealmTelemetry,
        CaptureHarness.At(tickIndex * CaptureHarness.TickSpacing + 200),
        durationTicks: 0,
        payloadBytes: 16);

    /// <summary>
    /// The session projection over the harness's runtime. Deliberately NOT disposed by the test: <c>AttachSession.Dispose</c> disposes the runtime, which the
    /// harness owns and disposes itself, so a <c>using</c> here would dispose it twice and tear the socket down mid-assertion.
    /// </summary>
    private static AttachSession SessionOver(CaptureHarness h) =>
        new(Guid.NewGuid(), $"127.0.0.1:{h.Server.Port}", h.Runtime);

    /// <summary>
    /// The negative case, and it is the common one: most engines run a single world, and a realm UI on a session with one realm is a menu entry that opens
    /// an empty table.
    /// </summary>
    [Test]
    public async Task AnEngineThatReportsNoRealms_LeavesTheSessionWithout()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.Everything, Timeout10s);
        var session = SessionOver(h);

        for (var i = 0; i < 3; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(i + 1), detailRecords: 5, ct: Timeout10s);
        }

        Assert.That(h.Runtime.HasRealmTelemetry, Is.False, "no per-realm record crossed the socket");
        Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Realms));
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Profiler), "a live attach still profiles");
    }

    [Test]
    public async Task OnePerRealmRecord_GrantsTheRealmsCapability()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.Everything, Timeout10s);
        var session = SessionOver(h);

        await h.SendTickAsync(0, engineTick: 1, ct: Timeout10s);
        Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Realms), "precondition: the capability is earned, not default");

        await h.SendBlockAsync(RealmTelemetry(1), Timeout10s);

        Assert.That(h.Runtime.HasRealmTelemetry, Is.True);
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Realms), "the shell gates the realm board on this");
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Profiler), "and the profiler capability is not displaced");
        Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Database),
            "an attach session still has no browsable database — blocker B1 is unchanged by realms");
    }

    /// <summary>
    /// <b>The assertion the unfiltered walk exists for.</b> Kind 67 is not in the exempt set, so in cherry-pick mode it is dropped from the retained stream
    /// for every tick outside an armed window. A capability derived from what the builder receives would therefore make the Realms view appear when an
    /// operator arms a capture and vanish when they disarm it — a menu whose contents move with an unrelated control.
    /// </summary>
    [Test]
    public async Task InCherryPickIdle_TheCapabilityStillArrives_ThoughTheRecordIsDropped()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);
        var session = SessionOver(h);

        await h.SendBlockAsync(
            MockRecordFactory.Concat(CaptureHarness.BuildTick(0, engineTick: 1), RealmTelemetry(0)),
            Timeout10s);

        var state = h.Runtime.CaptureState;
        Assert.Multiple(() =>
        {
            Assert.That(state.State, Is.EqualTo("Idle"), "cherry-pick starts idle, so the record was filtered out");
            Assert.That(state.BytesRetained, Is.LessThan(state.BytesReceived), "and the drop really happened");
            Assert.That(session.Capabilities, Contains.Item(SessionCapability.Realms), "yet the capability was read from the unfiltered block");
        });
    }

    /// <summary>
    /// <b>The capability is announced, and that is the half that makes the view reachable.</b> A session is projected to the client once, at attach, and the
    /// client caches what that projection said. <c>schema</c> survives that because the Init frame has already arrived by the time the attach call returns; a
    /// realm record cannot, since it rides a later tick. Without this delta the server grants <c>realms</c> to a client that never asks again, and the Realms
    /// view stays absent from the View menu and the palette over a session that has it — observed exactly that way against the live SWG demo before the delta
    /// existed. Broadcast <b>once</b>, on the transition, because it is a prompt to re-read the session and not a state carrier.
    /// </summary>
    [Test]
    public async Task EarningTheCapability_IsAnnouncedOnTheLiveStream_Once()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.Everything, Timeout10s);
        var (_, reader) = h.Runtime.Subscribe();

        await h.SendTickAsync(0, engineTick: 1, ct: Timeout10s);
        await h.SendBlockAsync(RealmTelemetry(1), Timeout10s);
        await h.SendBlockAsync(RealmTelemetry(2), Timeout10s);
        await h.SendTickAsync(3, engineTick: 4, ct: Timeout10s);

        var announcements = 0;
        while (reader.TryRead(out var delta))
        {
            if (delta.Kind == "capabilitiesChanged")
            {
                announcements++;
            }
        }

        Assert.That(announcements, Is.EqualTo(1),
            "exactly one announcement: the transition is the event, and a per-tick repeat would have the client re-read the session every tick forever");
    }

    /// <summary>
    /// It latches. A realm going dormant stops its records — that is RLM-B's whole claim — and a capability that tracked the live count would take the
    /// board away from under an operator watching precisely for realms to fall asleep.
    /// </summary>
    [Test]
    public async Task TheCapabilityDoesNotLapse_WhenTheRealmsGoQuiet()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.Everything, Timeout10s);
        var session = SessionOver(h);

        await h.SendBlockAsync(RealmTelemetry(0), Timeout10s);
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Realms), "precondition");

        for (var i = 1; i < 4; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(i + 1), detailRecords: 5, ct: Timeout10s);
        }

        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Realms), "three realm-free ticks must not retract it");
    }
}
