using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Bots.Brain;
using Typhon.Client;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>
/// The installation owners (crafting-build.md § 10, O-7): one bot in <see cref="BotSwarmOptions.InstallPercent"/> of those that trade, after the crafters,
/// keeps an extractor — buys the kit, places it out of town, pays its upkeep, collects it. Read from <c>GET /swg/installations</c> every
/// <see cref="InstallReadSeconds"/>; told to the brain as an errand (<see cref="Perceived.TendDue"/>); sent when the bot stands there
/// (<see cref="IntentKind.Tend"/>).
/// </summary>
public sealed partial class BotSwarm
{
    private const string PlaceCommand = "PlaceInstallation";
    private const string CollectCommand = "CollectInstallation";
    private const string PayCommand = "PayUpkeep";

    /// <summary>How often an owner reads its installations, seconds.</summary>
    private const int InstallReadSeconds = 60;

    /// <summary>The fleet's installation work, summed: kits bought, errands, places, collects, payments.</summary>
    public (int Owners, long Kits, long Tends, long Placed, long Collects, long Payments) Installing()
    {
        var t = (Owners: 0, Kits: 0L, Tends: 0L, Placed: 0L, Collects: 0L, Payments: 0L);
        foreach (var bot in Bots)
        {
            if (bot == null)
            {
                continue;
            }

            t.Owners += bot.Installs ? 1 : 0;
            t.Kits += Volatile.Read(ref bot.KitsBought);
            t.Tends += Volatile.Read(ref bot.TendsSent);
            t.Placed += Volatile.Read(ref bot.PlacesSent);
            t.Collects += Volatile.Read(ref bot.CollectsSent);
            t.Payments += Volatile.Read(ref bot.PaymentsSent);
        }

        return t;
    }

    private sealed partial class Bot
    {
        public long KitsBought;
        public long TendsSent;
        public long PlacesSent;
        public long CollectsSent;
        public long PaymentsSent;

        /// <summary>Whether this bot keeps installations.</summary>
        public bool Installs { get; private set; }

        private Uri _installationsUri;
        private volatile List<BotInstallation> _installs = [];
        private volatile bool _installsKnown;
        private long _installsReadAt = long.MinValue / 2;
        private int _installReading;
        private int _tendBusy;
        private int _placeAttempt;
        private ushort _family;

        /// <summary>Wires an owner: the next <see cref="BotSwarmOptions.InstallPercent"/> by index after the crafters, of those that trade.</summary>
        private void SetUpInstall(int index, BotSwarmOptions options)
        {
            var slot = index % 100;
            if (!_trades || _rush || _missionsUri == null || slot < options.CraftPercent || slot >= options.CraftPercent + options.InstallPercent)
            {
                return;
            }

            Installs = true;
            _family = InstallPlan.FamilyFor(index);
            _installationsUri = new Uri(_missionsUri, "/swg/installations");
        }

        /// <summary>Reads its installations every <see cref="InstallReadSeconds"/>, off the frame.</summary>
        private void PollInstallations(long tick)
        {
            if (!Installs || string.IsNullOrEmpty(_token) || tick - _installsReadAt < InstallReadSeconds * Math.Max(1L, _ticksPerSecond)
                || Interlocked.CompareExchange(ref _installReading, 1, 0) != 0)
            {
                return;
            }

            _installsReadAt = tick;
            _ = ReadInstallationsAsync();
        }

        private async Task ReadInstallationsAsync()
        {
            try
            {
                _installs = InstallPlan.Parse(await GetAsync(_installationsUri).ConfigureAwait(false));
                _installsKnown = true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
                                          or InvalidOperationException or FormatException)
            {
                // Read again at the next poll.
            }
            finally
            {
                Volatile.Write(ref _installReading, 0);
            }
        }

        /// <summary>The kit in its grid, from the last inventory read; 0 for none.</summary>
        private ulong KitHeld()
        {
            foreach (var g in _gear)
            {
                if (g.Template == InstallPlan.Kit && !g.Worn)
                {
                    return g.Id;
                }
            }

            return 0;
        }

        /// <summary>What the brain is told: an installation due, or a kit to place and where — a lot just out of town.</summary>
        private Perceived WithInstall(Perceived world)
        {
            if (!Installs || !world.HasBody)
            {
                return world;
            }

            var tending = Volatile.Read(ref _tendBusy) != 0;
            if (InstallPlan.NextDue(_installs) is { } due)
            {
                return world with { TendDue = true, TendX = due.X, TendZ = due.Z, Tending = tending };
            }

            if (KitHeld() != 0)
            {
                var (x, z) = _brain.WildLot(_placeAttempt);
                return world with { TendDue = true, TendX = x, TendZ = z, Tending = tending };
            }

            // Nothing placed and nothing to place, once its installations are read: the bazaar, for the vendor's kit — no other errand takes an owner
            // with room in its bag there. The plan checks its wallet (InstallPlan.BuysKit).
            return world with { Tending = tending, KitWanted = _installsKnown && _installs.Count == 0 };
        }

        /// <summary>The bot stands at an installation, or at the lot it chose: what is due there, sent at the commands' rate.</summary>
        private void BeginTend(float x, float z)
        {
            if (!Installs || Interlocked.CompareExchange(ref _tendBusy, 1, 0) != 0)
            {
                return;
            }

            Interlocked.Increment(ref TendsSent);
            _ = TendAsync(x, z);
        }

        private async Task TendAsync(float x, float z)
        {
            try
            {
                var wallet = MarketJson.Credits(await GetAsync(_walletUri).ConfigureAwait(false));
                var tended = false;
                foreach (var i in _installs)
                {
                    if (Routing.Distance(x, z, i.X, i.Z) > InstallPlan.ReachM)
                    {
                        continue;
                    }

                    tended = true;
                    var (collect, pay) = InstallPlan.Tend(in i, wallet);
                    if (collect && Client.Plan.CommandByName(CollectCommand) != null)
                    {
                        await Client.SendCommandAsync(CollectCommand, new RecordValues { ["installation"] = FieldValue.OfUInt64(i.Id) }).ConfigureAwait(false);
                        Interlocked.Increment(ref CollectsSent);
                        await Task.Delay(600).ConfigureAwait(false);
                    }

                    if (pay > 0 && Client.Plan.CommandByName(PayCommand) != null)
                    {
                        await Client.SendCommandAsync(
                            PayCommand,
                            new RecordValues { ["installation"] = FieldValue.OfUInt64(i.Id), ["credits"] = FieldValue.Of((double)pay) }).ConfigureAwait(false);
                        Interlocked.Increment(ref PaymentsSent);
                        wallet -= pay;
                        await Task.Delay(600).ConfigureAwait(false);
                    }
                }

                // At the lot it chose, with a kit: placed — and paid at once, on the next read, since an unpaid one makes nothing. A refused lot (a city, a
                // taken one) moves the next attempt round its city.
                var kit = KitHeld();
                if (!tended && kit != 0 && Client.Plan.CommandByName(PlaceCommand) != null)
                {
                    await Client.SendCommandAsync(
                        PlaceCommand,
                        new RecordValues { ["item"] = FieldValue.OfUInt64(kit), ["family"] = FieldValue.Of((double)_family) }).ConfigureAwait(false);
                    Interlocked.Increment(ref PlacesSent);
                    _placeAttempt++;
                }

                _installsReadAt = long.MinValue / 2;   // read again soon: what was sent shows there
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
                                          or InvalidOperationException or FormatException or OverflowException)
            {
                // Nothing more sent: the next errand tries again.
            }
            finally
            {
                Volatile.Write(ref _tendBusy, 0);
            }
        }
    }
}
