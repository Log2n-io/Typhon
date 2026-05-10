namespace Typhon.Engine;

/// <summary>
/// Numeric error codes organized by subsystem range.
/// Only Tier 1 codes are defined; reserved ranges are filled by later tiers.
/// Codes within a range are assigned sequentially as needed; gaps are intentional
/// to allow insertion without renumbering.
/// </summary>
public enum TyphonErrorCode
{
    // 0 — Unspecified / generic
    Unspecified                     = 0,

    // 1xxx — Transaction
    TransactionTimeout              = 1002,

    // 2xxx — Storage
    DataCorruption                  = 2003,
    StorageCapacityExceeded         = 2004,
    PageChecksumMismatch            = 2005,
    PageCacheBackpressureTimeout    = 2006,
    DatabaseLocked                  = 2007,

    // 3xxx — Component
    SchemaValidation                = 3001,
    SchemaMigration                 = 3002,

    // 4xxx — Index
    UniqueConstraintViolation       = 4001,
    // 5xxx — Query (reserved)

    // 6xxx — Resource
    ResourceExhausted               = 6001,
    LockTimeout                     = 6003,

    // 7xxx — Durability
    WalBackPressureTimeout          = 7001,
    WalClaimTooLarge                = 7002,
    WalWriteFailure                 = 7003,
    WalSegmentError                 = 7004,

    // 8xxx — Runtime / Scheduler
    InvalidSystemAccess             = 8001,
}
