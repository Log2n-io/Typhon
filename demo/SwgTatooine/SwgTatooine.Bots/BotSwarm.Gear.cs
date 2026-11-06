using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Bots.Brain;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>
/// A thinking bot's look at its gear (<see cref="GearPlan"/>, Loïc 2026-11-04: spend to get stronger, replace what is broken, carry a spare): every inventory
/// read says whether it is down to its fist; every <see cref="GearPlan.AssessSeconds"/>, and after a mission handed in, it reads its wallet and weighs
/// whether a bazaar trip is worth it. The brain is told (<see cref="Perceived.Unarmed"/>, <see cref="Perceived.GearDue"/>,
/// <see cref="Perceived.GearUrgent"/>); the trade plan does the buying (<c>PlanTradeAsync</c>).
/// </summary>
public sealed partial class BotSwarm
{
    /// <summary>
    /// The fleet's gear: assessments made and found wanting, bots down to their fist now, defenders that left a raid to arm, spares asked for, worn-down items
    /// thrown away.
    /// </summary>
    public (long Assessed, long Due, long Unarmed, long Runs, long Spares, long Discarded) Gearing() => Gearing(out _);

    /// <summary>The fleet's stims: drinks asked for (the server refuses some), and trades planned with a stim to buy (the board's or the vendor's).</summary>
    public (long Asked, long BuysPlanned) Stims()
    {
        var t = (Asked: 0L, BuysPlanned: 0L);
        foreach (var bot in Bots)
        {
            if (bot != null)
            {
                t = (t.Asked + Volatile.Read(ref bot.StimsAsked), t.BuysPlanned + Volatile.Read(ref bot.StimBuysPlanned));
            }
        }

        return t;
    }

    /// <summary>
    /// The fleet's room at the bazaar (<see cref="MarketPlan.MakeRoom"/>): commands sent to make room for a visit's goods, goods left for want of it — and
    /// gear bought in another city, to fetch — and the cores above Mk I crafters bought.
    /// </summary>
    public (long Made, long Left, long Remote, long Cores) Room()
    {
        var t = (Made: 0L, Left: 0L, Remote: 0L, Cores: 0L);
        foreach (var bot in Bots)
        {
            if (bot != null)
            {
                t = (t.Made + Volatile.Read(ref bot.GearRoomMade), t.Left + Volatile.Read(ref bot.GearLeftForRoom), t.Remote + Volatile.Read(ref bot.GearRemote),
                    t.Cores + Volatile.Read(ref bot.CoresBought));
            }
        }

        return t;
    }

    /// <summary><see cref="Gearing()"/>, and the repair kits bought off the board.</summary>
    public (long Assessed, long Due, long Unarmed, long Runs, long Spares, long Discarded) Gearing(out long kits)
    {
        kits = 0;
        var t = (Assessed: 0L, Due: 0L, Unarmed: 0L, Runs: 0L, Spares: 0L, Discarded: 0L);
        foreach (var bot in Bots)
        {
            if (bot != null)
            {
                t = (t.Assessed + Volatile.Read(ref bot.GearAssessed), t.Due + Volatile.Read(ref bot.GearFoundDue), t.Unarmed + (bot.IsUnarmed ? 1 : 0),
                    t.Runs + (bot.GearRuns), t.Spares + Volatile.Read(ref bot.SparesAsked), t.Discarded + Volatile.Read(ref bot.GearDiscarded));
                kits += Volatile.Read(ref bot.KitsAsked);
            }
        }

        return t;
    }

    private sealed partial class Bot
    {
        /// <summary>Assessments made, and those that found a trip due.</summary>
        public long GearAssessed;

        public long GearFoundDue;

        /// <summary>Spare weapons asked for at the bazaar (the board's or the vendor's), repair kits bought off the board, worn-down items thrown away.</summary>
        public long SparesAsked;

        public long KitsAsked;

        public long GearDiscarded;

        /// <summary>Drinks asked for, and trades planned with a stim to buy: asks, not outcomes — the server's counters say what was drunk.</summary>
        public long StimsAsked;

        public long StimBuysPlanned;

        // When it may drink again (Environment.TickCount64, ms): past the server's cooldown, so it never asks within it.
        private long _drinkAt;

        private const string ConsumeCommand = "ConsumeItem";

        /// <summary>
        /// A stim when it needs one (<see cref="GearPlan.StimToDrink"/>): hurt below half, attacked, and past its last drink's cooldown — from what its last
        /// inventory read said it holds. Sent off the tick; a refusal costs nothing but the dose's wait.
        /// </summary>
        private void PollStim(in Perceived world)
        {
            if (!world.HasBody || Environment.TickCount64 < Volatile.Read(ref _drinkAt))
            {
                return;
            }

            var stim = GearPlan.StimToDrink(_gear, world.Health, world.Attacker != 0);
            if (stim == 0 || Client.Plan.CommandByName(ConsumeCommand) == null)
            {
                return;
            }

            Volatile.Write(ref _drinkAt, Environment.TickCount64 + (long)(GearPlan.DrinkEverySeconds * 1000d));
            Interlocked.Increment(ref StimsAsked);
            _ = SendQuietly(ConsumeCommand, new RecordValues { ["item"] = FieldValue.OfUInt64(stim) }, counter: false);
        }

        // What its last inventory read said: read at all, down to its fist, and with no weapon to put on or repair.
        private volatile bool _gearKnown;
        private volatile bool _unarmed;
        private volatile bool _urgent;

        // The assessment's answer, held until the next trade; when the next one is due (Environment.TickCount64, ms); one at a time.
        private volatile bool _gearDue;
        private long _assessAt;
        private int _assessBusy;
        private uint _assessRng;
        private bool _assessSeeded;

        // Its wallet as the last assessment read it; -1 before the first.
        private long _credits = -1;

        /// <summary>Whether it fights with its fist now, as its last inventory read said.</summary>
        public bool IsUnarmed => _gearKnown && _unarmed;

        /// <summary>Defenders of this bot's brain that left a raid to arm.</summary>
        public long GearRuns => _brain?.GearRuns ?? 0;

        /// <summary>The gear half of an inventory read: down to its fist or not, and with nothing to put on — at once, whatever the assessment's clock.
        /// </summary>
        private void SawGear(List<GearItem> gear, List<HeldStack> held)
        {
            var band = MarketPlan.Band(_brain.CombatLevel);
            _unarmed = GearPlan.Unarmed(gear, CraftPlan.Blaster);
            _urgent = GearPlan.Urgent(gear, CraftPlan.Blaster, held, band);
            _forSale = Crafts ? CraftPlan.ForSale(gear, held, GearPlan.Spare(gear, CraftPlan.Blaster, held, band),
                MarketPlan.NextToWear(gear, band, CraftPlan.Blaster, held)) : 0;
            _gearKnown = true;
        }

        // A crafter's goods to sell, as its last inventory read counted them (CraftPlan.ForSale).
        private volatile int _forSale;

        /// <summary>A mission handed in: the next assessment now — the reward is what it can spend — even if one is under way (its end keeps this).</summary>
        private void AssessSoon()
        {
            Volatile.Write(ref _assessSoon, true);
            Volatile.Write(ref _assessAt, 0);
        }

        private bool _assessSoon;

        /// <summary>The assessment, when its clock says so: one at a time, off the tick.</summary>
        private void PollGear()
        {
            if (!_trades || _rush || !_gearKnown)
            {
                return;
            }

            // The first one spread over two minutes: a fleet started together would read 2 000 wallets in the same second. Down to its fist, it does not
            // wait for it (SawGear).
            if (!_assessSeeded)
            {
                _assessSeeded = true;
                _assessRng = _marketRng | 1u;
                Volatile.Write(ref _assessAt, Environment.TickCount64 + (MarketPlan.Next(ref _assessRng) % 120_000u));
            }

            if (Environment.TickCount64 < Volatile.Read(ref _assessAt)
                || Interlocked.CompareExchange(ref _assessBusy, 1, 0) != 0)
            {
                return;
            }

            _ = AssessAsync();
        }

        private async Task AssessAsync()
        {
            // Spread over the fleet: the bot's own draw, not 2 000 wallets read in the same second — its own stream, as the trade's is the trade's.
            var next = (long)(GearPlan.AssessSeconds * 1000d) + (MarketPlan.Next(ref _assessRng) % 60_000u);
            Volatile.Write(ref _assessSoon, false);
            try
            {
                var credits = MarketJson.Credits(await GetAsync(_walletUri).ConfigureAwait(false));
                _standard ??= MarketJson.Standard(await GetAsync(_standardUri).ConfigureAwait(false));
                Volatile.Write(ref _credits, credits);
                var prices = await PricesAsync().ConfigureAwait(false);
                var due = GearPlan.Due(_gear, _held, _standard, MarketPlan.Band(_brain.CombatLevel), credits, CraftPlan.Blaster, prices);
                _gearDue = due;

                // A crafter's next crafts, the scarcest on the board first (Loïc, 2026-11-04: identify what needs to be crafted and sold) — from the first
                // or the second of them by its own draw, so a line's crafters do not all make the same thing (review, 2026-11-04).
                if (Crafts && _lineTargets.Length > 0)
                {
                    _craftTargets = CraftPlan.ByDemand(_lineTargets, prices, _book);
                    _lootedOnly = CraftPlan.MkIGlutted(_lineTargets, prices, _book);
                    _craftTurn = (int)(MarketPlan.Next(ref _assessRng) % (uint)Math.Min(2, _lineTargets.Length));
                }
                Interlocked.Increment(ref GearAssessed);
                Interlocked.Add(ref GearFoundDue, due ? 1 : 0);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                          or FormatException or OverflowException)
            {
                next = 60_000;   // asked again in a minute
            }
            finally
            {
                Volatile.Write(ref _assessAt, Volatile.Read(ref _assessSoon) ? 0 : Environment.TickCount64 + next);
                Volatile.Write(ref _assessBusy, 0);
            }
        }

        /// <summary>A trade was planned: what the assessment asked for has been asked; the next one in its own time.</summary>
        private void Traded() => _gearDue = false;

        /// <summary>
        /// What the brain is told of its gear: its fist, a trip due, and a weapon wanted now — the latter only while it can pay for the vendor's (a bot with
        /// nothing goes on hunting with its fist, or it would walk to the bazaar and back for ever).
        /// </summary>
        private Perceived WithGear(Perceived world)
        {
            if (!_trades || !_gearKnown)
            {
                return world;
            }

            var urgent = _urgent;
            if (urgent)
            {
                var credits = Volatile.Read(ref _credits);
                var price = GearPlan.PriceOf(_standard, GearPlan.WeaponItem(_standard, CraftPlan.Blaster));
                urgent = credits < 0 || credits >= price + MarketPlan.StandardReserve;
            }

            // A crafter with goods to sell goes to sell them: it lives off the board, and its grid filling up was the only thing that sent it there.
            // Not with every listing slot taken: there is nothing it could put up.
            var selling = Crafts && _forSale >= CraftPlan.SellAt && _mine.Listed < MarketPlan.CrafterMaxListed;
            return world with { Unarmed = _unarmed, GearDue = _gearDue && !_rush, GearUrgent = urgent && !_rush, SellDue = selling && !_rush };
        }
    }
}
