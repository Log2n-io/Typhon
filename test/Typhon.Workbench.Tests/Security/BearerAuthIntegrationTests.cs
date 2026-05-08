using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Workbench.Security;

namespace Typhon.Workbench.Tests.Security;

/// <summary>
/// End-to-end auth tests covering the Personal Access Token (Bearer) fallback wired into
/// <c>RequireBootstrapTokenAttribute</c>. The bootstrap token path is exercised by the rest of
/// the test suite — these tests pin the contract that PATs are an *equivalent* credential.
/// </summary>
[TestFixture]
public sealed class BearerAuthIntegrationTests
{
    private WorkbenchFactory _factory;

    [SetUp]
    public void SetUp() => _factory = new WorkbenchFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private string MintToken(string name)
    {
        var store = _factory.Services.GetRequiredService<PersonalAccessTokenStore>();
        return store.Mint(name);
    }

    [Test]
    public async Task BearerToken_AloneIsSufficient_NoBootstrapHeader()
    {
        var token = MintToken("ci-bot");

        // Default client has no bootstrap-token handler attached. We attach Authorization: Bearer
        // and expect the request to succeed.
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/options");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), await resp.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task BogusBearerToken_Returns401()
    {
        MintToken("ci-bot"); // a real one exists, so the store isn't empty
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/options");
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new string('A', PersonalAccessTokenStore.TokenHexLength));

        var resp = await client.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task BootstrapToken_StillWorks_WithoutBearer()
    {
        // Sanity: the existing bootstrap-token path is not regressed by adding the PAT fallback.
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/options");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task RevokedBearer_Returns401()
    {
        var token = MintToken("ephemeral");

        var client = _factory.CreateClient();
        var req1 = new HttpRequestMessage(HttpMethod.Get, "/api/options");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.That((await client.SendAsync(req1)).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var store = _factory.Services.GetRequiredService<PersonalAccessTokenStore>();
        Assert.That(store.Revoke("ephemeral"), Is.True);

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/options");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.That((await client.SendAsync(req2)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task WrongScheme_Returns401()
    {
        var token = MintToken("scheme-test");

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/options");
        req.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");

        var resp = await client.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task BearerSchemeIsCaseInsensitive()
    {
        // RFC 7235 §2.1: scheme name is case-insensitive. Our parser must honour that.
        var token = MintToken("case-test");

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/options");
        req.Headers.TryAddWithoutValidation("Authorization", $"bearer {token}");

        var resp = await client.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
