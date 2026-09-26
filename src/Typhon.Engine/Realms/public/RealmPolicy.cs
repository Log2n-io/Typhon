using System;
using System.Threading;
using JetBrains.Annotations;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>What a realm is doing this tick, as its policy decided at tick start (RLM-03).</summary>
[PublicAPI]
public enum RealmRunState : byte
{
    /// <summary>A <see cref="RealmUnobserved.Sleep"/> realm unobserved for longer than its <see cref="RealmConfig.SleepAfterTicks"/>: its clusters are
    /// dispatched to no system and cost no elective maintenance. Mandatory work — a write to one of its entities — still happens.</summary>
    Dormant = 0,

    /// <summary>Unobserved but simulated, at <see cref="RealmConfig.UnobservedTickDivisor"/> of the rate.</summary>
    Simulated = 1,

    /// <summary>Observed by a session or pinned by the application: simulated at full rate.</summary>
    Active = 2,

    /// <summary>Unregistered and waiting to be empty (run-time registration).</summary>
    Closing = 3,
}

/// <summary>How a QuerySystem runs over a realm simulated at a divisor (<see cref="RealmConfig.UnobservedTickDivisor"/>).</summary>
[PublicAPI]
public enum RealmRate : byte
{
    /// <summary>Each cluster of an N-divided realm once every N runs of the system, spread over the N runs; the system integrates with
    /// <c>ctx.Realms.DeltaTime(realm)</c>. The default.</summary>
    Divided = 0,

    /// <summary>Every run, in every runnable realm, whatever its divisor — for a system that must see every cluster each tick. Dormant realms are still
    /// excluded.</summary>
    Full = 1,
}

/// <summary>
/// What a system reads of the realm policy this tick (<see cref="TickContext.Realms"/>): each realm's state, rate divisor and the delta time a cluster
/// of it integrates over.
/// </summary>
[PublicAPI]
public readonly struct RealmsAccessor
{
    private readonly RealmTable _table;
    private readonly float _amortizedDeltaTime;
    private readonly bool _divided;

    internal RealmsAccessor(RealmTable table, float amortizedDeltaTime, bool divided)
    {
        _table = table;
        _amortizedDeltaTime = amortizedDeltaTime;
        _divided = divided;
    }

    /// <summary>
    /// The time a cluster of <paramref name="realm"/> integrates over this run: <see cref="TickContext.AmortizedDeltaTime"/> × the realm's divisor for a
    /// <see cref="RealmRate.Divided"/> system (it saw the cluster once in that many runs), the amortized delta time for a <see cref="RealmRate.Full"/> one.
    /// </summary>
    public float DeltaTime(RealmId realm) => _divided && _table != null ? _amortizedDeltaTime * _table.DivisorOf(realm.Value) : _amortizedDeltaTime;

    /// <summary>The realm's rate divisor this tick: 1 when observed, its <see cref="RealmConfig.UnobservedTickDivisor"/> otherwise.</summary>
    public int DivisorOf(RealmId realm) => _table?.DivisorOf(realm.Value) ?? 1;

    /// <summary>True when <paramref name="realm"/>'s clusters are dispatched this tick.</summary>
    public bool IsRunnable(RealmId realm) => _table == null || _table.IsRunnable(realm.Value);

    /// <summary>What <paramref name="realm"/> is doing this tick. Refuses an unregistered id.</summary>
    public RealmRunState StateOf(RealmId realm) => _table?.StateOf(realm.Value) ?? RealmRunState.Active;
}

/// <summary>
/// An application pin on a realm (<see cref="RealmRegistry.Observe"/>): while held, the realm is <see cref="RealmRunState.Active"/> from the next tick on
/// — what a headless server, a benchmark or a test uses where no session observes. Dispose releases it; disposing twice releases once.
/// </summary>
[PublicAPI]
public sealed class RealmObserver : IDisposable
{
    private readonly RealmTable _table;
    private int _released;

    internal RealmObserver(RealmTable table, RealmId realm)
    {
        _table = table;
        Realm = realm;
        table.AddObserver(realm.Value);
    }

    /// <summary>The realm this pin holds active.</summary>
    public RealmId Realm { get; }

    /// <summary>Releases the pin; the realm follows its unobserved policy from the next tick on.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _table.RemoveObserver(Realm.Value);
        }
    }
}
