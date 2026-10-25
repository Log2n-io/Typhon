using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="DatabaseEngine.TimelineId"/> (<c>BK_TimelineId</c>): minted at creation, stable across reopens and crashes, minted and persisted once for a
/// database that predates the key, and replaced only by <see cref="DatabaseEngine.AdoptTimeline(System.Guid, System.Guid, long, TimelineReason, bool)"/>, durably.
/// </summary>
class TimelineIdentityTests : TestBase<TimelineIdentityTests>
{
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    [Test]
    [CancelAfter(10_000)]
    public void Create_MintsATimeline_ThatReopenAndCrashKeep()
    {
        Guid created;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            created = dbe.TimelineId;
            Assert.That(created, Is.Not.EqualTo(Guid.Empty));
            Assert.That(created, Is.Not.EqualTo(dbe.DatabaseId), "a timeline is its own identity");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.TimelineId, Is.EqualTo(created), "a clean reopen keeps the timeline");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: the crash routed the open through recovery");
            Assert.That(dbe.TimelineId, Is.EqualTo(created), "a crash does not start a timeline: the WAL continues");
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public void ADatabaseWithoutTheKey_MintsOne_AndKeepsIt()
    {
        Guid databaseId;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            databaseId = dbe.DatabaseId;
            dbe.MMF.MutateBootstrapAndPersist(() => dbe.MMF.Bootstrap.Remove(DatabaseEngine.BK_TimelineId));
        }

        Guid minted;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            minted = dbe.TimelineId;
            Assert.That(minted, Is.Not.EqualTo(Guid.Empty), "an open must never run without a timeline");
            Assert.That(dbe.DatabaseId, Is.EqualTo(databaseId), "minting a timeline leaves the database identity alone");
            dbe.SimulateHardCrash();   // the mint must already be durable: nothing else persists it
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.TimelineId, Is.EqualTo(minted), "the minted timeline was persisted at the open that minted it");
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public void AdoptTimeline_IsDurableAtOnce_AndIdempotent()
    {
        var adopted = Guid.NewGuid();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            dbe.AdoptTimeline(adopted, Guid.Empty, 0, TimelineReason.Restore, keepLineage: false);
            Assert.That(dbe.TimelineId, Is.EqualTo(adopted));
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.TimelineId, Is.EqualTo(adopted), "an adoption survives a crash right after it");

            var flips = 0;
            dbe.MMF.DataFileWriteProbe = (first, _) => flips += first <= 1 ? 1 : 0;
            dbe.AdoptTimeline(adopted, Guid.Empty, 0, TimelineReason.Restore, keepLineage: false);
            Assert.That(flips, Is.Zero, "adopting the current timeline writes nothing");
            Assert.Throws<ArgumentException>(() => dbe.AdoptTimeline(Guid.Empty, Guid.Empty, 0, TimelineReason.Restore, keepLineage: false));
        }
    }

    private static DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        return dbe;
    }
}
