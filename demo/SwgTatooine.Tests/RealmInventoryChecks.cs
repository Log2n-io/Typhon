using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using Typhon.Engine;
using NUnit.Framework;

namespace SwgTatooine.Tests;

/// <summary>
/// What <c>/typhon/realms.json</c> tells a client about what every realm is doing (CLI3D-11).
/// </summary>
/// <remarks>
/// <para>
/// The companion to <see cref="RealmDirectoryChecks"/>, and asserted differently on purpose. The directory is immutable
/// for a run, so it is pinned as an exact string; this one carries live counts, so what can be pinned is its SHAPE —
/// that it parses, that every row has every field, and above all which realms get a row at all. The row policy is the
/// feature: an interior is listed while somebody is in it and is one number in the aggregate once it falls asleep.
/// </para>
/// <para>
/// Built by hand with a <c>StringBuilder</c> and no serializer, so a stray comma is a real failure mode. Every case here
/// parses the document with a real JSON reader rather than matching substrings, which is what makes a missing brace show
/// up as a failure instead of as a passing assertion against a broken document.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RealmInventoryChecks
{
    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            Assert.Fail($"the inventory is not valid JSON: {e.Message}\n{json}");
            throw;
        }
    }

    /// <summary>Every field the client reads, on every row, in one place — so a rename fails here rather than in a browser.</summary>
    private static void AssertRowShape(JsonElement row)
    {
        foreach (var field in new[] { "id", "appTag", "state", "divisor", "sleepAfterTicks", "players", "npcs", "creatures", "structures" })
        {
            Assert.That(row.TryGetProperty(field, out _), Is.True, $"a row is missing \"{field}\": {row}");
        }

        Assert.That(
            row.GetProperty("state").GetString(),
            Is.AnyOf("active", "simulated", "dormant", "closing"),
            "a row carries a state name the client does not know");
    }

    /// <summary>AC-5: the barest world this demo can build still produces a document the client can read.</summary>
    /// <remarks>
    /// One planet, no interiors, no space, no dungeons: the case where every loop that writes a row has nothing to write,
    /// which is exactly where a hand-built document grows a trailing comma or an unclosed array.
    /// </remarks>
    [Test]
    public void OnePlanetAndNothingElseStillProducesAReadableDocument()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            using var sim = new TatooineSim(Worlds.Small(dir));
            sim.Initialize();

            using var doc = Parse(sim.RealmInventoryJson());
            var root = doc.RootElement;
            var realms = root.GetProperty("realms");
            Assert.Multiple(() =>
            {
                Assert.That(realms.GetArrayLength(), Is.EqualTo(1), "a one-planet world has exactly one realm to list");
                Assert.That(realms[0].GetProperty("id").GetInt32(), Is.Zero);
                Assert.That(root.GetProperty("omitted").GetInt32(), Is.Zero);
                Assert.That(root.GetProperty("censusTick").GetInt64(), Is.EqualTo(-1), "a world that has not ticked has no census");
                Assert.That(root.GetProperty("counts").GetProperty("active").GetInt32(), Is.GreaterThanOrEqualTo(0));
            });

            AssertRowShape(realms[0]);
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>AC-5: with everything turned on, every kind of realm is described and the document still parses.</summary>
    [Test]
    public void EveryKindOfRealmIsDescribed()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Planets = 2;
            config.Interiors = true;
            config.Space = true;

            // Interiors stay simulated rather than sleeping, so this case sees them listed and the next one sees them
            // absent. The two together are what pins the row policy rather than one configuration's accident.
            config.InteriorSleepS = 0f;
            using var sim = new TatooineSim(config);
            sim.Initialize();

            using var doc = Parse(sim.RealmInventoryJson());
            var root = doc.RootElement;
            var ids = new HashSet<int>();
            foreach (var row in root.GetProperty("realms").EnumerateArray())
            {
                AssertRowShape(row);
                ids.Add(row.GetProperty("id").GetInt32());
            }

            Assert.Multiple(() =>
            {
                Assert.That(ids, Does.Contain(0).And.Contain(1), "both planets");
                Assert.That(ids, Does.Contain(sim.SpaceRealm), "space");
                Assert.That(ids, Does.Contain(config.Planets), "the first interior, which is not dormant in this configuration");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// A dormant interior is in the aggregate and not in the rows — which is the whole point of the panel.
    /// </summary>
    /// <remarks>
    /// A shipping map has more than twelve hundred interiors across two planets. Listing them all would be a document
    /// nobody could read AND would bury the one fact worth seeing, so the rows are the realms somebody is in and
    /// everything else is the <c>dormant</c> count. A row appearing when a bot walks through a door and falling off the
    /// list two seconds after it leaves is the demonstration, not a limitation of it.
    /// </remarks>
    [Test]
    public void ADormantInteriorIsCountedAndNotListed()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Unpaced = false;
            config.TickRateHz = 40;
            config.WorkerCount = 1;
            config.Interiors = true;
            config.Shuttles = false;
            config.InteriorSleepS = 0.1f;

            using var sim = new TatooineSim(config);
            sim.Initialize();
            var harness = new SessionHarness(sim);
            var interior = (ushort)sim.InteriorsPerPlanet;
            SessionHarness.Until(
                () =>
                {
                    harness.Ticks(1);
                    return sim.Dbe.Realms.StateOf(new RealmId(interior)) == RealmRunState.Dormant;
                },
                "the untouched interior to go dormant");

            using var doc = Parse(sim.RealmInventoryJson());
            var root = doc.RootElement;
            var ids = new HashSet<int>();
            foreach (var row in root.GetProperty("realms").EnumerateArray())
            {
                ids.Add(row.GetProperty("id").GetInt32());
            }

            Assert.Multiple(() =>
            {
                Assert.That(ids, Does.Not.Contain((int)interior), "a dormant interior was listed");
                Assert.That(root.GetProperty("counts").GetProperty("dormant").GetInt32(), Is.GreaterThan(0), "nothing was counted as dormant");
                Assert.That(ids, Does.Contain(0), "the planet stopped being listed");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// A world with more awake interiors than the cap still lists every planet, space and dungeon.
    /// </summary>
    /// <remarks>
    /// <b>The cap is applied in the order rows are emitted, so the emission order is the policy.</b> With interiors
    /// written before dungeons, a busy world silently put every live dungeon in <c>omitted</c> — and a dungeon realm
    /// exists only while a party is inside one, which makes it the least droppable row in the document. The interiors
    /// are interchangeable; a planet, space and a live dungeon are not.
    /// </remarks>
    [Test]
    public void TheAlwaysListedRealmsSurviveAWorldThatOverflowsTheCap()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Planets = 2;
            config.Interiors = true;
            config.Space = true;
            config.Dungeons = 2;

            // Nothing sleeps, so every interior is awake and the cap has to bite.
            config.InteriorSleepS = 0f;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            Assert.That(config.Planets * sim.InteriorsPerPlanet, Is.GreaterThan(TatooineSim.RealmInventoryMaxRows),
                "this world has fewer interiors than the cap, so it cannot exercise the ordering");

            using var doc = Parse(sim.RealmInventoryJson());
            var root = doc.RootElement;
            var ids = new HashSet<int>();
            foreach (var row in root.GetProperty("realms").EnumerateArray())
            {
                ids.Add(row.GetProperty("id").GetInt32());
            }

            Assert.Multiple(() =>
            {
                Assert.That(ids, Does.Contain(0).And.Contain(1), "a planet was dropped to make room for interiors");
                Assert.That(ids, Does.Contain(sim.SpaceRealm), "space was dropped to make room for interiors");
                Assert.That(root.GetProperty("omitted").GetInt32(), Is.GreaterThan(0), "the cap did not bite, so this proves nothing");
            });

            // A live dungeon, staged rather than waited for: the world opens one on its own schedule, and a case that
            // waits for that passes in minutes or never. Registration at run time is the supported path (RLM-06) and is
            // exactly what SimBridge.Dungeons does when a party enters.
            var edge = WorldBuilder.InteriorEdgeM;
            sim.Dbe.Realms.Register(new RealmId((ushort)sim.FirstDungeonRealm), new RealmConfig
            {
                Grid = SpatialGridConfig.Flat(Vector2.Zero, new Vector2(edge, edge), edge),
                WhenUnobserved = RealmUnobserved.Sleep,
                UnobservedTickDivisor = 1,
                SleepAfterTicks = Math.Max(1, config.TickRateHz),
                Parent = RealmId.Default,
                Replication = TatooineSim.DungeonReplication(0),
            });

            using var withDungeon = Parse(sim.RealmInventoryJson());
            var listed = new HashSet<int>();
            foreach (var row in withDungeon.RootElement.GetProperty("realms").EnumerateArray())
            {
                listed.Add(row.GetProperty("id").GetInt32());
            }

            Assert.That(listed, Does.Contain(sim.FirstDungeonRealm),
                "the live dungeon was dropped: six hundred interchangeable interiors consumed the cap ahead of it");
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// AC-7: the row list is bounded, and what it left out is a number rather than a silence.
    /// </summary>
    /// <remarks>
    /// The cap is on what a viewer can take in, so the honest failure is a truncated list that SAYS it is truncated. A
    /// panel showing sixty-four rows out of six hundred with no indication is the one outcome that would mislead.
    /// </remarks>
    [Test]
    public void MoreAwakeRealmsThanTheCapAreCountedRatherThanListed()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Planets = 2;
            config.Interiors = true;

            // Nothing sleeps, so every interior is awake and the cap has to bite.
            config.InteriorSleepS = 0f;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            Assert.That(
                (config.Planets * sim.InteriorsPerPlanet) + config.Planets,
                Is.GreaterThan(TatooineSim.RealmInventoryMaxRows),
                "this world has fewer realms than the cap, so it cannot exercise it");

            using var doc = Parse(sim.RealmInventoryJson());
            var root = doc.RootElement;

            // Every realm this world would list: both planets and all their interiors, none of which sleeps here.
            var listable = config.Planets + (config.Planets * sim.InteriorsPerPlanet);
            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("realms").GetArrayLength(), Is.EqualTo(TatooineSim.RealmInventoryMaxRows), "the cap was not applied");
                Assert.That(root.GetProperty("maxRows").GetInt32(), Is.EqualTo(TatooineSim.RealmInventoryMaxRows));

                // The exact count, not merely "some": `omitted` is what a viewer reads to know how much of the world is
                // off screen, and a constant 1 would satisfy a greater-than-zero assertion while telling them nothing.
                Assert.That(
                    root.GetProperty("realms").GetArrayLength() + root.GetProperty("omitted").GetInt32(),
                    Is.EqualTo(listable),
                    "the rows shown plus the rows counted do not add up to the realms this world would list");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
