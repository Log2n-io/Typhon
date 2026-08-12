using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class SimulationDefinitionTests
{
    [Test]
    public void Default_UsesAgreedProductionIdentityAndInitialWorldValues()
    {
        var definition = SimulationDefinition.Default;

        Assert.Multiple(() =>
        {
            Assert.That(definition.RunName, Is.EqualTo("default"));
            Assert.That(definition.ShipCount, Is.EqualTo(50_000));
            Assert.That(definition.Seed, Is.EqualTo(0x5350414345424154UL));
            Assert.That(definition.RulesetVersion, Is.EqualTo(1U));
            Assert.That(definition.WorldSize, Is.EqualTo(1_000f));
            Assert.That(definition.MaximumHealth, Is.EqualTo(1_000U));
            Assert.That(definition.StagingTicks, Is.EqualTo(250));
            Assert.That(definition.SpatialCellSize, Is.EqualTo(100f));
            Assert.That(definition.SpatialMargin, Is.EqualTo(20f));
        });
    }
}