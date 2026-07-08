using System.Net;
using NUnit.Framework;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Covers the static-SPA serving added when the Workbench host was folded into <c>typhon ui</c> (#429):
/// the pre-built SPA (<c>wwwroot/index.html</c>) is served at the root, unknown client-side routes fall back to
/// <c>index.html</c> so the SPA router can handle them, and — critically — the API/health gating is unchanged
/// (static serving must not open a hole). Asserts against the real built <c>index.html</c>, which always carries
/// the <c>&lt;div id="root"&gt;</c> mount point the SPA bootstraps into.
/// </summary>
[TestFixture]
public sealed class WorkbenchHostStaticFilesTests
{
    // The Vite-built index.html always contains the React mount node; a stable, build-independent marker.
    private const string SpaMountMarker = "id=\"root\"";

    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task Root_ServesSpaIndexHtml()
    {
        var resp = await _client.GetAsync("/");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(resp.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain(SpaMountMarker));
    }

    [Test]
    public async Task UnknownClientRoute_FallsBackToIndexHtml()
    {
        // A deep client-side route with no server endpoint must serve index.html so the SPA router handles it.
        var resp = await _client.GetAsync("/data/browser/some/deep/route");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(resp.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain(SpaMountMarker));
    }

    [Test]
    public async Task ApiEndpoint_WithoutBootstrapToken_StillReturns401()
    {
        // Regression: adding static serving + SPA fallback must NOT bypass the bootstrap-token gate on the API.
        var resp = await _client.GetAsync("/api/options");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Health_RemainsUngated()
    {
        var resp = await _client.GetAsync("/health");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
