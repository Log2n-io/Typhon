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
    private readonly List<Action> _archetypeRegistrations = [];
    private SpatialGridConfig? _spatialGrid;

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

    /// <summary>
    /// Registers an archetype so entities of it can be spawned. AOT-safe: captures a closed-generic call to
    /// <see cref="Archetype{TSelf}.Touch"/>, which puts the archetype's shape in the registry before the engine wires its
    /// per-archetype storage. Register the archetype's component types separately with <see cref="Register{T}"/> — the two
    /// are orthogonal (shape vs per-engine storage), and the engine only wires an archetype whose components are all
    /// registered.
    /// </summary>
    /// <typeparam name="TArch">The archetype type.</typeparam>
    public TyphonOptions RegisterArchetype<TArch>() where TArch : Archetype<TArch>
    {
        _archetypeRegistrations.Add(static () => Archetype<TArch>.Touch());
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

    // ── Internal bridge to the existing Add* extension methods ──
    // Each configurator folds the accumulated delegates into one Action (or null when none were added, so the underlying
    // Add* method applies its defaults untouched).

    internal Action<MemoryAllocatorOptions> MemoryAllocatorConfigurator => Fold(_memoryAllocator);

    internal Action<ManagedPagedMMFOptions> StorageConfigurator => Fold(_storage);

    internal Action<DatabaseEngineOptions> EngineConfigurator => Fold(_engine);

    /// <summary>Applies every captured <see cref="Register{T}"/> to a fully-constructed engine. Called once, at first resolve.</summary>
    internal void ApplyComponentRegistrations(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        foreach (var register in _componentRegistrations)
        {
            register(engine);
        }
    }

    /// <summary>Touches every captured <see cref="RegisterArchetype{TArch}"/> — must run before <c>InitializeArchetypes</c>.</summary>
    internal void ApplyArchetypeRegistrations()
    {
        foreach (var touch in _archetypeRegistrations)
        {
            touch();
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
