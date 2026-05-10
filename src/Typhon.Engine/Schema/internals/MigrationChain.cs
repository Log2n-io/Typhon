namespace Typhon.Engine.Internals;

/// <summary>
/// An ordered sequence of migration steps that transforms a component from one revision to another, possibly through intermediate revisions.
/// Used by the migration execution engine for double-buffer chaining.
/// </summary>
internal readonly struct MigrationChain
{
    /// <summary>Ordered migration steps (e.g., R1→R2, R2→R3).</summary>
    public IMigrationEntry[] Steps { get; init; }

    /// <summary>Maximum byte size across all intermediate forms. Used to size the stackalloc double-buffer.</summary>
    public int MaxIntermediateSize { get; init; }

    public int StepCount => Steps.Length;
}
