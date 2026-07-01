using System;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// Concurrency-capable adapter. The harness runs N threads, each with its own <see cref="IWorker"/> (per-thread engine
/// state: own session / read-txn / connection). Operations work on a BATCH of <c>count</c> components/rows in one
/// transaction — that's the "volume" axis (1, 8, 16, 256, 512, 1024 rows/op). Threads operate on disjoint key
/// partitions (no artificial write-write conflict) to expose each engine's true concurrency ceiling.
/// </summary>
public interface IConcurrentAdapter : IDisposable
{
    string Name { get; }
    void Load(int totalCount);
    IWorker CreateWorker();
}

public interface IWorker : IDisposable
{
    /// <summary>Read <paramref name="count"/> components/rows starting at <paramref name="startKey"/> in ONE transaction; return a checksum (defeats DCE).</summary>
    long ReadBatch(int startKey, int count);

    /// <summary>Update <paramref name="count"/> components/rows in ONE transaction (and commit).</summary>
    void UpdateBatch(int startKey, int count, long seed);

    /// <summary>
    /// Read-modify-write (YCSB-F): for each of <paramref name="count"/> keys from <paramref name="startKey"/>, read the
    /// current value, increment by 1, and write it back atomically. Workers own disjoint partitions, so each key is
    /// touched by exactly one thread (no cross-thread lost-update); the per-engine form still uses its natural atomic RMW.
    /// </summary>
    void RmwBatch(int startKey, int count);

    /// <summary>
    /// Ordered range scan (YCSB-E): sum the values of <paramref name="length"/> rows in ascending key order starting at
    /// <paramref name="startKey"/>. Returns the checksum. Engines without an ordered iterator (FASTER) throw NotSupported.
    /// </summary>
    long RangeScan(int startKey, int length);
}
