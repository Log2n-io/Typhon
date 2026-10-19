using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsteroidField.Bot;
using AsteroidField.Client;

// Usage: AsteroidField.Bot [--host 127.0.0.1] [--port 9300] [--bots 1] [--seconds 0]. Headless pilots, for a belt with company in it or a check that
// the server answers. 0 seconds runs until Ctrl+C.
var host = "127.0.0.1";
var port = 9300;
var bots = 1;
var seconds = 0;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host":
            host = args[++i];
            break;
        case "--port":
            port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--bots":
            bots = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--seconds":
            seconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
if (seconds > 0)
{
    stop.CancelAfter(TimeSpan.FromSeconds(seconds));
}

var links = new List<BeltLink>();
var brains = new List<PilotBrain>();
for (var i = 0; i < bots; i++)
{
    var link = new BeltLink(host, port);
    await link.ConnectAsync();
    links.Add(link);
    brains.Add(new PilotBrain(link));
}

Console.WriteLine($"{bots} pilot(s) flying against {host}:{port}.");
var report = DateTime.UtcNow;
while (!stop.IsCancellationRequested)
{
    foreach (var brain in brains)
    {
        brain.Step();
    }

    if (DateTime.UtcNow - report > TimeSpan.FromSeconds(5))
    {
        report = DateTime.UtcNow;
        var w = brains[0].World;
        Console.WriteLine(w == null
            ? "  no frame yet"
            : $"  tick {w.Tick}: pilot 0 sees {w.Asteroids.Count} asteroids, {w.Ships.Count} ships; hold {w.Cargo}, credits {w.Credits}; "
              + $"holds filled {brains.Sum(b => b.HoldsFull)}");
    }

    try
    {
        await Task.Delay(50, stop.Token);
    }
    catch (OperationCanceledException)
    {
    }
}

foreach (var link in links)
{
    await link.DisposeAsync();
}

return 0;
