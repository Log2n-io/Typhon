using System;
using System.IO;
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
/// S1 — the asset origin over real HTTP: content types the glTF loader needs, caching that never has to be invalidated,
/// and the brotli sidecar handed over only to a caller that takes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real Kestrel rather than a unit test of the middleware.</b> Everything that can go wrong here is a header: a GLB
/// served as <c>application/octet-stream</c>, a <c>.br</c> body sent without <c>Content-Encoding</c> (which reaches the
/// browser as a corrupt glTF), a hashed file served with revalidation instead of <c>immutable</c>. None of those are
/// visible from the C# side of the call — only from the response.
/// </para>
/// <para>
/// No world and no simulation: this host maps the asset origin and nothing else.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AssetOriginHostChecks
{
    private const string Catalogue = """{"schema":1,"assets":{"house":{"rev":1,"descriptor":"house.aaaa.json","glb":"house.bbbb.glb"}},"textures":["t.cccc.ktx2"],"version":"abc123"}""";

    /// <summary>What a brotli sidecar holds here. Not real brotli — nothing in this fixture decodes it.</summary>
    private const string Compressed = "pretend-this-is-brotli";

    private const string Raw = "pretend-this-is-a-glb";

    private string _dir;
    private WebApplication _app;
    private HttpClient _client;

    [SetUp]
    public async Task SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "catalogue.json"), Catalogue);
        File.WriteAllText(Path.Combine(_dir, "catalogue.abc123.json"), Catalogue);
        File.WriteAllText(Path.Combine(_dir, "house.bbbb.glb"), Raw);
        File.WriteAllText(Path.Combine(_dir, "house.bbbb.glb.br"), Compressed);
        File.WriteAllText(Path.Combine(_dir, "house.aaaa.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "t.cccc.ktx2"), "pretend-this-is-ktx2");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        // Port 0: the OS picks a free one, so parallel fixtures and a developer's own server cannot collide.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));

        _app = builder.Build();
        AssetOrigin.Open(_dir).MapOn(_app);

        await _app.StartAsync();
        // AutomaticDecompression off, on purpose: these cases are ABOUT Content-Encoding, and a handler that unwrapped it
        // would be asserting on what the handler did rather than on what the server sent.
        _client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri(Address(_app)),
        };
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

        if (_dir != null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>A GLB arrives as a glTF binary, cached forever, because its name carries its content's hash.</summary>
    [Test]
    public async Task AGlbIsAGltfBinaryCachedForever()
    {
        using var response = await _client.GetAsync("/assets/house.bbbb.glb");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("model/gltf-binary"));
            Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("immutable"));
            Assert.That(response.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromDays(365)));
            Assert.That(body, Is.EqualTo(Raw), "no Accept-Encoding, so the identity bytes");
        });
    }

    /// <summary>
    /// A caller that takes brotli gets the sidecar, declared as such and with the content type of what it decodes to.
    /// </summary>
    /// <remarks>
    /// The declaration is the whole point. A sidecar body sent without <c>Content-Encoding: br</c> reaches the glTF loader
    /// as a GLB whose magic number is wrong, which is reported in the browser as a corrupt asset rather than as a server
    /// that answered the wrong file.
    /// </remarks>
    [Test]
    public async Task ACallerThatTakesBrotliGetsTheSidecar()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/house.bbbb.glb");
        request.Headers.Add("Accept-Encoding", "gzip, deflate, br");

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo(Compressed));
            Assert.That(response.Content.Headers.ContentEncoding, Does.Contain("br"));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("model/gltf-binary"), "the type of the resource, not of the sidecar");
            Assert.That(response.Headers.Vary, Does.Contain("Accept-Encoding"));
            Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("immutable"));
        });
    }

    /// <summary>A caller that refuses brotli gets the identity bytes, even though the sidecar is right there.</summary>
    [Test]
    public async Task ACallerThatRefusesBrotliGetsTheIdentityBytes()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/house.bbbb.glb");
        request.Headers.Add("Accept-Encoding", "gzip, br;q=0");

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(body, Is.EqualTo(Raw));
            Assert.That(response.Content.Headers.ContentEncoding, Is.Empty);
        });
    }

    /// <summary>A KTX2 texture arrives as one, which is what the loader's transcoder path keys on.</summary>
    [Test]
    public async Task ATextureIsKtx2AndIsNotPrecompressed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/t.cccc.ktx2");
        request.Headers.Add("Accept-Encoding", "br");

        using var response = await _client.SendAsync(request);
        Assert.Multiple(() =>
        {
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/ktx2"));
            // KTX2 is already entropy-coded, so the exporter writes no sidecar for it and this must not look for one.
            Assert.That(response.Content.Headers.ContentEncoding, Is.Empty);
        });
    }

    /// <summary>
    /// The immutable catalogue is cached forever; the mutable pointer beside it is revalidated every time.
    /// </summary>
    [Test]
    public async Task ThePointerIsRevalidatedAndTheVersionIsNot()
    {
        using var pinned = await _client.GetAsync("/assets/catalogue.abc123.json");
        using var pointer = await _client.GetAsync("/assets/catalogue.json");
        Assert.Multiple(() =>
        {
            Assert.That(pinned.Headers.CacheControl?.ToString(), Does.Contain("immutable"));
            Assert.That(pointer.Headers.CacheControl?.NoCache, Is.True, "a pointer that is cached forever is not a pointer");
            Assert.That(pointer.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
        });
    }

    /// <summary>Nothing outside the folder is reachable, and a name that is not published is a 404.</summary>
    [Test]
    public async Task NothingOutsideTheFolderIsReachable()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "not for the internet");
        try
        {
            using var traversal = await _client.GetAsync($"/assets/../{Path.GetFileName(secret)}");
            using var absent = await _client.GetAsync("/assets/house.zzzz.glb");
            Assert.Multiple(() =>
            {
                Assert.That(traversal.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
                Assert.That(absent.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            });
        }
        finally
        {
            File.Delete(secret);
        }
    }

    /// <summary>The address Kestrel actually bound, since the port was asked for as zero.</summary>
    /// <param name="app">The started host.</param>
    /// <returns>Its first address.</returns>
    private static string Address(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        Assert.That(addresses?.Addresses, Is.Not.Null.And.Not.Empty, "Kestrel reported no bound address");
        foreach (var address in addresses.Addresses)
        {
            return address;
        }

        return null;
    }
}
