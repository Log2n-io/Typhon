using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// View fan-out profile — decision D3 of claude/design/Querying/ViewSystem/archetype-membership-channel.md (#790).
//
// The question: the proposed archetype membership channel fans out to every registered view on the COMMIT path. ADR-042
// puts the existing per-field channel at "~50-100 views per field before commit-time fan-out becomes noticeable", which
// is an estimate, not a measurement. Before anyone builds a channel with a COARSER key (per archetype rather than per
// indexed field, so more views land on one publisher) that number needs to be real.
//
// What is measured: the existing per-field channel, because it has the identical loop shape — the membership channel
// would replace `ViewRegistry.GetViewsForField(fi)` with a per-archetype array and keep the body. Registering N views
// with the SAME predicate on the SAME indexed field puts all N in one registration array, which is exactly the
// publisher-side geometry the membership channel produces.
//
//   Transaction.ECS.cs:1517-1541  ->  for each registered view: TryAppend(entityId, ..., TSN, flags, tag)
//
// Only Commit() is timed. Views are drained (Refresh) between commits, untimed, so no buffer overflows and every
// TryAppend does real work — an overflowed buffer would cheapen the append and understate the cost.
//
// Every N runs against a fresh database and an identical spawn trajectory, so B+Tree depth is not a hidden variable.
//
// Run: dotnet run -c Release -- --profile-viewfanout [--iterations <n>] [--batch <n>]
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>One AllowMultiple indexed field — the publisher iterates its registration array once per spawned entity.</summary>
[Component("Typhon.Benchmark.VfData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct VfData
{
    [Field] [Index(AllowMultiple = true)] public int Bucket;

    public long Pad;
}

[Archetype]
class VfArch : Archetype<VfArch>
{
    public static readonly Comp<VfData> Data = Register<VfData>();
}

public static class ViewFanOutProfile
{
    private static readonly int[] ViewCounts = [0, 8, 25, 50, 100, 200, 400];
    private const int WarmupCommits = 5;

    public static void Run(string[] args)
    {
        var iterations = int.TryParse(ArgValue(args, "--iterations"), out var it) ? it : 40;
        var batch = int.TryParse(ArgValue(args, "--batch"), out var b) ? b : 500;

        Console.WriteLine($"View fan-out profile — {iterations} timed commits x {batch} spawns each, per view count");
        Console.WriteLine("Measuring: Commit() wall time. Views drained between commits (untimed).");
        Console.WriteLine();

        var rows = new List<Row>();
        foreach (var n in ViewCounts)
        {
            rows.Add(Measure(n, iterations, batch));
            Console.WriteLine($"  N={n,3} done");
        }

        Report(rows, iterations, batch);
    }

    private static Row Measure(int viewCount, int iterations, int batch)
    {
        var databaseName = $"ViewFanOut_{Environment.ProcessId}_{viewCount}";
        var dcs = (ulong)(200 * 1024) * PagedMMF.PageSize;

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
              options.DatabaseCacheSize = dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<VfData>();
        dbe.InitializeArchetypes();

        // Register the views BEFORE any spawn, so every spawned entity is published to all N of them.
        // The creating transaction is deliberately short-lived: production views outlive it (ADR-042), and #862 pins
        // that lifecycle with regression coverage rather than keeping this benchmark on a harness-only workaround.
        var views = new List<EcsView<VfArch>>(viewCount);
        using (var viewTx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < viewCount; i++)
            {
                views.Add(viewTx.Query<VfArch>().WhereField<VfData>(d => d.Bucket >= 0).ToView());
            }
        }

        var samples = new double[iterations];
        var seq = 0;

        for (var iter = 0; iter < WarmupCommits + iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < batch; i++)
            {
                var d = new VfData { Bucket = seq++ };
                tx.Spawn<VfArch>(VfArch.Data.Set(in d));
            }

            var t0 = Stopwatch.GetTimestamp();
            tx.Commit();
            var t1 = Stopwatch.GetTimestamp();

            if (iter >= WarmupCommits)
            {
                samples[iter - WarmupCommits] = (t1 - t0) * 1_000_000.0 / Stopwatch.Frequency;
            }

            // Untimed: drain every view so no ring buffer overflows and every TryAppend keeps doing real work.
            using var drainTx = dbe.CreateQuickTransaction();
            for (var v = 0; v < views.Count; v++)
            {
                views[v].Refresh(drainTx);
                views[v].ClearDelta();
            }
        }

        // Guard against measuring nothing: if the views did not actually receive the entities, the numbers are noise.
        var populated = views.Count == 0 || views[0].Count > 0;
        var observed = views.Count == 0 ? 0 : views[0].Count;

        Array.Sort(samples);
        var median = samples[samples.Length / 2];
        var min = samples[0];
        var mean = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            mean += samples[i];
        }
        mean /= samples.Length;

        foreach (var v in views)
        {
            v.Dispose();
        }
        dbe.Dispose();
        try { File.Delete($"{databaseName}.bin"); } catch (IOException) { /* best effort */ }
        try { File.Delete($"{databaseName}.lock"); } catch (IOException) { /* best effort */ }

        return new Row(viewCount, mean, median, min, batch, populated, observed);
    }

    private readonly struct Row(int views, double meanUs, double medianUs, double minUs, int batch, bool populated, int observed)
    {
        public int Views { get; } = views;
        public double MeanUs { get; } = meanUs;
        public double MedianUs { get; } = medianUs;
        public double MinUs { get; } = minUs;
        public int Batch { get; } = batch;
        public bool Populated { get; } = populated;
        public int Observed { get; } = observed;
    }

    private static void Report(List<Row> rows, int iterations, int batch)
    {
        var baseline = rows[0].MedianUs;

        Console.WriteLine();
        Console.WriteLine($"Commit of {batch} spawns, {iterations} timed iterations, one AllowMultiple indexed field");
        Console.WriteLine();
        Console.WriteLine("| views | median us | mean us | min us | vs N=0 | delta us | ns / entity / view |");
        Console.WriteLine("|------:|----------:|--------:|-------:|-------:|---------:|-------------------:|");

        foreach (var r in rows)
        {
            var delta = r.MedianUs - baseline;
            var perEntityPerView = r.Views == 0 ? 0 : delta * 1000.0 / (batch * (double)r.Views);
            var ratio = baseline > 0 ? r.MedianUs / baseline : 0;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "| {0,5} | {1,9:F1} | {2,7:F1} | {3,6:F1} | {4,5:F2}x | {5,8:F1} | {6,18:F2} |",
                r.Views, r.MedianUs, r.MeanUs, r.MinUs, ratio, delta, perEntityPerView));
        }

        Console.WriteLine();
        foreach (var r in rows)
        {
            if (!r.Populated)
            {
                Console.WriteLine($"!! N={r.Views}: view[0].Count == 0 — the views received nothing, this row is NOISE");
            }
        }

        // Least-squares slope over (views, medianUs). Pairwise deltas are dominated by commit-to-commit variance; the
        // slope uses every row and is what the ceiling question actually asks for.
        double n = rows.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var r in rows)
        {
            sx += r.Views;
            sy += r.MedianUs;
            sxx += (double)r.Views * r.Views;
            sxy += r.Views * r.MedianUs;
        }
        var slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Slope: {0:F3} us per view per commit of {1} spawns  =  {2:F2} ns per entity per view",
            slope, batch, slope * 1000.0 / batch));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Baseline (N=0): {0:F1} us  =  {1:F2} us per entity with no view registered", baseline, baseline / batch));

        var last = rows[^1];
        Console.WriteLine($"Sanity: at N={last.Views}, view[0] holds {last.Observed} entities " +
                          $"(expected {(WarmupCommits + iterations) * batch}).");
    }

    private static string ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
