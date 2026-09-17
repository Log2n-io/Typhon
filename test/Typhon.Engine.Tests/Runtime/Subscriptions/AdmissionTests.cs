using NUnit.Framework;
using System;
using System.Text;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-04 — admission: what reaches the application's hook, what never does, and what the hook's answer carries into the tick.
/// </summary>
/// <remarks>
/// The first version has no engine-side security at all (03-wire-protocol § 12 W22), which makes the ordering below the only protection there is: a kind the
/// application never declared must be refused <i>before</i> any application code runs, or every deployment has to defend its hook against arbitrary strings
/// from the network. The close-code range check is the same argument one layer up — a refusal that borrows a protocol code tells every SDK something the
/// application did not mean.
/// </remarks>
[TestFixture]
class AdmissionTests
{
    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private SubscriptionsSessions _sessions;
    private SessionTable _table;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "AdmissionTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "AdmissionTestAllocator" });
        _sessions = new SubscriptionsSessions();
        _table = new SessionTable("Sessions", _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = 8 }, _sessions.SessionEvents);
    }

    [TearDown]
    public void TearDown()
    {
        _table?.Dispose();
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private bool Admit(string kind, out SessionId session, out ushort code, out string reason, ReadOnlySpan<byte> payload = default)
        => _table.TryAdmit(_sessions, new AdmissionRequest(kind, "opaque-token", 0, payload, null, null, null, "fake"), out session, out code, out reason);

    // ── the kind check runs first ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AnUndeclaredKindIsRefusedBeforeAdmitRuns()
    {
        var hookRan = false;
        _sessions.Kinds("player", "god");
        _sessions.Admit = (in AdmissionRequest _) =>
        {
            hookRan = true;
            return Admission.Accept(SessionRole.Player);
        };

        var admitted = Admit("wookiee", out var session, out var code, out var reason);

        Assert.Multiple(() =>
        {
            Assert.That(hookRan, Is.False, "an application hook must never see a name the application did not declare");
            Assert.That(admitted, Is.False);
            Assert.That(code, Is.EqualTo(CloseCodes.AuthenticationRejected), "4003: admission refused");
            Assert.That(code, Is.EqualTo(4003));
            Assert.That(reason, Is.Not.Null);
            Assert.That(session, Is.EqualTo(SessionId.None));
            Assert.That(_table.FreeCount, Is.EqualTo(8), "and it costs no slot");
        });
    }

    [Test]
    public void ADeclaredKindReachesTheHookVerbatim()
    {
        string seenKind = null;
        string seenToken = null;
        string seenTransport = null;
        _sessions.Kinds("player", "god");
        _sessions.Admit = (in AdmissionRequest r) =>
        {
            seenKind = r.Kind;
            seenToken = r.Token;
            seenTransport = r.Transport;
            return Admission.Accept(SessionRole.Spectator);
        };

        Assert.That(Admit("god", out _, out _, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(seenKind, Is.EqualTo("god"));
            Assert.That(seenToken, Is.EqualTo("opaque-token"), "the token is handed over uninterpreted");
            Assert.That(seenTransport, Is.EqualTo("fake"));
        });
    }

    [Test]
    public void WithNoDeclaredKindsAClientThatNamesOneIsRefused()
    {
        var hookRan = false;
        _sessions.Admit = (in AdmissionRequest _) =>
        {
            hookRan = true;
            return Admission.Accept(SessionRole.Player);
        };

        Assert.Multiple(() =>
        {
            Assert.That(Admit("player", out _, out var named, out _), Is.False, "an application with no vocabulary has no 'player'");
            Assert.That(named, Is.EqualTo(CloseCodes.AuthenticationRejected));
            Assert.That(hookRan, Is.False);
            Assert.That(Admit(string.Empty, out _, out _, out _), Is.True, "while a client that names nothing is the hook's decision");
            Assert.That(hookRan, Is.True);
        });
    }

    [Test]
    public void WithNoHookEveryoneIsAdmittedAsASpectator()
    {
        Assert.That(Admit(string.Empty, out var session, out _, out _), Is.True);
        _table.BeginTick();

        var opened = _table.Events.AsSpan()[0];

        Assert.Multiple(() =>
        {
            Assert.That(opened.Role, Is.EqualTo(SessionRole.Spectator), "a forgotten hook must not hand world-changing commands to whoever finds the port");
            Assert.That(opened.Session, Is.EqualTo(session));
        });
    }

    // ── the refusal code range ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [TestCase((ushort)0)]
    [TestCase((ushort)1000)]
    [TestCase((ushort)1002)]
    [TestCase((ushort)1013)]
    [TestCase((ushort)4003)]
    [TestCase((ushort)4099)]
    [TestCase((ushort)5000)]
    public void RejectOutsideTheApplicationRangeThrowsAtTheCallSite(ushort code)
        => Assert.That(() => Admission.Reject(code), Throws.InstanceOf<ArgumentOutOfRangeException>(),
            "a refusal that borrows a protocol code changes whether every SDK reconnects");

    [Test]
    [TestCase((ushort)4100)]
    [TestCase((ushort)4500)]
    [TestCase((ushort)4999)]
    public void RejectInsideTheApplicationRangeIsCarriedToTheClient(ushort code)
    {
        _sessions.Admit = (in AdmissionRequest _) => Admission.Reject(code, "banned");

        Assert.That(Admit(string.Empty, out _, out var answered, out var reason), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(answered, Is.EqualTo(code));
            Assert.That(reason, Is.EqualTo("banned"));
        });
    }

    [Test]
    public void TheApplicationRangeIsTheOneTheProtocolDeclares()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CloseCodes.FirstApplicationCode, Is.EqualTo(4100));
            Assert.That(CloseCodes.LastApplicationCode, Is.EqualTo(4999));
        });
    }

    // ── what an acceptance carries ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AcceptCarriesRoleLimitsAndAppDataThroughToOpened()
    {
        var limits = new SessionLimits { AllowDebug = true, BytesPerSecond = 65_536 };
        var appData = new object();
        _sessions.Kinds("god");
        _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Spectator, limits, appData);

        var payload = Encoding.UTF8.GetBytes("hello-app");

        Assert.That(Admit("god", out var session, out _, out _, payload), Is.True);
        Assert.That(_table.BeginTick(), Is.EqualTo(1));

        var batch = _table.Events.AsSpan();
        var e = batch[0];
        var flags = _table.Row(session).Flags;
        var budget = _table.Row(session).BytesPerSecond;

        Assert.Multiple(() =>
        {
            Assert.That(e.Kind, Is.EqualTo(SessionEventKind.Opened));
            Assert.That(e.Session, Is.EqualTo(session));
            Assert.That(e.Role, Is.EqualTo(SessionRole.Spectator));
            Assert.That(e.Limits, Is.SameAs(limits), "the event carries what the hook answered, not a resolved copy of it");
            Assert.That(e.AppData, Is.SameAs(appData), "this is how a system knows which player it is possessing an avatar for");
            Assert.That(e.SessionKind, Is.EqualTo("god"));
            Assert.That(e.HelloPayload.ToArray(), Is.EqualTo(payload), "copied out of the transport's buffer, which is reused");
            Assert.That(e.Principal, Is.Null, "there is no engine-side security in the first version");
            Assert.That(flags & SessionRowFlags.AllowDebug, Is.Not.Zero, "AllowDebug is what gates the DEBUG capability");
            Assert.That(budget, Is.EqualTo(65_536));
            Assert.That(_table.AppData(session), Is.SameAs(appData));
            Assert.That(_table.SessionKind(session), Is.EqualTo("god"));
        });
    }

    [Test]
    public void AnEmptyHelloPayloadCostsNoArray()
    {
        Assert.That(Admit(string.Empty, out _, out _, out _), Is.True);
        _table.BeginTick();

        Assert.That(_table.Events.AsSpan()[0].HelloPayload.IsEmpty, Is.True);
    }

    [Test]
    public void EventsAreDeliveredOnceAndOnlyInTheTickThatTookThem()
    {
        Assert.That(Admit(string.Empty, out _, out _, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(_table.Events.PendingCount, Is.EqualTo(1));
            Assert.That(_table.Events.Count, Is.Zero, "nothing is visible until the tick takes the batch");
        });

        Assert.That(_table.BeginTick(), Is.EqualTo(1));
        Assert.That(_table.BeginTick(), Is.Zero, "and the batch is not delivered a second time");
        Assert.That(_table.Events.AsSpan().Length, Is.Zero);
    }
}
