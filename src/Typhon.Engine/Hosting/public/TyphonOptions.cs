using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine;

/// <summary>
/// Fluent configuration for the one-line Typhon setup surface — <see cref="ServiceCollectionExtensions.AddTyphon"/> and
/// <see cref="DatabaseEngine.Open(string,System.Action{TyphonOptions},Microsoft.Extensions.Logging.ILoggerFactory)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TyphonOptions"/> is a thin façade: it accumulates configuration <b>delegates</b> that target the real option
/// types (<see cref="MemoryAllocatorOptions"/>, <see cref="ManagedPagedMMFOptions"/>, <see cref="DatabaseEngineOptions"/>).
/// It declares no duplicate settings of its own, so validation and defaults stay on those leaf types (see issue #148).
/// </para>
/// <para>
/// <see cref="Register{T}"/> is AOT-safe: it captures a <b>closed-generic</b> delegate to
/// <see cref="DatabaseEngine.RegisterComponentFromAccessor{T}"/> at configuration time, when its component type <c>T</c> is
/// statically known. No <see cref="Type"/>-keyed reflection (<c>MakeGenericType</c> / <c>MakeGenericMethod</c>) is used on
/// this path, so the trimmer preserves every instantiation.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class TyphonOptions
{
    private readonly List<Action<MemoryAllocatorOptions>> _memoryAllocator = [];
    private readonly List<Action<ManagedPagedMMFOptions>> _storage = [];
    private readonly List<Action<DatabaseEngineOptions>> _engine = [];
    private readonly List<Action<DatabaseEngine>> _componentRegistrations = [];
    private SpatialGridConfig? _spatialGrid;
    private readonly List<(int Revision, Action<Transaction> Step)> _seedSteps = [];

    /// <summary>
    /// Sets the database file. The canonical extension is <c>.typhon</c>; any extension is stripped and the file stem
    /// becomes the database name, its directory the database directory. A bare name (no directory) resolves against the
    /// current working directory.
    /// </summary>
    /// <remarks>
    /// The <c>.typhon</c> extension is canonical now; the engine still materialises the on-disk file with its current
    /// convention until the <c>.typhon</c> bundle format lands (issue #450). This method is the single point that will
    /// swing to the bundle path, so callers should not encode the on-disk extension themselves.
    /// </remarks>
    /// <param name="path">The database path, e.g. <c>"game.typhon"</c> or <c>"data/game.typhon"</c>.</param>
    public TyphonOptions DatabaseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileNameWithoutExtension(fullPath);

        _storage.Add(o =>
        {
            o.DatabaseName = name;
            if (!string.IsNullOrEmpty(directory))
            {
                o.DatabaseDirectory = directory;
            }
        });
        return this;
    }

    /// <summary>
    /// Registers a component type for the engine. AOT-safe: captures a closed-generic call to
    /// <see cref="DatabaseEngine.RegisterComponentFromAccessor{T}"/>, applied once the engine is built.
    /// </summary>
    /// <typeparam name="T">The blittable component type.</typeparam>
    public TyphonOptions Register<T>() where T : unmanaged
    {
        _componentRegistrations.Add(static engine => engine.RegisterComponentFromAccessor<T>());
        return this;
    }

    /// <summary>Advanced: adjust the <see cref="MemoryAllocatorOptions"/> directly.</summary>
    public TyphonOptions ConfigureMemoryAllocator(Action<MemoryAllocatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _memoryAllocator.Add(configure);
        return this;
    }

    /// <summary>Advanced: adjust the storage <see cref="ManagedPagedMMFOptions"/> directly (page-cache size, etc.).</summary>
    public TyphonOptions ConfigureStorage(Action<ManagedPagedMMFOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _storage.Add(configure);
        return this;
    }

    /// <summary>
    /// Sets the page-cache size in bytes — the discoverable front for <see cref="PagedMMFOptions.DatabaseCacheSize"/>.
    /// The default is 256 MiB; size it for your workload's transaction working set (a cache too small for it hits
    /// <see cref="PageCacheBackpressureTimeoutException"/>). Must be a multiple of the 8 KiB page size, and between 2 MiB and 4 GiB.
    /// </summary>
    /// <param name="bytes">Cache size in bytes, e.g. <c>512UL * 1024 * 1024</c> for 512 MiB.</param>
    public TyphonOptions PageCacheSize(ulong bytes)
    {
        _storage.Add(o => o.DatabaseCacheSize = bytes);
        return this;
    }

    /// <summary>Advanced: adjust the <see cref="DatabaseEngineOptions"/> directly (WAL, resources, timeouts, etc.).</summary>
    public TyphonOptions ConfigureEngine(Action<DatabaseEngineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _engine.Add(configure);
        return this;
    }

    /// <summary>
    /// Configures the engine-wide spatial grid — required when a registered archetype has a <c>[SpatialIndex]</c> field.
    /// The one-line setup applies this in the narrow window after archetypes are touched and before
    /// <see cref="DatabaseEngine.InitializeArchetypes"/> (which <see cref="ServiceCollectionExtensions.AddTyphon"/> /
    /// <see cref="DatabaseEngine.Open(string,Action{TyphonOptions},Microsoft.Extensions.Logging.ILoggerFactory)"/> run for
    /// you), so you don't have to reach for the manual <c>Add*</c> chain just to call
    /// <see cref="DatabaseEngine.ConfigureSpatialGrid"/>. Only the first create of a spatial database needs this — the grid
    /// config is persisted, so a later reopen reconstructs it without this call.
    /// </summary>
    public TyphonOptions ConfigureSpatialGrid(SpatialGridConfig config)
    {
        _spatialGrid = config;
        return this;
    }

    /// <summary>
    /// Registers a data-seeding step tagged with a monotonic <paramref name="revision"/>. On every open the engine applies each
    /// registered step whose revision is greater than the database's committed seed revision, in <b>ascending revision order</b>,
    /// each inside its own durable transaction — bringing the instance up to date. Call it once per revision as your app evolves:
    /// <code>
    /// o.Seed(1, tx => { /* initial data */ })
    ///  .Seed(2, tx => { /* data introduced in revision 2 */ });
    /// </code>
    /// A fresh database runs every step; an existing one runs only the steps it has not applied yet. It is <b>crash-safe</b>: each
    /// step advances the committed revision in the <i>same</i> transaction as its data, so a crash anywhere in a step rolls both
    /// back and that step re-runs on the next open (a partial step never leaves committed data). Write your data through the
    /// supplied <see cref="Transaction"/> (<c>tx.Spawn</c>/<c>tx.OpenMut</c>); the engine commits each step for you.
    /// </summary>
    /// <param name="revision">The step's revision. Must be &gt;= 1 and unique across all <see cref="Seed"/> calls on these options.</param>
    /// <param name="step">The seeding action for this revision, given its open transaction. Do all of the step's work in that one
    /// transaction so it commits (and, on failure, rolls back) atomically.</param>
    public TyphonOptions Seed(int revision, Action<Transaction> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Seed revision must be >= 1.");
        }
        foreach (var existing in _seedSteps)
        {
            if (existing.Revision == revision)
            {
                throw new ArgumentException($"A seed step for revision {revision} is already registered.", nameof(revision));
            }
        }

        _seedSteps.Add((revision, step));
        return this;
    }

    // ── Internal bridge to the existing Add* extension methods ──
    // Each configurator folds the accumulated delegates into one Action (or null when none were added, so the underlying
    // Add* method applies its defaults untouched).

    internal Action<MemoryAllocatorOptions> MemoryAllocatorConfigurator => Fold(_memoryAllocator);

    internal Action<ManagedPagedMMFOptions> StorageConfigurator => Fold(_storage);

    internal Action<DatabaseEngineOptions> EngineConfigurator => Fold(_engine);

    /// <summary>The registered <see cref="Seed"/> steps, sorted ascending by revision (empty when none). Applied once at first resolve.</summary>
    internal (int Revision, Action<Transaction> Step)[] SeedSteps
    {
        get
        {
            var steps = _seedSteps.ToArray();
            Array.Sort(steps, static (a, b) => a.Revision.CompareTo(b.Revision));
            return steps;
        }
    }

    /// <summary>Applies every captured <see cref="Register{T}"/> to a fully-constructed engine. Called once, at first resolve.</summary>
    internal void ApplyComponentRegistrations(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        foreach (var register in _componentRegistrations)
        {
            register(engine);
        }
    }

    /// <summary>Applies the captured spatial-grid config (if any). Must run AFTER the archetypes are touched and BEFORE
    /// <c>InitializeArchetypes</c> — the engine builds the grid + per-archetype spatial state during that call.</summary>
    internal void ApplySpatialGridConfig(DatabaseEngine engine)
    {
        if (_spatialGrid.HasValue)
        {
            engine.ConfigureSpatialGrid(_spatialGrid.Value);
        }
    }

    private static Action<T> Fold<T>(List<Action<T>> actions)
    {
        if (actions.Count == 0)
        {
            return null;
        }

        return target =>
        {
            foreach (var action in actions)
            {
                action(target);
            }
        };
    }
}
