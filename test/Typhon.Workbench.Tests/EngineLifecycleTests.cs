using NUnit.Framework;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

[TestFixture]
[NonParallelizable] // opens engines via EngineLifecycle.OpenAsync — the schema-compat State check reads the process-global ArchetypeRegistry, which must not race with other engine tests (see #554)
public sealed class EngineLifecycleTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task Open_CreatesEngineAndExposesRegistry()
    {
        var path = Path.Combine(_tempDir, "demo.typhon");
        using var lifecycle = await EngineLifecycle.OpenAsync(path);

        Assert.That(lifecycle.Engine, Is.Not.Null);
        Assert.That(lifecycle.Registry, Is.Not.Null);
        Assert.That(lifecycle.Registry.Root, Is.Not.Null);
        Assert.That(lifecycle.Registry.Root.Children, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task DisposeAndReopen_SamePath_Succeeds()
    {
        var path = Path.Combine(_tempDir, "demo.typhon");

        var first = await EngineLifecycle.OpenAsync(path);
        first.Dispose();

        Assert.DoesNotThrowAsync(async () =>
        {
            using var second = await EngineLifecycle.OpenAsync(path);
            Assert.That(second.Registry.Root, Is.Not.Null);
        }, "File handle should be released so the same path can be reopened in-process");
    }

    [Test]
    public async Task Dispose_IsIdempotent()
    {
        var path = Path.Combine(_tempDir, "demo.typhon");
        var lifecycle = await EngineLifecycle.OpenAsync(path);

        lifecycle.Dispose();
        Assert.DoesNotThrow(() => lifecycle.Dispose());
    }
}
