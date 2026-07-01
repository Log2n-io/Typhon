using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RocksDbSharp;
using Typhon.CompetitiveBenchmark.Adapters;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// C5 — spatial AABB queries. N random points in a 10k×10k world; Q fixed-size query boxes (same data + same queries for
/// every engine). Typhon uses its NATIVE cluster spatial grid; SQLite uses the R*Tree virtual table; RocksDB EMULATES with a
/// Morton/Z-order key + range scan + box filter (the over-fetch + decode cost is part of its measured workload — stated
/// honestly). FASTER/LMDB are N/A for the emulation (LMDB could do Morton but it's the same pattern as RocksDB — omitted).
/// Reports queries/s and average hits/query (the hit counts are cross-checked for correctness).
/// </summary>
public static class C5Runner
{
    private const int World = 10000;
    private const int QueryBox = 200; // 200×200 box ≈ 0.04% of the world

    public static void Run(int count = 200_000, int queries = 20_000)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-c5");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        // Shared data + shared query workload (identical across engines).
        var px = new int[count];
        var py = new int[count];
        var rng = new Rng(12345);
        for (int i = 0; i < count; i++) { px[i] = (int)(rng.Next() % World); py[i] = (int)(rng.Next() % World); }
        var qx = new int[queries];
        var qy = new int[queries];
        var qrng = new Rng(98765);
        for (int i = 0; i < queries; i++) { qx[i] = (int)(qrng.Next() % World); qy[i] = (int)(qrng.Next() % World); }

        Console.WriteLine($"C5 — spatial AABB queries — {count:N0} points in {World}×{World}, {queries:N0} queries of {QueryBox}×{QueryBox}, single-thread, Zen 4");
        Console.WriteLine(new string('─', 84));

        var (tQps, tHits) = MeasureTyphon(px, py, qx, qy, count, queries);
        var (sQps, sHits) = MeasureSqlite(root, px, py, qx, qy, count, queries);
        var (rQps, rHits) = MeasureRocks(root, px, py, qx, qy, count, queries);

        Console.WriteLine(new string('─', 84));
        Console.WriteLine($"{"Engine",-40} {"K queries/s",14} {"avg hits/query",16}");
        Console.WriteLine(new string('─', 84));
        Row("Typhon native spatial grid", tQps, tHits);
        Row("SQLite R*Tree", sQps, sHits);
        Row("RocksDB Morton/Z-order (emulation)", rQps, rHits);
        Console.WriteLine(new string('─', 84));
        Console.WriteLine("avg hits/query should match across engines (correctness). RocksDB emulation over-fetches the Z-curve then");
        Console.WriteLine("filters — that decode+filter cost is part of its workload. FASTER N/A (hash, no range scan).");

        static void Row(string name, double qps, double hits) =>
            Console.WriteLine($"{name,-40} {qps / 1000.0,14:0.00} {hits,16:0.0}");
    }

    private static (double qps, double avgHits) MeasureTyphon(int[] px, int[] py, int[] qx, int[] qy, int count, int queries)
    {
        Archetype<SpArch>.Touch();
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
          .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o => { o.DatabaseName = $"c5_{Environment.ProcessId}"; o.DatabaseCacheSize = (ulong)(65536 * PagedMMF.PageSize); o.PagesDebugPattern = false; })
          .AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())
          .AddScopedDatabaseEngine(o => { o.Wal = new WalWriterOptions { UseFUA = false }; o.Resources.CheckpointIntervalMs = int.MaxValue; });
        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(new Vector2(0, 0), new Vector2(World, World), cellSize: 100f));
        dbe.InitializeArchetypes();

        const int LoadBatch = 8192;
        var t = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            var pos = new SpPos { Bounds = new AABB2F { MinX = px[i], MinY = py[i], MaxX = px[i], MaxY = py[i] } };
            t.Spawn<SpArch>(SpArch.Pos.Set(in pos));
            if ((i + 1) % LoadBatch == 0) { t.Commit(); t.Dispose(); t = dbe.CreateQuickTransaction(); }
        }
        t.Commit();
        t.Dispose();
        dbe.WriteTickFence(1);

        long hits = 0;
        // warmup
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var box = new AABB2F { MinX = qx[0], MinY = qy[0], MaxX = qx[0] + QueryBox, MaxY = qy[0] + QueryBox };
            foreach (var hit in dbe.ClusterSpatialQuery<SpArch>().AABB<AABB2F>(in box)) { hits += hit.EntityId; }
        }

        hits = 0;
        long found = 0;
        var sw = Stopwatch.StartNew();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            for (int i = 0; i < queries; i++)
            {
                var box = new AABB2F { MinX = qx[i], MinY = qy[i], MaxX = qx[i] + QueryBox, MaxY = qy[i] + QueryBox };
                foreach (var hit in dbe.ClusterSpatialQuery<SpArch>().AABB<AABB2F>(in box)) { found++; hits += hit.EntityId; }
            }
        }
        sw.Stop();
        GC.KeepAlive(hits);

        dbe.Dispose();
        try { File.Delete($"c5_{Environment.ProcessId}.bin"); } catch { }
        return (queries / sw.Elapsed.TotalSeconds, (double)found / queries);
    }

    private static (double qps, double avgHits) MeasureSqlite(string root, int[] px, int[] py, int[] qx, int[] qy, int count, int queries)
    {
        var path = Path.Combine(root, "c5.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, "PRAGMA synchronous=OFF;");
        Exec(conn, "CREATE VIRTUAL TABLE spatial USING rtree(id, minX, maxX, minY, maxY);");
        using (var tx = conn.BeginTransaction())
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO spatial(id,minX,maxX,minY,maxY) VALUES($id,$x,$x,$y,$y)";
            var pid = ins.CreateParameter(); pid.ParameterName = "$id"; ins.Parameters.Add(pid);
            var pxx = ins.CreateParameter(); pxx.ParameterName = "$x"; ins.Parameters.Add(pxx);
            var pyy = ins.CreateParameter(); pyy.ParameterName = "$y"; ins.Parameters.Add(pyy);
            ins.Prepare();
            for (int i = 0; i < count; i++) { pid.Value = (long)i; pxx.Value = (double)px[i]; pyy.Value = (double)py[i]; ins.ExecuteNonQuery(); }
            tx.Commit();
        }

        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT count(*) FROM spatial WHERE minX<=$qxMax AND maxX>=$qxMin AND minY<=$qyMax AND maxY>=$qyMin";
        var a = sel.CreateParameter(); a.ParameterName = "$qxMax"; sel.Parameters.Add(a);
        var b = sel.CreateParameter(); b.ParameterName = "$qxMin"; sel.Parameters.Add(b);
        var c = sel.CreateParameter(); c.ParameterName = "$qyMax"; sel.Parameters.Add(c);
        var d = sel.CreateParameter(); d.ParameterName = "$qyMin"; sel.Parameters.Add(d);
        sel.Prepare();

        long found = 0;
        sel.Parameters[0].Value = (double)(qx[0] + QueryBox); sel.Parameters[1].Value = (double)qx[0]; sel.Parameters[2].Value = (double)(qy[0] + QueryBox); sel.Parameters[3].Value = (double)qy[0];
        sel.ExecuteScalar(); // warmup

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < queries; i++)
        {
            a.Value = (double)(qx[i] + QueryBox); b.Value = (double)qx[i]; c.Value = (double)(qy[i] + QueryBox); d.Value = (double)qy[i];
            found += (long)sel.ExecuteScalar();
        }
        sw.Stop();
        return (queries / sw.Elapsed.TotalSeconds, (double)found / queries);

        static void Exec(SqliteConnection cn, string sql) { using var cm = cn.CreateCommand(); cm.CommandText = sql; cm.ExecuteNonQuery(); }
    }

    private static (double qps, double avgHits) MeasureRocks(string root, int[] px, int[] py, int[] qx, int[] qy, int count, int queries)
    {
        var dir = Path.Combine(root, "rocks-c5");
        var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(true), dir);
        var wo = new WriteOptions().SetSync(false);
        var wb = new WriteBatch();
        Span<byte> key = stackalloc byte[8];
        Span<byte> val = stackalloc byte[8];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(key, Morton((uint)px[i], (uint)py[i]));
            BinaryPrimitives.WriteInt64LittleEndian(val, ((long)px[i] << 32) | (uint)py[i]); // pack coords to filter on read
            wb.Put(key, val);
            if ((i + 1) % 16384 == 0) { db.Write(wb, wo); wb.Dispose(); wb = new WriteBatch(); }
        }
        db.Write(wb, wo); wb.Dispose();

        long found = 0;
        var sw = Stopwatch.StartNew();
        Span<byte> lo = stackalloc byte[8];
        for (int i = 0; i < queries; i++)
        {
            int xMin = qx[i], yMin = qy[i], xMax = qx[i] + QueryBox, yMax = qy[i] + QueryBox;
            ulong zHi = Morton((uint)xMax, (uint)yMax);
            BinaryPrimitives.WriteUInt64BigEndian(lo, Morton((uint)xMin, (uint)yMin));
            using var it = db.NewIterator();
            it.Seek(lo);
            while (it.Valid())
            {
                ulong z = BinaryPrimitives.ReadUInt64BigEndian(it.GetKeySpan());
                if (z > zHi) break;
                var (dx, dy) = Demort(z);                       // decode + box filter — the over-fetch cost
                if (dx >= xMin && dx <= xMax && dy >= yMin && dy <= yMax) found++;
                it.Next();
            }
        }
        sw.Stop();

        db.Dispose();
        try { Directory.Delete(dir, true); } catch { }
        return (queries / sw.Elapsed.TotalSeconds, (double)found / queries);
    }

    // Morton (Z-order) encode/decode for 2D.
    private static ulong Part1By1(ulong x)
    {
        x &= 0xFFFFFFFF;
        x = (x | (x << 16)) & 0x0000FFFF0000FFFF;
        x = (x | (x << 8)) & 0x00FF00FF00FF00FF;
        x = (x | (x << 4)) & 0x0F0F0F0F0F0F0F0F;
        x = (x | (x << 2)) & 0x3333333333333333;
        x = (x | (x << 1)) & 0x5555555555555555;
        return x;
    }
    private static uint Compact1By1(ulong x)
    {
        x &= 0x5555555555555555;
        x = (x | (x >> 1)) & 0x3333333333333333;
        x = (x | (x >> 2)) & 0x0F0F0F0F0F0F0F0F;
        x = (x | (x >> 4)) & 0x00FF00FF00FF00FF;
        x = (x | (x >> 8)) & 0x0000FFFF0000FFFF;
        x = (x | (x >> 16)) & 0xFFFFFFFF;
        return (uint)x;
    }
    private static ulong Morton(uint x, uint y) => Part1By1(x) | (Part1By1(y) << 1);
    private static (uint x, uint y) Demort(ulong z) => (Compact1By1(z), Compact1By1(z >> 1));

    private struct Rng
    {
        private uint _s;
        public Rng(uint seed) => _s = seed;
        public uint Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
    }
}
