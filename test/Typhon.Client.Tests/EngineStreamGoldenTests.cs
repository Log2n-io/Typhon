using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The <c>stream-engine</c> vector read the way a client reads it: the <c>WELCOME</c>'s catalog, then every <c>TICK</c> applied to a replica.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the consumer side of the engine's encoder.</b> The vector is produced by <c>Typhon.Engine.Tests</c>' <c>EntitiesEncodingTests</c> from the
/// engine's own <c>ENTITIES</c> encoder — bytes copied out of replication blocks, not built from an object model — and what this fixture asserts is that a
/// client with nothing but the file ends up holding what the server held. A TypeScript decoder reading the same file has the same job and the same
/// expectation (<c>stream-engine.json</c>'s per-frame call log).
/// </para>
/// <para>
/// <b>Self-contained on purpose.</b> The stream opens with the <c>WELCOME</c> that carries its catalog, so this fixture names no companion catalog vector
/// and cannot drift from one.
/// </para>
/// </remarks>
[TestFixture]
public class EngineStreamGoldenTests
{
    [Test]
    public void TheEngineStreamAppliesIntoAReplica()
    {
        var messages = Unframe(GoldenFiles.ReadBin("stream-engine"));
        Assert.That(messages, Has.Count.GreaterThan(1), "the stream is a WELCOME and at least one TICK");

        var welcome = WelcomeMessage.Parse(messages[0]);
        Assert.That(welcome.CatalogJson, Is.Not.Empty, "the vector carries its own catalog, so a reader needs nothing else");
        Assert.That(CatalogSerializer.HashBytes(welcome.CatalogJson), Is.EqualTo(welcome.CatalogHash), "the digest is the FNV-1a of the bytes that travel");

        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(welcome.CatalogJson));
        var store = new WorldStore(plan);
        var applier = new FrameApplier(store);
        var sink = new RecordingSink();

        foreach (var frame in messages.Skip(1))
        {
            applier.Apply(frame);
            TickReader.Read(frame, plan, ref sink);
        }

        var expected = GoldenFiles.ReadJson("stream-engine")!["frames"]!.AsArray();
        var creature = plan.ArchetypeByName("ProjCreature");
        var rock = plan.ArchetypeByName("ProjRock");

        Assert.Multiple(() =>
        {
            Assert.That(store.Anomalies, Is.Zero, "the engine's stream is self-consistent: every update names an entity the store holds");
            Assert.That(store.Frames, Is.EqualTo(messages.Count - 1), "every frame applied");
            Assert.That(sink.Frames, Is.EqualTo(expected.Select(f => f!["calls"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray()).ToArray()),
                "the calls a decoder makes are the vector's, frame for frame");

            // The last frame is the RESET refill, so the replica holds exactly what that frame carried.
            Assert.That(store.Archetypes[creature.Idx].LiveCount, Is.EqualTo(4), "four creatures survived the churn");
            Assert.That(store.Archetypes[rock.Idx].LiveCount, Is.EqualTo(3), "the static archetype re-entered with the rest");
            Assert.That(store.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset), "the last frame is the profile switch");
        });
    }

    /// <summary>Splits a TCP stream into its messages: <c>u32 len</c> little-endian, excluding itself (03 § 10, W31).</summary>
    private static List<byte[]> Unframe(byte[] stream)
    {
        var messages = new List<byte[]>();
        var at = 0;
        while (at < stream.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at, 4));
            at += 4;
            messages.Add(stream.AsSpan(at, length).ToArray());
            at += length;
        }

        return messages;
    }

    /// <summary>Records the calls each frame's decode makes, as the vector spells them.</summary>
    private struct RecordingSink : ITickSink
    {
        private List<string> _calls;

        public List<string[]> Frames { get; private set; }

        public void BeginTick(uint tick, TickFlags flags, uint periodUs)
        {
            Frames ??= [];
            _calls = [$"beginTick {tick}"];
        }

        public void BeginEntities(ArchetypePlan archetype) => _calls.Add($"beginEntities {archetype.Name}");

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
            _calls.Add($"enter {netId}");

        public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
            _calls.Add($"segment {netId}");

        public void State(uint netId, byte groupMask) => _calls.Add($"state {netId} 0x{groupMask:x2}");

        public void Leave(uint netId) => _calls.Add($"leave {netId}");

        public void Event(MessagePlan type) => _calls.Add($"event {type.Name}");

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask) => _calls.Add($"self {netId}");

        public void Ack(ushort seq, byte reason) => _calls.Add($"ack {seq}");

        public void Source(ushort requestId, byte status, ushort code) => _calls.Add($"source {requestId}");

        public void BeginAggregate(CatalogGrid grid, bool reset) => _calls.Add($"beginAggregate {grid.Idx}");

        public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts) => _calls.Add($"aggregateCell {cell}");

        public void Metric(MetricPlan metric, int valueIndex, double value) => _calls.Add($"metric {metric.Name}");

        public void Debug(byte subType, scoped ReadOnlySpan<byte> payload) => _calls.Add($"debug {subType}");

        public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload) => _calls.Add($"ext {appTypeId}");

        public void UnknownBlock(byte blockType) => _calls.Add($"unknownBlock {blockType}");

        public void EndTick()
        {
            _calls.Add("endTick");
            Frames.Add(_calls.ToArray());
        }

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components) { }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8) { }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes) { }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components) { }
    }
}
