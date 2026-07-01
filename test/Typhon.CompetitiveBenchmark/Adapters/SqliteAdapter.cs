using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Typhon.CompetitiveBenchmark.Adapters;

/// <summary>
/// SQLite (Microsoft.Data.Sqlite) — the feature-complete OLTP baseline. WAL mode; synchronous=OFF (D0) / FULL (D2).
/// Each autocommit UPDATE is its own transaction, so at D2 it fsyncs per write.
/// </summary>
public sealed class SqliteAdapter : IEngineAdapter
{
    private readonly DurabilityTier _tier;
    private readonly string _path;
    private SqliteConnection _conn;
    private SqliteCommand _read;
    private SqliteCommand _write;
    private SqliteParameter _rId;
    private SqliteParameter _wId;
    private SqliteParameter _wVal;

    public SqliteAdapter(string rootDir, DurabilityTier tier)
    {
        _tier = tier;
        var dir = Path.Combine(rootDir, "sqlite");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"bench_{tier}.db");
    }

    public string Name => $"SQLite ({_tier})";
    public bool IsFloor => false;

    public void Load(int count)
    {
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(f); } catch { }
        }

        _conn = new SqliteConnection($"Data Source={_path}");
        _conn.Open();
        Pragma("PRAGMA journal_mode=WAL;");
        Pragma("PRAGMA cache_size=-262144;");   // 256 MB page cache
        Pragma("PRAGMA temp_store=MEMORY;");
        Pragma("CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER) STRICT;");

        Pragma("PRAGMA synchronous=OFF;");      // fast bulk load regardless of tier
        using (var tx = _conn.BeginTransaction())
        using (var ins = _conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO t(id,v) VALUES($id,$v)";
            var pid = ins.CreateParameter(); pid.ParameterName = "$id"; ins.Parameters.Add(pid);
            var pv = ins.CreateParameter(); pv.ParameterName = "$v"; ins.Parameters.Add(pv);
            ins.Prepare();
            for (int i = 0; i < count; i++)
            {
                pid.Value = (long)i;
                pv.Value = (long)i;
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // Apply the tier's durability for the measured phase.
        Pragma($"PRAGMA synchronous={(_tier == DurabilityTier.D2 ? "FULL" : "OFF")};");

        _read = _conn.CreateCommand();
        _read.CommandText = "SELECT v FROM t WHERE id=$id";
        _rId = _read.CreateParameter(); _rId.ParameterName = "$id"; _read.Parameters.Add(_rId);
        _read.Prepare();

        _write = _conn.CreateCommand();
        _write.CommandText = "UPDATE t SET v=$v WHERE id=$id";
        _wId = _write.CreateParameter(); _wId.ParameterName = "$id"; _write.Parameters.Add(_wId);
        _wVal = _write.CreateParameter(); _wVal.ParameterName = "$v"; _write.Parameters.Add(_wVal);
        _write.Prepare();
    }

    private void Pragma(string sql)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public IDisposable OpenReadScope() => NoScope.Instance;

    public long PointRead(int key)
    {
        _rId.Value = (long)key;
        return (long)_read.ExecuteScalar();
    }

    public void PointWriteCommit(int key, long value)
    {
        _wId.Value = (long)key;
        _wVal.Value = value;
        _write.ExecuteNonQuery(); // autocommit → own transaction → fsync per write at D2
    }

    public long OnDiskBytes() => DiskUtil.Sum(_path, _path + "-wal", _path + "-shm");

    public void Dispose()
    {
        _read?.Dispose();
        _write?.Dispose();
        _conn?.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
