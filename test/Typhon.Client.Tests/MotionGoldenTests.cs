using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The cross-language pin for motion: the committed <c>stream-kitchen-sink</c> vector replayed through <see cref="FrameApplier"/>, then evaluated at the render
/// times a case names, against the committed <c>motion-eval</c> vector.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a committed vector and not a table inlined here.</b> Both SDKs are ports of one another, and a table in C# only binds the C# side: editing
/// <c>motion.ts</c> would break nothing. The vector is a third artefact both read, so a divergence in the arithmetic, the segment choice, the ring's wrap or
/// the <c>none</c>-model interpolation fails on whichever side moved. It carries an empty <c>.bin</c> — like <c>wire-refusals</c>, it pins no bytes of its
/// own, only what replaying another vector's bytes must evaluate to.
/// </para>
/// <para>
/// The catalog exercises all three models: <c>Beacon</c> is <c>static</c> in 2D, <c>Buoy</c> is <c>motion</c> with <c>model: none</c> in 2D, and <c>Drone</c>
/// is <c>motion</c> with <c>model: linear</c> in 3D. <c>Ledger</c> has no position and is never named by a case.
/// </para>
/// <para>
/// Every case is asserted through both entry points — <see cref="MotionEvaluator.EvaluateSlot"/> and the batch
/// <see cref="MotionEvaluator.EvaluateLive"/> — against the same bits, so the batch path cannot drift from the per-slot one it exists to accelerate.
/// </para>
/// </remarks>
[TestFixture]
public class MotionGoldenTests
{
    private const string VectorName = "motion-eval";

    private const string Description =
        "Motion evaluated over the committed stream-kitchen-sink replay: after frame `afterFrame`, every live entity of the archetypes a case names, "
        + "evaluated at renderTick + renderFrac and rendered as IEEE bit strings. Render time is split into an integer tick and a fraction in [0, 1) — never "
        + "one float — so evaluation stays exact however long the server has been running (03 § 6), and `epoch` is the motion epoch in force at renderTick, so "
        + "a change of it between two render times is a teleport. Entities are named by archetype and netId, never by slot, ordered by catalog archetype then "
        + "ascending netId; `position` and `velocity` each carry `dims` values in axis order, a static archetype included, whose velocities are zero. "
        + "segmentHistory is the ring depth the vector was generated with: the kitchen-sink catalog's 50 ms period under the 300 ms default render-delay cap. "
        + "Two intended cases are absent because the four-frame stream cannot produce them and extending it would move bytes other suites pin: an epoch "
        + "change (every entity of the stream keeps one epoch throughout — Beacon 0, Drone 1, Buoy 4, so no `none`-model pair ever straddles a teleport) and "
        + "a ring wrap (the ring is 8 deep and no entity receives more than two segments). Both are pinned across the SDKs by motion-eval-wrap, over the "
        + "longer stream-motion stream.";

    /// <summary>What each case pins, in the order the vector lists them: frames ascend, and within a frame the declaration order holds.</summary>
    private static readonly EvalCase[] Cases =
    [
        // Frame 0 — the RESET fill. Both Beacons are live, at the positions they entered with.
        new("a-static-archetype", 0, 1002, 0.5, ["Beacon"]),

        // Frame 1 — Buoy 10 and Drone 100 each hold two segments, the newer starting exactly at tick 1001.
        new("exactly-at-a-segment-start", 1, 1001, 0, ["Buoy", "Drone"]),
        new("between-two-none-samples", 1, 1000, 0.5, ["Buoy"]),

        // Frame 2 — Drone 102 enters with one segment; Drone 100 and Buoy 10 still hold the two they had.
        new("before-the-oldest-segment-held", 2, 900, 0, ["Buoy", "Drone"]),
        new("past-the-newest-segment", 2, 1100, 0.5, ["Buoy", "Drone"]),
    ];

    /// <summary>The ring depth and record size the kitchen-sink catalog's 20 Hz tick period gives, in both SDKs and in the vector.</summary>
    [Test]
    public void TheRingIsSizedTheSameInBothSdks()
    {
        var store = new WorldStore(KitchenSink());

        Assert.Multiple(() =>
        {
            Assert.That(store.SegmentHistory, Is.EqualTo(8), "50 000 µs ticks, a 300 ms delay: ⌈300/50⌉ + 2");
            Assert.That(store.Archetypes[store.Plan.ArchetypeByName("Beacon").Idx].Segments.RecordBytes, Is.EqualTo(304), "2D");
            Assert.That(store.Archetypes[store.Plan.ArchetypeByName("Drone").Idx].Segments.RecordBytes, Is.EqualTo(432), "3D");

            if (!GoldenFiles.Updating)
            {
                Assert.That(
                    GoldenFiles.ReadJson(VectorName)["segmentHistory"]!.GetValue<int>(),
                    Is.EqualTo(store.SegmentHistory),
                    $"{VectorName}.json was generated against another ring depth — every case's segment choice may have moved, in both SDKs");
            }
        });
    }

    /// <summary>Replaying the golden stream and evaluating it gives, to the last bit, what the committed vector says — through both evaluation paths.</summary>
    [Test]
    public void TheGoldenStreamEvaluatesToTheCommittedVector()
    {
        var store = new WorldStore(KitchenSink());
        var applier = new FrameApplier(store);
        var cases = new JsonArray();
        var frame = 0;
        foreach (var message in MotionEvalVector.Frames(GoldenFiles.ReadBin("stream-kitchen-sink")))
        {
            applier.Apply(message);
            foreach (var evaluation in Cases)
            {
                if (evaluation.AfterFrame == frame)
                {
                    cases.Add(MotionEvalVector.Render(store, evaluation));
                }
            }

            frame++;
        }

        Assert.That(cases.Count, Is.EqualTo(Cases.Length), "every case must name a frame the stream carries");

        GoldenFiles.Assert(VectorName, [], new JsonObject
        {
            ["description"] = Description,
            ["catalog"] = "catalog-kitchen-sink",
            ["stream"] = "stream-kitchen-sink",
            ["segmentHistory"] = store.SegmentHistory,
            ["cases"] = cases,
        });
    }

    private static CatalogPlan KitchenSink() => CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-kitchen-sink")));
}
