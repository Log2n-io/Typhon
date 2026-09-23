using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Subscriptions.AspNetCore;

/// <summary>
/// What the WebSocket endpoint refuses, and how long it waits — the operator's rails for the browser door.
/// </summary>
/// <remarks>
/// Everything a client is allowed to do once it is connected comes from the catalog's <c>limits</c>, which the engine owns. What is here is what has to be
/// decided before a session exists at all: who may open one, and how a dead connection is noticed.
/// </remarks>
[PublicAPI]
public sealed class TyphonSubscriptionsOptions
{
    /// <summary>
    /// The origins a browser may connect from. Empty refuses to start.
    /// </summary>
    /// <remarks>
    /// <b>Empty is a refusal, not "allow everything".</b> ASP.NET Core's own <c>WebSocketOptions.AllowedOrigins</c> treats an empty list as no restriction,
    /// which is the right default for a middleware that many applications use with cookie-less APIs and the wrong one here: a replication socket carries a
    /// live view of the world, and a WebSocket is not subject to the same-origin policy. An operator who really wants any origin says so with
    /// <see cref="AllowAnyOrigin"/>, and then it is in their configuration rather than in their omission.
    /// </remarks>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>Whether <see cref="AllowAnyOrigin"/> was called.</summary>
    public bool AnyOriginAllowed { get; private set; }

    /// <summary>How often the endpoint sends a WebSocket ping frame. Default: 15 s.</summary>
    /// <remarks>
    /// Set explicitly because the framework's default is two minutes, which is long enough that a client whose network vanished holds a session slot, its
    /// frames and an ingress ring for that whole time. It is the transport's liveness check, under the protocol's own <c>PING</c>, and the two answer
    /// different questions: this one notices a dead TCP connection, the protocol's notices a client that is alive but no longer applying frames.
    /// </remarks>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an unanswered ping frame may go unanswered before the connection is aborted. Default: 15 s.</summary>
    /// <remarks>The framework's default is infinite, which makes the interval above a heartbeat nobody ever checks.</remarks>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The largest client message this endpoint reads after the first one, in bytes. Default: <see cref="ProtocolConstants.HelloMaxBytes"/>.
    /// </summary>
    /// <remarks>
    /// The transport's own ceiling, and deliberately the looser of the two: the session's <c>clientMessageBytes</c> is the protocol's limit and only the engine
    /// knows it, so a message that passes here can still be refused 1009 by the connection. What this number buys is that a client cannot make the endpoint
    /// read an unbounded message before anyone has looked at it. The first message is <c>HELLO</c> and is bounded by
    /// <see cref="ProtocolConstants.HelloMaxBytes"/> whatever this says, because a client must be able to send a token before it has been told any limit.
    /// </remarks>
    public int MaxInboundMessageBytes { get; set; } = ProtocolConstants.HelloMaxBytes;

    /// <summary>Allows every origin, deliberately.</summary>
    /// <returns>These options, for chaining.</returns>
    public TyphonSubscriptionsOptions AllowAnyOrigin()
    {
        AnyOriginAllowed = true;
        return this;
    }

    /// <summary>Adds an allowed origin.</summary>
    /// <param name="origin">The origin, as a browser sends it: scheme, host and port, with no trailing slash.</param>
    /// <returns>These options, for chaining.</returns>
    public TyphonSubscriptionsOptions AllowOrigin(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        AllowedOrigins.Add(origin.TrimEnd('/'));
        return this;
    }

    /// <summary>Whether a request carrying <paramref name="origin"/> may open a session.</summary>
    /// <param name="origin">The request's <c>Origin</c> header, or <see langword="null"/> when it has none.</param>
    /// <returns><see langword="true"/> when the endpoint should accept.</returns>
    /// <remarks>
    /// A missing <c>Origin</c> is allowed: only browsers send one, and a native client over WebSocket is a legitimate caller that never will. The header is a
    /// browser-side control, so treating its absence as a refusal would lock out every non-browser client while stopping no attacker.
    /// </remarks>
    public bool IsOriginAllowed(string origin)
    {
        if (AnyOriginAllowed || string.IsNullOrEmpty(origin))
        {
            return true;
        }

        for (var i = 0; i < AllowedOrigins.Count; i++)
        {
            if (string.Equals(AllowedOrigins[i], origin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Throws when the options cannot serve an endpoint.</summary>
    /// <exception cref="InvalidOperationException">No origin is allowed and <see cref="AllowAnyOrigin"/> was not called.</exception>
    internal void Validate()
    {
        if (MaxInboundMessageBytes < ProtocolConstants.HelloMaxBytes)
        {
            throw new InvalidOperationException(
                $"MaxInboundMessageBytes is {MaxInboundMessageBytes}, below the {ProtocolConstants.HelloMaxBytes}-byte HELLO the protocol allows: no client "
                + "could complete a handshake carrying a token.");
        }

        if (!AnyOriginAllowed && AllowedOrigins.Count == 0)
        {
            throw new InvalidOperationException(
                "No allowed origin was configured for the Typhon subscriptions endpoint. Add the origins your client is served from with "
                + "AllowOrigin(\"https://…\"), or call AllowAnyOrigin() to accept every origin deliberately.");
        }
    }
}
