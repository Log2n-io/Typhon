using Typhon.Workbench.Fs;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Security;
using Typhon.Workbench.Services;
using Typhon.Workbench.Sessions;
using Typhon.Workbench.Streams;

namespace Typhon.Workbench.Hosting;

public static class ServiceExtensions
{
    public static IServiceCollection AddWorkbenchServices(this IServiceCollection services)
    {
        services.AddSingleton<BootstrapTokenGate>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<DemoDataProvider>();
        services.AddSingleton<FileBrowserService>();
        services.AddSingleton<SchemaService>();
        // #302 Phase 5: file-backed user options (editor preference, workspace root). Singleton because the
        // FileSystemWatcher hot-reload + atomic-write semantics need a single shared instance.
        services.AddSingleton<OptionsStore>();
        // #302 Phase 6: editor handoff dispatcher (per-OS adapters: VS Code / Cursor / Rider / VS / Custom).
        services.AddSingleton<EditorLauncher>();
        // #308: per-connection event-subscription state for the unified data stream.
        services.AddSingleton<StreamSubscriptionRegistry>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkbenchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sessions/{sessionId:guid}/heartbeat", HeartbeatStream.HandleAsync)
           .WithTags("Sessions");
        app.MapGet("/api/sessions/{sessionId:guid}/resources/stream", ResourceGraphStream.HandleAsync)
           .WithTags("Resources");
        app.MapGet("/api/sessions/{sessionId:guid}/profiler/build-progress", ProfilerBuildProgressStream.HandleAsync)
           .WithTags("Profiler");
        app.MapGet("/api/sessions/{sessionId:guid}/profiler/stream", ProfilerLiveStream.HandleAsync)
           .WithTags("Profiler");
        app.MapGet("/api/sessions/{sessionId:guid}/stream", UnifiedDataStream.HandleAsync)
           .WithTags("Data");
        app.MapGet("/api/options/stream", OptionsChangedStream.HandleAsync)
           .WithTags("Options");
        return app;
    }

    /// <summary>
    /// Registers a shutdown callback that disposes every live session — critical for releasing MMF
    /// file handles before the process exits. Under DEBUG also disposes any leftover mock profiler
    /// servers spun up via the Tier-0 E2E support endpoints.
    /// </summary>
    public static void RegisterSessionShutdownHook(this IServiceProvider services)
    {
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var manager = services.GetRequiredService<SessionManager>();
        lifetime.ApplicationStopping.Register(manager.DisposeAll);
#if DEBUG
        lifetime.ApplicationStopping.Register(() =>
        {
            foreach (var kvp in Typhon.Workbench.Controllers.FixturesController.MockServers)
            {
                try { kvp.Value.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
                catch { /* best-effort shutdown */ }
            }
            Typhon.Workbench.Controllers.FixturesController.MockServers.Clear();
        });
#endif
    }
}
