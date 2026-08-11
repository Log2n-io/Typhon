using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Linq;

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
