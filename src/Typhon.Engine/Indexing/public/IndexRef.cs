using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Opaque, reusable reference to a component index (primary key or secondary).
/// Resolved once (cold path via <see cref="DatabaseEngine.GetPKIndexRef{T}"/> or <see cref="DatabaseEngine.GetIndexRef{T,TKey}"/>),
/// reused many times at zero cost (hot path). Captures a layout version for O(1) staleness detection.
/// </summary>
[PublicAPI]
public readonly struct IndexRef
{
    internal readonly int FieldIndex;          // -1 = PK index
    internal readonly ComponentTable Table;
    internal readonly int CapturedLayoutVersion;

    internal IndexRef(int fieldIndex, ComponentTable table, int capturedLayoutVersion)
    {
        FieldIndex = fieldIndex;
        Table = table;
        CapturedLayoutVersion = capturedLayoutVersion;
    }

    /// <summary>True if this IndexRef points to the primary key index.</summary>
    public bool IsPrimaryKey => FieldIndex == -1;

    /// <summary>
    /// Validates that this IndexRef is still valid (not stale due to schema evolution).
    /// Throws <see cref="System.InvalidOperationException"/> if the index layout has changed since resolution.
    /// </summary>
    internal void Validate()
    {
        if (Table == null)
        {
            ThrowHelper.ThrowInvalidOp("IndexRef is not initialized");
        }

        if (CapturedLayoutVersion != Table.IndexLayoutVersion)
        {
            ThrowHelper.ThrowInvalidOp("IndexRef is stale — index layout changed. Call GetIndexRef/GetPKIndexRef again.");
        }
    }
}
