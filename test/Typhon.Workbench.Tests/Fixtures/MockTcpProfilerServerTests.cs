using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests.Fixtures;

/// <summary>
/// Smoke tests for <see cref="MockTcpProfilerServer"/>. Verifies a plain <see cref="TcpClient"/>
/// can connect, receive an Init frame, and receive at least one Block frame — the exact shape
/// <c>AttachSessionRuntime</c> expects from a real profiler endpoint.
/// </summary>
[TestFixture]
public sealed class MockTcpProfilerServerTests
{
    [Test]
    public async Task Start_AcceptsConnection_SendsInitFrame()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 0 };
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        // Read the Init frame header.
        var header = new byte[LiveStreamProtocol.FrameHeaderSize];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(header, cts.Token);

        var (type, payloadLength) = LiveStreamProtocol.ReadFrameHeader(header);
        Assert.That(type, Is.EqualTo(LiveFrameType.Init), "first frame must be Init");
        Assert.That(payloadLength, Is.GreaterThan(0), "Init payload carries at least the TraceFileHeader");
    }

    [Test]
    public async Task Start_EmitsBlockFrames_AtConfiguredCadence()
    {
        await using var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(50),
            MaxBlocks = 3,
        };
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Skip the Init frame.
        var header = new byte[LiveStreamProtocol.FrameHeaderSize];
        await stream.ReadExactlyAsync(header, cts.Token);
        var (_, initLen) = LiveStreamProtocol.ReadFrameHeader(header);
        var initPayload = new byte[initLen];
        await stream.ReadExactlyAsync(initPayload, cts.Token);

        // Read at least 2 Block frames — confirms the emit loop is firing at cadence.
        for (var i = 0; i < 2; i++)
        {
            await stream.ReadExactlyAsync(header, cts.Token);
            var (blockType, blockLen) = LiveStreamProtocol.ReadFrameHeader(header);
            Assert.That(blockType, Is.EqualTo(LiveFrameType.Block));
            Assert.That(blockLen, Is.GreaterThan(0));
            var blockPayload = new byte[blockLen];
            await stream.ReadExactlyAsync(blockPayload, cts.Token);
        }
    }

    /// <summary>
    /// Scripted mode: no automatic emission, the test owns the stream. Proves <see cref="MockTcpProfilerServer.SendBlockAsync"/>
    /// frames a caller-built record buffer correctly and that block boundaries are the caller's to choose — which is what
    /// makes a "block straddling the arm boundary" test possible at all.
    /// </summary>
    [Test]
    public async Task Scripted_EmitsNothingUntilDriven_ThenHonoursCallerBlockBoundaries()
    {
        await using var server = new MockTcpProfilerServer { Scripted = true };
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        var stream = client.GetStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Init arrives on connect.
        var header = new byte[LiveStreamProtocol.FrameHeaderSize];
        await stream.ReadExactlyAsync(header, cts.Token);
        var (initType, initLen) = LiveStreamProtocol.ReadFrameHeader(header);
        Assert.That(initType, Is.EqualTo(LiveFrameType.Init));
        await stream.ReadExactlyAsync(new byte[initLen], cts.Token);

        await server.WaitForClientAsync(cts.Token);
        Assert.That(client.Available, Is.Zero, "scripted mode must not emit blocks on its own");

        // Two caller-chosen blocks: the tick is deliberately split across them.
        await server.SendBlockAsync(MockRecordFactory.Concat(
            MockRecordFactory.TickStart(1_000),
            MockRecordFactory.SchedulerChunk(1_050, durationTicks: 10)), cts.Token);
        await server.SendBlockAsync(MockRecordFactory.TickEnd(1_500), cts.Token);

        for (var i = 0; i < 2; i++)
        {
            await stream.ReadExactlyAsync(header, cts.Token);
            var (blockType, blockLen) = LiveStreamProtocol.ReadFrameHeader(header);
            Assert.That(blockType, Is.EqualTo(LiveFrameType.Block), $"frame {i} must be a Block");
            await stream.ReadExactlyAsync(new byte[blockLen], cts.Token);
        }
    }

    /// <summary>
    /// Engine-restart simulation: dropping the client leaves the listener up so a reconnect lands a second connection
    /// and a second Init — the shape <c>AttachSessionRuntime</c>'s reconnect loop sees when an app is killed and relaunched.
    /// </summary>
    [Test]
    public async Task DropClient_LeavesListenerUp_SoAReconnectGetsAFreshInit()
    {
        await using var server = new MockTcpProfilerServer { Scripted = true };
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using (var first = new TcpClient())
        {
            await first.ConnectAsync("127.0.0.1", server.Port);
            await server.WaitForClientAsync(cts.Token);
        }
        await server.DropClientAsync();

        using var second = new TcpClient();
        await second.ConnectAsync("127.0.0.1", server.Port);
        await server.WaitForClientAsync(cts.Token);

        var header = new byte[LiveStreamProtocol.FrameHeaderSize];
        await second.GetStream().ReadExactlyAsync(header, cts.Token);
        var (type, _) = LiveStreamProtocol.ReadFrameHeader(header);
        Assert.That(type, Is.EqualTo(LiveFrameType.Init), "the reconnect must receive a fresh Init frame");
        Assert.That(server.ConnectionCount, Is.EqualTo(2), "both connections must have been accepted");
    }
}
