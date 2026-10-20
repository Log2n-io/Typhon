using Typhon.Workbench.DataBrowser;
using Typhon.Workbench.Fs;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Security;
using Typhon.Workbench.Services;
using Typhon.Workbench.Services.Querying;
using Typhon.Workbench.Sessions;
using Typhon.Workbench.Storage;
using Typhon.Workbench.Streams;

namespace Typhon.Workbench.Hosting;

public static class ServiceExtensions
{
    public static IServiceCollection AddWorkbenchServices(this IServiceCollection services)
    {
        services.AddSingleton<BootstrapTokenGate>();
        services.AddSingleton<PersonalAccessTokenStore>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<DemoDataProvider>();
        services.AddSingleton<FileBrowserService>();
        services.AddSingleton<SchemaService>();
        // Module 06: Data Browser — read-only entity enumeration + component decode over the live engine.
        services.AddSingleton<DataBrowserService>();
        // Module 15: Database File Map — read-only storage introspection of the live engine.
        services.AddSingleton<StorageMapService>();
        // #302 Phase 5: file-backed user options (editor preference, workspace root). Singleton because the
        // FileSystemWatcher hot-reload + atomic-write semantics need a single shared instance.
        services.AddSingleton<OptionsStore>();
        // #302 Phase 6: editor handoff dispatcher (per-OS adapters: VS Code / Cursor / Rider / VS / Custom).
        services.AddSingleton<EditorLauncher>();
        // #308: per-connection event-subscription state for the unified data stream.
        services.AddSingleton<StreamSubscriptionRegistry>();
        // #386 Phase 1: Query Console — DSL parser + compiler + execute/plan/parse service.
        services.AddSingleton<QueryConsoleService>();
        // #622 (D-7): the machine-local database registry, rooted where the engine writes it. Registered rather than
        // constructed per-request so tests can substitute a temp-rooted instance, the same isolation the bootstrap
        // token, PAT store and options store already need to keep test runs out of the real %LOCALAPPDATA%.
        services.AddSingleton(_ => new Typhon.Engine.DatabaseRegistry(Typhon.Engine.DatabaseRegistry.EffectiveDirectory));
        // #621: owns the paused-session lifecycle — releasing a database to another process and watching for its return. Singleton because it holds one
        // shared poll timer and the per-session watchers; a scoped instance would leave a paused session with nobody watching for it.
        services.AddSingleton<DatabasePauseCoordinator>();
        // #1417: the Backups view's server-side jobs, the engine calls they make (one seam, substituted by tests), and the retention plans it served.
        services.AddSingleton<Typhon.Workbench.Backups.BackupJobRegistry>();
        services.AddSingleton<Typhon.Workbench.Backups.IBackupOperations, Typhon.Workbench.Backups.BackupOperationsAdapter>();
        services.AddSingleton<Typhon.Workbench.Backups.BackupPlanCache>();
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
        app.MapGet("/api/backups/jobs/{jobId:guid}/stream", BackupJobStream.HandleAsync)
           .WithTags("Backups");
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

        // #621: a removed session must stop being watched, or the poll could reopen a database into a session that is being torn down — re-acquiring the very
        // lock the removal just released.
        var pause = services.GetRequiredService<DatabasePauseCoordinator>();
        manager.SessionRemoved += pause.Forget;

        // Registration order is deliberate: cancellation callbacks run LIFO, so registering the coordinator LAST makes it stop FIRST — the poll timer is dead
        // before any session is disposed, rather than racing the teardown.
        lifetime.ApplicationStopping.Register(manager.DisposeAll);
        lifetime.ApplicationStopping.Register(pause.Dispose);

        // #1417: registered last so it runs first — running backup jobs are cancelled (a capture publishes nothing, a restore before its publish leaves
        // only its staging) before any session's engine is disposed under an in-process capture.
        var backupJobs = services.GetRequiredService<Typhon.Workbench.Backups.BackupJobRegistry>();
        lifetime.ApplicationStopping.Register(() => backupJobs.CancelAll(TimeSpan.FromSeconds(10)));
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
