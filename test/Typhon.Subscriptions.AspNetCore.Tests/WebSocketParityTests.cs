using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime.Subscriptions;
using Typhon.Protocol;

namespace Typhon.Subscriptions.AspNetCore.Tests;

/// <summary>
/// AC-15's second half (#1040): the same script over a WebSocket and over an in-process link produces byte-identical protocol messages.
/// </summary>
/// <remarks>
/// <para>
/// <b>TCP against WebSocket, through one oracle.</b> <c>TcpTransportTests.ASocketClientAndAnInProcessClientProduceIdenticalProtocolMessages</c> holds the
/// TCP transport to the in-process link; this holds the WebSocket transport to the same in-process link, over the same fake host and the same script, so
/// the two transports produce the same bytes. A direct comparison of two transports on one running server could not be made as bytes: a session id, a
/// tick and a microsecond offset differ between two connections, and normalising them away would leave a test that could not fail for the reason it
/// exists.
/// </para>
/// <para>
/// <b>The engine's own connection, behind the real endpoint.</b> The acceptor is the engine's <c>SubscriptionAcceptor</c> over the fake host the engine's
/// tests use (linked in from <c>InProcessLink.cs</c>), so what reaches the WebSocket is what the engine wrote; the transport may add framing and nothing else.
/// </para>
/// </remarks>
[TestFixture]
public class WebSocketParityTests
{
    private const int TimeoutMs = 5000;

    [Test]
    public async Task AWebSocketClientAndAnInProcessClientProduceIdenticalProtocolMessages()
    {
        var script = new[]
        {
            ClientMessages.Hello("god", Capabilities.Stats),
            ClientMessages.Ping(0x0BADF00D, 1234),
            ClientMessages.Bye(),
        };

        var inProcess = RunScriptInProcess(script);
        Assert.That(inProcess, Has.Count.EqualTo(2), "the in-process oracle's WELCOME and PONG");

        var overWebSocket = await RunScriptOverWebSocketAsync(script).ConfigureAwait(false);

        Assert.That(overWebSocket, Has.Count.EqualTo(inProcess.Count), "the two arms produced a different number of messages");

        var socketToken = WelcomeMessage.Parse(overWebSocket[0]).ResumeToken;
        var oracleToken = WelcomeMessage.Parse(inProcess[0]).ResumeToken;
        Assert.That(socketToken, Is.Not.EqualTo(oracleToken), "a resume token is a per-session secret, and two sessions sharing one is the hazard");

        MaskResumeToken(overWebSocket[0]);
        MaskResumeToken(inProcess[0]);
        Assert.Multiple(() =>
        {
            for (var i = 0; i < inProcess.Count; i++)
            {
                Assert.That(overWebSocket[i], Is.EqualTo(inProcess[i]),
                    $"message {i} (type 0x{inProcess[i][0]:X2}) differs between the WebSocket and the in-process link");
            }

            Assert.That(overWebSocket[0][0], Is.EqualTo(MessageTypes.Welcome));
            Assert.That(overWebSocket[1][0], Is.EqualTo(MessageTypes.Pong));
        });
    }

    /// <summary>Drives the script through the real endpoint: an HTTP upgrade, the subprotocol, one binary message per protocol message.</summary>
    private static async Task<List<byte[]>> RunScriptOverWebSocketAsync(byte[][] script)
    {
        using var world = new FakeWorld();
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddTyphonSubscriptions(o => o.AllowAnyOrigin());
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapTyphonSubscriptions("/ws"));
                }))
            .StartAsync()
            .ConfigureAwait(false);
        host.Services.GetRequiredService<WebSocketSubscriptionTransport>().Start(new SubscriptionAcceptor(world.Host));

        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Sec-WebSocket-Protocol"] = ProtocolConstants.WebSocketSubprotocol;
        using var socket = await client.ConnectAsync(new Uri("http://localhost/ws"), CancellationToken.None).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeoutMs);

        var received = new List<byte[]>();
        await socket.SendAsync(script[0], WebSocketMessageType.Binary, endOfMessage: true, timeout.Token).ConfigureAwait(false);
        received.Add(await ReceiveAsync(socket, timeout.Token).ConfigureAwait(false));
        await socket.SendAsync(script[1], WebSocketMessageType.Binary, endOfMessage: true, timeout.Token).ConfigureAwait(false);
        received.Add(await ReceiveAsync(socket, timeout.Token).ConfigureAwait(false));
        await socket.SendAsync(script[2], WebSocketMessageType.Binary, endOfMessage: true, timeout.Token).ConfigureAwait(false);

        Assert.That(await ReceiveAsync(socket, timeout.Token).ConfigureAwait(false), Is.Null, "the WebSocket arm was sent a message after BYE");
        return received;
    }

    /// <summary>One whole binary message, or <see langword="null"/> once the server closes.</summary>
    private static async Task<byte[]> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Binary), "the protocol is binary messages only");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    /// <summary>Drives the same script through an in-process link against an identically configured fake host.</summary>
    private static List<byte[]> RunScriptInProcess(byte[][] script)
    {
        using var world = new FakeWorld();
        var acceptor = new SubscriptionAcceptor(world.Host);
        var link = new InProcessLink();
        var connection = acceptor.Accept(link, new LinkInfo { Transport = "fake" });
        link.Connection = connection;

        var received = new List<byte[]>();
        foreach (var message in script)
        {
            connection.OnMessage(message);
            while (link.TryTake(out var reply, 50))
            {
                received.Add(reply);
            }
        }

        return received;
    }

    /// <summary>Zeroes a <c>WELCOME</c>'s resume token in place: type, major, minor, caps and the session id come first, then its sixteen bytes.</summary>
    private static void MaskResumeToken(byte[] welcome)
    {
        const int Offset = sizeof(byte) + sizeof(ushort) + sizeof(ushort) + sizeof(uint) + sizeof(uint);
        Array.Clear(welcome, Offset, 16);
    }

    /// <summary>A fake replication host and the resources its session table needs, configured as the TCP parity test's.</summary>
    private sealed class FakeWorld : IDisposable
    {
        private readonly ResourceRegistry _registry;
        private readonly MemoryAllocator _allocator;
        private readonly SessionTable _table;

        public FakeWorld()
        {
            _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "WebSocketParityTests" });
            _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "WebSocketParityTestAllocator" });
            var sessions = new SubscriptionsSessions();
            sessions.Kinds("god");
            _table = new SessionTable("Sessions", _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = 4 }, sessions.SessionEvents);
            Host = new FakeSubscriptionsHost { Sessions = sessions, SessionTable = _table, AnswerPongs = true };
        }

        public FakeSubscriptionsHost Host { get; }

        public void Dispose()
        {
            _table.Dispose();
            _allocator.Dispose();
            _registry.Dispose();
        }
    }
}
