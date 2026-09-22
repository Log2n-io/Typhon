using System;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine;

namespace Typhon.Subscriptions.AspNetCore;

/// <summary>
/// The transport an ASP.NET Core host registers: it owns no listener of its own, because Kestrel is the listener.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a transport at all, when the host already listens.</b> <see cref="ISubscriptionTransport"/> is how the engine hands out its acceptor, and the
/// acceptor is what turns a link into a session. The endpoint needs it on every upgrade, so something has to hold it between <c>Start</c> and the first
/// request — and holding it here, behind the same interface the TCP transport implements, keeps the engine's side of the seam identical for both.
/// </para>
/// <para>
/// <b>Started once, by the host, after <c>TyphonRuntime.Start()</c>.</b> An endpoint mapped before that answers 503: the catalog a client negotiates against
/// does not exist yet, so accepting would mean admitting a session the runtime cannot describe.
/// </para>
/// </remarks>
public sealed class WebSocketSubscriptionTransport : ISubscriptionTransport
{
    private ISubscriptionAcceptor _acceptor;
    private int _stopped;

    /// <summary>The acceptor the runtime handed over, or <see langword="null"/> before it started.</summary>
    public ISubscriptionAcceptor Acceptor => Volatile.Read(ref _acceptor);

    /// <summary>Whether the host has stopped this transport, after which no upgrade is accepted.</summary>
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <inheritdoc />
    public void Start(ISubscriptionAcceptor acceptor)
    {
        ArgumentNullException.ThrowIfNull(acceptor);

        // Release: an endpoint on another thread reads this without a lock, and it must see a fully constructed acceptor or none.
        Volatile.Write(ref _acceptor, acceptor);
    }

    /// <inheritdoc />
    /// <remarks>
    /// There is nothing to close: the connections belong to Kestrel, which drains them on shutdown, and each one's own close path runs through
    /// <see cref="WebSocketLink.Close"/> when the engine ends its session. What this does is stop accepting, so a request arriving during shutdown is refused
    /// rather than given a session that is about to be torn down.
    /// </remarks>
    public ValueTask StopAsync()
    {
        Volatile.Write(ref _stopped, 1);
        Volatile.Write(ref _acceptor, null);
        return ValueTask.CompletedTask;
    }
}
