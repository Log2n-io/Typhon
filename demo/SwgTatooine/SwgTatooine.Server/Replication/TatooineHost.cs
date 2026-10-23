using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SwgTatooine.Server.Accounts;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Subscriptions.AspNetCore;

namespace SwgTatooine.Server;

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
    /// The header the front door adds and this server requires, which is what makes the CDN's prefix list sufficient.
    /// </summary>
    /// <remarks>
    /// An <c>X-</c> name on purpose: it has to be something a CDN can be configured to add to every origin request, and
    /// it must not collide with a header any intermediary rewrites. CloudFront adds it through an origin request policy.
    /// </remarks>
    public const string OriginSecretHeader = "X-Tatooine-Origin";

    /// <summary>
    /// Requires <see cref="OriginSecretHeader"/> on every request from outside the VPC.
    /// </summary>
    /// <param name="app">The host, before anything else is mapped on it.</param>
    /// <param name="secret">The shared secret the front door adds.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>Its own method so that a test can exercise the refusal.</b> Inline in <c>ServeAsync</c>, which never returns and starts
    /// the whole transport, the only reachable test was a copy of this logic in a fixture — and a copy cannot fail when the
    /// original is wrong. Worse, every request a test can make arrives over loopback, so the exempt branch was the only one that
    /// ever ran: nothing tested that an outside caller without the header is refused, which is the entire point.
    /// </para>
    /// <para>
    /// <b>Why a header at all.</b> 08-hosting § 4: CloudFront's flat-rate Pro tier cannot use a private VPC origin — that needs
    /// Business — so the origin is PUBLIC, and the security group can only narrow it to the CDN's origin-facing prefix list. That
    /// list contains every CloudFront distribution in the world, including one an attacker creates in their own account and
    /// points at this address. The prefix list is therefore necessary and not sufficient, and this header is what makes it
    /// sufficient: only our distribution knows the secret.
    /// </para>
    /// <para>
    /// It must be registered FIRST, before the WebSocket upgrade, the static files and <c>/auth</c>: a check that runs after any
    /// of those is a check some requests get past.
    /// </para>
    /// </remarks>
    internal static WebApplication UseOriginSecret(this WebApplication app, string secret)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        // Encoded ONCE, outside the request path. Inside it, this ran on every request from outside the VPC including each
        // static asset of a page load — two arrays and a string per request for a value that cannot change.
        var expected = System.Text.Encoding.UTF8.GetBytes(secret);

        app.Use(async (context, next) =>
        {
            // <b>Private callers are exempt, and that is not a loophole.</b> The bot fleet connects to the private IP by design
            // (07-bots § 5: same AZ, no transfer cost, still the real TCP and WebSocket path), so it never passes through the
            // front door and has no secret to present. Reaching the private address at all requires already being inside the
            // VPC, which the bot security group is the only thing granted.
            var remote = context.Connection.RemoteIpAddress;
            if (remote != null && IsInsideTheVpc(remote))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // Fixed-time compare: a byte-by-byte one leaks the secret's prefix to anybody who can measure the response, and this
            // is a value an attacker can guess against as fast as the network allows. FixedTimeEquals RETURNS false on a length
            // mismatch rather than throwing, so a missing header needs no special case.
            var given = System.Text.Encoding.UTF8.GetBytes(context.Request.Headers[OriginSecretHeader].ToString());
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, given))
            {
                // 403 and nothing else. Not 401, which invites a credential; not a body, which would confirm to a scanner that
                // something is here worth a second look.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        return app;
    }

    /// <summary>
    /// Refuses <c>/auth</c> to anyone outside the VPC, so account creation is reachable from the bot box and not the internet.
    /// </summary>
    /// <param name="app">The host, before the routes are mapped.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>Because checking a password is deliberately expensive, and the server is the one paying.</b> A signup or a login runs
    /// PBKDF2 at 600 000 iterations — about 51 ms of a core — behind a semaphore of <c>ProcessorCount / 4</c>. That slowness is
    /// the security property: it is what makes a stolen credential store useless. But the cost falls on this process on every
    /// attempt, including wrong ones, while costing the caller one HTTP request. A few hundred junk attempts a second keeps all
    /// four slots permanently busy with a queue behind them, and the tick loop — which wants to run fifty times a second — stops
    /// getting CPU. Cheap to send, expensive to absorb, and the game dies rather than the endpoint.
    /// </para>
    /// <para>
    /// <b><see cref="AuthRateLimiter"/> is not enough on its own on a public bind.</b> It caps each address prefix at ten
    /// attempts refilling one per six seconds, which stops one caller flooding — but a caller with many addresses gets ten each,
    /// and the design's answer to that was a WAF at a CDN edge. A deployment that serves TLS directly has no edge, so the
    /// honest move is to take the endpoint off the public internet instead of trying to out-rate-limit the internet.
    /// </para>
    /// <para>
    /// <b>And nothing public needs it.</b> A god session is anonymous and read-only, so a public demo requires no account at
    /// all; the bot fleet reaches this server on its PRIVATE address (07-bots § 5) and signs up over that. Which also removes a
    /// conflict: raising <c>--auth-burst</c> for a thousand bots used to raise it for the internet too, because the limiter is
    /// one instance for the endpoint. Behind this gate, it only raises it for callers who can get here.
    /// </para>
    /// <para>
    /// <b>404, not 403.</b> A refusal confirms the endpoint exists and is worth coming back to; "no such route" is the same
    /// answer an unrelated path gets, and tells a scanner nothing.
    /// </para>
    /// </remarks>
    internal static WebApplication UseAuthInsideVpcOnly(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var remote = context.Connection.RemoteIpAddress;
            if (remote == null || !IsInsideTheVpc(remote))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        return app;
    }

    /// <summary>
    /// Whether an address is one only something inside our own network could be calling from.
    /// </summary>
    /// <param name="address">The caller's address.</param>
    /// <returns><see langword="true"/> for loopback and the private ranges.</returns>
    /// <remarks>
    /// RFC 1918 for IPv4, RFC 4193 for IPv6, plus loopback and link-local. These are not routable on the internet, so a
    /// request that appears to come from one either does, or arrived through a proxy that rewrote the source -- and
    /// there is no proxy in this deployment: <c>AuthEndpoints.AddressKey</c> carries the same reasoning for the same
    /// reason, and both lines have to change together if a load balancer is ever put in front.
    /// </remarks>
    private static bool IsInsideTheVpc(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // fc00::/7 (unique local) and fe80::/10 (link local).
            var v6 = address.GetAddressBytes();
            return (v6[0] & 0xFE) == 0xFC || (v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80);
        }

        var octets = address.GetAddressBytes();
        return octets[0] == 10
            || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 169 && octets[1] == 254);
    }

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

    /// <summary>Serves the built client from <paramref name="clientRoot"/>: <c>index.html</c> at the root, every other file by its path.</summary>
    /// <param name="app">The host being built.</param>
    /// <param name="clientRoot">The client's build output (<c>dist/</c>).</param>
    /// <remarks>
    /// <b><c>.env</c> is mapped because ASP.NET answers 404 for an extension it has no type for.</b> Babylon's prefiltered environment is
    /// <c>vendor/babylon-&lt;version&gt;/environmentSpecular.env</c>: Vite serves it in development, this host refused it, and every served run drew
    /// without image-based lighting with nothing but a 404 in the browser console to say so. Mapped by name rather than by serving every unknown type, so a
    /// stray file in a build output is still not served.
    /// </remarks>
    internal static void MapClient(WebApplication app, string clientRoot)
    {
        var files = new PhysicalFileProvider(clientRoot);
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".env"] = "application/octet-stream";
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types });
    }

    /// <summary>
    /// Serves a running Tatooine over WebSocket until the process is asked to stop.
    /// </summary>
    /// <param name="runtime">The started runtime, whose registry already carries <see cref="TatooineReplication.Declare"/>.</param>
    /// <param name="port">The TCP port to listen on.</param>
    /// <param name="tlsChainPath">A PEM chain (certbot's <c>fullchain.pem</c>) to serve TLS with, or <see langword="null"/> for plain HTTP.</param>
    /// <param name="tlsKeyPath">The matching PEM private key.</param>
    /// <param name="tlsPort">The port TLS is served on. 443 needs <c>CAP_NET_BIND_SERVICE</c>, which the unit file grants.</param>
    /// <param name="redirectPort">A port that serves nothing but a redirect to the TLS one; <c>0</c> maps none.</param>
    /// <param name="authInsideVpcOnly">
    /// <see langword="true"/> answers <c>/auth</c> only for callers on a private address; the internet gets 404. Required on a
    /// public bind with no CDN in front of it — see <see cref="UseAuthInsideVpcOnly"/>.
    /// </param>
    /// <param name="bindAddress">
    /// The address to bind, or <see langword="null"/> for loopback. <c>0.0.0.0</c> binds every interface.
    /// </param>
    /// <param name="clientRoot">A directory of built client files to serve at the root, or <see langword="null"/> for none.</param>
    /// <param name="assets">
    /// The published asset set to serve at <c>/assets</c> and announce in <c>/typhon/demo.json</c>, or <see langword="null"/>
    /// for a run with no assets — see <see cref="AssetOrigin"/>.
    /// </param>
    /// <param name="origins">Origins a browser may connect from; empty allows any, which is the right default for a demo on a developer's machine.</param>
    /// <param name="realmsJson">The realm directory to publish at <c>/typhon/demo.json</c>, or <see langword="null"/> to publish none.</param>
    /// <param name="realmInventory">
    /// Builds the live realm inventory served at <c>/typhon/realms.json</c>, or <see langword="null"/> to serve none.
    /// </param>
    /// <returns>A task that completes when the host has stopped.</returns>
    public static async Task ServeAsync(TyphonRuntime runtime, int port, string clientRoot = null, string[] origins = null, string realmsJson = null,
        Func<string> realmInventory = null, string bindAddress = null, int authBurst = 0, int authRefillMs = 0, string originSecret = null,
        bool authInsideVpcOnly = false, string tlsChainPath = null, string tlsKeyPath = null, int tlsPort = 443, int redirectPort = 0,
        AssetOrigin assets = null, AssetColliders assetShapes = null, double worldHalfM = 0)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // LOOPBACK unless an address was asked for. This used to be ListenAnyIP unconditionally, which published the world to every interface the box had: on a
        // laptop on a shared network, `--serve 8080` served it to the network. Harmless while the only thing a session can do is watch, and not harmless at all
        // once there is an account to create — so the default changes now rather than on the day the login endpoint lands (08-hosting § 2, 03-server § 2).
        var address = ResolveBindAddress(bindAddress);

        // <b>TLS only when a certificate was actually given.</b> A box whose delegation is not finished, or whose first certbot
        // run has not succeeded, still comes up and serves plain HTTP on the game port with the warning below -- which is
        // diagnosable. Binding 443 with nothing to present would accept connections and fail every handshake, an outage that
        // reads as a network fault; and refusing to start at all would make a half-configured box look like a crash loop.
        var tls = !string.IsNullOrEmpty(tlsChainPath) && !string.IsNullOrEmpty(tlsKeyPath)
            ? new TlsCertificate(tlsChainPath, tlsKeyPath)
            : null;

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(address, port);

            if (tls != null)
            {
                // ServerCertificateSelector rather than a certificate handed over once: it is consulted per connection, which
                // is what lets a renewal be picked up without restarting and dropping every session. See TlsCertificate.
                //
                // <b>Set on the options object, NOT passed to UseHttps directly.</b> `UseHttps(_ => tls.Current())` compiles —
                // it binds to the `Action<HttpsConnectionAdapterOptions>` overload, where the lambda is a CONFIGURE callback
                // whose return value is discarded. So it configured nothing, Kestrel found no certificate, fell back to the
                // ASP.NET developer certificate, and the server died at startup with "No server certificate was specified"
                // while every unit test of the certificate loader passed. Two overloads, one of which silently does nothing.
                k.Listen(address, tlsPort, listen => listen.UseHttps(https =>
                {
                    https.ServerCertificateSelector = (_, _) => tls.Current();
                }));

                if (redirectPort > 0)
                {
                    k.Listen(address, redirectPort);
                }
            }
        });

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

        // ── The front door's shared secret (I3). FIRST in the pipeline, before the WebSocket upgrade, the static files
        // and /auth, because a check that runs after any of those is a check some requests get past.
        //
        // <b>Why a header at all.</b> 08-hosting § 4: CloudFront's flat-rate Pro tier cannot use a private VPC origin --
        // that needs Business -- so the origin is PUBLIC, and the security group can only narrow it to the CDN's
        // origin-facing prefix list. That list contains every CloudFront distribution in the world, including one an
        // attacker creates in their own account and points at this address. The prefix list is therefore necessary and
        // not sufficient, and this header is what makes it sufficient: only our distribution knows the secret.
        //
        // The design said Kestrel checks it. Nothing did, which meant the deployment's stated posture and its real one
        // differed by exactly the step that makes it work.
        if (!string.IsNullOrEmpty(originSecret))
        {
            app.UseOriginSecret(originSecret);
            Console.WriteLine($"  front door: requests from outside the VPC must carry {OriginSecretHeader}");
        }
        else if (!IPAddress.IsLoopback(address))
        {
            // Loud, because the gap is invisible from outside: the server works, the client works, and the origin is
            // reachable by anyone who can route a CloudFront distribution at it.
            Console.WriteLine("  !! no front-door secret set, so a reachable bind is open to anyone who can reach this address. "
                + "Set TATOOINE_ORIGIN_SECRET and the same value on the CDN's origin request policy.");
        }

        if (tls != null)
        {
            Console.WriteLine($"  tls: serving https on {tlsPort} with {tls.Describe()}");

            if (redirectPort > 0)
            {
                // Scoped to the redirect port ONLY, by testing the local port rather than using UseHttpsRedirection globally.
                // A blanket redirect would also catch the game port, which the bot fleet reaches over plain ws on the private
                // address by design -- so the fleet would be answered with a 307 to a name it has no reason to resolve.
                app.Use(async (context, next) =>
                {
                    if (context.Connection.LocalPort == redirectPort)
                    {
                        var host = context.Request.Host.Host;
                        var target = tlsPort == 443 ? $"https://{host}{context.Request.Path}{context.Request.QueryString}"
                            : $"https://{host}:{tlsPort.ToString(CultureInfo.InvariantCulture)}{context.Request.Path}{context.Request.QueryString}";

                        // 308, not 302: permanent and method-preserving, so a browser remembers it and a POST is not silently
                        // turned into a GET.
                        context.Response.Redirect(target, permanent: true, preserveMethod: true);
                        return;
                    }

                    await next(context).ConfigureAwait(false);
                });

                Console.WriteLine($"  tls: port {redirectPort} redirects to https");
            }
        }
        else if (!IPAddress.IsLoopback(address))
        {
            Console.WriteLine("  !! no certificate given, so this reachable bind serves plain HTTP. Browsers will refuse a wss:// "
                + "upgrade from an https:// page, and nothing here is encrypted.");
        }

        // Account creation off the public internet (B/M8b). Before the routes, because it is a property of the request and not
        // of the endpoint -- see UseAuthInsideVpcOnly for why an expensive endpoint is removed rather than rate-limited harder.
        if (authInsideVpcOnly)
        {
            app.UseAuthInsideVpcOnly();
            Console.WriteLine("  /auth: answered only for callers inside the VPC; the internet gets 404");
        }

        app.UseWebSockets();

        // The asset set before the client's own files, and on its own file provider: /assets is a deployment of its own,
        // cached forever by revision name, and must not be shadowed by whatever a client root happens to contain.
        if (assets != null)
        {
            assets.MapOn(app);
            Console.WriteLine($"  /assets: {assets.AssetCount} asset(s) and {assets.TextureCount} shared texture(s) from {assets.Directory}");
            Console.WriteLine($"           pinned to {assets.CatalogueName} for this run; announced in /typhon/demo.json");
        }

        if (!string.IsNullOrEmpty(clientRoot) && Directory.Exists(clientRoot))
        {
            MapClient(app, clientRoot);
        }

        // Identity (A2), before the WebSocket route for no technical reason beyond reading order: a client signs up or logs in here, then presents the token
        // it got on the HELLO that /ws carries. The limiter is one instance for both endpoints, so an address's allowance is shared across them — a signup
        // flood and a login flood are the same flood.
        // The limiter's own numbers unless a run says otherwise (--auth-burst / --auth-refill-ms). A bot fleet is one address key, so the defaults that
        // make a dictionary run hopeless also make a thousand bots take ninety-nine minutes to log in; raising them is a property of the run, not of the
        // build, and a server with no flags behaves exactly as before.
        var limiter = authBurst > 0 || authRefillMs > 0
            ? new AuthRateLimiter(
                authBurst > 0 ? authBurst : AuthRateLimiter.Capacity,
                authRefillMs > 0 ? TimeSpan.FromMilliseconds(authRefillMs) : AuthRateLimiter.RefillInterval,
                AuthRateLimiter.MaxTrackedKeys)
            : new AuthRateLimiter();

        if (authBurst > 0 || authRefillMs > 0)
        {
            Console.WriteLine(
                $"  !! /auth rate limit RAISED for this run: burst {(authBurst > 0 ? authBurst : AuthRateLimiter.Capacity)}, " +
                $"one attempt per {(authRefillMs > 0 ? authRefillMs : (int)AuthRateLimiter.RefillInterval.TotalMilliseconds)} ms per address");
        }

        app.MapAuth(runtime.Engine, limiter);

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
            // The last figure is how wide a body is, which the client needs to predict the same push-out this server
            // applies. Told for the reason every speed is told: a figure mirrored in a client is one this server can
            // never correct.
            var radiusM = Json(TatooineReplication.PlayerRadiusM);
            var maxRadiusM = Json(TatooineReplication.PlayerMaxRadiusM);
            // The DEFAULT as well as the two ends, because they are three different numbers: the floor is what a session
            // may narrow itself to, the ceiling is what the grid's window allows, and the default is what this profile
            // actually serves. Publishing only the floor made the slider start at the narrowest radius the server would
            // accept, which is not the radius the session was being served.
            var defaultRadiusM = Json(TatooineReplication.HumanRadiusM);
            var bodyHalfM = Json(TatooineData.PlayerBodyHalfM);
            // The world's edge as well, because the server clamps every push-out to it and a client that predicts past
            // it diverges from an authority that did not.
            var worldEdge = Json(worldHalfM);
            var player = FormattableString.Invariant(
                    $",\"playerRadiusM\":{radiusM},\"playerMaxRadiusM\":{maxRadiusM},\"playerDefaultRadiusM\":{defaultRadiusM}")
                + FormattableString.Invariant($",\"playerBodyHalfM\":{bodyHalfM},\"worldHalfM\":{worldEdge}");
            // Where the assets are and WHICH catalogue version this process is pinned to. The client is told rather than
            // left to discover it: both ends read the same descriptors, and the only way two readers of a mutable pointer
            // stay in step is if neither of them reads it. Absent from a run started without --assets, like the two above.
            var assetSet = assets == null ? string.Empty : assets.DemoJson(assetShapes);
            return Results.Text(
                FormattableString.Invariant(
                    $"{{\"godRegionMaxEdgeM\":{Json(edge)},\"maxViewRadiusM\":{Json(radius)}{player}{assetSet}{realms}}}"),
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

        // <b>Printed because it is the ceiling a crowd run hits, and it used to be invisible.</b> A 10 000-bot fleet against the default
        // table opened 8 192 and stopped, with no line here and no reason on the wire. The number belongs next to the URL for the same
        // reason the TLS and auth lines are there: it is a property of this run that decides whether it can do what was asked of it.
        Console.WriteLine($"  session table: {TatooineReplication.SessionTableSize} clients and god cameras at once (--max-sessions, 65535 is structural)");
        if (!string.IsNullOrEmpty(clientRoot) && !Directory.Exists(clientRoot))
        {
            Console.WriteLine($"  !! no client build at {clientRoot}; run `npm run build` in demo/SwgTatooine/SwgTatooine.Client to serve the viewer");
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
