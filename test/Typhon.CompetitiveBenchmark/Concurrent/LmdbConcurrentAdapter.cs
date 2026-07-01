using System;
using System.Buffers.Binary;
using System.IO;
using LightningDB;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// LMDB (memory-mapped B+tree). Shared environment; lock-free MVCC read-only transactions scale, writers serialize on the
/// single global write mutex (so updates/RMW cap — the contrast to Typhon's lock-free writes). Keys are BIG-ENDIAN so the
/// B-tree's byte ordering is numeric — mandatory for the ordered range scan (A6). D0: opened NoSync.
/// </summary>
public sealed class LmdbConcurrentAdapter : IConcurrentAdapter
{
    private readonly string _dir;
    private LightningEnvironment _env;

    public LmdbConcurrentAdapter(string root) => _dir = Path.Combine(root, "lmdb-m");

    public string Name => "LMDB";

    private static byte[] KeyBE(long k) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, k); return b; }
    private static byte[] Val(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); return b; }

    public void Load(int totalCount)
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        Directory.CreateDirectory(_dir);

        _env = new LightningEnvironment(_dir) { MapSize = 2L * 1024 * 1024 * 1024, MaxDatabases = 2, MaxReaders = 256 };
        _env.Open(EnvironmentOpenFlags.NoSync);

        using var tx = _env.BeginTransaction();
        using var db = tx.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
        for (int i = 0; i < totalCount; i++)
        {
            tx.Put(db, KeyBE(i), Val(i));
        }
        tx.Commit();
    }

    public IWorker CreateWorker() => new Worker(_env);

    public void Dispose()
    {
        _env?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Worker : IWorker
    {
        private readonly LightningEnvironment _env;

        public Worker(LightningEnvironment env) => _env = env;

        public long ReadBatch(int startKey, int count)
        {
            long sum = 0;
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase();
            for (int i = 0; i < count; i++)
            {
                var (rc, _, val) = tx.Get(db, KeyBE(startKey + i));
                if (rc == MDBResultCode.Success) sum += BinaryPrimitives.ReadInt64LittleEndian(val.AsSpan());
            }
            return sum;
        }

        public void UpdateBatch(int startKey, int count, long seed)
        {
            using var tx = _env.BeginTransaction();
            using var db = tx.OpenDatabase();
            for (int i = 0; i < count; i++)
            {
                tx.Put(db, KeyBE(startKey + i), Val(seed + i));
            }
            tx.Commit();
        }

        // One write txn doing get-then-put per key. LMDB write txns are globally serialized → atomic by construction.
        public void RmwBatch(int startKey, int count)
        {
            using var tx = _env.BeginTransaction();
            using var db = tx.OpenDatabase();
            for (int i = 0; i < count; i++)
            {
                long k = startKey + i;
                var (rc, _, val) = tx.Get(db, KeyBE(k));
                long v = rc == MDBResultCode.Success ? BinaryPrimitives.ReadInt64LittleEndian(val.AsSpan()) : 0;
                tx.Put(db, KeyBE(k), Val(v + 1));
            }
            tx.Commit();
        }

        public long RangeScan(int startKey, int length)
        {
            long sum = 0;
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase();
            using var cur = tx.CreateCursor(db);
            if (cur.SetRange(KeyBE(startKey)) == MDBResultCode.Success)
            {
                var (rc, _, val) = cur.GetCurrent();
                for (int i = 0; i < length && rc == MDBResultCode.Success; i++)
                {
                    sum += BinaryPrimitives.ReadInt64LittleEndian(val.AsSpan());
                    (rc, _, val) = cur.Next();
                }
            }
            return sum;
        }

        public void Dispose() { }
    }
}
