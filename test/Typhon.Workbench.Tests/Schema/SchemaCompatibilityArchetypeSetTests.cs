using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Schema.Definition;
using Typhon.Workbench.Backups;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Schema;

[Component("Workbench.Test.WbAsKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct WbAsKeep
{
    public int V;
    public int Pad;
    public WbAsKeep(int v) { V = v; Pad = 0; }
}

[Component("Workbench.Test.WbAsAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct WbAsAdded
{
    public long A;
}

[Archetype(1, Name = "Wb.ArchetypeSet")]
partial class WbAsV1 : Archetype<WbAsV1>
{
    public static readonly Comp<WbAsKeep> Keep = Register<WbAsKeep>();
}

[Archetype(2, Name = "Wb.ArchetypeSet")]
partial class WbAsV2 : Archetype<WbAsV2>
{
    public static readonly Comp<WbAsKeep> Keep = Register<WbAsKeep>();
    public static readonly Comp<WbAsAdded> Added = Register<WbAsAdded>();
}

/// <summary>Holds the added component in the file before the set change, so an inspector can register it.</summary>
[Archetype(1, Name = "Wb.ArchetypeSetHolder")]
partial class WbAsHolder : Archetype<WbAsHolder>
{
    public static readonly Comp<WbAsAdded> Added = Register<WbAsAdded>();
}

/// <summary>Revision 1 again, with the added component: a set change without a revision increase (R1), which an open with this build refuses.</summary>
[Archetype(1, Name = "Wb.ArchetypeSet")]
partial class WbAsR1Same : Archetype<WbAsR1Same>
{
    public static readonly Comp<WbAsKeep> Keep = Register<WbAsKeep>();
    public static readonly Comp<WbAsAdded> Added = Register<WbAsAdded>();
}

[Component("Workbench.Test.WbAsFmKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct WbAsFmKeepV1
{
    [Field] public int V;
    [Field] public int Pad;
}

/// <summary>The same component, one field wider: its registration stages a field migration.</summary>
[Component("Workbench.Test.WbAsFmKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct WbAsFmKeepV2
{
    [Field] public int V;
    [Field] public int Pad;
    [Field] public long W;
}

[Component("Workbench.Test.WbAsFmAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct WbAsFmAdded
{
    public long A;
}

[Archetype(1, Name = "Wb.FieldUnderSet")]
partial class WbAsFmV1 : Archetype<WbAsFmV1>
{
    public static readonly Comp<WbAsFmKeepV1> Keep = Register<WbAsFmKeepV1>();
}

/// <summary>A set change (+Added) over a component whose layout changes: the Workbench skips the archetype, the migrating component refuses (R10).</summary>
[Archetype(2, Name = "Wb.FieldUnderSet")]
partial class WbAsFmV2 : Archetype<WbAsFmV2>
{
    public static readonly Comp<WbAsFmKeepV2> Keep = Register<WbAsFmKeepV2>();
    public static readonly Comp<WbAsFmAdded> Added = Register<WbAsFmAdded>();
}

/// <summary>
/// The Workbench never migrates an archetype's component set (#1394, design 07 D4, AC-25): it opens with
/// <see cref="ArchetypeSetMigrationPolicy.Refuse"/>, the archetype is skipped and the file left as stored, and
/// <see cref="SchemaCompatibility"/> reports <see cref="SchemaCompatibility.State.MigrationRequired"/> with an
/// <see cref="SchemaCompatibility.ArchetypeSetChangeKind"/> diagnostic.
/// </summary>
/// <remarks>
/// Two shapes, because an inspector registers only the components the file holds: the added component is already in the file (another archetype holds
/// it), so the new revision resolves and the engine classifies it; or it is not, so the new revision is skipped for that unresolved slot and the engine
/// reports it from the names alone. The build under inspection declares revision 2 only: revision 1 is hidden from the Workbench opens.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class SchemaCompatibilityArchetypeSetTests
{
    private const string DatabaseName = "archetypeset";
    private const int Population = 60;
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-archetypeset-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private ServiceProvider Provider(Action<DatabaseEngineOptions> configure)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o => { o.DatabaseName = DatabaseName; o.DatabaseDirectory = _tempDir; })
            .AddScopedDatabaseEngine(configure);
        return services.BuildServiceProvider();
    }

    private static void ApplicationOptions(DatabaseEngineOptions o) => o.Wal = new WalWriterOptions { UseFUA = false };

    private string DataFile => Path.Combine(_tempDir, $"{DatabaseName}.typhon", "data");

    /// <summary>The application's revision-1 file: <see cref="Population"/> entities, and the added component's holder when asked.</summary>
    private List<EntityId> CreateRevision1Database(bool addedInFile)
    {
        using var provider = Provider(ApplicationOptions);
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(WbAsV2) || meta.ArchetypeType == typeof(WbAsR1Same);
        dbe.RegisterComponentFromAccessor<WbAsKeep>();
        if (addedInFile)
        {
            dbe.RegisterComponentFromAccessor<WbAsAdded>();
        }

        dbe.InitializeArchetypes();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < Population; i++)
            {
                ids.Add(tx.Spawn<WbAsV1>(WbAsV1.Keep.Set(new WbAsKeep(i + 1))));
            }

            if (addedInFile)
            {
                tx.Spawn<WbAsHolder>(WbAsHolder.Added.Set(new WbAsAdded { A = 5 }));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.ForceCheckpoint();
        return ids;
    }

    /// <summary>
    /// Opens the file the way the Workbench does (<see cref="EngineLifecycle.ConfigureEngineOptions"/>, components through
    /// <see cref="SchemaCompatibility.ClassifyAndRegister"/>), with a build declaring revision 2 only. Returns the compatibility result and the refusals.
    /// </summary>
    private (SchemaCompatibility.Result Result, IReadOnlyList<ArchetypeSetChange> Refused, int Migrations) OpenAsWorkbench()
    {
        using var provider = Provider(EngineLifecycle.ConfigureEngineOptions);
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(WbAsV1) || meta.ArchetypeType == typeof(WbAsR1Same);
        var loaded = new LoadedSchema([typeof(WbAsKeep).Assembly], [typeof(WbAsKeep), typeof(WbAsAdded)],
            ["Workbench.Test.WbAsKeep", "Workbench.Test.WbAsAdded"], [typeof(WbAsV2), typeof(WbAsHolder)]);
        var result = SchemaCompatibility.ClassifyAndRegister(dbe, loaded);
        dbe.InitializeArchetypes();
        return (SchemaCompatibility.WithArchetypeSetChanges(dbe, result), dbe.RefusedArchetypeSetChanges, dbe.LastOpenArchetypeSetMigrations.Count);
    }

    /// <summary>The stored row of the archetype and its component names, read by an application open of revision 1, plus every entity's value.</summary>
    private (ArchetypeR1 Row, string[] Names, int[] Values) ReadRevision1(List<EntityId> ids)
    {
        using var provider = Provider(ApplicationOptions);
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(WbAsV2) || meta.ArchetypeType == typeof(WbAsR1Same);
        dbe.RegisterComponentFromAccessor<WbAsKeep>();
        dbe.InitializeArchetypes();
        var values = new int[ids.Count];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < ids.Count; i++)
        {
            values[i] = tx.Open(ids[i]).Read(WbAsV1.Keep).V;
        }

        var row = dbe._persistedArchetypes["Wb.ArchetypeSet"].Arch;
        return (row, dbe.ReadPersistedComponentNames(row, "Wb.ArchetypeSet"), values);
    }

    [TestCase(true, TestName = "RefusePolicy_SkipsTheArchetype_FileUntouched(added component in the file)")]
    [TestCase(false, TestName = "RefusePolicy_SkipsTheArchetype_FileUntouched(added component new to the file)")]
    public void RefusePolicy_SkipsTheArchetype_FileUntouched(bool addedInFile)
    {
        var ids = CreateRevision1Database(addedInFile);
        var (rowBefore, namesBefore, _) = ReadRevision1(ids);
        var sizeBefore = new FileInfo(DataFile).Length;

        var (_, refused, migrations) = OpenAsWorkbench();
        Assert.That(refused, Has.Count.EqualTo(1), "premise: the archetype was skipped for its set change");
        Assert.That(migrations, Is.Zero, "the Workbench migrated nothing");

        var (rowAfter, namesAfter, values) = ReadRevision1(ids);
        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(DataFile).Length, Is.EqualTo(sizeBefore), "the page count is unchanged");
            Assert.That(rowAfter.Revision, Is.EqualTo(rowBefore.Revision), "Revision");
            Assert.That(rowAfter.ComponentCount, Is.EqualTo(rowBefore.ComponentCount), "ComponentCount");
            Assert.That(namesAfter, Is.EqualTo(namesBefore).And.EqualTo(new[] { "Workbench.Test.WbAsKeep" }), "ComponentNames");
            Assert.That(rowAfter.EntityMapSPI, Is.EqualTo(rowBefore.EntityMapSPI), "EntityMapSPI");
            Assert.That(rowAfter.ClusterSegmentSPI, Is.EqualTo(rowBefore.ClusterSegmentSPI), "ClusterSegmentSPI");
            Assert.That(rowAfter.ClusterIndexSPI, Is.EqualTo(rowBefore.ClusterIndexSPI), "ClusterIndexSPI");
            Assert.That(rowAfter.NextEntityKey, Is.EqualTo(rowBefore.NextEntityKey), "NextEntityKey");
            Assert.That(values, Is.EqualTo(Enumerable.Range(1, Population).ToArray()), "every entity still reads back under revision 1");
        });
    }

    [TestCase(true, TestName = "RefusePolicy_ReportsMigrationRequired_WithArchetypeSetChangeDiagnostic(added component in the file)")]
    [TestCase(false, TestName = "RefusePolicy_ReportsMigrationRequired_WithArchetypeSetChangeDiagnostic(added component new to the file)")]
    public void RefusePolicy_ReportsMigrationRequired_WithArchetypeSetChangeDiagnostic(bool addedInFile)
    {
        CreateRevision1Database(addedInFile);

        var (result, refused, _) = OpenAsWorkbench();
        var archetype = result.Diagnostics.Where(d => d.Kind == SchemaCompatibility.ArchetypeSetChangeKind).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(SchemaCompatibility.State.MigrationRequired));
            Assert.That(refused.Single().Kind, Is.EqualTo(ArchetypeSetChangeKind.SetChange));
            Assert.That(archetype, Has.Length.EqualTo(1));
            Assert.That(archetype[0].ComponentName, Is.EqualTo("Wb.ArchetypeSet"));
            Assert.That(archetype[0].Detail, Does.Contain("stored at revision 1 and the loaded schema declares revision 2"));
            Assert.That(archetype[0].Detail, Does.Contain("+Workbench.Test.WbAsAdded"));

            // Errors ahead of the informational notes: the banner shows the first three.
            var firstNote = Array.FindIndex(result.Diagnostics, d => d.Kind == SchemaCompatibility.NotInDatabaseKind);
            var archetypeAt = Array.FindIndex(result.Diagnostics, d => d.Kind == SchemaCompatibility.ArchetypeSetChangeKind);
            Assert.That(firstNote < 0 || archetypeAt < firstNote, Is.True, "the archetype diagnostic precedes every not-in-database note");
        });
    }

    /// <summary>
    /// The Workbench open's InitializeArchetypes wiring (<see cref="SchemaCompatibility.InitializeArchetypes"/>, the one call
    /// <see cref="EngineLifecycle"/> makes): an open that throws has already skipped archetypes for their set change, and both reach the banner — the
    /// refusal FIRST, ahead of the archetype diagnostics and of every not-in-database note, since the banner shows three.
    /// </summary>
    [Test]
    public void InitializeArchetypesThatThrows_KeepsTheSkippedSetChanges_RefusalFirst()
    {
        using (var provider = Provider(ApplicationOptions))
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(WbAsFmV2);
            dbe.RegisterComponentFromAccessor<WbAsFmKeepV1>();
            dbe.InitializeArchetypes();
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.Spawn<WbAsFmV1>(WbAsFmV1.Keep.Set(new WbAsFmKeepV1 { V = 3 }));
                Assert.That(tx.Commit(), Is.True);
            }

            dbe.ForceCheckpoint();
        }

        using var wbProvider = Provider(EngineLifecycle.ConfigureEngineOptions);
        using var wbScope = wbProvider.CreateScope();
        var engine = wbScope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        engine.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(WbAsFmV1);
        var loaded = new LoadedSchema([typeof(WbAsFmKeepV2).Assembly], [typeof(WbAsFmKeepV2), typeof(WbAsFmAdded), typeof(WbAsKeep)],
            ["Workbench.Test.WbAsFmKeep", "Workbench.Test.WbAsFmAdded", "Workbench.Test.WbAsKeep"], [typeof(WbAsFmV2)]);
        var registered = SchemaCompatibility.ClassifyAndRegister(engine, loaded);
        Assert.That(engine.HasComponentMigrations, Is.True, "premise: the shared component widens in this open");

        var result = SchemaCompatibility.InitializeArchetypes(engine, registered, out var failure);
        var kinds = Array.ConvertAll(result.Diagnostics, d => d.Kind);
        var firstNote = Array.IndexOf(kinds, SchemaCompatibility.NotInDatabaseKind);
        var setChange = Array.IndexOf(kinds, SchemaCompatibility.ArchetypeSetChangeKind);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.InstanceOf<ArchetypeSchemaException>(), "premise: the open refused (R10)");
            Assert.That((failure as ArchetypeSchemaException)?.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.UnopenedHolder));
            Assert.That(firstNote, Is.GreaterThanOrEqualTo(0), "premise: the loaded schema declares components the file lacks");
            Assert.That(result.Diagnostics[0].Kind, Is.EqualTo(SchemaCompatibility.ArchetypeSchemaRefusedKind), "the refusal leads");
            Assert.That(result.Diagnostics[0].Detail, Is.EqualTo(failure?.Message));
            Assert.That(setChange, Is.GreaterThan(0), "the set change the open recorded before it threw is reported too");
            Assert.That(result.Diagnostics[setChange].ComponentName, Is.EqualTo("Wb.FieldUnderSet"));
            Assert.That(setChange, Is.LessThan(firstNote), "ahead of the not-in-database notes");
            Assert.That(result.State, Is.EqualTo(SchemaCompatibility.State.MigrationRequired));
        });
    }

    /// <summary>
    /// D4: an archetype the loaded build's declaration refuses (here R1, a set change at the stored revision) is skipped and reported, not a failed open:
    /// the rest of the database opens, and the banner names the archetype with the engine's refusal.
    /// </summary>
    [Test]
    public void RefusedDeclaration_IsSkippedAndReported_TheOpenCompletes()
    {
        CreateRevision1Database(addedInFile: true);

        using var provider = Provider(EngineLifecycle.ConfigureEngineOptions);
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        engine.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(WbAsV1) || meta.ArchetypeType == typeof(WbAsV2);
        var loaded = new LoadedSchema([typeof(WbAsKeep).Assembly], [typeof(WbAsKeep), typeof(WbAsAdded)],
            ["Workbench.Test.WbAsKeep", "Workbench.Test.WbAsAdded"], [typeof(WbAsR1Same), typeof(WbAsHolder)]);
        var result = SchemaCompatibility.InitializeArchetypes(engine, SchemaCompatibility.ClassifyAndRegister(engine, loaded), out var failure);
        var archetype = result.Diagnostics.Where(d => d.Kind == SchemaCompatibility.ArchetypeSetChangeKind).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null, "the open completes");
            Assert.That(engine.RefusedArchetypeSetChanges.Single().Kind, Is.EqualTo(ArchetypeSetChangeKind.Refused));
            Assert.That(archetype, Has.Length.EqualTo(1));
            Assert.That(archetype[0].ComponentName, Is.EqualTo("Wb.ArchetypeSet"));
            Assert.That(archetype[0].Detail, Does.Contain("is refused against the stored one").And.Contain("but is still at revision 1"));
            Assert.That(result.State, Is.EqualTo(SchemaCompatibility.State.MigrationRequired));
        });
    }

    /// <summary>
    /// A settle whose crash open refuses because the Refuse policy left an archetype of its window unopened (7013) does not need the application's
    /// schema — it has it — and must not ask for it: only 7012 does.
    /// </summary>
    [Test]
    public void SettleRefusedForASetChange_IsNotAMissingSchema()
    {
        var refused = new DurabilityException(TyphonErrorCode.RecoveryArchetypeSetChangeRefused, "refused by ArchetypeSetMigration = Refuse");
        var unregistered = new DurabilityException(TyphonErrorCode.RecoveryArchetypeNotRegistered, "not registered");
        Assert.Multiple(() =>
        {
            Assert.That(BackupSettle.NeedsSchema(refused), Is.False);
            Assert.That(BackupSettle.NeedsSchema(new InvalidOperationException("open failed", refused)), Is.False);
            Assert.That(BackupSettle.NeedsSchema(unregistered), Is.True);
        });
    }

    /// <summary>R10 reaches the Workbench as a clear diagnostic, named after the archetype, carrying the engine's message rather than a stack trace.</summary>
    [Test]
    public void InitializeArchetypesRefusal_IsAClearDiagnostic()
    {
        var refusal = new ArchetypeSchemaException("Wb.Holder", ArchetypeSchemaErrorKind.UnopenedHolder,
            "Component 'X' changes layout in this open, but archetype 'Wb.Holder' stores it and is not opened by this process (…). Nothing was modified.");
        var diagnostic = SchemaCompatibility.DescribeInitializeArchetypesFailure(refusal);
        var other = SchemaCompatibility.DescribeInitializeArchetypesFailure(new InvalidOperationException("boom"));
        Assert.Multiple(() =>
        {
            Assert.That((diagnostic.ComponentName, diagnostic.Kind, diagnostic.Detail),
                Is.EqualTo(("Wb.Holder", SchemaCompatibility.ArchetypeSchemaRefusedKind, refusal.Message)));
            Assert.That((other.ComponentName, other.Kind), Is.EqualTo(("(InitializeArchetypes)", SchemaCompatibility.ArchetypeInitFailedKind)));
            Assert.That(other.Detail, Does.Contain("boom"));
        });
    }
}
