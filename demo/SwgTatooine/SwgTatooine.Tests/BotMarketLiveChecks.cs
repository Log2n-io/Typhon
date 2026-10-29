using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Client;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A bot's bazaar errand against the real server, end to end but for the socket (bazaar.md § 8, BZ-16): each bot is a session whose frames are applied to a
/// real <see cref="WorldStore"/>, read by the real <see cref="Perception"/>, decided by the real <see cref="BotBrain"/> at the driver's 4 Hz, its intents sent
/// as the swarm sends them, its market read from the server's own answers, planned by <see cref="MarketPlan"/> and sent by the real
/// <see cref="MarketSender"/>. The server moves the bodies, crowds them and judges every market command.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class BotMarketLiveChecks : ServedChecks
{
    /// <summary>Server ticks per bot decision: the driver's 4 Hz at the served rate.</summary>
    private const int TicksPerDecision = Worlds.ServedHz / 4;

    private sealed class LiveBot
    {
        public FakeLink Link;
        public EntityId Character;
        public BotBrain Brain;
        public WorldStore Store;
        public FrameApplier Applier;
        public readonly Perception Eyes = new();
        public readonly MarketSender Sender = new();
        public int Applied;
        public InventoryView Inventory = InventoryView.None;
        public List<HeldStack> Held = [];
        public MarketMine Mine = MarketMine.None;
        public uint Rng = 7u;
        public readonly List<(MarketStep Step, double Metres)> Sent = [];

        public long Key => unchecked((long)Character.RawValue);
    }

    /// <summary>This checkout's published asset set: what the live server serves, and what its collision is built from.</summary>
    private static string AssetsDirectory()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "demo", "SwgTatooine", "SwgTatooine.Assets", "build", "assets")))
        {
            dir = dir.Parent;
        }

        Assert.That(dir, Is.Not.Null, "the premise: the published assets of this checkout");
        return Path.Combine(dir.FullName, "demo", "SwgTatooine", "SwgTatooine.Assets", "build", "assets");
    }

    /// <summary>
    /// The structures' plans as a bot loads them from <c>/typhon/demo.json</c>: the server's own collision (<c>AssetColliders.ToJson</c>) for the walls, and
    /// each structure's descriptor named by the catalogue, through <see cref="BotAssets.Plan"/>.
    /// </summary>
    private static BotAssets Assets(string directory)
    {
        var shapes = TatooineSim.LoadAssetColliders(AssetOrigin.Open(directory));
        using var collision = JsonDocument.Parse(shapes.ToJson());
        var catalogueFile = Directory.GetFiles(directory, "catalogue-*.json")
            .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)["catalogue-".Length..], System.Globalization.CultureInfo.InvariantCulture)).Last();
        using var catalogue = JsonDocument.Parse(File.ReadAllText(catalogueFile));
        var entries = catalogue.RootElement.GetProperty("assets");
        var plans = new AssetPlan[StructureAssets.Table.Length];
        for (var i = 0; i < plans.Length; i++)
        {
            var id = StructureAssets.Table[i];
            var walls = i < collision.RootElement.GetArrayLength() ? BotAssets.ReadWalls(collision.RootElement[i]) : [];
            using var descriptor = entries.TryGetProperty(id, out var entry) && entry.TryGetProperty("descriptor", out var file)
                ? JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file.GetString())))
                : null;
            plans[i] = BotAssets.Plan(id, walls, descriptor?.RootElement, null);
        }

        return new BotAssets(plans);
    }

    private LiveBot Bot(TatooineSim sim, BotAssets assets, ulong account, (float X, float Z) at, int bindings)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        Worlds.Park(sim, link.Session, at.X, at.Z);
        Assert.That(CharacterStore.TryFind(sim.Dbe, TestAccounts.For(sim.Dbe, account), out var character, out _), Is.True);
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        var store = new WorldStore(link.Plan);
        return new LiveBot
        {
            Link = link,
            Character = character,
            Store = store,
            Applier = new FrameApplier(store),
            Brain = new BotBrain(BotMap.Cities, (uint)((account * 2246822519u) | 1u), 4d, assets, homeRegion: 1) { Trades = true, TakesMissions = false },
        };
    }

    /// <summary>One decision of one bot: its frames applied, what it perceives, the intent sent as the swarm sends it, and its market's next command.</summary>
    private static void Decide(TatooineSim sim, LiveBot bot, long tick, (float X, float Z) bazaar)
    {
        var sent = bot.Link.Sent;
        for (; bot.Applied < sent.Length; bot.Applied++)
        {
            if (sent[bot.Applied].Length > 0 && sent[bot.Applied][0] == MessageTypes.Tick)
            {
                bot.Applier.Apply(sent[bot.Applied]);
            }
        }

        bot.Held = [];
        bot.Inventory = InventoryView.Parse(ItemEndpoints.InventoryJson(sim.Dbe, bot.Character), bot.Inventory, true, out _, bot.Held);
        var world = bot.Eyes.Read(bot.Store, bot.Brain.Facilities) with
        {
            InventoryStacks = bot.Inventory.Stacks,
            Reserved = bot.Mine.Reserved,
            Trading = bot.Sender.Busy,
        };
        var intent = bot.Brain.Decide(in world, tick);
        switch (intent.Kind)
        {
            case IntentKind.Move:
                bot.Link.SendCommand("MoveDir", new RecordValues
                {
                    ["heading"] = FieldValue.Of((double)intent.Heading),
                    ["speedClass"] = FieldValue.Of((double)intent.SpeedClass),
                    ["changePhase"] = FieldValue.Of(0.0),
                    ["boost"] = FieldValue.Of(1.0),
                    ["throttle"] = FieldValue.Of(0.0),
                });
                break;
            case IntentKind.MoveTo:
                bot.Link.SendCommand("MoveTo", new RecordValues { ["x"] = FieldValue.Of((double)intent.X), ["z"] = FieldValue.Of((double)intent.Z) });
                break;
            case IntentKind.Mount:
                bot.Link.SendCommand("SetMount", new RecordValues { ["on"] = FieldValue.Of(intent.On ? 1.0 : 0.0) });
                break;
            case IntentKind.Trade:
                // The swarm's read, at once: /mine, /prices, the stacks; the plan handed to the sender for this visit.
                var board = sim.Bridge.MarketBoard;
                bot.Mine = MarketMine.Parse(MarketEndpoints.MineJson(sim.Dbe, bot.Character, board));
                var prices = MarketPrices.Parse(MarketEndpoints.PricesJson(board));
                bot.Sender.Offer(bot.Brain.VisitSerial, MarketPlan.Plan(bot.Held, bot.Mine, prices, ref bot.Rng, listMaterial: false, buy: null));
                break;
        }

        if (bot.Sender.Next(bot.Brain.AtBazaar, bot.Brain.VisitSerial, tick, 2) is { } c)
        {
            var (name, values) = BotSwarm.Encode(c);
            bot.Link.SendCommand(name, values);
            bot.Sent.Add((c.Step, Routing.Distance(world.X, world.Z, bazaar.X, bazaar.Z)));
        }
    }

    /// <summary>
    /// Bots back from a day's hunting — two cores and their inventory nearly full — standing in the street 20-30 m from their city's bazaar, all of them
    /// choosing it: they walk there, crowd its terminal, trade, and go. No market command of theirs is refused MarketNotAtBazaar (151) or MarketWorthless
    /// (163), each one's cores are listed (the board grows by them), and the vendor takes each down to its floor.
    /// </summary>
    [TestCase(1)]
    [TestCase(8)]
    [CancelAfter(300_000)]
    public void BotsAtTheirCitysBazaar_TradeWithoutARefusal_AndTheirListingsAppear(int count)
    {
        // The published assets, as the live server runs: its colliders are the real terminals', and the bots walk round the same walls.
        var directory = AssetsDirectory();
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        config.AssetDirectory = directory;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var assets = Assets(directory);
        var facilities = FacilityMap.Parse(FacilityEndpoints.Build(sim.Dbe));
        var bazaar = sim.Index.Bazaars.First(b => b.Region == 1);
        var bots = new List<LiveBot>();
        for (var i = 0; i < count; i++)
        {
            // North of it, on its stand point's side, in the open: the mission terminal beside it stands 11 m east.
            var at = (bazaar.X - 6f + (i * 1.7f), bazaar.Z + 18f + (i % 3 * 2f));
            bots.Add(Bot(sim, assets, 0x9400UL + (ulong)i, at, i + 1));
            bots[^1].Brain.Facilities.Seed(facilities);
        }

        foreach (var b in bots)
        {
            sim.Bridge.StageTransfer(ItemLedger.World, b.Key, ItemCatalog.CoreArmor, 2, 1, ItemMoveKind.Kill, 1);
            sim.Bridge.StageTransfer(ItemLedger.World, b.Key, ItemCatalog.AddonWeapon, 2, 1, ItemMoveKind.Kill, 1);
            var n = 2;
            for (var template = ItemCatalog.Metal; template <= ItemCatalog.Venom && n < 26; template++)
            {
                for (byte mk = 1; mk <= 4 && n < 26; mk++, n++)
                {
                    sim.Bridge.StageTransfer(ItemLedger.World, b.Key, template, mk, 5, ItemMoveKind.Kill, 1);
                }
            }
        }

        SessionHarness.Until(() => bots.All(b => Items.Stacks(sim, b.Character).Count == 26), "the inventories to be filled");
        var listingsBefore = Market.Listings(sim).Count;

        // A minute of the driver's 4 Hz: long enough to walk there, trade, and stand while the plan goes out.
        for (var tick = 1L; tick <= 240; tick++)
        {
            foreach (var b in bots)
            {
                Decide(sim, b, tick, (bazaar.X, bazaar.Z));
            }

            Harness.Ticks(TicksPerDecision);
        }

        Harness.Ticks(Worlds.ServedHz);
        var (ok, why) = Market.Audits(sim);
        var traders = bots.Where(b => b.Brain.TradesAsked > 0).ToList();
        Assert.Multiple(() =>
        {
            // A crowd of eight round one terminal leaves a bot or two stuck short of it, and that errand is not tried again within the minute.
            Assert.That(traders, Has.Count.GreaterThanOrEqualTo(Math.Max(1, count - 2)), "the premise: the bots traded");
            foreach (var b in bots)
            {
                var refusals = Ledgers.Refusals(b.Link);
                var farthest = b.Sent.Count == 0 ? 0d : b.Sent.Max(s => s.Metres);
                Assert.That(refusals.Count(r => r == TatooineReplication.MarketNotAtBazaar), Is.Zero,
                    $"bot {b.Key}: {b.Sent.Count} market commands sent, the farthest {farthest:F1} m from the terminal; refusals {string.Join(",", refusals)}");
                Assert.That(refusals.Count(r => r == TatooineReplication.MarketWorthless), Is.Zero, $"bot {b.Key}: a worthless vendor sale");
            }

            foreach (var b in traders)
            {
                Assert.That(Market.Listings(sim).Values.Count(l => l.Seller == b.Key), Is.EqualTo(2), $"bot {b.Key} traded and its two cores are not on the board");
                Assert.That(Items.Stacks(sim, b.Character).Count, Is.LessThanOrEqualTo(MarketPlan.KeepStacks), $"bot {b.Key} traded and was not vendored down");
            }

            Assert.That(Market.Listings(sim).Count - listingsBefore, Is.EqualTo(2 * traders.Count), "the board did not grow by every trader's cores");
            Assert.That(ok, Is.True, why);
        });
    }
}
