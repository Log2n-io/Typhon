using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// #WB-01 — an attach session reads the engine's schema off the Init frame, and advertises it.
/// </summary>
/// <remarks>
/// <para>
/// Before this, <c>TcpExporter.BuildInitPayload</c> wrote six zero counts and <c>AttachSession.StaticSchemaProvider</c> returned <see langword="null"/>
/// unconditionally, so a live attach was anonymous: every panel that resolves a component name, a field offset or an archetype's composition
/// dead-ended. Both halves changed, so both halves are asserted here — the runtime parses the tables, and the session projects them as schema plus the
/// capability the shell gates the Schema Explorer on.
/// </para>
/// <para>
/// The negative case matters as much: an engine that predates the change still sends empty sections, and a provider over an empty schema renders as
/// "schema present but empty", which reads as data loss rather than as a missing feature. The session must stay capability-free there.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AttachSessionSchemaTests
{
    [Test]
    public async Task AnInitFrameCarryingSchema_MakesTheSessionServeIt()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 0, IncludeSchema = true };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = await AttachSessionRuntime.StartAsync($"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);
        using var session = new AttachSession(Guid.NewGuid(), $"127.0.0.1:{server.Port}", runtime);

        await runtime.MetadataReady.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(session.StaticSchemaProvider, Is.Not.Null, "the Init frame carried static-structure tables");
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Schema), "the shell gates the Schema Explorer on this");
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Profiler), "a live attach still profiles");

        // Resolving a component by name through the provider is what every schema panel does first; a provider built over the wrong arrays would be
        // non-null and answer nothing, which is the failure this catches and a null-check would not.
        var components = session.StaticSchemaProvider.ListComponents();
        Assert.That(components, Has.Length.EqualTo(1));
        Assert.That(components[0].TypeName, Is.EqualTo(MockTcpProfilerServer.SchemaComponentName));

        var schema = session.StaticSchemaProvider.GetComponentSchema(MockTcpProfilerServer.SchemaComponentName);
        Assert.That(schema.Fields, Has.Length.EqualTo(2), "the field table crossed the socket with the component");
        Assert.That(schema.Fields[1].Offset, Is.EqualTo(4), "and the offsets the layout view draws survived the round trip");
    }

    [Test]
    public async Task AnEngineThatSendsNoSchema_LeavesTheSessionWithout()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 0 };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = await AttachSessionRuntime.StartAsync($"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);
        using var session = new AttachSession(Guid.NewGuid(), $"127.0.0.1:{server.Port}", runtime);

        await runtime.MetadataReady.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(session.StaticSchemaProvider, Is.Null, "empty sections must surface as unavailable, not as an empty schema");
        Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Schema));
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Profiler));
    }

    /// <summary>
    /// The capability is there the instant <c>StartAsync</c> returns — no wait of the caller's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the two tests above could not catch, and it was broken in production while they were green.</b> Both of them
    /// <c>await runtime.MetadataReady</c> before asserting — a wait <c>SessionsController.CreateAttachSession</c> never performed. It built the
    /// <c>SessionDto</c> as soon as the TCP connect succeeded, so the created session advertised <c>profiler</c> alone and acquired <c>schema</c>
    /// milliseconds later, on the read loop, with nobody left to ask. Measured on the wire against the SWG demo: <c>profiler</c> at create,
    /// <c>schema, profiler</c> three seconds on. The SPA seeds its store from the create response and re-reads a session only on profile
    /// attach/detach and pause flips, so the Schema Explorer stayed absent from the View menu and the palette for the session's whole life —
    /// in exactly the remote-attach mode #WB-01 exists to serve.
    /// </para>
    /// <para>
    /// So the assertion deliberately performs <b>no</b> synchronisation. Awaiting anything here would restore the very blind spot that let the bug
    /// ship: the point is that the contract holds for a caller who just takes what <c>StartAsync</c> hands back.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheSchemaCapabilityIsPresentTheMomentStartAsyncReturns()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 0, IncludeSchema = true };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = await AttachSessionRuntime.StartAsync($"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);
        using var session = new AttachSession(Guid.NewGuid(), $"127.0.0.1:{server.Port}", runtime);

        // No MetadataReady wait. This is the projection a controller makes on the line after StartAsync returns.
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Schema),
            "StartAsync must settle the Init handshake before returning, or every created attach session is projected schema-less");
        Assert.That(session.StaticSchemaProvider, Is.Not.Null);
    }

    /// <summary>
    /// A peer that accepts the socket and then says nothing still yields a usable session, after the handshake timeout rather than for ever.
    /// </summary>
    /// <remarks>
    /// The wait added for the test above is bounded precisely so this case stays a live profiler session instead of a hung attach. `MaxBlocks = 0`
    /// with <c>SuppressInit</c> is the shape of an engine whose exporter is wedged, or a port answered by something that is not Typhon at all.
    /// </remarks>
    [Test]
    public async Task APeerThatNeverSendsInit_StillYieldsAProfilerSession()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 0, SuppressInit = true };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // A short handshake timeout: the behaviour under test is what happens WHEN it fires, not how long production waits.
        var runtime = await AttachSessionRuntime.StartAsync(
            Guid.NewGuid(), $"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token, CaptureMode.Everything, TimeSpan.FromMilliseconds(150));
        using var session = new AttachSession(Guid.NewGuid(), $"127.0.0.1:{server.Port}", runtime);

        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Profiler), "the socket connected; the session is real");
        Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Schema), "nothing arrived to build a schema from");
        Assert.That(session.StaticSchemaProvider, Is.Null);
    }
}
