using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Regression guard for the page-cache seqlock-counter-on-slot-reuse defect. Under an explicit 2 MiB / 256-page cache (below the 8 MiB floor) the structural working set (schema +
/// many index B-trees) overflows the cache, so eviction recycles slots during <see cref="DatabaseEngine.InitializeArchetypes"/>. A recycled slot used to hand
/// its stale (possibly odd) seqlock <see cref="PageBaseHeader.ModificationCounter"/> to the fresh page; the checkpoint then treated that quiescent page as
/// "write-in-progress" and spin-waited the full 100 ms skip timeout on it every cycle (~650 ms per engine close). Fixed by resetting the counter when a slot is
/// repurposed (<c>PagedMMF.TryAcquire</c>). The invariant "an Idle page carries an EVEN counter" is checked via <c>PagedMMF.CountQuiescentPagesWithOddSeqlock</c>.
/// </summary>
[TestFixture]
[NonParallelizable] // deliberately overflows a minimum page cache; keep it off shared CPU so eviction pressure is representative
class SeqlockCounterSlotReuseTests
{
    // The 11-component set (incl. cascade EntityLink indexes) whose index B-trees overflow the min cache — the set that originally exposed the stall.
    private static void RegisterPressureComponents(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<HVehicleData>();
        dbe.RegisterComponentFromAccessor<HCarData>();
        dbe.RegisterComponentFromAccessor<HSportsData>();
        dbe.RegisterComponentFromAccessor<HRegionData>();
        dbe.RegisterComponentFromAccessor<HCityData>();
        dbe.RegisterComponentFromAccessor<HDistrictData>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.RegisterComponentFromAccessor<BagData>();
        dbe.RegisterComponentFromAccessor<ItemData>();
    }

    [Test]
    public void SlotReuseUnderCachePressure_LeavesNoQuiescentPageWithOddSeqlock()
    {
        // Pre-fix the invariant violation reproduced on a fraction of builds; loop so a regression is caught reliably. Each build overflows the min cache
        // (forcing the eviction/reuse path), then asserts the invariant directly — no timing dependence, so the test is deterministic once the fix holds.
        var baseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(SeqlockCounterSlotReuseTests));
        Directory.CreateDirectory(baseDir);

        for (var iter = 0; iter < 15; iter++)
        {
            var dir = Path.Combine(baseDir, iter.ToString());
            Directory.CreateDirectory(dir);

            var sc = new ServiceCollection();
            sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddResourceRegistry()
                .AddMemoryAllocator()
                .AddEpochManager()
                .AddHighResolutionSharedTimer()
                .AddDeadlineWatchdog()
                .AddScopedManagedPagedMemoryMappedFile(o =>
                {
                    o.DatabaseName = $"seqlock_reuse_{iter}";
                    o.DatabaseDirectory = dir;
                    // Pinned to an explicit 2 MiB (256 pages) — the historical floor — so this regression keeps overflowing the
                    // cache and exercising slot-reuse after MinimumMemPageCount was raised to 8 MiB. TestMode bypasses the floor.
                    o.DatabaseCacheSize = 256UL * PagedMMF.PageSize;
                    o.TestMode = true;
                    o.PagesDebugPattern = false;
                })
                .AddScopedDatabaseEngine(o => TestWalProfile.Apply(o, dir));
            sc.AddScoped<IWalFileIO>(_ => new InMemoryWalFileIO());

            // The cleanup MUST be in a finally. Anything thrown below — an assertion failure, or a storage error out of InitializeArchetypes — used to skip it
            // and leave a half-written database at this path. The next run's EnsureFileDeleted swallows every exception (PagedMMFOptions.cs) and waits only
            // 500 ms for the NTFS pending-delete, so a file still mapped by the wreckage of the previous run is silently NOT deleted — and the run after that
            // opens the stale database and fails with a CRC mismatch that looks exactly like a torn-write bug in the engine. That phantom cost real debugging
            // time chasing a page-cache defect that did not exist; residue from a failing test must not become the next run's input.
            try
            {
                using var sp = sc.BuildServiceProvider();
                sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
                var dbe = sp.GetRequiredService<DatabaseEngine>();
                RegisterPressureComponents(dbe);
                dbe.InitializeArchetypes();

                var violations = dbe.MMF.CollectQuiescentOddSeqlockDiagnostics();
                Assert.That(violations, Is.Empty,
                    $"iteration {iter}: {violations.Count} Idle page(s) carry an odd seqlock counter:\n  {string.Join("\n  ", violations)}");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }
    }
}
