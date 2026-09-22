using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using Typhon.Subscriptions.AspNetCore;

namespace SwgTatooine.Replication;

/// <summary>
/// The demo's web host: Tatooine ticking behind a WebSocket, with the browser client served beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the first consumer of the replication API, and that is its whole job.</b> Everything here is something a real application has to do — register
/// the transport, map the endpoint, serve the client, answer a health check, shut down on SIGTERM — and nothing here reaches into the engine's internals. If
/// this file needs an <c>InternalsVisibleTo</c> to compile, the public surface is missing something.
/// </para>
/// <para>
/// <b>Run forever, not for a tick count.</b> The measurement runs of this demo stop after a fixed number of ticks, which is right for a benchmark and wrong
/// for a server: a viewer connects when it connects. So this path runs the runtime until the process is asked to stop.
/// </para>
/// </remarks>
public static class TatooineHost
{
    /// <summary>
    /// Serves a running Tatooine over WebSocket until the process is asked to stop.
    /// </summary>
    /// <param name="runtime">The started runtime, whose registry already carries <see cref="TatooineReplication.Declare"/>.</param>
    /// <param name="port">The TCP port to listen on.</param>
    /// <param name="clientRoot">A directory of built client files to serve at the root, or <see langword="null"/> for none.</param>
    /// <param name="origins">Origins a browser may connect from; empty allows any, which is the right default for a demo on a developer's machine.</param>
    /// <returns>A task that completes when the host has stopped.</returns>
    public static async Task ServeAsync(TyphonRuntime runtime, int port, string clientRoot = null, string[] origins = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));

        // The runtime is a singleton the endpoints resolve: the WebSocket endpoint starts its transport against it on the first upgrade, and the catalog
        // endpoint serves exactly the bytes WELCOME carries.
        builder.Services.AddSingleton(runtime);
        builder.Services.AddTyphonSubscriptions(o =>
        {
            if (origins == null || origins.Length == 0)
            {
                o.AllowAnyOrigin();
            }
            else
            {
                foreach (var origin in origins)
                {
                    o.AllowOrigin(origin);
                }
            }
        });

        var app = builder.Build();
        app.UseWebSockets();

        if (!string.IsNullOrEmpty(clientRoot) && Directory.Exists(clientRoot))
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(clientRoot) });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(clientRoot) });
        }

        app.MapTyphonSubscriptions("/ws");
        app.MapTyphonCatalog("/typhon/catalog.json");
        // Plain text, not JSON: the slim builder uses source-generated serialization, and a health check is not worth a serializer context.
        app.MapGet("/healthz", () => Results.Text($"tick {runtime.CurrentTickNumber}"));

        Console.WriteLine($"  Tatooine is serving on http://localhost:{port}  (websocket /ws, catalog /typhon/catalog.json)");
        if (!string.IsNullOrEmpty(clientRoot) && !Directory.Exists(clientRoot))
        {
            Console.WriteLine($"  !! no client build at {clientRoot}; run `npm run build` in demo/SwgTatooine.Client to serve the viewer");
        }

        await app.RunAsync().ConfigureAwait(false);
    }

}
