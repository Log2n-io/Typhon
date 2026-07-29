using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Typhon.Benchmark;

/// <summary>
/// QPC profile of the three ingest routes across SCALE — the question BenchmarkDotNet could not answer here.
/// <para>
/// <b>Why not BDN.</b> Each sample needs a fresh engine (BulkLoad is exclusive per engine, and no route rolls back), so
/// the BDN form is <c>invocationCount:1</c> with a full engine build in <c>IterationSetup</c>. That produced ±20 µs of
/// error on a 2.3 µs mean — the engine construction, page-cache state and GC between samples dominate everything the
/// benchmark is trying to see. A handful of explicit runs with the median reported is both faster and far more honest.
/// </para>
/// <para>
/// <b>Why scale is the variable.</b> Typhon's WAL writer batches and flushes asynchronously, so a per-row WAL record
/// costs a buffer memcpy, not a disk write. At small N that makes BulkLoad's "skip per-row WAL" saving nearly invisible
/// while its one-time forced checkpoint is charged in full. BulkLoad's real win is WAL *volume* — and volume only starts
/// to hurt once it drives page-cache backpressure and checkpoint pressure, which is a large-N phenomenon. The Workbench
/// fixture generator (tools/Typhon.Workbench.Fixtures/FixtureDatabase.cs) builds multi-million-entity databases; that is
/// the regime this profile has to reach before it can claim anything.
/// </para>
/// <para>
/// Every route runs against a REAL on-disk WAL. Against the suite's usual in-memory WAL the per-row records are almost
/// free, which silently deletes the entire effect under test.
/// </para>
/// Run: <c>dotnet run -c Release -- --bulk-load [sizes...]</c>
/// </summary>
public static class BulkLoadProfile
{
    private const int CommitEvery = 8192;
    private const int Repeats = 3;

    public static void Run(string[] args)
    {
        var sizes = ParseSizes(args);

        Console.WriteLine("BULK INGEST PROFILE — three routes into a fresh database, real on-disk WAL");
        Console.WriteLine($"median of {Repeats} runs; engine build/teardown excluded from timing");
        Console.WriteLine(new string('─', 92));
        Console.WriteLine($"{"entities",12}{"route",-22}{"total ms",12}{"ns/entity",14}{"vs per-tx",12}");
        Console.WriteLine(new string('─', 92));

        foreach (int n in sizes)
        {
            double naive = Median(n, Route.PerEntityCommit);
            double perTx = Median(n, Route.PerTransaction);
            double batch = Median(n, Route.SpawnBatch);
            double bulk = Median(n, Route.BulkLoad);

            Print(n, "naive (commit/entity)", naive, perTx);
            Print(n, "per-transaction", perTx, perTx);
            Print(n, "SpawnBatch", batch, perTx);
            Print(n, "BulkLoad", bulk, perTx);
            Console.WriteLine(new string('─', 92));
        }
    }

    private static void Print(int n, string label, double ms, double baselineMs)
    {
        double nsPer = ms * 1_000_000.0 / n;
        string ratio = Math.Abs(ms - baselineMs) < 1e-9 ? "baseline" : $"{baselineMs / ms:0.00}× faster";
        Console.WriteLine($"{n,12:N0}{label,-22}{ms,12:0.0}{nsPer,14:0.0}{ratio,12}");
    }

    private enum Route { PerEntityCommit, PerTransaction, SpawnBatch, BulkLoad }

    private static double Median(int n, Route route)
    {
        var samples = new double[Repeats];
        for (int r = 0; r < Repeats; r++)
        {
            samples[r] = RunOnce(n, route, $"blp_{route}_{n}_{r}");
        }
        Array.Sort(samples);
        return samples[Repeats / 2];
    }

    private static double RunOnce(int n, Route route, string name)
    {
        var walDir = Path.Combine(Path.GetTempPath(), "typhon-blp-wal", name);
        Directory.CreateDirectory(walDir);
        var sp = Build(name, walDir);
        try
        {
            var dbe = sp.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AaBenchPosition>();
            dbe.RegisterComponentFromAccessor<AaBenchMovement>();
            dbe.InitializeArchetypes();

            var sw = Stopwatch.StartNew();
            switch (route)
            {
                case Route.PerEntityCommit: PerEntityCommit(dbe, n); break;
                case Route.PerTransaction: PerTransaction(dbe, n); break;
                case Route.SpawnBatch: SpawnBatch(dbe, n); break;
                default: BulkLoad(dbe, n); break;
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        finally
        {
            sp.GetService<DatabaseEngine>()?.Dispose();
            sp.Dispose();
            try { Directory.Delete(walDir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The naive loader: one transaction per entity. This is the shape a first-cut ingest usually takes, and the most
    /// likely baseline behind BulkLoad's original "≥3× speedup" acceptance criterion — a claim only meaningful relative
    /// to whatever it was compared against. Included so that comparison is explicit rather than assumed.
    /// </summary>
    private static void PerEntityCommit(DatabaseEngine dbe, int n)
    {
        for (int i = 0; i < n; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = new AaBenchPosition(i, i);
            var mov = new AaBenchMovement(1, 1);
            tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
            tx.Commit();
        }
        dbe.WriteTickFence(1);
    }

    private static void PerTransaction(DatabaseEngine dbe, int n)
    {
        var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < n; i++)
        {
            var pos = new AaBenchPosition(i, i);
            var mov = new AaBenchMovement(1, 1);
            tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
            if ((i + 1) % CommitEvery == 0)
            {
                tx.Commit();
                tx.Dispose();
                tx = dbe.CreateQuickTransaction();
            }
        }
        tx.Commit();
        tx.Dispose();
        dbe.WriteTickFence(1);
    }

    private static void SpawnBatch(DatabaseEngine dbe, int n)
    {
        var pos = new AaBenchPosition(1, 1);
        var mov = new AaBenchMovement(1, 1);
        var ids = new EntityId[CommitEvery];
        int done = 0;
        while (done < n)
        {
            int batch = Math.Min(CommitEvery, n - done);
            using var tx = dbe.CreateQuickTransaction();
            tx.SpawnBatch<AaBenchAnt>(ids.AsSpan(0, batch), AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
            tx.Commit();
            done += batch;
        }
        dbe.WriteTickFence(1);
    }

    private static void BulkLoad(DatabaseEngine dbe, int n)
    {
        using var bulk = dbe.BeginBulkLoad();
        for (int i = 0; i < n; i++)
        {
            var pos = new AaBenchPosition(i, i);
            var mov = new AaBenchMovement(1, 1);
            bulk.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
        }
        bulk.CompleteBulkLoad();   // included: it is what makes a bulk durable, since the per-row WAL was skipped
    }

    private static ServiceProvider Build(string name, string walDir)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"{name}_{Environment.ProcessId}";
              // 200K pages = 1.6 GiB. Do NOT raise past ~256K: the byte size overflows Int32 and the engine rejects it.
              o.DatabaseCacheSize = (ulong)(200L * 1024 * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine(o =>
          {
              o.Wal = new WalWriterOptions
              {
                  WalDirectory = walDir,
                  UseFUA = false,           // fsync-to-cache: the variable here is WAL volume, not the power-safe tier
                  SegmentSize = 16 * 1024 * 1024,
                  PreAllocateSegments = 2
              };
              o.Resources.CheckpointIntervalMs = int.MaxValue;
          });

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        return sp;
    }

    private static int[] ParseSizes(string[] args)
    {
        var parsed = new System.Collections.Generic.List<int>();
        foreach (var a in args)
        {
            if (int.TryParse(a, out int v) && v > 0)
            {
                parsed.Add(v);
            }
        }
        return parsed.Count > 0 ? parsed.ToArray() : [100_000, 500_000, 2_000_000];
    }
}
