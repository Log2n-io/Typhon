using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>
/// PRV-04 — a galaxy of thousands of interiors with a chosen number of them <b>occupied</b>: the harness whose absence stopped D-7 being measured.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it exists to vary is the ACTIVE realm count, independently of the registered one.</b> D-7 asks whether the serial share of a tick follows the
/// realms being served or the realms merely registered, and no fixture could ask that before this, because the only thing that put a session in a realm
/// was the bots' <c>ViewRealm</c> timer, which walks planets. Here N god cameras are sent into N <i>distinct</i> interiors, so <c>active = 1 + N</c>
/// (realm 0 is served always) over a registration this does not touch.
/// </para>
/// <para>
/// <b>Sessions enter by the product's own command, never by moving an entity.</b> An interior nobody is in is dormant and the fence does not index into a
/// dormant realm, so a hand-staged teleport leaves the entity in neither realm's spatial index and the crossing never completes — two of SWG-08's fixtures
/// were rewritten after exactly that. <c>ViewRealm</c> is what the product does, it is deterministic, and a session in a realm makes it Active by
/// definition (RLM-03).
/// </para>
/// <para>
/// <b>God cameras only, and no dungeons, no space.</b> The active set has to be exactly what this asked for: a player client would hand the simulation a
/// player and a possession, a dungeon pins a realm active on its own schedule, and space is one more served realm depending on whether anything launched.
/// All three would move the number being measured for reasons that have nothing to do with the measurement.
/// </para>
/// </remarks>
internal sealed class RealmScaleGalaxy : IDisposable
{
    private readonly string _dir;
    private readonly List<FakeLink> _gods = [];

    /// <summary>
    /// Builds the galaxy, connects <paramref name="occupied"/> god cameras and puts each one in an interior of its own.
    /// </summary>
    /// <remarks>
    /// <b>A factory, because the constructor cannot be allowed to throw past the point the simulation starts ticking.</b> <c>SessionHarness</c>'s constructor
    /// starts replication, and occupying the interiors then runs assertions and one ten-second wait per camera. A throw in any of those would leave
    /// <c>using var galaxy = new …</c> unbound, so <c>Dispose</c> would never run: the simulation would keep ticking into whatever fixture came next — the
    /// failure that surfaces three files away — and its multi-gigabyte world directory would never be deleted. This is the pattern <c>FrameHarness.Create</c>
    /// already uses, for the same reason.
    /// </remarks>
    /// <param name="planets">How many planets, which is what sets the registered realm count: each one registers one realm per enterable building.</param>
    /// <param name="occupied">How many interiors hold a session. Zero is legal and leaves realm 0 the only served realm.</param>
    /// <param name="population">The population scale; the default is small because this measures realms, not crowds.</param>
    /// <param name="spread">
    /// <see langword="true"/> gives every god camera an interior of its own; <see langword="false"/> puts them all in the FIRST interior, so the same
    /// number of sessions is served out of one realm. The two arms of D-7's measurement, and the synthetic bench's <c>apart</c> parameter under a name
    /// that says which way round it is.
    /// </param>
    /// <param name="phaseTiming">
    /// Turns on the engine's per-phase replication timing, without which <c>ReplicationPrologueMsTotal</c> reads zero. Off by default because it is a cost
    /// nothing but a measurement wants, and it is process-wide: <c>TatooineSim.Initialize</c> assigns it from its own config, so the next simulation built
    /// in this process resets it either way.
    /// </param>
    public static RealmScaleGalaxy Create(int planets, int occupied, float population = 0.05f, bool spread = true, bool phaseTiming = false,
        int tickRateHz = 40)
    {
        var galaxy = new RealmScaleGalaxy(planets, population, spread, phaseTiming, tickRateHz);
        try
        {
            galaxy.Occupy(occupied);
            return galaxy;
        }
        catch
        {
            galaxy.Dispose();
            throw;
        }
    }

    private RealmScaleGalaxy(int planets, float population, bool spread, bool phaseTiming, int tickRateHz)
    {
        Spread = spread;
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        var config = Worlds.Small(_dir);
        config.PopulationScale = population;
        config.Planets = planets;
        config.Interiors = true;

        // Deliberately off: each would add a served realm on its own schedule. See the remarks.
        config.Space = false;
        config.Dungeons = 0;

        // <b>Paced, and unpaced would be wrong rather than merely different.</b> A tick window has to mean the same number of ticks in every arm, and a
        // free-running loop would also outrun the keepalive the spin predicates send — the sessions would be closed for silence mid-measurement. The RATE is
        // the cheap lever: the counted checks raise it, because their quantity is per TICK and 40 Hz only makes the window take longer in wall clock.
        config.Unpaced = false;
        config.TickRateHz = tickRateHz;
        config.WorkerCount = 4;
        config.SubscriptionsPhaseTiming = phaseTiming;

        Sim = new TatooineSim(config);
        Sim.Initialize();
        Harness = new SessionHarness(Sim);

        FirstInterior = (ushort)Sim.Config.Planets;
        InteriorsPerPlanet = Sim.InteriorsPerPlanet;
    }

    /// <summary>
    /// Connects <paramref name="more"/> further god cameras and gives each one an interior nobody is in yet, raising the served realm count by exactly
    /// that many.
    /// </summary>
    /// <remarks>
    /// <b>Growing one galaxy is the point, not a convenience.</b> The proportionality claim needs several served counts compared against each other, and
    /// building a galaxy per count would compare worlds that differ in their entity layout as well as in their served set — with the map generator's own
    /// variation free to explain whatever was found. One world, one registration, sessions added: the served count is then the only thing that moved.
    /// </remarks>
    public void Occupy(int more)
    {
        if (more <= 0)
        {
            return;
        }

        var first = Occupied;
        Assert.That(InteriorsPerPlanet, Is.GreaterThan(first + more), "the world has fewer interiors than this asked to occupy");

        for (var i = 0; i < more; i++)
        {
            var god = Harness.Connect(TatooineReplication.GodKind);
            _gods.Add(god);
            SessionHarness.Until(() => god.Received(MessageTypes.Welcome), $"god camera {first + i} to be welcomed");
        }

        // One realm each, from where the last batch stopped: the ids are Planets + planet·N + portal, so a contiguous run stays inside planet 0's
        // buildings for any count this harness is used at. Asked for after every WELCOME, so no ask is dropped for arriving before its session had a
        // profile.
        for (var i = 0; i < more; i++)
        {
            var realm = (uint)(FirstInterior + (Spread ? first + i : 0));
            _gods[first + i].SendCommand(nameof(ViewRealm), new RecordValues { ["realm"] = FieldValue.Of(realm) });
        }

        // <b>Waited on the realm table, not on ReadStats.</b> A snapshot allocates a row per registered realm — up to 2 500 here — and a spin predicate
        // evaluates hundreds of times, so the wait would allocate more than the thing it waits for. A realm with a session in it is Active by definition.
        for (var i = 0; i < more; i++)
        {
            var realm = new RealmId((ushort)(FirstInterior + (Spread ? first + i : 0)));
            SessionHarness.Until(
                () => Sim.Dbe.Realms.StateOf(realm) == RealmRunState.Active,
                $"interior {realm.Value} to be entered by its god camera");
        }

        Occupied = first + more;

        // <b>A realm is Active as soon as the FIRST session arrives, so the shared arm needs the session COUNT as well.</b> Waiting on the run state alone
        // would let the unspread arm start measuring with one camera in the room and the rest still in realm 0 — the two arms would then differ in how
        // their sessions are distributed as well as in how many realms are served, which is the one difference this must not introduce.
        //
        // <b>A bounded retry, not a spin and not a single bet.</b> The only reading of "sessions served out of realm r" is the stats snapshot, which allocates
        // a row per registered realm — up to 2 500 here — so a spin predicate would allocate far more than the thing it waits for. Ten batches of twenty ticks
        // is five seconds at the slowest rate this harness runs, against a command applied within two ticks, and it costs at most ten snapshots. A single
        // twenty-tick bet would have turned a slow runner into a hard failure rather than a wait.
        for (var attempt = 0; attempt < 10 && SessionsInInteriors() != Occupied; attempt++)
        {
            Harness.Ticks(20);
        }

        Assert.That(SessionsInInteriors(), Is.EqualTo(Occupied), "every god camera should be served out of an interior by now");
    }

    /// <summary>Whether each god camera gets an interior of its own (the <c>apart</c> arm) or they all share the first one.</summary>
    public bool Spread { get; }

    public TatooineSim Sim { get; }

    public SessionHarness Harness { get; }

    /// <summary>The realm id of planet 0's first interior: interiors are <c>[Planets, Planets + Planets·InteriorsPerPlanet)</c>.</summary>
    public ushort FirstInterior { get; }

    public int InteriorsPerPlanet { get; }

    /// <summary>How many interiors hold a session now. Realm 0 is served besides these, so the served count is one more.</summary>
    public int Occupied { get; private set; }

    /// <summary>
    /// Realms this engine has registered: planets plus one per enterable building per planet.
    /// </summary>
    /// <remarks>
    /// A method, not a property, because it builds a whole stats snapshot — a row per registered realm — and a caller reading it twice in one expression should
    /// see that it is doing so. Read from the engine rather than recomputed, so a change to the demo's realm layout cannot make it quietly disagree.
    /// </remarks>
    public int ReadRegisteredRealms() => Sim.Runtime.ReadStats().Realms.Length;

    /// <summary>
    /// A reading of the two cumulative realm counters and the tick they were read at: the unit both counted checks and the D-7 bench difference.
    /// </summary>
    public readonly record struct Reading(long Tick, long PassSteps, long PolicyEvaluations, int Served)
    {
        /// <summary>Serial per-realm passes per tick between two readings; <c>double.NaN</c> when no tick separates them.</summary>
        public double PassStepsPerTickSince(in Reading earlier)
        {
            var ticks = Tick - earlier.Tick;
            return ticks <= 0 ? double.NaN : (double)(PassSteps - earlier.PassSteps) / ticks;
        }

        /// <summary>Policy evaluations per tick between two readings.</summary>
        public double EvaluationsPerTickSince(in Reading earlier)
        {
            var ticks = Tick - earlier.Tick;
            return ticks <= 0 ? double.NaN : (double)(PolicyEvaluations - earlier.PolicyEvaluations) / ticks;
        }
    }

    /// <summary>
    /// One reading of both counters, from <b>one</b> snapshot.
    /// </summary>
    /// <remarks>
    /// One call, because the tick and the counters have to come from the same instant to be differenced against each other. Two calls would let the tick
    /// advance between them and turn a rate into an estimate — which is the whole quantity here.
    /// </remarks>
    public Reading Read()
    {
        var stats = Sim.Runtime.ReadStats();
        var served = 0;
        foreach (var realm in stats.Realms)
        {
            served += realm.Served ? 1 : 0;
        }

        return new Reading(stats.Tick, stats.RealmPassSteps, stats.RealmPolicyEvaluations, served);
    }

    /// <summary>Sessions served out of an interior realm, over every interior: what <see cref="Occupied"/> should equal once the cameras have
    /// arrived.</summary>
    private int SessionsInInteriors()
    {
        var last = FirstInterior + (Sim.Config.Planets * InteriorsPerPlanet);
        var sessions = 0;
        foreach (var row in Sim.Runtime.ReadStats().Realms)
        {
            if (row.Realm >= FirstInterior && row.Realm < last)
            {
                sessions += row.Sessions;
            }
        }

        return sessions;
    }

    /// <summary>
    /// The frame stage's single-threaded prologue so far: total milliseconds and the ticks it accrued over. Both zero unless this was built with phase timing.
    /// </summary>
    /// <remarks>A method for the same reason as <see cref="ReadRegisteredRealms"/>: one call is one stats snapshot.</remarks>
    public (double Ms, long Ticks) ReadPrologue()
    {
        var stats = Sim.Runtime.ReadStats();
        return (stats.ReplicationPrologueMsTotal, stats.ReplicationPrologueTicks);
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> ticks, in batches.
    /// </summary>
    /// <remarks>
    /// <b>Batched because <c>SessionHarness.Ticks</c> gives up after ten seconds</b>, and this fixture is paced: 400 ticks at 40 Hz is exactly ten seconds
    /// of wall clock, so a single call for a window that long fails as "the runtime stopped ticking" while the runtime is ticking perfectly. Forty ticks is
    /// one second at the slowest rate this harness uses — a tenfold margin, so a runner modulating to a quarter of target still finishes the batch rather than
    /// reporting a stalled runtime.
    /// </remarks>
    public void Run(int ticks)
    {
        const int Batch = 40;
        for (var done = 0; done < ticks; done += Batch)
        {
            Harness.Ticks(Math.Min(Batch, ticks - done));
        }
    }

    /// <summary>Reads, runs <paramref name="ticks"/> ticks, reads again, and returns the two readings.</summary>
    public (Reading Before, Reading After) Over(int ticks)
    {
        var before = Read();
        Run(ticks);
        return (before, Read());
    }

    public void Dispose()
    {
        Harness?.Release();
        Sim?.Dispose();
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        Worlds.Delete(_dir);
    }
}
