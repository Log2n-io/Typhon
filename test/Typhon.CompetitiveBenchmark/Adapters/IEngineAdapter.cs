using System;

namespace Typhon.CompetitiveBenchmark.Adapters;

/// <summary>
/// One rung of the C0 cost-ladder (and, later, one engine×tier in the broader suite). The harness drives every adapter
/// through the identical operation surface so the comparison is apples-to-apples at the *operation* level.
/// <para>
/// Keys are dense integers in <c>[0, count)</c>. Each adapter maps that logical key to its own native key
/// (Typhon EntityId, FASTER/LMDB long). Values are a single <see cref="long"/> — C0 measures the *minimal* unit of work,
/// so a one-long payload is the matched-to-floor shape (Shape N / Shape Y come with the A-scenarios).
/// </para>
/// </summary>
public interface IEngineAdapter : IDisposable
{
    /// <summary>Ladder-rung / engine label shown in the report.</summary>
    string Name { get; }

    /// <summary>True for the lean KV floor references (FASTER, LMDB-write) — not opponents, the baseline line.</summary>
    bool IsFloor { get; }

    /// <summary>Populate keys <c>[0, count)</c> with an initial value (value := key). Called once before measurement.</summary>
    void Load(int count);

    /// <summary>
    /// Open a shared read scope for a batch of <see cref="PointRead"/> calls (a Typhon read transaction / an LMDB
    /// read-only txn). FASTER returns a no-op. Reads are measured as the read *primitive* amortized under one scope,
    /// because — unlike writes — a read carries no commit.
    /// </summary>
    IDisposable OpenReadScope();

    /// <summary>Read the value stored for <paramref name="key"/> (inside the current read scope).</summary>
    long PointRead(int key);

    /// <summary>
    /// Write <paramref name="value"/> for <paramref name="key"/> as one full durable-commit unit (the rung's commit
    /// discipline). FASTER/LMDB commit per write; Typhon opens a quick transaction and commits.
    /// </summary>
    void PointWriteCommit(int key, long value);

    /// <summary>Total on-disk bytes the engine is using after <see cref="Load"/> — for A5 space-amplification. 0 if not on disk.</summary>
    long OnDiskBytes();
}
