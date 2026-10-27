using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SwgTatooine.Dashboard;

/// <summary>A traced bot as the page shows it: its latest scalars, its recent events and its trail, oldest first.</summary>
public sealed record TracedBotView(TracedBotReport Latest, BotEventReport[] Events, BotTrailPoint[] Trail, DateTime SeenAt);

/// <summary>What the bot page renders: the latest fleet statistics and every traced bot, as one immutable answer.</summary>
public sealed record BotBoardView(
    DateTime At,
    string Run,
    double Hz,
    long Tick,
    long Reports,
    BotFleetReport Fleet,
    IReadOnlyList<TracedBotView> Bots)
{
    public static BotBoardView Empty { get; } = new(DateTime.MinValue, null, 1d, 0, 0, null, []);
}

/// <summary>
/// The bot reports the swarm posts (<see cref="BotReport"/>), kept in memory and bounded: the last fleet statistics, a short history of a few of them for
/// the sparklines, and per traced bot its last <see cref="EventCapacity"/> events and <see cref="TrailCapacity"/> trail points (bot-telemetry.md).
/// </summary>
/// <remarks>
/// <b>It only receives.</b> The game server knows nothing of bots, and this knows nothing of the swarm but the JSON: a report is merged, the page reads a
/// snapshot. A report repeating events it already holds (the swarm re-sends when it did not see an answer) is merged by sequence number, so nothing
/// shows twice.
/// </remarks>
public sealed class BotBoard
{
    /// <summary>The most traced bots kept; past it, the one heard from longest ago is dropped.</summary>
    public const int MaxBots = 64;

    public const int EventCapacity = 200;

    public const int TrailCapacity = 240;

    /// <summary>Reports of history kept for the sparklines: ten minutes at one every three seconds.</summary>
    public const int HistoryLength = 200;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Traced> _bots = [];
    private readonly Dictionary<string, Queue<double>> _history = [];
    private BotBoardView _view = BotBoardView.Empty;
    private long _reports;

    /// <summary>Raised after each report is merged; handlers run on the request's thread.</summary>
    public event Action Changed;

    /// <summary>The last merged state.</summary>
    public BotBoardView Latest => Volatile.Read(ref _view);

    /// <summary>One series' history, oldest first.</summary>
    public double[] History(string series)
    {
        lock (_gate)
        {
            return _history.TryGetValue(series, out var q) ? [.. q] : [];
        }
    }

    /// <summary>Merges one report.</summary>
    /// <param name="report">The report.</param>
    /// <param name="now">When it arrived, UTC.</param>
    /// <param name="error">Why it was refused, or null.</param>
    /// <returns>Whether it was taken.</returns>
    public bool Ingest(BotReport report, DateTime now, out string error)
    {
        if (report == null || report.Version != BotReport.CurrentVersion)
        {
            error = $"bot report version {report?.Version.ToString() ?? "none"} is not {BotReport.CurrentVersion}";
            return false;
        }

        if (report.Fleet == null)
        {
            error = "a bot report needs its fleet";
            return false;
        }

        error = null;
        lock (_gate)
        {
            if (report.Run != _view.Run)
            {
                // Another fleet process: its sequence numbers start again, and its bots are other bots.
                _bots.Clear();
                _history.Clear();
            }

            foreach (var bot in report.Traced ?? [])
            {
                if (!_bots.TryGetValue(bot.Index, out var held))
                {
                    held = new Traced();
                    _bots[bot.Index] = held;
                }

                held.Merge(bot, now);
            }

            while (_bots.Count > MaxBots)
            {
                var oldest = -1;
                var at = DateTime.MaxValue;
                foreach (var (index, bot) in _bots)
                {
                    if (bot.SeenAt < at)
                    {
                        (oldest, at) = (index, bot.SeenAt);
                    }
                }

                _bots.Remove(oldest);
            }

            var f = report.Fleet;
            Record("goal changes/bot-min p90", f.GoalChangesPerBotMin?.P90 ?? 0d);
            Record("goal changes/bot-min mean", f.GoalChangesPerBotMin?.Mean ?? 0d);
            Record("reversals/bot-min p90", f.ReversalsPerBotMin?.P90 ?? 0d);
            Record("legs stuck/min", Of(f.LegsPerMin, "Stuck") + Of(f.LegsPerMin, "TravelStuck"));
            Record("hunts given up/min", Of(f.WhyPerMin, "HuntTimeout") + Of(f.WhyPerMin, "HuntOutOfRange"));
            Record("market trades/min", Of(f.PerMin, "marketTrades"));

            var bots = new List<TracedBotView>(_bots.Count);
            foreach (var (_, bot) in _bots)
            {
                bots.Add(bot.View());
            }

            bots.Sort((a, b) => a.Latest.Index.CompareTo(b.Latest.Index));
            _reports++;
            Volatile.Write(ref _view, new BotBoardView(now, report.Run, report.Hz > 0 ? report.Hz : 1d, report.Tick, _reports, f, bots));
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>A named rate of a report's map, 0 when the map or the name is absent.</summary>
    public static double Of(Dictionary<string, double> map, string name) => map != null && map.TryGetValue(name, out var v) ? v : 0d;

    /// <summary>A named count of a report's map, 0 when the map or the name is absent.</summary>
    public static long Of(Dictionary<string, long> map, string name) => map != null && map.TryGetValue(name, out var v) ? v : 0L;

    private void Record(string series, double value)
    {
        if (!_history.TryGetValue(series, out var q))
        {
            q = new Queue<double>(HistoryLength);
            _history[series] = q;
        }

        if (q.Count == HistoryLength)
        {
            q.Dequeue();
        }

        q.Enqueue(value);
    }

    /// <summary>One traced bot's rings.</summary>
    private sealed class Traced
    {
        private readonly Queue<BotEventReport> _events = new(EventCapacity);
        private readonly Queue<BotTrailPoint> _trail = new(TrailCapacity);
        private long _lastEvent;
        private long _lastTrail;
        private TracedBotReport _latest;

        public DateTime SeenAt { get; private set; }

        public void Merge(TracedBotReport bot, DateTime now)
        {
            SeenAt = now;
            foreach (var e in bot.Events ?? [])
            {
                if (e.Seq > _lastEvent)
                {
                    _lastEvent = e.Seq;
                    if (_events.Count == EventCapacity)
                    {
                        _events.Dequeue();
                    }

                    _events.Enqueue(e);
                }
            }

            foreach (var p in bot.Trail ?? [])
            {
                if (p.Seq > _lastTrail)
                {
                    _lastTrail = p.Seq;
                    if (_trail.Count == TrailCapacity)
                    {
                        _trail.Dequeue();
                    }

                    _trail.Enqueue(p);
                }
            }

            // The scalars only: the rings above hold the history, and the view must not carry this report's slice of it as if it were all.
            bot.Events = [];
            bot.Trail = [];
            _latest = bot;
        }

        public TracedBotView View() => new(_latest, [.. _events], [.. _trail], SeenAt);
    }
}

/// <summary>The dashboard's one bot-facing surface: <c>POST /bots/report</c>, which takes a <see cref="BotReport"/> and nothing else.</summary>
public static class BotIngest
{
    /// <summary>The largest report taken, bytes: fifty traced bots' few seconds are tens of kilobytes.</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>Maps the endpoint onto <paramref name="app"/>.</summary>
    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/bots/report", ReceiveAsync);

    private static async Task<IResult> ReceiveAsync(HttpContext http, BotBoard board)
    {
        if (http.Request.ContentLength is > MaxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        BotReport report;
        try
        {
            report = await JsonSerializer.DeserializeAsync<BotReport>(http.Request.Body, BotReport.Json, http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            return Results.BadRequest(e.Message);
        }

        return board.Ingest(report, DateTime.UtcNow, out var error) ? Results.NoContent() : Results.BadRequest(error);
    }
}
