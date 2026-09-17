using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// An <see cref="ISubscriptionLink"/> over a queue: the transport every later slice and the differential oracle drive the engine with.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a product seam, not scaffolding.</b> <c>design/Subscriptions/04-transport.md § 2</c> already specifies in-process links — an embedded application,
/// a test, or the oracle implements <see cref="ISubscriptionLink"/> over a queue and receives real frames, and nothing in the engine distinguishes it from a
/// remote session. That is why this fake has no shortcuts into the engine: it sees exactly the bytes a socket would.
/// </para>
/// <para>
/// <b>It copies on send, deliberately.</b> The engine hands out native memory it recycles the moment the returned task completes, so a link that stored the
/// <see cref="ReadOnlyMemory{T}"/> would be reading freed memory by the time a test looked at it — which is the bug this fake most needs to not hide.
/// </para>
/// <para>
/// <b>Delay and throttling are what make backpressure testable.</b> <see cref="Delay"/> models a fat link (AC-16's 200 ms-RTT god client, which must never be
/// degraded) and <see cref="BytesPerSecond"/> models a thin one (AC-16's 10 KB/s client, which must be closed 1013 within five seconds). Both leave the
/// message stream itself exact.
/// </para>
/// </remarks>
internal sealed class InProcessLink : ISubscriptionLink
{
    private readonly ConcurrentQueue<byte[]> _messages = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private int _closes;

    /// <summary>How long every send waits before completing. Zero — the default — completes synchronously.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>A throughput ceiling in bytes per second, added to <see cref="Delay"/>. Zero — the default — is unthrottled.</summary>
    public int BytesPerSecond { get; set; }

    /// <summary>What a send should fail with, or <see langword="null"/> for a link that works.</summary>
    public Exception FailSendWith { get; set; }

    /// <summary>
    /// The connection to report the close to, as a real transport does.
    /// </summary>
    /// <remarks>
    /// Wired by the test after <see cref="ISubscriptionAcceptor.Accept"/> returns. A transport that never calls
    /// <see cref="ISubscriptionConnection.OnClosed"/> leaks a session slot for the life of the process, so the fake does call it — including for a close the
    /// engine itself asked for, which is the case a hand-written fake is most likely to skip.
    /// </remarks>
    public ISubscriptionConnection Connection { get; set; }

    /// <inheritdoc />
    public bool SupportsUnreliable => false;

    /// <summary>Whether <see cref="Close"/> has been called.</summary>
    public bool IsClosed => Volatile.Read(ref _closes) != 0;

    /// <summary>How many times <see cref="Close"/> was called. More than one is a bug in the engine's close path, not in the link.</summary>
    public int CloseCount => Volatile.Read(ref _closes);

    /// <summary>The code of the first close.</summary>
    public ushort CloseCode { get; private set; }

    /// <summary>The reason of the first close.</summary>
    public string CloseReason { get; private set; }

    /// <summary>Messages the engine tried to send after the link was closed. A real link would drop them too.</summary>
    public int DroppedAfterClose { get; private set; }

    /// <summary>How many messages are waiting to be read.</summary>
    public int PendingCount => _messages.Count;

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (FailSendWith != null)
        {
            return ValueTask.FromException(FailSendWith);
        }

        if (IsClosed)
        {
            DroppedAfterClose++;
            return ValueTask.CompletedTask;
        }

        var copy = message.ToArray();
        var wait = Delay;
        if (BytesPerSecond > 0)
        {
            wait += TimeSpan.FromSeconds((double)copy.Length / BytesPerSecond);
        }

        if (wait <= TimeSpan.Zero)
        {
            Deliver(copy);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(SlowSendAsync(copy, wait, ct));
    }

    /// <inheritdoc />
    public bool TrySendUnreliable(ReadOnlySpan<byte> datagram) => false;

    /// <inheritdoc />
    public void Close(ushort code, string reason)
    {
        if (Interlocked.Increment(ref _closes) != 1)
        {
            return;
        }

        CloseCode = code;
        CloseReason = reason;
        Connection?.OnClosed(code, null);
    }

    /// <summary>Takes the next message the engine sent.</summary>
    /// <param name="message">The message's bytes.</param>
    /// <param name="timeoutMs">How long to wait for one.</param>
    /// <returns><see langword="false"/> when none arrived in time.</returns>
    public bool TryTake(out byte[] message, int timeoutMs = 2000)
    {
        if (_arrived.Wait(timeoutMs))
        {
            return _messages.TryDequeue(out message);
        }

        message = null;
        return false;
    }

    /// <summary>Takes the next message, failing the test when none arrives.</summary>
    /// <param name="timeoutMs">How long to wait.</param>
    /// <returns>The message.</returns>
    public byte[] Take(int timeoutMs = 2000)
        => TryTake(out var message, timeoutMs) ? message : throw new TimeoutException("the engine sent no message");

    /// <summary>The type byte of the next message, without consuming it.</summary>
    /// <param name="type">The type byte.</param>
    /// <returns><see langword="false"/> when nothing is queued.</returns>
    public bool TryPeekType(out byte type)
    {
        if (_messages.TryPeek(out var message) && message.Length > 0)
        {
            type = message[0];
            return true;
        }

        type = 0;
        return false;
    }

    private async Task SlowSendAsync(byte[] copy, TimeSpan wait, CancellationToken ct)
    {
        await Task.Delay(wait, ct).ConfigureAwait(false);
        Deliver(copy);
    }

    private void Deliver(byte[] copy)
    {
        _messages.Enqueue(copy);
        _arrived.Release();
    }
}

/// <summary>
/// A stand-in for the replication runtime, holding what a connection reads from it and nothing else.
/// </summary>
/// <remarks>
/// <c>SubscriptionsRuntime</c> (P1-03) is what implements <see cref="ISubscriptionsHost"/> in production; this is the same surface with a catalog a test
/// chooses and a tick a test advances, so the handshake can be driven through every branch without a running engine.
/// </remarks>
internal sealed class FakeSubscriptionsHost : ISubscriptionsHost
{
    /// <inheritdoc />
    public SubscriptionsSessions Sessions { get; init; }

    /// <inheritdoc />
    public SessionTable SessionTable { get; init; }

    /// <inheritdoc />
    public byte[] CatalogJson { get; set; } = "{\"protocol\":{\"major\":2,\"minor\":0}}"u8.ToArray();

    /// <inheritdoc />
    public ulong CatalogHash { get; set; } = 0xD1CEF00DBAADF00DUL;

    /// <inheritdoc />
    public bool HasMetrics { get; set; } = true;

    /// <inheritdoc />
    public bool IsAccepting { get; set; } = true;

    /// <inheritdoc />
    public uint CurrentTick { get; set; } = 1234;

    /// <inheritdoc />
    public uint TickPeriodUs { get; set; } = 100_000;

    /// <inheritdoc />
    public uint MicrosecondsIntoTick { get; set; } = 99_999;

    /// <summary>How many <c>COMMANDS</c> messages reached ingress. P1-05 is what turns them into commands.</summary>
    public int CommandMessages { get; private set; }

    /// <inheritdoc />
    public void OnCommands(SessionId session, ReadOnlySpan<byte> message) => CommandMessages++;
}

/// <summary>
/// Builds the client-to-server messages a test sends, through <c>Typhon.Protocol</c>'s own writers.
/// </summary>
/// <remarks>
/// Never a hand-written byte array: a test that spells the wire out itself is green in the same build as a red encoder, which is the trap
/// <c>scripts/audit-rule-coverage.py</c> was written about (LOG-06's hand-constructed bytes).
/// </remarks>
internal static class ClientMessages
{
    /// <summary>Encodes a <c>HELLO</c>.</summary>
    /// <param name="kind">The session kind.</param>
    /// <param name="caps">The capabilities asked for.</param>
    /// <param name="clientCatalogHash">The catalog hash the client already holds, or zero.</param>
    /// <param name="token">The opaque credential.</param>
    /// <param name="major">The protocol major to claim.</param>
    /// <param name="payload">The application payload.</param>
    /// <returns>The message.</returns>
    public static byte[] Hello(string kind = "", Capabilities caps = Capabilities.None, ulong clientCatalogHash = 0, string token = "opaque",
        ushort major = ProtocolConstants.Major, byte[] payload = null)
    {
        var hello = new HelloMessage
        {
            Major = major,
            Minor = ProtocolConstants.Minor,
            Caps = caps,
            Kind = kind,
            Token = token,
            ClientCatalogHash = clientCatalogHash,
            HelloPayload = payload ?? [],
        };

        return Encode(hello.Write);
    }

    /// <summary>Encodes a <c>PING</c>.</summary>
    /// <param name="clientMs">The client's own clock reading, which the server echoes.</param>
    /// <param name="lastAppliedTick">The newest tick the client has applied.</param>
    /// <returns>The message.</returns>
    public static byte[] Ping(uint clientMs, uint lastAppliedTick) => Encode(new PingMessage(clientMs, lastAppliedTick).Write);

    /// <summary>Encodes a <c>BYE</c>.</summary>
    /// <param name="code">1000, or a code in 4000-4999.</param>
    /// <returns>The message.</returns>
    public static byte[] Bye(ushort code = CloseCodes.Normal) => Encode(new ByeMessage(code).Write);

    /// <summary>Encodes a <c>COMMANDS</c> message carrying no commands' worth of bytes a test needs to decode.</summary>
    /// <param name="bytes">How long the whole message should be.</param>
    /// <returns>The message.</returns>
    public static byte[] Commands(int bytes)
    {
        var message = new byte[bytes];
        message[0] = MessageTypes.Commands;
        return message;
    }

    private delegate void Writer(ref WireWriter writer);

    private static byte[] Encode(Writer write)
    {
        Span<byte> scratch = stackalloc byte[ProtocolConstants.HelloMaxBytes];
        var writer = new WireWriter(scratch);
        write(ref writer);
        return writer.Written.ToArray();
    }
}
