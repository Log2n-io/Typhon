using NUnit.Framework;
using System;
using System.IO;
using Typhon.Client;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// P1-20b: which closes are worth reconnecting after, and how long to wait.
/// </summary>
/// <remarks>
/// The table is small enough to test exhaustively and important enough to deserve it. Getting a code on the wrong side is not a small error in either
/// direction: treating a transient close as fatal makes a client that never comes back from a server restart, and treating a fatal one as transient makes a
/// reconnect loop that hammers a server which has already said no — and looks, from the outside, exactly like a network problem.
/// </remarks>
[TestFixture]
sealed class ReconnectPolicyTests
{
    /// <summary>Every code whose condition can change on its own reconnects.</summary>
    [TestCase(CloseCodes.GoingAway)]
    [TestCase(CloseCodes.InternalError)]
    [TestCase(CloseCodes.TryAgainLater)]
    [TestCase(CloseCodes.PolicyViolation)]
    [TestCase(CloseCodes.NoAcknowledgement)]
    [TestCase(CloseCodes.HelloTimeout)]
    public void TransientClosesReconnect(ushort code) =>
        Assert.That(ReconnectPolicy.DecideFor(code), Is.EqualTo(ReconnectDecision.Retry), $"{code} is transient; a client that gives up never comes back");

    /// <summary>Every code the client would earn again by doing the same thing stops.</summary>
    [TestCase(CloseCodes.ProtocolError)]
    [TestCase(CloseCodes.MalformedPayload)]
    [TestCase(CloseCodes.MessageTooBig)]
    [TestCase(CloseCodes.AuthenticationRejected)]
    public void PermanentClosesDoNotReconnect(ushort code) =>
        Assert.That(ReconnectPolicy.DecideFor(code), Is.EqualTo(ReconnectDecision.Stop),
            $"{code} would happen again identically; retrying is a hot loop that reads as a network fault");

    /// <summary>A normal close and the application range are the application's call, not the SDK's.</summary>
    [TestCase(CloseCodes.Normal)]
    [TestCase((ushort)4100)]
    [TestCase((ushort)4999)]
    public void ApplicationClosesAreHandedBack(ushort code) =>
        Assert.That(ReconnectPolicy.DecideFor(code), Is.EqualTo(ReconnectDecision.Application));

    /// <summary>Reconnect can be switched off entirely, whatever the code says.</summary>
    [Test]
    public void ReconnectCanBeDisabled()
    {
        var policy = new ReconnectPolicy(new ClientOptions { Endpoint = new Uri("ws://localhost:1/"), Reconnect = false });
        Assert.That(policy.Decide(CloseCodes.TryAgainLater), Is.EqualTo(ReconnectDecision.Stop));
    }

    /// <summary>The attempt cap ends the retries even for a transient code.</summary>
    [Test]
    public void TheAttemptCapStopsRetrying()
    {
        var policy = new ReconnectPolicy(new ClientOptions { Endpoint = new Uri("ws://localhost:1/"), MaxReconnectAttempts = 2 });

        Assert.That(policy.Decide(CloseCodes.GoingAway), Is.EqualTo(ReconnectDecision.Retry));
        policy.NextDelay();
        Assert.That(policy.Decide(CloseCodes.GoingAway), Is.EqualTo(ReconnectDecision.Retry));
        policy.NextDelay();
        Assert.That(policy.Decide(CloseCodes.GoingAway), Is.EqualTo(ReconnectDecision.Stop), "the third attempt exceeds a cap of two");
    }

    /// <summary>
    /// Backoff grows and is capped, and two policies with different jitter do not agree.
    /// </summary>
    /// <remarks>
    /// The disagreement is the assertion that matters. Exponential backoff alone synchronises every client that lost the same server onto the same retry
    /// instants, so a restart becomes a thundering herd; the jitter is the only thing preventing it, and a jitter that happened to be constant would pass
    /// every other test here.
    /// </remarks>
    [Test]
    public void BackoffGrowsIsCappedAndIsJittered()
    {
        var options = new ClientOptions
        {
            Endpoint = new Uri("ws://localhost:1/"),
            InitialBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromSeconds(1),
        };

        var first = new ReconnectPolicy(options, seed: 1);
        var second = new ReconnectPolicy(options, seed: 2);

        var differ = false;
        for (var i = 0; i < 12; i++)
        {
            var a = first.NextDelay();
            var b = second.NextDelay();
            Assert.That(a, Is.LessThanOrEqualTo(options.MaxBackoff), "a delay exceeded the ceiling");
            Assert.That(a, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
            differ |= a != b;
        }

        Assert.That(differ, Is.True, "two policies produced identical delays, so the backoff is not jittered and clients would retry in lockstep");
        Assert.That(first.Attempt, Is.EqualTo(12));

        first.NoteConnected();
        Assert.That(first.Attempt, Is.Zero, "a successful handshake has to forget the failures, or a long-lived session retries instantly after its first drop");
    }
}

/// <summary>P1-20b: a recorded session round-trips, and a truncated one is refused rather than half-applied.</summary>
[TestFixture]
sealed class RecorderTests
{
    /// <summary>Messages come back in the order they went in, byte for byte.</summary>
    [Test]
    public void ARecordingRoundTrips()
    {
        var recorder = new Recorder();
        recorder.Record([1, 2, 3]);
        recorder.Record([]);
        recorder.Record([9]);

        using var stream = new MemoryStream();
        recorder.Save(stream);
        stream.Position = 0;
        var loaded = Recorder.Load(stream);

        Assert.That(loaded.Count, Is.EqualTo(3));
        Assert.That(loaded.Messages[0], Is.EqualTo(new byte[] { 1, 2, 3 }).AsCollection);
        Assert.That(loaded.Messages[1], Is.Empty, "an empty message is a message; dropping it would shift everything after it");
        Assert.That(loaded.Messages[2], Is.EqualTo(new byte[] { 9 }).AsCollection);
    }

    /// <summary>A file that is not a recording fails at its header, not at the first message.</summary>
    [Test]
    public void AForeignFileIsRefusedAtTheHeader()
    {
        using var stream = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => Recorder.Load(stream));
    }

    /// <summary>A truncated recording throws rather than returning the messages that survived.</summary>
    /// <remarks>
    /// Returning a prefix would be the worst outcome: frames do not commute, so a partial replay produces a world that is not wrong in any single field and
    /// is not the recorded one either — the divergence has no symptom at the point it happens.
    /// </remarks>
    [Test]
    public void ATruncatedRecordingIsRefused()
    {
        var recorder = new Recorder();
        recorder.Record([1, 2, 3, 4, 5]);
        recorder.Record([6, 7, 8]);

        using var full = new MemoryStream();
        recorder.Save(full);
        var bytes = full.ToArray();

        using var truncated = new MemoryStream(bytes[..(bytes.Length - 4)]);
        Assert.Throws<EndOfStreamException>(() => Recorder.Load(truncated));
    }
}
