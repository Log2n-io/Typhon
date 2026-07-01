using System;
using System.Buffers.Binary;
using System.IO;
using RocksDbSharp;

namespace Typhon.CompetitiveBenchmark.Adapters;

/// <summary>
/// RocksDB (rocksdb / RocksDbSharp) — LSM KV. Single-long value; secondary indexes / range emulation arrive with A6.
/// D0 = WriteOptions.sync=false; D2 = sync=true (fsync WAL per Put).
/// </summary>
public sealed class RocksDbAdapter : IEngineAdapter
{
    private readonly DurabilityTier _tier;
    private readonly string _dir;
    private RocksDb _db;
    private WriteOptions _wo;
    private readonly byte[] _kbuf = new byte[8];
    private readonly byte[] _vbuf = new byte[8];

    public RocksDbAdapter(string rootDir, DurabilityTier tier)
    {
        _tier = tier;
        _dir = Path.Combine(rootDir, $"rocks_{tier}");
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        Directory.CreateDirectory(_dir);
    }

    public string Name => $"RocksDB ({_tier})";
    public bool IsFloor => false;

    public void Load(int count)
    {
        var opts = new DbOptions().SetCreateIfMissing(true).SetCompression(Compression.No);
        _db = RocksDb.Open(opts, _dir);
        _wo = new WriteOptions().SetSync(_tier == DurabilityTier.D2);

        var loadWo = new WriteOptions().SetSync(false);
        using var wb = new WriteBatch();
        for (int i = 0; i < count; i++)
        {
            var k = new byte[8];
            var v = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(k, i);
            BinaryPrimitives.WriteInt64LittleEndian(v, i);
            wb.Put(k, v);
        }
        _db.Write(wb, loadWo);
    }

    public IDisposable OpenReadScope() => NoScope.Instance;

    public long PointRead(int key)
    {
        BinaryPrimitives.WriteInt64BigEndian(_kbuf, key);
        var got = _db.Get(_kbuf);
        return BinaryPrimitives.ReadInt64LittleEndian(got);
    }

    public void PointWriteCommit(int key, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(_kbuf, key);
        BinaryPrimitives.WriteInt64LittleEndian(_vbuf, value);
        _db.Put(_kbuf, _vbuf, cf: null, writeOptions: _wo);
    }

    public long OnDiskBytes() => DiskUtil.Sum(_dir); // SST + WAL + MANIFEST + CURRENT + OPTIONS

    public void Dispose() => _db?.Dispose();
}
