using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A character's own waypoints on the server (client.md C38, U2): created, listed by <c>GET /swg/waypoints</c>, made active and deleted through the real
/// commands; every refusal with its reason, writing nothing; kept across a reopen; an installation's pin deleted when it is picked up.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class WaypointChecks : ServedChecks
{
    private TatooineSim Start(bool persist = false)
    {
        var config = Worlds.Served(Dir);
        config.CommandRateScale = Worlds.FastCommands;
        config.WorkerCount = 1;
        config.Persist = persist;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private sealed record Owner(FakeLink Link, EntityId Character)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    private Owner LogIn(TatooineSim sim, ulong account, long bindings = 1)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        // Waited for, not asserted: on CI's runner the character was not found yet, two ticks after the possession, in five fixtures (PR #1590).
        var characterAccount = TestAccounts.For(sim.Dbe, account);
        var character = EntityId.Null;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, characterAccount, out character, out _), "the account's character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return new Owner(link, character);
    }

    private void Breathe() => Harness.Ticks(Worlds.FastCommandGapTicks);

    private static void Create(FakeLink link, string name, float x, float z, uint color = 0, long installation = 0)
        => link.SendCommand(
            nameof(CreateWaypoint),
            new RecordValues
            {
                ["name"] = FieldValue.Of(name),
                ["x"] = FieldValue.Of(x),
                ["z"] = FieldValue.Of(z),
                ["color"] = FieldValue.Of(color),
                ["installation"] = FieldValue.OfUInt64(unchecked((ulong)installation)),
            });

    private static void Delete(FakeLink link, long waypoint)
        => link.SendCommand(nameof(DeleteWaypoint), new RecordValues { ["waypoint"] = FieldValue.OfUInt64(unchecked((ulong)waypoint)) });

    private static void Activate(FakeLink link, long waypoint)
        => link.SendCommand(nameof(SetActiveWaypoint), new RecordValues { ["waypoint"] = FieldValue.OfUInt64(unchecked((ulong)waypoint)) });

    private sealed record Listed(long Id, string Name, int Kind, uint Color, float X, float Z, long Installation, bool Active);

    /// <summary>The character's waypoints as <c>GET /swg/waypoints</c> lists them.</summary>
    private static List<Listed> Of(TatooineSim sim, Owner o)
    {
        using var json = JsonDocument.Parse(WaypointEndpoints.Json(sim.Dbe, o.Key));
        Assert.That(json.RootElement.GetProperty("max").GetInt32(), Is.EqualTo(Waypoints.MaxPerCharacter));
        return json.RootElement.GetProperty("waypoints").EnumerateArray()
            .Select(w => new Listed(
                long.Parse(w.GetProperty("id").GetString()),
                w.GetProperty("name").GetString(),
                w.GetProperty("kind").GetInt32(),
                w.GetProperty("color").GetUInt32(),
                w.GetProperty("x").GetSingle(),
                w.GetProperty("z").GetSingle(),
                long.Parse(w.GetProperty("installation").GetString()),
                w.GetProperty("active").GetBoolean()))
            .OrderBy(w => w.Id)
            .ToList();
    }

    private static int Count(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    /// <summary>U2-1: two waypoints made, one of them coloured; listed; one made active, then the other; one deleted, the active one gone with it.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void AWaypoint_IsCreated_Listed_MadeActive_AndDeleted()
    {
        using var sim = Start();
        var o = LogIn(sim, 0xA701);
        Create(o.Link, "  Krayt   bones ", 1200.5f, -3400f);
        Breathe();
        Create(o.Link, "Water", -50f, 75f, 0x3399FF);
        SessionHarness.Until(() => sim.Bridge.WaypointsCreated == 2, "two waypoints");
        var two = Of(sim, o);
        Assert.Multiple(() =>
        {
            Assert.That(two.Select(w => w.Name), Is.EquivalentTo(new[] { "Krayt bones", "Water" }), "the name trimmed, its spaces made one");
            Assert.That(two.All(w => w.Kind == WaypointKind.Custom && !w.Active && w.Installation == 0), Is.True);
            Assert.That(two.Single(w => w.Name == "Water").Color, Is.EqualTo(0x3399FFu));
            Assert.That(two.Single(w => w.Name == "Krayt bones").Color, Is.Zero, "no colour: the kind's");
            Assert.That((two.Single(w => w.Name == "Krayt bones").X, two.Single(w => w.Name == "Krayt bones").Z), Is.EqualTo((1200.5f, -3400f)));
        });

        var krayt = two.Single(w => w.Name == "Krayt bones").Id;
        var water = two.Single(w => w.Name == "Water").Id;
        Activate(o.Link, krayt);
        SessionHarness.Until(() => Of(sim, o).Any(w => w.Active), "the active one");
        Breathe();
        Activate(o.Link, water);
        SessionHarness.Until(() => Of(sim, o).Single(w => w.Id == water).Active, "the other active");
        Assert.That(Of(sim, o).Count(w => w.Active), Is.EqualTo(1), "one active at most");

        Delete(o.Link, water);
        SessionHarness.Until(() => sim.Bridge.WaypointsDeleted == 1, "the delete");
        Assert.Multiple(() =>
        {
            Assert.That(Of(sim, o).Select(w => w.Id), Is.EqualTo(new[] { krayt }));
            Assert.That(Of(sim, o).Any(w => w.Active), Is.False, "the active one deleted: none active");
        });
    }

    /// <summary>U2-2: no name, a colour that is not one, a point off the planet, past the 100, another's waypoint or none — refused, nothing written.</summary>
    [Test]
    [CancelAfter(180_000)]
    public void EveryWaypointRefusal_HasItsReason_AndWritesNothing()
    {
        using var sim = Start();
        var o = LogIn(sim, 0xA702);
        var other = LogIn(sim, 0xA703, 2);
        Create(other.Link, "Theirs", 10f, 10f);
        SessionHarness.Until(() => sim.Bridge.WaypointsCreated == 1, "the other's waypoint");
        var theirs = Of(sim, other).Single().Id;

        Create(o.Link, "   ", 0f, 0f);
        Breathe();
        Create(o.Link, "Bad colour", 0f, 0f, 0x1000000);
        Breathe();
        Create(o.Link, "Off planet", TatooineData.PlanetHalfExtentM + 1f, 0f);
        Breathe();
        Create(o.Link, "No installation", 0f, 0f, 0, 12345);
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.WaypointBad) == 4, "four bad waypoints");
        Delete(o.Link, theirs);
        Breathe();
        Activate(o.Link, theirs);
        Breathe();
        Delete(o.Link, 999_999);
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.WaypointUnknown) == 3, "three unknown waypoints");
        Assert.Multiple(() =>
        {
            Assert.That(Of(sim, o), Is.Empty, "a refusal wrote a waypoint");
            Assert.That(Of(sim, other).Single().Active, Is.False, "another's waypoint activated");
            Assert.That(sim.Bridge.WaypointsCreated, Is.EqualTo(1));
            Assert.That(sim.Bridge.WaypointsDeleted, Is.Zero);
        });

        // The hundredth is the last.
        for (var i = 0; i < Waypoints.MaxPerCharacter; i++)
        {
            Create(o.Link, $"w{i}", i, i);
            Breathe();
        }

        SessionHarness.Until(() => sim.Bridge.WaypointsCreated == 1 + Waypoints.MaxPerCharacter, "a hundred waypoints");
        Create(o.Link, "one too many", 0f, 0f);
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.WaypointsFull) == 1, "the hundred-and-first refused");
        Assert.That(Of(sim, o), Has.Count.EqualTo(Waypoints.MaxPerCharacter));
    }

    /// <summary>U2-3: the waypoints outlive a stop: reopened, the same names, colours, places and the active one.</summary>
    [Test]
    [CancelAfter(240_000)]
    public void Waypoints_SurviveAStop()
    {
        List<Listed> before;
        using (var first = Start(persist: true))
        {
            var o = LogIn(first, 0xA704);
            Create(o.Link, "Home", 100f, 200f, 0xFF8800);
            Breathe();
            Create(o.Link, "Away", -300f, 400f);
            SessionHarness.Until(() => first.Bridge.WaypointsCreated == 2, "two waypoints");
            Activate(o.Link, Of(first, o).Single(w => w.Name == "Away").Id);
            SessionHarness.Until(() => Of(first, o).Any(w => w.Active), "the active one");
            before = Of(first, o);
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True);
        var again = LogIn(second, 0xA704);
        Assert.That(Of(second, again), Is.EqualTo(before));

        // And the Workshop reads them back: the reopened ones count, and an activation clears the old active one.
        Activate(again.Link, before.Single(w => w.Name == "Home").Id);
        SessionHarness.Until(() => Of(second, again).Single(w => w.Name == "Home").Active, "the other active after the reopen");
        Assert.That(Of(second, again).Count(w => w.Active), Is.EqualTo(1));
    }

    /// <summary>
    /// U2-4: an installation's pin — where it stands, kind installation — refused a second time and for another's installation, and gone when it is
    /// picked up.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AnInstallationsPin_FollowsIt_AndGoesWithIt()
    {
        using var sim = Start();
        var o = LogIn(sim, 0xA705);
        var other = LogIn(sim, 0xA706, 2);
        var before = Items.Stacks(sim, o.Character).Select(s => s.Id).ToHashSet();
        sim.Bridge.StageInstance(o.Key, ItemCatalog.Extractor, 0, Installations.StandardKit());
        EntityId kit = default;
        SessionHarness.Until(
            () =>
            {
                kit = Items.Stacks(sim, o.Character).FirstOrDefault(s => s.Stack.Template == ItemCatalog.Extractor && !before.Contains(s.Id)).Id;
                return !kit.IsNull;
            },
            "the staged kit");

        // On wild ground east of city 0.
        var city = sim.Index.Cities[0];
        var (x, z) = Installations.CentreOf(Installations.LotOf(0, city.X + city.Radius + 600f, city.Z));
        Assume.That(sim.Index.Levels.NearCity(x, z, Installations.CityMarginM + 10f), Is.False, "the premise: wild ground east of city 0");
        Worlds.Park(sim, o.Link.Session, x, z);
        Harness.Ticks(2);
        o.Link.SendCommand(
            nameof(PlaceInstallation),
            new RecordValues { ["item"] = FieldValue.OfUInt64(kit.RawValue), ["family"] = FieldValue.Of(ItemCatalog.Metal) });
        SessionHarness.Until(() => sim.Bridge.InstallationsPlaced == 1, "the extractor placed");
        using var installations = JsonDocument.Parse(InstallationEndpoints.Json(sim.Dbe, o.Key));
        var installation = long.Parse(installations.RootElement.GetProperty("installations")[0].GetProperty("id").GetString());
        Breathe();

        Create(o.Link, "My extractor", 0f, 0f, 0, installation);
        SessionHarness.Until(() => sim.Bridge.WaypointsCreated == 1, "the pin");
        var pin = Of(sim, o).Single();
        Assert.Multiple(() =>
        {
            Assert.That(pin.Kind, Is.EqualTo(WaypointKind.Installation));
            Assert.That(pin.Installation, Is.EqualTo(installation));
            Assert.That((pin.X, pin.Z), Is.EqualTo((x, z)), "where it stands, not where it was asked");
        });

        Breathe();
        Create(o.Link, "Again", 0f, 0f, 0, installation);
        Breathe();
        Create(other.Link, "Not mine", 0f, 0f, 0, installation);
        SessionHarness.Until(
            () => Count(o.Link, TatooineReplication.WaypointBad) == 1 && Count(other.Link, TatooineReplication.WaypointBad) == 1,
            "a second pin and another's refused");

        Breathe();
        o.Link.SendCommand(nameof(PickUpInstallation), new RecordValues { ["installation"] = FieldValue.OfUInt64(unchecked((ulong)installation)) });
        SessionHarness.Until(() => sim.Bridge.InstallationsPickedUp == 1, "the pick-up");
        Assert.Multiple(() =>
        {
            Assert.That(Of(sim, o), Is.Empty, "the pin went with it");
            Assert.That(sim.Bridge.WaypointsDeleted, Is.EqualTo(1));
        });
    }
}
