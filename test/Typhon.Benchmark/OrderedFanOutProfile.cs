using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Typhon.Benchmark;

// -------------------------------------------------------------------------------------------------------------------
// Ordered-query fan-out profile - the harness behind claude/design/Indexing/parkable-cursor-report.md.
//
// Each (K, scenario) pair gets its OWN call site. That is not cosmetic: EcsQuery captures [CallerFilePath] /
// [CallerLineNumber] and the plan cache is keyed on them, so routing four Ks through one generic helper would make
// them share a plan-cache entry and measure cache thrash instead of the merge.
//
// Run: dotnet run -c Release -- --profile-fanout [--label <name>] [--iterations <n>]
// -------------------------------------------------------------------------------------------------------------------

public static class OrderedFanOutProfile
{
    private const int TotalEntities = 65_536;
    private const int DistinctScores = 4_096;      // -> 16 entities per Score value, so the AllowMultiple path really uses its value buffers
    private const int WarmupRuns = 30;

    private static readonly int[] Ks = [1, 4, 16, 64];
    private static readonly string[] ScenarioNames = ["UniqueTake100", "MultiTake100", "MultiSkip2000T50", "UniqueFull"];

    public static void Run(string[] args)
    {
        var label = ArgValue(args, "--label") ?? "run";
        var iterations = int.TryParse(ArgValue(args, "--iterations"), out var it) ? it : 300;

        Console.WriteLine($"Ordered fan-out profile - label='{label}', {TotalEntities} entities per tree, {iterations} iterations");
        Console.WriteLine();

        var databaseName = $"OrderedFanOut_{Environment.ProcessId}";
        var dcs = 200 * 1024;
        dcs *= PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = databaseName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<FoData>();
        dbe.InitializeArchetypes();

        var swSetup = Stopwatch.StartNew();
        foreach (var k in Ks)
        {
            Populate(dbe, k);
        }

        swSetup.Stop();
        Console.WriteLine($"Populated 4 trees x {TotalEntities} entities in {swSetup.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();

        var rows = new List<Row>();
        for (var s = 0; s < ScenarioNames.Length; s++)
        {
            foreach (var k in Ks)
            {
                rows.Add(Measure(dbe, k, s, iterations));
            }
        }

        Report(rows, label, iterations);

        dbe.Dispose();
        try { File.Delete($"{databaseName}.bin"); } catch (IOException) { /* best effort */ }
        try { File.Delete($"{databaseName}.lock"); } catch (IOException) { /* best effort */ }
    }

    private readonly struct Row(string scenario, int k, double meanUs, double medianUs, double minUs, int resultCount)
    {
        public string Scenario { get; } = scenario;
        public int K { get; } = k;
        public double MeanUs { get; } = meanUs;
        public double MedianUs { get; } = medianUs;
        public double MinUs { get; } = minUs;
        public int ResultCount { get; } = resultCount;
    }

    private static Row Measure(DatabaseEngine dbe, int k, int scenario, int iterations)
    {
        for (var w = 0; w < WarmupRuns; w++)
        {
            Dispatch(dbe, k, scenario);
        }

        var samples = new double[iterations];
        var count = 0;
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            count = Dispatch(dbe, k, scenario);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds * 1000.0;
        }

        Array.Sort(samples);
        var sum = 0.0;
        foreach (var v in samples)
        {
            sum += v;
        }

        var row = new Row(ScenarioNames[scenario], k, sum / iterations, samples[iterations / 2], samples[0], count);
        Console.WriteLine($"  {row.Scenario,-18} K={k,-3} mean={row.MeanUs,10:F1}us  median={row.MedianUs,10:F1}us  min={row.MinUs,10:F1}us"
            + $"  n={row.ResultCount}");
        return row;
    }

    private static int Dispatch(DatabaseEngine dbe, int k, int scenario) => k switch
    {
        1 => scenario switch
        {
            0 => K1_UniqueTake100(dbe),
            1 => K1_MultiTake100(dbe),
            2 => K1_MultiSkip2000T50(dbe),
            3 => K1_UniqueFull(dbe),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        },
        4 => scenario switch
        {
            0 => K4_UniqueTake100(dbe),
            1 => K4_MultiTake100(dbe),
            2 => K4_MultiSkip2000T50(dbe),
            3 => K4_UniqueFull(dbe),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        },
        16 => scenario switch
        {
            0 => K16_UniqueTake100(dbe),
            1 => K16_MultiTake100(dbe),
            2 => K16_MultiSkip2000T50(dbe),
            3 => K16_UniqueFull(dbe),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        },
        64 => scenario switch
        {
            0 => K64_UniqueTake100(dbe),
            1 => K64_MultiTake100(dbe),
            2 => K64_MultiSkip2000T50(dbe),
            3 => K64_UniqueFull(dbe),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        },
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

    private static int K1_UniqueTake100(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK1Root>().WhereField<FoData>(d => d.UKey >= 0).OrderByField<FoData, int>(d => d.UKey).Take(100).ExecuteOrdered().Count;
    }

    private static int K1_MultiTake100(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK1Root>().WhereField<FoData>(d => d.Score >= 0).OrderByField<FoData, int>(d => d.Score).Take(100).ExecuteOrdered().Count;
    }

    private static int K1_MultiSkip2000T50(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK1Root>().WhereField<FoData>(d => d.Score >= 0).OrderByField<FoData, int>(d => d.Score).Skip(2000).Take(50).ExecuteOrdered().Count;
    }

    private static int K1_UniqueFull(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK1Root>().WhereField<FoData>(d => d.UKey >= 0).OrderByField<FoData, int>(d => d.UKey).ExecuteOrdered().Count;
    }

    private static int K4_UniqueTake100(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK4Root>().WhereField<FoData>(d => d.UKey >= 0).OrderByField<FoData, int>(d => d.UKey).Take(100).ExecuteOrdered().Count;
    }

    private static int K4_MultiTake100(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK4Root>().WhereField<FoData>(d => d.Score >= 0).OrderByField<FoData, int>(d => d.Score).Take(100).ExecuteOrdered().Count;
    }

    private static int K4_MultiSkip2000T50(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK4Root>().WhereField<FoData>(d => d.Score >= 0).OrderByField<FoData, int>(d => d.Score).Skip(2000).Take(50).ExecuteOrdered().Count;
    }

    private static int K4_UniqueFull(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK4Root>().WhereField<FoData>(d => d.UKey >= 0).OrderByField<FoData, int>(d => d.UKey).ExecuteOrdered().Count;
    }

    private static int K16_UniqueTake100(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK16Root>().WhereField<FoData>(d => d.UKey >= 0).OrderByField<FoData, int>(d => d.UKey).Take(100).ExecuteOrdered().Count;
    }

    private static int K16_MultiTake100(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK16Root>().WhereField<FoData>(d => d.Score >= 0).OrderByField<FoData, int>(d => d.Score).Take(100).ExecuteOrdered().Count;
    }

    private static int K16_MultiSkip2000T50(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK16Root>().WhereField<FoData>(d => d.Score >= 0).OrderByField<FoData, int>(d => d.Score).Skip(2000).Take(50).ExecuteOrdered().Count;
    }

    private static int K16_UniqueFull(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK16Root>().WhereField<FoData>(d => d.UKey >= 0).OrderByField<FoData, int>(d => d.UKey).ExecuteOrdered().Count;
    }

    private static int K64_UniqueTake100(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK64Root>().WhereField<FoData>(d => d.UKey >= 0).OrderByField<FoData, int>(d => d.UKey).Take(100).ExecuteOrdered().Count;
    }

    private static int K64_MultiTake100(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK64Root>().WhereField<FoData>(d => d.Score >= 0).OrderByField<FoData, int>(d => d.Score).Take(100).ExecuteOrdered().Count;
    }

    private static int K64_MultiSkip2000T50(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK64Root>().WhereField<FoData>(d => d.Score >= 0).OrderByField<FoData, int>(d => d.Score).Skip(2000).Take(50).ExecuteOrdered().Count;
    }

    private static int K64_UniqueFull(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<FoK64Root>().WhereField<FoData>(d => d.UKey >= 0).OrderByField<FoData, int>(d => d.UKey).ExecuteOrdered().Count;
    }

    /// <summary>
    /// Fills one K-tree. Entities go round-robin across the tree's archetypes while UKey/Score climb monotonically, so every archetype's key range spans the
    /// whole domain - the shape in which range-disjointness pruning can never fire.
    /// </summary>
    private static void Populate(DatabaseEngine dbe, int k)
    {
        const int batch = 4_096;
        var spawned = 0;
        while (spawned < TotalEntities)
        {
            using var tx = dbe.CreateQuickTransaction();
            var end = Math.Min(spawned + batch, TotalEntities);
            for (var i = spawned; i < end; i++)
            {
                // UKey climbs with the spawn order and Score does not. That is the whole point of having both: the entity a B+Tree entry points at is found
                // through a cluster chunk lookup, and whether that lookup hits the accessor's warm window depends entirely on whether key order happens to
                // match cluster-slot order.
                //   UKey  - correlated:   scanning the index walks the cluster front to back, every lookup warm. The friendly case, and the rarer one.
                //   Score - uncorrelated: consecutive index entries land on cluster chunks all over the archetype, so the lookups go cold. This is what an
                //                         ordinary "top N by score" does, and what the design's cost model assumes (~65 ns cold vs ~9 ns warm).
                // Measuring only the correlated case would have hidden most of the cost of resolving rows the query never emits.
                var d = new FoData(i, (int)((uint)i * 2654435761u % DistinctScores));
                _ = Spawn(tx, k, i % k, in d);
            }

            tx.Commit();
            spawned = end;
        }
    }

    private static EntityId Spawn(Transaction tx, int k, int index, in FoData d) => k switch
    {
        1 => OrderedFanOutSchema.SpawnK1(tx, index, in d),
        4 => OrderedFanOutSchema.SpawnK4(tx, index, in d),
        16 => OrderedFanOutSchema.SpawnK16(tx, index, in d),
        64 => OrderedFanOutSchema.SpawnK64(tx, index, in d),
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

    private static void Report(List<Row> rows, string label, int iterations)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "profiling");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"fanout-{label}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("scenario,k,mean_us,median_us,min_us,result_count");
        foreach (var r in rows)
        {
            sb.Append(r.Scenario).Append(',').Append(r.K).Append(',')
              .Append(r.MeanUs.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.MedianUs.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.MinUs.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.ResultCount).AppendLine();
        }

        File.WriteAllText(path, sb.ToString());

        Console.WriteLine();
        Console.WriteLine("| scenario | K=1 | K=4 | K=16 | K=64 | K=64/K=1 |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");
        foreach (var name in ScenarioNames)
        {
            var line = new StringBuilder();
            line.Append("| ").Append(name).Append(' ');
            double first = 0, last = 0;
            foreach (var k in Ks)
            {
                var r = rows.Find(x => x.Scenario == name && x.K == k);
                if (k == 1)
                {
                    first = r.MedianUs;
                }

                last = r.MedianUs;
                line.Append("| ").Append(r.MedianUs.ToString("F1", CultureInfo.InvariantCulture)).Append(" us ");
            }

            line.Append("| ").Append((last / first).ToString("F1", CultureInfo.InvariantCulture)).Append("x |");
            Console.WriteLine(line.ToString());
        }

        Console.WriteLine();
        Console.WriteLine($"Medians over {iterations} iterations. Written to {path}");
    }

    private static string ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
