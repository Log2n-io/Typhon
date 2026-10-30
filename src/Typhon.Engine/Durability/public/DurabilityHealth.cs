using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Health of the durability subsystem (checkpoint cycle + WAL), surfaced for introspection and used by the checkpoint
/// failure classification (CK-06).
/// </summary>
/// <remarks>
/// A <see cref="Degraded"/> state is recoverable: a transient cycle exception (back-pressure / lock / IO timeout —
/// any <see cref="TyphonException"/> with <see cref="TyphonException.IsTransient"/>) is logged and retried on the next
/// cycle, never latched. <see cref="Fatal"/> is terminal for periodic cycles (the subsystem stops advancing the
/// checkpoint), but the shutdown path still attempts one last-chance flush cycle (04 §7).
/// </remarks>
[PublicAPI]
public enum DurabilityHealth
{
    /// <summary>All durability operations are completing within budget.</summary>
    Ok = 0,

    /// <summary>The last cycle hit a transient stall (timeout / back-pressure) and will retry; no data at risk.</summary>
    Degraded = 1,

    /// <summary>
    /// The checkpoint cannot advance: a page has been refused on several consecutive cycles, so CheckpointLSN stays where it is and no WAL segment is
    /// recycled — the WAL grows until the disk is full. Nothing is lost and nothing is slow, which is why it is a state of its own: a writer
    /// registration nobody releases (CP-13, #1538) shows nowhere else. Cleared by the next cycle that covers every page.
    /// </summary>
    Stalled = 2,

    /// <summary>A fatal, unrecoverable error latched the checkpoint subsystem; surfaced for operator action.</summary>
    Fatal = 3,
}
