using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Dtos.Realms;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// #1083 rung 1 — the realm catalog over a database the Workbench merely opened, and the capability that decides whether any of it is offered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves of the capability are asserted, and the absent half is the one that matters.</b> A single-realm database must grow no realm UI at all: a
/// navigator node, a context-bar chip and a panel over one world are noise, and the engine makes the same call by leaving
/// <c>RuntimeStatsSnapshot.Realms</c> empty there. A test that only checked the present case would pass against a Workbench that offered realms to everyone.
/// </para>
/// <para>
/// <b>The databases are built here rather than taken from the demo fixture</b>, because the fact under test is a property of the file: a database is
/// realm-capable when its persisted catalog names a realm, and the only way to have one is to have written one.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable] // opens engines, so it shares the process-global ArchetypeRegistry hazard that EngineLifecycleTests documents (#554)
public sealed class RealmsControllerTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private WorkbenchFactory _factory;
    private HttpClient _client;
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-realm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort: a file still mapped by a finalizer-pending handle goes with the temp directory later
        }
    }

    /// <summary>The interior realm this fixture writes, and the bounds it writes it with — read back verbatim, so the numbers are the assertion.</summary>
    private const ushort Interior = 3;

    private const double InteriorExtent = 64d;
    private const double InteriorCell = 64d;

    /// <summary>
    /// Writes a database whose catalog names one realm besides realm 0, then closes it.
    /// </summary>
    /// <remarks>
    /// <b>Closed before the Workbench opens it</b>, because the Workbench takes the database lock: two live engines over one directory is the case
    /// <c>EngineLifecycle</c> exists to serialise, not something a test should race on.
    /// </remarks>
    private string WriteMultiRealmDatabase() => WriteDatabase(
        "realms.typhon",
        dbe =>
        {
            dbe.ConfigureRealms(Interior + 1);
            dbe.Realms.Register(
                new RealmId(Interior),
                RealmConfig.SimulatedAlways(
                    SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2((float)InteriorExtent, (float)InteriorExtent), InteriorCell)));
        });

    private string WriteSingleRealmDatabase() => WriteDatabase("oneworld.typhon", null);

    /// <summary>
    /// Builds a database at <paramref name="name"/>, lets <paramref name="declare"/> name realms, and closes it.
    /// </summary>
    /// <remarks>
    /// The same service graph <c>EngineLifecycle</c> builds, minus the Workbench's own concerns: a realm catalog is written by registering a realm before
    /// <c>InitializeArchetypes</c>, and there is no shorter way to have a database that holds one.
    /// </remarks>
    private string WriteDatabase(string name, Action<DatabaseEngine> declare)
    {
        var path = Path.Combine(_tempDir, name);
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddManagedPagedMMF(opts =>
            {
                opts.DatabaseName = Path.GetFileNameWithoutExtension(name);
                opts.DatabaseDirectory = _tempDir;
            })
            .AddDatabaseEngine();

        using var sp = services.BuildServiceProvider();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1024, 1024), 64));
        declare?.Invoke(dbe);
        dbe.InitializeArchetypes();
        return path;
    }

    private async Task<SessionDto> OpenAsync(string path)
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest(path));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json);
    }

    private async Task<HttpResponseMessage> GetRealmsAsync(SessionDto session)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/realms");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        return await _client.SendAsync(req);
    }

    [Test]
    public async Task ARealmCapableDatabaseGrantsTheRealmsCapability()
    {
        var session = await OpenAsync(WriteMultiRealmDatabase());
        Assert.That(session.Capabilities, Contains.Item(SessionCapability.Realms), "a database whose catalog names a realm is realm-capable");
    }

    /// <summary>The half that stops the realm UI appearing for everybody.</summary>
    [Test]
    public async Task ASingleRealmDatabaseDoesNotGrantTheRealmsCapability()
    {
        var session = await OpenAsync(WriteSingleRealmDatabase());

        Assert.Multiple(() =>
        {
            Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Realms), "one world is not a realm world");
            Assert.That(session.Capabilities, Contains.Item(SessionCapability.Database), "precondition: the database itself opened fine");
        });
    }

    [Test]
    public async Task ASingleRealmDatabaseRefusesTheRealmsRouteRatherThanAnsweringOneRow()
    {
        var session = await OpenAsync(WriteSingleRealmDatabase());

        var response = await GetRealmsAsync(session);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
            "a one-world database has no realms to list, and an array of one would invite a caller to build a realm UI over it");
    }

    [Test]
    public async Task TheRealmListCarriesTheCatalogsIdentityForEveryRealmIncludingRealmZero()
    {
        var session = await OpenAsync(WriteMultiRealmDatabase());

        var response = await GetRealmsAsync(session);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = JsonSerializer.Deserialize<RealmListDto>(await response.Content.ReadAsStringAsync(), Json);

        Assert.That(list, Is.Not.Null);
        var realm0 = Array.Find(list.Realms, r => r.Id == 0);
        var interior = Array.Find(list.Realms, r => r.Id == Interior);

        Assert.Multiple(() =>
        {
            // Realm 0 is the row a list built from catalog rows alone would miss: it keeps the single-world grid record and is deliberately not catalogued.
            Assert.That(realm0, Is.Not.Null, "realm 0 must be listed even though the catalog does not name it");
            Assert.That(interior, Is.Not.Null, $"the catalogued interior {Interior} must be listed");

            Assert.That(list.MaxRealms, Is.EqualTo(Interior + 1),
                "the id space is widened to the realms that exist: the configured capacity reads 1 here, because a schema-less open never runs the raise");
            Assert.That(list.Catalogued, Is.EqualTo(1), "one realm besides realm 0 was written");

            Assert.That(interior.Grid.MaxX, Is.EqualTo(InteriorExtent), "the interior's bounds come back as they were written");
            Assert.That(interior.Grid.CellSize, Is.EqualTo(InteriorCell));
            Assert.That(interior.Grid.Deep, Is.False, "a flat realm has no Z extent, which is how dimensionality is derived rather than stored");
            Assert.That(realm0.Grid.MaxX, Is.EqualTo(1024d), "realm 0's bounds come from its own grid record, not from a catalog row");
        });
    }

    /// <summary>
    /// The policy half must come back unknown, not defaulted.
    /// </summary>
    /// <remarks>
    /// This is the assertion that stops a plausible lie. A realm the application did not register is given
    /// <c>RealmConfig.SimulatedAlways</c> by the engine's open-time merge, so a divisor and a run state are sitting there to be read — invented so that there is
    /// something, not decided by anyone. Reporting them would render "Simulated, divisor 1" for a realm the application sleeps.
    /// </remarks>
    [Test]
    public async Task ThePolicyHalfIsReportedAsUnknownRatherThanAsTheEnginesInventedDefault()
    {
        var session = await OpenAsync(WriteMultiRealmDatabase());

        var response = await GetRealmsAsync(session);
        var list = JsonSerializer.Deserialize<RealmListDto>(await response.Content.ReadAsStringAsync(), Json);
        var interior = Array.Find(list.Realms, r => r.Id == Interior);

        Assert.Multiple(() =>
        {
            Assert.That(list.LiveState, Is.False, "nothing is ticking, and the realms here were recovered rather than registered");
            Assert.That(list.LiveStateReason, Is.Not.Empty, "a panel showing nothing needs the reason to show instead");
            Assert.That(interior.Divisor, Is.Null, "the divisor on a recovered realm is an engine default, not the application's policy");
            Assert.That(interior.RunState, Is.Empty, "a run state derived from an invented policy is not a fact about this realm");
            Assert.That(interior.Kind, Is.Empty, "the replication kind is not persisted at all");
            Assert.That(interior.Sessions, Is.Null, "an opened file serves no sessions");

            // The identity half, by contrast, IS known — so the nulls above are a statement about policy and not a broken endpoint.
            Assert.That(interior.Generation, Is.Zero, "first incarnation of the id");
            Assert.That(interior.Lifecycle, Is.EqualTo("live"));
            // Deliberately NOT asserted true: a Workbench open of a database with no schema assemblies never reaches InitializeArchetypes, so the realm table
            // is absent and this realm is known from its catalog row alone. "Catalogued but not registered" is a real state of a real session, which is why
            // RealmDto carries the two as separate questions.
            Assert.That(interior.Source, Is.EqualTo("catalog").Or.EqualTo("registered"));
        });
    }

    [Test]
    public async Task TheRouteIsGatedByTheBootstrapToken()
    {
        using var unauthenticated = _factory.CreateClient();
        var response = await unauthenticated.GetAsync($"/api/sessions/{Guid.NewGuid()}/realms");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
