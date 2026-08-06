using System;
using System.IO;
using NUnit.Framework;
using Typhon.Shell.Commands;

namespace Typhon.Shell.Tests;

/// <summary>
/// Verifies <c>typhon ui --open-db</c> database discovery (<see cref="UiCommand.FindDatabaseInDirectory"/>): a Typhon
/// database is a <c>*.typhon</c> <b>directory</b>, so discovery must pick the sole one, and report a clear error when
/// there are none or several (the CLI can't guess which).
/// </summary>
[TestFixture]
public sealed class UiCommandTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void FindDatabase_SingleTyphonDirectory_ReturnsIt()
    {
        var db = Path.Combine(_tempDir, "swg-guide.typhon");
        Directory.CreateDirectory(db);

        var result = UiCommand.FindDatabaseInDirectory(_tempDir, out var error);

        Assert.That(error, Is.Null);
        Assert.That(result, Is.EqualTo(Path.GetFullPath(db)));
    }

    [Test]
    public void FindDatabase_NoTyphonDirectory_ReportsError()
    {
        // A file that merely ends in .typhon is not a database (a db is a directory) — must not be matched.
        File.WriteAllText(Path.Combine(_tempDir, "not-a-db.typhon"), "x");

        var result = UiCommand.FindDatabaseInDirectory(_tempDir, out var error);

        Assert.That(result, Is.Null);
        Assert.That(error, Does.Contain("No *.typhon database"));
    }

    [Test]
    public void FindDatabase_MultipleTyphonDirectories_ReportsAmbiguityWithNames()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "a.typhon"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "b.typhon"));

        var result = UiCommand.FindDatabaseInDirectory(_tempDir, out var error);

        Assert.That(result, Is.Null);
        Assert.That(error, Does.Contain("Multiple *.typhon databases"));
        Assert.That(error, Does.Contain("a.typhon").And.Contain("b.typhon"));
    }

    [Test]
    public void FindDatabase_MissingDirectory_ReportsError()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");

        var result = UiCommand.FindDatabaseInDirectory(missing, out var error);

        Assert.That(result, Is.Null);
        Assert.That(error, Does.Contain("No *.typhon database"));
    }

    [Test]
    public void DeriveAssemblyName_SingleCsproj_ReturnsBaseName()
    {
        File.WriteAllText(Path.Combine(_tempDir, "MyApp.csproj"), "<Project/>");

        Assert.That(UiCommand.DeriveAssemblyName(_tempDir), Is.EqualTo("MyApp"));
    }

    [Test]
    public void DeriveAssemblyName_ZeroOrMultipleCsproj_ReturnsNull()
    {
        Assert.That(UiCommand.DeriveAssemblyName(_tempDir), Is.Null, "no csproj → null");

        File.WriteAllText(Path.Combine(_tempDir, "A.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(_tempDir, "B.csproj"), "<Project/>");
        Assert.That(UiCommand.DeriveAssemblyName(_tempDir), Is.Null, "ambiguous → null");
    }

    [Test]
    public void FindSchemaAssembly_ReturnsNewestNonRefMatch()
    {
        // bin/Debug/net10.0/MyApp.dll (older) and bin/Release/net10.0/MyApp.dll (newer) → newest wins.
        var dbg = Path.Combine(_tempDir, "bin", "Debug", "net10.0");
        var rel = Path.Combine(_tempDir, "bin", "Release", "net10.0");
        Directory.CreateDirectory(dbg);
        Directory.CreateDirectory(rel);
        var older = Path.Combine(dbg, "MyApp.dll");
        var newer = Path.Combine(rel, "MyApp.dll");
        File.WriteAllText(older, "x");
        File.WriteAllText(newer, "x");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var result = UiCommand.FindSchemaAssembly(Path.Combine(_tempDir, "bin"), "MyApp");

        Assert.That(result, Is.EqualTo(Path.GetFullPath(newer)));
    }

    [Test]
    public void FindSchemaAssembly_SkipsReferenceAssemblies()
    {
        // Only a ref assembly exists (bin/Debug/net10.0/ref/MyApp.dll) — metadata-only, must NOT be chosen.
        var refDir = Path.Combine(_tempDir, "bin", "Debug", "net10.0", "ref");
        Directory.CreateDirectory(refDir);
        File.WriteAllText(Path.Combine(refDir, "MyApp.dll"), "x");

        var result = UiCommand.FindSchemaAssembly(Path.Combine(_tempDir, "bin"), "MyApp");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void FindSchemaAssembly_NoMatchOrMissingInput_ReturnsNull()
    {
        Assert.That(UiCommand.FindSchemaAssembly(Path.Combine(_tempDir, "bin"), "MyApp"), Is.Null, "missing root → null");
        Assert.That(UiCommand.FindSchemaAssembly(_tempDir, null), Is.Null, "null name → null");
    }

    [Test]
    public void ValidateOpenTargets_DatabaseAndTrace_AreMutuallyExclusive()
    {
        // Every db-intent × trace-intent pairing must be rejected — a session opens one or the other, not both.
        foreach (var s in new[]
                 {
                     new UiCommand.Settings { OpenDb = true, OpenLatest = true },
                     new UiCommand.Settings { OpenDb = true, Trace = "x.typhon-trace" },
                     new UiCommand.Settings { Database = "x.typhon", OpenLatest = true },
                     new UiCommand.Settings { Database = "x.typhon", Trace = "x.typhon-trace" },
                 })
        {
            var error = UiCommand.ValidateOpenTargets(s);
            Assert.That(error, Does.Contain("either a database or a trace"), $"combo should be rejected: {error}");
        }
    }

    [Test]
    public void ValidateOpenTargets_SingleTargetOrNone_IsValid()
    {
        Assert.That(UiCommand.ValidateOpenTargets(new UiCommand.Settings { OpenDb = true }), Is.Null);
        Assert.That(UiCommand.ValidateOpenTargets(new UiCommand.Settings { Database = "x.typhon" }), Is.Null);
        Assert.That(UiCommand.ValidateOpenTargets(new UiCommand.Settings { OpenLatest = true }), Is.Null);
        Assert.That(UiCommand.ValidateOpenTargets(new UiCommand.Settings { Trace = "x.typhon-trace" }), Is.Null);
        Assert.That(UiCommand.ValidateOpenTargets(new UiCommand.Settings()), Is.Null, "welcome screen (nothing) is valid");
    }

    [Test]
    public void ValidateOpenTargets_SchemaWithoutDatabase_IsRejected()
    {
        Assert.That(
            UiCommand.ValidateOpenTargets(new UiCommand.Settings { Schema = "MyApp.dll" }),
            Does.Contain("--schema only applies"));
        Assert.That(
            UiCommand.ValidateOpenTargets(new UiCommand.Settings { Schema = "MyApp.dll", OpenLatest = true }),
            Does.Contain("--schema only applies"),
            "schema paired with a trace (no db) is still rejected");
        // With a database it is fine.
        Assert.That(UiCommand.ValidateOpenTargets(new UiCommand.Settings { Schema = "MyApp.dll", OpenDb = true }), Is.Null);
    }

    // ── --open-latest (#621 AC13) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--open-latest</c> must find captures where they actually live.
    /// </summary>
    /// <remarks>
    /// It searched <c>&lt;cwd&gt;/captures</c> until #621 — the location captures were written to before #616 moved them
    /// into the database bundle. That change landed on the writer; this is the reader, in a different assembly, with no
    /// test spanning the two, so the flag silently found nothing from the day captures moved. This fixture is that
    /// missing test.
    /// </remarks>
    [Test]
    public void FindLatestCapture_LooksInsideTheBundle_NotALegacyCapturesDirectory()
    {
        var bundle = Path.Combine(_tempDir, "world.typhon");
        var profilings = Path.Combine(bundle, "profilings");
        Directory.CreateDirectory(profilings);
        var capture = Path.Combine(profilings, "20260803-120000-000.typhon-trace");
        File.WriteAllBytes(capture, [0x54, 0x59, 0x54, 0x52]);

        // A file in the pre-#616 location must NOT win — finding it would mean the reader is still looking there.
        var legacyDir = Path.Combine(_tempDir, "captures");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllBytes(Path.Combine(legacyDir, "legacy.typhon-trace"), [0x54, 0x59, 0x54, 0x52]);

        Assert.That(UiCommand.FindLatestCaptureUnder(_tempDir), Is.EqualTo(capture));
    }

    [Test]
    public void FindLatestCapture_PicksTheNewestAcrossEveryBundle()
    {
        var older = MakeCapture("alpha.typhon", "20260801-100000-000.typhon-trace", DateTime.UtcNow.AddHours(-2));
        var newer = MakeCapture("beta.typhon", "20260803-100000-000.typhon-trace", DateTime.UtcNow);

        Assert.That(UiCommand.FindLatestCaptureUnder(_tempDir), Is.EqualTo(newer), $"expected the newer capture, not {older}");
    }

    [Test]
    public void FindLatestCapture_ReturnsNull_WhenNoBundleHasCaptures()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "empty.typhon"));
        Assert.That(UiCommand.FindLatestCaptureUnder(_tempDir), Is.Null, "null is what makes the CLI print a usable error rather than open something arbitrary");
    }

    [Test]
    public void FindLatestCapture_IgnoresSidecarCaches()
    {
        // Sidecars sit beside captures and share the stem; picking one would open a derived file as if it were a recording.
        var bundle = Path.Combine(_tempDir, "world.typhon");
        var profilings = Path.Combine(bundle, "profilings");
        Directory.CreateDirectory(profilings);
        var capture = Path.Combine(profilings, "20260803-090000-000.typhon-trace");
        File.WriteAllBytes(capture, [0x54, 0x59, 0x54, 0x52]);

        var sidecar = Path.Combine(profilings, "20260803-090000-000.typhon-trace-cache");
        File.WriteAllBytes(sidecar, [0x54, 0x50, 0x43, 0x48]);
        File.SetLastWriteTimeUtc(sidecar, DateTime.UtcNow.AddHours(1)); // newer, so only the IsCapture filter can exclude it

        Assert.That(UiCommand.FindLatestCaptureUnder(_tempDir), Is.EqualTo(capture));
    }

    private string MakeCapture(string bundleName, string captureName, DateTime writtenUtc)
    {
        var profilings = Path.Combine(_tempDir, bundleName, "profilings");
        Directory.CreateDirectory(profilings);
        var path = Path.Combine(profilings, captureName);
        File.WriteAllBytes(path, [0x54, 0x59, 0x54, 0x52]);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }
}
