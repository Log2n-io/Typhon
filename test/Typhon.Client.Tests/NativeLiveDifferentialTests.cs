using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The native SDK's live run, replayed through this client: two decoders over the same live bytes must hold the same replica after every frame
/// (design/Subscriptions/13 § 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Run by <c>scripts/native-live-test.py</c>, which provides the input.</b> The script starts <c>Typhon.Subscriptions.E2EHost</c>, runs the native
/// <c>typhon_client_live</c> against it over TCP — which records every message it received and renders its replica after each frame — and then runs this
/// fixture with <c>TYPHON_NATIVE_LIVE_DIR</c> pointing at that output. Without the variable there is nothing to compare, and the test says so as Ignored
/// rather than passing; the script fails a run in which it did not execute.
/// </para>
/// <para>
/// <b>Why a replay and not a second live client.</b> Two sessions on one server receive different frames — they connect at different ticks — so their
/// replicas never line up frame for frame. The same bytes through both decoders do, and any difference is a decoder disagreeing on a live engine's output.
/// </para>
/// </remarks>
[TestFixture]
[Category("NativeSdk")]
public class NativeLiveDifferentialTests
{
    [Test]
    public void TheNativeReplicaMatchesThisClientAfterEveryFrame()
    {
        var directory = Environment.GetEnvironmentVariable("TYPHON_NATIVE_LIVE_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            Assert.Ignore("TYPHON_NATIVE_LIVE_DIR is not set: run scripts/native-live-test.py");
        }

        var messages = Unframe(File.ReadAllBytes(Path.Combine(directory!, "record.bin")));
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(directory!, "snapshots.json")))!.AsArray();
        Assert.That(messages, Has.Count.GreaterThan(1), "the native run recorded no session");

        var welcome = WelcomeMessage.Parse(messages[0]);
        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(welcome.CatalogJson));
        var store = new WorldStore(plan);
        var recorder = new EventRecorder(store);
        var applier = new FrameApplier(store, recorder);

        // The recording may end with a frame the native client received after its last snapshot (it stops polling once it has enough); only the frames
        // it rendered are compared, and there must be as many as it rendered.
        var compared = 0;
        for (var i = 1; i < messages.Count && compared < expected.Count; i++)
        {
            if (messages[i][0] != MessageTypes.Tick)
            {
                continue;
            }

            recorder.Events.Clear();
            applier.Apply(messages[i]);
            var actual = StreamSnapshot.Render(store, recorder.Events);
            if (!JsonNode.DeepEquals(actual, expected[compared]))
            {
                Assert.Fail($"frame {compared} (tick {store.Tick}) differs:\n  .NET   {actual.ToJsonString()}\n  native {expected[compared]!.ToJsonString()}");
            }

            compared++;
        }

        Assert.That(compared, Is.EqualTo(expected.Count), "the recording holds fewer frames than the native client rendered");
        Assert.That(store.Anomalies, Is.Zero);
    }

    private static List<byte[]> Unframe(byte[] stream)
    {
        var messages = new List<byte[]>();
        for (var at = 0; at + 4 <= stream.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at));
            messages.Add(stream.AsSpan(at + 4, length).ToArray());
            at += 4 + length;
        }

        return messages;
    }
}
