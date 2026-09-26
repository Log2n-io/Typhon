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

    /// <summary>Unregistered and waiting to be empty (Realms D5): entries refused, removed at the first fence that finds it holding no cluster.</summary>
    internal volatile bool Closing;

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
    private System.Collections.Generic.Dictionary<int, string> _incompatibleReason;

    /// <summary>Records that <paramref name="archetypeId"/> cannot live in this realm, and why. Open time only.</summary>
    internal void MarkIncompatible(int archetypeId, string reason)
    {
        var word = archetypeId >> 6;
        if (word >= _incompatible.Length)
        {
            Array.Resize(ref _incompatible, word + 1);
        }

        _incompatible[word] |= 1UL << (archetypeId & 63);
        (_incompatibleReason ??= [])[archetypeId] = reason;
    }

    /// <summary>True when an entity of <paramref name="archetypeId"/> may enter this realm.</summary>
    internal bool IsCompatible(int archetypeId)
    {
        var word = archetypeId >> 6;
        return word >= _incompatible.Length || (_incompatible[word] & (1UL << (archetypeId & 63))) == 0;
    }

    /// <summary>Why <paramref name="archetypeId"/> cannot enter this realm (the extent check's message), or null.</summary>
    internal string IncompatibilityOf(int archetypeId) =>
        _incompatibleReason != null && _incompatibleReason.TryGetValue(archetypeId, out var reason) ? reason : null;
}

/// <summary>
/// The engine's realms, indexed by <see cref="RealmId"/>. Sized once to the configured realm count and never resized, so a realm's slot is a
/// plain array load on every hot path; registration is rare and off-tick.
/// </summary>
/// <remarks>
/// Readers are lock-free: a realm is published with a release store into its slot and into the dense <see cref="Registered"/> snapshot, so a reader that
/// sees it sees a fully built <see cref="Realm"/> (x64 and arm64). Writers serialize on a private lock.
/// </remarks>
internal sealed class RealmTable
{
    private readonly Realm[] _byId;

    /// <summary>One immutable view of the dense list: the array and how many of its entries are live, published as ONE reference so a reader can never
    /// pair one publication's count with another's array (a removal compacts the list — review #4).</summary>
    private sealed class Snapshot
    {
        internal readonly Realm[] Items;
        internal readonly int Count;

        internal Snapshot(Realm[] items, int count)
        {
            Items = items;
            Count = count;
        }
    }

    // Dense, in registration order. An append writes into spare capacity (grown by doubling, so thousands of realms stay linear) and publishes a new
    // snapshot over the same array; a removal publishes a compacted copy. Entries below a published count are never written again.
    private Snapshot _registered = new(new Realm[4], 0);
    private readonly Lock _writeLock = new();

    // The primary realm, fixed at open (review #4): a run-time registration of a lower id must not move the archetype-level configuration mid-run.
    private Realm _pinnedPrimary;

    internal RealmTable(int maxRealms)
    {
        if (maxRealms < 1 || maxRealms > RealmId.MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRealms), maxRealms,
                $"The realm count must be between 1 and {RealmId.MaxCount} ({RealmId.NoneValue} is reserved for RealmId.None).");
        }

        _byId = new Realm[maxRealms];
        _state = new RealmRunState[maxRealms];
        _divisor = new ushort[maxRealms];
        _observers = new int[maxRealms];
        _unobservedTicks = new int[maxRealms];
        _wakeRequested = new byte[maxRealms];
    }

    // ── Policy (Realms D1, RT-3) ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Structure of arrays indexed by realm id, sized once with the table. Written by EvaluatePolicy at tick start, single-threaded, before any dispatch
    // (RLM-03); read by dispatch and the fence for the rest of the tick. Observers and wake requests arrive from any thread and are only READ by the
    // evaluation, so a change lands at the next tick start.
    private readonly RealmRunState[] _state;
    private readonly ushort[] _divisor;
    private readonly int[] _observers;
    private readonly int[] _unobservedTicks;
    private readonly byte[] _wakeRequested;

    /// <summary>Moves whenever any realm's state or divisor changes. Written only by <see cref="EvaluatePolicy"/> and <see cref="Remove"/>, both on the
    /// tick thread.</summary>
    internal int PolicyEpoch { get; private set; }

    /// <summary>Moves only when some realm's RUNNABILITY changes (becomes or stops being Dormant) — what the runnable indexes depend on. An Active ↔
    /// Simulated or divisor-only flip leaves it, so observer churn rebuilds nothing (review #4).</summary>
    internal int RunnableEpoch { get; private set; }

    /// <summary>How many realms are registered now: 1 on a single-realm engine, where nothing needs a realm filter.</summary>
    internal int RegisteredCount => Volatile.Read(ref _registered).Count;

    /// <summary>Registered realms that are not runnable this tick (<see cref="RealmRunState.Dormant"/>). Zero ⇒ nothing is filtered anywhere (RLM-04).</summary>
    internal int NonRunnableCount { get; private set; }

    private int _closingCount;

    /// <summary>Registered realms that are Closing (Realms D5): the fence checks them for removal only while this is non-zero.</summary>
    internal int ClosingCount => Volatile.Read(ref _closingCount);

    /// <summary>
    /// Marks realm <paramref name="id"/> Closing: entries refused from now on (the flag, read by every entry check), removal once empty. Idempotent, any
    /// thread. The policy STATE follows at the next evaluation, never mid-tick (RLM-03): every system of the tick keeps the state and divisor tick start
    /// decided.
    /// </summary>
    internal void MarkClosing(ushort id)
    {
        var realm = Get(id);
        lock (_writeLock)
        {
            if (realm.Closing)
            {
                return;
            }

            realm.Closing = true;
            Interlocked.Increment(ref _closingCount);
        }
    }

    /// <summary>
    /// Removes realm <paramref name="id"/> from the table (Realms D5). The caller has dropped every archetype's state for it. The dense list is rebuilt
    /// copy-on-write: a reader's snapshot never holds a null (the count shrinks first, so a reader pairing the new count with the old array misses at
    /// most the last entry for that one read).
    /// </summary>
    internal void Remove(ushort id)
    {
        var realm = Get(id);
        lock (_writeLock)
        {
            var old = _registered;
            var compacted = new Realm[Math.Max(4, old.Items.Length)];
            var n = 0;
            for (var i = 0; i < old.Count; i++)
            {
                if (!ReferenceEquals(old.Items[i], realm))
                {
                    compacted[n++] = old.Items[i];
                }
            }

            if (realm.Closing)
            {
                Interlocked.Decrement(ref _closingCount);
            }

            Volatile.Write(ref _byId[id], null);
            Volatile.Write(ref _registered, new Snapshot(compacted, n));

            // The observer count is left as it is: a pin taken before the removal still releases once, and the id is quarantined for the session
            // (RLM-06), so nothing else reads it (review #4: zeroing it made that release throw).
            _state[id] = RealmRunState.Dormant;
            _unobservedTicks[id] = 0;
            _wakeRequested[id] = 0;
            PolicyEpoch++;
            RunnableEpoch++;
        }
    }

    /// <summary>Runnable realms simulated at a divisor above 1 this tick. Zero ⇒ no system strides (RLM-05).</summary>
    internal int DividedCount { get; private set; }

    /// <summary>
    /// Realm <paramref name="id"/>'s offset in its divisor's rotation, so realms of one divisor do not all run the same bucket on the same run. A hash,
    /// fixed per realm: the stride is keyed on (system run count + this + chunk id), RLM-05.
    /// </summary>
    internal static int PhaseOf(ushort id) => (int)((id * 0x9E3779B1u) >> 16);

    /// <summary>Evaluations run — tests read it to prove the policy runs once per tick.</summary>
    internal long EvaluationCount { get; private set; }

    /// <summary>The realm's state this tick. Refuses an id that is not registered.</summary>
    internal RealmRunState StateOf(ushort id)
    {
        _ = Get(id);
        return _state[id];
    }

    /// <summary>True when <paramref name="id"/>'s clusters are dispatched this tick. Hot path: one byte load, no registration check.</summary>
    internal bool IsRunnable(ushort id) => _state[id] != RealmRunState.Dormant;

    /// <summary>The realm's rate divisor this tick: 1 when observed, its <see cref="RealmConfig.UnobservedTickDivisor"/> otherwise.</summary>
    internal int DivisorOf(ushort id) => _divisor[id];

    /// <summary>A session or an application pin starts observing <paramref name="id"/>. Any thread; takes effect at the next tick start.</summary>
    internal void AddObserver(ushort id)
    {
        _ = Get(id);
        Interlocked.Increment(ref _observers[id]);
    }

    /// <summary>An observer of <paramref name="id"/> leaves. Any thread; takes effect at the next tick start.</summary>
    internal void RemoveObserver(ushort id)
    {
        if (Interlocked.Decrement(ref _observers[id]) < 0)
        {
            Interlocked.Increment(ref _observers[id]);
            throw new InvalidOperationException($"Realm {id}: an observer was removed that was never added.");
        }
    }

    /// <summary>Fixes the primary realm for the rest of the session (end of open). Idempotent.</summary>
    internal void PinPrimary() => _pinnedPrimary ??= ComputePrimary();

    /// <summary>Requests that <paramref name="id"/> be simulated from the next tick on, restarting its sleep hold. Any thread.</summary>
    internal void RequestWake(ushort id) => Volatile.Write(ref _wakeRequested[id], 1);

    /// <summary>
    /// Decides every registered realm's state and divisor for the tick about to run. Tick start only, single-threaded, before any dispatch (RLM-03).
    /// </summary>
    /// <remarks>
    /// Observed ⇒ <see cref="RealmRunState.Active"/> at divisor 1. Unobserved: a wake request restarts the hold; a <see cref="RealmUnobserved.Sleep"/>
    /// realm unobserved for more than <see cref="RealmConfig.SleepAfterTicks"/> ticks is <see cref="RealmRunState.Dormant"/>; anything else is
    /// <see cref="RealmRunState.Simulated"/> at its <see cref="RealmConfig.UnobservedTickDivisor"/>. O(registered realms), no allocation.
    /// </remarks>
    internal void EvaluatePolicy()
    {
        EvaluationCount++;
        var changed = false;
        var nonRunnable = 0;
        var divided = 0;
        foreach (var realm in Registered)
        {
            var id = realm.Id.Value;
            var config = realm.Config;
            RealmRunState next;
            ushort divisor;
            if (realm.Closing)
            {
                // Closing is final until removal: runnable at full rate, for the entities still in it.
                next = RealmRunState.Closing;
                divisor = 1;
            }
            else if (Volatile.Read(ref _observers[id]) > 0)
            {
                next = RealmRunState.Active;
                divisor = 1;
                _unobservedTicks[id] = 0;
                if (_wakeRequested[id] != 0)
                {
                    Volatile.Write(ref _wakeRequested[id], 0);
                }
            }
            else
            {
                if (Volatile.Read(ref _wakeRequested[id]) != 0)
                {
                    Volatile.Write(ref _wakeRequested[id], 0);
                    _unobservedTicks[id] = 0;
                }
                else if (_unobservedTicks[id] < int.MaxValue)
                {
                    _unobservedTicks[id]++;
                }

                if (config == null)
                {
                    next = RealmRunState.Simulated;
                    divisor = 1;
                }
                else if (config.WhenUnobserved == RealmUnobserved.Sleep && _unobservedTicks[id] > config.SleepAfterTicks)
                {
                    next = RealmRunState.Dormant;
                    divisor = 1;
                }
                else
                {
                    next = RealmRunState.Simulated;
                    divisor = (ushort)Math.Min(config.UnobservedTickDivisor, ushort.MaxValue);
                }
            }

            if (next != _state[id] || divisor != _divisor[id])
            {
                if ((next == RealmRunState.Dormant) != (_state[id] == RealmRunState.Dormant))
                {
                    RunnableEpoch++;
                }

                _state[id] = next;
                _divisor[id] = divisor;
                changed = true;
            }

            nonRunnable += next == RealmRunState.Dormant ? 1 : 0;
            divided += next != RealmRunState.Dormant && divisor > 1 ? 1 : 0;
        }

        NonRunnableCount = nonRunnable;
        DividedCount = divided;
        if (changed)
        {
            PolicyEpoch++;
        }
    }

    /// <summary>Bumped by every registered grid whenever a cell's tier changes — the tier index's engine-wide staleness stamp.</summary>
    internal readonly TierVersionCounter TierVersionCounter = new();

    /// <summary>The engine-wide tier version: moves whenever any realm's cell tier moves.</summary>
    internal int TierVersion => Volatile.Read(ref TierVersionCounter.Value);

    /// <summary>The configured realm count: valid ids are <c>[0, MaxRealms)</c>.</summary>
    internal int MaxRealms => _byId.Length;

    /// <summary>Realm 0, or null while it is not registered.</summary>
    internal Realm Default => Volatile.Read(ref _byId[0]);

    /// <summary>
    /// The primary realm: realm 0 when registered, otherwise the first registered one. Its grid's configuration carries the engine's ARCHETYPE-level
    /// spatial knobs — maintenance budgets, the migration cost model, repair — which are per archetype, not per realm, until per-realm maintenance
    /// scheduling lands (Realms D). Null when no realm is registered.
    /// </summary>
    internal Realm Primary => _pinnedPrimary ?? ComputePrimary();

    private Realm ComputePrimary()
    {
        var realm0 = Default;
        if (realm0 != null)
        {
            return realm0;
        }

        // The LOWEST registered id, not the first registered: registration order comes from a dictionary, and the primary realm supplies the
        // archetype-level budgets — it must not depend on enumeration order.
        Realm lowest = null;
        foreach (var realm in Registered)
        {
            if (lowest == null || realm.Id.Value < lowest.Id.Value)
            {
                lowest = realm;
            }
        }

        return lowest;
    }

    /// <summary>The highest registered realm id (0 when none): per-archetype realm tables are sized to it, not to <see cref="MaxRealms"/>.</summary>
    internal int HighestRegisteredId
    {
        get
        {
            var highest = 0;
            foreach (var realm in Registered)
            {
                highest = Math.Max(highest, realm.Id.Value);
            }

            return highest;
        }
    }

    /// <summary>The registered realms, densely, in registration order — one consistent snapshot (a single reference: count and array together).</summary>
    internal ReadOnlySpan<Realm> Registered
    {
        get
        {
            var snapshot = Volatile.Read(ref _registered);
            return snapshot.Items.AsSpan(0, snapshot.Count);
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
    /// <remarks><c>beforePublish</c> runs on the complete realm before any reader can find it — a run-time registration marks its incompatible archetypes
    /// there, so no entity enters before the marks exist.</remarks>
    internal Realm Register(RealmId id, SpatialGrid grid, RealmConfig config = null, Action<Realm> beforePublish = null)
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
            grid.BindToRealm(id, TierVersionCounter);
            realm = new Realm(id, grid, config);
            beforePublish?.Invoke(realm);

            var current = _registered;
            var items = current.Items;
            if (current.Count == items.Length)
            {
                var grown = new Realm[items.Length * 2];
                items.CopyTo(grown, 0);
                items = grown;
            }

            items[current.Count] = realm;

            // Simulated at full rate until the first evaluation decides otherwise: a Sleep realm holds SleepAfterTicks before it goes dormant.
            _state[id.Value] = RealmRunState.Simulated;
            _divisor[id.Value] = 1;
            _unobservedTicks[id.Value] = 0;
            Volatile.Write(ref _byId[id.Value], realm);
            Volatile.Write(ref _registered, new Snapshot(items, current.Count + 1));
        }

        return realm;
    }
}

/// <summary>A shared, bump-only tier version (Realms C1): every grid of one <see cref="RealmTable"/> increments it with its own.</summary>
internal sealed class TierVersionCounter
{
    internal int Value;
}
