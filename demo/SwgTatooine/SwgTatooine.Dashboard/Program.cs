using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwgTatooine.Dashboard;
using SwgTatooine.Dashboard.Components;

// The demo's dashboard: one page over what the game server publishes. Read-only, so it may bind the LAN (see the .csproj).
//   --port 5090            what the game server announces in /typhon/demo.json (its --dashboard-port)
//   --bind 0.0.0.0         every interface by default: a person on the LAN opens it from the game client's link
//   --server http://127.0.0.1:5080   the game server to read

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var port = Value("--port", "5090");
var bind = Value("--bind", "0.0.0.0");
var server = Value("--server", "http://127.0.0.1:5080");

if (!IPAddress.TryParse(bind, out var address) || !int.TryParse(port, out var portNumber) || !Uri.TryCreate(server, UriKind.Absolute, out var serverUri))
{
    Console.Error.WriteLine("usage: SwgTatooine.Dashboard [--port 5090] [--bind 0.0.0.0] [--server http://127.0.0.1:5080]");
    return 2;
}

builder.WebHost.ConfigureKestrel(k => k.Listen(address, portNumber));

// The page's stylesheet from the project's own wwwroot, run from bin as it is: the static-web-assets map is only loaded by default in Development, and
// without it app.css was served empty.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(new DashboardFeed(serverUri));
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardFeed>());

// What the bot swarm reports when run with --dashboard (bot-telemetry.md): received here, never asked of the game server, which knows nothing of bots.
builder.Services.AddSingleton<BotBoard>();

var app = builder.Build();
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
BotIngest.Map(app);

Console.WriteLine($"  dashboard on http://{address}:{portNumber}/ reading {serverUri}");
await app.RunAsync();
return 0;

string Value(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
