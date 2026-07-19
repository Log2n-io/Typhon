using System;
using System.IO;
using System.Numerics;
using Friflo.Engine.ECS;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.CompetitiveBenchmark.Adapters;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Harness;

// Friflo component for the iteration comparison.
file struct C8Val : IComponent
{
    public long V;
}

/// <summary>
/// C8 — ECS tick-loop iteration: read+mutate one field across N entities. This is a STANDALONE CAPABILITY, not a
/// head-to-head win (plan §7-C8). Typhon's cluster SoA scan and Friflo's archetype query share the SoA primitive;
/// SQLite's full-table UPDATE is a *labelled proxy* (a different primitive — a transactional set-update), included only
/// to contextualize the order-of-magnitude, never called a "win".
/// </summary>
public static class C8Runner
{
    public static void Run(int count = 200_000)
    {
        Console.WriteLine($"C8 ECS iteration — read+mutate one field × {count:N0} entities, ns/entity (standalone capability)");
        Console.WriteLine(new string('─', 72));

        double typhon = MeasureTyphon(count);
        double friflo = MeasureFriflo(count);
        double sqlite = MeasureSqlite(count);

        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"{"Engine",-40} {"ns/entity",12} {"M entities/s",14}");
        Console.WriteLine(new string('─', 72));
        Row("Typhon cluster SoA scan", typhon);
        Row("Friflo archetype query (SoA)", friflo);
        Row("SQLite UPDATE full-table (proxy, diff. primitive)", sqlite);
        Console.WriteLine(new string('─', 72));
        Console.WriteLine("SQLite is a different primitive (transactional set-update over rows) — context, not a head-to-head.");

        static void Row(string name, double ns) =>
            Console.WriteLine($"{name,-40} {Measure.FormatNs(ns),12} {1000.0 / ns,14:0.0}");
    }

    private static double MeasureTyphon(int count)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
          .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"c8_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(32768 * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())
          .AddScopedDatabaseEngine(o => { o.Wal = new WalWriterOptions { UseFUA = false }; o.Resources.CheckpointIntervalMs = int.MaxValue; });
        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvVal>();
        dbe.RegisterComponentFromAccessor<VVal>();
        dbe.InitializeArchetypes();

        const int LoadBatch = 8192;
        var t = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            var c = new SvVal { Value = i };
            t.Spawn<SvValArch>(SvValArch.Data.Set(in c));
            if ((i + 1) % LoadBatch == 0) { t.Commit(); t.Dispose(); t = dbe.CreateQuickTransaction(); }
        }
        t.Commit();
        t.Dispose();
        dbe.WriteTickFence(1);

        double ns = Measure.NsPerOp(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var acc = tx.For<SvValArch>();
            foreach (var cluster in acc.GetClusterEnumerator())
            {
                var span = cluster.GetSpan<SvVal>(SvValArch.Data);
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int idx = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    span[idx].Value += 1;
                }
            }
            acc.Dispose();
        }, opsPerBatch: count, warmupBatches: 20, minMs: 500);

        dbe.Dispose();
        try { File.Delete($"c8_{Environment.ProcessId}.bin"); } catch { }
        return ns;
    }

    private static double MeasureFriflo(int count)
    {
        var store = new EntityStore();
        for (int i = 0; i < count; i++)
        {
            var e = store.CreateEntity();
            e.AddComponent(new C8Val { V = i });
        }
        var query = store.Query<C8Val>();

        return Measure.NsPerOp(() =>
        {
            query.ForEachEntity((ref C8Val c, Entity _) => c.V += 1);
        }, opsPerBatch: count, warmupBatches: 20, minMs: 500);
    }

    private static double MeasureSqlite(int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), "typhon-c8-sqlite");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "c8.db");
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) { try { File.Delete(f); } catch { } }

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, "PRAGMA synchronous=OFF;");
        Exec(conn, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER) STRICT;");
        using (var tx = conn.BeginTransaction())
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO t(id,v) VALUES($id,$v)";
            var pid = ins.CreateParameter(); pid.ParameterName = "$id"; ins.Parameters.Add(pid);
            var pv = ins.CreateParameter(); pv.ParameterName = "$v"; ins.Parameters.Add(pv);
            ins.Prepare();
            for (int i = 0; i < count; i++) { pid.Value = (long)i; pv.Value = (long)i; ins.ExecuteNonQuery(); }
            tx.Commit();
        }

        using var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE t SET v = v + 1";
        upd.Prepare();

        return Measure.NsPerOp(() => upd.ExecuteNonQuery(), opsPerBatch: count, warmupBatches: 3, minMs: 500);

        static void Exec(SqliteConnection cn, string sql)
        {
            using var c = cn.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }
}
