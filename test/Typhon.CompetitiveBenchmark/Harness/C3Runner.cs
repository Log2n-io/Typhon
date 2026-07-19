using System;
using System.Buffers.Binary;
using System.IO;
using LightningDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RocksDbSharp;
using Typhon.CompetitiveBenchmark.Adapters;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// C3 — cross-component (cross-table) atomic commit. One logical commit atomically mutates THREE values belonging to one
/// entity. Typhon (SV-Committed) emits ONE WAL record covering all three; the competitors do an N-key/N-statement atomic
/// batch. Measures the per-commit CPU overhead of atomicity (no-fsync everywhere so the disk floor isn't the variable).
/// </summary>
public static class C3Runner
{
    public static void Run(int count = 200_000)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-c3");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"C3 — cross-component atomic commit (3 values / entity) — {count:N0} entities, no-fsync, single-thread, Zen 4");
        Console.WriteLine(new string('─', 78));

        double typhon = MeasureTyphon(count);
        double sqlite = MeasureSqlite(root, count);
        double rocks = MeasureRocks(root, count);
        double lmdb = MeasureLmdb(root, count);

        Console.WriteLine(new string('─', 78));
        Console.WriteLine($"{"Engine",-40} {"ns/commit",12} {"M commits/s",14}");
        Console.WriteLine(new string('─', 78));
        Row("Typhon SV-Committed (1 WAL record)", typhon);
        Row("SQLite (3-col UPDATE, 1 txn)", sqlite);
        Row("RocksDB (WriteBatch, 3 keys)", rocks);
        Row("LMDB (write txn, 3 keys)", lmdb);
        Console.WriteLine(new string('─', 78));
        Console.WriteLine("All atomic across the 3 values; no-fsync isolates the atomicity machinery. Typhon: one logical-redo record");
        Console.WriteLine("for the whole entity vs the competitors' 3-key batches.");

        static void Row(string name, double ns) =>
            Console.WriteLine($"{name,-40} {Measure.FormatNs(ns),12} {1000.0 / ns,14:0.000}");
    }

    private static double MeasureTyphon(int count)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
          .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o => { o.DatabaseName = $"c3_{Environment.ProcessId}"; o.DatabaseCacheSize = (ulong)(32768 * PagedMMF.PageSize); o.PagesDebugPattern = false; })
          .AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())
          .AddScopedDatabaseEngine(o => { o.Wal = new WalWriterOptions { UseFUA = false }; o.Resources.CheckpointIntervalMs = int.MaxValue; });
        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<T1>();
        dbe.RegisterComponentFromAccessor<T2>();
        dbe.RegisterComponentFromAccessor<T3>();
        dbe.InitializeArchetypes();

        var ids = new EntityId[count];
        const int LoadBatch = 8192;
        var t = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            ids[i] = t.Spawn<TripArch>(TripArch.A.Set(new T1 { V = i }), TripArch.B.Set(new T2 { V = i }), TripArch.C.Set(new T3 { V = i }));
            if ((i + 1) % LoadBatch == 0) { t.Commit(); t.Dispose(); t = dbe.CreateQuickTransaction(); }
        }
        t.Commit();
        t.Dispose();
        dbe.WriteTickFence(1);

        int k = 0;
        long val = 1;
        double ns = Measure.NsPerOp(() =>
        {
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, DurabilityDiscipline.Commit);
            var e = tx.OpenMut(ids[k]);
            e.Write(TripArch.A).V = val;
            e.Write(TripArch.B).V = val;
            e.Write(TripArch.C).V = val;
            tx.Commit();
            val++;
            if (++k >= count) k = 0;
        }, opsPerBatch: 1, warmupBatches: 2000, minMs: 500);

        dbe.Dispose();
        try { File.Delete($"c3_{Environment.ProcessId}.bin"); } catch { }
        return ns;
    }

    private static double MeasureSqlite(string root, int count)
    {
        var path = Path.Combine(root, "c3.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, "PRAGMA synchronous=OFF;");
        Exec(conn, "CREATE TABLE t(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, c INTEGER) STRICT;");
        using (var tx = conn.BeginTransaction())
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO t VALUES($id,$a,$b,$c)";
            var p = new SqliteParameter[4];
            for (int j = 0; j < 4; j++) { p[j] = ins.CreateParameter(); p[j].ParameterName = new[] { "$id", "$a", "$b", "$c" }[j]; ins.Parameters.Add(p[j]); }
            ins.Prepare();
            for (int i = 0; i < count; i++) { p[0].Value = (long)i; p[1].Value = (long)i; p[2].Value = (long)i; p[3].Value = (long)i; ins.ExecuteNonQuery(); }
            tx.Commit();
        }

        using var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE t SET a=$v, b=$v, c=$v WHERE id=$id"; // 3 columns atomically in one (autocommit) transaction
        var pv = upd.CreateParameter(); pv.ParameterName = "$v"; upd.Parameters.Add(pv);
        var pid = upd.CreateParameter(); pid.ParameterName = "$id"; upd.Parameters.Add(pid);
        upd.Prepare();

        int k = 0;
        long val = 1;
        double ns = Measure.NsPerOp(() => { pv.Value = val++; pid.Value = (long)k; upd.ExecuteNonQuery(); if (++k >= count) k = 0; }, 1, warmupBatches: 2000, minMs: 500);
        return ns;

        static void Exec(SqliteConnection cn, string sql) { using var c = cn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    }

    private static double MeasureRocks(string root, int count)
    {
        var dir = Path.Combine(root, "rocks-c3");
        var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(true), dir);
        var wo = new WriteOptions().SetSync(false);
        Span<byte> key = stackalloc byte[8];
        Span<byte> val = stackalloc byte[8];
        var wb0 = new WriteBatch();
        for (int i = 0; i < count; i++)
        {
            for (int comp = 0; comp < 3; comp++) { BinaryPrimitives.WriteInt64BigEndian(key, (long)i * 3 + comp); BinaryPrimitives.WriteInt64LittleEndian(val, i); wb0.Put(key, val); }
            if ((i + 1) % 16384 == 0) { db.Write(wb0, wo); wb0.Dispose(); wb0 = new WriteBatch(); }
        }
        db.Write(wb0, wo); wb0.Dispose();

        int k = 0;
        long v = 1;
        double ns = Measure.NsPerOp(() =>
        {
            using var wb = new WriteBatch(); // 3 keys, atomic
            Span<byte> kk = stackalloc byte[8];
            Span<byte> vv = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(vv, v++);
            for (int comp = 0; comp < 3; comp++) { BinaryPrimitives.WriteInt64BigEndian(kk, (long)k * 3 + comp); wb.Put(kk, vv); }
            db.Write(wb, wo);
            if (++k >= count) k = 0;
        }, 1, warmupBatches: 2000, minMs: 500);

        db.Dispose();
        try { Directory.Delete(dir, true); } catch { }
        return ns;
    }

    private static double MeasureLmdb(string root, int count)
    {
        var dir = Path.Combine(root, "lmdb-c3");
        Directory.CreateDirectory(dir);
        var env = new LightningEnvironment(dir) { MapSize = 1L * 1024 * 1024 * 1024, MaxDatabases = 1 };
        env.Open(EnvironmentOpenFlags.NoSync);
        LightningDatabase db;
        using (var tx0 = env.BeginTransaction()) { db = tx0.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create }); tx0.Commit(); }
        using (var tx0 = env.BeginTransaction())
        {
            Span<byte> key = stackalloc byte[8];
            Span<byte> val = stackalloc byte[8];
            for (int i = 0; i < count; i++)
            {
                for (int comp = 0; comp < 3; comp++) { BinaryPrimitives.WriteInt64BigEndian(key, (long)i * 3 + comp); BinaryPrimitives.WriteInt64LittleEndian(val, i); tx0.Put(db, key, val); }
            }
            tx0.Commit();
        }

        int k = 0;
        long v = 1;
        double ns = Measure.NsPerOp(() =>
        {
            using var tx = env.BeginTransaction(); // 3 keys, one write txn = atomic
            Span<byte> kk = stackalloc byte[8];
            Span<byte> vv = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(vv, v++);
            for (int comp = 0; comp < 3; comp++) { BinaryPrimitives.WriteInt64BigEndian(kk, (long)k * 3 + comp); tx.Put(db, kk, vv); }
            tx.Commit();
            if (++k >= count) k = 0;
        }, 1, warmupBatches: 2000, minMs: 500);

        db.Dispose();
        env.Dispose();
        try { Directory.Delete(dir, true); } catch { }
        return ns;
    }
}
