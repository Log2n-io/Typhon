using Typhon.Engine;

namespace SimpleSpaceBattle;

/// <summary>
/// The four DAG-local phases. Phases form a total order — every system in phase N completes before any system in
/// phase N+1 — and that ordering is the *only* synchronisation this simulation uses.
///
/// <para>The order is forced by the access matrix (DESIGN.md §5.1), not chosen:</para>
/// <list type="number">
///   <item><b>Acquire &lt; Fire</b> — a ship can only ask "does E shoot me?" once every E has published its choice.
///         The target lane is written in Acquire and read cross-entity in Fire; never both at once.</item>
///   <item><b>Fire &lt; Move</b> — both Acquire and Fire read neighbours' <c>Hull</c>; Move is its only writer.
///         Merging them would mean one worker writing a 24-byte AABB while another reads it, and a torn position
///         yields a garbage distance and therefore garbage combat.</item>
///   <item><b>Reap last</b> — CLUSTERWALK-01 forbids a cluster walk concurrent with Destroy+Commit on the same
///         archetype, so destruction is deferred out of the parallel phases entirely.</item>
/// </list>
///
/// <para>Within each phase every lane has exactly one writer, so there are no intra-phase edges at all: each
/// parallel system is alone in its phase and occupies every worker for its whole duration.</para>
/// </summary>
public static class BattlePhases
{
    /// <summary>Ships with no lock find one, and every ship publishes its target to the lane.</summary>
    public static readonly Phase Acquire = new("Acquire");

    /// <summary>The bulk of the tick: one neighbour scan yielding incoming damage, pursuit steering and lock validity.</summary>
    public static readonly Phase Fire = new("Fire");

    /// <summary>Integrate velocity and reflect off the world walls. The only writer of <c>Hull</c>.</summary>
    public static readonly Phase Move = new("Move");

    /// <summary>Sequential tail: destroy the dead, update counters, report. Cost is O(deaths), not O(N).</summary>
    public static readonly Phase Reap = new("Reap");
}
