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
