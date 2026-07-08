using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;
using Typhon.Workbench.Controllers;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Covers the sample-database surface of <see cref="FixturesController"/> that ships in Release (#433): the
/// capability advertisement, its bootstrap-token gate, and the async create → poll → done flow producing a real
/// <c>.typhon</c>. The DEBUG-only <c>trace</c>/<c>mock-profiler</c> endpoints are exercised by the Playwright E2E
/// canaries, not here; generation correctness across every feature path lives in <see cref="FixtureConfigTests"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class FixturesControllerTests
{
    // Web defaults (camelCase, case-insensitive) + string enums — matches the Workbench server's JSON options so
    // the posted CreateFixtureRequestDto (incl. the FixtureConfig enum) round-trips exactly.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task Capability_IsAvailable()
    {
        var resp = await _client.GetAsync("/api/fixtures/capability");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var dto = await resp.Content.ReadFromJsonAsync<FixtureCapabilityDto>(JsonOpts);
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto.Available, Is.True);
        Assert.That(dto.OutputDirectory, Is.Not.Empty);
        Assert.That(dto.DefaultDatabaseName, Is.Not.Empty);
    }

    [Test]
    public async Task Capability_RequiresBootstrapToken()
    {
        // The shipped sample-DB surface must stay gated — no new unauthenticated surface (#433).
        using var unauth = _factory.CreateClient();
        var resp = await unauth.GetAsync("/api/fixtures/capability");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Create_GeneratesSampleDatabase()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "typhon-fixture-test", Guid.NewGuid().ToString("N"));
        try
        {
            // Minimal config — a handful of entities so the test stays well under a second (generation correctness
            // across the full feature matrix is FixtureConfigTests' job).
            var config = FixtureConfig.Default with
            {
                ResourceTypeCount = 4,
                GuildCount = 1,
                RecipeCount = 2,
                PlayerCount = 2,
                DepositCount = 1,
                HarvesterCount = 1,
                FactoryCount = 1,
                ItemCount = 2,
                ResourceTaxonomyDepth = 1,
                MaxAffixesPerItem = 1,
            };
            var req = new CreateFixtureRequestDto(Force: true, OutputDirectory: outDir, Config: config, DatabaseName: "sample-test");

            var startResp = await _client.PostAsJsonAsync("/api/fixtures/create", req, JsonOpts);
            Assert.That(startResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var start = await startResp.Content.ReadFromJsonAsync<StartFixtureJobResponseDto>(JsonOpts);
            Assert.That(start?.JobId, Is.Not.Null.And.Not.Empty);

            FixtureJobStateDto state = null;
            for (var i = 0; i < 100; i++)
            {
                var jobResp = await _client.GetAsync($"/api/fixtures/jobs/{start!.JobId}");
                Assert.That(jobResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                state = await jobResp.Content.ReadFromJsonAsync<FixtureJobStateDto>(JsonOpts);
                if (state.State is "done" or "error" or "cancelled")
                {
                    break;
                }
                await Task.Delay(50);
            }

            Assert.That(state, Is.Not.Null);
            Assert.That(state.State, Is.EqualTo("done"), $"job did not complete: state={state.State} error={state.Error}");
            Assert.That(state.Result, Is.Not.Null);
            // A `.typhon` database is a bundle directory, not a plain file — Path.Exists matches either.
            Assert.That(Path.Exists(state.Result.TyphonFilePath), Is.True,
                $"expected a generated .typhon at {state.Result?.TyphonFilePath}");
            Assert.That(state.Result!.TotalEntities, Is.GreaterThan(0));
        }
        finally
        {
            try
            {
                if (Directory.Exists(outDir))
                {
                    Directory.Delete(outDir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
