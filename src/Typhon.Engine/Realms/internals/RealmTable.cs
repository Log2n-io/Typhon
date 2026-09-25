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

    /// <summary>The registered configuration: the grid's identity and the policy when unobserved. Null only for a table built in a unit test.</summary>
    internal readonly RealmConfig Config;

    internal Realm(RealmId id, SpatialGrid grid, RealmConfig config)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Id = id;
        Grid = grid;
        Config = config;
    }

    /// <summary>The grid's configuration: geometry and every per-realm spatial knob.</summary>
    internal ref readonly SpatialGridConfig GridConfig => ref Grid.Config;

    // Realm-keyed archetypes whose spatial field cannot address this realm's world (#919 AC-9 per realm), one bit per archetype id. Written at open,
    // read-only afterwards; an entity of such an archetype is refused entry here (spawn, teleport) with the reason recorded below.
    private ulong[] _incompatible = [];
    private readonly System.Collections.Generic.Dictionary<int, string> _incompatibleReason = [];

    /// <summary>Records that <paramref name="archetypeId"/> cannot live in this realm, and why. Open time only.</summary>
    internal void MarkIncompatible(int archetypeId, string reason)
    {
        var word = archetypeId >> 6;
        if (word >= _incompatible.Length)
        {
            Array.Resize(ref _incompatible, word + 1);
        }

        _incompatible[word] |= 1UL << (archetypeId & 63);
        _incompatibleReason[archetypeId] = reason;
    }

    /// <summary>True when an entity of <paramref name="archetypeId"/> may enter this realm.</summary>
    internal bool IsCompatible(int archetypeId)
    {
        var word = archetypeId >> 6;
        return word >= _incompatible.Length || (_incompatible[word] & (1UL << (archetypeId & 63))) == 0;
    }

    /// <summary>Why <paramref name="archetypeId"/> cannot enter this realm (the extent check's message), or null.</summary>
    internal string IncompatibilityOf(int archetypeId) => _incompatibleReason.TryGetValue(archetypeId, out var reason) ? reason : null;
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
    // Dense, in registration order, grown by doubling (never copied per registration: thousands of realms would make that quadratic). A reader takes the
    // count first and the array second; the writer publishes the array first and the count second, so the array is never older than the count.
    private Realm[] _registered = new Realm[4];
    private int _registeredCount;
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

    /// <summary>The registered realms, densely, in registration order — a consistent snapshot: entries are only ever appended.</summary>
    internal ReadOnlySpan<Realm> Registered
    {
        get
        {
            var count = Volatile.Read(ref _registeredCount);
            return Volatile.Read(ref _registered).AsSpan(0, count);
        }
    }

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
    internal Realm Register(RealmId id, SpatialGrid grid, RealmConfig config = null)
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

            // Before the release stores below: a reader that finds the realm finds its grid already naming it. Refused, and nothing mutated, when the
            // grid already belongs to a realm.
            grid.BindToRealm(id);
            realm = new Realm(id, grid, config);

            var registered = _registered;
            if (_registeredCount == registered.Length)
            {
                var grown = new Realm[registered.Length * 2];
                registered.CopyTo(grown, 0);
                registered = grown;
            }

            registered[_registeredCount] = realm;
            Volatile.Write(ref _byId[id.Value], realm);
            Volatile.Write(ref _registered, registered);
            Volatile.Write(ref _registeredCount, _registeredCount + 1);
        }

        return realm;
    }
}
