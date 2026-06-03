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
    /// Opens (or auto-creates) a database at the given path, optionally loading schema DLLs into a
    /// per-session collectible ALC and registering their component types. On breaking-schema or
    /// downgrade, the engine stays open but non-<see cref="SchemaCompatibility.State.Ready"/> —
    /// the caller surfaces a banner.
    /// </summary>
    /// <exception cref="WorkbenchException">
    /// 409 file_locked — file held by another process/session.
    /// 400 engine_open_failed — any other engine open failure.
    /// 404 schema_missing / 400 schema_load_failed / 400 schema_missing_dependency.
    /// </exception>
    public static async Task<EngineLifecycle> OpenAsync(
        string filePath,
        string[] schemaDllPaths = null,
        string[] registeredSchemaDirs = null,
        CancellationToken ct = default)
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
        var walDir = Path.Combine(directory, "wal");
        Directory.CreateDirectory(walDir);

        // Windows doesn't release MMF file handles synchronously on Dispose — a fresh POST arriving
        // milliseconds after a DELETE can hit a transient sharing violation. Retry briefly before
        // surfacing it as file_locked (mirrors the engine test harness's DeleteAndWait pattern).
        const int maxAttempts = 6;
        const int retryDelayMs = 100;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return TryOpenOnce(fullPath, directory, databaseName, walDir, schemaDllPaths, registeredSchemaDirs);
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
        string walDir,
        string[] schemaDllPaths,
        string[] registeredSchemaDirs)
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
                // .claude/state/wb-dev.kestrel.log — the place to read a slow-open diagnosis.
                b.AddConsole();
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
                    // 131072 pages × 8KB = 1 GiB page cache. Sized for multi-million-entity databases: at that
                    // scale the EntityMap (paged hash) + component clusters + BTree index pages alone need
                    // thousands of pages resident or eviction back-pressure dominates on Commit. 1 GiB gives
                    // comfortable headroom on a dev workstation; matches the fixture-generation engine
                    // (FixtureDatabase.cs:570) so opening a freshly-generated fixture behaves identically.
                    opts.DatabaseCacheSize = 131072UL * 8192;
                })
                .AddDatabaseEngine(engineOpts =>
                {
                    engineOpts.Wal = new WalWriterOptions
                    {
                        WalDirectory = walDir,
                        UseFUA = false
                    };
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
            var missingAssemblies = new List<string>();
            string schemaStatus;
            string[] resolvedSchemaPaths;
            if (schemaDllPaths.Length > 0)
            {
                resolvedSchemaPaths = schemaDllPaths;
                schemaStatus = "user-specified";
            }
            else
            {
                resolvedSchemaPaths = ResolveManifestAssemblies(engine, directory, registeredSchemaDirs, missingAssemblies, out schemaStatus);
            }

            SchemaCompatibility.State state = SchemaCompatibility.State.Ready;
            SchemaCompatibility.Diagnostic[] diagnostics = [];
            int loaded = 0;

            if (resolvedSchemaPaths.Length > 0)
            {
                var schemaStart = System.Diagnostics.Stopwatch.GetTimestamp();
                alc = new WorkbenchAssemblyLoadContext($"Workbench-Session-{Guid.NewGuid():N}");
                var loadedSchema = SchemaLoader.LoadSchemaDlls(alc, resolvedSchemaPaths);
                var result = SchemaCompatibility.ClassifyAndRegister(engine, loadedSchema);
                state = result.State;
                diagnostics = result.Diagnostics;
                loaded = result.RegisteredCount;

                // Discover + register archetype types. Two-step dance:
                //   1. RunClassConstructor triggers the archetype's static field initializers — the
                //      `public static readonly Comp<T> X = Register<T>()` lines which populate
                //      ArchetypeRegistry's component→archetype slot map.
                //   2. ArchetypeRegistry.EnsureFinalized assigns the archetype its id and inserts it
                //      into Archetypes[] so GetAllArchetypes() + ComponentTable.EstimatedEntityCount
                //      can find it. (RunClassConstructor alone only runs the field inits — the metadata
                //      insertion happens inside the lazily-invoked Metadata property getter.)
                // Then InitializeArchetypes wires per-engine archetype storage (EntityMap pages) so
                // counts are recovered from the MMF-backed state. Per-archetype try/catch keeps one
                // bad archetype from aborting the rest.
                if (result.RegisteredCount > 0 && loadedSchema.ArchetypeTypes.Length > 0)
                {
                    foreach (var archetypeType in loadedSchema.ArchetypeTypes)
                    {
                        try
                        {
                            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(archetypeType.TypeHandle);
                            Typhon.Engine.Internals.ArchetypeRegistry.EnsureFinalized(archetypeType);
                        }
                        catch (Exception archEx)
                        {
                            // Per-archetype tolerance — a broken archetype shouldn't block the rest. Surface it as
                            // a diagnostic so the user has a breadcrumb; swallowing silently meant the Schema
                            // Inspector could show wrong counts with no visible error.
                            diagnostics = [.. diagnostics, new SchemaCompatibility.Diagnostic(
                                ComponentName: archetypeType.FullName ?? archetypeType.Name,
                                Kind: "archetype_finalize_failed",
                                Detail: archEx.ToString())];
                        }
                    }
                    // After a reopen, every session spins up a fresh collectible ALC — the schema DLL's component/archetype
                    // types are *new* CLR Type instances even when the file is byte-identical. DeclareComponent already
                    // refreshes the global Type→componentTypeId map through its schema-name dedup path, but EnsureFinalized
                    // short-circuits on pre-populated archetype slots, leaving _slotToComponentType pointing at the first
                    // ALC's Type instances. RefreshSlotTypes propagates the current ALC's Types into every archetype so
                    // reflection-equality lookups (Workbench's GetArchetypesForComponent, etc.) match the session's engine.
                    Typhon.Engine.Internals.ArchetypeRegistry.RefreshSlotTypes();
                    schemaDllTicks = System.Diagnostics.Stopwatch.GetTimestamp() - schemaStart;
                    try
                    {
                        var initStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        engine.InitializeArchetypes();
                        initArchetypesTicks = System.Diagnostics.Stopwatch.GetTimestamp() - initStart;
                    }
                    catch (Exception initEx)
                    {
                        diagnostics = [.. diagnostics, new SchemaCompatibility.Diagnostic(
                            ComponentName: "(InitializeArchetypes)",
                            Kind: "archetype_init_failed",
                            Detail: initEx.ToString())];
                        if (state == SchemaCompatibility.State.Ready)
                        {
                            state = SchemaCompatibility.State.MigrationRequired;
                        }
                    }
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

            return new EngineLifecycle(sp, alc, engine, registry, fullPath, state, loaded, diagnostics, schemaStatus, resolvedSchemaPaths);
        }
        catch (WorkbenchException)
        {
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
    /// Resolves the database's persisted assembly manifest to on-disk DLL paths across the ADR-055 search order:
    /// <paramref name="registeredDirs"/> (user-pointed, Phase 2) → the Workbench's own deployment dir ("bundled") →
    /// <paramref name="directory"/> (the database file's own directory, "legacy-adjacent"). Each assembly is located
    /// by simple name: first the fast path <c>{SimpleName}.dll</c>, then a metadata name-match over every <c>*.dll</c>
    /// in the dir (so a versioned or <c>*.schema.dll</c>-named file still resolves by its real assembly name). First
    /// hit wins. Names that resolve to no file are appended to <paramref name="missing"/>. The core engine assembly is
    /// never in the manifest, so it is never sought here.
    /// </summary>
    private static string[] ResolveManifestAssemblies(DatabaseEngine engine, string directory, string[] registeredDirs, List<string> missing, out string status)
    {
        var required = engine.GetRequiredAssemblies();
        if (required.Count == 0)
        {
            status = "schemaless";
            return [];
        }

        // ADR-055 search order. A user-registered dir (e.g. a recompiled-from-git schema) wins over the Workbench's
        // own shipped/current binaries, which in turn win over a *.schema.dll copied next to the database by an older
        // build. The per-DB copy is kept only as a transition fallback ("legacy-adjacent"); a future build can drop it.
        var searchDirs = new List<(string Dir, string Tier)>((registeredDirs?.Length ?? 0) + 2);
        if (registeredDirs != null)
        {
            foreach (var d in registeredDirs)
            {
                if (!string.IsNullOrWhiteSpace(d))
                {
                    searchDirs.Add((d, "registered"));
                }
            }
        }
        searchDirs.Add((AppContext.BaseDirectory, "bundled"));
        searchDirs.Add((directory, "legacy-adjacent"));

        var nameIndex = new Dictionary<string, string>[searchDirs.Count]; // metadata-name index, built lazily per dir

        var paths = new List<string>(required.Count);
        var usedLegacy = false;
        var usedRegistered = false;
        foreach (var an in required)
        {
            var name = an.Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            string resolved = null;
            var resolvedTier = "bundled";
            for (var i = 0; i < searchDirs.Count; i++)
            {
                // Fast path: {SimpleName}.dll. Fixtures resolve here (file == "Typhon.Workbench.Fixtures.schema.dll").
                var direct = Path.Combine(searchDirs[i].Dir, name + ".dll");
                if (File.Exists(direct))
                {
                    resolved = direct;
                    resolvedTier = searchDirs[i].Tier;
                    break;
                }
                // Slow path: match by assembly metadata name (handles versioned / renamed files).
                nameIndex[i] ??= BuildAssemblyNameIndex(searchDirs[i].Dir);
                if (nameIndex[i].TryGetValue(name, out var m))
                {
                    resolved = m;
                    resolvedTier = searchDirs[i].Tier;
                    break;
                }
            }

            if (resolved == null)
            {
                missing.Add(name);
                continue;
            }
            paths.Add(resolved);
            usedLegacy |= resolvedTier == "legacy-adjacent";
            usedRegistered |= resolvedTier == "registered";
        }

        // Provenance precedence (most-notable wins): "legacy-adjacent" the moment we fell back to a per-DB copy for
        // anything (so the UI can flag a still-copied database); else "registered" if a user-pointed dir supplied any
        // assembly; else "bundled" when everything resolved from the Workbench's own binaries.
        status = paths.Count == 0 ? "schemaless"
            : usedLegacy ? "legacy-adjacent"
            : usedRegistered ? "registered"
            : "bundled";
        return paths.ToArray();
    }

    /// <summary>
    /// Indexes every managed <c>*.dll</c> in <paramref name="directory"/> by its assembly simple name (metadata only —
    /// no assembly is loaded). Returns an empty map for a missing/blank directory (a registered dir may not exist yet).
    /// </summary>
    private static Dictionary<string, string> BuildAssemblyNameIndex(string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return map;
        }
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var name = System.Reflection.AssemblyName.GetAssemblyName(dll).Name;
                if (name != null)
                {
                    map.TryAdd(name, dll);
                }
            }
            catch
            {
                // Not a managed assembly (native DLL, corrupt, etc.) — skip it.
            }
        }
        return map;
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
