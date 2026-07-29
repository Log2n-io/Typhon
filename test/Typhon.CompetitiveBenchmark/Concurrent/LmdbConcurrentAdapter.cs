using System;
using System.Buffers.Binary;
using System.IO;
using LightningDB;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// LMDB (memory-mapped B+tree). Shared environment; lock-free MVCC read-only transactions scale, writers serialize on the
/// single global write mutex (so updates/RMW cap — the contrast to Typhon's lock-free writes). Keys are BIG-ENDIAN so the
/// B-tree's byte ordering is numeric — mandatory for the ordered range scan (A6). D0: opened NoSync.
/// <para>
/// <b>Two corrections applied after review</b>, because a naive adapter produces a number that flatters Typhon and that is
/// a defect in the measurement, not a property of LMDB:
/// </para>
/// <list type="number">
/// <item><b>The database handle is opened ONCE</b> and reused across transactions and threads. The previous version called
/// <c>tx.OpenDatabase()</c> on every batch; that is <c>mdb_dbi_open</c>, which takes an environment-wide lock and is
/// explicitly documented as a startup-time call. Re-opening it per operation is the single worst thing an LMDB binding
/// can do under concurrency.</item>
/// <item><b>Keys and values are stack-allocated</b>. The previous version allocated two <c>byte[8]</c> per operation,
/// putting GC pressure into the measured loop — a cost LMDB itself never incurs.</item>
/// </list>
/// <para>
/// Remaining known headroom, stated rather than hidden: LMDB read transactions can be pooled with
/// <c>Reset()</c>/<c>Renew()</c> instead of begin/dispose per batch. That is a further optimisation an LMDB specialist
/// would likely make, and it is not applied here.
/// </para>
/// </summary>
public sealed class LmdbConcurrentAdapter : IConcurrentAdapter
{
    private readonly string _dir;
    private LightningEnvironment _env;

    /// <summary>Opened once in <see cref="Load"/>; an LMDB dbi handle is environment-lifetime and is meant to be shared across threads.</summary>
    private LightningDatabase _db;

    public LmdbConcurrentAdapter(string root) => _dir = Path.Combine(root, "lmdb-m");

    public string Name => "LMDB";

    private static void WriteKeyBE(Span<byte> dst, long k) => BinaryPrimitives.WriteInt64BigEndian(dst, k);
    private static void WriteValLE(Span<byte> dst, long v) => BinaryPrimitives.WriteInt64LittleEndian(dst, v);

    public void Load(int totalCount)
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        Directory.CreateDirectory(_dir);

        _env = new LightningEnvironment(_dir) { MapSize = 2L * 1024 * 1024 * 1024, MaxDatabases = 2, MaxReaders = 256 };
        _env.Open(EnvironmentOpenFlags.NoSync);

        using var tx = _env.BeginTransaction();
        _db = tx.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
        Span<byte> key = stackalloc byte[8];
        Span<byte> val = stackalloc byte[8];
        for (int i = 0; i < totalCount; i++)
        {
            WriteKeyBE(key, i);
            WriteValLE(val, i);
            tx.Put(_db, key, val);
        }
        tx.Commit();
        // _db is deliberately NOT disposed here — the handle stays open for the environment's lifetime and every worker
        // reuses it. Disposing would close the dbi and force a re-open (and its lock) on first use.
    }

    public IWorker CreateWorker() => new Worker(_env, _db);

    public void Dispose()
    {
        _db?.Dispose();
        _env?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Worker : IWorker
    {
        private readonly LightningEnvironment _env;
        private readonly LightningDatabase _db;

        public Worker(LightningEnvironment env, LightningDatabase db)
        {
            _env = env;
            _db = db;
        }

        public long ReadBatch(int startKey, int count)
        {
            long sum = 0;
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            Span<byte> key = stackalloc byte[8];
            for (int i = 0; i < count; i++)
            {
                WriteKeyBE(key, startKey + i);
                var (rc, _, val) = tx.Get(_db, key);
                if (rc == MDBResultCode.Success)
                {
                    sum += BinaryPrimitives.ReadInt64LittleEndian(val.AsSpan());
                }
            }
            return sum;
        }

        public void UpdateBatch(int startKey, int count, long seed)
        {
            using var tx = _env.BeginTransaction();
            Span<byte> key = stackalloc byte[8];
            Span<byte> val = stackalloc byte[8];
            for (int i = 0; i < count; i++)
            {
                WriteKeyBE(key, startKey + i);
                WriteValLE(val, seed + i);
                tx.Put(_db, key, val);
            }
            tx.Commit();
        }

        // One write txn doing get-then-put per key. LMDB write txns are globally serialized → atomic by construction.
        public void RmwBatch(int startKey, int count)
        {
            using var tx = _env.BeginTransaction();
            Span<byte> key = stackalloc byte[8];
            Span<byte> val = stackalloc byte[8];
            for (int i = 0; i < count; i++)
            {
                WriteKeyBE(key, startKey + i);
                var (rc, _, cur) = tx.Get(_db, key);
                long v = rc == MDBResultCode.Success ? BinaryPrimitives.ReadInt64LittleEndian(cur.AsSpan()) : 0;
                WriteValLE(val, v + 1);
                tx.Put(_db, key, val);
            }
            tx.Commit();
        }

        public long RangeScan(int startKey, int length)
        {
            long sum = 0;
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var cur = tx.CreateCursor(_db);
            Span<byte> key = stackalloc byte[8];
            WriteKeyBE(key, startKey);
            if (cur.SetRange(key) == MDBResultCode.Success)
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
