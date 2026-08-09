using System;
using System.IO;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;

namespace Typhon.Engine.Tests;

/// <summary>
/// Manual-run wrapper around <see cref="FixtureDatabase.CreateOrReuse"/>. Produces a populated
/// <c>base-tests.typhon</c> database under <c>TestOutput/workbench-fixture</c> that can be opened
/// in the Workbench to iterate on panels without having to drive the Dev Fixture tab.
///
/// To run: temporarily remove the <see cref="IgnoreAttribute"/> below and execute
///   <c>dotnet test --filter "FullyQualifiedName~WorkbenchFixtureGenerator"</c>
/// …then open the printed path with the Workbench. The DEBUG-gated Dev Fixture tab in the Workbench
/// runs the same <see cref="FixtureDatabase.CreateOrReuse"/> method — the test is here only for
/// headless CI / scripted regeneration.
/// </summary>
[TestFixture]
public sealed class WorkbenchFixtureGenerator
{
    private static string OutputDirectory => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestOutput", "workbench-fixture");

    // Manual tier: a developer tool, not a test — it PRODUCES base-tests.typhon for the Workbench and asserts nothing
    // about the engine. [Explicit] rather than [Ignore] so `--filter` can actually invoke it without editing source.
    [Test]
    [Explicit("Fixture generator — produces base-tests.typhon for the Workbench")]
    [Category("Manual")]
    public void GenerateBaseTestsDatabase()
    {
        var outDir = Path.GetFullPath(OutputDirectory);
        var result = FixtureDatabase.CreateOrReuse(outDir, force: true);

        Assert.That(File.Exists(result.TyphonFilePath), Is.True, "Typhon marker file should exist");
        Assert.That(File.Exists(result.SchemaDllPath), Is.True, "Fixture schema DLL should be copied next to the DB");

        TestContext.Out.WriteLine();
        TestContext.Out.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
        TestContext.Out.WriteLine($"  Workbench fixture {(result.WasCreated ? "generated" : "reused")}.");
        TestContext.Out.WriteLine($"  Directory : {outDir}");
        TestContext.Out.WriteLine($"  Open file : {result.TyphonFilePath}");
        TestContext.Out.WriteLine($"  Entities  : {result.TotalEntities:N0}");
        TestContext.Out.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
    }
}
