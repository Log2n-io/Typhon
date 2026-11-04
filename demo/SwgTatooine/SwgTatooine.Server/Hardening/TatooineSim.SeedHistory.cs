using System;
using System.Collections.Generic;
using System.Linq;

namespace SwgTatooine.Server;

/// <summary><c>--seed-history</c>: the world's side of the history seeder (<see cref="SimBridge.SeedHistory"/>).</summary>
public sealed partial class TatooineSim
{
    /// <summary>Credit rows per seeded character: a long-lived character's share of the ledger.</summary>
    private const int SeedRowsPerCharacter = 400;

    /// <summary>
    /// Seeds the fresh world's history when <c>--seed-history</c> asks for it; says so and does nothing on a reopened world, so one command line serves both
    /// the first run and every reopen. Prints the storage's pages per segment kind before and after, and the file's size.
    /// </summary>
    private void SeedHistoryIfAsked()
    {
        if (_config.SeedHistoryThousands <= 0)
        {
            return;
        }

        if (Reopened)
        {
            Console.WriteLine("seed: --seed-history ignored — the world was reopened, and only a fresh one is seeded");
            return;
        }

        var rows = (long)_config.SeedHistoryThousands * 1_000;
        var characters = (int)Math.Clamp(rows / SeedRowsPerCharacter, 1_000, 2_000_000);
        Console.WriteLine($"seed: {rows:N0} credit rows across {characters:N0} characters, seed {_config.Seed}");
        var before = PagesByKind();
        var report = _bridge.SeedHistory(rows, characters, _config.Seed, Console.WriteLine);
        var after = PagesByKind();
        Console.WriteLine($"seed: done in {report.Seconds:F0} s — {report.EconomyRows:N0} credit rows, {report.ItemRows:N0} item rows, "
            + $"{report.Kills:N0} kills, {report.Missions:N0} missions, {report.VendorSales:N0} vendor sales, {report.Tips:N0} tips ({report.TipsRefused:N0} refused), "
            + $"{report.Commits:N0} commits");
        foreach (var kind in after.Keys.Union(before.Keys).OrderByDescending(k => after.GetValueOrDefault(k)))
        {
            Console.WriteLine($"seed:   {kind,-24} {before.GetValueOrDefault(kind),10:N0} -> {after.GetValueOrDefault(kind),10:N0} pages");
        }

        Console.WriteLine($"seed:   total {before.Values.Sum() * 8 / 1024.0:N0} -> {after.Values.Sum() * 8 / 1024.0:N0} MiB in segments (8 KiB pages)");
    }

    private Dictionary<string, long> PagesByKind()
    {
        var pages = new Dictionary<string, long>();
        foreach (var segment in Dbe.EnumerateStorageSegments())
        {
            var name = segment.Kind.ToString();
            pages[name] = pages.GetValueOrDefault(name) + segment.Pages.Length;
        }

        return pages;
    }
}
