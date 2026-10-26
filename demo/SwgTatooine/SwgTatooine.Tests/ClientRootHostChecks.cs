using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The built client over real HTTP: the page at the root, and every file the page asks for, including the ones whose extension
/// ASP.NET has no content type for.
/// </summary>
/// <remarks>No world: this host maps the client root and nothing else.</remarks>
[TestFixture]
internal sealed class ClientRootHostChecks
{
    private string _dir;
    private WebApplication _app;
    private HttpClient _client;

    [SetUp]
    public async Task SetUp()
    {
        _dir = Worlds.NewDirectory();
        var vendor = Path.Combine(_dir, "vendor", "babylon-1.0.0");
        Directory.CreateDirectory(vendor);
        File.WriteAllText(Path.Combine(_dir, "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(vendor, "environmentSpecular.env"), "pretend-this-is-an-environment");
        File.WriteAllText(Path.Combine(_dir, "stray.unknownext"), "not for serving");
        Directory.CreateDirectory(Path.Combine(_dir, "app"));
        File.WriteAllText(Path.Combine(_dir, "app", "index-AbCd1234.js"), string.Concat(Enumerable.Repeat("export const tatooine = 1;\n", 400)));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        TatooineHost.AddClient(builder.Services);
        _app = builder.Build();
        TatooineHost.MapClient(_app, _dir);
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    [TearDown]
    public async Task TearDown()
    {
        _client?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        Worlds.Delete(_dir);
    }

    /// <summary>The root is the page.</summary>
    [Test]
    public async Task TheRootIsThePage()
    {
        using var response = await _client.GetAsync("/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
    }

    /// <summary>
    /// Babylon's prefiltered environment is served. It was a 404, so every served run drew without image-based lighting.
    /// </summary>
    [Test]
    public async Task TheEnvironmentIsServed()
    {
        using var response = await _client.GetAsync("/vendor/babylon-1.0.0/environmentSpecular.env");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("pretend-this-is-an-environment"));
    }

    /// <summary>
    /// The page is revalidated on every load, and what it names is cached for good: a chunk by its hash, a vendored file by its Babylon version. Without a
    /// policy the browser guessed one, and could keep a stale page naming chunks a rebuild had deleted.
    /// </summary>
    [TestCase("/", "no-cache")]
    [TestCase("/index.html", "no-cache")]
    [TestCase("/app/index-AbCd1234.js", TatooineHost.Immutable)]
    [TestCase("/vendor/babylon-1.0.0/environmentSpecular.env", TatooineHost.Immutable)]
    public async Task CachedByWhatChangesItsName(string path, string cacheControl)
    {
        using var response = await _client.GetAsync(path);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.NonValidated["Cache-Control"].ToString(), Is.EqualTo(cacheControl));
    }

    /// <summary>The client's text goes out compressed to a browser that takes brotli, and as it is to one that takes nothing.</summary>
    [Test]
    public async Task ChunksAreCompressedForWhoeverTakesIt()
    {
        using var brotli = new HttpRequestMessage(HttpMethod.Get, "/app/index-AbCd1234.js");
        brotli.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
        using var compressed = await _client.SendAsync(brotli);
        Assert.That(compressed.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "br" }));
        Assert.That((await compressed.Content.ReadAsByteArrayAsync()).Length, Is.LessThan(400 * 27 / 4));

        using var plain = await _client.GetAsync("/app/index-AbCd1234.js");
        Assert.That(plain.Content.Headers.ContentEncoding, Is.Empty);
        Assert.That(await plain.Content.ReadAsStringAsync(), Does.StartWith("export const tatooine = 1;"));
    }

    /// <summary>Only <c>.env</c> was added: an extension nobody mapped is still not served.</summary>
    [Test]
    public async Task AnUnmappedExtensionIsStillRefused()
    {
        using var response = await _client.GetAsync("/stray.unknownext");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
