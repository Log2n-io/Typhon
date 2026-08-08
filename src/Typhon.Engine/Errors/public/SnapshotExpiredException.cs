using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Thrown when a read is attempted at a snapshot whose revisions have already been reclaimed by cleanup.
/// </summary>
/// <remarks>
/// <para>
/// Raised by <see cref="PointInTimeAccessor"/> reads of <c>Versioned</c> components. A PTA obtains a TSN but registers nothing in the transaction chain, so
/// the cleanup that decides how far back revisions must be kept cannot see it and is free to trim below a live snapshot. Before this existed, such a read
/// returned an all-zero component — wrong data, indistinguishable at the call site from legitimately-zero data (#672).
/// </para>
/// <para>
/// The accessor is not given real retention deliberately. Retention means chains stop being trimmed while a snapshot is live, and
/// <c>RevisionChainReader.TryWalkSingleEntryOptimistic</c> — the lock-free fast path for every Versioned read in the engine — is predicated on chains being
/// one entry long. That would hand any caller a way to silently degrade MVCC read performance engine-wide by holding an accessor too long, or leaking one.
/// Failing fast is the cheaper and safer contract.
/// </para>
/// <para>
/// A caller that needs a snapshot to survive concurrent commits should use a read-only <c>Transaction</c>, which registers in the chain and is therefore
/// visible to cleanup.
/// </para>
/// </remarks>
[PublicAPI]
public class SnapshotExpiredException : TyphonException
{
    /// <summary>Creates a new <see cref="SnapshotExpiredException"/> naming the snapshot and the watermark that overtook it.</summary>
    /// <param name="snapshotTsn">The TSN the read was attempted at.</param>
    /// <param name="retainedMinTsn">The oldest TSN whose revisions are still guaranteed to be retained.</param>
    public SnapshotExpiredException(long snapshotTsn, long retainedMinTsn)
        : base(TyphonErrorCode.SnapshotExpired,
            $"Snapshot at TSN {snapshotTsn} has expired: revisions are only retained from TSN {retainedMinTsn}. A PointInTimeAccessor does not hold "
            + "retention — a committing writer may trim the revisions its snapshot needs. Use a read-only Transaction for a snapshot that must survive "
            + "concurrent commits.")
    {
        SnapshotTsn = snapshotTsn;
        RetainedMinTsn = retainedMinTsn;
    }

    /// <summary>The TSN the read was attempted at.</summary>
    public long SnapshotTsn { get; }

    /// <summary>The oldest TSN whose revisions are still guaranteed to be retained.</summary>
    public long RetainedMinTsn { get; }
}
