using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using Typhon.Workbench.Security;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Hosting;

/// <summary>
/// Builds and runs the Workbench web host. Extracted from <c>Program.cs</c> by Feature #435 (#429) so one
/// bootstrap serves both the standalone <c>dotnet run</c> entry point and the <c>typhon ui</c> CLI command
/// (in-process hosting, decision D-6). The host binds a loopback URL in code, serves the pre-built SPA from
/// <c>wwwroot</c>, and — when asked — opens the browser at a tokenized launch URL once Kestrel is listening.
/// </summary>
public static class WorkbenchHost
{
    /// <summary>
    /// Builds the Workbench host per <paramref name="options"/> and runs it to termination (blocking).
    /// </summary>
    /// <param name="options">Host-startup wiring (bind URL, optional db path, whether to open a browser).</param>
    /// <param name="args">Process args forwarded to the ASP.NET configuration system; null for the CLI path.</param>
    /// <returns>The process exit code (0 on graceful shutdown).</returns>
    public static int Run(WorkbenchHostOptions options, string[] args = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(args ?? []);

        // Loopback bind in code (#429). The packaged `typhon` tool has no launchSettings.json, so the 127.0.0.1
        // bind cannot come from a dev profile — it is set here. Keep it loopback-only; never a routable interface.
        builder.WebHost.UseUrls(options.Url);

        builder.Services
            .AddControllers(o =>
            {
                // Force every action to advertise (and return) application/json only. By default MVC also
                // lists text/json and text/plain in the OpenAPI "produces" for JSON responses, which makes
                // Orval 8 emit a discriminated union of three media types per response — garbage at the
                // call site. The Workbench never speaks text/plain for DTOs, so we strip those formatters
                // from the content-negotiation pipeline entirely.
                o.Filters.Add(new ProducesAttribute("application/json"));
            })
            .ConfigureApplicationPartManager(apm =>
            {
                // MVC's default controller discovery keys off the ENTRY assembly. Standalone (`dotnet run`) and
                // the test host both have Typhon.Workbench as (or contributing) the entry, so its controllers are
                // found. Under `typhon ui` the entry assembly is `typhon.dll` (the CLI) and the Workbench's
                // controllers live in a *referenced* assembly that discovery misses — leaving every /api/* route
                // unmapped and silently served by the SPA fallback (a 200 index.html where a 401 belongs). Register
                // the Workbench assembly as an application part explicitly, but only if not already present so the
                // standalone/test hosts don't double-register it (which would make every action ambiguous).
                var wbAssembly = typeof(WorkbenchHost).Assembly;
                if (!apm.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == wbAssembly))
                {
                    apm.ApplicationParts.Add(new AssemblyPart(wbAssembly));
                }
            })
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                // String-encode enums camelCase (matches the TS client which uses 'vsCode' | 'rider' …).
                // Without this, PATCH /api/options/editor 400s because the server can't deserialize "rider"
                // to the EditorKind enum.
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                // Cache-format structs (`SystemTickSummary`, `QueueTickSummary`, `PostTickSummary`) are
                // packed `struct`s with public **fields** — System.Text.Json ignores fields by default and
                // would emit `[{}, {}, ...]` for them inside `/profiler/metadata`. Enable field serialization
                // globally so those arrays are populated. Localhost dev tool — the leak risk is bounded.
                o.JsonSerializerOptions.IncludeFields = true;
            });

        builder.Services.AddOpenApi(o =>
        {
            // Document the three credential channels (Bearer PAT, X-Workbench-Token, X-Session-Token) so
            // the Scalar API explorer can render an Authorize dialog and attach the right header.
            o.AddDocumentTransformer<WorkbenchSecuritySchemeTransformer>();
            o.AddOperationTransformer<WorkbenchSecurityRequirementTransformer>();
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<WorkbenchExceptionHandler>();
        builder.Services.AddWorkbenchServices();

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        // Serve the pre-built SPA from wwwroot (#429). UseDefaultFiles rewrites "/" → "/index.html";
        // UseStaticFiles serves the bundle; MapFallbackToFile (registered last) sends client-side routes to
        // index.html. The API, OpenAPI and Scalar endpoints are matched first, so the fallback only catches
        // otherwise-unhandled GETs. In Vite dev the browser hits :5173, so this path is unused there — it is
        // what makes `typhon ui` serve the UI with no Node at runtime. The file provider is pinned to the
        // resolved SPA root (see ResolveSpaRoot) rather than the default web root: the packaged tool runs from
        // an arbitrary CWD, so wwwroot must be located next to the binary, not relative to the caller.
        var spaRoot = ResolveSpaRoot(app.Environment);
        IFileProvider spaFiles = null;
        if (spaRoot is not null)
        {
            spaFiles = new PhysicalFileProvider(spaRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaFiles });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = spaFiles });
        }

        // OpenAPI document at /openapi.json — stable path agreed upon by Orval and the Vite proxy.
        app.MapOpenApi("/openapi.json");

        // Browser-based API explorer at /api-explorer (Scalar). Reads /openapi.json and lets the user
        // authenticate (Bearer PAT recommended) before firing requests. The page itself is unauthenticated;
        // it's static HTML/JS that performs the requests client-side, so the same trust boundary as the SPA applies.
        app.MapScalarApiReference("/api-explorer", o =>
        {
            o.WithTitle("Typhon Workbench API")
             .WithOpenApiRoutePattern("/openapi.json")
             // Classic layout (Swagger-like) surfaces a global "Authorize" button at the top of the page.
             .WithClassicLayout()
             .AddPreferredSecuritySchemes("Bearer")
             // Persists the pasted PAT in localStorage so it survives page reloads.
             .EnablePersistentAuthentication()
             .WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl);
        });

        app.MapControllers();
        app.MapWorkbenchEndpoints();

        // SPA fallback — registered after the API/OpenAPI/Scalar endpoints so it only catches unmatched routes.
        // Served from the same pinned provider; skipped entirely when no SPA is present (API-only host).
        if (spaFiles is not null)
        {
            app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spaFiles });
        }

        app.Services.RegisterSessionShutdownHook();

        // Eagerly materialize the bootstrap token so the file is written before any client tries to read it
        // (Vite dev proxy, Playwright runs, launcher child processes). The constructor performs the disk write.
        var gate = app.Services.GetRequiredService<BootstrapTokenGate>();
        app.Logger.LogInformation("Workbench bootstrap token written to {Path}", gate.TokenFilePath);

        // Sweep orphan profiler temp files left by prior crashes (LZ4 chunk caches under %TEMP%/typhon-workbench).
        LiveCacheTempFile.SweepOrphans(app.Logger);

        if (options.OpenBrowser)
        {
            ScheduleBrowserLaunch(app, options, gate);
        }

        app.Run();

        return 0;
    }

    /// <summary>
    /// Resolves the directory containing the SPA's <c>index.html</c>, or null when no built SPA is present (the
    /// host then runs API-only). Priority: the configured web root (dev + tests already point it at the project
    /// wwwroot / static-web-assets), then <c>{ContentRoot}/wwwroot</c>, then <c>wwwroot</c> next to the app binary
    /// — the last is what makes the packaged <c>typhon</c> tool work, since its ContentRoot is the caller's CWD
    /// while the bundled wwwroot ships beside the DLL.
    /// </summary>
    private static string ResolveSpaRoot(IWebHostEnvironment env)
    {
        var candidates = new[]
        {
            env.WebRootPath,
            string.IsNullOrEmpty(env.ContentRootPath) ? null : Path.Combine(env.ContentRootPath, "wwwroot"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        };

        foreach (var dir in candidates)
        {
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "index.html")))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// Registers a one-shot <see cref="IHostApplicationLifetime.ApplicationStarted"/> callback that opens the
    /// browser at the tokenized launch URL once Kestrel is actually listening (so the first request never races
    /// the bind). Best-effort — a failed launch prints the URL for manual opening.
    /// </summary>
    private static void ScheduleBrowserLaunch(WebApplication app, WorkbenchHostOptions options, BootstrapTokenGate gate)
    {
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() =>
        {
            var launchUrl = BuildLaunchUrl(options, gate.Token);
            if (BrowserLauncher.TryOpen(launchUrl))
            {
                app.Logger.LogInformation("Workbench UI opened in your browser at {Url}", options.Url);
            }
            else
            {
                app.Logger.LogWarning("Could not open a browser automatically. Open the Workbench manually: {Url}", launchUrl);
            }
        });
    }

    /// <summary>
    /// Builds the tokenized launch URL. The bootstrap token (and optional db path) travel in the URL <b>fragment</b>
    /// (Jupyter-style handoff, #429): a fragment is never sent to the server or written to request logs. The SPA
    /// reads it, moves the token into sessionStorage, and strips it from the address bar. The custom
    /// <c>X-Workbench-Token</c> header the SPA then sends cannot be forged cross-origin (no permissive CORS),
    /// preserving the CSRF protection of the loopback threat model.
    /// </summary>
    private static string BuildLaunchUrl(WorkbenchHostOptions options, string token)
    {
        var url = $"{options.Url.TrimEnd('/')}/#wbtoken={Uri.EscapeDataString(token)}";
        if (!string.IsNullOrEmpty(options.DbPath))
        {
            url += $"&db={Uri.EscapeDataString(options.DbPath)}";
        }

        return url;
    }
}
