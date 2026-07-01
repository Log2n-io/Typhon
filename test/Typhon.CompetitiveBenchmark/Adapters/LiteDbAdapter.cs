using System;
using System.Collections.Generic;
using System.IO;
using LiteDB;

namespace Typhon.CompetitiveBenchmark.Adapters;

/// <summary>
/// LiteDB — pure-managed BSON document store; the developer-convenience baseline (lower bound). Durability knobs are
/// coarse (WAL log + checkpoint); it has no fine fsync dial, so the tier is informational only (treat as ≈default).
/// </summary>
public sealed class LiteDbAdapter : IEngineAdapter
{
    private readonly string _path;
    private LiteDatabase _db;
    private ILiteCollection<BsonDocument> _col;

    public LiteDbAdapter(string rootDir, DurabilityTier tier)
    {
        var dir = Path.Combine(rootDir, "litedb");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"bench_{tier}.litedb");
    }

    public string Name => "LiteDB (baseline)";
    public bool IsFloor => false;

    public void Load(int count)
    {
        try { File.Delete(_path); } catch { }
        _db = new LiteDatabase(_path);
        _col = _db.GetCollection<BsonDocument>("t");

        var docs = new List<BsonDocument>(count);
        for (int i = 0; i < count; i++)
        {
            docs.Add(new BsonDocument { ["_id"] = i, ["v"] = (long)i });
        }
        _col.InsertBulk(docs);
    }

    public IDisposable OpenReadScope() => NoScope.Instance;

    public long PointRead(int key) => _col.FindById(key)["v"].AsInt64;

    public void PointWriteCommit(int key, long value) =>
        _col.Update(new BsonDocument { ["_id"] = key, ["v"] = value });

    public long OnDiskBytes() => DiskUtil.Sum(_path);

    public void Dispose() => _db?.Dispose();
}
