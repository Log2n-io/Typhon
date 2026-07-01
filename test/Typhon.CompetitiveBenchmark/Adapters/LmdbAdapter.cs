using System;
using System.Buffers.Binary;
using System.IO;
using LightningDB;

namespace Typhon.CompetitiveBenchmark.Adapters;

/// <summary>
/// Floor reference: LMDB (LightningDB) write path — copy-on-write B+tree, single-writer, zero-copy mmap reads.
/// At I0 (no snapshot needed) it is a lean durable-ish KV; here C0 runs it in-memory-fast (NoSync) to measure the CPU floor.
/// NOT an opponent for the durable comparison (that's the I0-txn rung).
/// </summary>
public sealed class LmdbAdapter : IEngineAdapter
{
    private readonly DurabilityTier _tier;
    private readonly string _dir;
    private LightningEnvironment _env;
    private LightningDatabase _db;

    public LmdbAdapter(string rootDir, DurabilityTier tier = DurabilityTier.D0)
    {
        _tier = tier;
        _dir = Path.Combine(rootDir, $"lmdb_{tier}");
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        Directory.CreateDirectory(_dir);
    }

    // At D0 (NoSync) LMDB is the lean CPU floor; at D2 (msync) it is a real durable single-writer opponent (I0-txn).
    public string Name => _tier == DurabilityTier.D0 ? "LMDB-write (floor)" : "LMDB (D2)";
    public bool IsFloor => _tier == DurabilityTier.D0;

    public void Load(int count)
    {
        _env = new LightningEnvironment(_dir)
        {
            MapSize = 1L * 1024 * 1024 * 1024, // 1 GiB — ample for the cache-resident C0 set
            MaxDatabases = 2
        };
        // D0 = NoSync (CPU floor); D2 = default open (msync on commit = durable).
        _env.Open(_tier == DurabilityTier.D0 ? EnvironmentOpenFlags.NoSync : EnvironmentOpenFlags.None);

        using (var tx = _env.BeginTransaction())
        {
            _db = tx.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
            tx.Commit();
        }

        using (var tx = _env.BeginTransaction())
        {
            Span<byte> k = stackalloc byte[8];
            Span<byte> v = stackalloc byte[8];
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt64BigEndian(k, i);
                BinaryPrimitives.WriteInt64LittleEndian(v, i);
                tx.Put(_db, k, v);
            }
            tx.Commit();
        }
    }

    private LightningTransaction _readTx;

    public IDisposable OpenReadScope()
    {
        _readTx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
        return new ReadScope(this);
    }

    public long PointRead(int key)
    {
        Span<byte> k = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(k, key);
        var (_, _, value) = _readTx.Get(_db, k);
        return BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan());
    }

    public void PointWriteCommit(int key, long value)
    {
        Span<byte> k = stackalloc byte[8];
        Span<byte> v = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(k, key);
        BinaryPrimitives.WriteInt64LittleEndian(v, value);
        using var tx = _env.BeginTransaction();
        tx.Put(_db, k, v);
        tx.Commit();
    }

    // data.mdb is pre-sized to MapSize (sparse) — its file length overstates usage. Use the page accounting instead.
    public long OnDiskBytes()
    {
        var info = _env.Info;
        var st = _env.EnvironmentStats;
        return (info.LastPageNumber + 1) * (long)st.PageSize;
    }

    public void Dispose()
    {
        _db?.Dispose();
        _env?.Dispose();
    }

    // The read scope holds a single read-only transaction so a batch of reads amortizes txn setup (the read-primitive cost).
    private sealed class ReadScope : IDisposable
    {
        private readonly LmdbAdapter _owner;
        public ReadScope(LmdbAdapter owner) => _owner = owner;

        public void Dispose()
        {
            _owner._readTx?.Dispose();
            _owner._readTx = null;
        }
    }
}
