using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace SwgTatooine.Dashboard;

/// <summary>What the dashboard last read from the game server, as one immutable answer.</summary>
/// <param name="At">When it was read, UTC.</param>
/// <param name="Up">Whether the game server answered.</param>
/// <param name="Error">Why it did not, or null.</param>
/// <param name="Stats"><c>GET /swg/stats</c>: the economy, the missions, who is playing where, the crowd.</param>
/// <param name="Health"><c>GET /typhon/stats.json</c>: ticks, sessions, replication, systems.</param>
/// <param name="Audit">The last <c>GET /swg/economy/audit</c>, or null before the first.</param>
/// <param name="AuditAt">When that audit ran, UTC.</param>
/// <param name="Ledger">The latest ledger rows, or null.</param>
/// <param name="OutMBps">Replication out, megabytes a second, between the last two reads.</param>
/// <param name="ItemAudit">The last <c>GET /swg/items/audit</c> (inventory-loot.md § 3.5), or null before the first or from a server without items.</param>
public sealed record DashboardSnapshot(
    DateTime At,
    bool Up,
    string Error,
    JsonNode Stats,
    JsonNode Health,
    JsonNode Audit,
    DateTime AuditAt,
    JsonNode Ledger,
    double OutMBps,
    JsonNode ItemAudit)
{
    public static DashboardSnapshot Empty { get; } = new(DateTime.MinValue, false, "not read yet", null, null, null, DateTime.MinValue, null, 0d, null);
}

/// <summary>
/// Reads the game server every two seconds — its stats, its health, the ledger's latest rows — and audits the economy and the items every thirty; keeps a
/// few minutes of history for the sparklines. One poller for every page open, so ten people watching do not mean ten times the reads.
/// </summary>
public sealed class DashboardFeed : BackgroundService
{
    /// <summary>How many reads of history are kept: ten minutes at one read every two seconds.</summary>
    public const int HistoryLength = 300;

    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AuditPeriod = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Dictionary<string, Queue<double>> _history = [];
    private readonly object _lock = new();
    private DashboardSnapshot _latest = DashboardSnapshot.Empty;
    private double _lastOutBytes = double.NaN;
    private DateTime _lastOutAt;

    public DashboardFeed(Uri server)
    {
        Server = server;
        _http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>The game server read.</summary>
    public Uri Server { get; }

    /// <summary>Raised after each read; handlers run on the poller's thread.</summary>
    public event Action Changed;

    /// <summary>The last answer.</summary>
    public DashboardSnapshot Latest => Volatile.Read(ref _latest);

    /// <summary>One series' history, oldest first.</summary>
    public double[] History(string series)
    {
        lock (_lock)
        {
            return _history.TryGetValue(series, out var q) ? [.. q] : [];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var auditDue = DateTime.MinValue;
        using var timer = new PeriodicTimer(Period);
        do
        {
            await ReadAsync(auditDue <= DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            if (auditDue <= DateTime.UtcNow)
            {
                auditDue = DateTime.UtcNow + AuditPeriod;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ReadAsync(bool audit, CancellationToken ct)
    {
        var previous = Latest;
        try
        {
            var stats = JsonNode.Parse(await _http.GetStringAsync("swg/stats", ct).ConfigureAwait(false));
            var health = JsonNode.Parse(await _http.GetStringAsync("typhon/stats.json", ct).ConfigureAwait(false));
            var auditNode = previous.Audit;
            var auditAt = previous.AuditAt;
            var itemAudit = previous.ItemAudit;
            if (audit)
            {
                auditNode = JsonNode.Parse(await _http.GetStringAsync("swg/economy/audit", ct).ConfigureAwait(false));
                auditAt = DateTime.UtcNow;
                itemAudit = await ItemAuditAsync(ct).ConfigureAwait(false);
            }

            // The ledger's end: the audit's last sequence when there is one — the economy's counters start again at 0 with the process, the ledger does
            // not — or the rows written since start.
            var last = Math.Max(Number(auditNode, "lastSeq") is var a && double.IsNaN(a) ? 0d : a, Number(stats, "economy", "transfers") is var t && double.IsNaN(t) ? 0d : t);
            var after = Math.Max(0, (long)last - 12);
            var ledger = JsonNode.Parse(await _http.GetStringAsync(
                string.Create(CultureInfo.InvariantCulture, $"swg/economy/ledger?after={after}&limit=12"), ct).ConfigureAwait(false));

            var now = DateTime.UtcNow;
            var outBytes = Number(health, "replication", "outBytesTotal");
            var outMBps = double.IsNaN(_lastOutBytes) || now <= _lastOutAt ? 0d : (outBytes - _lastOutBytes) / (now - _lastOutAt).TotalSeconds / 1e6;
            _lastOutBytes = outBytes;
            _lastOutAt = now;

            Record("tx/s", Number(stats, "economy", "transfersPerSec", "10s"));
            Record("drops/min", Number(stats, "items", "dropsPerMin", "10s"));
            Record("rewards/min", Number(stats, "economy", "rewardsPerMin", "10s"));
            Record("missions done/min", Number(stats, "missions", "completedPerMin", "10s"));
            Record("lair hits/s", Number(stats, "crowd", "lairHitsPerSec", "10s"));
            Record("tick p99", Number(health, "tickMs", "p99"));
            Record("bots", Number(stats, "population", "bots"));
            Record("out MB/s", outMBps);
            Volatile.Write(ref _latest, new DashboardSnapshot(now, true, null, stats, health, auditNode, auditAt, ledger, outMBps, itemAudit));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Volatile.Write(ref _latest, previous with { At = DateTime.UtcNow, Up = false, Error = e.Message });
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The item audit, run with the credit audit; null when the server has no items (an older build answers 404), which must not mark the whole server
    /// down. It is timed with the audit it ran beside.
    /// </summary>
    private async Task<JsonNode> ItemAuditAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("swg/items/audit", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : null;
    }

    private void Record(string series, double value)
    {
        lock (_lock)
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

            q.Enqueue(double.IsFinite(value) ? value : 0d);
        }
    }

    /// <summary>A number at a path in an answer; NaN when it is not there.</summary>
    public static double Number(JsonNode node, params string[] path)
    {
        foreach (var step in path)
        {
            node = node?[step];
        }

        return node is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
