using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The <c>ENTITIES</c> sub-list run structure (03 § 5): a sub-list is a sequence of runs, each restarting the netId delta, so a frame can carry bytes
/// that were encoded somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this fixture hand-splices bytes rather than calling the writer.</b> <see cref="TickWriter"/> builds a frame from an object model and has no
/// shared blocks to reference, so it always produces exactly one run — the case every other vector already covers. The property that matters here is the
/// one no current writer exercises: that a SECOND run starts its netIds again from zero and may therefore name an entity whose id is LOWER than the last
/// one of the run before it. A single ascending sub-list could not encode that at all, which is precisely why the engine could not reference a block it
/// encoded once per cluster (17 § 18).
/// </para>
/// <para>
/// The splice is built from the writer's own output, so the record bytes are the writer's and only the framing is this fixture's.
/// </para>
/// </remarks>
[TestFixture]
public class EntitiesRunTests
{
    private const uint Tick = 70_000;

    private static readonly CatalogPlan Plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink()));

    [Test]
    public void TwoStateRunsDecodeInOrderEvenWhenTheSecondNamesALowerNetId()
    {
        // Run A names 40 then 41; run B names 7. Concatenated as one ascending list this is impossible, which is the whole point.
        var runA = StateRun([(40, -2.25, 0), (41, 1.5, -3)]);
        var runB = StateRun([(7, 0.5, 9)]);

        var sink = new RecordingSink();
        var message = Frame(SpliceStateRuns(runA, runB));
        TickReader.Read(message, Plan, ref sink);

        Assert.That(Calls(sink, "state"), Is.EqualTo(new uint[] { 40, 41, 7 }), "every run's records reach the sink, in the order the runs travel");

        // Committed as a tick-* vector so the TypeScript decoder, which discovers them by prefix, reads the same bytes and must produce the same call log.
        // A run structure only one of the two implementations understands is worse than none at all.
        Golden.Assert("tick-runs", message, new JsonObject
        {
            ["description"] = "an ENTITIES state sub-list of two runs, the second restarting at a lower netId than the first ended on",
            ["catalog"] = "catalog-kitchen-sink",
            ["log"] = sink.Log.DeepClone(),
        });
    }

    [Test]
    public void OrderingIsRelaxedBetweenRunsAndNotInsideOne()
    {
        // The relaxation is exactly one thing: the DELTA restarts. Inside a run the gap is still a forward step, which the encoder — the only side that
        // can observe the violation, since a decoded gap is always non-negative — refuses.
        var values = new RecordValues { ["strength"] = FieldValue.Of(0.5), ["drift"] = FieldValue.Of(1) };
        var descending = new List<StateRecord>
        {
            new() { NetId = 41, GroupMask = 1, Values = values },
            new() { NetId = 40, GroupMask = 1, Values = values },
        };

        var buffer = new byte[4096];
        Assert.Throws<ArgumentException>(() =>
        {
            var w = new WireWriter(buffer);
            TickWriter.WriteEntities(ref w, Tick, Plan.ArchetypeByName("Beacon"), [], [], descending, []);
        });
    }

    [Test]
    public void AnEmptyRunIsMalformed()
    {
        // runs = 1, records = 0. A sub-list with nothing to say writes a zero RUN count instead, so this byte string is not one any encoder produces.
        var sink = new RecordingSink();
        Assert.Throws<WireFormatException>(() => TickReader.Read(Frame([0x01, 0x00]), Plan, ref sink));
    }

    [Test]
    public void ASubListWithNoRunsIsWellFormedAndSilent()
    {
        var sink = new RecordingSink();
        TickReader.Read(Frame([0x00]), Plan, ref sink);
        Assert.That(Calls(sink, "state"), Is.Empty);
    }

    /// <summary>Encodes one state run's bytes — <c>varu count</c> then the records — by taking them back out of the writer's own block.</summary>
    private static byte[] StateRun(IReadOnlyList<(uint NetId, double Strength, double Drift)> records)
    {
        var states = new List<StateRecord>();
        foreach (var (netId, strength, drift) in records)
        {
            var values = new RecordValues { ["strength"] = FieldValue.Of(strength), ["drift"] = FieldValue.Of(drift) };
            states.Add(new StateRecord { NetId = netId, GroupMask = 1, Values = values });
        }

        var buffer = new byte[4096];
        var w = new WireWriter(buffer);
        TickWriter.WriteEntities(ref w, Tick, Plan.ArchetypeByName("Beacon"), [], [], states, []);

        // type | length | archetypeIdx | enterRuns(0) | segmentRuns(0) | stateRuns(1) | <the run> | leaveRuns(0)
        var r = new WireReader(buffer.AsSpan(0, w.Position));
        r.ReadU8();
        var length = (int)r.ReadVaru();
        var start = r.Position;
        r.ReadVaru();
        Assert.That(r.ReadVaru(), Is.EqualTo(0u), "the fixture's blocks carry no enters");
        Assert.That(r.ReadVaru(), Is.EqualTo(0u), "Beacon is static and carries no segments");
        Assert.That(r.ReadVaru(), Is.EqualTo(1u), "the object-model writer emits exactly one run");
        var runStart = r.Position;
        return buffer[runStart..(start + length - 1)];
    }

    /// <summary>Builds the state sub-list of an <c>ENTITIES</c> block from pre-encoded runs: the run count, then each run verbatim.</summary>
    private static byte[] SpliceStateRuns(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var buffer = new byte[8192];
        var w = new WireWriter(buffer);
        w.WriteVaru(second.Length == 0 ? 1u : 2u);
        w.WriteBytes(first);
        w.WriteBytes(second);
        return buffer[..w.Position];
    }

    /// <summary>Wraps a state sub-list into a whole <c>TICK</c> message for the Beacon archetype.</summary>
    private static byte[] Frame(ReadOnlySpan<byte> stateSubList)
    {
        var buffer = new byte[16384];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick, TickFlags.None);
        var mark = TickWriter.BeginBlock(ref w, BlockTypes.Entities);
        w.WriteVaru((uint)Plan.ArchetypeByName("Beacon").Idx);
        w.WriteVaru(0);
        w.WriteVaru(0);
        w.WriteBytes(stateSubList);
        w.WriteVaru(0);
        TickWriter.EndBlock(ref w, mark);
        return buffer[..w.Position];
    }

    private static uint[] Calls(RecordingSink sink, string call)
    {
        var ids = new List<uint>();
        foreach (var node in sink.Log)
        {
            if (node != null && (string)node["call"] == call)
            {
                ids.Add((uint)node["netId"]);
            }
        }

        return [.. ids];
    }
}
