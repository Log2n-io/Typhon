using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Per-cluster dormancy state (issue #233). Clusters transition through Active → Sleeping → WakePending → Active.
/// Sleeping clusters are skipped during dispatch at zero cost; WakePending clusters become Active at the start of the
/// next tick so woken clusters appear in the per-tier lists before any system runs.
/// </summary>
[PublicAPI]
public enum ClusterSleepState : byte
{
    /// <summary>Normal processing — the cluster is dispatched by all systems whose tier/view filters include it.</summary>
    Active = 0,

    /// <summary>Cluster is dormant — skipped by all dispatch paths. Transitions to <see cref="WakePending"/> on a wake trigger.</summary>
    Sleeping = 1,

    /// <summary>Wake requested — will become <see cref="Active"/> at the start of the next tick (one-tick latency). Set by
    /// <see cref="DormancyReporter.RequestWake"/> or the heartbeat timer inside <see cref="ArchetypeClusterState.DormancySweep"/>.</summary>
    WakePending = 2,
}
