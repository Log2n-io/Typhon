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
// A realm-keyed spatial schema: the position carries the [SpatialIndex] bounds, a sibling component carries the
// [RealmKey] — the shape every placement in the SWG demo uses, and the only shape in which "which realm?" is a question.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Workbench.Test.RealmSpatPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct RealmSpatPos
{
    [Field] [SpatialIndex] public AABB2F Bounds;
}

[Component("Workbench.Test.RealmSpatKey", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct RealmSpatKey
{
    [Field] [RealmKey] public ushort Realm;
}

[Archetype]
partial class RealmSpatArch : Archetype<RealmSpatArch>
{
    public static readonly Comp<RealmSpatPos> Pos = Register<RealmSpatPos>();
    public static readonly Comp<RealmSpatKey> Key = Register<RealmSpatKey>();
}

/// <summary>
/// WB-06 / #1083 rung 4 — a SPATIAL query answers the realm it was asked for, and refuses to pick one for you.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this fixture exists for.</b> <c>QuerySpecCompiler.ApplySpatial</c> never called
/// <c>EcsQuery.InRealm</c>, and that method's own documentation reads <i>"Realm 0 when never called"</i> — so every
/// SPATIAL query the Query Console has run since #386 searched realm 0 whatever the author meant. Right by accident on
/// a single-realm database; on a realm database, an empty result for a dungeon with no reason given.
/// </para>
/// <para>
/// <b>Both realms hold entities at the SAME coordinates on purpose.</b> Realm isolation is not decided by geometry —
/// identical local coordinates in two realms is the expected case (SUB-28's own "never" clause) — so a test whose
/// realms were geometrically separated would pass against a compiler that ignored realms entirely and simply searched
/// a box that happened to contain one of them.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class QuerySpecCompilerRealmTests
{
    private const float WorldSize = 10_000f;
    private const ushort Interior = 1;
    private const int InRealmZero = 3;
    private const int InInterior = 5;

    private string _tempDir;
    private ServiceProvider _sp;
    private DatabaseEngine _engine;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-realm-compiler", Guid.NewGuid().ToString("N"));
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
                opts.DatabaseName = "realm-compiler";
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

        _engine.RegisterComponentFromAccessor<RealmSpatPos>();
        _engine.RegisterComponentFromAccessor<RealmSpatKey>();
        _engine.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0f, 0f), new Vector2(WorldSize, WorldSize), cellSize: 100f));
        _engine.ConfigureRealms(Interior + 1);
        _engine.Realms.Register(
            new RealmId(Interior),
            RealmConfig.SimulatedAlways(SpatialGridConfig.Flat(new Vector2(0f, 0f), new Vector2(WorldSize, WorldSize), cellSize: 100f)));
        _engine.InitializeArchetypes();

        using var tx = _engine.CreateQuickTransaction();
        Spawn(tx, RealmId.Default.Value, InRealmZero);
        Spawn(tx, Interior, InInterior);
        tx.Commit();
    }

    /// <summary>Spawns <paramref name="count"/> entities of <paramref name="realm"/>, all at the same place.</summary>
    private static void Spawn(Transaction tx, ushort realm, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var pos = new RealmSpatPos
            {
                Bounds = new AABB2F { MinX = 490f, MinY = 490f, MaxX = 510f, MaxY = 510f },
            };
            tx.Spawn<RealmSpatArch>(RealmSpatArch.Pos.Set(pos), RealmSpatArch.Key.Set(new RealmSpatKey { Realm = realm }));
        }
    }

    [TearDown]
    public void TearDown()
    {
        _sp?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
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
        Assert.That(parse.Errors, Is.Empty, $"the rejection is a compile error, not a parse error: {dsl}");
        using var tx = _engine.CreateReadOnlyTransaction();
        return Assert.Throws<WorkbenchException>(() => QuerySpecCompiler.Compile(parse.Spec, _engine, tx));
    }

    private const string Box = "FROM RealmSpatArch\nSPATIAL Workbench.Test.RealmSpatPos AABB 0, 0, 0, 10000, 10000, 0";

    /// <summary>
    /// The assertion the whole change is for: the same box answers differently per realm.
    /// </summary>
    /// <remarks>
    /// Before this item both queries returned realm 0's three entities, because the compiler never named a realm and the
    /// engine's documented default is realm 0. The interior's five were unreachable from the Query Console at all.
    /// </remarks>
    [Test]
    public void TheSameBoxAnswersTheRealmItWasAskedFor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunCount($"{Box} IN REALM 0"), Is.EqualTo(InRealmZero));
            Assert.That(RunCount($"{Box} IN REALM {Interior}"), Is.EqualTo(InInterior),
                "the interior's entities were unreachable from the Console before WB-06 — the query silently answered realm 0");
        });
    }

    /// <summary>
    /// A realm-less spatial query on a realm database is refused, not defaulted.
    /// </summary>
    /// <remarks>
    /// This is the case that used to return a plausible wrong answer, which is worse than an error: three rows come
    /// back, they are real entities, and nothing says they are the wrong world's. The engine takes the same stance from
    /// the other side — <c>CheckRealmScope</c> refuses a realm named with no spatial predicate to consume it.
    /// </remarks>
    [Test]
    public void ARealmLessSpatialQueryIsRefusedOnADatabaseThatHoldsSeveralRealms()
    {
        var error = CompileError(Box);

        Assert.Multiple(() =>
        {
            Assert.That(error.ErrorCode, Is.EqualTo("spatial_realm_required"));
            Assert.That(error.Message, Does.Contain("IN REALM"), "the refusal must name the syntax that fixes it");
            Assert.That(error.Message, Does.Contain("realm 0"), "and must say what it would otherwise have answered");
        });
    }

    /// <summary>
    /// An id in range but not registered is refused — at EXECUTION, which is where the engine checks it.
    /// </summary>
    /// <remarks>
    /// <b>Not at compile, and that is the engine's documented contract rather than an oversight here:</b> <c>InRealm</c>
    /// states "the realm must be registered; the check runs when the query executes". Realms register and unregister at
    /// run time, so a compile-time check would be answering a question whose answer can change before the query runs.
    /// Asserting it at compile time would have pinned the wrong contract.
    /// </remarks>
    [Test]
    public void AnUnregisteredRealmIsRefusedWhenTheQueryRuns()
    {
        var parse = DslParser.Parse($"{Box} IN REALM 9");
        Assert.That(parse.Errors, Is.Empty);
        using var tx = _engine.CreateReadOnlyTransaction();
        var compiled = QuerySpecCompiler.Compile(parse.Spec, _engine, tx);

        Assert.Throws<InvalidOperationException>(() => compiled.Execute(CancellationToken.None));
    }

    /// <summary>A non-spatial query is untouched: `IN REALM` scopes a spatial predicate and there is none to scope.</summary>
    [Test]
    public void ANonSpatialQueryIsUnaffectedByTheRefusal()
    {
        Assert.That(RunCount("FROM RealmSpatArch"), Is.EqualTo(InRealmZero + InInterior),
            "without a spatial predicate the query walks every realm, which is what it did before and still means");
    }
}
