namespace Typhon.CompetitiveBenchmark;

/// <summary>
/// Durability tier — every engine runs in the SAME tier per scenario (plan §3 F1). v1 implements the two endpoints;
/// D1 (group / OS-buffered) is deferred.
/// </summary>
public enum DurabilityTier
{
    /// <summary>No sync — CPU ceiling. SQLite synchronous=OFF, RocksDB sync=false, LMDB NoSync, Typhon in-mem WAL Deferred.</summary>
    D0,

    /// <summary>fsync per commit — the honest ACID-durable comparison. SQLite WAL+FULL, RocksDB sync=true, LMDB msync, Typhon on-disk WAL+FUA+Immediate.</summary>
    D2
}
