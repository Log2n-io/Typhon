using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for user-source-location capture on the public Query API surface.
/// Asserts that <c>[CallerFilePath]</c> / <c>[CallerLineNumber]</c> / <c>[CallerMemberName]</c> substitution works at user call sites,
/// is stored on the constructed View / EcsQuery, and produces repo-relative paths under the test project's MSBuild config.
/// Part of P0 (issue #333) of the Query Profiling umbrella (#342).
/// </summary>
/// <remarks>
/// Execution-site capture on <c>.Execute()</c>, <c>.Count()</c>, <c>.Any()</c>, <c>view.Refresh(tx)</c>, etc. is accepted by the
/// signatures (verified by the build) but the values are not yet observable from outside the engine — P2 (issue #335) wires them
/// into the trace events. Those tests will live alongside the P2 implementation.
/// </remarks>
[TestFixture]
[NonParallelizable]
class QuerySourceLocationTests : TestBase<QuerySourceLocationTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<EcsUnit>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Captures the current source line + file at the call site, so tests can assert against the right values without hardcoding line numbers.</summary>
    private static (string file, int line, string method) Here(
        [CallerFilePath]   string callerFile = null,
        [CallerLineNumber] int    callerLine = 0,
        [CallerMemberName] string callerMethod = null)
        => (callerFile, callerLine, callerMethod);

    [Test]
    [CancelAfter(15000)]
    public void Query_CapturesUserConstructionSite()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var query = tx.Query<EcsUnit>(); var (expectedFile, expectedLine, expectedMethod) = Here();

        Assert.That(query.SourceFile, Is.EqualTo(expectedFile), "tx.Query<T>() must capture the caller's source file via [CallerFilePath]");
        // Construction is on the same line as Here(); line number must be identical.
        Assert.That(query.SourceLine, Is.EqualTo(expectedLine));
        Assert.That(query.SourceMethod, Is.EqualTo(expectedMethod), "Method name should match the enclosing test method");
    }

    [Test]
    [CancelAfter(15000)]
    public void QueryExact_CapturesUserConstructionSite()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var query = tx.QueryExact<EcsUnit>(); var (expectedFile, expectedLine, expectedMethod) = Here();

        Assert.That(query.SourceFile, Is.EqualTo(expectedFile));
        Assert.That(query.SourceLine, Is.EqualTo(expectedLine));
        Assert.That(query.SourceMethod, Is.EqualTo(expectedMethod));
    }

    [Test]
    [CancelAfter(15000)]
    public void ToView_CapturesUserConstructionSite()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var view = tx.Query<EcsUnit>().ToView(); var (expectedFile, expectedLine, expectedMethod) = Here();

        Assert.That(view.SourceFile, Is.EqualTo(expectedFile));
        Assert.That(view.SourceLine, Is.EqualTo(expectedLine));
        Assert.That(view.SourceMethod, Is.EqualTo(expectedMethod));
        view.Dispose();
    }

    [Test]
    [CancelAfter(15000)]
    public void Query_SourceFile_IsRepoRelative()
    {
        // Typhon's Directory.Build.props sets <PathMap>$(MSBuildThisFileDirectory)=/_/</PathMap> + <DeterministicSourcePaths>true</DeterministicSourcePaths>.
        // The test project inherits these properties, so [CallerFilePath] values are normalized to the repo-relative /_/ form.
        // Hosts that don't inherit this config will ship machine-absolute paths (documented behavior — see issue #333 / typhon-profiler.md update in P1).
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var query = tx.Query<EcsUnit>();

        Assert.That(query.SourceFile, Is.Not.Null);
        Assert.That(query.SourceFile, Does.Contain("/_/").Or.Contain(@"\_\"),
            "Path should be repo-relative (start with /_/ from PathMap normalization). Found: " + query.SourceFile);
        Assert.That(query.SourceFile, Does.Contain("QuerySourceLocationTests"));
        // No drive letters / Windows-absolute roots.
        Assert.That(query.SourceFile, Does.Not.Match(@"^[A-Za-z]:"), "Source path must not be machine-absolute (PathMap should strip the repo root)");
    }

    [Test]
    [CancelAfter(15000)]
    public void Query_WithoutCallerInfo_DefaultsAreUsable()
    {
        // The engine itself calls EcsQuery's internal ctor without caller info during ToPullView/ToIncrementalView (internal recurse via .Execute()).
        // Verify that constructing with explicit nulls/zeros works (mirrors the engine-internal path).
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // Explicit nulls override the [Caller*] defaults — same shape the engine-internal call uses.
        var query = tx.Query<EcsUnit>(sourceFile: null, sourceLine: 0, sourceMethod: null);

        Assert.That(query.SourceFile, Is.Null);
        Assert.That(query.SourceLine, Is.EqualTo(0));
        Assert.That(query.SourceMethod, Is.Null);
        Assert.That(query.EcsQueryId, Is.GreaterThan(0), "EcsQueryId is still assigned even without source attribution");
    }

    [Test]
    [CancelAfter(15000)]
    public void ExecutionTerminals_AcceptCallerAttributesWithoutCrashing()
    {
        // P0 just makes the terminals accept the attribute trio; P2 will plumb the values into trace events.
        // For now we just verify the API surface compiles and runs.
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var count = tx.Query<EcsUnit>().Count();
        var any = tx.Query<EcsUnit>().Any();
        var entities = tx.Query<EcsUnit>().Execute();

        Assert.That(count, Is.GreaterThanOrEqualTo(0));
        Assert.That(any, Is.False.Or.True);
        Assert.That(entities, Is.Not.Null);
    }
}
