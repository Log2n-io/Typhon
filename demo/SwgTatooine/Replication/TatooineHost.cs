using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Accounts;
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
    /// Turns the requested bind address into one Kestrel can listen on, saying out loud when the answer is a public one.
    /// </summary>
    /// <param name="requested">What the operator asked for, or <see langword="null"/> for the loopback default.</param>
    /// <returns>The address to bind.</returns>
    /// <exception cref="ArgumentException">The address does not parse — which must be an error rather than a silent fall back to loopback.</exception>
    /// <remarks>
    /// <para>
    /// <b>An unparseable address is refused, not defaulted.</b> Falling back to loopback would be the safe direction for exposure and the wrong one for
    /// trust: an operator who typed an address meant to be reachable, and whose server came up reachable by nobody, would be debugging the client.
    /// </para>
    /// <para>
    /// Binding something other than loopback prints a line saying so. There is no authentication on this port, and <c>/typhon/stats.json</c> publishes system
    /// and archetype names to anyone who asks, so the operator should be told in the terminal rather than in a design document.
    /// </para>
    /// </remarks>
    internal static IPAddress ResolveBindAddress(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return IPAddress.Loopback;
        }

        if (!IPAddress.TryParse(requested.Trim(), out var address))
        {
            throw new ArgumentException($"--bind '{requested}' is not an IP address. Use 127.0.0.1 for local only, or 0.0.0.0 for every interface.");
        }

        if (!IPAddress.IsLoopback(address))
        {
            Console.WriteLine($"  !! binding {address} — this server is reachable from the network, and it has no authentication");
        }

        return address;
    }

    /// <summary>
    /// Serves a running Tatooine over WebSocket until the process is asked to stop.
    /// </summary>
    /// <param name="runtime">The started runtime, whose registry already carries <see cref="TatooineReplication.Declare"/>.</param>
    /// <param name="port">The TCP port to listen on.</param>
    /// <param name="bindAddress">
    /// The address to bind, or <see langword="null"/> for loopback. <c>0.0.0.0</c> binds every interface.
    /// </param>
    /// <param name="clientRoot">A directory of built client files to serve at the root, or <see langword="null"/> for none.</param>
    /// <param name="origins">Origins a browser may connect from; empty allows any, which is the right default for a demo on a developer's machine.</param>
    /// <param name="realmsJson">The realm directory to publish at <c>/typhon/demo.json</c>, or <see langword="null"/> to publish none.</param>
    /// <param name="realmInventory">
    /// Builds the live realm inventory served at <c>/typhon/realms.json</c>, or <see langword="null"/> to serve none.
    /// </param>
    /// <returns>A task that completes when the host has stopped.</returns>
    public static async Task ServeAsync(TyphonRuntime runtime, int port, string clientRoot = null, string[] origins = null, string realmsJson = null,
        Func<string> realmInventory = null, string bindAddress = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // LOOPBACK unless an address was asked for. This used to be ListenAnyIP unconditionally, which published the world to every interface the box had: on a
        // laptop on a shared network, `--serve 8080` served it to the network. Harmless while the only thing a session can do is watch, and not harmless at all
        // once there is an account to create — so the default changes now rather than on the day the login endpoint lands (08-hosting § 2, 03-server § 2).
        var address = ResolveBindAddress(bindAddress);
        builder.WebHost.ConfigureKestrel(k => k.Listen(address, port));

        // The runtime is a singleton the endpoints resolve: the WebSocket endpoint starts its transport against it on the first upgrade, and the catalog
        // endpoint serves exactly the bytes WELCOME carries.
        builder.Services.AddSingleton(runtime);

        // Identity (A2). The /auth bodies are the only JSON this host serialises through a serializer rather than a Utf8JsonWriter, so they bring their own
        // source-generated context — CreateSlimBuilder has no reflection-based resolver to fall back on.
        builder.Services.AddAuthJson();

        builder.Services.AddTyphonSubscriptions(o =>
        {
            if (origins != null && origins.Length > 0)
            {
                foreach (var origin in origins)
                {
                    o.AllowOrigin(origin);
                }
            }
            else if (IPAddress.IsLoopback(address))
            {
                // Any origin is the right default for a developer's own machine, where the client is served from a Vite port that is not the server's.
                o.AllowAnyOrigin();
            }
            else
            {
                // On a reachable bind it is not: any page a visitor opens could then drive this server from their browser. --origin is how a real deployment
                // names its front door, and a public bind without one is refused rather than opened to everybody.
                throw new ArgumentException($"--bind {address} is not loopback, so --origin <url> is required: naming no origin would let any website drive "
                    + "this server from a visitor's browser. Pass --origin for each host the client is served from.");
            }
        });

        var app = builder.Build();
        app.UseWebSockets();

        if (!string.IsNullOrEmpty(clientRoot) && Directory.Exists(clientRoot))
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(clientRoot) });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(clientRoot) });
        }

        // Identity (A2), before the WebSocket route for no technical reason beyond reading order: a client signs up or logs in here, then presents the token
        // it got on the HELLO that /ws carries. The limiter is one instance for both endpoints, so an address's allowance is shared across them — a signup
        // flood and a login flood are the same flood.
        app.MapAuth(runtime.Engine, new AuthRateLimiter());

        app.MapTyphonSubscriptions("/ws");
        app.MapTyphonCatalog("/typhon/catalog.json");
        // 08-hosting.md § 7's stats page, now that the engine can answer it (#ENG-07). The same numbers the STATS block sends to game clients, plus the
        // overrun count and the compute-vs-durability split — readable with curl, which is what makes them watchable on a box nobody has a client for.
        // It writes its JSON with a Utf8JsonWriter, so the slim builder's source-generated serialization is not involved.
        app.MapTyphonStats("/typhon/stats.json");
        // Plain text, not JSON: the slim builder uses source-generated serialization, and a health check is not worth a serializer context.
        app.MapGet("/healthz", () => Results.Text($"tick {runtime.CurrentTickNumber}"));

        // What this process was started with, for the browser client it is serving.
        //
        // <b>It exists because a client cannot discover the god camera's region ceiling any other way.</b> A ClientRegion
        // larger than the profile's `maxEdgeM` is silently shrunk about its centroid, and the ceiling is on no wire the
        // client can read (#1075) — so a viewer asking for a 4 km radius was served 1.5 km and told it had 4. The engine
        // should eventually say so itself; until it does, the one process that owns BOTH ends publishes its own
        // configuration rather than leaving the page to guess.
        //
        // Written by hand for the same reason /healthz is plain text: the slim builder's source-generated serialization
        // would want a serializer context for one object holding two numbers.
        app.MapGet("/typhon/demo.json", () =>
        {
            var edge = TatooineReplication.GodRegionMaxEdgeM;

            // The client sends the smallest square containing its disc, so the side is twice the radius: the largest
            // radius that survives the clamp is half the ceiling. Zero means this server gives its god cameras the whole
            // world and no client-driven region at all, in which case a radius means nothing.
            var radius = edge > 0 ? edge / 2 : 0;
            // The realm directory rides in the same document rather than one of its own: it is the same kind of fact —
            // what this process was started with — and a client that already fetches this should not make a second
            // request for it. Absent when the caller published none, so a page written against a server that serves it
            // must treat it as optional, exactly as it already must for the radius above.
            var realms = realmsJson == null ? string.Empty : FormattableString.Invariant($",\"realms\":{realmsJson}");

            // The two doubles are written with "R" and guarded against the non-finite. Interpolated raw, `--god-region 1e30`
            // emits `1E+30` and a non-finite one emits `NaN` — neither is JSON, so the WHOLE document fails to parse and the
            // client loses the realm directory it also carries, with no error path. A ceiling that cannot be expressed is
            // reported as no ceiling, which is what a client already handles.
            // A PLAYER's range, which is a different mechanism from the god region above: a sphere the session widens with
            // SetViewRadius, between its profile's own radius and the cap the engine's window bound allows. Told here so the
            // client's slider has both ends from the server and no figure of its own — a radius mirrored in the client would be
            // a number the server can never correct, which is the shape of bug `no-speed-literals.test.ts` exists for.
            var player = FormattableString.Invariant(
                $",\"playerRadiusM\":{Json(TatooineReplication.PlayerRadiusM)},\"playerMaxRadiusM\":{Json(TatooineReplication.PlayerMaxRadiusM)}");
            return Results.Text(
                FormattableString.Invariant($"{{\"godRegionMaxEdgeM\":{Json(edge)},\"maxViewRadiusM\":{Json(radius)}{player}{realms}}}"),
                "application/json");
        });

        // What every realm is DOING, against demo.json's what this process was STARTED with (CLI3D-11).
        //
        // <b>Its own document rather than more of demo.json, and the difference is lifetime.</b> demo.json is immutable
        // for the run and is fetched once; this one is polled about once a second. Merging them would make a page
        // re-parse the static realm directory every second and would make an immutable document look volatile.
        //
        // The delegate keeps this file ignorant of the simulation, as everything else here is: the host maps an endpoint
        // and serves what it is handed.
        if (realmInventory != null)
        {
            app.MapGet("/typhon/realms.json", () => Results.Text(realmInventory(), "application/json"));
        }

        // A double as JSON, or 0 for one JSON cannot express. See the note at its call site.
        static string Json(double value) =>
            double.IsFinite(value) ? value.ToString("0.####", CultureInfo.InvariantCulture) : "0";

        // Tell every client the server is going, before Kestrel drops their sockets (SWG-07). ApplicationStopping runs before the listeners are closed and
        // blocks shutdown until it returns, which is exactly the window in which the runtime is still ticking and a KICK can still be staged and sent.
        app.Lifetime.ApplicationStopping.Register(KickEveryone);

        Console.WriteLine($"  Tatooine is serving on http://localhost:{port}  (websocket /ws, catalog /typhon/catalog.json, stats /typhon/stats.json)");
        if (!string.IsNullOrEmpty(clientRoot) && !Directory.Exists(clientRoot))
        {
            Console.WriteLine($"  !! no client build at {clientRoot}; run `npm run build` in demo/SwgTatooine.Client to serve the viewer");
        }

        await app.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The ceiling on waiting for the kicks to be STAGED — three ticks at the slowest rate a server is likely to run, rounded up.
    /// </summary>
    /// <remarks>
    /// A ceiling, not a delay: the wait ends as soon as every session has been kicked or none is left to kick. A server with no client stops as fast as it did
    /// before this existed.
    /// </remarks>
    private const int StageCeilingMs = 400;

    /// <summary>
    /// The unconditional wait for the send pump, once the kicks are staged.
    /// </summary>
    /// <remarks>
    /// <b>A delay, and called one honestly.</b> The pump writes the <c>KICK</c> and closes the link on a pool thread after the tick applies the request, and
    /// there is no public count of frames written that this could wait on instead — so this is the one part of the shutdown that cannot be made a condition.
    /// It is paid only when a client was connected. If the engine ever exposes the pump's kick count, this becomes a spin and goes away.
    /// </remarks>
    private const int PumpSettleMs = 150;

    /// <summary>
    /// Asks the tick to kick every open session, and waits for it to have done so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two boundaries, and only one of them is observable.</b> <c>KICK</c> is a session request: the tick's prologue applies it, and only then does the send
    /// pump write the frame and close the link. The first boundary is a condition and is spun on; the second is <see cref="PumpSettleMs"/>, which is a delay
    /// and says so.
    /// </para>
    /// <para>
    /// <b>The loop re-reads the live count rather than fixing it up front.</b> Taking a snapshot before <c>RequestShutdown</c> meant a client that
    /// disconnected of its own accord in the same window could never be reached, so <c>KicksStaged</c> never caught up and the loop spun the whole ceiling —
    /// with <c>Thread.Sleep(5)</c>, which is about 15 ms at the default timer quantum, so eighty-odd iterations of waiting for a client that had already gone.
    /// </para>
    /// <para>
    /// It is deliberately not <c>async</c>: <c>ApplicationStopping</c> is a synchronous callback, and blocking in it is what holds the listeners open long
    /// enough for the frames to leave.
    /// </para>
    /// </remarks>
    private static void KickEveryone()
    {
        var before = TatooineReplication.LiveSessions;
        var sessions = before.Clients + before.Spectators;
        TatooineReplication.RequestShutdown("server shutting down");
        if (sessions == 0)
        {
            return;
        }

        // Done when every session the tick can still see has been kicked. Re-read, so a client that left on its own ends the wait instead of extending it.
        var deadline = Environment.TickCount64 + StageCeilingMs;
        while (Environment.TickCount64 < deadline)
        {
            var live = TatooineReplication.LiveSessions;
            if (TatooineReplication.KicksStaged >= live.Clients + live.Spectators)
            {
                break;
            }

            Thread.Sleep(5);
        }

        Thread.Sleep(PumpSettleMs);
        Console.WriteLine($"  shutdown: {TatooineReplication.KicksStaged} sessions kicked");
    }
}
