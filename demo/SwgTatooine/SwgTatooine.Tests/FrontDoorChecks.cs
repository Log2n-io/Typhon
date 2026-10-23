using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// I3: the front door's shared secret, over the real middleware, with both of its branches reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>This fixture used to be self-fulfilling and a review said so.</b> It contained a COPY of the middleware, and a copy cannot
/// fail when the original is wrong. Worse, every request a test makes arrives over loopback, and loopback is exempt — so the
/// exempt branch was the only one that ever ran and <b>nothing tested that an outside caller without the header is refused</b>,
/// which is the entire point of the control.
/// </para>
/// <para>
/// Both halves are fixed by one change: <c>TatooineHost.UseOriginSecret</c> is now its own internal method, so these cases run
/// the production pipeline, and a middleware registered <i>ahead</i> of it rewrites <c>RemoteIpAddress</c> to a public address —
/// which is the only way to reach the refusal path from a test at all.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class FrontDoorChecks
{
    private const string Secret = "a-front-door-secret";
    private const string Outside = "203.0.113.7";

    private WebApplication _app;
    private HttpClient _client;

    /// <summary>The address the next request should appear to come from; <see langword="null"/> leaves it as loopback.</summary>
    private static string _pretendCaller;

    [SetUp]
    public async Task SetUp()
    {
        _pretendCaller = null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();

        // AHEAD of the middleware under test, so the production code sees whatever address a case asks for. Kestrel always
        // reports loopback for a local socket, so without this the exempt branch is the only one a test can reach.
        _app.Use(async (context, next) =>
        {
            if (_pretendCaller != null)
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(_pretendCaller);
            }

            await next(context);
        });

        // The real thing.
        _app.UseOriginSecret(Secret);

        _app.MapGet("/probe", () => Results.Text("reached"));
        _app.MapGet("/healthz", () => Results.Text("ok"));
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(Address(_app)) };
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

        _pretendCaller = null;
    }

    /// <summary>A caller inside the network is exempt, which is what keeps the bot fleet and a dev box working.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task ACallerInsideTheNetworkNeedsNoSecret()
    {
        var response = await _client.GetAsync("/probe");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(TatooineHost.OriginSecretHeader, Is.EqualTo("X-Tatooine-Origin"),
                "the header's name is configured on the CDN, so changing it is a deployment change and not a rename");
        });
    }

    /// <summary>
    /// <b>The refusal, which is the case the old fixture could not reach.</b>
    /// </summary>
    /// <remarks>
    /// A public caller with no header, with the wrong header, and with a prefix of the right one. The last matters because a
    /// byte-by-byte comparison would accept a prefix as "equal so far" and leak the secret one character at a time to anyone
    /// able to measure the response.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public async Task AnOutsideCallerIsRefusedWithoutTheSecret()
    {
        _pretendCaller = Outside;

        var bare = await _client.GetAsync("/probe");

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/probe");
        wrong.Headers.Add(TatooineHost.OriginSecretHeader, "not-the-secret");
        var refusedWrong = await _client.SendAsync(wrong);

        using var prefix = new HttpRequestMessage(HttpMethod.Get, "/probe");
        prefix.Headers.Add(TatooineHost.OriginSecretHeader, Secret[..5]);
        var refusedPrefix = await _client.SendAsync(prefix);

        Assert.Multiple(() =>
        {
            Assert.That(bare.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "a public caller with no header must be refused");
            Assert.That(refusedWrong.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(refusedPrefix.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "a prefix of the secret must not pass");
        });

        // Bare 403: no body to confirm to a scanner that something is here, and no WWW-Authenticate to invite a credential.
        Assert.Multiple(() =>
        {
            Assert.That(bare.Content.Headers.ContentLength ?? 0, Is.Zero, "the refusal carries a body");
            Assert.That(bare.Headers.WwwAuthenticate, Is.Empty, "the refusal invites a credential");
        });
    }

    /// <summary>An outside caller WITH the secret is served, or the front door could not work at all.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task AnOutsideCallerWithTheSecretIsServed()
    {
        _pretendCaller = Outside;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add(TatooineHost.OriginSecretHeader, Secret);
        var response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("reached"));
    }

    /// <summary>
    /// The check is ahead of everything, so no endpoint escapes it.
    /// </summary>
    /// <remarks>
    /// <c>/healthz</c> specifically, because it is the one route somebody would be tempted to exempt — and the reason not to is
    /// that an unauthenticated route on a public origin is exactly what a scanner looks for. Nothing health-checks this server
    /// from outside the VPC: CloudFront does not, and the instance's own checks are hypervisor-level.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public async Task NoEndpointEscapesTheCheck()
    {
        _pretendCaller = Outside;
        var health = await _client.GetAsync("/healthz");
        var missing = await _client.GetAsync("/does-not-exist");

        Assert.Multiple(() =>
        {
            Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "/healthz answered an outside caller with no secret");

            // A 404 would tell an outside caller which routes exist. The check running first means it cannot.
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "an unknown route leaked a 404 to an outside caller");
        });
    }

    /// <summary>
    /// The exemption predicate, at the edges of every range it decides.
    /// </summary>
    /// <param name="address">The address under test.</param>
    /// <param name="expected">Whether it should count as inside.</param>
    /// <remarks>
    /// The boundaries, because that is where an off-by-one in a range check lives: 172.16 and 172.31 are private and 172.15 and
    /// 172.32 are not, which is the one range people get wrong. A public address treated as inside would mean the secret
    /// protects nothing at all — so each of these runs through the live pipeline rather than through a reimplementation.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [TestCase("127.0.0.1", true)]
    [TestCase("10.0.0.1", true)]
    [TestCase("10.255.255.254", true)]
    [TestCase("172.16.0.1", true)]
    [TestCase("172.31.255.254", true)]
    [TestCase("172.15.0.1", false)]
    [TestCase("172.32.0.1", false)]
    [TestCase("192.168.1.1", true)]
    [TestCase("192.169.1.1", false)]
    [TestCase("169.254.1.1", true)]
    [TestCase("1.1.1.1", false)]
    [TestCase("8.8.8.8", false)]
    [TestCase("::1", true)]
    [TestCase("fd00::1", true)]
    [TestCase("fe80::1", true)]
    [TestCase("2606:4700::1", false)]
    [TestCase("::ffff:10.0.0.1", true)]
    [TestCase("::ffff:8.8.8.8", false)]
    public async Task TheExemptionCoversExactlyTheNonRoutableRanges(string address, bool expected)
    {
        _pretendCaller = address;
        var response = await _client.GetAsync("/probe");

        Assert.That(
            response.StatusCode,
            Is.EqualTo(expected ? HttpStatusCode.OK : HttpStatusCode.Forbidden),
            $"{address} was treated as {(expected ? "outside" : "inside")} the network");
    }

    /// <summary>The address Kestrel bound, since the port was asked for as zero.</summary>
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
