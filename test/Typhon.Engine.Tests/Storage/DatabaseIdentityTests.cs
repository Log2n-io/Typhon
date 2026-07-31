using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Feature #614 (F1) AC4 — the durable database identity (<c>BK_DatabaseId</c>). A profiling capture records the id of the database it ran against, so the
/// Workbench can pair a trace to a database structurally rather than by inference (design: Apps/Workbench/10-database-and-profiles.md, D-2). That pairing is
/// only worth anything if the id is genuinely stable, which is what this fixture pins down: stable across reopen, distinct per database, and adopted (not
/// re-minted) by a database created before the key existed.
/// </summary>
[TestFixture]
[NonParallelizable]
class DatabaseIdentityTests : TestBase<DatabaseIdentityTests>
{
    private DatabaseEngine NewSession(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void DatabaseId_IsMintedAtCreation_AndIsNotEmpty()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = NewSession(scope);

        Assert.That(dbe.DatabaseId, Is.Not.EqualTo(Guid.Empty), "a freshly created database must carry a minted identity, not the default Guid");
    }

    [Test]
    public void DatabaseId_IsStableAcrossReopen()
    {
        Guid first;
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = NewSession(scope))
        {
            first = dbe.DatabaseId;
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = NewSession(scope2);

        Assert.That(dbe2.DatabaseId, Is.EqualTo(first), "the identity is persisted, so reopening the same bundle must yield the same value");
    }

    // The strongest form of "distinct per database": same path, wiped and re-created. If the id were derived from the path (or from anything else about the
    // location) this would pass with an identical value — which is exactly the failure mode that would make a trace point at the wrong database.
    [Test]
    public void DatabaseId_IsDistinct_ForANewDatabaseAtTheSamePath()
    {
        Guid first;
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = NewSession(scope))
        {
            first = dbe.DatabaseId;
        }

        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = NewSession(scope2);

        Assert.That(dbe2.DatabaseId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(dbe2.DatabaseId, Is.Not.EqualTo(first), "a different database at the same path is a different database — the identity must not be reused");
    }

    // The identity is written at creation, so in practice the key is always present — a database old enough to lack it is refused outright by the
    // system-schema revision gate (#615). This exercises the fallback anyway: a live engine must never report Guid.Empty as its identity, and if it ever does
    // mint one it must persist it eagerly rather than handing out a fresh Guid per open, which looks fine in one session and drifts silently across restarts.
    [Test]
    public void DatabaseId_IsAdoptedAndPersisted_WhenTheKeyIsMissing()
    {
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = NewSession(scope))
        {
            dbe.MMF.Bootstrap.Remove(DatabaseEngine.BK_DatabaseId);
        }

        Guid adopted;
        using (var scope2 = ServiceProvider.CreateScope())
        using (var dbe2 = NewSession(scope2))
        {
            adopted = dbe2.DatabaseId;
            Assert.That(adopted, Is.Not.EqualTo(Guid.Empty), "an id-less bundle must adopt an identity on open");
        }

        using var scope3 = ServiceProvider.CreateScope();
        using var dbe3 = NewSession(scope3);

        Assert.That(dbe3.DatabaseId, Is.EqualTo(adopted), "the adopted identity must have been persisted, not regenerated on the next open");
    }
}
