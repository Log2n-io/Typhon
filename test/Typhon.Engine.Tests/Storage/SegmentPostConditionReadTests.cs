using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="LogicalSegment{TStore}.CreateOrGrow"/>'s post-condition must read the pages it checks, not whatever their slots hold next (#892, #840).
/// <para>
/// The defect: the check fetched each page with <c>RequestPageEpoch</c> alone and read it. The tag is the grow's bare <c>GlobalEpoch</c> snapshot, and a
/// caller with no epoch scope of its own — <c>InitializeArchetypes</c> creating an EntityMap, schema registration creating a component table — leaves
/// nothing holding <c>MinActiveEpoch</c> below it. Once the checkpoint had written the page back, its slot was evictable between the fetch and the read;
/// the checkpoint thread took it for another page, and the check reported that page's header as a lost chain write: "CreateOrGrow IN-MEMORY chain
/// mismatch … page[1]=446 points at 0". The write was on disk and correct. <c>SeqlockCounterSlotReuseTests</c> failed that way 9 runs in 10.
/// </para>
/// <para>
/// The tests stage that interleaving on one thread. The probe writes the page back (the checkpoint's part), advances the global epoch past the snapshot,
/// and faults fresh pages in until the cache has cycled: once while the check holds the page, once before it does.
/// </para>
/// </summary>
public sealed class SegmentPostConditionReadTests
{
    private const int MemPageCount = 64;
    private const int SegmentPages = 4;

    /// <summary>
    /// Fillers faulted at most. Deliberately not "until the first filler is evicted": the checked page has been requested several times, so its
    /// clock-sweep counter outlives a filler's, and stopping there would end the staging before an unprotected checked page is taken.
    /// </summary>
    private const int MaxFillers = 16 * MemPageCount;

    /// <summary>Far beyond the file end, so each filler is a new zero page that nothing else uses.</summary>
    private const int FirstFillerPage = 50_000;

    private readonly List<string> _bundles = [];

    [TearDown]
    public void TearDown() => SegmentGrowTestKit.DeleteBundles(_bundles);

    [Test]
    [CancelAfter(5000)]
    public void ACreateOutsideAnyEpochScope_ChecksThePagesItWrote_NotTheirSlotsNextOccupant()
    {
        using var provider = SegmentGrowTestKit.CreateProvider(MemPageCount, "postcondition_read", _bundles);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var (segment, pages) = NewSegment(pmmf);

        // The first data page: the chain check reads it right after the root.
        var checkedPage = pages[1];
        Staging staging = null;
        segment.PostConditionReadProbe = filePage =>
        {
            if (filePage == checkedPage && staging == null)
            {
                staging = WriteBackAndWantTheSlot(pmmf, filePage);
            }
        };

        Assert.That(pmmf.EpochManager.IsCurrentThreadInScope, Is.False, "precondition: the create runs with no epoch scope, as InitializeArchetypes runs it");

        Assert.DoesNotThrow(() => segment.Create(PageBlockType.None, StorageSegmentKind.Other, pages, null),
            "the post-condition must read the page the create wrote; a slot it does not hold can be handed to another page between its fetch and its read");

        Assert.That(staging, Is.Not.Null, "precondition: the probe ran while the chain check held the first data page");
        Assert.That(staging.DirtyAfterWriteBack || staging.EpochHeldAfterWriteBack != 0, Is.False,
            $"precondition: once written back the page was clean (dirty={staging.DirtyAfterWriteBack}) and no page was epoch-held "
            + $"({staging.EpochHeldAfterWriteBack}), so only the check's own reference could keep it resident");
        Assert.That(staging.FirstFillerEvicted, Is.True, "precondition: the fillers cycled the cache — the first of them was itself evicted");
        Assert.That(pmmf.CountUnevictablePages().SlotRef, Is.Zero, "the check must release every slot it held");
    }

    /// <summary>
    /// The slot is reclaimed in the gap between the check's lookup and its reference: the reference then lands on whatever took the slot, and only the
    /// second lookup notices. The check must let that reference go, look again and read the page itself.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    public void ASlotReclaimedBeforeTheCheckHoldsIt_IsLetGo_AndThePageIsLookedUpAgain()
    {
        using var provider = SegmentGrowTestKit.CreateProvider(MemPageCount, "postcondition_pin_gap", _bundles);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var (segment, pages) = NewSegment(pmmf);

        var checkedPage = pages[1];
        Staging staging = null;
        var gapVisits = 0;
        segment.PostConditionPinGapProbe = filePage =>
        {
            if (filePage != checkedPage)
            {
                return;
            }
            if (++gapVisits == 1)
            {
                staging = WriteBackAndWantTheSlot(pmmf, filePage);
            }
        };

        Assert.DoesNotThrow(() => segment.Create(PageBlockType.None, StorageSegmentKind.Other, pages, null),
            "a reference taken on a slot that was meanwhile given to another page must not be read as the checked page");

        Assert.That(staging is { CheckedPageEvicted: true }, Is.True, "precondition: the checked page lost its slot inside the gap");
        Assert.That(gapVisits, Is.EqualTo(2), "the second lookup must have caught the reclaim and sent the check round again");
        Assert.That(pmmf.CountUnevictablePages().SlotRef, Is.Zero, "the reference that landed on the reclaimed slot must have been given back");
    }

    private sealed class Staging
    {
        public bool DirtyAfterWriteBack;
        public int EpochHeldAfterWriteBack;
        public bool CheckedPageEvicted;
        public bool FirstFillerEvicted;
    }

    private static (LogicalSegment<PersistentStore> Segment, int[] Pages) NewSegment(ManagedPagedMMF pmmf)
    {
        var pages = new int[SegmentPages];
        var allocated = pages.AsSpan();
        pmmf.AllocatePages(ref allocated, 0, null);
        return (new LogicalSegment<PersistentStore>(new PersistentStore(pmmf)), pages);
    }

    /// <summary>
    /// Writes <paramref name="filePage"/> back, as the checkpoint did, so its debt no longer keeps it resident; the scope's exit advances the global epoch
    /// past the grow's snapshot, as any other thread's scope does. Then faults fresh pages in, each released at once, until the page is gone or the
    /// cache has cycled many times.
    /// </summary>
    private static Staging WriteBackAndWantTheSlot(ManagedPagedMMF pmmf, int filePage)
    {
        var epochManager = pmmf.EpochManager;
        var staging = new Staging();
        using (EpochGuard.Enter(epochManager))
        {
            pmmf.RequestPageEpoch(filePage, epochManager.GlobalEpoch, out var memPageIndex);
            pmmf.SavePages([memPageIndex]).Wait();
        }
        pmmf.TryGetPageResidency(filePage, out _, out staging.DirtyAfterWriteBack);
        staging.EpochHeldAfterWriteBack = pmmf.CountUnevictablePages().EpochHeld;

        var fillers = 0;
        while (pmmf.TryGetPageResidency(filePage, out _, out _) && fillers < MaxFillers)
        {
            using (EpochGuard.Enter(epochManager))
            {
                pmmf.RequestPageEpoch(FirstFillerPage + fillers++, epochManager.GlobalEpoch, out _);
            }
        }

        staging.CheckedPageEvicted = !pmmf.TryGetPageResidency(filePage, out _, out _);
        staging.FirstFillerEvicted = !pmmf.TryGetPageResidency(FirstFillerPage, out _, out _);
        return staging;
    }
}
