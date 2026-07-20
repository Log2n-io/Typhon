using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// #450 hardening — a legacy/foreign <b>file</b> sitting where the <c>{name}.typhon</c> bundle <b>directory</b> must go
/// (e.g. the old 0-byte Workbench marker, or a pre-bundle data file) must be:
/// <list type="bullet">
/// <item><description>rejected on open with a clear, typed <see cref="StorageException"/> — never an opaque
/// <c>Directory.CreateDirectory</c> <c>IOException</c>;</description></item>
/// <item><description>cleared by <see cref="PagedMMFOptions.EnsureFileDeleted"/> (which otherwise deletes directories
/// only), so a subsequent open succeeds against a fresh bundle.</description></item>
/// </list>
/// </summary>
[TestFixture]
class LegacyBundleArtifactTests
{
    private const string DbName = "legacy_artifact_db";
    private string _dir;

    private string BundlePath => Path.Combine(_dir, $"{DbName}.typhon");

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(LegacyBundleArtifactTests));
        Directory.CreateDirectory(_dir);
        Clean();
    }

    [TearDown]
    public void TearDown() => Clean();

    private void Clean()
    {
        try
        {
            if (Directory.Exists(BundlePath))
            {
                Directory.Delete(BundlePath, recursive: true);
            }

            if (File.Exists(BundlePath))
            {
                File.Delete(BundlePath);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = DbName;
                o.DatabaseDirectory = _dir;
            })
            .BuildServiceProvider();

    [Test]
    public void Open_FileAtBundlePath_ThrowsClearStorageException()
    {
        File.WriteAllText(BundlePath, "legacy marker");  // a FILE where the bundle DIRECTORY must be

        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<StorageException>(() => scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>());
        Assert.Multiple(() =>
        {
            Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.InvalidDatabaseBundle));
            Assert.That(ex.Message, Does.Contain(".typhon"), "message must name the bundle and be actionable");
        });
    }

    [Test]
    public void EnsureFileDeleted_RemovesLegacyFileAtBundlePath()
    {
        File.WriteAllText(BundlePath, "legacy marker");
        Assert.That(File.Exists(BundlePath), Is.True, "precondition: legacy file planted");

        new PagedMMFOptions { DatabaseName = DbName, DatabaseDirectory = _dir }.EnsureFileDeleted();

        Assert.That(File.Exists(BundlePath), Is.False, "EnsureFileDeleted must clear a legacy file at the bundle path");
    }

    [Test]
    public void Open_AfterClearingLegacyFile_CreatesFreshBundleDirectory()
    {
        File.WriteAllText(BundlePath, "legacy marker");
        new PagedMMFOptions { DatabaseName = DbName, DatabaseDirectory = _dir }.EnsureFileDeleted();

        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        using (var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>())
        {
            Assert.That(mmf, Is.Not.Null);
        }

        Assert.That(Directory.Exists(BundlePath), Is.True, "a fresh bundle directory must be created once the legacy file is gone");
    }
}
