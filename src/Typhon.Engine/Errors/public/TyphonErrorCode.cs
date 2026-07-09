namespace Typhon.Engine;

/// <summary>
/// Numeric error codes organized by subsystem range.
/// Only implemented codes are defined; reserved ranges are filled as later tiers are built out.
/// Codes within a range are assigned sequentially as needed; gaps are intentional
/// to allow insertion without renumbering.
/// </summary>
public enum TyphonErrorCode
{
    // 0 — Unspecified / generic

    /// <summary>No specific error code — a generic or unclassified engine failure.</summary>
    Unspecified                     = 0,

    // 1xxx — Transaction

    /// <summary>A transaction exceeded its overall deadline (see <see cref="TransactionTimeoutException"/>).</summary>
    TransactionTimeout              = 1002,

    // 2xxx — Storage

    /// <summary>Structural data corruption or an integrity violation (see <see cref="CorruptionException"/>).</summary>
    DataCorruption                  = 2003,

    /// <summary>A storage capacity limit was exceeded.</summary>
    StorageCapacityExceeded         = 2004,

    /// <summary>A page failed CRC32C verification — torn or corrupted (see <see cref="PageCorruptionException"/>).</summary>
    PageChecksumMismatch            = 2005,

    /// <summary>Page cache allocation timed out waiting for dirty pages to flush (see <see cref="PageCacheBackpressureTimeoutException"/>).</summary>
    PageCacheBackpressureTimeout    = 2006,

    /// <summary>The database bundle is already locked by another process (see <see cref="DatabaseLockedException"/>).</summary>
    DatabaseLocked                  = 2007,

    /// <summary>A file occupies the database bundle path, which must be a directory (raised as a <see cref="StorageException"/>).</summary>
    InvalidDatabaseBundle           = 2008,

    // 3xxx — Component

    /// <summary>
    /// A component's runtime schema is incompatible with the persisted schema, or the persisted revision is newer than the runtime
    /// (see <see cref="SchemaValidationException"/> and <see cref="SchemaDowngradeException"/>).
    /// </summary>
    SchemaValidation                = 3001,

    /// <summary>A user-supplied migration function failed for one or more entities (see <see cref="SchemaMigrationException"/>).</summary>
    SchemaMigration                 = 3002,

    // 4xxx — Index

    /// <summary>An insert or update would create a duplicate key in a unique index (see <see cref="UniqueConstraintViolationException"/>).</summary>
    UniqueConstraintViolation       = 4001,
    // 5xxx — Query (reserved)

    // 6xxx — Resource

    /// <summary>
    /// A resource limit was exhausted — registry slots, active transactions, or segment capacity (see <see cref="ResourceExhaustedException"/>).
    /// </summary>
    ResourceExhausted               = 6001,

    /// <summary>A shared or exclusive lock acquisition exceeded its deadline (see <see cref="LockTimeoutException"/>).</summary>
    LockTimeout                     = 6003,

    // 7xxx — Durability

    /// <summary>A WAL commit-buffer claim timed out waiting for buffer space (see <see cref="WalBackPressureTimeoutException"/>).</summary>
    WalBackPressureTimeout          = 7001,

    /// <summary>A single WAL claim exceeds the entire commit-buffer capacity (see <see cref="WalClaimTooLargeException"/>).</summary>
    WalClaimTooLarge                = 7002,

    /// <summary>A fatal WAL write I/O failure — the engine can no longer accept durable commits (see <see cref="WalWriteException"/>).</summary>
    WalWriteFailure                 = 7003,

    /// <summary>A WAL segment file operation (create, rotate, or header validation) failed (see <see cref="WalSegmentException"/>).</summary>
    WalSegmentError                 = 7004,

    /// <summary>A bulk-load session is already open — only one is allowed per engine (see <see cref="BulkSessionAlreadyActiveException"/>).</summary>
    BulkSessionAlreadyActive        = 7005,

    /// <summary>
    /// An operation was attempted on a bulk-load session that was already completed or disposed (see <see cref="BulkSessionClosedException"/>).
    /// </summary>
    BulkSessionClosed               = 7006,

    /// <summary>
    /// The synchronous checkpoint at bulk-load completion did not finish within its timeout (see <see cref="BulkLoadCheckpointTimeoutException"/>).
    /// </summary>
    BulkLoadCheckpointTimeout       = 7007,

    /// <summary>
    /// A published transaction's durability wait did not confirm its records reached stable storage (see <see cref="CommitDurabilityUncertainException"/>).
    /// </summary>
    CommitDurabilityUncertain       = 7008,

    // 8xxx — Runtime / Scheduler

    /// <summary>DEBUG-only: a system mutated a component it did not declare in its access set (see <see cref="InvalidAccessException"/>).</summary>
    InvalidSystemAccess             = 8001,
}
