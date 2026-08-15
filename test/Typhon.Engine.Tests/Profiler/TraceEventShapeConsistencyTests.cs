using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests;

/// <summary>
/// The wire shape a producer WRITES and the shape a consumer READS must agree, for every kind.
///
/// <para>They are declared in two different places, in two different assemblies, with nothing tying them together:
/// <c>[TraceEvent(kind, Shape = …)]</c> (Typhon.Engine) drives the source generator that emits the record, while
/// <see cref="TraceEventKindExtensions.IsSpan"/> (Typhon.Profiler) — a hand-maintained ladder of numeric carve-outs —
/// decides whether a reader consumes the 25-byte span-header extension. Add an instant-shaped kind without adding its
/// carve-out and every span-aware decoder reads 25 bytes of payload as a span header: garbage duration, garbage
/// spanId/parentSpanId, and the event renders as a phantom span. The records stay aligned (<c>pos += size</c>), so
/// nothing crashes and nothing else looks wrong — which is why this drifts silently.</para>
///
/// <para>Both existing consumers of <c>IsSpan</c> are affected: <c>TraceEventDecoder.HandGlue</c> and the Workbench's
/// <c>TraceChunkScan</c>. The Workbench client mirrors the same ladder by hand in
/// <c>ClientApp/src/libs/profiler/decode/chunkDecoder.ts</c> (<c>isInstantKind</c>), so a drift here becomes a drift
/// there too.</para>
///
/// <para>Found via <c>EcsSpawnBatch</c> (kind 36, #620): declared <c>Shape = Instant</c>, but <c>IsSpan</c> had no
/// carve-out for it, so it rendered in the profiler timeline as a span named <c>Kind[36]</c>.</para>
/// </summary>
[TestFixture]
internal sealed class TraceEventShapeConsistencyTests
{
    [Test]
    public void EveryDeclaredTraceEventShape_AgreesWithIsSpan()
    {
        var mismatches = new List<string>();

        foreach (var type in typeof(DatabaseEngine).Assembly.GetTypes())
        {
            var attribute = type.GetCustomAttribute<TraceEventAttribute>();
            if (attribute == null)
            {
                continue;
            }

            var producerWritesInstant = attribute.Shape == TraceEventShape.Instant;
            var consumerReadsSpan = attribute.Kind.IsSpan();

            // Agreement means exactly one of the two is true. Equal ⇒ they disagree about the wire layout.
            if (producerWritesInstant == consumerReadsSpan)
            {
                mismatches.Add(
                    $"{type.Name}: kind {(byte)attribute.Kind} ({attribute.Kind}) is declared Shape={attribute.Shape} "
                    + $"but TraceEventKindExtensions.IsSpan() returns {consumerReadsSpan}");
            }
        }

        Assert.That(mismatches, Is.Empty,
            "A producer's declared wire shape disagrees with what readers will decode. Every span-aware consumer "
            + "(TraceEventDecoder.HandGlue, the Workbench's TraceChunkScan, and the client's isInstantKind mirror) will "
            + "misread these records — an instant decoded as a span yields garbage duration and parent links, and renders "
            + "as a phantom nested span.\n  " + string.Join("\n  ", mismatches));
    }

    /// <summary>
    /// Guards the specific regression: kind 36 is an instant, and readers must know it.
    /// </summary>
    [Test]
    public void EcsSpawnBatch_IsAnInstant_NotASpan()
    {
        Assert.That(TraceEventKind.EcsSpawnBatch.IsSpan(), Is.False,
            "EcsSpawnBatch is emitted with a 12-byte instant header (Shape = Instant); a reader that treats it as a span "
            + "consumes 25 bytes of payload as a span header and renders it on a thread lane with a fabricated duration");

        // Its neighbours are genuine spans — this must not have been fixed by widening a range.
        Assert.Multiple(() =>
        {
            Assert.That(TraceEventKind.EcsSpawn.IsSpan(), Is.True);
            Assert.That(TraceEventKind.EcsDestroy.IsSpan(), Is.True);
        });
    }

    /// <summary>
    /// The kinds with a hand-written codec and no <c>[TraceEvent]</c> struct cannot be covered by the reflection sweep
    /// above, so their classification is asserted explicitly. Listed here rather than silently uncovered.
    /// </summary>
    [Test]
    public void HandCodedInstantKinds_AreClassifiedAsInstants()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TraceEventKind.PerTickSnapshot.IsSpan(), Is.False);
            Assert.That(TraceEventKind.ThreadInfo.IsSpan(), Is.False);
            Assert.That(TraceEventKind.QueueTickEnd.IsSpan(), Is.False);
            Assert.That(TraceEventKind.ThreadContextSwitch.IsSpan(), Is.False);
            Assert.That(TraceEventKind.QueryDefinitionDescribe.IsSpan(), Is.False);
            Assert.That(TraceEventKind.QueryArgs.IsSpan(), Is.False);
        });
    }
}
