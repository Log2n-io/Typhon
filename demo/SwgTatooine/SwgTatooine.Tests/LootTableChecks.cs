using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The item templates and the loot tables (inventory-loot.md § 3.1, § 4), pure: no simulation. The numbers are the balance model's, pinned here so a knob
/// changed in one place and not the other fails.
/// </summary>
[TestFixture]
internal sealed class LootTableChecks
{
    // MK_ROLL of balance_model.py: the roll's offsets and their chances.
    private static readonly (int D, double P)[] MkRoll = [(-1, 0.20), (0, 0.65), (1, 0.13), (2, 0.02)];

    /// <summary>IL-6: 100 000 rolls at levels 5..45 follow crafting-catalog § 14.3 within a point; never Mk V in the wild; a mission reaches V from 25.</summary>
    [Test]
    public void TheMkRoll_IsCraftingCatalog14_3()
    {
        const int n = 100_000;
        foreach (var level in new[] { 5, 15, 25, 35, 45 })
        {
            foreach (var mission in new[] { false, true })
            {
                var expected = new double[6];
                foreach (var (d, p) in MkRoll)
                {
                    var mk = Math.Clamp(LootTables.Band(level) + d, 1, 5);
                    expected[mission ? mk : Math.Min(mk, 4)] += p;
                }

                var seen = new int[6];
                var rng = new Rng((uint)(level * 7919 + (mission ? 1 : 0)));
                for (var i = 0; i < n; i++)
                {
                    seen[LootTables.RollMk(level, ref rng, mission)]++;
                }

                for (var mk = 1; mk <= 5; mk++)
                {
                    Assert.That((double)seen[mk] / n, Is.EqualTo(expected[mk]).Within(0.01), $"level {level}, mission {mission}: Mk {mk}");
                }

                Assert.That(seen[0], Is.Zero);
                if (!mission)
                {
                    Assert.That(seen[5], Is.Zero, $"level {level}: Mk V dropped in the wild");
                }
                else if (level >= 25)
                {
                    Assert.That(seen[5], Is.GreaterThan(0), $"level {level}: a mission never reached Mk V");
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(LootTables.Band(0), Is.EqualTo(1), "level 0 (none) reads as 1");
            Assert.That(LootTables.Band(10), Is.EqualTo(1));
            Assert.That(LootTables.Band(11), Is.EqualTo(2));
            Assert.That(LootTables.Band(50), Is.EqualTo(5));
        });
    }

    /// <summary>IL-6: the core rates are balance_model.py's supply() per kill and per mission, to 3 significant digits; a wild cache is ten kills.</summary>
    [Test]
    public void TheCoreRates_AreTheModelsSupply()
    {
        // supply(b)[2] and supply(b)[1], printed by the model (90 kills/h, 2 missions/h, supply 1.3 x use, half from mission caches).
        double[] perKill = [0.004157003237526233, 0.0031155107426005567, 0.0024918290471163335, 0.0020763662161896656];
        double[] perMission = [0.18706514568868052, 0.14019798341702505, 0.112132307120235, 0.09343647972853496, 0.08008706157699871];
        for (var i = 0; i < 4; i++)
        {
            Assert.That(LootTables.PCoreKill[i], Is.EqualTo(perKill[i]).Within(perKill[i] * 0.001), $"PCoreKill Mk {i + 1}");
            Assert.That(LootTables.PCoreWildCache[i], Is.EqualTo(10 * LootTables.PCoreKill[i]).Within(1e-6), $"PCoreWildCache Mk {i + 1}");
            Assert.That(LootTables.PAddonKill[i], Is.EqualTo(LootTables.PCoreKill[i]), $"PAddonKill Mk {i + 1}");
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.That(LootTables.PCoreMissionCache[i], Is.EqualTo(perMission[i]).Within(perMission[i] * 0.001), $"PCoreMissionCache Mk {i + 1}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(LootTables.PCoreKill[4], Is.Zero, "a kill never rolls Mk V");
            Assert.That(LootTables.PCoreWildCache[4], Is.Zero, "a wild cache never rolls Mk V");
            Assert.That(LootTables.LootKillUnits, Is.EqualTo(4), "loot_units_per_kill");
            Assert.That(LootTables.MissionCacheUnits, Is.EqualTo(10), "cache_units");
        });
    }

    /// <summary>Each species drops only its families, 4 units a kill; a cache holds 6 or 10 units and exactly one core or add-on, all of one Mk.</summary>
    [Test]
    public void EverySpeciesDropsOnlyItsFamilies()
    {
        for (var species = 0; species < CreatureTemplates.Count; species++)
        {
            var families = new HashSet<ushort>();
            foreach (var f in LootTables.FamiliesOf(species))
            {
                families.Add(f.Template);
            }

            Assert.That(families, Is.Not.Empty, $"species {species} drops nothing");
            var parts = 0;
            for (var k = 0; k < 10_000; k++)
            {
                var kill = LootTables.Roll((byte)species, 15, DropSource.Kill, (ulong)(1000 + k), k);
                var units = 0;
                for (var i = 0; i < kill.Lines; i++)
                {
                    var line = kill[i];
                    Assert.That(line.Mk, Is.EqualTo(kill[0].Mk), "one Mk per drop");
                    if (families.Contains(line.Template))
                    {
                        units += line.Count;
                        continue;
                    }

                    Assert.That(i, Is.Zero, $"species {species}: {line.Template} is not its family, and not the first line");
                    Assert.That(ItemCatalog.TryGet(line.Template, out var t) && t.Category is ItemCategory.Core or ItemCategory.Addon, Is.True);
                    Assert.That(line.Count, Is.EqualTo(1));
                    parts++;
                }

                Assert.That(units, Is.EqualTo(LootTables.LootKillUnits), $"species {species}: a kill's units");
            }

            Assert.That(parts, Is.LessThan(200), "a kill's core or add-on is rare");
            foreach (var (source, want) in new[] { (DropSource.WildCache, LootTables.WildCacheUnits), (DropSource.MissionCache, LootTables.MissionCacheUnits) })
            {
                for (var k = 0; k < 1_000; k++)
                {
                    var cache = LootTables.Roll((byte)species, 30, source, (ulong)(77 + k), k * 3);
                    Assert.That(ItemCatalog.TryGet(cache[0].Template, out var first) && first.Category is ItemCategory.Core or ItemCategory.Addon, Is.True,
                        $"{source}: the first line is not a core or an add-on");
                    Assert.That(cache[0].Count, Is.EqualTo(1));
                    var units = 0;
                    for (var i = 1; i < cache.Lines; i++)
                    {
                        Assert.That(families, Does.Contain(cache[i].Template), $"{source}: a second part, or a foreign family");
                        Assert.That(cache[i].Mk, Is.EqualTo(cache[0].Mk));
                        units += cache[i].Count;
                    }

                    Assert.That(units, Is.EqualTo(want), $"{source}: units");
                    Assert.That(source == DropSource.MissionCache || cache[0].Mk < 5, Is.True, "Mk V outside a mission cache");
                }
            }
        }
    }

    /// <summary>A drop is a function of the victim and the tick: the same death drops the same, another does not (always).</summary>
    [Test]
    public void ARoll_IsDeterministic()
    {
        var a = LootTables.Roll(CreatureTemplates.Spider, 20, DropSource.Kill, 123_456, 789);
        var b = LootTables.Roll(CreatureTemplates.Spider, 20, DropSource.Kill, 123_456, 789);
        Assert.That(a.Lines, Is.EqualTo(b.Lines));
        for (var i = 0; i < a.Lines; i++)
        {
            Assert.That(a[i], Is.EqualTo(b[i]));
        }
    }

    /// <summary>Every template's asset is an <c>item</c> of its category in the built catalogue, with 32 and 48 px icons; the ids are pinned.</summary>
    [Test]
    public void TheTemplates_AreTheCataloguesItems()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ItemCatalog.Templates.Length, Is.EqualTo(66));
            Assert.That(ItemCatalog.IdOf("mat_ore"), Is.EqualTo(1));
            Assert.That(ItemCatalog.IdOf("mat_hide"), Is.EqualTo(8));
            Assert.That(ItemCatalog.IdOf("core_ranged"), Is.EqualTo(12));
            Assert.That(ItemCatalog.IdOf("addon_additive"), Is.EqualTo(19));
            Assert.That(ItemCatalog.IdOf("part_barrel_pistol"), Is.EqualTo(20));
            Assert.That(ItemCatalog.IdOf("part_chassis"), Is.EqualTo(43));
            Assert.That(ItemCatalog.IdOf("armor_padded_helmet"), Is.EqualTo(44));
            Assert.That(ItemCatalog.IdOf("potion_stim"), Is.EqualTo(56));
            Assert.That(ItemCatalog.IdOf("kit_weapon_repair"), Is.EqualTo(62));
            Assert.That(ItemCatalog.IdOf("crate"), Is.EqualTo(66));
            Assert.That(ItemCatalog.IdOf("nothing"), Is.Zero);
        });

        var lootable = 0;
        for (var i = 0; i < ItemCatalog.Templates.Length; i++)
        {
            var t = ItemCatalog.Templates[i];
            Assert.That(t.Id, Is.EqualTo(i + 1), "ids run from 1 without a gap");
            lootable += t.Lootable ? 1 : 0;
        }

        Assert.That(lootable, Is.EqualTo(17));

        var root = FindAssets();
        Assume.That(root, Is.Not.Null, "no built catalogue (SwgTatooine.Assets/build/assets) in this checkout");
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "catalogue.json")));
        var assets = catalogue.RootElement.GetProperty("assets");
        var icons = catalogue.RootElement.GetProperty("icons");
        foreach (var t in ItemCatalog.Templates)
        {
            Assert.That(assets.TryGetProperty(t.Asset, out var asset), Is.True, $"{t.Asset} is not in the catalogue");
            using var descriptor = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, asset.GetProperty("descriptor").GetString())));
            var d = descriptor.RootElement;
            Assert.That(d.GetProperty("kind").GetString(), Is.EqualTo("item"), t.Asset);
            var item = d.GetProperty("item");
            Assert.That(item.GetProperty("category").GetString(), Is.EqualTo(ItemCategory.NameOf(t.Category)), t.Asset);
            Assert.That(item.TryGetProperty("mk", out _), Is.EqualTo(t.HasMk), $"{t.Asset}: its Mk rule");
            Assert.That(icons.TryGetProperty(t.Asset, out var icon), Is.True, $"{t.Asset} has no icon");
            Assert.That(icon.GetProperty("png").TryGetProperty("32", out _) && icon.GetProperty("png").TryGetProperty("48", out _), Is.True, t.Asset);
        }
    }

    private static string FindAssets()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "SwgTatooine.Assets", "build", "assets");
            if (File.Exists(Path.Combine(candidate, "catalogue.json")))
            {
                return candidate;
            }
        }

        return null;
    }
}
