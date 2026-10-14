using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>The TCP transport's framing under the client's real use: several loops sending at once.</summary>
[TestFixture]
public class TcpClientTransportTests
{
    /// <summary>
    /// The ping loop, commands and a refusal's BYE all send. Each send is a prefix then a body over one shared header buffer, so two at once interleaved
    /// into a stream the server reads as garbage; every frame must arrive whole, its length matching its body.
    /// </summary>
    [Test]
    public async Task ConcurrentSendsArriveAsWholeFrames()
    {
        const int Senders = 64;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = listener.AcceptSocketAsync();
        await using var transport = new TcpClientTransport("127.0.0.1", port);
        var connect = transport.ConnectAsync(default);
        using var server = await accept;
        await using var stream = new NetworkStream(server);

        var preamble = new byte[4];
        await stream.ReadExactlyAsync(preamble);
        await stream.WriteAsync(ProtocolConstants.TcpPreamble.ToArray());
        await connect;

        // Message i is i + 1 bytes, every byte equal to i: a frame from two senders cannot satisfy both rules.
        var sends = new Task[Senders];
        for (var i = 0; i < Senders; i++)
        {
            var message = new byte[i + 1];
            Array.Fill(message, (byte)i);
            sends[i] = Task.Run(async () => await transport.SendAsync(message, default));
        }

        await Task.WhenAll(sends);

        var seen = new bool[Senders];
        var header = new byte[4];
        for (var n = 0; n < Senders; n++)
        {
            await stream.ReadExactlyAsync(header);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            Assert.That(length, Is.InRange(1, Senders), $"frame {n}: a length no sender wrote");
            var body = new byte[length];
            await stream.ReadExactlyAsync(body);
            Assert.That(Array.TrueForAll(body, b => b == length - 1), Is.True, $"frame {n}: a body mixing two senders");
            seen[length - 1] = true;
        }

        Assert.That(Array.TrueForAll(seen, s => s), Is.True, "every sender's frame arrived");
    }
}
