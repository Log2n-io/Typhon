using System;
using System.Buffers.Binary;
using System.IO;
using RocksDbSharp;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// RocksDB (LSM key-value). One <see cref="RocksDb"/> instance is fully thread-safe and shared across all workers. Keys are
/// stored BIG-ENDIAN so the LSM's lexicographic byte ordering matches numeric order — mandatory for the ordered range scan
/// (A6); point ops are unaffected by byte order. D0: writes hit the WAL but are not fsync'd.
/// </summary>
public sealed class RocksDbConcurrentAdapter : IConcurrentAdapter
{
    private readonly string _dir;
    private RocksDb _db;

    public RocksDbConcurrentAdapter(string root) => _dir = Path.Combine(root, "rocksdb-m");

    public string Name => "RocksDB";

    // Numeric-sortable big-endian key (row ids are non-negative → unsigned memcmp order == numeric order). Value byte order
    // is irrelevant (never range-compared) — keep it little-endian.
    private static byte[] KeyBE(long k) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, k); return b; }
    private static byte[] Val(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); return b; }

    public void Load(int totalCount)
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        Directory.CreateDirectory(_dir);

        var options = new DbOptions().SetCreateIfMissing(true).IncreaseParallelism(Environment.ProcessorCount);
        _db = RocksDb.Open(options, _dir);

        var wo = new WriteOptions().SetSync(false);
        var wb = new WriteBatch();
        for (int i = 0; i < totalCount; i++)
        {
            wb.Put(KeyBE(i), Val(i));
            if ((i + 1) % 16384 == 0) { _db.Write(wb, wo); wb.Dispose(); wb = new WriteBatch(); }
        }
        _db.Write(wb, wo);
        wb.Dispose();
    }

    public IWorker CreateWorker() => new Worker(_db);

    public void Dispose()
    {
        _db?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Worker : IWorker
    {
        private readonly RocksDb _db;
        private readonly WriteOptions _wo = new WriteOptions().SetSync(false);
        private readonly byte[] _key = new byte[8];

        public Worker(RocksDb db) => _db = db;

        private byte[] K(long k) { BinaryPrimitives.WriteInt64BigEndian(_key, k); return _key; }

        public long ReadBatch(int startKey, int count)
        {
            long sum = 0;
            for (int i = 0; i < count; i++)
            {
                var v = _db.Get(K(startKey + i));
                if (v != null) sum += BinaryPrimitives.ReadInt64LittleEndian(v);
            }
            return sum;
        }

        public void UpdateBatch(int startKey, int count, long seed)
        {
            using var wb = new WriteBatch();
            for (int i = 0; i < count; i++)
            {
                wb.Put(KeyBE(startKey + i), Val(seed + i));
            }
            _db.Write(wb, _wo);
        }

        // Literal read-modify-write (get+put). Workers own disjoint partitions, so no cross-thread key contention — get+put
        // is correct here (RocksDbSharp exposes no transactions; the atomic-under-contention form would be a Merge operator).
        public void RmwBatch(int startKey, int count)
        {
            for (int i = 0; i < count; i++)
            {
                long k = startKey + i;
                var cur = _db.Get(K(k));
                long v = cur != null ? BinaryPrimitives.ReadInt64LittleEndian(cur) : 0;
                _db.Put(KeyBE(k), Val(v + 1), writeOptions: _wo);
            }
        }

        public long RangeScan(int startKey, int length)
        {
            long sum = 0;
            using var it = _db.NewIterator();
            it.Seek(KeyBE(startKey));
            for (int i = 0; i < length && it.Valid(); i++)
            {
                sum += BinaryPrimitives.ReadInt64LittleEndian(it.GetValueSpan());
                it.Next();
            }
            return sum;
        }

        public void Dispose() { }
    }
}
