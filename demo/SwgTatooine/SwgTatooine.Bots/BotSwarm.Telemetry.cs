using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Bots.Telemetry;

namespace SwgTatooine.Bots;

/// <summary>
/// The bot telemetry (bot-telemetry.md): every few seconds, the fleet's statistics and the traced bots' recent decisions, posted to the dashboard's
/// <c>/bots/report</c>. Off unless <see cref="BotSwarmOptions.Dashboard"/> is set — then no bot is traced and no report is built.
/// </summary>
public sealed partial class BotSwarm
{
    /// <summary>How often a report is posted.</summary>
    public static readonly TimeSpan ReportPeriod = TimeSpan.FromSeconds(3);

    private Task _reporter;

    /// <summary>Reports posted and accepted, and refused or failed, with the first failure's reason.</summary>
    private long _reportsSent;

    private long _reportsFailed;
    private string _firstReportFailure;

    /// <summary>Reports the dashboard accepted, reports that failed, and why the first did.</summary>
    public (long Sent, long Failed, string FirstFailure) Reports =>
        (Interlocked.Read(ref _reportsSent), Interlocked.Read(ref _reportsFailed), Volatile.Read(ref _firstReportFailure));

    /// <summary>Whether bot <paramref name="index"/> is traced: only with a dashboard to report to, a thinking run, and when the sample picks it.</summary>
    public static bool Traces(int index, BotSwarmOptions options) =>
        options.Dashboard != null && options.Brain && BotTraceSampler.IsTraced(index, options.Count, options.TraceBots);

    /// <summary>Starts the reporter when the run is thinking and has somewhere to report to.</summary>
    private void StartTelemetry()
    {
        if (_options.Dashboard != null && _options.Brain)
        {
            _reporter = Task.Run(() => ReportLoopAsync(_options.Dashboard, _stopping.Token), CancellationToken.None);
        }
    }

    private async Task ReportLoopAsync(Uri dashboard, CancellationToken ct)
    {
        var target = new Uri(dashboard, "bots/report");
        var run = Guid.NewGuid().ToString("N")[..8];
        var fleet = new FleetStats(_bots.Length, DateTime.UtcNow);
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        var traced = new List<BotTracer>();
        var cursors = new (long Events, long Trail)[BotTraceSampler.Max * 4];
        var stats = new long[Enum.GetValues<FleetStat>().Length];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var timer = new PeriodicTimer(ReportPeriod);
        Console.WriteLine($"  telemetry: reporting to {target} every {ReportPeriod.TotalSeconds:F0} s, "
            + $"{BotTraceSampler.Count(_options.Count, _options.TraceBots)} bot(s) traced");
        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            SampleFleet(fleet, traced, stats);
            if (cursors.Length < traced.Count)
            {
                cursors = new (long, long)[traced.Count];
            }

            buffer.ResetWrittenCount();
            using (var w = new Utf8JsonWriter(buffer))
            {
                BotReportWriter.Write(w, run, DateTime.UtcNow, Math.Max(1, _options.TickHz), Ticks, fleet, traced, cursors);
            }

            try
            {
                using var content = new ReadOnlyMemoryContent(buffer.WrittenMemory);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var answer = await http.PostAsync(target, content, ct).ConfigureAwait(false);
                if (answer.IsSuccessStatusCode)
                {
                    // Accepted: what it carried is not sent again.
                    for (var i = 0; i < traced.Count; i++)
                    {
                        (traced[i].SentEvents, traced[i].SentTrail) = cursors[i];
                    }

                    Interlocked.Increment(ref _reportsSent);
                    continue;
                }

                ReportFailed($"{(int)answer.StatusCode} {answer.ReasonPhrase} from {target}");
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException)
            {
                ReportFailed($"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void ReportFailed(string why)
    {
        Interlocked.Increment(ref _reportsFailed);

        // The first failure, once, in words: a dashboard that is not there is not worth a line every three seconds.
        if (Interlocked.CompareExchange(ref _firstReportFailure, why, null) == null)
        {
            Console.WriteLine($"  telemetry: the dashboard did not take a report ({why}); still trying every {ReportPeriod.TotalSeconds:F0} s");
        }
    }

    /// <summary>Sums every bot's counters into <paramref name="fleet"/> and collects the traced bots.</summary>
    private void SampleFleet(FleetStats fleet, List<BotTracer> traced, long[] stats)
    {
        fleet.Begin();
        traced.Clear();
        var live = BotCount;
        Span<long> refusedBy = stackalloc long[MarketRefusals];
        for (var slot = 0; slot < live; slot++)
        {
            var bot = Volatile.Read(ref _bots[slot]);
            if (bot?.Brain is not { } brain)
            {
                continue;
            }

            Array.Clear(stats);
            stats[(int)FleetStat.Hunts] = Volatile.Read(ref bot.HuntsStarted);
            stats[(int)FleetStat.MissionsAsked] = Volatile.Read(ref bot.MissionTakesSent);
            stats[(int)FleetStat.MissionsTaken] = Volatile.Read(ref bot.MissionsTaken);
            stats[(int)FleetStat.MissionsCompleted] = Volatile.Read(ref bot.MissionsCompleted);
            stats[(int)FleetStat.MissionsCancelled] = Volatile.Read(ref bot.MissionsCancelled);
            stats[(int)FleetStat.Mounts] = Volatile.Read(ref bot.Mounts);
            stats[(int)FleetStat.Dismounts] = Volatile.Read(ref bot.Dismounts);
            stats[(int)FleetStat.Reversals] = Volatile.Read(ref bot.Moves.Reversals);
            stats[(int)FleetStat.MarketTrades] = Volatile.Read(ref bot.MarketTrades);
            stats[(int)FleetStat.MarketListed] = Volatile.Read(ref bot.MarketListed);
            stats[(int)FleetStat.MarketVendored] = Volatile.Read(ref bot.MarketVendored);
            stats[(int)FleetStat.MarketBought] = Volatile.Read(ref bot.MarketBought);
            stats[(int)FleetStat.MarketBuyLost] = Volatile.Read(ref bot.MarketBuyLost);
            stats[(int)FleetStat.MarketWon] = Volatile.Read(ref bot.MarketWon);
            stats[(int)FleetStat.MarketCollected] = Volatile.Read(ref bot.MarketCollected);
            for (var i = 0; i < MarketRefusals; i++)
            {
                refusedBy[i] = Volatile.Read(ref bot.MarketRefused[i]);
                stats[(int)FleetStat.MarketRefused] += refusedBy[i];
            }

            fleet.AddMarketRefusals(refusedBy);

            fleet.Add(slot, brain.Goal, brain.Counters, stats);
            if (bot.Tracer != null)
            {
                traced.Add(bot.Tracer);
            }
        }

        fleet.End(DateTime.UtcNow);
    }

    private sealed partial class Bot
    {
        /// <summary>This bot's tracer when the run traces it (<see cref="BotTraceSampler"/>), else null — and its brain's observer is null with it.</summary>
        public BotTracer Tracer;

        /// <summary>Times the server put the body on its swoop, and took it off: state flips, not asks.</summary>
        public long Mounts;

        public long Dismounts;

        /// <summary>The direction of its last move, for the reversal count every bot keeps.</summary>
        public MoveWatch Moves;

        private bool _wasMounted;

        /// <summary>The brain, for the fleet's statistics; null unless the run is thinking.</summary>
        public BotBrain Brain => _brain;

        /// <summary>Attaches a tracer when telemetry is on and this bot is one of the sample.</summary>
        private void TraceIfSampled(int index, BotSwarmOptions options)
        {
            if (_brain != null && Traces(index, options))
            {
                Tracer = new BotTracer(index, Math.Max(1, options.TickHz));
                _brain.Trace = Tracer;
            }
        }

        /// <summary>What one decision did, for the statistics every bot keeps and the trace a few do.</summary>
        private void Watch(long tick, in Perceived world, Intent intent)
        {
            if (world.Mounted != _wasMounted)
            {
                _wasMounted = world.Mounted;
                if (world.Mounted)
                {
                    Mounts++;
                }
                else
                {
                    Dismounts++;
                }
            }

            var reversal = Moves.See(tick, in world, intent, 5 * _ticksPerSecond, out var heading);
            Tracer?.Observe(tick, in world, intent, HasHome, heading, reversal, Client.Store?.Self?.NetId ?? 0u);
        }
    }
}
