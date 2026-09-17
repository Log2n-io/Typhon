using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Typhon.Client.Tests;

/// <summary>A render time to evaluate a replayed stream's replica at, and the archetypes the case covers.</summary>
internal readonly record struct EvalCase(string Name, int AfterFrame, long RenderTick, double RenderFrac, string[] Archetypes);

/// <summary>
/// The shape a <c>motion-eval*</c> vector commits, and the framing a <c>stream-*</c> vector carries: shared by every motion golden suite so the two SDKs
/// compare one document, never two renderings of it.
/// </summary>
internal static class MotionEvalVector
{
    /// <summary>The stream vectors are TCP-framed: <c>u32 len LE | message</c> (W31).</summary>
    /// <param name="stream">The whole vector.</param>
    /// <returns>Each framed message, in order.</returns>
    internal static IEnumerable<byte[]> Frames(byte[] stream)
    {
        for (var at = 0; at < stream.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at));
            yield return stream.AsSpan(at + 4, length).ToArray();
            at += 4 + length;
        }
    }

    /// <summary>One case: the live entities of the archetypes it names, each evaluated once per slot and once in the batch, which must agree.</summary>
    /// <param name="store">The replica, after the case's frame.</param>
    /// <param name="evaluation">The case.</param>
    /// <returns>The case's JSON.</returns>
    internal static JsonObject Render(WorldStore store, EvalCase evaluation)
    {
        var one = new double[MotionEvaluator.MaxStride];
        var entities = new JsonArray();
        var previousIdx = -1;
        foreach (var name in evaluation.Archetypes)
        {
            var plan = store.Plan.ArchetypeByName(name);
            Assert.That(plan.Idx, Is.GreaterThan(previousIdx), $"{evaluation.Name}: `archetypes` must follow catalog archetype order");
            previousIdx = plan.Idx;

            var archetype = store.Archetypes[plan.Idx];
            var dims = archetype.Dims;
            var stride = archetype.MotionStride;
            var all = new double[archetype.LiveCount * stride];
            MotionEvaluator.EvaluateLive(archetype, evaluation.RenderTick, evaluation.RenderFrac, all);

            foreach (var i in Enumerable.Range(0, archetype.LiveCount).OrderBy(i => archetype.NetIds[archetype.Live[i]]))
            {
                var slot = archetype.Live[i];
                var netId = archetype.NetIds[slot];
                MotionEvaluator.EvaluateSlot(archetype, slot, evaluation.RenderTick, evaluation.RenderFrac, one);
                Assert.That(
                    BitStrings(all.AsSpan(i * stride, stride)),
                    Is.EqualTo(BitStrings(one.AsSpan(0, stride))),
                    $"{evaluation.Name}: EvaluateLive and EvaluateSlot disagree for {name} {netId}");

                entities.Add(new JsonObject
                {
                    ["archetype"] = name,
                    ["netId"] = netId,
                    ["epoch"] = MotionEvaluator.EpochAt(archetype, slot, evaluation.RenderTick),
                    ["position"] = GoldenFiles.Bits(one.AsSpan(0, dims)),
                    ["velocity"] = GoldenFiles.Bits(one.AsSpan(dims, dims)),
                });
            }
        }

        return new JsonObject
        {
            ["name"] = evaluation.Name,
            ["afterFrame"] = evaluation.AfterFrame,
            ["renderTick"] = evaluation.RenderTick,
            ["renderFrac"] = GoldenFiles.Bits(evaluation.RenderFrac),
            ["archetypes"] = new JsonArray([.. evaluation.Archetypes.Select(a => (JsonNode)a)]),
            ["entities"] = entities,
        };
    }

    // Bit strings rather than the doubles themselves: Double.Equals calls -0.0 equal to 0.0 and NaN equal to NaN, and this comparison must see both.
    private static string[] BitStrings(ReadOnlySpan<double> values)
    {
        var bits = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            bits[i] = GoldenFiles.Bits(values[i]);
        }

        return bits;
    }
}
