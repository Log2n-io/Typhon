using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>Which lairs a region spawns, and how hard a lair is to break (Loïc, 2026-10-24).</summary>
[TestFixture]
public sealed class LairKindChecks
{
    /// <summary>Half of a region's lairs are camps of foes: drawn uniformly over its kinds, nine in ten were beasts'.</summary>
    [Test]
    public void HalfOfARegionsLairsAreCamps()
    {
        int[] kinds = [CreatureTemplates.Frog, CreatureTemplates.Snake, CreatureTemplates.Wasp, CreatureTemplates.SandNinja];
        var rng = new Rng(11);
        var camps = 0;
        const int draws = 4000;
        for (var i = 0; i < draws; i++)
        {
            camps += CreatureTemplates.IsCamp(CreatureTemplates.PickLair(kinds, ref rng)) ? 1 : 0;
        }

        Assert.That(camps / (double)draws, Is.EqualTo(TatooineData.CampShare).Within(0.03));
    }

    [Test]
    public void ASiteOfOneSortDrawsOnlyThatSort()
    {
        var rng = new Rng(3);
        for (var i = 0; i < 200; i++)
        {
            Assert.That(CreatureTemplates.IsCamp(CreatureTemplates.PickLair(TatooineData.PoiLairTemplates, ref rng)), Is.True);
            Assert.That(CreatureTemplates.IsCamp(CreatureTemplates.PickLair([CreatureTemplates.Rat, CreatureTemplates.Wasp], ref rng)), Is.False);
        }
    }

    [Test]
    public void EveryRegionHasACamp()
    {
        foreach (var region in TatooineData.SpawnRegions)
        {
            Assert.That(System.Array.Exists(region.Templates, CreatureTemplates.IsCamp), Is.True, $"{region.Name} spawns no camp");
        }
    }

    /// <summary>A lair is harder to destroy than any foe it holds.</summary>
    [Test]
    public void ALairOutlastsItsOccupants()
    {
        for (var t = 0; t < CreatureTemplates.Count; t++)
        {
            Assert.That(CreatureTemplates.LairHealth(t), Is.GreaterThan(CreatureTemplates.Health[t]), $"template {t}");
        }
    }
}
