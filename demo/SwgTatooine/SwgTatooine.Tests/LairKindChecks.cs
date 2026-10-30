using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>Which lairs a region spawns, and how hard a lair is to break (Loïc, 2026-10-24).</summary>
[TestFixture]
public sealed class LairKindChecks
{
    /// <summary>
    /// Half of a region's lairs are camps of foes where both sorts cover the level (Jundland Wastes at 30: spiders and ninjas qualify, the rats and vipers
    /// do not): drawn uniformly over its kinds, nine in ten were beasts'.
    /// </summary>
    [Test]
    public void HalfOfARegionsLairsAreCamps()
    {
        int[] kinds = [CreatureTemplates.Spider, CreatureTemplates.Viper, CreatureTemplates.Rat, CreatureTemplates.Ninja];
        var rng = new Rng(11);
        var camps = 0;
        const int draws = 4000;
        for (var i = 0; i < draws; i++)
        {
            camps += CreatureTemplates.IsCamp(CreatureTemplates.PickLair(kinds, 30, ref rng)) ? 1 : 0;
        }

        Assert.That(camps / (double)draws, Is.EqualTo(TatooineData.CampShare).Within(0.03));
    }

    /// <summary>At a level both sorts cover (30), a site of one sort draws only that sort.</summary>
    [Test]
    public void ASiteOfOneSortDrawsOnlyThatSort()
    {
        var rng = new Rng(3);
        for (var i = 0; i < 200; i++)
        {
            Assert.That(CreatureTemplates.IsCamp(CreatureTemplates.PickLair(TatooineData.PoiLairTemplates, 30, ref rng)), Is.True);
            Assert.That(CreatureTemplates.IsCamp(CreatureTemplates.PickLair([CreatureTemplates.Rat, CreatureTemplates.Spider], 30, ref rng)), Is.False);
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

    /// <summary>A lair is harder to destroy than any foe it holds, at every level of every kind.</summary>
    [Test]
    public void ALairOutlastsItsOccupants()
    {
        for (var t = 0; t < CreatureTemplates.Count; t++)
        {
            for (var level = 1; level <= ContentLevels.MaxLevel; level++)
            {
                Assert.That(ContentLevels.LairHealth(level, t), Is.GreaterThan(ContentLevels.CreatureHealth(level, CreatureTemplates.IsCamp(t))),
                    $"template {t} at level {level}");
            }
        }
    }
}
