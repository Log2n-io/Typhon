using NUnit.Framework;
using System;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-02 / D2 — a <c>vel</c> codec's width and its <c>quantaDiv</c> are derived together at registration, neither fixed (W5).
/// </summary>
/// <remarks>
/// <para>
/// <c>L ≥ ⌈maxSpeed × period × multiplier / posStep × quantaDiv⌉</c>, with <c>L = 2ᵇ⁻¹ − 1</c>. The search runs the four legal widths outermost and the
/// divisors <c>16, 8, 4, 2, 1</c> inside each: <b>the narrowest width that can carry the displacement, then the finest velocity quantum that width affords.</b>
/// Every displacement a tick can carry below the teleport threshold must fit, because saturating instead would emit a segment per tick for every fast mover
/// exactly while the server is overloaded — which is when traffic must not grow.
/// </para>
/// <para>
/// <b>The ladder cases are driven through unit inputs</b> (<c>posStep = 1</c>, <c>period = 1</c>, <c>multiplier = 1</c>) so the speed IS the displacement in
/// position quanta and the boundary is exact rather than a float32 near-miss. The SWG case then checks the real numbers end to end, through the codec itself.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class VelocityCodecWidthTests : TestBase<VelocityCodecWidthTests>
{
    /// <summary>The derived pair for a displacement of <paramref name="quanta"/> position quanta per tick.</summary>
    private static (int Bits, int QuantaDiv) CodecFor(double quanta) => ProjectionCompiler.VelocityCodec(quanta, 1.0, 1, 1.0);

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    /// <summary>
    /// The width rounds up to one of the four legal quantizing widths, and never further than it has to.
    /// </summary>
    [Test]
    public void Width_RoundsUpToTheFourLegalWidths()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CodecFor(1).Bits, Is.EqualTo(8));
            Assert.That(CodecFor(127).Bits, Is.EqualTo(8), "127 quanta still fit 8 bits, at quantaDiv 1");
            Assert.That(CodecFor(128).Bits, Is.EqualTo(16));
            Assert.That(CodecFor(32_767).Bits, Is.EqualTo(16));
            Assert.That(CodecFor(32_768).Bits, Is.EqualTo(24));
            Assert.That(CodecFor(8_388_607).Bits, Is.EqualTo(24));
            Assert.That(CodecFor(8_388_608).Bits, Is.EqualTo(32));
        });
    }

    /// <summary>
    /// The divisor is the largest power of two ≤ 16 the chosen width still carries, and it drops only as far as the displacement forces it.
    /// </summary>
    /// <remarks>
    /// This is the half of W5 that used to be a constant. At 8 bits a displacement of 7 quanta leaves room for the full sixteenths; 127 quanta leaves room
    /// for none of them and takes whole quanta rather than a second byte. The pair is what a client decodes with, so both halves are asserted together.
    /// </remarks>
    [Test]
    public void QuantaDiv_IsTheFinestTheChosenWidthAffords()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CodecFor(7), Is.EqualTo((8, 16)), "7 x 16 = 112 codes fits 8 bits with the finest divisor");
            Assert.That(CodecFor(15), Is.EqualTo((8, 8)), "15 x 16 = 240 does not; halving the divisor costs resolution, not a byte");
            Assert.That(CodecFor(127), Is.EqualTo((8, 1)), "at the edge of 8 bits the divisor is spent entirely");
            Assert.That(CodecFor(128), Is.EqualTo((16, 16)), "the next width restores the finest divisor");
            Assert.That(CodecFor(2_048), Is.EqualTo((16, 8)), "SWG's creature: one halving, and 16 bits instead of 24");
            Assert.That(CodecFor(32_768), Is.EqualTo((24, 16)), "past 16 bits at quantaDiv 1 the width widens and the divisor resets");
            Assert.That(ProjectionCompiler.MaxVelocityQuantaDiv, Is.EqualTo(16), "the divisor is derived in [1, 16]; 16 is the ceiling, not the value");
        });
    }

    /// <summary>
    /// The derived pair actually round-trips the largest legal displacement: it encodes inside ±L and decodes back within one velocity quantum.
    /// </summary>
    /// <remarks>
    /// <b>The invariant, not the loop condition.</b> Asserting <c>SymmetricLimit(bits) ≥ needed</c> restates the search's own test and would pass for any
    /// arithmetic the derivation happened to use. What has to hold is a property of the codec: the fastest thing that is not a teleport encodes without
    /// clamping — one code past L and the segment saturates, which is the failure the derivation exists to prevent — and decodes back to the displacement it
    /// started from, to the resolution the chosen divisor promises (<c>posStep / quantaDiv</c>).
    /// </remarks>
    [Test]
    public void DerivedPair_EncodesTheLargestLegalDisplacementWithoutClamping()
    {
        foreach (var speed in new[] { 0.5, 2.0, 20.0, 100.0, 300.0 })
        {
            foreach (var multiplier in new[] { 1, 4, 6 })
            {
                var step = ProjectionTestSchema.PositionStepM;
                var (bits, quantaDiv) = ProjectionCompiler.VelocityCodec(speed, ProjectionTestSchema.TickPeriodSeconds, multiplier, step);

                // The largest displacement one tick may carry and still not be a teleport, on one axis.
                var displacement = speed * ProjectionTestSchema.TickPeriodSeconds * multiplier;
                var limit = WireMath.SymmetricLimit(bits);
                var code = WireMath.EncodeVel(displacement, step, quantaDiv, bits);
                var decoded = WireMath.DecodeVel(code, step, quantaDiv, bits);
                var velocityQuantum = step / quantaDiv;

                Assert.Multiple(() =>
                {
                    Assert.That(Math.Abs(code), Is.LessThanOrEqualTo(limit),
                        $"{speed} m/s at x{multiplier} must encode inside +/-{limit} at {bits} bits / quantaDiv {quantaDiv}");
                    Assert.That(WireMath.EncodeVel(-displacement, step, quantaDiv, bits), Is.EqualTo(-code), "the codec is symmetric");
                    Assert.That(Math.Abs(decoded - displacement), Is.LessThanOrEqualTo(velocityQuantum),
                        $"{speed} m/s at x{multiplier} must decode back within one velocity quantum ({velocityQuantum} m/tick)");
                });
            }
        }
    }

    /// <summary>
    /// The pair rises with the teleport speed and with the tick multiplier, which are the two factors of the same displacement.
    /// </summary>
    /// <remarks>
    /// Resolution is spent before bytes are, so a faster archetype first loses divisor and only then gains a byte per axis. Both directions are asserted as
    /// the ordering they are — finer-or-equal divisor never accompanies a narrower width — rather than as three fixed pairs.
    /// </remarks>
    [Test]
    public void Pair_DegradesResolutionBeforeItSpendsBytes()
    {
        var step = ProjectionTestSchema.PositionStepM;
        var atOne = ProjectionCompiler.VelocityCodec(10.0, 0.1, 1, step);
        var atSix = ProjectionCompiler.VelocityCodec(10.0, 0.1, 6, step);
        var faster = ProjectionCompiler.VelocityCodec(40.0, 0.1, 1, step);

        Assert.Multiple(() =>
        {
            // 10 m/s over a 0.1 s tick is 1 m per tick: 1 024 position quanta, which 16 bits carries at the full sixteenths. Six ticks' worth in one costs
            // resolution — 6 144 quanta needs quantaDiv 4 — but not the third byte the fixed divisor used to force.
            Assert.That(atOne, Is.EqualTo((16, 16)));
            Assert.That(atSix, Is.EqualTo((16, 4)));
            Assert.That(faster, Is.EqualTo((16, 4)), "4x the speed at the nominal rate is the same displacement as 6x the period, near enough");
            Assert.That(atSix.QuantaDiv, Is.LessThan(atOne.QuantaDiv), "a longer tick spends resolution first");
            Assert.That(atSix.Bits, Is.EqualTo(atOne.Bits), "and does not spend a byte while resolution is left to spend");
        });
    }

    /// <summary>
    /// SWG's numbers — 20 m/s, 10 Hz, a 2⁻¹⁰ m position step — land on 16 bits at <c>quantaDiv</c> 8, not on 24 bits at 16.
    /// </summary>
    /// <remarks>
    /// This is the case the joint derivation exists for. At the fixed divisor the displacement needed 32 768 codes and 16 bits carries 32 767 — one short —
    /// so every creature segment paid two bytes forever to keep a velocity quantum of a sixteenth of 2⁻¹⁰ m. Halving the divisor buys the whole width back
    /// for an eighth of a position quantum, 0.12 mm per tick, which is three orders below the float32 jitter the measurement already lives with.
    /// </remarks>
    [Test]
    public void SwgNumbers_Land_On16BitsAtQuantaDiv8()
    {
        var quanta = ProjectionTestSchema.MaxSpeedMps * ProjectionTestSchema.TickPeriodSeconds / ProjectionTestSchema.PositionStepM;
        Assert.That(quanta, Is.EqualTo(2_048), "2 m per tick is 2 048 position quanta");
        Assert.That(WireMath.SymmetricLimit(16), Is.EqualTo(32_767), "at quantaDiv 16 that is 32 768 codes - one more than 16 bits carries");

        var pair = ProjectionCompiler.VelocityCodec(ProjectionTestSchema.MaxSpeedMps, ProjectionTestSchema.TickPeriodSeconds, 1,
            ProjectionTestSchema.PositionStepM);

        Assert.That(pair, Is.EqualTo((16, 8)));
        Assert.That(ProjectionTestSchema.PositionStepM / pair.QuantaDiv, Is.EqualTo(1.0 / 8192.0).Within(1e-12),
            "an eighth of a position quantum is 0.12 mm per tick of velocity resolution");
    }

    /// <summary>
    /// The per-archetype opt-out caps the multiplier at 1: an archetype whose movement is bounded per tick pays the nominal width.
    /// </summary>
    /// <remarks>
    /// At 100 m/s the dilated displacement no longer fits 16 bits at <i>any</i> divisor, so the opt-out is worth a byte per axis per segment here — and the
    /// wider width comes with the divisor restored to 16, which is the derivation's ordering showing through.
    /// </remarks>
    [Test]
    public void IgnoreTickDilation_CapsTheMultiplierAtOne()
    {
        using var dbe = SetupEngine();

        var dilated = CompileWithMotion(dbe, ignoreDilation: false, largestTickMultiplier: 6);
        var nominal = CompileWithMotion(dbe, ignoreDilation: true, largestTickMultiplier: 6);

        Assert.Multiple(() =>
        {
            Assert.That(dilated.Position.SizedForTickMultiplier, Is.EqualTo(6), "without the opt-out the width covers the ladder's last rung");
            Assert.That(dilated.Position.Vel.Bits, Is.EqualTo(24));
            Assert.That(dilated.Position.Vel.QuantaDiv, Is.EqualTo(16), "the wider width affords the finest divisor again");
            Assert.That(nominal.Position.SizedForTickMultiplier, Is.EqualTo(1), "the opt-out caps the multiplier at 1");
            Assert.That(nominal.Position.IgnoresTickDilation, Is.True);
            Assert.That(nominal.Position.Vel.Bits, Is.EqualTo(16), "the nominal rate needs 8 bits fewer here - 2 B per segment, forever");
            Assert.That(nominal.Position.Vel.QuantaDiv, Is.EqualTo(2), "and pays for them in resolution, which is the cheaper of the two");
        });
    }

    /// <summary>
    /// The ceiling the width is derived from is the allowed ladder's last rung, never the raw <c>BaseTickRate / MinTickRateHz</c> ratio (finding F1).
    /// </summary>
    /// <remarks>
    /// The ratio only FILTERS the fixed ladder <c>[1, 2, 3, 4, 6]</c>. A runtime whose ratio is 60 still never ticks slower than 6×, so deriving the codec
    /// from the ratio would buy 3 more bits per axis per segment for a rate the engine cannot reach. The default 60 Hz over a 10 Hz floor gives exactly 6,
    /// which is why reading the ratio looked correct for as long as nobody configured anything else.
    /// </remarks>
    [Test]
    public void LargestAllowedMultiplier_IsTheLaddersLastRung_NotTheRatio()
    {
        Assert.That(new OverloadDetector(new OverloadOptions { MinTickRateHz = 10 }, baseTickRate: 600).MaxTickMultiplier, Is.EqualTo(6),
            "a ratio of 60 still stops at the ladder's last rung");
        Assert.That(new OverloadDetector(new OverloadOptions { MinTickRateHz = 10 }, baseTickRate: 60).MaxTickMultiplier, Is.EqualTo(6),
            "the default ratio is exactly the ladder's last rung, which is what hid the difference");
        Assert.That(new OverloadDetector(new OverloadOptions { MinTickRateHz = 10 }, baseTickRate: 25).MaxTickMultiplier, Is.EqualTo(2),
            "a ratio of 2 admits [1, 2]");
        Assert.That(new OverloadDetector(new OverloadOptions { MinTickRateHz = 10 }, baseTickRate: 10).MaxTickMultiplier, Is.EqualTo(1),
            "a runtime that may not slow down at all has one rung");
    }

    /// <summary>
    /// The compiled velocity codec carries the divisor a client decodes with, and the segment is sized from both codecs.
    /// </summary>
    [Test]
    public void CompiledVelocityCodec_CarriesItsDivisorAndSizesTheSegment()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjCreature));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Position.Vel.Kind, Is.EqualTo(CodecKind.Vel2), "a 2D position takes a 2D velocity");
            Assert.That(plan.Position.Vel.QuantaDiv, Is.EqualTo(8));
            Assert.That(plan.Position.Vel.Bits, Is.EqualTo(16));
            Assert.That(plan.Position.Pos.Kind, Is.EqualTo(CodecKind.Pos2));
            Assert.That(plan.Position.Pos.Bits, Is.EqualTo(24));

            // p0 (2 x 3 B) | v (2 x 2 B) | t0 (2 B) | epoch (1 B) - the two bytes the fixed divisor used to cost, on every creature segment.
            Assert.That(plan.Position.SegmentBytes, Is.EqualTo(13));
        });
    }

    private static CompiledProjectionPlan CompileWithMotion(DatabaseEngine dbe, bool ignoreDilation, int largestTickMultiplier)
    {
        var subs = new SubscriptionsRegistry();
        subs.Archetype<ProjCreature>(a => a
            .Motion(ProjCreature.Bounds, m =>
            {
                m.Teleport(100.0);
                if (ignoreDilation)
                {
                    m.IgnoreTickDilation();
                }
            })
            .Field(ProjCreature.Ai, x => x.Level, Codec.U16, name: "level"));

        return ProjectionCompiler.Compile(subs, dbe, 0.1, largestTickMultiplier)[0];
    }
}
