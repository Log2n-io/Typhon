namespace SwgTatooine;

/// <summary>
/// The DAG-local phases the simulation runs in. The phase list is the causal order, but it binds only through the access
/// declarations and explicit edges: a system in phase N+1 waits for a phase-N system when a declared conflict or an explicit
/// edge connects them, directly or through other systems, and may run alongside it otherwise. So each system declares every
/// placement its spatial queries read — that, not the phase, is what makes Awareness wait for the movers.
/// </summary>
/// <remarks>
/// <para>The order is the causal one a server actually needs: decide, then move, then observe what moved, then resolve
/// what the observation implies, then let the slow economy tick.</para>
/// <para><b>Awareness is its own phase and comes after movement.</b> That is the whole reason the phase list is not
/// flatter. Interest management is the query that defines the load — it must see the positions this tick produced, not
/// last tick's, or the simulation is answering a question about a world that no longer exists.</para>
/// </remarks>
public static class SimPhases
{
    /// <summary>
    /// What the outside world asked for: sessions opening and closing, possession, and this tick's client intents (SWG-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own phase, ahead of everything, rather than sharing <see cref="Spawn"/>.</b> Applying an intent writes <c>PlayerState</c> and
    /// <c>PlayerMotion</c>, which <c>Shuttle</c> also writes in <see cref="Spawn"/> — and the access deriver rightly refuses two writers of one component in
    /// one phase without an explicit edge. An edge would do, but it would have to name a system that is only in the schedule when <c>--no-shuttles</c> is
    /// absent, and it would say nothing about why. A phase says it: the client's intent for this tick is settled before the simulation looks at anything.
    /// </para>
    /// <para>Empty in a measurement run, which has no sessions, and an empty phase costs nothing.</para>
    /// </remarks>
    public static readonly Phase Input = new("Input");

    /// <summary>
    /// Spawning and despawning: lairs replacing killed creatures, mission terminals issuing destroy missions, camps
    /// being created and torn down. Runs first because everything downstream should see this tick's population.
    /// </summary>
    public static readonly Phase Spawn = new("Spawn");

    /// <summary>
    /// Behaviour decisions. Creature AI picks a mode and a destination; players pick an activity. Writes velocity and
    /// AI state, never position — which is what lets it run in parallel with nothing else needing to know where anything
    /// ended up.
    /// </summary>
    public static readonly Phase Think = new("Think");

    /// <summary>
    /// Integration: where creatures, NPCs and players move, and so the phase the spatial fence cares about most (Spawn
    /// also writes placements: shuttle arrivals and mission teleports). Separated from <see cref="Think"/> so the decision
    /// writers and the position writers do not collide on a component and get serialised for it.
    /// </summary>
    public static readonly Phase Move = new("Move");

    /// <summary>
    /// Interest management: for every player, everything within the awareness radius. This is the query the whole study
    /// is about, and it runs against positions this tick's <see cref="Move"/> just wrote.
    /// </summary>
    public static readonly Phase Awareness = new("Awareness");

    /// <summary>
    /// Who fires at whom. Every system here walks its own archetype in parallel, decides what happens to somebody else's
    /// entity, and pushes it as a <c>CombatEvent</c> — writing nothing outside its own archetype.
    /// </summary>
    /// <remarks>
    /// The two producers share no component in either direction, which is why they run concurrently: the player's side
    /// writes the shooter's cooldown and target, the creature's side writes the attacker's cooldown and mode, and each
    /// only READS the other archetype's vitals and placement.
    /// </remarks>
    public static readonly Phase Resolve = new("Resolve");

    /// <summary>
    /// The consequences: damage, death, loot, mission payouts, cloning. One serial system, and the only phase that opens
    /// a real transaction against a <see cref="StorageMode.Versioned"/> component — so the only one that puts anything in
    /// the WAL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own phase rather than an explicit edge inside <see cref="Resolve"/>, and the access deriver is what settles
    /// it.</b> The consumer writes the very components the two producers read — a creature's vitals, a lair's vitals, a
    /// player's placement — and a plain <c>Reads&lt;T&gt;</c> facing a same-phase writer is a <c>Build()</c> error by
    /// design (rule ED-05). The only in-phase resolutions offered are <c>ReadsFresh</c>, which would order all three
    /// producers AFTER the consumer that depends on them, and <c>ReadsSnapshot</c>, which needs a Versioned component.
    /// Either way the graph is a cycle. A phase boundary is the disambiguator the deriver accepts, and it also says the
    /// true thing: this tick's exchanges are all decided before any of them lands.
    /// </para>
    /// <para>Same reasoning as <see cref="Input"/>, arrived at the same way — by the deriver refusing the alternative.</para>
    /// </remarks>
    public static readonly Phase Apply = new("Apply");

    /// <summary>
    /// The economy: harvesters extracting, factories manufacturing, structures ageing. Ticks on periods of seconds to
    /// minutes rather than every tick, and exists to give the world a large population that is neither inert nor hot.
    /// </summary>
    public static readonly Phase Economy = new("Economy");

    /// <summary>Telemetry collection. Reads everything, writes nothing the simulation depends on.</summary>
    public static readonly Phase Report = new("Report");
}
