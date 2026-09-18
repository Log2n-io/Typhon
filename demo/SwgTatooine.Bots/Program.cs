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

var endpoint = Arg("--endpoint") ?? "ws://127.0.0.1:9100/ws";
var bots = int.Parse(Arg("--bots") ?? "100");
var seconds = int.Parse(Arg("--seconds") ?? "60");
var hz = int.Parse(Arg("--hz") ?? "4");
var kind = Arg("--kind") ?? "god";

var options = new BotSwarmOptions
{
    Endpoint = new Uri(endpoint),
    Count = bots,
    TickHz = hz,
    Kind = kind,
};

Console.WriteLine($"connecting {bots} {kind} bots to {endpoint} …");

await using var swarm = new BotSwarm(options);
using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

var opened = await swarm.StartAsync(CancellationToken.None);
Console.WriteLine($"opened {opened} of {bots} sessions");

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
    var interest = Mean("SubscriptionsInterest");
    var frames = Mean("SubscriptionsFrames");
    var subs = project + interest + frames;
    var tick = swarm.ServerMetric("typhon.tick.p50");
    Console.WriteLine();
    Console.WriteLine($"SWEEP kind={kind} sessions={bots} project={project:F3} interest={interest:F3} frames={frames:F3} "
        + $"subs={subs:F3} tickP50={tick:F3} subsPct={(tick > 0 ? subs / tick * 100 : 0):F1} recPerFrame={swarm.RecordsPerFrame:F0}");
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

return swarm.Disconnects.Count == 0 ? 0 : 1;

static string Describe(ushort code) => code switch
{
    0 => "0 (the handshake never completed)",
    1001 => "1001 going away",
    1013 => "1013 try again later — the server shed this client under load",
    4001 => "4001 no acknowledgement — the swarm stopped pinging",
    _ => code.ToString(),
};

static string Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
        {
            return args[i + 1];
        }
    }

    return null;
}
