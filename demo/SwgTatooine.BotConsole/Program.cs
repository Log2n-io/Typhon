using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwgTatooine.BotConsole.Components;
using SwgTatooine.Bots;

// SwgTatooine.BotConsole — the bot box's operator surface. Set a fleet up, start it, watch what the server does.
//
//   SwgTatooine.BotConsole --port 8090 --endpoint ws://10.0.1.17:8080/ws
//
// LOOPBACK by default, like the game server's --bind and the profiler exporter's posture: this page can open a thousand
// sessions against a server, so it is reached over an SSM port forward rather than published. --bind takes an explicit
// address for the cases where that is not possible, and says so loudly when it is not a loopback one.

var builder = WebApplication.CreateBuilder(args);

var port = Value("--port", "8090");
var bind = Value("--bind", "127.0.0.1");
var endpoint = Value("--endpoint", "ws://127.0.0.1:9100/ws");

if (!IPAddress.TryParse(bind, out var address))
{
    Console.Error.WriteLine($"--bind {bind} is not an IP address");
    return 2;
}

if (!IPAddress.IsLoopback(address))
{
    // Not refused — a bot box inside a private subnet is a legitimate place to reach this from another host in the VPC —
    // but never silent. The thing on the other side of this page is a load generator with a Start button.
    Console.WriteLine($"  !! the bot console is binding {address}, which is NOT loopback. Anyone who can reach it can start a fleet.");
}

builder.WebHost.ConfigureKestrel(k => k.Listen(address, int.Parse(port)));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// One runner for the process, because there is one box and one fleet: two runners would be two populations against one
// server reported as one measurement, which FleetRunner refuses anyway.
builder.Services.AddSingleton<FleetRunner>();

// SCOPED, not a singleton. A Blazor Server circuit is a scope, so this gives each browser tab its own copy of the form to edit:
// as a singleton, two tabs bound their inputs to ONE object and a second operator's typing changed the first one's pending run.
// FleetRunner clones whatever it is handed at Start, so the run's own configuration is immutable once it begins either way --
// this is about the form, not the run.
builder.Services.AddScoped(_ => new FleetSettings { Endpoint = endpoint });

// Long enough to close a thousand WebSockets. The default is five seconds, and the stop registered below closes every session
// in the fleet: it would have been cut off part-way through doing the very thing it exists to do, leaving the server holding
// link-dead sessions for their whole resume grace.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(120));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// The fleet is stopped when the process is, so a kill does not leave a thousand sessions on the server waiting out their resume
// grace. ApplicationStopping runs before the host tears the DI container down, which is the only point where the runner is still
// resolvable.
//
// Blocking here is safe — there is no SynchronizationContext on this callback, so there is no deadlock — but it only WORKS
// because ShutdownTimeout was raised above: on the default five seconds the host would abandon the wait mid-close. The
// exception is caught because a throw escaping a lifetime callback takes the shutdown with it, and by then there is nothing
// left to report it to.
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        app.Services.GetRequiredService<FleetRunner>().StopAsync().GetAwaiter().GetResult();
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"the fleet did not stop cleanly: {e.Message}");
    }
});

Console.WriteLine($"bot console on http://{address}:{port} — target {endpoint}");
await app.RunAsync();
return 0;

// The argument reader. Deliberately tiny: everything that configures a RUN is on the page, and what is left here is only
// what has to be known before Kestrel binds.
string Value(string name, string fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
        {
            return args[i + 1];
        }
    }

    return fallback;
}
