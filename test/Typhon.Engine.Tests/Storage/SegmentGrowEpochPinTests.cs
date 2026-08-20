using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Rule <b>EP-01</b> (<c>rules/concurrency.md</c>): a post-condition check reads only what its operation wrote.
/// <para>
/// The defect these tests lock down (#838): <see cref="LogicalSegment{TStore}.CreateOrGrow"/> proved a grow correct by
/// walking the segment's <b>entire</b> data-page forward chain — one <c>RequestPageEpoch</c> per data page. That call
/// raises a page's <c>AccessEpoch</c> by CAS-max, and the only thing that lowers it is <c>UnlatchPageExclusive</c>,
/// which resets it to 0 (PS-03). A page that is merely READ therefore stays unevictable under PS-01 for the rest of the
/// enclosing epoch scope — which is the caller's: <c>Transaction.Init</c> opens it and only <c>Dispose</c> closes it,
/// and a nested <c>EpochGuard.Enter</c> does not re-pin. So an O(segment) walk pinned O(segment) pages for a whole
/// transaction, and a commit that grew a segment larger than the page cache ended up waiting for eviction of pages its
/// own pin protected. It surfaced as a 5 s <see cref="PageCacheBackpressureTimeoutException"/> — a self-deadlock
/// reported as a timeout.
/// </para>
/// <para>
/// Note what that implies, because it is what makes the measurement below meaningful: the grow's write loops latch and
/// unlatch every page they touch, so they end pinning <b>nothing</b>. The post-condition is the only thing a grow
/// leaves pinned, and the count these tests read is entirely its doing.
/// </para>
/// <para>
/// The verifier <b>counts pins</b> rather than waiting for the timeout: it is the invariant itself rather than a
/// downstream symptom, it runs instantly, and it needs no mutation of the process-wide <c>TimeoutOptions.Current</c>
/// (unsafe under the suite's parallel fixtures). The back-pressure symptom gets its own end-to-end test below.
/// </para>
/// </summary>
/// <remarks>
/// <b>Keep the page caches here small.</b> An earlier draft gave the verifier and the mutant 2800-page caches (23 MB
/// each, GC-pinned) so both could hold a 2400-page segment. That alone drove <c>SeqlockCounterSlotReuseTests</c> — whose
/// whole design is to run cache-STARVED — from its pre-existing 1-run-in-3 <c>CreateOrGrow</c> chain-truncation failure
/// to 3-of-3. Measured: excluding this fixture returned it to 1-in-3 with the #838 engine change still applied, so the
/// amplifier was this fixture's memory, not the fix. Shrinking the caches is what fixed it, so only the mutant gets a
/// cache that holds its whole segment, and it uses a smaller segment to pay for it.
/// <para>
/// <c>[NonParallelizable]</c> was tried first and did NOT help, which makes sense in hindsight: the neighbour already
/// carries that attribute, so the two were never running in parallel — the interference is process-level memory, not CPU
/// contention, and marking this fixture would only move it into the same serial phase. It is deliberately absent.
/// </para>
/// <para>
/// The underlying defect is real and untouched by any of this: it reproduces on an unmodified engine at the same rate.
/// </para>
/// </remarks>
public sealed class SegmentGrowEpochPinTests
{
    /// <summary>
    /// Pages the segment is built up to before the measured grow. Deliberately past
    /// <c>LogicalSegment.RootHeaderIndexSectionCount</c> (2000 entries), so the build allocates a directory map-extension
    /// page — the path on which <c>CreateOrGrow</c> rewrites the ROOT page's header, at an index outside
    /// <c>[growFrom-1, end]</c>. A smaller segment never reaches it, and the chain check's root coverage would go untested.
    /// </summary>
    private const int SegmentPages = 2400;

    /// <summary>
    /// Segment size for the mutant only. The mutant needs a cache big enough to hold the WHOLE segment (see its remarks),
    /// so it uses a smaller one — it has no reason to cross the map-extension boundary, and a 1000-page segment already
    /// overruns <see cref="PinBudget"/> by more than an order of magnitude, which is all it has to demonstrate.
    /// </summary>
    private const int MutantSegmentPages = 1000;

    /// <summary>Pages added per build step. Each step commits and releases its epoch scope, so its pages become evictable again.</summary>
    private const int GrowStep = 300;

    /// <summary>Pages added by the measured grow — the only work whose pins EP-01 permits.</summary>
    private const int GrowBy = 10;

    /// <summary>
    /// Pins allowed for a <see cref="GrowBy"/>-page grow: the new pages, the old tail, the directory root and its map
    /// extensions, and the occupancy-bitmap pages the allocator touches.
    /// </summary>
    /// <remarks>
    /// Measured at <b>13</b> — the 10 new pages, the old tail and the directory root from the chain check, plus the
    /// map-extension page that post-condition #2 (<c>VerifyDirectoryAgainst</c>) faults — against
    /// <b>2410</b> before the fix — the whole segment. The headroom is deliberate: the exact figure moves with page
    /// geometry and with how many occupancy pages an allocation happens to touch, and pinning the test to 13 would make
    /// it a change-detector. What must not move is the ORDER, so the budget only has to stay far below
    /// <see cref="SegmentPages"/> to mean something.
    /// </remarks>
    private const int PinBudget = GrowBy + 64;

    /// <summary>
    /// Distinctive substring of the verifier's own rejection message. <see cref="RuleMutants.AssertDetects"/> requires
    /// the mutant to fail on THIS assertion and not on unrelated scaffolding, which is what makes the mutant evidence.
    /// </summary>
    private const string Ep01Marker = "EP-01 violated: the grow pinned pages it never wrote";

    /// <summary>Database bundles created by this fixture, deleted in <see cref="TearDown"/>.</summary>
    private readonly List<string> _bundles = [];

    /// <summary>
    /// Deletes the databases this fixture created. Not just tidiness: these bundles run to ~19 MB each, and the
    /// neighbouring <c>SeqlockCounterSlotReuseTests</c> is sensitive enough to process-wide memory pressure that leaving
    /// them behind is a measurable cost to somebody else's test (see the remarks on this class and #840).
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        foreach (var bundle in _bundles)
        {
            try
            {
                if (Directory.Exists(bundle))
                {
                    Directory.Delete(bundle, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best effort — a lingering handle must not fail a green test.
            }
        }

        _bundles.Clear();
    }

    [Test]
    [VerifiesRule("EP-01")]
    public void Grow_InsideCallerEpochScope_PinsOnlyTheGrownRange()
    {
        using var provider = CreateProvider(memPageCount: 800, "seg_grow_pin_budget");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        var segment = BuildSegment(pmmf, SegmentPages);

        // The measured grow, inside ONE scope — the miniature of Transaction.Init → FinalizeSpawns → Grow.
        using var guard = EpochGuard.Enter(pmmf.EpochManager);
        // The scope must start clean, or the budget below would be measuring the build rather than the grow. Every build step
        // released its own scope, which advanced the global epoch past the pages it tagged.
        Assert.That(pmmf.CountUnevictablePages().EpochHeld, Is.Zero,
            "the build phase must leave nothing epoch-pinned, otherwise the budget below is not measuring the grow");

        // No SaveChanges, unlike BuildSegment: a transaction that grows a segment mid-commit has not checkpointed either,
        // and the measurement is of EpochHeld, which dirty state does not affect.
        var changeSet = pmmf.CreateChangeSet();
        segment.Grow(segment.Length + GrowBy, true, changeSet);

        AssertPinBudget(pmmf, segment.Length);
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: drives the verifier's assertion with the pre-fix behaviour —
    /// the exhaustive whole-segment chain walk, inside the caller's pin — and requires that assertion to reject it.
    /// Without this the verifier could be passing because the budget is unreachable rather than because the fix works.
    /// </summary>
    /// <remarks>
    /// The cache here is larger than the segment on purpose. With a small cache the exhaustive walk would throw
    /// <see cref="PageCacheBackpressureTimeoutException"/> instead of failing the assertion, and a mutant that dies on
    /// its own scaffolding proves nothing about the verifier — the failure has to carry the verifier's marker.
    /// </remarks>
    [Test]
    [RuleMutant("EP-01")]
    public void Grow_ExhaustiveWalkInsideCallerEpochScope_PinsWholeSegment()
    {
        RuleMutants.AssertDetects("EP-01", Ep01Marker, () =>
        {
            using var provider = CreateProvider(memPageCount: MutantSegmentPages + 400, "seg_grow_pin_mutant");
            using var scope = provider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var segment = BuildSegment(pmmf, MutantSegmentPages);

            using var guard = EpochGuard.Enter(pmmf.EpochManager);
            var changeSet = pmmf.CreateChangeSet();
            segment.Grow(segment.Length + GrowBy, true, changeSet);

            // The removed post-condition, verbatim: prove a 10-page grow correct by re-reading all 1010 pages.
            segment.WalkForwardChainPageCount(guard.Epoch);

            AssertPinBudget(pmmf, segment.Length);
        });
    }

    /// <summary>
    /// The end-to-end symptom, and the suite's first assertion of <see cref="PageCacheBackpressureTimeoutException"/> —
    /// nothing referenced that exception before #838. Repeatedly growing a segment past the size of the page cache must
    /// complete; before the fix each grow's post-condition walk pinned the whole segment inside its own epoch scope, so
    /// the first step that crossed the cache size blocked for 5 s and threw.
    /// </summary>
    [Test]
    public void Grow_OnSegmentLargerThanPageCache_DoesNotExhaustTheCache()
    {
        // 800 pages of cache for a 2400-page segment — three times too small to hold it. TestMode is what permits a cache
        // below the 8 MiB floor; the floor exists so production cannot be configured into this corner, and this test's
        // whole point is to sit in it. The margin matters: since the check pins its own range, a GrowStep-page step holds
        // roughly GrowStep+1 pages, so the cache must comfortably exceed the STEP even though it cannot hold the segment.
        using var provider = CreateProvider(memPageCount: 800, "seg_grow_backpressure");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        LogicalSegment<PersistentStore> segment = null;
        Assert.DoesNotThrow(
            () => segment = BuildSegment(pmmf, SegmentPages),
            $"growing a segment to {SegmentPages} pages behind an 800-page cache must not exhaust the cache — a grow's "
            + $"check may only read what it wrote (EP-01), so no single step can hold more than about {GrowStep} pages");

        Assert.That(segment.Length, Is.EqualTo(SegmentPages));
    }

    /// <summary>
    /// The bounded check verifies index 0 even when the root falls outside <c>[growFrom-1, end]</c>. This proves that
    /// coverage can FAIL, not merely that the line executes.
    /// </summary>
    /// <remarks>
    /// The root is in range for a reason: a grow that pushes the directory past <c>RootHeaderIndexSectionCount</c>
    /// rewrites the ROOT page's <c>LogicalSegmentHeader</c> to chain in a map-extension page, and
    /// <c>LogicalSegmentNextMapPBID</c> is the field adjacent to <c>LogicalSegmentNextRawDataPBID</c> in that struct — so
    /// a wrong-field write there is exactly the bug class this post-condition exists to catch, at an index a naive
    /// <c>[growFrom-1, end]</c> bound would skip. Without this test the root branch would be covered by execution only:
    /// <see cref="Grow_InsideCallerEpochScope_PinsOnlyTheGrownRange"/> runs it, but would stay green if it checked nothing.
    /// </remarks>
    [Test]
    public void Grow_WithACorruptedRootChainPointer_IsRejectedByThePostCondition()
    {
        using var provider = CreateProvider(memPageCount: 800, "seg_grow_root_stomp");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        var segment = BuildSegment(pmmf, SegmentPages);
        var expectedNext = segment.Pages[1];

        // Stand in for the wrong-field write: break the ROOT's forward pointer only, leaving the directory intact so the
        // failure has to come from the chain check (post-condition #1) and not from VerifyDirectoryAgainst.
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            pmmf.RequestPageEpoch(segment.Pages[0], pmmf.EpochManager.GlobalEpoch, out var memPageIndex);
            ref var header = ref pmmf.GetPage(memPageIndex).StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
            header.LogicalSegmentNextRawDataPBID = 0;
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var guard = EpochGuard.Enter(pmmf.EpochManager);
            var changeSet = pmmf.CreateChangeSet();
            segment.Grow(segment.Length + GrowBy, true, changeSet);
        });

        Assert.That(error.Message, Does.Contain("page[0]"),
            "the post-condition must name index 0 — a grow past the directory root rewrites that page's header, so it is "
            + "inside what the grow wrote even when it is outside [growFrom-1, end]");
        Assert.That(error.Message, Does.Contain($"expected {expectedNext}"),
            "and must report the pointer it expected, so the failure is diagnosable without a debugger");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The shared assertion, used by both the verifier and its mutant so the mutant is guaranteed to fail on the
    /// verifier's own message. Must be called INSIDE the measured epoch scope: <c>EpochHeld</c> is computed against
    /// <c>MinActiveEpoch</c>, which moves the moment the scope exits.
    /// </summary>
    private static void AssertPinBudget(ManagedPagedMMF pmmf, int segmentLength)
    {
        var counts = pmmf.CountUnevictablePages();

        // Lower bound first, and it is not ceremony: the post-condition is the only thing a grow leaves pinned, so ZERO
        // pins would mean RequestPageEpoch had stopped stamping AccessEpoch — a PS-01 use-after-free, which would make
        // the upper bound below pass for the worst possible reason.
        Assert.That(counts.EpochHeld, Is.GreaterThan(0),
            "the grow's chain check must pin the pages it reads; zero would mean RequestPageEpoch stopped stamping "
            + "AccessEpoch, which is a PS-01 use-after-free, not a success");

        Assert.That(counts.EpochHeld, Is.LessThanOrEqualTo(PinBudget),
            $"{Ep01Marker}: a {GrowBy}-page grow on a {segmentLength}-page segment left {counts.EpochHeld} pages "
            + $"epoch-pinned (budget {PinBudget}). RequestPageEpoch raises AccessEpoch by CAS-max and only "
            + "UnlatchPageExclusive lowers it (PS-03), so a page this check merely READS stays unevictable under PS-01 "
            + "for the rest of the CALLER's scope — for a commit, the whole transaction. A grow whose check reads the "
            + "segment rather than the pages it wrote will eventually wait for eviction of pages its own pin protects "
            + "(#838).");
    }

    /// <summary>
    /// Builds a segment of <paramref name="targetPages"/> pages in <see cref="GrowStep"/>-page steps, each step in its
    /// OWN epoch scope with its own <c>SaveChanges</c>.
    /// </summary>
    /// <remarks>
    /// The per-step scope is load-bearing, not tidiness: exiting the outermost scope advances the global epoch, which
    /// drops <c>MinActiveEpoch</c> below the pages that step tagged and makes them evictable again; <c>SaveChanges</c>
    /// clears their writeback debt. Building the whole segment inside one scope would leave every page pinned and the
    /// measurement could not distinguish the grow's pins from the build's.
    /// </remarks>
    private static LogicalSegment<PersistentStore> BuildSegment(ManagedPagedMMF pmmf, int targetPages)
    {
        LogicalSegment<PersistentStore> segment;
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            var changeSet = pmmf.CreateChangeSet();
            segment = pmmf.AllocateSegment(PageBlockType.None, GrowStep, changeSet);
            changeSet.SaveChanges();
        }

        while (segment.Length < targetPages)
        {
            using (EpochGuard.Enter(pmmf.EpochManager))
            {
                var changeSet = pmmf.CreateChangeSet();
                segment.Grow(Math.Min(segment.Length + GrowStep, targetPages), true, changeSet);
                changeSet.SaveChanges();
            }
        }

        return segment;
    }

    private ServiceProvider CreateProvider(int memPageCount, string databaseName)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = $"Typhon_{databaseName}_db";
                options.DatabaseCacheSize = (ulong)memPageCount * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                // Permits a cache below the 8 MiB floor and skips the physical fsync — both wanted here.
                options.TestMode = true;
            });

        var provider = services.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _bundles.Add(provider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.BundleDirectory);
        return provider;
    }
}
