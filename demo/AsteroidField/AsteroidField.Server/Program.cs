using System;
using System.Globalization;
using System.Threading;
using AsteroidField.Server;

// Usage: AsteroidField.Server [--port 9300] [--any] [--hz 20] [--asteroids 5000] [--drones 8]. Ctrl+C stops it.
var options = new ServerOptions();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            options.Port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--any":
            options.AnyAddress = true;
            break;
        case "--hz":
            options.TickRate = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--asteroids":
            options.Asteroids = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--drones":
            options.Drones = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

using var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};

using (var server = AsteroidServer.Start(options))
{
    Console.WriteLine($"Asteroid Field: {options.Asteroids} asteroids, {options.Drones} drones, {options.TickRate} Hz.");
    Console.WriteLine($"Pilots connect to tcp://{(options.AnyAddress ? "<this machine>" : "127.0.0.1")}:{server.Port} (session kind \"{AsteroidServer.Kind}\"). Ctrl+C stops.");
    while (!stop.Wait(TimeSpan.FromSeconds(10)))
    {
        Console.WriteLine($"  pilots {server.Pilots}, asteroids mined out {server.Depletions}");
    }
}

return 0;
