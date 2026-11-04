using Typhon.Engine;
using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session wrapper around a DatabaseEngine, its private IServiceProvider, and (optionally) a
/// collectible AssemblyLoadContext holding the user's schema DLLs. Disposal order is precise:
/// engine first, ALC next, ServiceProvider last — unwinding MMF, WAL, allocators, and releasing
/// the file handle in the right sequence.
/// </summary>
public sealed class EngineLifecycle : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly WorkbenchAssemblyLoadContext _alc;
    private bool _disposed;

    public DatabaseEngine Engine { get; }
    public IResourceRegistry Registry { get; }
    public string FilePath { get; }
    public SchemaCompatibility.State State { get; }
    public int LoadedComponentTypes { get; }
    public SchemaCompatibility.Diagnostic[] Diagnostics { get; }

    /// <summary>Schema DLL paths actually resolved + loaded for this session (ADR-055). Empty when schemaless.</summary>
    public string[] ResolvedSchemaPaths { get; }

    /// <summary>Provenance of the resolved schema: "user-specified" | "registered" | "bundled" | "legacy-adjacent" | "schemaless".</summary>
    public string SchemaStatus { get; }

    /// <summary>
    /// The open took the crash path with no schema archetypes registered, so <c>InitializeArchetypes</c> was not called (#1417 WB-5): the session shows the
    /// image as the last checkpoint left it, before any WAL replay, and the engine refuses an online backup. Open it with its application schema to
    /// recover it.
    /// </summary>
    public bool OpenedOnCrashPathWithoutSchema { get; private init; }

    /// <summary>What <c>InitializeArchetypes</c> threw, or null (also surfaced as an <c>archetype_init_failed</c> diagnostic).</summary>
    public Exception InitializeArchetypesFailure { get; private init; }

    /// <summary>The open's <c>InitializeArchetypes</c> call; zero when it was not called.</summary>
    public TimeSpan InitializeArchetypesDuration { get; private init; }

    private EngineLifecycle(
        ServiceProvider services,
        WorkbenchAssemblyLoadContext alc,
        DatabaseEngine engine,
        IResourceRegistry registry,
        string filePath,
        SchemaCompatibility.State state,
        int loadedComponentTypes,
        SchemaCompatibility.Diagnostic[] diagnostics,
        string schemaStatus,
        string[] resolvedSchemaPaths)
    {
        _services = services;
        _alc = alc;
        Engine = engine;
        Registry = registry;
        FilePath = filePath;
        State = state;
        LoadedComponentTypes = loadedComponentTypes;
        Diagnostics = diagnostics;
        SchemaStatus = schemaStatus;
        ResolvedSchemaPaths = resolvedSchemaPaths;
    }

    /// <summary>
    /// A session's page cache: 131072 pages × 8KB = 1 GiB. Sized for multi-million-entity databases: at that scale the EntityMap (paged hash) +
    /// component clusters + BTree index pages alone need thousands of pages resident or eviction back-pressure dominates on Commit. 1 GiB gives
    /// comfortable headroom on a dev workstation; matches the fixture-generation engine (FixtureDatabase.cs:570) so opening a freshly-generated fixture
    /// behaves identically.
    /// </summary>
    public const ulong SessionPageCacheBytes = 131072UL * 8192;

    /// <summary>
    /// Opens (or auto-creates) a database at the given path, optionally loading schema DLLs into a
    /// per-session collectible ALC and registering their component types. On breaking-schema or
    /// downgrade, the engine stays open but non-<see cref="SchemaCompatibility.State.Ready"/> —
    /// the caller surfaces a banner.
    /// </summary>
    /// <param name="pageCacheBytes">The page cache: a session's <see cref="SessionPageCacheBytes"/> by default; 0 = the engine's default (a settle).</param>
    /// <exception cref="WorkbenchException">
    /// 409 file_locked — file held by another process/session.
    /// 400 engine_open_failed — any other engine open failure.
    /// 404 schema_missing / 400 schema_load_failed / 400 schema_missing_dependency.
    /// </exception>
    public static async Task<EngineLifecycle> OpenAsync(
        string filePath,
        string[] schemaDllPaths = null,
        string[] registeredSchemaDirs = null,
        CancellationToken ct = default,
        ulong pageCacheBytes = SessionPageCacheBytes,
        bool exactSchema = false,
        bool consoleLog = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        schemaDllPaths ??= [];
        registeredSchemaDirs ??= [];

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new WorkbenchException(400, "engine_open_failed", $"Invalid path: {filePath}");
        }

        Directory.CreateDirectory(directory);
        var databaseName = Path.GetFileNameWithoutExtension(fullPath);

        // Windows doesn't release MMF file handles synchronously on Dispose — a fresh POST arriving
        // milliseconds after a DELETE can hit a transient sharing violation. Retry briefly before
        // surfacing it as file_locked (mirrors the engine test harness's DeleteAndWait pattern).
        const int maxAttempts = 6;
        const int retryDelayMs = 100;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return TryOpenOnce(fullPath, directory, databaseName, schemaDllPaths, registeredSchemaDirs, pageCacheBytes, exactSchema, consoleLog);
            }
            catch (WorkbenchException wb) when (wb.ErrorCode == "file_locked" && attempt < maxAttempts)
            {
                await Task.Delay(retryDelayMs, ct);
            }
        }
    }

    private static EngineLifecycle TryOpenOnce(
        string fullPath,
        string directory,
        string databaseName,
        string[] schemaDllPaths,
        string[] registeredSchemaDirs,
        ulong pageCacheBytes,
        bool exactSchema,
        bool consoleLog)
    {
        ServiceProvider sp = null;
        WorkbenchAssemblyLoadContext alc = null;

        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b =>
            {
                // Default engine logs stay quiet (Warning+). The DatabaseEngine category is admitted at Information so
                // the open-time latency breakdown ("Open: InitializeArchetypes …", "Open: WAL recovery …") surfaces on
                // a normal open. A console provider routes it to Kestrel stdout, which `/wb-dev` captures to
                // .claude/state/wb-dev.kestrel.log — the place to read a slow-open diagnosis. Not under `typhon backup drill`, whose stdout is its
                // report (--format json).
                if (consoleLog)
                {
                    b.AddConsole();
                }

                b.AddFilter((category, level) =>
                    level >= LogLevel.Warning ||
                    (level >= LogLevel.Information && category != null && category.StartsWith("Typhon.Engine", StringComparison.Ordinal)));
            });
            services
                .AddResourceRegistry()
                .AddMemoryAllocator()
                .AddEpochManager()
                .AddHighResolutionSharedTimer()
                .AddDeadlineWatchdog()
                .AddManagedPagedMMF(opts =>
                {
                    opts.DatabaseName = databaseName;
                    opts.DatabaseDirectory = directory;
                    // #621 — advertise that this holder will step aside. The Workbench is a long-lived, read-only
                    // observer; an application that wants the database it is sitting on should not have to wait for a
                    // human to close a window. Only a holder's own advertisement enables handoff, so setting it here
                    // changes nothing about how two applications contend with each other.
                    //
                    // This is a promise, and DatabasePauseCoordinator is what keeps it: it watches for the claim file
                    // and disposes this engine when one appears. Advertising without honouring requests would merely
                    // turn an instant failure into a short wait followed by the same failure.
                    opts.YieldableLock = true;
                    // A session's 1 GiB (SessionPageCacheBytes), or the engine's default when the caller asks for 0.
                    if (pageCacheBytes != 0)
                    {
                        opts.DatabaseCacheSize = pageCacheBytes;
                    }
                })
                .AddDatabaseEngine(engineOpts =>
                {
                    ConfigureEngineOptions(engineOpts);

                    // #1485: a drill's or a settle's open reproduces the database as written, with the build that wrote it — or refuses.
                    engineOpts.SchemaMatch = exactSchema ? SchemaMatch.Exact : SchemaMatch.Migrate;
                });

            // Open-time instrumentation (#diagnose-open): bracket the three synchronous open phases so a slow open is
            // fully attributed in the log. Engine construction includes the system-schema load + WAL recovery (the
            // latter logs its own sub-figure); schemaDllLoad is reflection + JIT + per-archetype class-ctor runs;
            // initializeArchetypes is the per-entity reopen rebuilds (which log their own breakdown).
            var openStart = System.Diagnostics.Stopwatch.GetTimestamp();
            sp = services.BuildServiceProvider();
            var engine = sp.GetRequiredService<DatabaseEngine>();
            var engineConstructTicks = System.Diagnostics.Stopwatch.GetTimestamp() - openStart;
            var registry = sp.GetRequiredService<IResourceRegistry>();
            var schemaDllTicks = 0L;
            var initArchetypesTicks = 0L;

            // Schema DLLs to load (ADR-055): an explicit (user-specified) list wins; otherwise resolve the database's persisted assembly manifest
            // (engine.GetRequiredAssemblies) across the search order { registered = user-pointed dirs (Phase 2), bundled = the Workbench's own
            // deployment dir, legacy-adjacent = next to the database file }. The per-DB copy is no longer authoritative — a shipped/current assembly
            // in the Workbench bin (or a user-registered dir) wins over a stale copy.
            //
            // #1485: every match is exact — name, version and public-key token. A file of the right name at another version is another build: it is reported
            // (version_mismatch) and never loaded in place of the one the database names, which an ordinary open would record over it.
            var required = engine.GetRequiredAssemblies();
            var missingAssemblies = new List<string>();
            var mismatchedAssemblies = new List<SchemaAssemblyResolver.VersionMismatch>();
            string schemaStatus;
            string[] resolvedSchemaPaths;
            if (schemaDllPaths.Length > 0)
            {
                var kept = new List<string>(schemaDllPaths.Length);
                foreach (var path in schemaDllPaths)
                {
                    if (SchemaAssemblyResolver.CheckNamedFile(required, path) is { } mismatch)
                    {
                        mismatchedAssemblies.Add(mismatch);
                    }
                    else
                    {
                        kept.Add(path);
                    }
                }

                resolvedSchemaPaths = kept.ToArray();
                schemaStatus = kept.Count > 0 ? "user-specified" : "schemaless";
            }
            else
            {
                var resolution = SchemaAssemblyResolver.Resolve(required, SchemaAssemblyResolver.SearchOrder(directory, registeredSchemaDirs));
                resolvedSchemaPaths = resolution.Paths;
                schemaStatus = resolution.Status;
                missingAssemblies.AddRange(resolution.Missing);
                mismatchedAssemblies.AddRange(resolution.Mismatches);
            }

            if (exactSchema && (missingAssemblies.Count > 0 || mismatchedAssemblies.Count > 0))
            {
                // Refused before anything is loaded or registered: the drill or the settle ends with the reason.
                var differences = new List<string>();
                foreach (var name in missingAssemblies)
                {
                    differences.Add($"assembly {name}: not found (the database names it)");
                }

                foreach (var m in mismatchedAssemblies)
                {
                    differences.Add($"assembly {m.Name}: the database was written by {m.Expected}, {m.Path} is {m.Found}");
                }

                throw new SchemaMismatchException("The schema assemblies", differences);
            }

            SchemaCompatibility.State state = SchemaCompatibility.State.Ready;
            SchemaCompatibility.Diagnostic[] diagnostics = [];
            int loaded = 0;
            var initializeArchetypes = false;
            Exception initFailure = null;

            if (resolvedSchemaPaths.Length > 0)
            {
                var schemaStart = System.Diagnostics.Stopwatch.GetTimestamp();
                alc = new WorkbenchAssemblyLoadContext($"Workbench-Session-{Guid.NewGuid():N}");
                engine.UseArchetypesFrom(alc);
                var loadedSchema = SchemaLoader.LoadSchemaDlls(alc, resolvedSchemaPaths);
                var result = SchemaCompatibility.ClassifyAndRegister(engine, loadedSchema);
                state = result.State;
                diagnostics = result.Diagnostics;
                loaded = result.RegisteredCount;


                // Discover + register archetype types (feature #514 §5.2). Each loaded schema assembly carries a generated `[ModuleInitializer]` barrier
                // (`__TyphonRegistry_<Assembly>`); RunModuleConstructor fires it, and the CLR runs a module initializer exactly once, before any type in the
                // module. So a single call per assembly registers ALL of that assembly's components + archetypes — GeneratedSchemaRegistry specs,
                // component-collection factories, and ArchetypeRegistry finalization — replacing the old per-archetype RunClassConstructor + EnsureFinalized
                // dance (which only ran the barrier implicitly, off the first archetype's class ctor, then re-finalized each type redundantly). Per-assembly
                // try/catch keeps one poisoned module from aborting the others. Then InitializeArchetypes wires per-engine archetype storage (EntityMap pages) so counts are recovered from the
                // MMF-backed state.
                if (result.RegisteredCount > 0 && loadedSchema.ArchetypeTypes.Length > 0)
                {
                    foreach (var asm in loadedSchema.Assemblies)
                    {
                        try
                        {
                            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle);
                        }
                        catch (Exception modEx)
                        {
                            // A poisoned module initializer (a genuine authoring error in one of the assembly's archetypes) shouldn't block the other
                            // assemblies. Surface it as a diagnostic so the Schema Inspector has a breadcrumb instead of silently showing wrong counts.
                            diagnostics = [.. diagnostics, new SchemaCompatibility.Diagnostic(
                                ComponentName: asm.GetName().Name ?? asm.FullName ?? "(schema assembly)",
                                Kind: "archetype_finalize_failed",
                                Detail: modEx.ToString())];
                        }
                    }
                    // Cross-ALC slot-Type freshness is handled by the refcounted engine-use lifecycle (#514): UnregisterEngineUse clears a collectible ALC's
                    // registry entries on session dispose, so the next session's RunModuleConstructor barrier repopulates the slot map with THIS ALC's Types —
                    // no manual RefreshSlotTypes pass needed (retired, #529).
                    schemaDllTicks = System.Diagnostics.Stopwatch.GetTimestamp() - schemaStart;
                    initializeArchetypes = true;
                }

                // An exact open goes on with nothing it could not register or finalize: a component left out, or an archetype whose module failed, would be
                // skipped and the open would then "pass" without it. Only a component the database never stored is no difference (#1485); an archetype
                // that differs is the engine's to refuse (SCHEMA-14).
                if (exactSchema && Array.Exists(diagnostics, d => d.Kind != SchemaCompatibility.NotInDatabaseKind))
                {
                    var refused = new List<string>();
                    foreach (var d in diagnostics)
                    {
                        if (d.Kind != SchemaCompatibility.NotInDatabaseKind)
                        {
                            refused.Add($"{d.ComponentName}: {d.Kind} ({d.Detail})");
                        }
                    }

                    throw new SchemaMismatchException("The schema assemblies", refused);
                }
            }

            // #1417 WB-5: a session without schema archetypes completes its open too, so its engine can take an online backup (the engine refuses one
            // until InitializeArchetypes has run) — as DerivedStructureRegeneration's schemaless call does. Except on the crash path: there it is where the
            // WAL replays, and a process that does not register the application's archetypes cannot replay their records (7012, #1309), so the call
            // would fail the open. Such a session keeps the pre-replay image and cannot back up (OpenedOnCrashPathWithoutSchema says why).
            // An exact open (a drill's, a settle's) always completes: its replay failing on such a record is the answer it owes (#1485).
            var crashPathWithoutSchema = !exactSchema && !initializeArchetypes && engine.CrashRecoveryAtOpen;
            if (!crashPathWithoutSchema)
            {
                // Archetypes whose stored component set differs from the loaded assembly's were skipped, not migrated (D4): each is reported, whether the
                // open completed or threw, and a failure leads the diagnostics.
                var initStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var initialized = SchemaCompatibility.InitializeArchetypes(engine, new SchemaCompatibility.Result(state, diagnostics, loaded), out initFailure);
                initArchetypesTicks = System.Diagnostics.Stopwatch.GetTimestamp() - initStart;
                state = initialized.State;
                diagnostics = initialized.Diagnostics;
            }

            // Another build of a manifest assembly: named in the banner, never loaded in its place (#1485).
            foreach (var m in mismatchedAssemblies)
            {
                diagnostics = [.. diagnostics, new SchemaCompatibility.Diagnostic(
                    m.Name,
                    "version_mismatch",
                    $"Assembly '{m.Name}' at {m.Path} is {m.Found}, but this database was last written by {m.Expected}. It was not loaded: point the "
                    + "Workbench at that build (Options → Schema).")];
                if (state == SchemaCompatibility.State.Ready)
                {
                    state = SchemaCompatibility.State.Incompatible;
                }
            }

            // Any manifest assembly we couldn't locate → surface it as a diagnostic and a non-Ready state so the UI banner names the missing assembly
            // rather than silently opening schemaless (which renders the data as unclassified).
            if (missingAssemblies.Count > 0)
            {
                foreach (var name in missingAssemblies)
                {
                    diagnostics = [.. diagnostics, new SchemaCompatibility.Diagnostic(
                        name,
                        "missing_assembly",
                        $"Assembly '{name}' is referenced by this database but was not found in any registered schema directory, the Workbench's own "
                        + $"binaries, or next to '{Path.GetFileName(fullPath)}'. Register a directory containing it (Options → Schema) to load the schema.")];
                }
                if (state == SchemaCompatibility.State.Ready)
                {
                    state = SchemaCompatibility.State.Incompatible;
                }
            }

            // Open-time total breakdown (#diagnose-open) — read it from the Kestrel log (.claude/state/wb-dev.kestrel.log)
            // to attribute a slow open. The sub-phases (WAL recovery, per-entity rebuilds) emit their own finer figures.
            var toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            engine.LogOpenTiming(
                (System.Diagnostics.Stopwatch.GetTimestamp() - openStart) * toMs,
                engineConstructTicks * toMs,
                schemaDllTicks * toMs,
                initArchetypesTicks * toMs);

            return new EngineLifecycle(sp, alc, engine, registry, fullPath, state, loaded, diagnostics, schemaStatus, resolvedSchemaPaths)
            {
                OpenedOnCrashPathWithoutSchema = crashPathWithoutSchema,
                InitializeArchetypesFailure = initFailure,
                InitializeArchetypesDuration = TimeSpan.FromMilliseconds(initArchetypesTicks * toMs),
            };
        }
        catch (WorkbenchException)
        {
            alc?.Dispose();
            sp?.Dispose();
            throw;
        }
        catch (DatabaseLockedException)
        {
            // Propagated intact rather than flattened into engine_open_failed (#621). This exception is the only place that carries WHO holds the database —
            // OwnerPid / OwnerMachine / StartedAt, observed at the moment of refusal. The caller turns that into a paused session whose banner names the
            // holder; re-reading db.lock to recover it would race, because the holder may have exited and been replaced in between.
            alc?.Dispose();
            sp?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            alc?.Dispose();
            sp?.Dispose();
            if (FindIOException(ex) is { } io && IsFileLocked(io))
            {
                throw new WorkbenchException(409, "file_locked", $"Database file is in use: {fullPath}", ex);
            }
            throw new WorkbenchException(400, "engine_open_failed", $"Failed to open database: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The engine options every Workbench session opens with. An inspector never rewrites the file it inspects: an archetype whose stored component set
    /// differs from the loaded assembly's is skipped and reported, not migrated (#1394, design 07 D4 — a dated clarification of ADR-055, whose
    /// migrate-on-open concerns components, not archetype sets).
    /// </summary>
    internal static void ConfigureEngineOptions(DatabaseEngineOptions engineOpts)
    {
        engineOpts.Wal = new WalWriterOptions
        {
            // WalDirectory left null — the engine derives {bundle}/wal inside the .typhon bundle.
            UseFUA = false
        };
        engineOpts.ArchetypeSetMigration = ArchetypeSetMigrationPolicy.Refuse;
    }

    private static IOException FindIOException(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is IOException io) return io;
        }
        return null;
    }

    private static bool IsFileLocked(IOException ex)
    {
        // Windows ERROR_SHARING_VIOLATION = 32, ERROR_LOCK_VIOLATION = 33.
        var hr = ex.HResult & 0xFFFF;
        return hr == 32 || hr == 33;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Order: engine (flush WAL, unmap pages) → ALC (unload collectible assemblies) → ServiceProvider
        // (unmap MMF, dispose allocator). Engine must go first so it's done touching ALC-loaded Types
        // before we Unload().
        try { Engine.Dispose(); } catch { /* non-fatal; ServiceProvider disposal still runs */ }
        try { _alc?.Dispose(); } catch { /* already unloaded */ }
        try { _services.Dispose(); } catch { /* MMF already unmapped — nothing to do */ }
    }
}
