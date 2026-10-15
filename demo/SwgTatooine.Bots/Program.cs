using SwgTatooine;
using SwgTatooine.Bots;
using System;
using System.Threading;
using System.Threading.Tasks;

// SwgTatooine.Bots — point a population of scripted god cameras at a running server and report what happened to them.
//
//   SwgTatooine.Bots --endpoint ws://127.0.0.1:9100/ws --bots 100 --seconds 3600
//
// The report is the product. A load generator that only prints "done" cannot distinguish a server that carried a hundred
// sessions for an hour from one that shed them in the first minute and left the generator reconnecting.
//
// The default is a WEBSOCKET endpoint because that is what `SwgTatooine --serve <port>` listens on, and nothing else. A tcp://
// default read plausibly and connected to nothing: the swarm printed its first line and exited zero, having measured a server it
// never reached. The scheme and the port have to name what the server actually serves.

// The flags are the knobs an A/B moves, so they are parsed as strictly as the server's (#1072): an unknown, misspelt, repeated or malformed flag exits 2
// naming the token, instead of running the default — a mistyped --bots used to give two identical arms under a report that said otherwise.
string endpoint, kind, fuzzMode;
Uri endpointUri;
int bots, seconds, hz, connectBatch, tourSeconds, planets, fuzz, fuzzRate;
double regionM;
try
{
    var defaults = new BotSwarmOptions();
    var r = new ArgReader(args, Usage);
    r.Group("swarm");
    r.Switch("--help", "print this list and exit (or -h as the first argument)");
    endpoint = r.Str("--endpoint", "ws://127.0.0.1:9100/ws", "the server's WebSocket endpoint (`SwgTatooine --serve <port>` listens at ws://host:<port>/ws)",
        "<uri>");
    bots = r.Int("--bots", defaults.Count, "sessions to open", min: 1);
    seconds = r.Int("--seconds", 60, "the measured window, from the moment every session is open", min: 1);
    hz = r.Int("--hz", defaults.TickHz, "the driver's tick rate", min: 1);
    kind = r.Str("--kind", defaults.Kind, "the session kind the server's admission expects", "<kind>");
    connectBatch = r.Int("--connect-batch", defaults.ConnectBatch, "sessions opened per connect step", min: 1);
    // A god camera's region edge: what a ClientRegion god profile (the server's --god-region) observes.
    regionM = r.Dbl("--region-m", defaults.RegionRadiusM, "a god camera's region edge, metres", min: 1);
    // Every god camera moves to the next planet on this period (Realms G3); the report counts the switches applied and the RealmNews heard.
    tourSeconds = r.Int("--god-tour", defaults.TourEverySeconds, "seconds between planet switches; 0 never tours", min: 0);
    planets = r.Int("--planets", defaults.Planets, "planets a touring camera cycles through", min: 1);
    r.Group("fuzz — hostile connections instead of a swarm (AC-17's live half)");
    fuzz = r.Int("--fuzz", 0, "hostile clients to open; 0 runs the swarm", min: 0);
    fuzzRate = r.Int("--fuzz-rate", 500, "messages per second per fuzz client; 0 is the control arm", min: 0);
    fuzzMode = r.Choice("--fuzz-mode", "mixed", ["mixed", "valid", "churn", "mixed-slow"], "what the fuzz clients send");
    if (r.WantsHelp)
    {
        Console.Write(r.Help());
        return 0;
    }

    r.RejectUnknown();
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri))
    {
        throw new ArgumentException($"'--endpoint' takes an absolute URI such as ws://127.0.0.1:9100/ws, not '{endpoint}'.");
    }
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

if (fuzz > 0)
{
    using var fuzzStop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    await new Fuzzer(endpointUri, fuzz, fuzzRate, fuzzMode).RunAsync(fuzzStop.Token);
    return 0;
}

var options = new BotSwarmOptions
{
    Endpoint = endpointUri,
    Count = bots,
    TickHz = hz,
    ConnectBatch = connectBatch,
    Kind = kind,
    RegionRadiusM = regionM,
    TourEverySeconds = tourSeconds,
    Planets = planets,
};

Console.WriteLine($"connecting {bots} {kind} bots to {endpoint} …");

await using var swarm = new BotSwarm(options);
// Armed only once every session is open: armed here, a connect phase longer than `--seconds` left no measured window at all, and every figure
// below was read the moment the last session joined.
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

var opened = await swarm.StartAsync(CancellationToken.None);
Console.WriteLine($"opened {opened} of {bots} sessions");
stopping.CancelAfter(TimeSpan.FromSeconds(seconds));

// A swarm that opened nothing has measured nothing, and every figure below it would be a well-formatted zero. It is reported as a
// failure here rather than left to the reader, because the numbers that follow are the kind a reader trusts.
if (opened == 0)
{
    Console.Error.WriteLine($"no session opened against {endpoint}. Check the scheme and the port: `SwgTatooine --serve <port>` "
        + "listens for WEBSOCKET connections at ws://host:<port>/ws and for nothing else.");
    return 2;
}

if (opened < bots)
{
    Console.Error.WriteLine($"only {opened} of {bots} sessions opened, so everything below is measured on a smaller population "
        + "than was asked for.");
}

var started = DateTime.UtcNow;

// Bytes counted from the same instant as the window, not from the first connect: dividing everything received since the first session opened by the
// time since the last one did overstated the rate by the connect phase's share.
var bytesAtStart = swarm.BytesReceived;

// Every 5 s sample, for the rate over the run's last 10 s: a per-session budget is judged on its steady state, not on the fill that opens a run.
var samples = new System.Collections.Generic.List<(DateTime At, long Bytes)> { (started, bytesAtStart) };
while (!stopping.IsCancellationRequested)
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    samples.Add((DateTime.UtcNow, swarm.BytesReceived));

    // The server's own numbers, as the server reported them to its clients (AC-1's track p99 is the headline).
    Console.WriteLine($"  t+{(DateTime.UtcNow - started).TotalSeconds,6:F0}s  connected {swarm.Connected,5}  "
        + $"messages {swarm.MessagesReceived,10}  faults {swarm.Faults}  "
        + $"frames {Frames()}  "
        + $"stats {Blocks()}  "
        + $"sessions {swarm.ServerMetric("typhon.sessions"),5:F0}  "
        + $"track p99 {swarm.ServerMetric("typhon.subscriptions.track.p99"),7:F3} ms  "
        + $"tick p99 {swarm.ServerMetric("typhon.tick.p99"),7:F3} ms");
}

// TICK frames applied across the population: a connected session that is never produced for looks healthy in every other number.
string Frames()
{
    var (total, min, max, unserved) = swarm.Frames();
    return $"{total,7} total {min}..{max}/session, {unserved} unserved, {swarm.RecordsPerFrame:F0} records/frame";
}

// STATS blocks received across the population: the count, not a value, because a value cannot tell "stopped sending" from "stopped moving".
string Blocks()
{
    var (total, min, max, starved) = swarm.StatsBlocks();
    return $"{total,6} total {min}..{max}/session, {starved} with none, {swarm.StatsGranted} granted";
}

// The measurement line: what fraction of a tick replication costs, at this session count and view shape. One line so a sweep is greppable.
{
    var systems = swarm.LabelledMetric("typhon.system.mean", 64);
    double Mean(string name)
    {
        foreach (var (n, ms) in systems)
        {
            if (n == name)
            {
                return ms;
            }
        }

        return 0;
    }

    var project = Mean("SubscriptionsProject");
    var frames = Mean("SubscriptionsFrames");
    var subs = project + frames;
    var tick = swarm.ServerMetric("typhon.tick.p50");
    var tick99 = swarm.ServerMetric("typhon.tick.p99");
    // The track's own span p99 (P1-17), which the Phase 2 criteria are written against; `subs` above is a sum of two stages' means.
    var track99 = swarm.ServerMetric("typhon.subscriptions.track.p99");
    Console.WriteLine();
    // Wire cost beside CPU cost: bytes per session per second is the number a capacity plan is actually built on, and a design that trades CPU for
    // payload (or the reverse) cannot be judged from the timing half alone.
    var elapsed = Math.Max(1.0, (DateTime.UtcNow - started).TotalSeconds);
    var bytesPerSessionPerSec = bots > 0 ? (swarm.BytesReceived - bytesAtStart) / elapsed / bots : 0;
    var now = DateTime.UtcNow;
    var from = samples[0];
    foreach (var sample in samples)
    {
        if ((now - sample.At).TotalSeconds >= 10)
        {
            from = sample;
        }
    }

    var tailSeconds = Math.Max(1.0, (now - from.At).TotalSeconds);
    var bytesLast10 = bots > 0 ? (swarm.BytesReceived - from.Bytes) / tailSeconds / bots : 0;
    Console.WriteLine($"SWEEP kind={kind} sessions={bots} project={project:F3} frames={frames:F3} "
        + $"subs={subs:F3} trackP99={track99:F3} tickP50={tick:F3} tickP99={tick99:F3} subsPct={(tick > 0 ? subs / tick * 100 : 0):F1} recPerFrame={swarm.RecordsPerFrame:F0} "
        + $"bytesPerSessionPerSec={bytesPerSessionPerSec:F0} bytesLast10={bytesLast10:F0} totalBytes={swarm.BytesReceived}");
    // Every system's mean, heaviest first: the tick is more than replication, and a change that moves cost out of the three stages above shows up here.
    var bySystem = new System.Collections.Generic.List<(string Name, double Ms)>(systems);
    bySystem.Sort((x, y) => y.Ms.CompareTo(x.Ms));
    Console.WriteLine("SYSTEMS " + string.Join(" ", bySystem.ConvertAll(x => $"{x.Name}={x.Ms:F3}")));
}

Console.WriteLine();
Console.WriteLine("server systems by mean duration:");
foreach (var (name, ms) in swarm.LabelledMetric("typhon.system.mean", 4))
{
    Console.WriteLine($"  {ms,8:F3} ms  {name}");
}

Console.WriteLine("live entities per archetype:");
foreach (var (name, count) in swarm.LabelledMetric("typhon.archetype.entities", 8))
{
    Console.WriteLine($"  {count,9:N0}  {name}");
}

Console.WriteLine();
Console.WriteLine($"ran {(DateTime.UtcNow - started).TotalSeconds:F0}s over {swarm.Ticks} driver ticks");
Console.WriteLine($"connected at end: {swarm.Connected} of {opened}");
Console.WriteLine($"messages received: {swarm.MessagesReceived}");
Console.WriteLine($"receive-loop faults: {swarm.Faults}");
var (toursAsked, realmSwitches, news, inRealm) = swarm.Realms();
Console.WriteLine($"realms: {toursAsked} tours asked, {realmSwitches} realm switches applied, {news} RealmNews heard, {inRealm} of {opened} sessions holding a realm");

if (swarm.Disconnects.Count == 0)
{
    Console.WriteLine("disconnects: none");
}
else
{
    Console.WriteLine("disconnects by close code:");
    foreach (var (code, count) in swarm.Disconnects)
    {
        Console.WriteLine($"  {Describe(code)}: {count}");
    }
}

// The verdict reads DELIVERY, not just disconnects. A run where every session connected, stayed connected and received no TICK frame at all used to exit
// zero — which is the failure this file's own remarks name ("a connected session that is never produced for looks healthy in every other number") and then
// did not check for. A driver that never fired, or a population that faulted its way through the run, exited zero too.
var (framesTotal, _, _, unserved) = swarm.Frames();
var healthy = swarm.Disconnects.Count == 0 && swarm.Faults == 0 && swarm.Ticks > 0 && framesTotal > 0 && unserved == 0;

if (!healthy)
{
    Console.Error.WriteLine(
        $"run NOT healthy: disconnects={swarm.Disconnects.Count} faults={swarm.Faults} driverTicks={swarm.Ticks} frames={framesTotal} unserved={unserved}");
}

return healthy ? 0 : 1;

static string Describe(ushort code) => code switch
{
    0 => "0 (the handshake never completed)",
    1001 => "1001 going away",
    1013 => "1013 try again later — the server shed this client under load",
    4001 => "4001 no acknowledgement — the swarm stopped pinging",
    _ => code.ToString(),
};

partial class Program
{
    /// <summary>What <c>--help</c> prints before the flags.</summary>
    private const string Usage =
        """
        SwgTatooine.Bots — scripted sessions against a running SwgTatooine server, and a report of what happened to them.

          usage: SwgTatooine.Bots [flags]                 a swarm of sessions for --seconds, then the report
                 SwgTatooine.Bots --fuzz <clients> [...]  hostile connections instead of a swarm

        """;
}
