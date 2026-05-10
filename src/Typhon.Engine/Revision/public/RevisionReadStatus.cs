using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>Status codes for revision chain reads (MVCC-aware).</summary>
[PublicAPI]
public enum RevisionReadStatus : byte
{
    /// <summary>Revision found and visible at this snapshot tick.</summary>
    Success = 0,

    /// <summary>Entity has no revision chain (never created).</summary>
    NotFound = 1,

    /// <summary>Revision exists but is not visible at the reader's snapshot tick.</summary>
    SnapshotInvisible = 2,

    /// <summary>Entity was tombstoned (deleted) at or before the reader's snapshot tick.</summary>
    Deleted = 3,
}
