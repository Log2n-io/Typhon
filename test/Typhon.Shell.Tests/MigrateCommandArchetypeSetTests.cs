using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Shell.Commands;
using Typhon.Shell.Session;

namespace Typhon.Shell.Tests;

[Component("Typhon.Shell.Test.TsAsKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TsAsKeep
{
    [Field] public int V;
    [Field] public int Pad;
}

[Component("Typhon.Shell.Test.TsAsAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TsAsAdded
{
    [Field] public long A;
}

[Archetype(1, Name = "Tsh.ArchetypeSet")]
partial class TsAsV1 : Archetype<TsAsV1>
{
    public static readonly Comp<TsAsKeep> Keep = Register<TsAsKeep>();
}

[Archetype(2, Name = "Tsh.ArchetypeSet")]
partial class TsAsV2 : Archetype<TsAsV2>
{
    public static readonly Comp<TsAsKeep> Keep = Register<TsAsKeep>();
    public static readonly Comp<TsAsAdded> Added = Register<TsAsAdded>();
}

[Component("Typhon.Shell.Test.TsFm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TsFmV1
{
    [Field] public int A;
    [Field] public int Pad;
}

/// <summary>The same component one field wider: registering it stages a field migration, which only an open that builds the archetypes commits.</summary>
[Component("Typhon.Shell.Test.TsFm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TsFmV2
{
    [Field] public int A;
    [Field] public int Pad;
    [Field] public long B;
}

[Archetype(1, Name = "Tsh.FieldMigration")]
partial class TsFmArch : Archetype<TsFmArch>
{
    public static readonly Comp<TsFmV1> Fm = Register<TsFmV1>();
}

/// <summary>
/// <c>tsh migrate</c> reports the archetype sets the open migrated, and <c>schema-validate</c> reports the ones an open would migrate without changing the
/// file (#1394, design 07 AC-26). The application's revision-1 file is written by an engine of this process; tsh then loads revision 2's components.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class MigrateCommandArchetypeSetTests
{
    private const string DatabaseName = "tshset";
    private const int Population = 40;
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "typhon-shell-archetypeset", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string DatabasePath => Path.Combine(_root, DatabaseName);

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o => { o.DatabaseName = DatabaseName; o.DatabaseDirectory = _root; })
            .AddScopedDatabaseEngine(o => o.Wal = new WalWriterOptions { UseFUA = false });
        return services.BuildServiceProvider();
    }

    /// <summary>The application at revision 1: only its components are registered, so revision 2 does not resolve.</summary>
    private void CreateRevision1Database()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TsAsKeep>();
        dbe.InitializeArchetypes();
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < Population; i++)
        {
            tx.Spawn<TsAsV1>(TsAsV1.Keep.Set(new TsAsKeep { V = i + 1 }));
        }

        Assert.That(tx.Commit(), Is.True);
    }

    /// <summary>
    /// The archetype's stored row — revision, component count, every segment root and the names buffer — and its entity count, read by an application open of
    /// revision 1.
    /// </summary>
    private (int Revision, int ComponentCount, int Entities, int EntityMapSpi, int ClusterSpi, int ClusterIndexSpi, int NamesBuffer) ReadStoredRow()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TsAsKeep>();
        dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(TsAsV2);
        dbe.InitializeArchetypes();
        var row = dbe._persistedArchetypes["Tsh.ArchetypeSet"].Arch;
        using var tx = dbe.CreateQuickTransaction();
        return (row.Revision, row.ComponentCount, tx.Query<TsAsV1>().Execute().Count(), row.EntityMapSPI, row.ClusterSegmentSPI, row.ClusterIndexSPI,
            NamesBufferOf(row));
    }

    /// <summary>The names buffer id of a row: <c>ComponentCollection._bufferId</c> is internal to the schema assembly, which this one cannot see.</summary>
    private static int NamesBufferOf(ArchetypeR1 row) =>
        (int)typeof(ComponentCollection<String64>).GetField("_bufferId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(row.ComponentNames)!;

    private static (ShellSession Session, CommandExecutor Executor) RevisionTwoShell()
    {
        var session = new ShellSession();
        session.RegisterComponent("TsAsKeep", typeof(TsAsKeep), null);
        session.RegisterComponent("TsAsAdded", typeof(TsAsAdded), null);
        return (session, new CommandExecutor(session));
    }

    [Test]
    [CancelAfter(30_000)]
    public void Migrate_ReportsArchetypeSetChanges()
    {
        CreateRevision1Database();

        var (session, executor) = RevisionTwoShell();
        using (session)
        {
            // Opened through the session: how the command parser tokenizes a Windows path is not what this test is about.
            session.OpenDatabase(DatabasePath);
            var result = executor.Execute("migrate");
            Assert.That(result.Success, Is.True, result.Output);
            Assert.Multiple(() =>
            {
                Assert.That(result.Output, Does.Contain("[white]Archetypes[/]"));
                Assert.That(result.Output, Does.Contain($"MIGRATED[/] Tsh.ArchetypeSet — rev 1→2, +Typhon.Shell.Test.TsAsAdded"));
                Assert.That(result.Output, Does.Contain($"({Population} entities"));
                Assert.That(result.Output, Does.Contain("1 archetype set(s) migrated."));
                Assert.That(session.Engine.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));
            });
        }

        // The migration committed: the stored row names revision 2's set.
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TsAsKeep>();
        dbe.RegisterComponentFromAccessor<TsAsAdded>();
        dbe.InitializeArchetypes();
        var row = dbe._persistedArchetypes["Tsh.ArchetypeSet"].Arch;
        Assert.That((row.Revision, (int)row.ComponentCount, dbe.LastOpenArchetypeSetMigrations.Count), Is.EqualTo((2, 2, 0)));
    }

    [Test]
    [CancelAfter(30_000)]
    public void SchemaValidate_ReportsWithoutChangingTheFile()
    {
        CreateRevision1Database();
        var before = ReadStoredRow();

        var (session, executor) = RevisionTwoShell();
        using (session)
        {
            session.OpenDatabase(DatabasePath);
            var result = executor.Execute("schema-validate");
            Assert.That(result.Success, Is.True, result.Output);
            Assert.Multiple(() =>
            {
                Assert.That(result.Output, Does.Contain("[white]Archetypes[/]"));
                Assert.That(result.Output, Does.Contain("MIGRATE[/] Tsh.ArchetypeSet — rev 1→2, set change: +Typhon.Shell.Test.TsAsAdded"));
                Assert.That(result.Output, Does.Contain("1 archetype(s) would migrate at open"));
                Assert.That(result.Output, Does.Contain("Nothing was changed."));
                Assert.That(session.ArchetypesInitialized, Is.False, "the dry run built nothing");
            });
        }

        Assert.That(ReadStoredRow(), Is.EqualTo(before),
            "the stored row (segment roots and names buffer included) and every entity are as revision 1 wrote them");
        Assert.That((before.Revision, before.ComponentCount, before.Entities), Is.EqualTo((1, 1, Population)));
    }

    /// <summary>
    /// An archetype whose added component this session does not register is SKIPPED by the open (it is never classified), not migrated: schema-validate
    /// says so, with what it would do once registered, instead of listing it under "would migrate".
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void SchemaValidate_SetChangeBehindAnUnregisteredComponent_IsReportedSkipped()
    {
        CreateRevision1Database();

        var session = new ShellSession();
        session.RegisterComponent("TsAsKeep", typeof(TsAsKeep), null);
        var executor = new CommandExecutor(session);
        using (session)
        {
            session.OpenDatabase(DatabasePath);
            session.Engine.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(TsAsV1);
            var result = executor.Execute("schema-validate");
            Assert.That(result.Success, Is.True, result.Output);
            Assert.Multiple(() =>
            {
                Assert.That(result.Output, Does.Contain("SKIPPED[/] Tsh.ArchetypeSet — its component TsAsAdded is not registered"));
                Assert.That(result.Output, Does.Contain("once registered, rev 1→2 would migrate: +Typhon.Shell.Test.TsAsAdded"));
                Assert.That(result.Output, Does.Not.Contain("MIGRATE[/] Tsh.ArchetypeSet"), "never listed as a migration the open would run");
                Assert.That(result.Output, Does.Contain("0 archetype(s) would migrate at open"));
            });
        }
    }

    /// <summary>
    /// schema-validate on a session whose registration staged a component field migration writes nothing: it previews, and an open under any policy would
    /// commit the staged migration. The catalog row keeps the component's stored size and every entity reads back as revision 1 wrote it.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void SchemaValidate_WithAPendingFieldMigration_WritesNothing()
    {
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TsFmV1>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                tx.Spawn<TsFmArch>(TsFmArch.Fm.Set(new TsFmV1 { A = i + 1 }));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var session = new ShellSession();
        session.RegisterComponent("TsFm", typeof(TsFmV2), null);
        var executor = new CommandExecutor(session);
        using (session)
        {
            session.OpenDatabase(DatabasePath);
            Assert.That(session.Engine.HasComponentMigrations, Is.True, "premise: the session's registration staged a field migration");
            var result = executor.Execute("schema-validate");
            Assert.That(result.Success, Is.True, result.Output);
            Assert.That(session.ArchetypesInitialized, Is.False, "the dry run built nothing");
        }

        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Assert.That(dbe.PersistedComponents["Typhon.Shell.Test.TsFm"].Comp.CompSize, Is.EqualTo(8), "the catalog row still has the stored size");
            dbe.RegisterComponentFromAccessor<TsFmV1>();
            Assert.That(dbe.HasComponentMigrations, Is.False, "nothing was committed, so revision 1 registers as stored");
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            var values = tx.Query<TsFmArch>().Execute().Select(id => tx.Open(id).Read(TsFmArch.Fm).A).OrderBy(a => a).ToArray();
            Assert.That(values, Is.EqualTo(Enumerable.Range(1, Population).ToArray()));
        }
    }
}
