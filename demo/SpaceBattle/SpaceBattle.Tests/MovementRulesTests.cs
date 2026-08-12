using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class MovementRulesTests
{
    [Test]
    public void Advance_WhenCrossingWorldFace_MirrorsOvershootAndReflectsOnlyThatAxis()
    {
        var result = MovementRules.Advance(
            new PositionSnapshot(999f, 400f, 300f),
            new MotionSnapshot(1f, 0f, 0f, 100f),
            simulationDeltaSeconds: 0.04f,
            worldSize: 1_000f);

        Assert.Multiple(() =>
        {
            Assert.That(result.Position, Is.EqualTo(new PositionSnapshot(997f, 400f, 300f)));
            Assert.That(result.Motion, Is.EqualTo(new MotionSnapshot(-1f, 0f, 0f, 100f)));
        });
    }

    [Test]
    public void Advance_WhenCrossingAnEdge_ReflectsEveryImpactedAxis()
    {
        var result = MovementRules.Advance(
            new PositionSnapshot(999f, 999f, 500f),
            new MotionSnapshot(0.6f, 0.8f, 0f, 5f),
            simulationDeltaSeconds: 1f,
            worldSize: 1_000f);

        Assert.Multiple(() =>
        {
            Assert.That(result.Position, Is.EqualTo(new PositionSnapshot(998f, 997f, 500f)));
            Assert.That(result.Motion, Is.EqualTo(new MotionSnapshot(-0.6f, -0.8f, 0f, 5f)));
        });
    }

    [Test]
    public void Advance_NormalizesDirectionBeforeApplyingTheFixedStep()
    {
        var result = MovementRules.Advance(
            new PositionSnapshot(999f, 400f, 300f),
            new MotionSnapshot(2f, 0f, 0f, 100f),
            simulationDeltaSeconds: 0.04f,
            worldSize: 1_000f);

        Assert.Multiple(() =>
        {
            Assert.That(result.Position, Is.EqualTo(new PositionSnapshot(997f, 400f, 300f)));
            Assert.That(result.Motion, Is.EqualTo(new MotionSnapshot(-1f, 0f, 0f, 100f)));
        });
    }
}
