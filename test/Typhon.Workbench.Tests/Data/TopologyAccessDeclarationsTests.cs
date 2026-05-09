using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests.Data;

/// <summary>
/// Integration tests for RFC 07 surfacing through the Data API (#310): the topology endpoint must
/// return access declarations + phase order, and the new <c>/queries/who-writes/{component}</c> /
/// <c>/queries/who-reads/{component}</c> endpoints must filter the system list correctly.
/// </summary>
[TestFixture]
public sealed class TopologyAccessDeclarationsTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task<SessionDto> CreateRfc07TraceAsync()
    {
        var path = TraceFixtureBuilder.BuildTraceWithAccessDeclarations(_factory.DemoDirectory);
        var resp = await _client.PostAsJsonAsync("/api/sessions/trace", new CreateTraceSessionRequest(path));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    private async Task<TopologyDto> WaitForTopologyAsync(Guid sessionId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/topology");
            req.Headers.Add("X-Session-Token", sessionId.ToString());
            req.Headers.Add("X-Workbench-Api", "1");
            var resp = await _client.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                return JsonSerializer.Deserialize<TopologyDto>(await resp.Content.ReadAsStringAsync(), Json)!;
            }
            if (resp.StatusCode != HttpStatusCode.Accepted)
            {
                Assert.Fail($"Unexpected status: {resp.StatusCode}");
            }
            await Task.Delay(20);
        }
        Assert.Fail("topology did not become available in time");
        return null!;
    }

    [Test]
    public async Task Topology_PopulatesRfc07Fields()
    {
        var session = await CreateRfc07TraceAsync();
        var topo = await WaitForTopologyAsync(session.SessionId);

        Assert.That(topo.Phases, Is.EqualTo(new[] { "Input", "Simulation", "Output" }));
        Assert.That(topo.Systems.Length, Is.EqualTo(2));

        var movement = FindByName(topo.Systems, "Movement");
        Assert.That(movement.PhaseName, Is.EqualTo("Simulation"));
        Assert.That(movement.IsExclusivePhase, Is.False);
        Assert.That(movement.Reads, Is.EqualTo(new[] { "Game.Velocity" }));
        Assert.That(movement.Writes, Is.EqualTo(new[] { "Game.Position" }));
        Assert.That(movement.ReadsEvents, Is.EqualTo(new[] { "Damage" }));
        Assert.That(movement.WritesResources, Is.EqualTo(new[] { "world.physics" }));

        var damage = FindByName(topo.Systems, "Damage");
        Assert.That(damage.IsExclusivePhase, Is.True);
        Assert.That(damage.ReadsSnapshot, Is.EqualTo(new[] { "Game.Position" }));
        Assert.That(damage.Writes, Is.EqualTo(new[] { "Game.Health" }));
        Assert.That(damage.WritesEvents, Is.EqualTo(new[] { "Death" }));
    }

    [Test]
    public async Task Topology_PopulatesArchetypeLabelAndComponentTypeNames()
    {
        var session = await CreateRfc07TraceAsync();
        var topo = await WaitForTopologyAsync(session.SessionId);

        // The RFC 07 fixture writes an empty archetype table — what matters here is the projection shape: every record
        // exposes Label, SchemaRevision and ComponentTypeNames in the contract, regardless of whether the trace populated them.
        Assert.That(topo.Archetypes, Is.Not.Null);
        foreach (var a in topo.Archetypes)
        {
            // Trace sessions can't recover [Archetype(Alias=...)] — Label falls back to Name.
            Assert.That(a.Label, Is.EqualTo(a.Name));
            Assert.That(a.SchemaRevision, Is.GreaterThanOrEqualTo(0));
            Assert.That(a.ComponentTypeNames, Is.Not.Null);
        }
    }

    [Test]
    public async Task Topology_ComponentFamilies_ClassifiesAllComponents()
    {
        var session = await CreateRfc07TraceAsync();
        var topo = await WaitForTopologyAsync(session.SessionId);

        Assert.That(topo.ComponentFamilies, Is.Not.Null, "ComponentFamilies must be populated");
        Assert.That(topo.ComponentFamilies.ComponentToFamily, Is.Not.Empty);
        // Every component on the wire should have a family entry.
        foreach (var ct in topo.ComponentTypes)
        {
            Assert.That(topo.ComponentFamilies.ComponentToFamily.ContainsKey(ct.Name), Is.True,
                $"component '{ct.Name}' must have a family classification");
        }

        // Heuristic check: Position/Velocity → Spatial, Health → Combat. Fixture uses Game.Position / Game.Velocity / Game.Health.
        var pos = topo.ComponentFamilies.ComponentToFamily["Game.Position"];
        Assert.That(pos, Is.EqualTo("Spatial"));
        var vel = topo.ComponentFamilies.ComponentToFamily["Game.Velocity"];
        Assert.That(vel, Is.EqualTo("Spatial"));
        var health = topo.ComponentFamilies.ComponentToFamily["Game.Health"];
        Assert.That(health, Is.EqualTo("Combat"));

        // Family order should be a subset of canonical, in canonical order, with no duplicates.
        var canonical = new[] { "Spatial", "Combat", "AI", "Inventory", "Rendering", "Networking", "Input", "Misc" };
        var seen = new HashSet<string>();
        var lastIdx = -1;
        foreach (var fam in topo.ComponentFamilies.FamilyOrder)
        {
            Assert.That(seen.Add(fam), Is.True, $"family '{fam}' appears twice in FamilyOrder");
            var idx = Array.IndexOf(canonical, fam);
            Assert.That(idx, Is.GreaterThan(lastIdx), $"family '{fam}' is out of canonical order");
            lastIdx = idx;
        }
    }

    [Test]
    public async Task WhoWrites_ReturnsMatchingSystems()
    {
        var session = await CreateRfc07TraceAsync();
        await WaitForTopologyAsync(session.SessionId); // Make sure metadata is ready

        var result = await GetSystemListAsync(session.SessionId, "queries/who-writes/Game.Position");
        Assert.That(result.Query, Is.EqualTo("Game.Position"));
        Assert.That(result.Systems.Length, Is.EqualTo(1));
        Assert.That(result.Systems[0].Name, Is.EqualTo("Movement"));
    }

    [Test]
    public async Task WhoReads_IncludesAllReadKinds()
    {
        var session = await CreateRfc07TraceAsync();
        await WaitForTopologyAsync(session.SessionId);

        // Game.Position is read snapshot-style by Damage; Movement reads Velocity, not Position.
        var result = await GetSystemListAsync(session.SessionId, "queries/who-reads/Game.Position");
        Assert.That(result.Query, Is.EqualTo("Game.Position"));
        Assert.That(result.Systems.Length, Is.EqualTo(1));
        Assert.That(result.Systems[0].Name, Is.EqualTo("Damage"));
    }

    [Test]
    public async Task WhoReads_NoMatch_ReturnsEmptyArray()
    {
        var session = await CreateRfc07TraceAsync();
        await WaitForTopologyAsync(session.SessionId);

        var result = await GetSystemListAsync(session.SessionId, "queries/who-reads/Game.NonExistent");
        Assert.That(result.Systems, Is.Empty);
    }

    private async Task<SystemListDto> GetSystemListAsync(Guid sessionId, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/{path}");
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        req.Headers.Add("X-Workbench-Api", "1");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET {path} → {resp.StatusCode}");
        return JsonSerializer.Deserialize<SystemListDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    private static SystemDefinitionDto FindByName(SystemDefinitionDto[] systems, string name)
    {
        foreach (var s in systems)
        {
            if (s.Name == name) return s;
        }
        Assert.Fail($"system '{name}' not found in topology");
        return null!;
    }
}
