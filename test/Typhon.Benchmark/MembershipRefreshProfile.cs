using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Membership refresh profile — the #790 claim, measured.
//
// A/B on ONE engine, ONE archetype, ONE entity population. Both views are "pull mode" (no WhereField), so before #790
// they took the identical code path:
//
//   membership : Query<VfArch>().ToView(), refreshed normally      -> takes the channel
//   baseline   : Query<VfArch>().ToView(), QueryPathProbe.ForceViewRequery -> the path it took before #790
//
// Two views built from the IDENTICAL query, so the only difference is which refresh path runs. A .Where(lambda)
// view would also fall to the re-query, but it carries a per-entity delegate call the archetype-only path never
// had, which would flatter the comparison.
//
// Three regimes, because they answer different questions:
//   quiet   — nothing spawned or destroyed. This is most views on most ticks, and the epoch gate is the whole design.
//   delta   — a handful of spawns/destroys per tick. The steady state of a running simulation.
//   churn   — 1% of the population replaced per tick.
//
// Run: dotnet run -c Release -- --profile-membership [--entities <n>] [--iterations <n>]
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

public static class MembershipRefreshProfile
{
    private const int Warmup = 20;

    public static void Run(string[] args)
    {
        var entities = int.TryParse(ArgValue(args, "--entities"), out var e) ? e : 50_000;
        var iterations = int.TryParse(ArgValue(args, "--iterations"), out var it) ? it : 200;

        Console.WriteLine($"Membership refresh profile — {entities} entities, {iterations} timed refreshes per regime");
        Console.WriteLine();

        var databaseName = $"MembershipRefresh_{Environment.ProcessId}";
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

        var live = new List<EntityId>(entities);
        var seq = 0;
        const int batch = 2_000;
        for (var done = 0; done < entities; done += batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < batch && done + i < entities; i++)
            {
                var d = new VfData { Bucket = seq++ };
                live.Add(tx.Spawn<VfArch>(VfArch.Data.Set(in d)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // The creating transaction is DISPOSED immediately. Holding it open pins TransactionChain.MinTSN, so no destroyed entity's record is ever
        // reclaimable for the whole run (Transaction.cs:413, :422, :430-446) — the churn regime would accumulate ~121 000 tombstones that the
        // re-query column walks and rejects per entity while the channel column never touches them. That inflates the ratio with an artefact of
        // the harness, and the resulting number was cited as fact in rules/ and the design doc.
        //
        // Views deliberately outlive the transaction that creates them (#862). The retained query copy is detached from that creator;
        // snapshot-dependent re-query work binds the transaction supplied to Refresh, while routing metadata comes from the view's cached engine.
        EcsView<VfArch> membership, baseline;
        using (var viewTx = dbe.CreateQuickTransaction())
        {
            membership = viewTx.Query<VfArch>().ToView();
            baseline = viewTx.Query<VfArch>().ToView();   // identical query; forced down the re-query path below
        }

        // Anchor both past the seeding resync so the timed regimes measure steady state, not first-refresh behaviour.
        using (var anchor = dbe.CreateQuickTransaction())
        {
            membership.Refresh(anchor);
            QueryPathProbe.ForceViewRequery = true;
            try { baseline.Refresh(anchor); } finally { QueryPathProbe.ForceViewRequery = false; }
        }

        Console.WriteLine($"Populated. membership={membership.Count}  baseline={baseline.Count}");
        if (membership.Count != entities || baseline.Count != entities)
        {
            Console.WriteLine("!! the two views disagree with the population — every number below is meaningless");
        }
        Console.WriteLine();

        var rows = new List<Row>
        {
            Measure(dbe, membership, baseline, live, iterations, churnPerTick: 0,               label: "quiet   (0 changes)"),
            Measure(dbe, membership, baseline, live, iterations, churnPerTick: 50,              label: "delta   (50 changes)"),
            Measure(dbe, membership, baseline, live, iterations, churnPerTick: entities / 100,  label: $"churn   ({entities / 100} changes)")
        };

        Console.WriteLine("| regime | membership us | re-query us | speedup |");
        Console.WriteLine("|--------|--------------:|------------:|--------:|");
        foreach (var r in rows)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1,13:F2} | {2,11:F1} | {3,6:F0}x |", r.Label, r.MembershipUs, r.LambdaUs, r.LambdaUs / Math.Max(r.MembershipUs, 0.0001)));
        }

        Console.WriteLine();
        Console.WriteLine($"Final: membership={membership.Count}  baseline={baseline.Count}  (must be equal — the channel and the re-query must agree)");

        membership.Dispose();
        baseline.Dispose();
        dbe.Dispose();
        try { File.Delete($"{databaseName}.bin"); } catch (IOException) { /* best effort */ }
        try { File.Delete($"{databaseName}.lock"); } catch (IOException) { /* best effort */ }
    }

    private static Row Measure(DatabaseEngine dbe, EcsView<VfArch> membership, EcsView<VfArch> baseline, List<EntityId> live,
        int iterations, int churnPerTick, string label)
    {
        var mSamples = new double[iterations];
        var lSamples = new double[iterations];
        var seq = 1_000_000;
        var cursor = 0;

        for (var iter = 0; iter < Warmup + iterations; iter++)
        {
            if (churnPerTick > 0)
            {
                using var tx = dbe.CreateQuickTransaction();
                for (var c = 0; c < churnPerTick; c++)
                {
                    // Replace in place: destroy one live entity and spawn a fresh one, so the population size is stable and the two
                    // regimes differ only in how many entries the refresh has to apply.
                    tx.Destroy(live[cursor]);
                    var d = new VfData { Bucket = seq++ };
                    live[cursor] = tx.Spawn<VfArch>(VfArch.Data.Set(in d));
                    cursor = (cursor + 1) % live.Count;
                }
                tx.Commit();
            }

            using var refreshTx = dbe.CreateQuickTransaction();

            var t0 = Stopwatch.GetTimestamp();
            membership.Refresh(refreshTx);
            var t1 = Stopwatch.GetTimestamp();

            // The SAME view, forced down the path it took before #790. Not a second, differently-shaped view: a .Where(lambda) baseline would
            // carry a per-entity delegate call the archetype-only path never had, and would flatter the result.
            QueryPathProbe.ForceViewRequery = true;
            try
            {
                baseline.Refresh(refreshTx);
            }
            finally
            {
                QueryPathProbe.ForceViewRequery = false;
            }
            var t2 = Stopwatch.GetTimestamp();

            membership.ClearDelta();
            baseline.ClearDelta();

            if (iter >= Warmup)
            {
                mSamples[iter - Warmup] = (t1 - t0) * 1_000_000.0 / Stopwatch.Frequency;
                lSamples[iter - Warmup] = (t2 - t1) * 1_000_000.0 / Stopwatch.Frequency;
            }
        }

        Array.Sort(mSamples);
        Array.Sort(lSamples);
        return new Row(label, mSamples[mSamples.Length / 2], lSamples[lSamples.Length / 2]);
    }

    private readonly struct Row(string label, double membershipUs, double lambdaUs)
    {
        public string Label { get; } = label;
        public double MembershipUs { get; } = membershipUs;
        public double LambdaUs { get; } = lambdaUs;
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
