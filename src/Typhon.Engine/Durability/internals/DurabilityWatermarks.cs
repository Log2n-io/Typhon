using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Durability-layer owner of the persisted <b>checkpoint watermark</b> — the <c>CheckpointLSN</c> + <c>CleanShutdown</c>
/// pair carried in the meta-pair bootstrap block (MinimalWal design 04 §6, M12). The storage layer
/// (<see cref="ManagedPagedMMF"/>) persists this as an opaque bootstrap value via
/// <see cref="ManagedPagedMMF.MutateBootstrapAndPersist"/>; it does <b>not</b> know these bytes are a log-sequence
/// number. This keeps <c>src/Typhon.Engine/Storage/</c> free of WAL/LSN identifiers (grep gate, 08 §7.1).
/// <para>
/// This static helper is the seed of the design's eventual <c>SnapshotStore</c> (04 §20), which will absorb the full
/// page-capture protocol; for now it owns only the watermark semantics, leaving the meta-pair A/B write protocol
/// (CK-05) in <see cref="ManagedPagedMMF"/> where it belongs (torn-write page protection is a storage concern).
/// </para>
/// </summary>
internal static class DurabilityWatermarks
{
    // {CheckpointLSN: long, CleanShutdown: bool} packed Int3 (lo32, hi32, flag), flipped atomically with the meta
    // generation (CK-05). Replaces the v1 BK_CheckpointLSN / BK_LastTickFenceLSN / BK_CleanShutdown keys (04 §6).
    private const string Key = "DurabilityWatermarks";

    /// <summary>Reads the persisted watermark pair; a fresh database (key absent) reads as <c>(0, false)</c>.</summary>
    internal static (long CheckpointLsn, bool CleanShutdown) Read(ManagedPagedMMF mmf)
    {
        if (!mmf.Bootstrap.TryGet(Key, out var v))
        {
            return (0, false);
        }

        var lsn = (uint)v.GetInt(0) | ((long)v.GetInt(1) << 32);
        return (lsn, v.GetInt(2) != 0);
    }

    /// <summary>The persisted checkpoint-LSN watermark (0 on a fresh database).</summary>
    internal static long ReadCheckpointLsn(ManagedPagedMMF mmf) => Read(mmf).CheckpointLsn;

    /// <summary>The persisted clean-shutdown flag.</summary>
    internal static bool ReadCleanShutdown(ManagedPagedMMF mmf) => Read(mmf).CleanShutdown;

    /// <summary>
    /// Advances the checkpoint-LSN watermark (preserving the clean-shutdown flag) and flips the meta pair — the
    /// generation flip is the atomic, fsynced commit point (CK-05). Called by the checkpoint cycle.
    /// </summary>
    internal static void UpdateCheckpointLsn(ManagedPagedMMF mmf, long checkpointLsn)
        => mmf.MutateBootstrapAndPersist(() => Write(mmf, checkpointLsn, Read(mmf).CleanShutdown));

    /// <summary>Sets the clean-shutdown flag (preserving the checkpoint LSN) and persists the meta page atomically (CK-05).</summary>
    internal static void SetCleanShutdown(ManagedPagedMMF mmf, bool cleanShutdown)
        => mmf.MutateBootstrapAndPersist(() => Write(mmf, Read(mmf).CheckpointLsn, cleanShutdown));

    // Must run inside MutateBootstrapAndPersist (under the meta lock) — the read-modify-write is atomic w.r.t. the flip.
    private static void Write(ManagedPagedMMF mmf, long checkpointLsn, bool cleanShutdown)
        => mmf.Bootstrap.Set(Key, BootstrapDictionary.Value.FromInt3((int)checkpointLsn, (int)(checkpointLsn >> 32), cleanShutdown ? 1 : 0));
}
