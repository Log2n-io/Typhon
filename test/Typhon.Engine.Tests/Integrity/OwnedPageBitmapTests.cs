using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// <c>CK-09</c> — page ownership is a property of the FILE, not of what the calling process registered (#771).
/// </summary>
/// <remarks>
/// <para>
/// The occupancy bitmap is derived state, and the crash path adopts a reconstruction of it <b>wholesale</b>
/// (<c>BitmapL3.OverwriteFromDerived</c> — a full replacement, not a read-then-diff). That is only sound if the
/// reconstruction is <i>total</i>: every page it fails to attribute is written as free, and a free bit over a live page
/// is handed to the next allocator caller, at which point two structures write to the same page.
/// </para>
/// <para>
/// The reconstruction used to enumerate <c>MMF.RegisteredSegments</c> — the segments this session loaded, which is a
/// function of the archetypes the caller registered, because <c>InitializeArchetypes</c> iterates
/// <c>ArchetypeRegistry.GetAllArchetypes()</c>. Opening with a subset of the schema is supported (a repair or forensic
/// tool has no schema assembly at all), so that made ownership caller-dependent and silently freed 36 live pages on a
/// plain open-and-close.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class OwnedPageBitmapTests : IntegrityFixtureBase
{
    /// <summary>
    /// The derived ownership bitmap is bit-identical whether or not the opener registered the schema.
    /// </summary>
    /// <remarks>
    /// The property <c>CK-09</c> already claimed — <i>"owned depends only on persisted segment directories"</i> — and the
    /// one nothing checked. Asserted against the builder directly rather than through a reopen, because the re-derive is
    /// now skipped after a clean shutdown: routing the assertion through it would make this test vacuous.
    /// (Known deviation: it is not skipped today — see <see cref="CleanShutdownReopenDoesNotRederive"/>.)
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CK-09")]
    public void OwnedBitmapIsIdenticalWithAndWithoutSchema()
    {
        BuildHealthyDatabase();

        var withSchema = DeriveOwned(registerSchema: true, out var claimedWith);
        var withoutSchema = DeriveOwned(registerSchema: false, out var claimedWithout);

        Assert.That(withoutSchema.Length, Is.EqualTo(withSchema.Length), "the two reconstructions must cover the same page range");
        Assert.That(claimedWithout, Is.EqualTo(claimedWith),
            $"an opener with no component types registered attributed {claimedWithout} pages against {claimedWith} with the "
            + "schema, so ownership is still a function of the caller rather than of the file");

        var differing = FirstDifferingPage(withSchema, withoutSchema);
        Assert.That(differing, Is.EqualTo(-1),
            $"page {differing} is owned in one reconstruction and not the other. A wholesale overwrite would write that page "
            + "free, and the next allocation would hand it to a second owner (CK-09 on_violation).");
    }

    /// <summary>
    /// A persisted segment pointer that cannot be read makes the re-derive refuse, and leaves the bitmap untouched.
    /// </summary>
    /// <remarks>
    /// The general guard, and the reason it is not merely belt-and-braces: <c>BuildOwnedPageBitmap</c> was written as the
    /// storage-integrity <i>canary</i>, where an under-derivation is a false positive — noisy and self-announcing. The
    /// design doc then reused it for the <i>heal</i> (<c>03-recovery.md</c> §7, "reuse-not-fork"), where the identical
    /// under-derivation destroys data instead. "I found no claimant" and "there is no claimant" are different statements
    /// and only the second licenses the write, so an incomplete reconstruction must refuse rather than proceed.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void RederiveRefusesWhenAPersistedSpiCannotBeAccounted()
    {
        BuildHealthyDatabase();

        using var provider = ReopenProvider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();

        // Point one persisted archetype's cluster segment outside the file. This is the shape a torn or partially-written
        // ArchetypeR1 leaves behind, and the case where the reconstruction genuinely cannot know what that segment owned.
        // An archetype this session did NOT materialize is required: the walk consults the persisted record only there,
        // because for a materialized one the live registry supersedes it (a migrating open leaves ArchetypeR1 stale).
        // This open registers no component type, so every persisted archetype is unmaterialized and any of them is walked.
        var key = dbe._persistedArchetypes.Keys.First();
        var original = dbe._persistedArchetypes[key];
        try
        {
            var poisoned = original.Arch;
            poisoned.ClusterSegmentSPI = int.MaxValue;
            dbe._persistedArchetypes[key] = (original.ChunkId, poisoned);

            var before = dbe.BuildOwnedPageBitmap(out _, out var unresolved);
            Assert.That(unresolved, Is.GreaterThan(0), "an out-of-file segment pointer must be reported, not silently skipped");

            Assert.That(() => dbe.RederiveOccupancyOnCrash(), Throws.InvalidOperationException.With.Message.Contains("partial"),
                "a reconstruction that is missing a segment it knows exists must not be adopted wholesale");

            // The refusal must leave the database exactly as it was — a guard that throws after writing is not a guard.
            var after = dbe.BuildOwnedPageBitmap(out _, out _);
            Assert.That(after, Is.EqualTo(before), "the refusal must not have modified the occupancy bitmap");
        }
        finally
        {
            // Restore before teardown. Dispose persists the archetype table, so leaving the poisoned pointer in place would
            // write it to the bundle — and an engine torn down in an inconsistent state destabilises fixtures running in
            // parallel with this one, which is a far more confusing failure than the one this test exists to catch.
            dbe._persistedArchetypes[key] = original;
        }
    }

    /// <summary>
    /// A reopen after a clean shutdown does not re-derive at all.
    /// </summary>
    /// <remarks>
    /// <c>RederiveOccupancyOnCrash</c> is documented "Crash-path only", but reached it via
    /// <c>WalFilesPresentAtOpen</c> — which means "WAL segments exist on disk", something a clean shutdown does not
    /// preclude. So the crash-path heal ran on every clean reopen, which is what made #771 reachable in ordinary use.
    /// Known deviation (2026-10-05 bug bash): the guard reads the clean-shutdown flag after the constructor has cleared
    /// it, so the heal still runs here, and this test is green only because that re-derive changes no word.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void CleanShutdownReopenDoesNotRederive()
    {
        BuildHealthyDatabase();

        using var provider = ReopenProvider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();

        Assert.That(dbe.WalFilesPresentAtOpen, Is.True,
            "this test is only meaningful while WAL files survive a clean shutdown — if that changes, the gate it guards "
            + "against is gone and this test should be re-examined rather than deleted");
        Assert.That(dbe.LastOpenOccupancyRederiveWordsChanged, Is.Zero,
            "a cleanly-closed database consolidated its bitmap on the way out; re-deriving over it is not a no-op in "
            + "general, it is an overwrite with a reconstruction");
    }

    /// <summary>
    /// The crash-path re-derive keeps the occupancy reserve and the CK-05 directory twins owned, though neither has ever been written (#850).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are recorded in durable metadata — the reserve in the bootstrap, a twin in its primary's header — and bit-set the moment they are
    /// handed out, but nothing writes to them until first use. The file's page count is the high-water of what has been WRITTEN, so bounding
    /// their claim by it dropped them, and the wholesale adoption wrote them free while the metadata still named them.
    /// </para>
    /// <para>
    /// Staged rather than waited for. Leaked dirty marks (#824) used to rewrite pages on every checkpoint and push the file past both, which is
    /// why the bug was invisible until they were fixed. This crash-path open allocates a fresh segment above everything written, and a twin is
    /// written only by its directory's second write, so that segment's twin is past the written end with no help. The reserve moves only when
    /// the occupancy map grows, which the staging forces.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CK-09")]
    public void CrashRederiveKeepsTheUnwrittenReserveAndTwinsOwned()
    {
        var leaked = BuildCrashedDatabaseWithGrownOccupancy();

        using var provider = ReopenProvider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();

        AssertRederiveRanPastUnwrittenClaims(dbe, leaked);
        AssertMetadataClaimsAllocated(dbe);
    }

    /// <summary>
    /// <see cref="CrashRederiveKeepsTheUnwrittenReserveAndTwinsOwned"/> can fail: a re-derive that adopts a bitmap clipped to the written end of
    /// the file — what the pre-#850 reconstruction produced — is rejected by its assertion.
    /// </summary>
    /// <remarks>Each half is clipped alone, so neither is proven only because the other one failed first.</remarks>
    [TestCase(false, TestName = "Mutant_ARederiveClippedToTheWrittenEndIsCaught(reserve)")]
    [TestCase(true, TestName = "Mutant_ARederiveClippedToTheWrittenEndIsCaught(twins)")]
    [CancelAfter(60_000)]
    [RuleMutant("CK-09")]
    public void Mutant_ARederiveClippedToTheWrittenEndIsCaught(bool clipTwins)
    {
        var leaked = BuildCrashedDatabaseWithGrownOccupancy();

        using var provider = ReopenProvider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        AssertRederiveRanPastUnwrittenClaims(dbe, leaked);

        var owned = dbe.BuildOwnedPageBitmap(out _, out _);
        var clipped = (long[])owned.Clone();
        var pageCount = dbe.MMF.StorageFilePageCount;
        foreach (var (page, isTwin, _) in MetadataClaims(dbe))
        {
            if (isTwin == clipTwins && page >= pageCount)
            {
                clipped[page >> 6] &= ~(1L << (page & 0x3F));
            }
        }

        try
        {
            Rederive(dbe, clipped);
            RuleMutants.AssertDetects("CK-09", "the re-derive freed page", () => AssertMetadataClaimsAllocated(dbe));
        }
        finally
        {
            // Teardown persists the bitmap: put the true ownership back first.
            Rederive(dbe, owned);
        }
    }

    /// <summary>
    /// The persisted-directory walk claims a twin past the written end of the file, as the registered walk does (#850).
    /// </summary>
    /// <remarks>
    /// An opener without the schema reaches an archetype's segments only through their persisted pointers, and their twins only through
    /// <c>ClaimDirectoryTwin</c>. <see cref="OwnedBitmapIsIdenticalWithAndWithoutSchema"/> catches the two walks disagreeing, not both clipping the
    /// same page, which is what reverting the fix in both places does. <c>InitializeArchetypes</c> allocates the archetype's cluster segment
    /// last, so its twin is the highest page allocated, and neither the checkpoint nor the clean close writes it.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CK-09")]
    public void UnwrittenTwinsAreOwnedWithAndWithoutSchema()
    {
        BuildHealthyDatabase();

        List<(int Page, bool IsTwin, string Role)> claims;
        using (var provider = ReopenProvider())
        {
            using var scope = provider.CreateScope();
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            claims = MetadataClaims(dbe);
            AssertClaimsOwned(dbe, claims, "registered walk");
        }

        using (var provider = ReopenProvider())
        {
            using var scope = provider.CreateScope();
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.InitializeArchetypes();

            // A twin this open has no pair for was reached through a persisted pointer, not through a registered segment.
            var pageCount = dbe.MMF.StorageFilePageCount;
            var registeredTwins = dbe.MMF.DirectoryPairs.Select(p => p.Twin).ToHashSet();
            Assert.That(claims.Any(c => c.IsTwin && c.Page >= pageCount && !registeredTwins.Contains(c.Page)), Is.True,
                $"STAGING: no unregistered segment's twin is past the written end (page {pageCount}), so this open cannot tell a persisted-walk "
                + "twin bounded by the bitmap from one bounded by the file. Restage rather than delete the test.");
            AssertClaimsOwned(dbe, claims, "persisted walk");
        }
    }

    /// <summary>Opens the bundle with or without the schema and returns the ownership bitmap that open derives.</summary>
    private long[] DeriveOwned(bool registerSchema, out int claimed)
    {
        using var provider = ReopenProvider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        if (registerSchema)
        {
            dbe.RegisterComponentFromAccessor<CompA>();
        }

        dbe.InitializeArchetypes();

        var owned = dbe.BuildOwnedPageBitmap(out claimed, out var unresolved);
        Assert.That(unresolved, Is.Zero, $"registerSchema={registerSchema}: a healthy database must resolve every persisted segment pointer");
        return owned;
    }

    /// <summary>
    /// Builds a database whose occupancy map has grown once, leaves WAL records past its last checkpoint, and hard-crashes it.
    /// </summary>
    /// <returns>A page allocated and checkpointed with no owner. The reopen's re-derive reclaiming it proves the re-derive ran.</returns>
    private int BuildCrashedDatabaseWithGrownOccupancy()
    {
        int leaked;
        using (var scope = Provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            Spawn(dbe, 0, 64);

            // Fill the map, then take single pages until one cannot be met: that request grows the occupancy segment and reserves its next data
            // page at the lowest free index — the first page of the grown range, far past anything written. A bulk request that overruns the
            // map puts the reserve back among the low pages, because the failed call frees its partial allocation before it grows, so the bulk
            // fill leaves a margin for whatever the checkpoint thread allocates meanwhile.
            var mmf = dbe.MMF;
            var filler = new int[mmf.OccupancyCapacityPages - CountAllocated(mmf) - 64];
            Span<int> fill = filler;
            mmf.AllocatePages(ref fill);
            var reserveBeforeGrow = mmf.ReservedOccupancyPages.DataReserve;
            var singles = new List<int>();
            while (mmf.ReservedOccupancyPages.DataReserve == reserveBeforeGrow && singles.Count < 1024)
            {
                singles.Add(mmf.AllocatePage());
            }

            Assert.That(mmf.ReservedOccupancyPages.DataReserve, Is.Not.EqualTo(reserveBeforeGrow), "the occupancy map must have grown and re-reserved");

            // Give the filler back so recovery allocates low, below the reserve, rather than past it: a write past the reserve would cover it.
            mmf.FreePages(filler);
            mmf.FreePages(singles.ToArray());

            leaked = mmf.AllocatePage();
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint must persist the grown map");

            // A tail past the checkpoint for recovery to replay, as a real crash leaves one.
            Spawn(dbe, 1000, 8);
            dbe.SimulateHardCrash();
        }

        CloseEngine();
        return leaked;
    }

    private static void Spawn(DatabaseEngine dbe, int first, int count)
    {
        using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
        for (var i = first; i < first + count; i++)
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(i + 1, i, i);
            tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();
        }

        uow.Flush();
    }

    private static int CountAllocated(ManagedPagedMMF mmf)
    {
        var words = new long[(mmf.OccupancyCapacityPages + 63) / 64];
        mmf.ReadOccupancyBits(words);
        var count = 0;
        foreach (var word in words)
        {
            count += System.Numerics.BitOperations.PopCount((ulong)word);
        }

        return count;
    }

    /// <summary>
    /// Asserts the open's re-derive ran, and that the reserve and at least one twin sit past the written end, where a claim bounded by the file
    /// and one bounded by the bitmap disagree. Without both, <see cref="AssertMetadataClaimsAllocated"/> passes whatever the bound is.
    /// </summary>
    private static void AssertRederiveRanPastUnwrittenClaims(DatabaseEngine dbe, int leaked)
    {
        Assert.That(dbe.MMF.IsPageAllocated(leaked), Is.False,
            $"GENUINENESS: page {leaked} was allocated and checkpointed with no owner. A crash-path re-derive reclaims it, so it is still allocated "
            + "only if the re-derive never ran.");

        var pageCount = dbe.MMF.StorageFilePageCount;
        Assert.That(dbe.MMF.ReservedOccupancyPages.DataReserve, Is.GreaterThanOrEqualTo(pageCount),
            $"STAGING: the occupancy data reserve must be past the written end (page {pageCount}). Restage rather than delete the test.");
        Assert.That(dbe.MMF.DirectoryPairs.Any(p => p.Twin >= pageCount), Is.True,
            $"STAGING: some directory twin must be past the written end (page {pageCount}). Restage rather than delete the test.");
    }

    /// <summary>Asserts every page the metadata names outside any segment is allocated in the live occupancy bitmap.</summary>
    private static void AssertMetadataClaimsAllocated(DatabaseEngine dbe)
    {
        var pageCount = dbe.MMF.StorageFilePageCount;
        foreach (var (page, _, role) in MetadataClaims(dbe))
        {
            Assert.That(dbe.MMF.IsPageAllocated(page), Is.True,
                $"the re-derive freed page {page}, the {role}, which the file's metadata still names (the file ends at page {pageCount}). "
                + "The next allocation would hand it to a second owner (CK-09 on_violation).");
        }
    }

    /// <summary>Asserts the derived ownership bitmap claims every one of <paramref name="claims"/>.</summary>
    private static void AssertClaimsOwned(DatabaseEngine dbe, List<(int Page, bool IsTwin, string Role)> claims, string walk)
    {
        var owned = dbe.BuildOwnedPageBitmap(out _, out var unresolved);
        Assert.That(unresolved, Is.Zero, $"{walk}: a healthy database must resolve every persisted segment pointer");

        var pageCount = dbe.MMF.StorageFilePageCount;
        foreach (var (page, _, role) in claims)
        {
            Assert.That((owned[page >> 6] >> (page & 0x3F)) & 1, Is.EqualTo(1L),
                $"{walk}: the derived ownership omits page {page}, the {role} (the file ends at page {pageCount}). A re-derive would write it free.");
        }
    }

    /// <summary>The pages held outside any segment and named by metadata: the occupancy reserves and the CK-05 directory twins.</summary>
    private static List<(int Page, bool IsTwin, string Role)> MetadataClaims(DatabaseEngine dbe)
    {
        var claims = new List<(int Page, bool IsTwin, string Role)>();
        var (dataReserve, mapReserve, mapTwinReserve) = dbe.MMF.ReservedOccupancyPages;
        AddIfSet(claims, dataReserve, false, "occupancy data reserve");
        AddIfSet(claims, mapReserve, false, "occupancy map-extension reserve");
        AddIfSet(claims, mapTwinReserve, false, "occupancy map-extension twin reserve");
        foreach (var (primary, twin) in dbe.MMF.DirectoryPairs)
        {
            AddIfSet(claims, twin, true, $"twin of directory page {primary}");
        }

        return claims;

        static void AddIfSet(List<(int Page, bool IsTwin, string Role)> sink, int page, bool isTwin, string role)
        {
            if (page > 0)
            {
                sink.Add((page, isTwin, role));
            }
        }
    }

    /// <summary>Adopts <paramref name="owned"/> as the occupancy bitmap, the way <c>RederiveOccupancyOnCrash</c> does.</summary>
    private static void Rederive(DatabaseEngine dbe, long[] owned)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var changeSet = dbe.MMF.CreateChangeSet();
        try
        {
            dbe.MMF.RederiveOccupancy(owned, changeSet);
        }
        finally
        {
            changeSet.SaveChanges();
        }
    }

    /// <summary>The first page whose ownership bit differs between two reconstructions, or -1 when they agree.</summary>
    private static int FirstDifferingPage(long[] a, long[] b)
    {
        for (var w = 0; w < a.Length; w++)
        {
            var diff = a[w] ^ b[w];
            if (diff != 0)
            {
                return (w * 64) + System.Numerics.BitOperations.TrailingZeroCount((ulong)diff);
            }
        }

        return -1;
    }
}
