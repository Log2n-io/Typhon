// unset

using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Mutable state for writing WAL records within a single claim.
/// Groups the cursor state that advances as records are written.
/// </summary>
internal ref struct WalRecordWriter
{
    public Span<byte> DataSpan;
    public int WriteOffset;
    public int RecordIndex;
    public long CurrentLsn;
    public int TotalRecordCount;

    /// <summary>
    /// Returns the highest LSN that was written (CurrentLsn - 1 after writing).
    /// </summary>
    public readonly long HighestLsn => CurrentLsn - 1;
}
