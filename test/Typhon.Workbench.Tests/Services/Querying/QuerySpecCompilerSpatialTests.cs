using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Services.Querying;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Services.Querying;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Test-only spatial schema. SpatPos is the [SpatialIndex] position; SpatMeta carries an indexed non-spatial field for the
// SPATIAL + WHERE composition and "component is not spatial" cases. Both SingleVersion → SpatArch is cluster-eligible
// (the realistic Workbench case: the SWG fixture's spatial archetypes are clustered too).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Workbench.Test.SpatPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatPos
{
    [Field] [SpatialIndex(1.0f)] public AABB2F Bounds;
}

[Component("Workbench.Test.SpatMeta", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatMeta
{
    [Index] public int Level;       // indexed → usable in WHERE / ORDER BY; intentionally NOT spatial
}

[Archetype(3010)]
partial class SpatArch : Archetype<SpatArch>
{
    public static readonly Comp<SpatPos> Pos = Register<SpatPos>();
    public static readonly Comp<SpatMeta> Meta = Register<SpatMeta>();
}

/// <summary>
/// Direct coverage of <see cref="QuerySpecCompiler"/>'s SPATIAL compilation — DSL → compiler → <c>EcsQuery.WhereNearby /
/// WhereInAABB / WhereRay</c> → execution. The engine already implements spatial queries; this proves the Workbench
/// emits them correctly (incl. the 2D-vs-3D AABB argument repack), composes them with WHERE, and rejects the engine
/// limits with stable error codes.
/// </summary>
/// <remarks>
/// Drives a raw <see cref="DatabaseEngine"/> rather than a Workbench session because spatial archetypes are
/// cluster-eligible and require <c>ConfigureSpatialGrid</c> BEFORE <c>InitializeArchetypes</c> — which a session's
/// auto-initialising <c>OpenAsync</c> doesn't allow. The compiler is a pure static function of (spec, engine, tx), so
/// this exercises exactly the same path the service uses. Entities sit on the line y=500 so AABB counts are exact.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class QuerySpecCompilerSpatialTests
{
    private const float WorldSize = 10_000f;
    private const int EntityCount = 8;       // i = 0..7, centred at x = 1000*i + 500, y = 500, half-extent 10, Level = i
    private const float HalfExtent = 10f;

    private string _tempDir;
    private ServiceProvider _sp;
    private DatabaseEngine _engine;

    [OneTimeSetUp]
    public void OneTimeSetup() => Archetype<SpatArch>.Touch();

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-spatial-compiler", Guid.NewGuid().ToString("N"));
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
                opts.DatabaseName = "spatial-compiler";
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

        _engine.RegisterComponentFromAccessor<SpatPos>();
        _engine.RegisterComponentFromAccessor<SpatMeta>();
        _engine.ConfigureSpatialGrid(new SpatialGridConfig(new Vector2(0f, 0f), new Vector2(WorldSize, WorldSize), cellSize: 100f));
        _engine.InitializeArchetypes();

        using var tx = _engine.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var cx = 1000f * i + 500f;
            const float cy = 500f;
            var pos = new SpatPos
            {
                Bounds = new AABB2F { MinX = cx - HalfExtent, MinY = cy - HalfExtent, MaxX = cx + HalfExtent, MaxY = cy + HalfExtent },
            };
            tx.Spawn<SpatArch>(SpatArch.Pos.Set(pos), SpatArch.Meta.Set(new SpatMeta { Level = i }));
        }
        tx.Commit();
    }

    [TearDown]
    public void TearDown()
    {
        _sp?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private int RunCount(string dsl)
    {
        var parse = DslParser.Parse(dsl);
        Assert.That(parse.Errors, Is.Empty, $"DSL should parse cleanly: {dsl}");
        using var tx = _engine.CreateReadOnlyTransaction();
        var compiled = QuerySpecCompiler.Compile(parse.Spec, _engine, tx);
        return compiled.Execute(CancellationToken.None).Count;
    }

    private WorkbenchException CompileError(string dsl)
    {
        var parse = DslParser.Parse(dsl);
        Assert.That(parse.Errors, Is.Empty, $"DSL should parse cleanly (the rejection is a compile error, not a parse error): {dsl}");
        using var tx = _engine.CreateReadOnlyTransaction();
        return Assert.Throws<WorkbenchException>(() => QuerySpecCompiler.Compile(parse.Spec, _engine, tx));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Positive — each kind compiles, executes, and returns the geometrically-correct set
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void Aabb_CoveringWorld_ReturnsAllEntities()
    {
        // The 2D AABB repack is the load-bearing case: the DSL's [minX,minY,minZ,maxX,maxY,maxZ] must become
        // WhereInAABB(minX, minY, maxX, maxY, _, _) for the 2D component, or the box inverts and returns nothing.
        Assert.That(RunCount("FROM SpatArch SPATIAL Workbench.Test.SpatPos AABB 0, 0, 0, 10000, 10000, 0"), Is.EqualTo(EntityCount));
    }

    [Test]
    public void Aabb_SubRegion_ReturnsOverlappingEntitiesOnly()
    {
        // x ≤ 2500 covers entities centred at 500, 1500, 2500 (the 2500 box spans 2490..2510, overlapping maxX=2500).
        Assert.That(RunCount("FROM SpatArch SPATIAL Workbench.Test.SpatPos AABB 0, 0, 0, 2500, 1000, 0"), Is.EqualTo(3));
    }

    [Test]
    public void Nearby_BroadRadius_ReturnsAllEntities()
    {
        Assert.That(RunCount("FROM SpatArch SPATIAL Workbench.Test.SpatPos NEARBY 5000, 500, 0 RADIUS 100000"), Is.EqualTo(EntityCount));
    }

    [Test]
    public void Nearby_TightRadius_ReturnsClosestEntitiesOnly()
    {
        // From (500,500): entity 0 dist 0, entity 1 (centre 1500) nearest-edge dist ~990 (< 1200), entity 2 dist ~1990 (> 1200).
        Assert.That(RunCount("FROM SpatArch SPATIAL Workbench.Test.SpatPos NEARBY 500, 500, 0 RADIUS 1200"), Is.EqualTo(2));
    }

    [Test]
    public void Spatial_ComposedWithWhere_NarrowsResult()
    {
        // AABB covers all 8; WHERE Level >= 4 keeps entities 4..7 → spatial candidates AND the indexed predicate.
        Assert.That(
            RunCount("FROM SpatArch WHERE Workbench.Test.SpatMeta.Level >= 4 SPATIAL Workbench.Test.SpatPos AABB 0, 0, 0, 10000, 10000, 0"),
            Is.EqualTo(4));
    }

    [Test]
    public void Ray_OnClusterArchetype_EngineThrowsNotSupported()
    {
        // Compilation succeeds (WhereRay is emitted); execution throws because the cluster tier serves only AABB + Radius
        // (#230 Option B). QueryConsoleService maps this NotSupportedException to a stable 400 'spatial_shape_not_supported'.
        var parse = DslParser.Parse("FROM SpatArch SPATIAL Workbench.Test.SpatPos RAY 0, 500, 0, 1, 0, 0, 100000");
        Assert.That(parse.Errors, Is.Empty);
        using var tx = _engine.CreateReadOnlyTransaction();
        var compiled = QuerySpecCompiler.Compile(parse.Spec, _engine, tx);
        Assert.Throws<NotSupportedException>(() => compiled.Execute(CancellationToken.None));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Negative — the three engine limits surface as stable error codes
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void TwoSpatialClauses_Rejected_SingleClauseOnly()
    {
        var ex = CompileError(
            "FROM SpatArch SPATIAL Workbench.Test.SpatPos AABB 0,0,0,1,1,0 SPATIAL Workbench.Test.SpatPos AABB 0,0,0,1,1,0");
        Assert.That(ex.ErrorCode, Is.EqualTo("spatial_single_clause_only"));
    }

    [Test]
    public void SpatialWithOrderBy_Rejected_OrderByUnsupported()
    {
        var ex = CompileError(
            "FROM SpatArch SPATIAL Workbench.Test.SpatPos AABB 0,0,0,10000,10000,0 ORDER BY Workbench.Test.SpatMeta.Level");
        Assert.That(ex.ErrorCode, Is.EqualTo("spatial_orderby_unsupported"));
    }

    [Test]
    public void SpatialOnNonSpatialComponent_Rejected_NotIndexed()
    {
        // SpatMeta has no [SpatialIndex] field — must be rejected before the engine NREs in Release.
        var ex = CompileError("FROM SpatArch SPATIAL Workbench.Test.SpatMeta NEARBY 0, 0, 0 RADIUS 1");
        Assert.That(ex.ErrorCode, Is.EqualTo("spatial_component_not_indexed"));
    }
}
