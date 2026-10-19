using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// <see cref="EventCollector"/> against the committed kitchen-sink stream: every frame's events, in order, each field with the value the golden recorder
/// decodes — numbers bit for bit, entity references as netIds, text and bytes — and a frame's events taken once.
/// </summary>
[TestFixture]
public class EventCollectorTests
{
    [Test]
    public void CollectsEveryFramesEventsAsTheGoldenRecorderDecodesThem()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-kitchen-sink")));
        var frames = Frames(GoldenFiles.ReadBin("stream-kitchen-sink"));

        var reference = new WorldStore(plan);
        var recorder = new EventRecorder(reference);
        var referenceApplier = new FrameApplier(reference, recorder);
        var collector = new EventCollector();
        var applier = new FrameApplier(new WorldStore(plan), collector);

        var total = 0;
        var taken = new List<ReceivedEvent>();
        foreach (var frame in frames)
        {
            recorder.Events.Clear();
            referenceApplier.Apply(frame);
            applier.Apply(frame);

            taken.Clear();
            collector.TakeFrame(taken);
            Assert.That(collector.Pending, Is.Empty, "a frame's events are taken once");
            Assert.That(taken.Select(e => e.Name), Is.EqualTo(recorder.Events.Select(e => e!["type"]!.GetValue<string>())));
            for (var i = 0; i < taken.Count; i++)
            {
                var expected = recorder.Events[i]!["fields"]!.AsObject();
                var got = taken[i];
                Assert.That(got.Numbers.Count + got.Integers.Count + got.Texts.Count + got.Bytes.Count, Is.EqualTo(expected.Count), got.Name);
                foreach (var (name, value) in expected)
                {
                    if (got.Numbers.TryGetValue(name, out var numbers))
                    {
                        // A list records {count, values}; every other numeric field its components' bits.
                        var bits = value is System.Text.Json.Nodes.JsonObject list ? list["values"] : value;
                        Assert.That(bits!.ToJsonString(), Is.EqualTo(GoldenFiles.Bits(numbers).ToJsonString()), $"{got.Name}.{name}");
                    }
                    else if (got.Integers.TryGetValue(name, out var integers))
                    {
                        Assert.That(value!.ToJsonString(), Is.EqualTo(GoldenFiles.Bits64(integers).ToJsonString()), $"{got.Name}.{name}");
                    }
                    else if (got.Texts.TryGetValue(name, out var text))
                    {
                        Assert.That(value!.GetValue<string>(), Is.EqualTo(GoldenFiles.Hex(Encoding.UTF8.GetBytes(text))), $"{got.Name}.{name}");
                    }
                    else
                    {
                        Assert.That(got.Bytes.TryGetValue(name, out var bytes), Is.True, $"{got.Name}.{name} is collected");
                        Assert.That(value!.GetValue<string>(), Is.EqualTo(GoldenFiles.Hex(bytes)), $"{got.Name}.{name}");
                    }
                }
            }

            total += taken.Count;
        }

        Assert.That(total, Is.GreaterThan(0), "the stream carries events, so the comparison compared something");
    }

    private static List<byte[]> Frames(byte[] stream)
    {
        var frames = new List<byte[]>();
        for (var at = 0; at < stream.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at));
            frames.Add(stream.AsSpan(at + 4, length).ToArray());
            at += 4 + length;
        }

        return frames;
    }
}
