namespace SwgTatooine;

/// <summary>
/// The DAG-local phases the simulation runs in. Every system in phase N completes before any system in phase N+1, so
/// the phase list is the coarse ordering and the per-system access declarations refine it inside a phase.
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
    /// Integration. The one phase that writes <see cref="WorldBounds"/>, and therefore the one the spatial fence cares
    /// about. Separated from <see cref="Think"/> so the decision writers and the position writer do not collide on a
    /// component and get serialised for it.
    /// </summary>
    public static readonly Phase Move = new("Move");

    /// <summary>
    /// Interest management: for every player, everything within the awareness radius. This is the query the whole study
    /// is about, and it runs against positions this tick's <see cref="Move"/> just wrote.
    /// </summary>
    public static readonly Phase Awareness = new("Awareness");

    /// <summary>
    /// Combat resolution and its consequences: damage, death, loot. The only phase that opens a real transaction against
    /// a <see cref="StorageMode.Versioned"/> component, and so the only one that puts anything in the WAL.
    /// </summary>
    public static readonly Phase Resolve = new("Resolve");

    /// <summary>
    /// The economy: harvesters extracting, factories manufacturing, structures ageing. Ticks on periods of seconds to
    /// minutes rather than every tick, and exists to give the world a large population that is neither inert nor hot.
    /// </summary>
    public static readonly Phase Economy = new("Economy");

    /// <summary>Telemetry collection. Reads everything, writes nothing the simulation depends on.</summary>
    public static readonly Phase Report = new("Report");
}
