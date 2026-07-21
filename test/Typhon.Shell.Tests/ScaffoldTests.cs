using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Typhon.Shell.Commands;

namespace Typhon.Shell.Tests;

/// <summary>
/// Verifies the <c>typhon new</c> scaffold (#532/F2): it emits the full starter, the schema/app-template files are
/// byte-identical to the in-repo source they are embedded from (no drift), and the generated csproj carries exactly one
/// pinned <c>Typhon</c> package reference.
/// </summary>
[TestFixture]
public sealed class ScaffoldTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-scaffold-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void Emit_ProducesAllExpectedFiles()
    {
        var dir = Path.Combine(_tempDir, "Demo");
        ProjectScaffolder.Emit(dir, "Demo");

        foreach (var f in new[] { "Harvester.cs", "Program.cs", "Systems.cs", "typhon.telemetry.json", ".gitignore", "Demo.csproj", "README.md" })
        {
            var p = Path.Combine(dir, f);
            Assert.That(File.Exists(p), Is.True, $"{f} should be emitted");
            Assert.That(new FileInfo(p).Length, Is.GreaterThan(0), $"{f} should be non-empty");
        }
    }

    [Test]
    public void Emit_TemplateFilesAreByteIdenticalToInRepoSource()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(_tempDir, "Demo");
        ProjectScaffolder.Emit(dir, "Demo");

        AssertByteIdentical(Path.Combine(dir, "Harvester.cs"), Path.Combine(root, "samples", "Typhon.Samples.Swg", "Light", "Harvester.cs"));
        AssertByteIdentical(Path.Combine(dir, "Program.cs"), Path.Combine(root, "doc", "guide", "example", "Program.cs"));
        AssertByteIdentical(Path.Combine(dir, "Systems.cs"), Path.Combine(root, "doc", "guide", "example", "Systems.cs"));
        AssertByteIdentical(Path.Combine(dir, "typhon.telemetry.json"), Path.Combine(root, "doc", "guide", "example", "typhon.telemetry.json"));
        AssertByteIdentical(Path.Combine(dir, ".gitignore"), Path.Combine(root, "doc", "guide", "example", ".gitignore"));
    }

    [Test]
    public void Emit_CsprojHasExactlyOnePinnedTyphonPackageReference()
    {
        var dir = Path.Combine(_tempDir, "Demo");
        ProjectScaffolder.Emit(dir, "Demo");

        var csproj = File.ReadAllText(Path.Combine(dir, "Demo.csproj"));
        Assert.That(csproj, Does.Contain($"<PackageReference Include=\"Typhon\" Version=\"{ProjectScaffolder.TyphonPackageVersion}\""),
            "the csproj must pin the published Typhon package");
        Assert.That(Regex.Matches(csproj, "PackageReference").Count, Is.EqualTo(1), "exactly one PackageReference — the single Typhon dependency");
    }

    [Test]
    public void IsValidName_AcceptsSafeAndRejectsUnsafeNames()
    {
        Assert.That(ProjectScaffolder.IsValidName("MyApp"), Is.True);
        Assert.That(ProjectScaffolder.IsValidName("my.app-1"), Is.True);
        Assert.That(ProjectScaffolder.IsValidName(""), Is.False);
        Assert.That(ProjectScaffolder.IsValidName("../evil"), Is.False);
        Assert.That(ProjectScaffolder.IsValidName("has space"), Is.False);
        Assert.That(ProjectScaffolder.IsValidName("1leading-digit"), Is.False);
    }

    private static void AssertByteIdentical(string emitted, string source)
    {
        Assert.That(File.Exists(source), Is.True, $"in-repo source not found: {source}");
        Assert.That(File.ReadAllBytes(emitted), Is.EqualTo(File.ReadAllBytes(source)),
            $"{Path.GetFileName(emitted)} must be byte-identical to its in-repo source ({source})");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Typhon.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.That(dir, Is.Not.Null, "could not locate the repo root (Typhon.slnx) above the test bin directory");
        return dir!.FullName;
    }
}
