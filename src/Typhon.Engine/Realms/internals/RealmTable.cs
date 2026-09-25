using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// One registered realm: its identity and the structures it owns. The spatial grid today; its per-archetype spatial state, policy and replication
/// state join it as the Realms plan lands (claude/design/Realms/README.md §2.1).
/// </summary>
internal sealed class Realm
{
    internal readonly RealmId Id;

    /// <summary>The realm's own grid: its bounds, cell size and depth. Never shared with another realm.</summary>
    internal readonly SpatialGrid Grid;

    internal Realm(RealmId id, SpatialGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Id = id;
        Grid = grid;
    }

    /// <summary>The grid's configuration: geometry and every per-realm spatial knob.</summary>
    internal ref readonly SpatialGridConfig Config => ref Grid.Config;
}

/// <summary>
/// The engine's realms, indexed by <see cref="RealmId"/>. Sized once to the configured realm count and never resized, so a realm's slot is a
/// plain array load on every hot path; registration is rare and off-tick.
/// </summary>
/// <remarks>
/// Readers are lock-free: a realm is published with a release store into its slot and into the dense <see cref="Registered"/> copy, so a reader that
/// sees it sees a fully built <see cref="Realm"/> (x64 and arm64). Writers serialize on a private lock.
/// </remarks>
internal sealed class RealmTable
{
    private readonly Realm[] _byId;
    private Realm[] _registered = [];
    private readonly Lock _writeLock = new();

    internal RealmTable(int maxRealms)
    {
        if (maxRealms < 1 || maxRealms > RealmId.MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRealms), maxRealms,
                $"The realm count must be between 1 and {RealmId.MaxCount} ({RealmId.NoneValue} is reserved for RealmId.None).");
        }

        _byId = new Realm[maxRealms];
    }

    /// <summary>The configured realm count: valid ids are <c>[0, MaxRealms)</c>.</summary>
    internal int MaxRealms => _byId.Length;

    /// <summary>Realm 0, or null while it is not registered.</summary>
    internal Realm Default => Volatile.Read(ref _byId[0]);

    /// <summary>The registered realms, densely, in registration order. A snapshot: registration replaces the array, never mutates it.</summary>
    internal ReadOnlySpan<Realm> Registered => Volatile.Read(ref _registered);

    /// <summary>True when <paramref name="id"/> names a registered realm.</summary>
    internal bool IsRegistered(ushort id) => id < _byId.Length && Volatile.Read(ref _byId[id]) != null;

    /// <summary>The realm <paramref name="id"/>, or null when it is out of range or not registered.</summary>
    internal Realm TryGet(ushort id) => id < _byId.Length ? Volatile.Read(ref _byId[id]) : null;

    /// <summary>The realm <paramref name="id"/>; throws when it is out of range or not registered.</summary>
    internal Realm Get(ushort id) => TryGet(id) ?? throw new InvalidOperationException(
        id < _byId.Length
            ? $"Realm {id} is not registered."
            : $"Realm {id} is out of range: this engine is configured for {_byId.Length} realm(s).");

    /// <summary>Registers realm <paramref name="id"/> over <paramref name="grid"/>. Refuses an id out of range and one already registered.</summary>
    internal Realm Register(RealmId id, SpatialGrid grid)
    {
        if (id.Value >= _byId.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id.Value,
                $"Realm {id.Value} is out of range: this engine is configured for {_byId.Length} realm(s).");
        }

        ArgumentNullException.ThrowIfNull(grid);
        Realm realm;
        lock (_writeLock)
        {
            if (_byId[id.Value] != null)
            {
                throw new InvalidOperationException($"Realm {id.Value} is already registered.");
            }

            // Before the release stores below: a reader that finds the realm finds its grid already naming it.
            grid.Realm = id;
            realm = new Realm(id, grid);

            var registered = _registered;
            var grown = new Realm[registered.Length + 1];
            registered.CopyTo(grown, 0);
            grown[^1] = realm;
            Volatile.Write(ref _byId[id.Value], realm);
            Volatile.Write(ref _registered, grown);
        }

        return realm;
    }
}
