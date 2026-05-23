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
    internal IMemoryAllocator MemoryAllocator { get; }
    public string FilePath { get; }
    public SchemaCompatibility.State State { get; }
    public int LoadedComponentTypes { get; }
    public SchemaCompatibility.Diagnostic[] Diagnostics { get; }

    private EngineLifecycle(
        ServiceProvider services,
        WorkbenchAssemblyLoadContext alc,
        DatabaseEngine engine,
        IResourceRegistry registry,
        IMemoryAllocator allocator,
        string filePath,
        SchemaCompatibility.State state,
        int loadedComponentTypes,
        SchemaCompatibility.Diagnostic[] diagnostics)
    {
        _services = services;
        _alc = alc;
        Engine = engine;
        Registry = registry;
        MemoryAllocator = allocator;
        FilePath = filePath;
        State = state;
        LoadedComponentTypes = loadedComponentTypes;
        Diagnostics = diagnostics;
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
    public static async Task<EngineLifecycle> OpenAsync(string filePath, string[] schemaDllPaths = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        schemaDllPaths ??= [];

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
                return TryOpenOnce(fullPath, directory, databaseName, walDir, schemaDllPaths);
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
        string[] schemaDllPaths)
    {
        ServiceProvider sp = null;
        WorkbenchAssemblyLoadContext alc = null;

        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddFilter((_, level) => level >= LogLevel.Warning));
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
                    // 65536 pages × 8KB = 512 MiB page cache.
                    opts.DatabaseCacheSize = 65536UL * 8192;
                })
                .AddDatabaseEngine(engineOpts =>
                {
                    engineOpts.Wal = new WalWriterOptions
                    {
                        WalDirectory = walDir,
                        UseFUA = false
                    };
                });

            sp = services.BuildServiceProvider();
            var engine = sp.GetRequiredService<DatabaseEngine>();
            var registry = sp.GetRequiredService<IResourceRegistry>();
            var allocator = sp.GetRequiredService<IMemoryAllocator>();

            // Schema DLLs to load: an explicit (user-specified) list wins; otherwise resolve from the database's persisted assembly manifest
            // (engine.GetRequiredAssemblies, populated on every open including schemaless) by locating each assembly next to the database file.
            var missingAssemblies = new List<string>();
            var resolvedSchemaPaths = schemaDllPaths.Length > 0
                ? schemaDllPaths
                : ResolveManifestAssemblies(engine, directory, missingAssemblies);

            SchemaCompatibility.State state = SchemaCompatibility.State.Ready;
            SchemaCompatibility.Diagnostic[] diagnostics = [];
            int loaded = 0;

            if (resolvedSchemaPaths.Length > 0)
            {
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
                    try
                    {
                        engine.InitializeArchetypes();
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
                        $"Assembly '{name}' is referenced by this database but was not found next to '{Path.GetFileName(fullPath)}'. Place the assembly there to load the schema.")];
                }
                if (state == SchemaCompatibility.State.Ready)
                {
                    state = SchemaCompatibility.State.Incompatible;
                }
            }

            return new EngineLifecycle(sp, alc, engine, registry, allocator, fullPath, state, loaded, diagnostics);
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
    /// Resolves the database's persisted assembly manifest to on-disk DLL paths, all within <paramref name="directory"/> (the database file's own directory).
    /// Each assembly is located by simple name: first the fast path <c>{SimpleName}.dll</c>, then a metadata name-match over every <c>*.dll</c> in the directory
    /// (so a versioned or <c>*.schema.dll</c>-named file still resolves by its real assembly name). Names that resolve to no file are appended to
    /// <paramref name="missing"/>. The core engine assembly is never in the manifest, so it is never sought here.
    /// </summary>
    private static string[] ResolveManifestAssemblies(DatabaseEngine engine, string directory, List<string> missing)
    {
        var required = engine.GetRequiredAssemblies();
        if (required.Count == 0)
        {
            return [];
        }

        var paths = new List<string>(required.Count);
        Dictionary<string, string> byMetadataName = null; // built lazily on the first fast-path miss
        foreach (var an in required)
        {
            var name = an.Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var direct = Path.Combine(directory, name + ".dll");
            if (File.Exists(direct))
            {
                paths.Add(direct);
                continue;
            }

            byMetadataName ??= BuildAssemblyNameIndex(directory);
            if (byMetadataName.TryGetValue(name, out var resolved))
            {
                paths.Add(resolved);
                continue;
            }

            missing.Add(name);
        }

        return paths.ToArray();
    }

    /// <summary>Indexes every managed <c>*.dll</c> in <paramref name="directory"/> by its assembly simple name (metadata only — no assembly is loaded).</summary>
    private static Dictionary<string, string> BuildAssemblyNameIndex(string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
