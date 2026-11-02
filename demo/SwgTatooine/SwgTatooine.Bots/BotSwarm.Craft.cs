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

/// <summary>A crafting command as the crafting panel sends it (crafting-build.md C2): its catalog name and its fields.</summary>
public readonly record struct CraftCommand(string Name, RecordValues Values);

/// <summary>
/// The crafters (crafting-build.md § 6, profiles): a share of the thinking bots that, holding a craft's inputs, walk to their city's nearest crafting
/// station and craft — start, a point at a time, the add-on, the finish — exactly the commands the crafting panel sends. What to craft is
/// <see cref="CraftPlan"/>'s; this decides when, and sends.
/// </summary>
public sealed partial class BotSwarm
{
    private const string CraftStartCommand = "CraftStart";
    private const string CraftExperimentCommand = "CraftExperiment";
    private const string CraftEnhanceCommand = "CraftEnhance";
    private const string CraftFinishCommand = "CraftFinish";

    /// <summary>The workshop's crafting refusals, <c>CraftNotAtStation</c> … <c>CraftNoAddon</c>: the first application reason + 41 … + 49.</summary>
    public const byte CraftFirstReason = AckReasons.FirstApplicationReason + 41;

    public const int CraftReasons = 9;

    /// <summary>The refusals' names, by offset from <see cref="CraftFirstReason"/>.</summary>
    public static readonly string[] CraftReasonNames =
        ["notAtStation", "busy", "none", "badInputs", "noPoints", "noRoom", "notEnough", "failed", "noAddon"];

    /// <summary>A craft's commands, in order: the start, a point per line listed, the add-on when it has one, the finish.</summary>
    public static List<CraftCommand> Encode(CraftJob job)
    {
        var start = new RecordValues { ["schematic"] = FieldValue.Of((double)job.Schematic) };
        for (var i = 0; i < 6; i++)
        {
            start[$"mk{i}"] = FieldValue.Of(i < job.Mks.Length ? job.Mks[i] : 0d);
            start[$"part{i}"] = FieldValue.OfUInt64(i < job.Parts.Length ? job.Parts[i] : 0UL);
        }

        var commands = new List<CraftCommand> { new(CraftStartCommand, start) };
        commands.AddRange(Rest(job.Schematic, job.Points, job.AddonMk));
        return commands;
    }

    /// <summary>What finishes a craft under way: its points left, its add-on, its finish.</summary>
    public static List<CraftCommand> Rest(ushort schematic, IReadOnlyList<byte> points, byte addonMk)
    {
        var commands = new List<CraftCommand>();
        foreach (var line in points)
        {
            commands.Add(new CraftCommand(CraftExperimentCommand, new RecordValues { ["line"] = FieldValue.Of((double)line) }));
        }

        if (addonMk != 0)
        {
            commands.Add(new CraftCommand(CraftEnhanceCommand, new RecordValues { ["mk"] = FieldValue.Of((double)addonMk) }));
        }

        commands.Add(new CraftCommand(CraftFinishCommand, new RecordValues { ["schematic"] = FieldValue.Of((double)schematic) }));
        return commands;
    }

    /// <summary>The fleet's crafting, summed: crafts asked, commands sent by kind, refusals by reason.</summary>
    public CraftTotals Crafting()
    {
        var t = new CraftTotals { Refused = new long[CraftReasons] };
        foreach (var bot in Bots)
        {
            if (bot == null)
            {
                continue;
            }

            t.Crafters += bot.Crafts ? 1 : 0;
            t.Started += Volatile.Read(ref bot.CraftsStarted);
            t.Resumed += Volatile.Read(ref bot.CraftsResumed);
            t.Points += Volatile.Read(ref bot.CraftPointsSent);
            t.Enhanced += Volatile.Read(ref bot.CraftsEnhanced);
            t.Finished += Volatile.Read(ref bot.CraftsFinishSent);
            t.InputsBought += Volatile.Read(ref bot.CraftInputsBought);
            t.GearBought += Volatile.Read(ref bot.GearBought);
            t.ItemsListed += Volatile.Read(ref bot.ItemsListed);
            for (var i = 0; i < CraftReasons; i++)
            {
                t.Refused[i] += Volatile.Read(ref bot.CraftRefused[i]);
            }
        }

        return t;
    }

    private sealed partial class Bot
    {
        /// <summary>Crafting (crafting-build.md C2): starts sent, crafts found under way and finished, points, add-ons, finishes, refusals by reason.</summary>
        public long CraftsStarted;

        public long CraftsResumed;
        public long CraftPointsSent;
        public long CraftsEnhanced;
        public long CraftsFinishSent;

        /// <summary>Bazaar buys of an input its next craft lacks (§ 6, Inputs).</summary>
        public long CraftInputsBought;
        public readonly long[] CraftRefused = new long[CraftReasons];

        private CraftBook _book;
        private ushort[] _craftTargets = [];
        private HashSet<ushort> _craftKeeps = [];

        // Which of its targets it tries first: moved on by each craft started, so a crafter with the inputs for several makes them in turn.
        private int _craftTurn;

        /// <summary>The craft its last inventory read makes possible, or null.</summary>
        private volatile CraftJob _craftJob;

        private Uri _craftUri;

        // The sequence of the last start sent: a refusal naming it drops the rest of its craft.
        private int _craftStartSeq;
        private int _craftBusy;
        private readonly PlanSender<CraftCommand> _craftSender = new();

        /// <summary>Whether this bot crafts.</summary>
        public bool Crafts { get; private set; }

        /// <summary>
        /// Wires the crafter: one bot in <see cref="BotSwarmOptions.CraftPercent"/> by index of those that trade — it buys its inputs and sells what it makes
        /// at the bazaar — the three lines in turn. Not in the rush, which does nothing but trade.
        /// </summary>
        private void SetUpCraft(int index, BotSwarmOptions options, CraftBook book)
        {
            if (!_trades || _rush || book == null || book.Count == 0 || index % 100 >= options.CraftPercent)
            {
                return;
            }

            Crafts = true;
            _book = book;
            _craftTargets = CraftPlan.Targets(CraftPlan.Lines[index % CraftPlan.Lines.Length]);
            _craftKeeps = CraftPlan.Keeps(book, _craftTargets);
            _craftUri = new Uri(_missionsUri, "/swg/craft");
            _brain.Crafts = true;
        }

        /// <summary>The crafting half of an inventory read: the craft it can make now, from the stacks and the parts held in the grid.</summary>
        private void SawCraftInputs(List<HeldStack> held, List<GearItem> gear)
        {
            if (!Crafts)
            {
                return;
            }

            _craftJob = CraftPlan.Next(_book, _craftTargets, _craftTurn, held, CraftPlan.PartsOf(gear));
        }

        /// <summary>What the brain is told of crafting: whether a craft can be made, and whether one is still going out.</summary>
        private Perceived WithCraft(Perceived world) =>
            Crafts
                ? world with { CraftReady = _craftJob != null, Crafting = Volatile.Read(ref _craftBusy) != 0 || _craftSender.Busy }
                : world;

        /// <summary>
        /// The bot stands at a station: the craft under way, if the server holds one — a finish refused for room, a run stopped mid-craft — is finished
        /// first; else the planned one starts.
        /// </summary>
        private void BeginCraft()
        {
            if (!Crafts || Interlocked.CompareExchange(ref _craftBusy, 1, 0) != 0)
            {
                return;
            }

            var job = _craftJob;
            _craftJob = null;
            _ = CraftAsync(job, _brain.VisitSerial);
        }

        private async Task CraftAsync(CraftJob job, long visit)
        {
            try
            {
                using var doc = JsonDocument.Parse(await GetAsync(_craftUri).ConfigureAwait(false));
                if (doc.RootElement.GetProperty("craft") is { ValueKind: JsonValueKind.Object } c)
                {
                    var schematic = (ushort)c.GetProperty("schematic").GetInt32();
                    var left = c.GetProperty("pointsLeft").GetInt32();
                    var rolled = _book.Of(schematic)?.Rolled ?? [];
                    var points = new byte[rolled.Length == 0 ? 0 : left];
                    for (var p = 0; p < points.Length; p++)
                    {
                        points[p] = (byte)rolled[p % rolled.Length];
                    }

                    Interlocked.Increment(ref CraftsResumed);
                    _craftSender.Offer(visit, Rest(schematic, points, 0));
                    return;
                }

                if (job != null)
                {
                    _craftTurn++;
                    _craftSender.Offer(visit, Encode(job));
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                          or FormatException or OverflowException)
            {
                // Nothing sent: the next visit plans again.
            }
            finally
            {
                Volatile.Write(ref _craftBusy, 0);
            }
        }

        /// <summary>Sends the craft's next command, at the commands' rate, while the bot stands at the station of the visit it was planned for.</summary>
        private async Task SendCraftAsync(long tick, CancellationToken ct)
        {
            if (!Crafts || _craftSender.Next(_brain.AtStation, _brain.VisitSerial, tick, Math.Max(1L, _ticksPerSecond / 2)) is not { } c)
            {
                return;
            }

            if (Client.Plan.CommandByName(c.Name) == null)
            {
                return;   // a server without crafting
            }

            var seq = await Client.SendCommandAsync(c.Name, c.Values, ct).ConfigureAwait(false);
            switch (c.Name)
            {
                case CraftStartCommand:
                    Volatile.Write(ref _craftStartSeq, seq);
                    Interlocked.Increment(ref CraftsStarted);
                    break;
                case CraftExperimentCommand:
                    Interlocked.Increment(ref CraftPointsSent);
                    break;
                case CraftEnhanceCommand:
                    Interlocked.Increment(ref CraftsEnhanced);
                    break;
                default:
                    Interlocked.Increment(ref CraftsFinishSent);
                    break;
            }

            IntentsSent++;
        }

        /// <summary>Counts the workshop's crafting refusals of this session's commands, from each applied frame's <c>ACKS</c>.</summary>
        private void CountCraftRefusals(WorldStore store)
        {
            var acks = store.Acks;
            var start = Volatile.Read(ref _craftStartSeq);
            for (var i = 0; i < acks.Count; i++)
            {
                var offset = acks[i].Reason - CraftFirstReason;
                if (offset is >= 0 and < CraftReasons)
                {
                    Interlocked.Increment(ref CraftRefused[offset]);
                }

                // Its start refused: the rest of that craft would be refused too, one command at a time — dropped instead.
                if (start != 0 && acks[i].Seq == start && acks[i].Reason != 0)
                {
                    _craftSender.Abort();
                }
            }
        }

        /// <summary>
        /// A crafter's stacks as the bazaar plan sees them (§ 6, Surplus): its recipes' inputs are kept — neither listed nor sold to the vendor — so what
        /// it loots and buys is crafted with (<see cref="CraftPlan.Tradeable"/>).
        /// </summary>
        private List<HeldStack> Tradeable(List<HeldStack> held) =>
            Crafts ? CraftPlan.Tradeable(held, _craftKeeps, CraftPlan.Uses(_book, _craftJob)) : held;

        /// <summary>The input its next target lacks first, to buy at this visit's bazaar; null for a bot that does not craft or lacks nothing.</summary>
        private BookSlot? CraftWant() =>
            Crafts && _craftTargets.Length > 0
                ? CraftPlan.Wants(_book, _craftTargets[_craftTurn % _craftTargets.Length], _held, CraftPlan.PartsOf(_gear))
                : null;
    }
}

/// <summary>The fleet's crafting, summed (crafting-build.md C2).</summary>
public struct CraftTotals
{
    public int Crafters;
    public long Started;
    public long Resumed;
    public long Points;
    public long Enhanced;
    public long Finished;
    public long InputsBought;

    /// <summary>Every trading bot's, not only the crafters': gear of its band bought off the board, items with stats listed.</summary>
    public long GearBought;

    public long ItemsListed;
    public long[] Refused;
}
