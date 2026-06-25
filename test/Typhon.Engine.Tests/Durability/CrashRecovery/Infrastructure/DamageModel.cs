namespace Typhon.Engine.Tests;

/// <summary>
/// Which I/O subsystem a crash event belongs to. Layer 1 (P0) only injects WAL faults; <see cref="IoSubsystem.DataFile"/> is reserved for the Layer 2
/// cross-subsystem timeline (P1.5).
/// </summary>
internal enum IoSubsystem
{
    /// <summary>Write-ahead log segment I/O (<see cref="ChaosWalFileIO"/>).</summary>
    Wal,

    /// <summary>Data file page I/O (Layer 2 — not used in P0).</summary>
    DataFile,
}

/// <summary>
/// How an interrupted write is reflected on disk after a simulated crash. Models the fact that a power loss can leave the in-flight write absent, half-written,
/// reordered, or zeroed (per <c>claude/design/Durability/crash-recovery-testing.md</c> §5.1.4).
/// </summary>
internal enum DamageType
{
    /// <summary>All writes before the crash survive; the in-flight write is lost entirely (the common, well-behaved case).</summary>
    CleanCut,

    /// <summary>The in-flight write is partially applied — its first <see cref="DamageModel.TornWriteFraction"/> (rounded down to 4096-byte alignment) lands; the rest is absent.</summary>
    TornWrite,

    /// <summary>
    /// Each in-flight write before the crash is independently included or excluded by a seeded coin flip; the crash write is dropped. Models firmware/controller
    /// anomalies for unsynchronized writes (defense-in-depth, not the common case). Note: WAL writes are append-only at distinct offsets, so actual byte-reordering is
    /// a no-op for the reader — this effectively reduces to a "random surviving subset of the in-flight writes" model.
    /// </summary>
    Reordered,

    /// <summary>The in-flight write's destination range is zero-filled (a pre-allocated segment region that never received data).</summary>
    ZeroFill,
}

/// <summary>
/// Parameters controlling how <see cref="ChaosWalFileIO.GetPostCrashState"/> reconstructs the on-disk state after a simulated crash. Deterministic given
/// <see cref="Seed"/>.
/// </summary>
/// <param name="Type">The damage pattern applied to writes that were not yet durable at the crash instant.</param>
/// <param name="Seed">Seed for the deterministic random choices used by <see cref="DamageType.Reordered"/>.</param>
/// <param name="TornWriteFraction">For <see cref="DamageType.TornWrite"/>: the fraction of the crash write that survived, in [0, 1].</param>
internal readonly record struct DamageModel(DamageType Type, int Seed = 0, float TornWriteFraction = 0.5f);

/// <summary>
/// How a data-file page is left after a simulated crash during checkpoint (Layer 2, P1.5). Applied by
/// <see cref="ChaosPageIO.DamagePageOnDisk"/> to a specific page in the <b>real</b> data file after the crash, before the engine reopens
/// (per <c>claude/design/Durability/crash-recovery-testing.md</c> §5.2.3). FPI repair is gone (increment D); recovery now heals a torn page
/// by re-deriving it (rebuild net, RB-01) or fails the open loudly (RB-04) if it still backs a live primary chunk.
/// </summary>
internal enum PageDamageType
{
    /// <summary>The page is left untouched — models a checkpoint that crashed BEFORE writing this page (its on-disk content is the pre-checkpoint
    /// version, covered by the WAL window since the cycle never advanced CheckpointLSN).</summary>
    MissedPage,

    /// <summary>The second 4 KiB half of the page is overwritten with 0xFF — a torn write whose two sectors disagree, so the page CRC fails on load.</summary>
    TornPage,

    /// <summary>The whole page is zero-filled — models a write into a freshly-allocated region that never received data; CRC fails on load.</summary>
    ZeroPage,
}
