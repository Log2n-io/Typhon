using BenchmarkDotNet.Running;
using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Typhon.Benchmark.Cli;

namespace Typhon.Benchmark;

class Program
{
    /// <summary>
    /// Combines multiple BenchmarkDotNet JSON result files into a single file.
    /// Useful for aggregating results from multiple benchmark runs.
    /// </summary>
    private static void CombineBenchmarkResults(
        string resultsDir = "./BenchmarkDotNet.Artifacts/results",
        string resultsFile = "Combined.Benchmarks",
        string searchPattern = "*full.json")
    {
        var resultsPath = Path.Combine(resultsDir, resultsFile + ".json");

        if (!Directory.Exists(resultsDir))
            throw new DirectoryNotFoundException($"Directory not found '{resultsDir}'");

        if (File.Exists(resultsPath))
            File.Delete(resultsPath);

        var reports = Directory
            .GetFiles(resultsDir, searchPattern, SearchOption.TopDirectoryOnly)
            .ToArray();

        if (reports.Length == 0)
            throw new FileNotFoundException($"Reports not found '{searchPattern}'");

        var combinedReport = JsonNode.Parse(File.ReadAllText(reports.First()))!;
        var title = combinedReport["Title"]!;
        var benchmarks = combinedReport["Benchmarks"]!.AsArray();

        // Rename title whilst keeping original timestamp
        combinedReport["Title"] = $"{resultsFile}{title.GetValue<string>()[^16..]}";

        foreach (var report in reports.Skip(1))
        {
            var array = JsonNode.Parse(File.ReadAllText(report))!["Benchmarks"]!.AsArray();
            foreach (var benchmark in array)
            {
                // Double parse avoids "The node already has a parent" exception
                benchmarks.Add(JsonNode.Parse(benchmark!.ToJsonString())!);
            }
        }

        File.WriteAllText(resultsPath, combinedReport.ToString());
    }

    static void Main(string[] args)
    {
        // If command-line arguments are provided, use BenchmarkDotNet's built-in handling
        // This preserves backward compatibility with --filter, --list, etc.
        if (args.Length > 0)
        {
            // Handle special commands
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
            {
                PrintHelp();
                return;
            }

            if ((args.Contains("--list") || args.Contains("-l")) && !args.Contains("--allCategories") && !args.Contains("--anyCategories"))
            {
                ListBenchmarks();
                return;
            }

            if (args.Contains("--bandwidth"))
            {
                MemoryBandwidthBenchmark.Run();
                return;
            }

            if (args.Contains("--interest-direct"))
            {
                InterestDirect.Run(args);
                return;
            }

            if (args.Contains("--trigger-direct"))
            {
                TriggerDirect.Run(args);
                return;
            }

            if (args.Contains("--storage-model"))
            {
                StorageModelProfile.Run(args);
                return;
            }

            if (args.Contains("--profile-spawn"))
            {
                SpawnProfile.Run();
                return;
            }

            if (args.Contains("--profile-write-v"))
            {
                WriteProfile.Run(versioned: true);
                return;
            }

            if (args.Contains("--profile-write-sv"))
            {
                WriteProfile.Run(versioned: false);
                return;
            }

            if (args.Contains("--crud-compare"))
            {
                BenchmarkRunner.Run<CrudCompareBenchmarks>();
                return;
            }

            if (args.Contains("--quick-bench"))
            {
                QuickSpawnBench.Run();
                return;
            }

            if (args.Contains("--bulk-load"))
            {
                BulkLoadProfile.Run(args);
                return;
            }

            if (args.Contains("--storage-modes"))
            {
                using var bench = new StorageModeCompareBenchmarks();
                bench.Setup();
                bench.Run();
                return;
            }

            if (args.Contains("--profile-delete"))
            {
                BTreeDeleteProfile.Run();
                return;
            }

            if (args.Contains("--profile-scan"))
            {
                BTreeScanProfile.Run();
                return;
            }

            if (args.Contains("--profile-hashmap"))
            {
                HashMapProfileWorkload.Run();
                return;
            }

            if (args.Contains("--profile-fanout"))
            {
                OrderedFanOutProfile.Run(args);
                return;
            }

            if (args.Contains("--parallel-dispatch"))
            {
                ParallelDispatchBenchmark.Run();
                return;
            }

            if (args.Contains("--aa-bench"))
            {
                ArchetypeAccessorBenchmark.Run();
                return;
            }

            if (args.Contains("--profile-aa-standard"))
            {
                ArchetypeAccessorBenchmark.ProfileStandard();
                return;
            }

            if (args.Contains("--profile-aa-accessor"))
            {
                ArchetypeAccessorBenchmark.ProfileAccessor();
                return;
            }

            if (args.Contains("--aa-scale"))
            {
                ArchetypeAccessorBenchmark.ScaleTest();
                return;
            }

            if (args.Contains("--cluster-bench"))
            {
                ArchetypeAccessorBenchmark.RunCluster();
                return;
            }

            if (args.Contains("--tickfence-bench"))
            {
                ArchetypeAccessorBenchmark.RunTickFenceBench();
                return;
            }

            if (args.Contains("--indexed-bench"))
            {
                ArchetypeAccessorBenchmark.RunIndexedBench();
                return;
            }

            if (args.Contains("--spatial-bench"))
            {
                ArchetypeAccessorBenchmark.RunSpatialBench();
                return;
            }

            if (args.Contains("--versioned-cluster-bench"))
            {
                ArchetypeAccessorBenchmark.RunVersionedCluster();
                return;
            }

            if (args.Contains("--simd-bench"))
            {
                ArchetypeAccessorBenchmark.RunSimdQuery();
                return;
            }

            if (args.Contains("--ordered-bench"))
            {
                ArchetypeAccessorBenchmark.RunOrderedQuery();
                return;
            }

            if (args.Contains("--zonemap-bench"))
            {
                ArchetypeAccessorBenchmark.RunZoneMapPruning();
                return;
            }

            if (args.Contains("--profile-runtime-standard"))
            {
                ArchetypeAccessorBenchmark.ProfileRuntimeStandard();
                return;
            }

            if (args.Contains("--profile-runtime-accessor"))
            {
                ArchetypeAccessorBenchmark.ProfileRuntimeAccessor();
                return;
            }



            // BTree profile shortcuts: --btree-fast, --btree-medium, --btree-full
            // These run curated subsets of BTree benchmarks for quick/medium/full analysis.
            if (args.Contains("--btree-fast"))
            {
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                    .Run(["--allCategories", "BTreeFast", "--exporters", "json"]);
                return;
            }

            if (args.Contains("--btree-medium"))
            {
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                    .Run(["--allCategories", "BTreeMedium", "--exporters", "json"]);
                return;
            }

            if (args.Contains("--btree-full"))
            {
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                    .Run(["--anyCategories", "BTreeFast", "BTreeMedium", "BTreeFull", "--exporters", "json"]);
                return;
            }

            // Pass through to BenchmarkDotNet for standard args like --filter
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            return;
        }

        // No arguments: launch interactive mode
        try
        {
            var runner = new InteractiveBenchmarkRunner(typeof(Program).Assembly);
            runner.Run();
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
    }

    private static void PrintHelp()
    {
        AnsiConsole.Write(new Rule("[cyan]Typhon Benchmark Runner[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[yellow]Option[/]")
            .AddColumn("[yellow]Description[/]");

        table.AddRow("[cyan](no args)[/]", "Launch interactive mode with menu selection");
        table.AddRow("[cyan]--list, -l[/]", "List all available benchmarks");
        table.AddRow("[cyan]--filter <pattern>[/]", "Run benchmarks matching the pattern (e.g., --filter *BTree*)");
        table.AddRow("[cyan]--bandwidth[/]", "Memory bandwidth vs chunk size & indirection (ACLP analysis)");
        table.AddRow("[cyan]--btree-fast[/]", "BTree quick profile: core ops + 2 concurrent (~3 min)");
        table.AddRow("[cyan]--btree-medium[/]", "BTree medium profile: all key types + concurrent scaling (~15 min)");
        table.AddRow("[cyan]--btree-full[/]", "BTree full profile: everything including tree sizes + enumeration (~50 min)");
        table.AddRow("[cyan]--parallel-dispatch[/]", "Parallel dispatch: prepare O(1), PTA vs Transaction, scaling");
        table.AddRow("[cyan]--versioned-cluster-bench[/]", "Mixed SV+Versioned cluster: bulk, random access, write+commit");
        table.AddRow("[cyan]--simd-bench[/]", "SIMD query scan at 1%/10%/50% selectivity (100K entities)");
        table.AddRow("[cyan]--ordered-bench[/]", "Ordered query: Take(100), Skip+Take, full sort (50K entities)");
        table.AddRow("[cyan]--zonemap-bench[/]", "Zone map pruning at 0.1%/1%/10%/50% selectivity (100K entities)");
        table.AddRow("[cyan]--help, -h, -?[/]", "Show this help message");
        table.AddRow("[cyan]--exporters <list>[/]", "Export formats: json,markdown,html,rplot");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[grey]Examples:[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run[/]                           [grey]# Interactive mode[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run -- --filter *BTree*[/]       [grey]# Run BTree benchmarks[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run -- --btree-fast[/]           [grey]# Quick BTree baseline (~3 min)[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run -- --list[/]                 [grey]# List all benchmarks[/]");
        AnsiConsole.WriteLine();
    }

    private static void ListBenchmarks()
    {
        var benchmarks = BenchmarkDiscovery.DiscoverBenchmarks(typeof(Program).Assembly);
        var grouped = BenchmarkDiscovery.GroupByCategory(benchmarks);

        AnsiConsole.Write(new Rule("[cyan]Available Benchmarks[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        foreach (var (category, categoryBenchmarks) in grouped)
        {
            AnsiConsole.MarkupLine($"[yellow]■[/] [bold]{category}[/]");

            foreach (var benchmark in categoryBenchmarks)
            {
                AnsiConsole.MarkupLine($"    [cyan]●[/] {benchmark.Name} [grey]({benchmark.MethodCount} method{(benchmark.MethodCount != 1 ? "s" : "")})[/]");
            }

            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[grey]Total:[/] [white]{benchmarks.Count}[/] benchmarks, [white]{benchmarks.Sum(b => b.MethodCount)}[/] methods");
        AnsiConsole.WriteLine();
    }
}