using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Hosting;

/// <summary>
/// Feature #622 (F9) AC1–AC10 — the machine-local database registry (design D-7).
/// </summary>
/// <remarks>
/// <para>
/// The fixture drives <see cref="DatabaseRegistry"/> against a temp-rooted directory and, for the two cases that only mean something end to end, against a real
/// <see cref="DatabaseEngine.Open(string,Action{TyphonOptions},Microsoft.Extensions.Logging.ILoggerFactory)"/> <b>outside</b> the OS temp directory — because
/// the temp guard would otherwise suppress exactly the behaviour under test, and a fixture that quietly tested nothing is the failure mode this feature is most
/// exposed to.
/// </para>
/// <para>
/// <see cref="DatabaseRegistry.SuppressForProcess"/> is set for the whole suite by <c>AssemblyWarmup</c>; every case here restores whatever it found, so the
/// suite-wide opt-out survives this fixture regardless of which test fails.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class DatabaseRegistryTests
{
    private string _root;
    private string _outsideTemp;
    private bool _priorSuppress;
    private string _priorOverride;
    private string _priorEnv;

    [SetUp]
    public void SetUp()
    {
        _priorSuppress = DatabaseRegistry.SuppressForProcess;
        _priorOverride = DatabaseRegistry.DirectoryOverride;
        _priorEnv = Environment.GetEnvironmentVariable(DatabaseRegistry.DisableEnvironmentVariable);

        _root = Path.Combine(Path.GetTempPath(), "typhon-registry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // A scratch area that is NOT under %TEMP%, for the cases that must survive the temp guard. The test binaries' own directory is the one location a test
        // can rely on being both writable and outside the temp tree on every machine and CI runner.
        _outsideTemp = Path.Combine(TestContext.CurrentContext.TestDirectory, "registry-scratch", Guid.NewGuid().ToString("N"));

        DatabaseRegistry.SuppressForProcess = false;
        DatabaseRegistry.DirectoryOverride = _root;
        Environment.SetEnvironmentVariable(DatabaseRegistry.DisableEnvironmentVariable, null);
    }

    [TearDown]
    public void TearDown()
    {
        DatabaseRegistry.SuppressForProcess = _priorSuppress;
        DatabaseRegistry.DirectoryOverride = _priorOverride;
        Environment.SetEnvironmentVariable(DatabaseRegistry.DisableEnvironmentVariable, _priorEnv);

        TryDeleteTree(_root);
        TryDeleteTree(_outsideTemp);
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>A bundle-shaped directory that actually exists, so the <c>Exists</c> verdict is true for it.</summary>
    private string MakeBundle(string name)
    {
        var path = Path.Combine(_outsideTemp, name + ".typhon");
        Directory.CreateDirectory(path);
        return path;
    }

    // ── AC2 / AC4 · the key ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void FileName_IsIdenticalForRelativeAndAbsoluteSpellingsOfOnePath()
    {
        // PagedMMFOptions.BundleDirectory composes the RAW DatabaseDirectory, which is very often relative. Keying on it unnormalised would file one database
        // under two names the moment it is opened from a different working directory, and the user would simply see it twice.
        var absolute = DatabaseRegistry.NormalizePath(Path.Combine(_outsideTemp, "world.typhon"));
        var viaDots = DatabaseRegistry.NormalizePath(Path.Combine(_outsideTemp, "sub", "..", "world.typhon"));
        var trailing = DatabaseRegistry.NormalizePath(Path.Combine(_outsideTemp, "world.typhon") + Path.DirectorySeparatorChar);

        Assert.That(DatabaseRegistry.FileNameFor(viaDots), Is.EqualTo(DatabaseRegistry.FileNameFor(absolute)));
        Assert.That(DatabaseRegistry.FileNameFor(trailing), Is.EqualTo(DatabaseRegistry.FileNameFor(absolute)));
    }

    [Test]
    [Platform("Win")]
    public void FileName_FoldsCaseOnWindows()
    {
        // On Windows these name the same directory, so they must be the same row. On Linux they would be two different databases, which is why the folding is
        // platform-conditional rather than unconditional.
        var lower = DatabaseRegistry.NormalizePath(@"C:\games\world.typhon");
        var upper = DatabaseRegistry.NormalizePath(@"C:\Games\World.typhon");

        Assert.That(DatabaseRegistry.FileNameFor(upper), Is.EqualTo(DatabaseRegistry.FileNameFor(lower)));
    }

    [Test]
    public void Record_TwoSpellingsOfOnePath_ProduceOneEntry()
    {
        var registry = new DatabaseRegistry(_root);
        var bundle = MakeBundle("world");

        Assert.That(registry.Record(bundle, "world", Guid.NewGuid()), Is.True);
        Assert.That(registry.Record(Path.Combine(_outsideTemp, "sub", "..", "world.typhon"), "world", Guid.NewGuid()), Is.True);

        Assert.That(registry.List(), Has.Count.EqualTo(1));
    }

    // ── AC2 · what a row holds ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Record_StoresTheIdentityItWasGiven()
    {
        var registry = new DatabaseRegistry(_root);
        var bundle = MakeBundle("world");
        var id = Guid.NewGuid();

        registry.Record(bundle, "world", id);

        var entry = registry.List().Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.BundlePath, Is.EqualTo(DatabaseRegistry.NormalizePath(bundle)));
            Assert.That(entry.Name, Is.EqualTo("world"));
            Assert.That(entry.DatabaseId, Is.EqualTo(id));
            Assert.That(entry.LastOpenedBy, Is.Not.Null.And.Not.Empty);
            Assert.That(entry.Exists, Is.True);
        });
    }

    [Test]
    public void Record_Again_KeepsFirstSeen_AndAdvancesLastOpened()
    {
        var registry = new DatabaseRegistry(_root);
        var bundle = MakeBundle("world");
        var id = Guid.NewGuid();

        registry.Record(bundle, "world", id);
        var first = registry.List().Single();

        // Re-registration must not rewrite history: "known since" is the only field that says anything about how long this database has been around, and it is
        // unrecoverable once overwritten.
        registry.Record(bundle, "world", id);
        var second = registry.List().Single();

        Assert.Multiple(() =>
        {
            Assert.That(second.FirstSeenUtc, Is.EqualTo(first.FirstSeenUtc));
            Assert.That(second.LastOpenedUtc, Is.GreaterThanOrEqualTo(first.LastOpenedUtc));
        });
    }

    // ── AC5 · the temp guard ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void IsUnderTempDirectory_MatchesOnDirectoryBoundaries()
    {
        var temp = DatabaseRegistry.NormalizePath(Path.GetTempPath());

        Assert.Multiple(() =>
        {
            Assert.That(DatabaseRegistry.IsUnderTempDirectory(Path.Combine(temp, "x.typhon")), Is.True);
            Assert.That(DatabaseRegistry.IsUnderTempDirectory(Path.Combine(temp, "a", "b", "x.typhon")), Is.True);
            Assert.That(DatabaseRegistry.IsUnderTempDirectory(temp), Is.True);
            // The naive StartsWith would suppress this one: a sibling directory whose name merely begins with the temp directory's spelling.
            Assert.That(DatabaseRegistry.IsUnderTempDirectory(temp + "-elsewhere"), Is.False);
        });
    }

    [Test]
    public void TryRecordOpen_UnderTemp_RecordsNothing()
    {
        var inTemp = Path.Combine(Path.GetTempPath(), "typhon-registry-tests", "suppressed.typhon");

        DatabaseRegistry.TryRecordOpen(null, inTemp, "suppressed", Guid.NewGuid());

        Assert.That(new DatabaseRegistry(_root).List(), Is.Empty);
    }

    // ── AC6 / AC7 / AC13 · the three switches, each naming itself ─────────────────────────────────────────────

    [Test]
    public void SuppressForProcess_DisablesAndSaysSo()
    {
        DatabaseRegistry.SuppressForProcess = true;
        var registry = new DatabaseRegistry(_root);

        Assert.That(registry.IsEnabled(out var reason), Is.False);
        Assert.That(reason, Does.Contain("SuppressForProcess"));
        Assert.That(registry.Record(MakeBundle("world"), "world", Guid.NewGuid()), Is.False);
    }

    [TestCase("off")]
    [TestCase("0")]
    [TestCase("false")]
    [TestCase("NO")]
    public void EnvironmentVariable_DisablesAndSaysSo(string value)
    {
        Environment.SetEnvironmentVariable(DatabaseRegistry.DisableEnvironmentVariable, value);
        var registry = new DatabaseRegistry(_root);

        Assert.That(registry.IsEnabled(out var reason), Is.False);
        Assert.That(reason, Does.Contain(DatabaseRegistry.DisableEnvironmentVariable));
        Assert.That(registry.Record(MakeBundle("world"), "world", Guid.NewGuid()), Is.False);
    }

    [Test]
    public void EnvironmentVariable_UnrecognisedValue_LeavesItEnabled()
    {
        // A guard that treated anything non-empty as "off" would silently disable the feature for a machine that set the variable to "on".
        Environment.SetEnvironmentVariable(DatabaseRegistry.DisableEnvironmentVariable, "on");

        Assert.That(new DatabaseRegistry(_root).IsEnabled(out _), Is.True);
    }

    [Test]
    public void DisabledMarkerFile_DisablesAndSaysSo()
    {
        File.WriteAllText(Path.Combine(_root, DatabaseRegistry.DisabledMarkerFileName), "");
        var registry = new DatabaseRegistry(_root);

        Assert.That(registry.IsEnabled(out var reason), Is.False);
        Assert.That(reason, Does.Contain(DatabaseRegistry.DisabledMarkerFileName));
        Assert.That(registry.Record(MakeBundle("world"), "world", Guid.NewGuid()), Is.False);
    }

    [Test]
    public void Readme_IsWrittenBesideTheEntries_AndNamesEverySwitch()
    {
        // D-7's "findable without reading source" is only true if the instructions live where someone uneasy about the index will actually look: with it.
        new DatabaseRegistry(_root).Record(MakeBundle("world"), "world", Guid.NewGuid());

        var readme = File.ReadAllText(Path.Combine(_root, DatabaseRegistry.ReadmeFileName));
        Assert.Multiple(() =>
        {
            Assert.That(readme, Does.Contain(DatabaseRegistry.DisabledMarkerFileName));
            Assert.That(readme, Does.Contain(DatabaseRegistry.DisableEnvironmentVariable));
            Assert.That(readme, Does.Contain("SuppressForProcess"));
        });
    }

    // ── AC8 · reading, and what a bad file costs ──────────────────────────────────────────────────────────────

    [Test]
    public void List_SkipsACorruptEntry_AndStillReturnsItsNeighbours()
    {
        // Containing corruption to one row is the stated reason D-7 chose a directory of files over one shared document, so it is asserted rather than assumed.
        var registry = new DatabaseRegistry(_root);
        registry.Record(MakeBundle("alpha"), "alpha", Guid.NewGuid());
        registry.Record(MakeBundle("beta"), "beta", Guid.NewGuid());
        File.WriteAllText(Path.Combine(_root, "deadbeefdeadbeefdeadbeefdeadbeef.json"), "{ not json");

        var entries = registry.List();

        Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "alpha", "beta" }));
    }

    [Test]
    public void List_IgnoresTheReadmeAndAnyStagingFile()
    {
        var registry = new DatabaseRegistry(_root);
        registry.Record(MakeBundle("alpha"), "alpha", Guid.NewGuid());
        File.WriteAllText(Path.Combine(_root, "abcdef0123456789abcdef0123456789.json.4242.tmp"), "{}");

        Assert.That(registry.List(), Has.Count.EqualTo(1));
    }

    [Test]
    public void List_OrdersMostRecentlyOpenedFirst()
    {
        var registry = new DatabaseRegistry(_root);
        registry.Record(MakeBundle("older"), "older", Guid.NewGuid());
        registry.Record(MakeBundle("newer"), "newer", Guid.NewGuid());
        // Re-record the first so it becomes the most recent — proves the order comes from lastOpenedUtc, not from directory enumeration order.
        registry.Record(Path.Combine(_outsideTemp, "older.typhon"), "older", Guid.NewGuid());

        Assert.That(registry.List().First().Name, Is.EqualTo("older"));
    }

    // ── AC9 / AC10 · staleness, forget, prune ─────────────────────────────────────────────────────────────────

    [Test]
    public void List_ReportsAVanishedBundleAsMissing_WithoutRemovingIt()
    {
        var registry = new DatabaseRegistry(_root);
        var bundle = MakeBundle("gone");
        registry.Record(bundle, "gone", Guid.NewGuid());
        Directory.Delete(bundle, recursive: true);

        var entry = registry.List().Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.Exists, Is.False);
            // Validate on listing, OFFER to prune — a list that silently deleted rows would make "forget this" impossible to undo by reopening the database.
            Assert.That(Directory.GetFiles(_root, "*.json"), Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void Forget_RemovesOneEntry_AndLeavesTheDatabaseAlone()
    {
        var registry = new DatabaseRegistry(_root);
        var kept = MakeBundle("kept");
        var dropped = MakeBundle("dropped");
        registry.Record(kept, "kept", Guid.NewGuid());
        registry.Record(dropped, "dropped", Guid.NewGuid());

        Assert.That(registry.Forget(dropped), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(registry.List().Single().Name, Is.EqualTo("kept"));
            Assert.That(Directory.Exists(dropped), Is.True, "forgetting a database must never delete it");
            Assert.That(registry.Forget(dropped), Is.False, "forgetting twice is not an error, but it is not a second removal either");
        });
    }

    [Test]
    public void PruneMissing_RemovesOnlyTheVanishedOnes()
    {
        var registry = new DatabaseRegistry(_root);
        var alive = MakeBundle("alive");
        var gone = MakeBundle("gone");
        registry.Record(alive, "alive", Guid.NewGuid());
        registry.Record(gone, "gone", Guid.NewGuid());
        Directory.Delete(gone, recursive: true);

        Assert.That(registry.PruneMissing(), Is.EqualTo(1));
        Assert.That(registry.List().Single().Name, Is.EqualTo("alive"));
    }

    // ── Review findings ───────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AMalformedPath_IsRefused_NotThrown()
    {
        // These arguments reach the registry from an HTTP query string, where a caller can put anything. Modern .NET no
        // longer validates characters like < > | in Path.GetFullPath — an embedded null is one of the few inputs it
        // still rejects — so this is a narrow case, but an unguarded one answers a malformed path with a 500 instead of
        // the "that names nothing, so it forgets nothing" it actually is.
        var registry = new DatabaseRegistry(_root);
        const string malformed = "C:\\bad\0path.typhon";

        Assert.Multiple(() =>
        {
            Assert.That(registry.Forget(malformed), Is.False);
            Assert.That(registry.Record(malformed, "bad", Guid.NewGuid()), Is.False);
        });
    }

    [Test]
    public void AnUnwritableReadme_DoesNotCostTheRegistration()
    {
        // Two engines starting at once both try to write the help file; without the guard the loser's Record() would
        // fail on the sharing violation and a real database would go unrecorded for the sake of a README.
        Directory.CreateDirectory(_root);
        var readme = Path.Combine(_root, DatabaseRegistry.ReadmeFileName);
        using (File.Open(readme, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.That(new DatabaseRegistry(_root).Record(MakeBundle("world"), "world", Guid.NewGuid()), Is.True);
        }

        Assert.That(new DatabaseRegistry(_root).List(), Has.Count.EqualTo(1));
    }

    // ── AC3 · the engine hook, end to end ─────────────────────────────────────────────────────────────────────

    [Test]
    public void OpeningADatabaseOutsideTemp_RegistersIt()
    {
        // The one test that proves the whole chain: a real open, through the real constructor hook, into a real registry directory. Everything above tests the
        // registry; only this tests that anything ever calls it.
        Directory.CreateDirectory(_outsideTemp);
        var dbPath = Path.Combine(_outsideTemp, "regtest.typhon");

        Guid id;
        using (var dbe = DatabaseEngine.Open(dbPath))
        {
            id = dbe.DatabaseId;
        }

        var entry = new DatabaseRegistry(_root).List().Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Name, Is.EqualTo("regtest"));
            Assert.That(entry.DatabaseId, Is.EqualTo(id));
            Assert.That(entry.BundlePath, Is.EqualTo(DatabaseRegistry.NormalizePath(dbPath)));
        });
    }

    [Test]
    public void AnUnwritableRegistryRoot_StillOpensTheDatabase_AndSaysWhyItCouldNotRecordIt()
    {
        // A machine-local convenience index must never be able to fail an open — a service account with a redirected or absent %LOCALAPPDATA% is a normal
        // deployment, not an error. A plain file where the directory should be is the portable way to make the root unusable.
        //
        // Absorbed is not the same as invisible, though. A user whose databases never appear in the Workbench has no way to find out why from a silent
        // swallow, so the failure is logged once, names the directory, and says the open itself was fine.
        var blocked = Path.Combine(_outsideTemp, "blocked-registry");
        Directory.CreateDirectory(_outsideTemp);
        File.WriteAllText(blocked, "not a directory");
        DatabaseRegistry.DirectoryOverride = blocked;

        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(provider));

        var dbPath = Path.Combine(_outsideTemp, "unwritable.typhon");
        using (var dbe = DatabaseEngine.Open(dbPath, loggerFactory: loggerFactory))
        {
            Assert.That(dbe.DatabaseId, Is.Not.EqualTo(Guid.Empty));
        }

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(blocked), Is.EqualTo("not a directory"), "the blocking file must be left alone");
            Assert.That(provider.Warnings, Has.Exactly(1).Contains("Database registry"));
            Assert.That(provider.Warnings.Find(w => w.Contains("Database registry")), Does.Contain(blocked));
        });
    }

    [Test]
    public void AGuardDecliningIsNotAFailure_AndSaysNothing()
    {
        // Temp suppression, an opted-out process and the kill-switch are the feature working as configured. Logging them would put a warning in every test
        // run and in every deployment that deliberately turned the registry off — which is how a warning stops being read.
        DatabaseRegistry.DirectoryOverride = _root;
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(provider));

        var underTemp = Path.Combine(Path.GetTempPath(), "typhon-registry-tests", Guid.NewGuid().ToString("N"), "quiet.typhon");
        Directory.CreateDirectory(Path.GetDirectoryName(underTemp)!);
        using (DatabaseEngine.Open(underTemp, loggerFactory: loggerFactory)) { }

        Assert.That(provider.Warnings.FindAll(w => w.Contains("Database registry")), Is.Empty);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly List<string> Warnings = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Warnings);

        public void Dispose() { }

        private sealed class CapturingLogger(List<string> warnings) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    lock (warnings)
                    {
                        warnings.Add(formatter(state, exception));
                    }
                }
            }
        }
    }
}
