using System;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary><c>GET /swg/stats</c>: the demo's own numbers, with their rates, and who is playing where.</summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class DemoStatsChecks : ServedChecks
{
    [Test]
    public void TheRateWindow_IsTheCounterOverTheWindow()
    {
        var window = new RateWindow(2, 60);
        for (var s = 0; s <= 70; s++)
        {
            window.Sample(s, [s * 10L, s * s]);
        }

        Span<double> r10 = stackalloc double[2];
        window.Rates(10, r10);
        var linear = r10[0];
        var square = r10[1];
        Assert.Multiple(() =>
        {
            Assert.That(linear, Is.EqualTo(10d).Within(1e-9), "10 a second over the last 10 s");
            Assert.That(square, Is.EqualTo(((70d * 70d) - (60d * 60d)) / 10d).Within(1e-9), "the last 10 s of a square, not its whole run");
        });

        var fresh = new RateWindow(1, 60);
        Span<double> none = stackalloc double[1];
        fresh.Rates(10, none);
        var empty = none[0];
        Assert.That(empty, Is.Zero, "a rate from fewer than two samples");
    }

    [Test]
    [CancelAfter(60_000)]
    public void TheDemoStats_AreJson_AndCountWhoPlays()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7301));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar");
        Harness.Ticks(Worlds.ServedHz * 3);

        using var doc = JsonDocument.Parse(sim.Bridge.DemoStatsJson());
        var root = doc.RootElement;
        var population = root.GetProperty("population");
        Assert.Multiple(() =>
        {
            Assert.That(population.GetProperty("people").GetInt32(), Is.EqualTo(1), "the one person logged in");
            Assert.That(population.GetProperty("bots").GetInt32(), Is.Zero);
            Assert.That(population.GetProperty("crowd").GetInt32(), Is.GreaterThan(0), "the scripted crowd");
            Assert.That(population.GetProperty("botsByCity").EnumerateObject().Count(), Is.EqualTo(sim.Index.Cities.Count));
            Assert.That(root.GetProperty("economy").GetProperty("walletsOpened").GetInt64(), Is.EqualTo(1));
            Assert.That(root.GetProperty("economy").GetProperty("transfersPerSec").GetProperty("60s").GetDouble(), Is.GreaterThan(0d), "the grant shows in the rate");
            Assert.That(root.GetProperty("missions").GetProperty("offers").GetInt32(), Is.GreaterThan(0));
            Assert.That(root.GetProperty("crowd").TryGetProperty("lairHitsPerSec", out _), Is.True);
        });

        // Content levels (content-levels.md § Monitoring): the director's walk counts the standing lairs by band — every one of them — and the ones the
        // crowd may fight, which the fringes fill.
        var standing = 0;
        var belowCrowd = 0;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
            {
                var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var lair = spawners[System.Numerics.BitOperations.TrailingZeroCount(bits)];
                    standing++;
                    belowCrowd += lair.Level <= TatooineData.CrowdLevel && lair.Mission.IsNull ? 1 : 0;
                }
            }
        }

        var levels = root.GetProperty("levels");
        var byBand = levels.GetProperty("lairsByBand").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(byBand, Has.Length.EqualTo(5));
            Assert.That(byBand.Sum(), Is.EqualTo(standing).Within(3), "the bands do not count the lairs standing");
            Assert.That(byBand[0], Is.GreaterThan(0), "no lair in band I: the fringes are empty");
            Assert.That(levels.GetProperty("creaturesByBand").GetArrayLength(), Is.EqualTo(5));
            Assert.That(levels.GetProperty("lairsBelowCrowdLevel").GetInt32(), Is.EqualTo(belowCrowd).Within(3).And.GreaterThan(0));
            Assert.That(levels.GetProperty("crowdObjectiveLevelMax").GetInt32(), Is.InRange(0, TatooineData.CrowdLevel));
        });
    }

    /// <summary>CH-11: <c>/swg/stats</c> carries the progression section: XP by source, the gates, the bands, XP per character-hour against the model's.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void TheStats_CarryTheProgressionSection()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7311));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar");
        Harness.Ticks(Worlds.ServedHz * 3);

        using var doc = JsonDocument.Parse(sim.Bridge.DemoStatsJson());
        var progression = doc.RootElement.GetProperty("progression");
        Assert.Multiple(() =>
        {
            foreach (var key in new[] { "levelUps", "killXp", "missionXp", "greyKills", "worthlessKills", "noCharacterKills", "crowdKills", "noKillerKills",
                         "missionLairKills", "killsDrained", "commitsFailed", "killXpOverflow", "killLootOverflow", "progressOverflow", "xpRatePercent",
                         "modelXpPerCharHour" })
            {
                Assert.That(progression.TryGetProperty(key, out _), Is.True, key);
            }

            var bands = progression.GetProperty("bands");
            Assert.That(bands.GetProperty("people").EnumerateArray().Select(b => b.GetInt32()), Is.EqualTo(new[] { 1, 0, 0, 0, 0 }), "one person, CL 1");
            Assert.That(bands.GetProperty("bots").EnumerateArray().Sum(b => b.GetInt32()), Is.Zero);
            Assert.That(progression.GetProperty("meanCl").GetProperty("people").GetDouble(), Is.EqualTo(1d));
            Assert.That(progression.GetProperty("modelXpPerCharHour").GetDouble(), Is.EqualTo(1200d), "1 200 × mean CL 1");
            Assert.That(progression.GetProperty("xpPerCharHour").TryGetProperty("60s", out _), Is.True);
            Assert.That(progression.GetProperty("levelUpsPerMin").TryGetProperty("10s", out _), Is.True);
            Assert.That(progression.GetProperty("xpRatePercent").GetInt32(), Is.EqualTo(100));
            Assert.That(progression.GetProperty("progressOverflow").GetInt64(), Is.Zero);
        });
    }

    /// <summary>IL-13: the <c>items</c> section counts the rows, the drops and the units minted and held by template and Mk, as the rows say.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheItemsSection_CountsUnitsByTemplateAndMk()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7302));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar");
        var character = EntityId.Null;
        SessionHarness.Until(() => SwgTatooine.Server.Accounts.CharacterStore.TryFind(sim.Dbe, EntityId.FromRawValue(0x7302), out character, out _),
            "the character to be committed");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        var owner = (long)character.RawValue;
        sim.Bridge.StageTransfer(ItemLedger.World, owner, ItemCatalog.Hide, 2, 7, ItemMoveKind.Kill, 1);
        sim.Bridge.StageTransfer(ItemLedger.World, owner, ItemCatalog.Bone, 1, 3, ItemMoveKind.Kill, 1);
        sim.Bridge.StageTransfer(ItemLedger.World, owner, ItemCatalog.Hide, 2, 2, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Items.Rows(sim).Count == 3, "the three rows");
        Harness.Ticks(Worlds.ServedHz * 2);

        using var doc = JsonDocument.Parse(sim.Bridge.DemoStatsJson());
        var items = doc.RootElement.GetProperty("items");
        Assert.Multiple(() =>
        {
            Assert.That(items.GetProperty("rows").GetInt64(), Is.EqualTo(3));
            Assert.That(items.GetProperty("units").GetInt64(), Is.EqualTo(12));
            Assert.That(items.GetProperty("stacks").GetInt64(), Is.EqualTo(2));
            Assert.That(items.GetProperty("minted").GetProperty("mat_hide").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new long[] { 0, 9, 0, 0, 0 }));
            Assert.That(items.GetProperty("held").GetProperty("mat_hide").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new long[] { 0, 9, 0, 0, 0 }));
            Assert.That(items.GetProperty("held").GetProperty("mat_bone").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new long[] { 3, 0, 0, 0, 0 }));
            Assert.That(items.GetProperty("minted").TryGetProperty("mat_metal", out _), Is.False, "a template with nothing minted");
            Assert.That(items.GetProperty("destroyed").EnumerateObject().Count(), Is.Zero);
            Assert.That(items.GetProperty("unitsPerMin").GetProperty("60s").GetDouble(), Is.GreaterThan(0d), "the units show in the rate");
            Assert.That(items.TryGetProperty("bySource", out _) && items.TryGetProperty("lostPerMin", out _) && items.TryGetProperty("queueOverflow", out _), Is.True);
        });
    }
}
