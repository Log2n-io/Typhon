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
}
