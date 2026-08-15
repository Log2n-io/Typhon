using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Feature #615 (F2) AC2/AC3 — the system-schema revision gate.
/// </summary>
/// <remarks>
/// <para>
/// The engine's own components (<c>ComponentR1</c>, <c>SchemaHistoryR1</c>, <c>ArchetypeR1</c>, <c>AssemblyR1</c>) do not go through schema evolution:
/// <c>LoadSystemSchemaR1</c> rebuilds their tables straight from the CLR types, bypassing the diff/migration machinery that user components get on
/// registration, and their chunk stride is fixed when the table is created. So a layout change would reinterpret existing rows under a new stride
/// <b>with no error at all</b>.
/// </para>
/// <para>
/// This gate turns that silent misread into a loud refusal. It is the only thing standing between a schema-history record that gained two fields (#615) and a
/// database quietly reporting garbage in its own audit trail — which would be a particularly unpleasant way to fail, since the audit trail is what the
/// Workbench consults to explain drift.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SystemSchemaRevisionGateTests : TestBase<SystemSchemaRevisionGateTests>
{
    [Test]
    public void NewDatabase_IsWrittenAtTheCurrentRevision()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(dbe.MMF.Bootstrap.GetInt(DatabaseEngine.BK_SystemSchemaRevision), Is.EqualTo(DatabaseEngine.CurrentSystemSchemaRevision));
    }

    [Test]
    public void OlderRevision_IsRejectedWithAnActionableMessage()
    {
        StampSystemSchemaRevision(DatabaseEngine.CurrentSystemSchemaRevision - 1);

        var ex = AssertOpenThrows();

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain($"revision {DatabaseEngine.CurrentSystemSchemaRevision - 1}"), "the message must name what was found…");
            Assert.That(ex.Message, Does.Contain($"{DatabaseEngine.CurrentSystemSchemaRevision}"), "…and what is required…");
            Assert.That(ex.Message, Does.Contain("Recreate the database"), "…and the remedy, or the reader is left to guess");
        });
    }

    // The reverse direction matters just as much: an older build opening a database written by a newer one reads the same wrong layout. Only the remedy differs.
    [Test]
    public void NewerRevision_IsAlsoRejected_WithTheOppositeRemedy()
    {
        StampSystemSchemaRevision(DatabaseEngine.CurrentSystemSchemaRevision + 1);

        var ex = AssertOpenThrows();

        Assert.That(ex.Message, Does.Contain("upgrade this one"), "the remedy for a future database is to upgrade the build, not to recreate the data");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates the database, then rewrites its recorded system-schema revision and closes cleanly so the value is persisted.</summary>
    private void StampSystemSchemaRevision(int revision)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        dbe.MMF.Bootstrap.SetInt(DatabaseEngine.BK_SystemSchemaRevision, revision);
    }

    /// <summary>Reopens the database and returns the <see cref="InvalidDataException"/> the gate raised, unwrapping whatever DI wrapped it in.</summary>
    private InvalidDataException AssertOpenThrows()
    {
        var ex = Assert.Catch(() =>
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
        });

        Assert.That(ex, Is.Not.Null, "opening a database whose system schema does not match this build must fail, not proceed on a wrong layout");

        var inner = ex;
        while (inner != null && inner is not InvalidDataException)
        {
            inner = inner.InnerException;
        }
        Assert.That(inner, Is.InstanceOf<InvalidDataException>(), $"expected an InvalidDataException from the gate, got: {ex}");
        return (InvalidDataException)inner;
    }
}
