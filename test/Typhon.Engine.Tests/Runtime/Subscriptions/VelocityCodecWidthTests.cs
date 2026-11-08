using NUnit.Framework;
using System;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-02 / D2, Realms D-3 — a <c>vel</c> codec's absolute unit <c>2^unitExp</c> m per tick comes from the motion rule's drift budget, and its width is
/// the narrowest that carries the largest non-teleport displacement at that unit (W5, <c>typhon.3</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>u</c> = the largest power of two ≤ <c>Tolerance / MaxAge</c> (ticks at the nominal period); then <c>L(b) ≥ ⌈maxSpeed × period × multiplier / u⌉</c>,
/// <c>L = 2ᵇ⁻¹ − 1</c>. Nothing of a position codec enters: the unit is the same in every realm, whatever its bounds.
/// </para>
/// <para>
/// <b>The ladder cases are driven through unit inputs</b> (tolerance 1 m over one tick, so <c>u = 1</c>; <c>period = 1</c>, <c>multiplier = 1</c>) so the
/// speed IS the code count and the boundary is exact.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class VelocityCodecWidthTests : TestBase<VelocityCodecWidthTests>
{
    /// <summary>The derived pair for a displacement of <paramref name="codes"/> unit-1 codes per tick.</summary>
    private static (int Bits, int UnitExp) CodecFor(double codes) => ProjectionCompiler.VelocityCodec(codes, 1.0, 1, 1.0, 1.0);

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
            Assert.That(CodecFor(127).Bits, Is.EqualTo(8));
            Assert.That(CodecFor(128).Bits, Is.EqualTo(16));
            Assert.That(CodecFor(32_767).Bits, Is.EqualTo(16));
            Assert.That(CodecFor(32_768).Bits, Is.EqualTo(24));
            Assert.That(CodecFor(8_388_607).Bits, Is.EqualTo(24));
            Assert.That(CodecFor(8_388_608).Bits, Is.EqualTo(32));
        });
    }

    /// <summary>
    /// The drift budget sets the COARSEST unit (the largest power of two within <c>Tolerance / MaxAge</c> ticks), which picks the width; the unit is then
    /// the finest that width still carries (#1632): the width is paid for, and a coarser unit only adds a systematic drift to every straight walker.
    /// </summary>
    [Test]
    public void UnitExp_IsTheFinestTheWidthCarries_NeverCoarserThanTheDriftBudget()
    {
        static void Check(double speed, double period, double tolerance, double maxAge)
        {
            var (bits, exp) = ProjectionCompiler.VelocityCodec(speed, period, 1, tolerance, maxAge);
            var budget = Math.Clamp(Math.ILogB(tolerance / Math.Max(1d, Math.Round(maxAge / period, MidpointRounding.AwayFromZero))),
                ProtocolConstants.MinVelocityUnitExp, ProtocolConstants.MaxVelocityUnitExp);
            var range = speed * period;
            Assert.That(exp, Is.LessThanOrEqualTo(budget), $"{speed} m/s: never coarser than the drift budget");
            Assert.That(Math.Ceiling(Math.ScaleB(range, -exp)), Is.LessThanOrEqualTo(WireMath.SymmetricLimit(bits)), $"{speed} m/s: the range still fits");
            if (exp > ProtocolConstants.MinVelocityUnitExp)
            {
                Assert.That(Math.Ceiling(Math.ScaleB(range, -(exp - 1))), Is.GreaterThan(WireMath.SymmetricLimit(bits)),
                    $"{speed} m/s: one step finer would overflow {bits} bits, so this is the finest");
            }
        }

        Assert.Multiple(() =>
        {
            Check(1, 1.0, 1.0, 4.0);
            Check(1, 1.0, 1.0, 5.0);
            Check(20, 0.02, 0.05, 5.0);
            Check(20, 0.1, 0.05, 5.0);
            Check(200, 0.1, 0.05, 5.0);
            Check(21, 1.0 / 30, 0.5, 5.0);
            Assert.That(ProjectionCompiler.VelocityCodec(1e-9, 1.0, 1, 1e-20, 1.0).UnitExp, Is.EqualTo(ProtocolConstants.MinVelocityUnitExp));
        });
    }

    /// <summary>
    /// The case #1632 measured: the MMORPG shard's players (0.5 m tolerance, 5 s heartbeat, 30 Hz, 21 m/s teleport) walking a constant 6 m/s. At the drift
    /// budget's 2^-9 m/tick that read back as 5.977 m/s, a 0.12 m correction every heartbeat; at the width's finest unit it round-trips within 1 mm/s.
    /// </summary>
    [Test]
    public void AStraightWalkersVelocityRoundTripsWithinAMillimetrePerSecond()
    {
        const double period = 1.0 / 30;
        var (bits, exp) = ProjectionCompiler.VelocityCodec(21, period, 1, 0.5, 5.0);
        var perTick = 6.0 * period;
        var decoded = WireMath.DecodeVel(WireMath.EncodeVel(perTick, exp, bits), exp, bits) / period;
        Assert.Multiple(() =>
        {
            Assert.That(bits, Is.EqualTo(16), "the width is unchanged: precision costs no byte");
            Assert.That(Math.Abs(decoded - 6.0), Is.LessThan(0.001), $"6 m/s decodes as {decoded} m/s at 2^{exp}");
            Assert.That(Math.Abs(decoded - 6.0) * 5.0, Is.LessThan(0.01), "under a centimetre of drift per heartbeat");
        });
    }

    /// <summary>
    /// A speed no width can carry at the derived unit is refused at registration, never saturated.
    /// </summary>
    [Test]
    public void AnUncarriableSpeed_IsRefused() => Assert.Throws<InvalidOperationException>(() => ProjectionCompiler.VelocityCodec(1e9, 1.0, 1, 1e-6, 1.0));

    /// <summary>
    /// The derived pair round-trips the largest legal displacement without clamping, within half a unit — and that half unit over <c>MaxAge</c> ticks of
    /// extrapolation stays within half the tolerance, which is what the unit is derived for.
    /// </summary>
    [Test]
    public void DerivedPair_EncodesTheLargestLegalDisplacement_AndBoundsTheDrift()
    {
        const double tolerance = 0.05;
        const double maxAge = 5.0;
        var period = ProjectionTestSchema.TickPeriodSeconds;
        foreach (var speed in new[] { 0.5, 2.0, 20.0, 100.0, 300.0 })
        {
            foreach (var multiplier in new[] { 1, 4, 6 })
            {
                var (bits, unitExp) = ProjectionCompiler.VelocityCodec(speed, period, multiplier, tolerance, maxAge);

                var displacement = speed * period * multiplier;
                var limit = WireMath.SymmetricLimit(bits);
                var code = WireMath.EncodeVel(displacement, unitExp, bits);
                var decoded = WireMath.DecodeVel(code, unitExp, bits);
                var unit = Math.ScaleB(1.0, unitExp);

                Assert.Multiple(() =>
                {
                    Assert.That(Math.Abs(code), Is.LessThanOrEqualTo(limit), $"{speed} m/s at x{multiplier} must encode inside +/-{limit} at {bits} bits");
                    Assert.That(WireMath.EncodeVel(-displacement, unitExp, bits), Is.EqualTo(-code), "the codec is symmetric");
                    Assert.That(Math.Abs(decoded - displacement), Is.LessThanOrEqualTo(unit / 2), "decodes within half a unit");
                    Assert.That(unit / 2 * (maxAge / period), Is.LessThanOrEqualTo(tolerance / 2), "half a unit of drift per tick over MaxAge");
                });
            }
        }
    }

    /// <summary>
    /// The per-archetype opt-out caps the multiplier at 1: an archetype whose movement is bounded per tick pays the nominal width, at the same unit.
    /// </summary>
    [Test]
    public void IgnoreTickDilation_CapsTheMultiplierAtOne()
    {
        using var dbe = SetupEngine();

        var dilated = CompileWithMotion(dbe, ignoreDilation: false, largestTickMultiplier: 6);
        var nominal = CompileWithMotion(dbe, ignoreDilation: true, largestTickMultiplier: 6);

        Assert.Multiple(() =>
        {
            // 100 m/s x 0.1 s x 6 = 60 m per tick at 2^-10 m: 61 440 codes, past 16 bits. The nominal 10 m is 10 240 codes.
            Assert.That(dilated.Position.SizedForTickMultiplier, Is.EqualTo(6), "without the opt-out the width covers the ladder's last rung");
            Assert.That(dilated.Position.Vel.Bits, Is.EqualTo(24));
            Assert.That(nominal.Position.SizedForTickMultiplier, Is.EqualTo(1), "the opt-out caps the multiplier at 1");
            Assert.That(nominal.Position.IgnoresTickDilation, Is.True);
            Assert.That(nominal.Position.Vel.Bits, Is.EqualTo(16), "the nominal rate needs 8 bits fewer here - 2 B per segment, forever");
            Assert.That(dilated.Position.Vel.UnitExp, Is.LessThanOrEqualTo(-10), "never coarser than the drift budget");
            Assert.That(nominal.Position.Vel.UnitExp, Is.LessThanOrEqualTo(-10), "never coarser than the drift budget, whatever the width");
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
    /// The compiled velocity codec carries the unit a client decodes with, and the segment is sized from both codecs.
    /// </summary>
    [Test]
    public void CompiledVelocityCodec_CarriesItsUnitAndSizesTheSegment()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjCreature));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Position.Vel.Kind, Is.EqualTo(CodecKind.Vel2), "a 2D position takes a 2D velocity");
            Assert.That(plan.Position.Vel.UnitExp, Is.EqualTo(-13), "5 cm over 50 ticks budgets 2^-10; 16 bits carry 2 m per tick down to 2^-13");
            Assert.That(plan.Position.Vel.Bits, Is.EqualTo(16), "2 m per tick is 2 048 codes");
            Assert.That(plan.Position.Pos.Kind, Is.EqualTo(CodecKind.Pos2));
            Assert.That(plan.Position.Pos.Bits, Is.EqualTo(24));

            // p0 (2 x 3 B) | v (2 x 2 B) | t0 (2 B) | epoch (1 B).
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
