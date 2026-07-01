using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// SQLite, WAL mode (synchronous=NORMAL — D0-ish, no per-commit fsync; the durable comparison is a separate axis).
/// WAL gives concurrent readers; writers serialize on the single-writer lock — so reads should scale, updates should cap.
/// Each worker holds its own connection (SqliteConnection is not thread-safe).
/// </summary>
public sealed class SqliteConcurrentAdapter : IConcurrentAdapter
{
    private readonly string _path;

    public SqliteConcurrentAdapter(string rootDir)
    {
        var dir = Path.Combine(rootDir, "sqlite-m");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "bench.db");
    }

    public string Name => "SQLite";

    public void Load(int totalCount)
    {
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) { try { File.Delete(f); } catch { } }
        using var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        Pragma(conn, "PRAGMA journal_mode=WAL;");
        Pragma(conn, "PRAGMA synchronous=NORMAL;");
        Pragma(conn, "PRAGMA cache_size=-524288;"); // 512 MiB page cache (negative = KiB), matching Typhon's 512 MB
        Pragma(conn, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER) STRICT;");
        using var tx = conn.BeginTransaction();
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO t(id,v) VALUES($id,$v)";
        var pid = ins.CreateParameter(); pid.ParameterName = "$id"; ins.Parameters.Add(pid);
        var pv = ins.CreateParameter(); pv.ParameterName = "$v"; ins.Parameters.Add(pv);
        ins.Prepare();
        for (int i = 0; i < totalCount; i++) { pid.Value = (long)i; pv.Value = (long)i; ins.ExecuteNonQuery(); }
        tx.Commit();

        static void Pragma(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    }

    public IWorker CreateWorker() => new Worker(_path);

    public void Dispose() => SqliteConnection.ClearAllPools();

    private sealed class Worker : IWorker
    {
        private readonly SqliteConnection _conn;
        private readonly SqliteCommand _read;
        private readonly SqliteParameter _rId;
        private readonly SqliteCommand _write;
        private readonly SqliteParameter _wId, _wVal;
        private readonly SqliteCommand _scan;
        private readonly SqliteParameter _scanStart, _scanN;

        public Worker(string path)
        {
            _conn = new SqliteConnection($"Data Source={path}"); // private cache per connection → WAL readers scale
            _conn.Open();
            // synchronous=NORMAL (D0-ish) + a 512 MiB page cache per connection (negative = KiB) so the whole working set
            // stays resident, matching Typhon's 512 MB cache. The ~30 MB DB fits entirely → no per-read page eviction/re-fault.
            using (var c = _conn.CreateCommand()) { c.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA cache_size=-524288;"; c.ExecuteNonQuery(); }
            _read = _conn.CreateCommand();
            _read.CommandText = "SELECT v FROM t WHERE id=$id";
            _rId = _read.CreateParameter(); _rId.ParameterName = "$id"; _read.Parameters.Add(_rId);
            _read.Prepare();
            _write = _conn.CreateCommand();
            _write.CommandText = "UPDATE t SET v=$v WHERE id=$id";
            _wId = _write.CreateParameter(); _wId.ParameterName = "$id"; _write.Parameters.Add(_wId);
            _wVal = _write.CreateParameter(); _wVal.ParameterName = "$v"; _write.Parameters.Add(_wVal);
            _write.Prepare();
            _scan = _conn.CreateCommand();
            _scan.CommandText = "SELECT v FROM t WHERE id>=$start ORDER BY id LIMIT $n"; // id is INTEGER PRIMARY KEY → indexed
            _scanStart = _scan.CreateParameter(); _scanStart.ParameterName = "$start"; _scan.Parameters.Add(_scanStart);
            _scanN = _scan.CreateParameter(); _scanN.ParameterName = "$n"; _scan.Parameters.Add(_scanN);
            _scan.Prepare();
        }

        public long ReadBatch(int startKey, int count)
        {
            long sum = 0;
            // Wrap the batch in ONE transaction (a single WAL read snapshot), symmetric with UpdateBatch — otherwise each
            // SELECT runs in autocommit (an implicit per-statement transaction) and reads are unfairly penalised vs writes.
            using var tx = _conn.BeginTransaction(deferred: true);
            _read.Transaction = tx;
            for (int i = 0; i < count; i++)
            {
                _rId.Value = (long)(startKey + i);
                sum += (long)_read.ExecuteScalar();
            }
            tx.Commit();
            _read.Transaction = null;
            return sum;
        }

        public void UpdateBatch(int startKey, int count, long seed)
        {
            using var tx = _conn.BeginTransaction();
            _write.Transaction = tx;
            for (int i = 0; i < count; i++)
            {
                _wId.Value = (long)(startKey + i);
                _wVal.Value = seed + i;
                _write.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // RMW: BEGIN IMMEDIATE (deferred:false → reserved write lock up front, no read→write upgrade deadlock), SELECT then
        // UPDATE per key, all in one atomic transaction.
        public void RmwBatch(int startKey, int count)
        {
            using var tx = _conn.BeginTransaction(deferred: false);
            _read.Transaction = tx;
            _write.Transaction = tx;
            for (int i = 0; i < count; i++)
            {
                long k = startKey + i;
                _rId.Value = k;
                long cur = (long)_read.ExecuteScalar();
                _wId.Value = k;
                _wVal.Value = cur + 1;
                _write.ExecuteNonQuery();
            }
            tx.Commit();
            _read.Transaction = null;
            _write.Transaction = null;
        }

        public long RangeScan(int startKey, int length)
        {
            long sum = 0;
            _scanStart.Value = (long)startKey;
            _scanN.Value = (long)length;
            using var r = _scan.ExecuteReader();
            while (r.Read()) sum += r.GetInt64(0);
            return sum;
        }

        public void Dispose() { _read?.Dispose(); _write?.Dispose(); _scan?.Dispose(); _conn?.Dispose(); }
    }
}
