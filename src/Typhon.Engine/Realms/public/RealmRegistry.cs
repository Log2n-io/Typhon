using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// The engine's realms: how many it can host and which are registered. Reached through <see cref="DatabaseEngine.Realms"/>.
/// </summary>
/// <remarks>
/// Realms are registered before <c>InitializeArchetypes</c>, after <see cref="DatabaseEngine.ConfigureRealms"/> has sized the table: the spatial rebuild
/// at open needs every realm's grid. Registering at run time (instances, houses) lands with the realm lifecycle work.
/// </remarks>
[PublicAPI]
public sealed class RealmRegistry
{
    private readonly DatabaseEngine _engine;
    private readonly Dictionary<ushort, RealmConfig> _pending = [];

    internal RealmRegistry(DatabaseEngine engine) => _engine = engine;

    /// <summary>The configured realm count: valid ids are <c>[0, MaxRealms)</c>.</summary>
    public int MaxRealms => _engine.RealmTable?.MaxRealms ?? _engine.ConfiguredMaxRealms;

    /// <summary>Registers realm <paramref name="id"/>. Refused when the id is out of range or already registered, or the config is invalid.</summary>
    /// <exception cref="InvalidOperationException">After <c>InitializeArchetypes</c> (run-time registration is not supported yet), or a duplicate.</exception>
    public void Register(RealmId id, RealmConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_engine.RealmTable != null)
        {
            throw new InvalidOperationException(
                $"Realm {id.Value} registered after InitializeArchetypes. Realms are registered at open for now; run-time registration comes with the realm "
                + "lifecycle (instances).");
        }

        if (id.Value >= _engine.ConfiguredMaxRealms)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id.Value,
                $"Realm {id.Value} is out of range: this engine is configured for {_engine.ConfiguredMaxRealms} realm(s). Call ConfigureRealms first.");
        }

        config.Validate(id);
        if (id == RealmId.Default && _engine.HasPendingRealm0Grid)
        {
            throw new InvalidOperationException("Realm 0 was already configured by ConfigureSpatialGrid.");
        }

        if (!_pending.TryAdd(id.Value, config))
        {
            throw new InvalidOperationException($"Realm {id.Value} is already registered.");
        }
    }

    /// <summary>True when <paramref name="id"/> is registered (pending registrations count before <c>InitializeArchetypes</c>).</summary>
    public bool IsRegistered(RealmId id) => _engine.RealmTable?.IsRegistered(id.Value) ?? _pending.ContainsKey(id.Value);

    /// <summary>The registrations made before <c>InitializeArchetypes</c>, which builds the realm table from them.</summary>
    internal IReadOnlyDictionary<ushort, RealmConfig> Pending => _pending;
}
