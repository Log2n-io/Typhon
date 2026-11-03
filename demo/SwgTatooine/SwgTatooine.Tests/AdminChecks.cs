using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The back office's writes (accounts.md § 2; the dashboard's accounts page): credits given and taken, items given, taken and rewritten, a level set up and
/// down — each through the code path every other write takes, as <c>admingrant</c> and <c>admintake</c> rows, the audits clean; a worn item's new stats and
/// a new level on the body at once; every refusal writing nothing.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class AdminChecks : ServedChecks
{
    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;

        // The welcome pack's blaster, worn: the item whose stats a rewrite must carry to the body's gear.
        config.WelcomePack = true;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private (EntityId Body, EntityId Character) LogIn(TatooineSim sim, ulong account)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar to be spawned");
        Assert.That(CharacterStore.TryFind(sim.Dbe, TestAccounts.For(sim.Dbe, account), out var character, out _), Is.True);
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        SessionHarness.Until(() => Items.Stacks(sim, character).Any(s => s.Stack.Slot == EquipSlot.Weapon), "the welcome blaster, worn");
        Harness.Ticks(2);
        return (TatooineReplication.ControlledBy(sim.Dbe, link.Session), character);
    }

    private static AdminOutcome Do(TatooineSim sim, AdminRequest request)
    {
        var done = sim.Bridge.Admin(request);
        SessionHarness.Until(() => done.IsCompleted, $"the {request.Action} request answered");
        return done.Result;
    }

    // The Padded Segment: a part made with a grade, so an item with stats, and no condition.
    private static readonly ushort PaddedSegment = ItemCatalog.IdOf("part_padded_segment");

    private static AdminRequest Req(AdminAction action, EntityId character) => new() { Action = action, Character = unchecked((long)character.RawValue) };

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    private static T InTick<T>(TatooineSim sim, Func<Transaction, T> read)
    {
        var value = default(T);
        sim.WriteInTick(tx => value = read(tx));
        return value;
    }

    /// <summary>
    /// Credits given then taken: an <c>admingrant</c> row from World and an <c>admintake</c> row to it, the balance moved by each; more than it holds, an
    /// amount of zero and one past the ceiling refused, writing no row; the economy's audit clean — conservation included.
    /// </summary>
    /// <summary>
    /// A request its caller gave up on — the endpoint's "no tick answered" — is never applied, however late the tick comes (review, 2026-11-03): retried, it
    /// would have been applied twice.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ARequestGivenUpOn_IsNeverApplied()
    {
        using var sim = Start();
        var (_, character) = LogIn(sim, 0xAD09);
        var before = Ledgers.Balance(sim, character);
        var r = Req(AdminAction.Credits, character);
        r.Delta = 5_000;
        Assert.That(r.TryAbandon(), Is.True);
        var done = sim.Bridge.Admin(r);
        Harness.Ticks(10);
        Assert.Multiple(() =>
        {
            Assert.That(done.IsCompleted, Is.False, "never answered: nobody waits for it");
            Assert.That(Ledgers.Balance(sim, character), Is.EqualTo(before), "applied after it was given up");
        });
    }

    /// <summary>A body that overflows a field or lacks what its action needs is refused as malformed, never wrapped into a valid value.</summary>
    [Test]
    public void ARequestsBody_IsReadStrictly()
    {
        using var overflow = JsonDocument.Parse("{\"template\":69,\"mk\":1,\"count\":4294967297}");
        using var noStats = JsonDocument.Parse("{\"item\":\"5\"}");
        Assert.Multiple(() =>
        {
            Assert.Throws<OverflowException>(() => AdminEndpoints.Read(overflow.RootElement, AdminAction.AddItem, EntityId.Null));
            Assert.Throws<KeyNotFoundException>(() => AdminEndpoints.Read(noStats.RootElement, AdminAction.ItemStats, EntityId.Null));
        });
    }

    [Test]
    [CancelAfter(120_000)]
    public void Credits_AreGivenAndTakenAsLedgerRows_AndARefusalWritesNothing()
    {
        using var sim = Start();
        var (_, character) = LogIn(sim, 0xAD01);
        var key = unchecked((long)character.RawValue);
        var before = Ledgers.Balance(sim, character);

        Assert.That(Do(sim, Req(AdminAction.Credits, character)).Ok, Is.False, "a request of zero credits is refused");

        var r = Req(AdminAction.Credits, character);
        r.Delta = 5_000;
        Assert.That(Do(sim, r).Ok, Is.True);
        r = Req(AdminAction.Credits, character);
        r.Delta = -1_200;
        Assert.That(Do(sim, r).Ok, Is.True);
        var rows = Ledgers.Rows(sim).Count;

        r = Req(AdminAction.Credits, character);
        r.Delta = -(before + 10_000);
        var refused = Do(sim, r);
        r = Req(AdminAction.Credits, character);
        r.Delta = SimBridge.MaxAdminCredits + 1;
        var tooMuch = Do(sim, r);

        var admin = Ledgers.Rows(sim).Where(e => e.Kind is TransferKind.AdminGrant or TransferKind.AdminTake).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(Ledgers.Balance(sim, character), Is.EqualTo(before + 5_000 - 1_200));
            Assert.That(admin, Has.Count.EqualTo(2));
            Assert.That(admin[0].From == EconomyLedger.World && admin[0].To == key && admin[0].Amount == 5_000, Is.True, "the grant, World to the wallet");
            Assert.That(admin[1].From == key && admin[1].To == EconomyLedger.World && admin[1].Amount == 1_200, Is.True, "the take, the wallet to World");
            Assert.That(refused.Ok, Is.False, "more than it holds");
            Assert.That(refused.Message, Does.Contain("holds"));
            Assert.That(tooMuch.Ok, Is.False, "past the ceiling");
            Assert.That(Ledgers.Rows(sim), Has.Count.EqualTo(rows), "a refusal writes no row");
        });
        AuditsOk(sim, "after the credits");
    }

    /// <summary>
    /// Items given and taken: 150 Hide Mk II as two stacks; a Mk V blaster with the operator's stats (only an operator, a mission or a craft mints Mk V);
    /// 30 hide taken, the blaster taken; every row an <c>admingrant</c> or <c>admintake</c>; a template, a Mk or a count the catalogue refuses written nowhere;
    /// the item audit clean.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void Items_AreGivenAndTakenThroughTheItemLedger_AtAnyMk()
    {
        using var sim = Start();
        var (_, character) = LogIn(sim, 0xAD02);

        var hide = Req(AdminAction.AddItem, character);
        hide.Template = ItemCatalog.Hide;
        hide.Mk = 2;
        hide.Count = 150;
        Assert.That(Do(sim, hide).Ok, Is.True);

        var blaster = Req(AdminAction.AddItem, character);
        blaster.Template = ItemCatalog.Blaster;
        blaster.Mk = 5;
        blaster.Count = 1;
        blaster.HasStats = true;
        blaster.Stats = new ItemInstance { Stat0 = 900, Stat1 = 600, Stat2 = 300, Condition = 50_000, MaxCondition = 60_000, LifeMilli = 1000 };
        Assert.That(Do(sim, blaster).Ok, Is.True);

        var stacks = Items.Stacks(sim, character);
        var minted = stacks.Single(s => s.Stack.Template == ItemCatalog.Blaster && s.Stack.Mk == 5);
        var stats = InTick(sim, tx => tx.Open(minted.Id).Read(ItemInstanceArch.Stats));
        Assert.Multiple(() =>
        {
            Assert.That(Market.Units(sim, character, ItemCatalog.Hide, 2), Is.EqualTo(150));
            Assert.That(stacks.Count(s => s.Stack.Template == ItemCatalog.Hide), Is.EqualTo(2), "99 and 51");
            Assert.That((stats.Stat0, stats.Stat1, stats.Stat2, stats.Condition, stats.MaxCondition), Is.EqualTo((900, 600, 300, 50_000, 60_000)));
        });

        var rows = Items.Rows(sim).Count;
        var bad = Req(AdminAction.AddItem, character);
        bad.Template = ItemCatalog.Blaster;
        bad.Mk = 0;
        bad.Count = 1;
        var badMk = Do(sim, bad);
        bad = Req(AdminAction.AddItem, character);
        bad.Template = 9_999;
        bad.Count = 1;
        var badTemplate = Do(sim, bad);
        Assert.Multiple(() =>
        {
            Assert.That(badMk.Ok, Is.False, "a blaster has a Mk");
            Assert.That(badTemplate.Ok, Is.False, "no such template");
            Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "a refusal writes no row");
        });

        var some = Req(AdminAction.RemoveItem, character);
        some.Item = unchecked((long)stacks.First(s => s.Stack.Template == ItemCatalog.Hide).Id.RawValue);
        some.Count = 30;
        Assert.That(Do(sim, some).Ok, Is.True);
        var gone = Req(AdminAction.RemoveItem, character);
        gone.Item = unchecked((long)minted.Id.RawValue);
        Assert.That(Do(sim, gone).Ok, Is.True);

        // A part has a grade and no condition: its stats rewritten with a maximum of 0 (seen on the live page, 2026-11-02).
        var part = Req(AdminAction.AddItem, character);
        part.Template = PaddedSegment;
        part.Count = 1;
        Assert.That(Do(sim, part).Ok, Is.True);
        var segment = Items.Stacks(sim, character).Single(s => s.Stack.Template == PaddedSegment).Id;
        var grade = Req(AdminAction.ItemStats, character);
        grade.Item = unchecked((long)segment.RawValue);
        grade.HasStats = true;
        grade.Stats = new ItemInstance { Stat0 = 950, LifeMilli = 1000 };
        var regraded = Do(sim, grade);
        Assert.That(regraded.Ok, Is.True, regraded.Message);
        Assert.That(InTick(sim, tx => tx.Open(segment).Read(ItemInstanceArch.Stats)).Stat0, Is.EqualTo(950));

        var admin = Items.Rows(sim).Where(e => e.Kind is ItemMoveKind.AdminGrant or ItemMoveKind.AdminTake).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(Market.Units(sim, character, ItemCatalog.Hide, 2), Is.EqualTo(120));
            Assert.That(Items.Stacks(sim, character).Any(s => s.Id == minted.Id), Is.False, "the blaster destroyed");
            Assert.That(admin.Where(e => e.Kind == ItemMoveKind.AdminGrant).Sum(e => e.Count), Is.EqualTo(152), "the hide, the blaster, the part");
            Assert.That(admin.Where(e => e.Kind == ItemMoveKind.AdminTake).Sum(e => e.Count), Is.EqualTo(31));
            Assert.That(admin.All(e => (e.Kind == ItemMoveKind.AdminGrant) == (e.From == ItemLedger.World)), Is.True);
        });
        AuditsOk(sim, "after the items");
    }

    /// <summary>
    /// A worn item's stats rewritten in place: the body's gear carries the new damage, delay and reach on that very tick's pass, no ledger row is written,
    /// and its crafter is not the operator's to change. Then the worn item taken: the body is back to its fist.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AWornItem_RewrittenOrTaken_ReachesTheBodysGear()
    {
        using var sim = Start();
        var (body, character) = LogIn(sim, 0xAD03);
        var worn = Items.Stacks(sim, character).Single(s => s.Stack.Slot == EquipSlot.Weapon);
        var rows = Items.Rows(sim).Count;

        var rewrite = Req(AdminAction.ItemStats, character);
        rewrite.Item = unchecked((long)worn.Id.RawValue);
        rewrite.HasStats = true;
        rewrite.Stats = new ItemInstance { Stat0 = 777, Stat1 = 450, Stat2 = 250, Condition = 1_000, MaxCondition = 2_000, LifeMilli = 1000, Crafter = 42 };
        Assert.That(Do(sim, rewrite).Ok, Is.True);
        Harness.Ticks(2);

        var gear = InTick(sim, tx => tx.Open(body).Read(Player.Gear));
        var stats = InTick(sim, tx => tx.Open(worn.Id).Read(ItemInstanceArch.Stats));
        Assert.Multiple(() =>
        {
            Assert.That((gear.Damage, gear.DelayMs, gear.RangeDm), Is.EqualTo(((short)777, (short)450, (short)250)));
            Assert.That(stats.Crafter, Is.Zero, "the welcome blaster's crafter stays nobody");
            Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "a rewrite is not a move");
        });

        var take = Req(AdminAction.RemoveItem, character);
        take.Item = unchecked((long)worn.Id.RawValue);
        Assert.That(Do(sim, take).Ok, Is.True);
        Harness.Ticks(2);
        gear = InTick(sim, tx => tx.Open(body).Read(Player.Gear));
        Assert.That(gear.Damage, Is.EqualTo((short)Equipment.UnarmedDamage), "the fist");
        AuditsOk(sim, "after the worn blaster was taken");
    }

    /// <summary>
    /// A level set up then down: the character's progress at the band's start, the body's level and max health with it — down as well as up, the health
    /// capped; an XP alone sets the level its band says; a level past 50 refused.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ALevel_SetUpOrDown_ReachesTheBody()
    {
        using var sim = Start();
        var (body, character) = LogIn(sim, 0xAD04);

        (int Level, int Xp, int BodyLevel, int MaxHealth, int Health) Read()
        {
            Harness.Ticks(2);
            using var tx = sim.Dbe.CreateQuickTransaction();
            var p = tx.Open(character).Read(CharacterArch.Progress);
            var (look, vitals) = InTick(sim, t =>
            {
                var b = t.Open(body);
                return (b.Read(Player.Look), b.Read(Player.Vitals));
            });
            return (p.Level, p.Xp, look.Level, vitals.MaxHealth, vitals.Health);
        }

        var up = Req(AdminAction.Progress, character);
        up.Level = 20;
        Assert.That(Do(sim, up).Ok, Is.True);
        var high = Read();

        var down = Req(AdminAction.Progress, character);
        down.Level = 5;
        Assert.That(Do(sim, down).Ok, Is.True);
        var low = Read();

        var byXp = Req(AdminAction.Progress, character);
        byXp.Xp = Progression.XpAt(12) + 5;
        Assert.That(Do(sim, byXp).Ok, Is.True);
        var mid = Read();

        var past = Req(AdminAction.Progress, character);
        past.Level = Progression.MaxLevel + 1;
        Assert.Multiple(() =>
        {
            Assert.That(high, Is.EqualTo((20, Progression.XpAt(20), 20, Progression.HealthFor(20), high.Health)));
            Assert.That(low.Level == 5 && low.Xp == Progression.XpAt(5) && low.BodyLevel == 5, Is.True, $"down: {low}");
            Assert.That(low.MaxHealth, Is.EqualTo(Progression.HealthFor(5)));
            Assert.That(low.Health, Is.LessThanOrEqualTo(low.MaxHealth), "the health capped to the lower maximum");
            Assert.That((mid.Level, mid.Xp, mid.BodyLevel), Is.EqualTo((12, Progression.XpAt(12) + 5, 12)));
            Assert.That(Do(sim, past).Ok, Is.False);
        });
    }

    /// <summary>
    /// The routes exist only with a key long enough, and answer only a request carrying it: none without it, 401 for a wrong or missing header; the
    /// listing finds a character by its name; the catalogue carries a blaster's defaults for every Mk; a write's body that names nothing is a 400.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task TheRoutes_ExistOnlyWithAKey_AndAnswerOnlyItsHolder()
    {
        using var sim = Start();
        var (_, character) = LogIn(sim, 0xAD05);
        const string key = "a-key-long-enough-for-the-office";

        await using var off = await Host(sim, null);
        using (var c = Client(off, key))
        {
            Assert.That((await c.GetAsync("admin/accounts")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "no key on the server: no route");
        }

        await using var shortKey = await Host(sim, "too-short");
        using (var c = Client(shortKey, "too-short"))
        {
            Assert.That((await c.GetAsync("admin/accounts")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "a short key: no route either");
        }

        await using var on = await Host(sim, key);
        using var bare = Client(on, null);
        using var wrong = Client(on, key + "!");
        using var right = Client(on, key);
        Assert.Multiple(async () =>
        {
            Assert.That((await bare.GetAsync("admin/accounts")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await wrong.GetAsync("admin/catalog")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await wrong.PostAsync($"admin/character/{character.RawValue}/credits", Json("{\"delta\":5}"))).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
        });

        var name = InTick(sim, tx => tx.Open(character).Read(CharacterArch.Profile).Name.AsString);
        var listing = JsonNode.Parse(await right.GetStringAsync($"admin/accounts?q={Uri.EscapeDataString(name)}"));
        var catalog = JsonNode.Parse(await right.GetStringAsync("admin/catalog"));
        var detail = JsonNode.Parse(await right.GetStringAsync($"admin/character/{character.RawValue}"));
        var blaster = catalog!["templates"]!.AsArray().Single(t => (int)t!["id"] == ItemCatalog.Blaster);
        var empty = await right.PostAsync($"admin/character/{character.RawValue}/credits", Json("{}"));
        Assert.Multiple(() =>
        {
            Assert.That(listing!["accounts"]!.AsArray().Any(a => (string)a!["character"] == character.RawValue.ToString()), Is.True, "found by its name");
            Assert.That(blaster["defaults"]!.AsObject().Count, Is.EqualTo(5), "Mk I to V");
            Assert.That((int)blaster["defaults"]!["1"]!["stat0"]!, Is.GreaterThan(0), "a Mk I blaster deals damage");
            Assert.That((string)detail!["name"], Is.EqualTo(name));
            Assert.That(detail["inventory"]!["items"]!.AsArray(), Is.Not.Empty);
            Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "a body without its delta");
        });
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<WebApplication> Host(TatooineSim sim, string key)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapAdmin(sim.Dbe, key);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, string key)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address + "/") };
        if (key != null)
        {
            client.DefaultRequestHeaders.Add(AdminEndpoints.KeyHeader, key);
        }

        return client;
    }
}
