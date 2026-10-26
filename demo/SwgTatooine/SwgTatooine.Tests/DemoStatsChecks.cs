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
            foreach (var key in new[] { "levelUps", "killXp", "missionXp", "greyKills", "crowdKills", "noKillerKills", "missionLairKills", "killsDrained",
                         "commitsFailed", "xpRatePercent", "modelXpPerCharHour" })
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
        });
    }
}
