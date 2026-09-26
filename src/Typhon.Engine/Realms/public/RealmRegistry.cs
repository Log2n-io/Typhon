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
        CheckParentChain(id, config);
        ArgumentNullException.ThrowIfNull(config);
        if (_engine.RealmTable != null)
        {
            // Realms D5: a running engine registers at once — grid built, per-archetype tables grown, catalog row written, then published.
            _engine.RegisterRealmAtRuntime(id, config);
            return;
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

    // The parent tree routes RouteToRealm's subtree (12-realms § 3), walked at most 8 deep on the tick: a cycle or a deeper chain would silently lose
    // announcements there, so it is refused here, off the tick.
    private void CheckParentChain(RealmId id, RealmConfig config)
    {
        var parent = config?.Parent ?? RealmId.None;
        for (var depth = 0; !parent.IsNone; depth++)
        {
            if (parent == id)
            {
                throw new ArgumentException($"Realm {id.Value}: its parent chain comes back to it — the parent tree may not have a cycle.", nameof(config));
            }

            if (depth >= 8)
            {
                throw new ArgumentException($"Realm {id.Value}: its parent chain is deeper than 8 realms.", nameof(config));
            }

            var next = _engine.RealmTable?.TryGet(parent.Value)?.Config ?? (_pending.TryGetValue(parent.Value, out var pending) ? pending : null);
            parent = next?.Parent ?? RealmId.None;
        }
    }

    /// <summary>True when <paramref name="id"/> is registered (pending registrations count before <c>InitializeArchetypes</c>).</summary>
    public bool IsRegistered(RealmId id) => _engine.RealmTable?.IsRegistered(id.Value) ?? _pending.ContainsKey(id.Value);

    /// <summary>
    /// Pins realm <paramref name="id"/> active — observed, simulated at full rate — from the next tick on, until the returned handle is disposed. For an
    /// application with no session in the realm (a headless server, a benchmark, a test); sessions observe through replication.
    /// </summary>
    /// <exception cref="InvalidOperationException">Before <c>InitializeArchetypes</c>, or <paramref name="id"/> is not registered.</exception>
    public RealmObserver Observe(RealmId id) => new(OpenTable(), id);

    /// <summary>
    /// Wakes realm <paramref name="id"/>: simulated from the next tick on, and a <see cref="RealmUnobserved.Sleep"/> realm holds for its
    /// <see cref="RealmConfig.SleepAfterTicks"/> again before it may go dormant. Any thread.
    /// </summary>
    public void Wake(RealmId id)
    {
        var table = OpenTable();
        _ = table.Get(id.Value);
        table.RequestWake(id.Value);
    }

    /// <summary>
    /// Unregisters realm <paramref name="id"/> on a running engine (Realms D5): it is <see cref="RealmRunState.Closing"/> from now on — no entity may
    /// enter it — and is removed at the first fence that finds it empty. Empty it with <see cref="DestroyContents"/>. Its id may be registered again only
    /// after a later open (RLM-06). The primary realm cannot be unregistered.
    /// </summary>
    public void Unregister(RealmId id)
    {
        _ = OpenTable();
        _engine.UnregisterRealm(id);
    }

    /// <summary>
    /// Destroys every entity in realm <paramref name="id"/> through <paramref name="tx"/>, on the ordinary destroy path. Returns how many. Commit the
    /// transaction; the next fence frees the realm's clusters (and removes a Closing realm once nothing is left).
    /// </summary>
    public int DestroyContents(RealmId id, Transaction tx)
    {
        _ = OpenTable();
        return _engine.DestroyRealmContents(id, tx);
    }

    /// <summary>How many registered realms are in each run state this tick, and how many run at a divisor (Realms D6). Before
    /// <c>InitializeArchetypes</c>, all zero.</summary>
    public RealmStateCounts Counts
    {
        get
        {
            var table = _engine.RealmTable;
            if (table == null)
            {
                return default;
            }

            int active = 0, simulated = 0, dormant = 0, closing = 0;
            foreach (var realm in table.Registered)
            {
                switch (table.StateOf(realm.Id.Value))
                {
                    case RealmRunState.Active: active++; break;
                    case RealmRunState.Simulated: simulated++; break;
                    case RealmRunState.Dormant: dormant++; break;
                    default: closing++; break;
                }
            }

            return new RealmStateCounts(active, simulated, dormant, closing, table.DividedCount, table.PolicyEpoch);
        }
    }

    /// <summary>What realm <paramref name="id"/> is doing this tick, as its policy decided at tick start.</summary>
    /// <remarks>A realm Unregister has closed reads <see cref="RealmRunState.Closing"/> at once: entries are refused from the call on. Its policy state
    /// — what dispatch reads — follows at the next tick start (RLM-03).</remarks>
    public RealmRunState StateOf(RealmId id)
    {
        var table = OpenTable();
        return table.Get(id.Value).Closing ? RealmRunState.Closing : table.StateOf(id.Value);
    }

    private RealmTable OpenTable() => _engine.RealmTable
        ?? throw new InvalidOperationException("Realms are observed, woken and inspected after InitializeArchetypes, once the realm table exists.");

    /// <summary>The registrations made before <c>InitializeArchetypes</c>, which builds the realm table from them.</summary>
    internal IReadOnlyDictionary<ushort, RealmConfig> Pending => _pending;
}
