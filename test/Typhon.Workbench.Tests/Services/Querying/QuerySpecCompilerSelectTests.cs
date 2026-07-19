using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Services.Querying;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Services.Querying;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Test-only schema for SELECT projection. Two plain Versioned components, each with an indexed field (so they can also
// drive WHERE / ORDER BY). No spatial / cluster machinery — SELECT is a pure projection concern, independent of storage.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Workbench.Test.SelA", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct SelA
{
    [Field] [Index] public int Av;
    [Field] public int Ax;
}

[Component("Workbench.Test.SelB", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct SelB
{
    [Field] [Index] public int Bv;
    [Field] public int Bx;
}

[Archetype(3011)]
partial class SelArch : Archetype<SelArch>
{
    public static readonly Comp<SelA> A = Register<SelA>();
    public static readonly Comp<SelB> B = Register<SelB>();
}

/// <summary>
/// Direct coverage of <see cref="QuerySpecCompiler"/>'s SELECT (projection) compilation. SELECT decides which
/// components' fields become result columns — <see cref="CompiledQuery.ProjectedComponents"/>. These assertions pin the
/// contract that SELECT changes: it is authoritative when present (replaces the WHERE-inferred projection), and the
/// no-SELECT default is byte-for-byte unchanged (WHERE component projected; nothing projected without WHERE).
/// </summary>
/// <remarks>
/// Drives a raw <see cref="DatabaseEngine"/> like <see cref="QuerySpecCompilerSpatialTests"/> — the compiler is a pure
/// static function of (spec, engine, tx), so this is exactly the path the service uses. SELECT never touches the
/// EcsQuery (the engine returns ids; projection is a Workbench read concern), so the assertions are on the projected
/// component set, not on Execute().
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class QuerySpecCompilerSelectTests
{
    private string _tempDir;
    private ServiceProvider _sp;
    private DatabaseEngine _engine;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-select-compiler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var walDir = Path.Combine(_tempDir, "wal");
        Directory.CreateDirectory(walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = "select-compiler";
                opts.DatabaseDirectory = _tempDir;
                opts.DatabaseCacheSize = 8192UL * 8192;
                opts.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions { WalDirectory = walDir, UseFUA = false };
            });
        _sp = services.BuildServiceProvider();
        _engine = _sp.GetRequiredService<DatabaseEngine>();

        _engine.RegisterComponentFromAccessor<SelA>();
        _engine.RegisterComponentFromAccessor<SelB>();
        _engine.InitializeArchetypes();

        using var tx = _engine.CreateQuickTransaction();
        for (var i = 0; i < 4; i++)
        {
            tx.Spawn<SelArch>(SelArch.A.Set(new SelA { Av = i, Ax = i * 10 }), SelArch.B.Set(new SelB { Bv = i, Bx = i * 100 }));
        }
        tx.Commit();
    }

    [TearDown]
    public void TearDown()
    {
        _sp?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>Compile the DSL and return the registered names of the projected components (the result columns' source).</summary>
    private string[] ProjectedNames(string dsl)
    {
        var parse = DslParser.Parse(dsl);
        Assert.That(parse.Errors, Is.Empty, $"DSL should parse cleanly: {dsl}");
        using var tx = _engine.CreateReadOnlyTransaction();
        var compiled = QuerySpecCompiler.Compile(parse.Spec, _engine, tx);
        return compiled.ProjectedComponents.Select(c => c.Definition.Name).ToArray();
    }

    private WorkbenchException CompileError(string dsl)
    {
        var parse = DslParser.Parse(dsl);
        Assert.That(parse.Errors, Is.Empty, $"DSL should parse cleanly (the rejection is a compile error): {dsl}");
        using var tx = _engine.CreateReadOnlyTransaction();
        return Assert.Throws<WorkbenchException>(() => QuerySpecCompiler.Compile(parse.Spec, _engine, tx));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // SELECT projects the named components
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void Select_ProjectsNamedComponent()
    {
        Assert.That(ProjectedNames("FROM SelArch SELECT Workbench.Test.SelA"), Is.EqualTo(new[] { "Workbench.Test.SelA" }));
    }

    [Test]
    public void Select_MultipleComponents_ProjectsAllInOrder()
    {
        Assert.That(
            ProjectedNames("FROM SelArch SELECT Workbench.Test.SelA, Workbench.Test.SelB"),
            Is.EqualTo(new[] { "Workbench.Test.SelA", "Workbench.Test.SelB" }));
    }

    [Test]
    public void Select_RepeatedComponent_IsDeduped()
    {
        Assert.That(
            ProjectedNames("FROM SelArch SELECT Workbench.Test.SelA, Workbench.Test.SelA"),
            Is.EqualTo(new[] { "Workbench.Test.SelA" }));
    }

    [Test]
    public void Select_IsAuthoritative_ReplacesWhereProjection()
    {
        // WHERE filters on SelA but SELECT projects SelB → result columns are SelB's only (SELECT, not WHERE, owns columns).
        Assert.That(
            ProjectedNames("FROM SelArch WHERE Workbench.Test.SelA.Av >= 0 SELECT Workbench.Test.SelB"),
            Is.EqualTo(new[] { "Workbench.Test.SelB" }));
    }

    [Test]
    public void Select_UnknownComponent_Rejected()
    {
        var ex = CompileError("FROM SelArch SELECT Workbench.Test.Nope");
        Assert.That(ex.ErrorCode, Is.EqualTo("unknown_component"));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Default (no SELECT) is byte-for-byte unchanged — the load-bearing regression guard
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void NoSelect_WithWhere_ProjectsWhereComponent()
    {
        Assert.That(
            ProjectedNames("FROM SelArch WHERE Workbench.Test.SelA.Av >= 0"),
            Is.EqualTo(new[] { "Workbench.Test.SelA" }));
    }

    [Test]
    public void NoSelect_NoWhere_ProjectsNothing()
    {
        // The original "only EntityId" behaviour: no projecting clause → empty projection → EntityId-only rows.
        Assert.That(ProjectedNames("FROM SelArch"), Is.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // ORDER BY is validated against the WHERE component, independent of SELECT projection
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void OrderBy_WithWhere_WhileSelectingAnotherComponent_Compiles()
    {
        // ORDER BY's WHERE prerequisite is on SelA (satisfied); SELECT projects SelB. The two are decoupled: ordering
        // compiles, projection stays SELECT's choice.
        Assert.That(
            ProjectedNames("FROM SelArch WHERE Workbench.Test.SelA.Av >= 0 SELECT Workbench.Test.SelB ORDER BY Workbench.Test.SelA.Av"),
            Is.EqualTo(new[] { "Workbench.Test.SelB" }));
    }

    [Test]
    public void OrderBy_WithoutWhere_Rejected_EvenWhenComponentIsSelected()
    {
        // SELECT-ing SelA must NOT satisfy ORDER BY's WHERE prerequisite — ordering needs a WHERE on the same component.
        var ex = CompileError("FROM SelArch SELECT Workbench.Test.SelA ORDER BY Workbench.Test.SelA.Av");
        Assert.That(ex.ErrorCode, Is.EqualTo("invalid_query_syntax"));
    }
}
