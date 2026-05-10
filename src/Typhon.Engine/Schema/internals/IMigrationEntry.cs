using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Uniform interface for a single migration step, regardless of whether it was registered as strongly-typed or byte-level.
/// The execution engine calls <see cref="Execute"/> with raw byte spans, enabling seamless chaining of mixed migration types.
/// </summary>
internal interface IMigrationEntry
{
    /// <summary>The component schema name (from [Component] attribute).</summary>
    string ComponentName { get; }

    /// <summary>Source revision this migration converts from.</summary>
    int FromRevision { get; }

    /// <summary>Target revision this migration converts to.</summary>
    int ToRevision { get; }

    /// <summary>Size in bytes of the old component struct.</summary>
    int OldSize { get; }

    /// <summary>Size in bytes of the new component struct.</summary>
    int NewSize { get; }

    /// <summary>
    /// Executes the migration on raw bytes. <paramref name="source"/> contains the old component data,
    /// <paramref name="destination"/> receives the new component data (pre-zeroed by caller).
    /// </summary>
    void Execute(ReadOnlySpan<byte> source, Span<byte> destination);
}
