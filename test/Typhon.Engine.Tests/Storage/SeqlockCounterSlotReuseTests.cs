using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using Typhon.Engine;

namespace Typhon.Engine.Tests;

/// <summary>
/// Regression guard for the page-cache seqlock-counter-on-slot-reuse defect. Under a minimum (2 MiB / 256-page) cache the structural working set (schema +
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

        for (var iter = 0; iter < 30; iter++)
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
                    o.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize;
                    o.TestMode = true;
                    o.PagesDebugPattern = false;
                })
                .AddScopedDatabaseEngine(o => TestWalProfile.Apply(o, dir));
            sc.AddScoped<IWalFileIO>(_ => new InMemoryWalFileIO());

            using (var sp = sc.BuildServiceProvider())
            {
                sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
                var dbe = sp.GetRequiredService<DatabaseEngine>();
                RegisterPressureComponents(dbe);
                dbe.InitializeArchetypes();

                var violations = dbe.MMF.CountQuiescentPagesWithOddSeqlock();
                Assert.That(violations, Is.Zero,
                    $"iteration {iter}: {violations} Idle page(s) carry an odd seqlock counter — a page-cache slot was reused without resetting ModificationCounter.");
            }

            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
