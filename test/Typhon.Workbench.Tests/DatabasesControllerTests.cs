using System.Net;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Databases;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Feature #622 (F9) AC11 — the session-free registry API (design D-7).
/// </summary>
/// <remarks>
/// <see cref="WorkbenchFactory"/> roots the registry in the per-test temp directory, so these cases never read or delete the developer's real entries. The
/// suite-wide <c>DatabaseRegistryOptOut</c> is lifted for the duration of each case, because a registry that refuses to record cannot be seeded.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class DatabasesControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;
    private DatabaseRegistry _registry;
    private string _bundleRoot;
    private bool _priorSuppress;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [SetUp]
    public void SetUp()
    {
        _priorSuppress = DatabaseRegistry.SuppressForProcess;
        DatabaseRegistry.SuppressForProcess = false;

        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
        _registry = (DatabaseRegistry)_factory.Services.GetService(typeof(DatabaseRegistry));

        _bundleRoot = Path.Combine(_factory.DemoDirectory, "bundles");
        Directory.CreateDirectory(_bundleRoot);
    }

    [TearDown]
    public void TearDown()
    {
        DatabaseRegistry.SuppressForProcess = _priorSuppress;
        _factory.Dispose();
    }

    private string SeedBundle(string name)
    {
        var path = Path.Combine(_bundleRoot, name + ".typhon");
        Directory.CreateDirectory(path);
        _registry.Record(path, name, Guid.NewGuid());
        return path;
    }

    private async Task<KnownDatabaseListDto> GetListAsync()
    {
        var resp = await _client.GetAsync("/api/databases");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JsonSerializer.Deserialize<KnownDatabaseListDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    [Test]
    public async Task List_ReturnsKnownDatabases_NewestFirst()
    {
        SeedBundle("older");
        SeedBundle("newer");

        var list = await GetListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(list.Enabled, Is.True);
            Assert.That(list.DisabledReason, Is.Null);
            Assert.That(list.RegistryDirectory, Is.Not.Null.And.Not.Empty);
            Assert.That(list.Entries.Select(e => e.Name), Is.EqualTo(new[] { "newer", "older" }));
            Assert.That(list.Entries.All(e => e.Exists), Is.True);
        });
    }

    [Test]
    public async Task List_IsReachableWithoutASession()
    {
        // The whole point of this surface: it is how you FIND a database, so it must answer before any session exists.
        // Nothing in the request carries a session id, and the assertion is simply that it succeeded.
        SeedBundle("standalone");

        Assert.That((await GetListAsync()).Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task List_RequiresTheBootstrapToken()
    {
        using var unauthenticated = _factory.CreateClient();

        var resp = await unauthenticated.GetAsync("/api/databases");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task List_ReportsAVanishedBundleAsMissing()
    {
        var gone = SeedBundle("gone");
        Directory.Delete(gone, recursive: true);

        var entry = (await GetListAsync()).Entries.Single();

        Assert.That(entry.Exists, Is.False);
    }

    [Test]
    public async Task List_SaysWhenTheRegistryIsSwitchedOff_AndNamesTheSwitch()
    {
        // AC13: "off" and "nothing recorded yet" must not render identically, or the user concludes the feature is
        // useless instead of learning it is disabled — the exact failure D-7 calls out.
        SeedBundle("recorded-before-it-was-disabled");
        File.WriteAllText(Path.Combine(_registry.Directory, DatabaseRegistry.DisabledMarkerFileName), "");

        var list = await GetListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(list.Enabled, Is.False);
            Assert.That(list.DisabledReason, Does.Contain(DatabaseRegistry.DisabledMarkerFileName));
            // Rows written before it was switched off are still real databases — "stop recording" must not mean "lose what was recorded".
            Assert.That(list.Entries, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Forget_RemovesOneRow_AndReturnsTheRefreshedList()
    {
        SeedBundle("kept");
        var dropped = SeedBundle("dropped");

        var resp = await _client.DeleteAsync($"/api/databases?path={Uri.EscapeDataString(dropped)}");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = JsonSerializer.Deserialize<KnownDatabaseListDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.Multiple(() =>
        {
            Assert.That(list.Entries.Single().Name, Is.EqualTo("kept"));
            Assert.That(Directory.Exists(dropped), Is.True, "forgetting a database must never delete it");
        });
    }

    [Test]
    public async Task Forget_IsIdempotent()
    {
        var dropped = SeedBundle("dropped");
        var url = $"/api/databases?path={Uri.EscapeDataString(dropped)}";

        await _client.DeleteAsync(url);
        var second = await _client.DeleteAsync(url);

        // Forgetting something already forgotten achieved what the user asked for; answering 404 would error-toast a double-click for no reason.
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Forget_WithoutAPath_IsARequestError()
    {
        var resp = await _client.DeleteAsync("/api/databases?path=");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Prune_RemovesOnlyTheMissingOnes()
    {
        SeedBundle("alive");
        var gone = SeedBundle("gone");
        Directory.Delete(gone, recursive: true);

        var resp = await _client.PostAsync("/api/databases/prune", content: null);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = JsonSerializer.Deserialize<KnownDatabaseListDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(list.Entries.Single().Name, Is.EqualTo("alive"));
    }
}
