using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Replication;

namespace SwgTatooine.Tests;

/// <summary>
/// Serving TLS directly: the certificate that has to survive a renewal, and <c>/auth</c> kept off the public internet.
/// </summary>
/// <remarks>
/// <b>Both of these fail silently and late, which is why they are tested rather than reasoned about.</b> A certificate reload
/// that does not work is invisible for ninety days and then takes the site down; an <c>/auth</c> gate that does not hold is
/// invisible until somebody points a load at it. Neither shows up in a deployment that looks healthy on the day it is made.
/// </remarks>
[Category("Integration")]
[TestFixture]
internal sealed class PublicTlsChecks
{
    private string _dir;

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("tatooine-tls").FullName;

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory we could not remove is not a test failure.
        }
    }

    /// <summary>A certificate loads from the PEM pair certbot writes.</summary>
    [Test]
    public void ACertificateLoadsFromCertbotsPemPair()
    {
        var (chain, key) = Write("demo.typhondb.io", days: 90);
        var certificate = new TlsCertificate(chain, key);

        Assert.Multiple(() =>
        {
            Assert.That(certificate.Current(), Is.Not.Null);
            Assert.That(certificate.Current().Subject, Does.Contain("demo.typhondb.io"));
            Assert.That(certificate.Current().HasPrivateKey, Is.True, "a certificate with no private key cannot complete a handshake");
            Assert.That(certificate.Describe(), Does.Contain("demo.typhondb.io"));
        });
    }

    /// <summary>
    /// <b>A renewal on disk is picked up without a restart.</b>
    /// </summary>
    /// <remarks>
    /// The whole reason the certificate is behind a selector rather than handed to Kestrel once. The alternative is certbot's
    /// <c>--deploy-hook</c> restarting the service, which kicks every connected session on a ninety-day schedule — survivable,
    /// because shutdown is clean, but not something to put in a soak or a demo when re-reading a file is the fix.
    /// </remarks>
    [Test]
    public void ARenewedCertificateIsPickedUpWithoutARestart()
    {
        var (chain, key) = Write("demo.typhondb.io", days: 10);

        // A zero interval, so the next call re-checks. The production interval is a minute, which a test must not wait for.
        var certificate = new TlsCertificate(chain, key, TimeSpan.Zero);
        var before = certificate.Current().NotAfter;

        // What certbot does: replaces both files in place.
        Write("demo.typhondb.io", days: 300, chainPath: chain, keyPath: key);
        var after = certificate.Current().NotAfter;

        Assert.That(after, Is.GreaterThan(before.AddDays(100)), "the renewed certificate was not picked up");
    }

    /// <summary>
    /// A half-written renewal keeps the old certificate rather than dropping TLS.
    /// </summary>
    /// <remarks>
    /// certbot writes the chain and the key separately, so there is a window where they do not match. Throwing there would
    /// refuse connections for a certificate that is about to be perfectly valid — the failure would last seconds and look like
    /// an outage. Keeping the old one is correct, and the next check picks the new pair up.
    /// </remarks>
    [Test]
    public void AMismatchedPairKeepsTheCertificateItAlreadyHad()
    {
        var (chain, key) = Write("demo.typhondb.io", days: 10);
        var certificate = new TlsCertificate(chain, key, TimeSpan.Zero);
        var before = certificate.Current().NotAfter;

        // A new chain whose key is NOT the matching one: exactly the mid-write state.
        var (otherChain, _) = Write("other.example.com", days: 300);
        File.Copy(otherChain, chain, overwrite: true);

        Assert.Multiple(() =>
        {
            Assert.That(certificate.Current().NotAfter, Is.EqualTo(before), "a mismatched pair must not replace a working certificate");
            Assert.That(certificate.Current().HasPrivateKey, Is.True);
        });

        // And once the key catches up, the new pair IS taken.
        Write("renewed.typhondb.io", days: 300, chainPath: chain, keyPath: key);
        Assert.That(certificate.Current().Subject, Does.Contain("renewed.typhondb.io"));
    }

    /// <summary>A missing certificate is refused at construction, loudly, rather than binding a port that cannot handshake.</summary>
    [Test]
    public void AMissingCertificateIsRefusedAtStartup()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => new TlsCertificate(Path.Combine(_dir, "absent.pem"), Path.Combine(_dir, "absent-key.pem")));

        Assert.That(thrown.Message, Does.Contain("certbot"), "the message must say what to run");
    }

    /// <summary>
    /// <c>/auth</c> answers a caller inside the network and gives the internet a 404.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>404 and not 403</b>: a refusal confirms the endpoint is there and worth returning to, where "no such route" is the
    /// same answer any unrelated path gets.
    /// </para>
    /// <para>
    /// The inside case matters as much as the outside one — the bot fleet signs up through this endpoint over the server's
    /// private address, so a gate that refused everyone would break provisioning while looking like a security win.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public async Task AuthIsAnsweredInsideTheNetworkAndHiddenFromTheInternet()
    {
        string pretend = null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (pretend != null)
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(pretend);
            }

            await next(context);
        });

        app.UseAuthInsideVpcOnly();
        app.MapPost("/auth/signup", () => Results.Text("signed up"));
        app.MapGet("/typhon/stats.json", () => Results.Text("{}"));

        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(BoundAddress(app)) };

            var inside = await client.PostAsync("/auth/signup", new StringContent(string.Empty));

            pretend = "203.0.113.9";
            var outside = await client.PostAsync("/auth/signup", new StringContent(string.Empty));
            var otherRoute = await client.GetAsync("/typhon/stats.json");

            Assert.Multiple(() =>
            {
                Assert.That(inside.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the bot fleet signs up over the private address and must not be refused");
                Assert.That(outside.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "/auth answered a caller on the public internet");

                // The gate is for /auth ONLY. Everything else still has to work for an outside caller, or a public demo serves
                // nothing at all.
                Assert.That(otherRoute.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the gate swallowed a route that is not /auth");
            });
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// <b>Kestrel actually completes a TLS handshake with the certificate — the wiring, not just the loader.</b>
    /// </summary>
    /// <remarks>
    /// <b>This is the regression test for a bug that every other test here missed.</b> The host wired the selector as
    /// <c>listen.UseHttps(_ =&gt; tls.Current())</c>, which compiles: it binds to the
    /// <c>Action&lt;HttpsConnectionAdapterOptions&gt;</c> overload, where the lambda is a CONFIGURE callback and its return
    /// value is thrown away. So nothing was configured, Kestrel fell back to the ASP.NET developer certificate, and the
    /// deployed server died at startup with "No server certificate was specified" — while the five cases above, which all
    /// exercise <see cref="TlsCertificate"/> directly, passed happily.
    /// <para>
    /// Two overloads, one of which silently does nothing, is not a mistake a loader test can see. Only a handshake can.
    /// </para>
    /// <para>
    /// The client is told to accept this specific self-signed certificate rather than any certificate: a handler that
    /// accepted everything would also pass if Kestrel presented something else entirely, which is most of what is being
    /// checked here.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public async Task KestrelPresentsTheCertificateOverARealHandshake()
    {
        var (chain, key) = Write("kestrel.test", days: 60);
        var tls = new TlsCertificate(chain, key);
        var expected = tls.Current().Thumbprint;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);

        // The SAME shape as TatooineHost.ServeAsync. If that moves, this has to move with it.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
        {
            https.ServerCertificateSelector = (_, _) => tls.Current();
        })));

        var app = builder.Build();
        app.MapGet("/probe", () => Results.Text("over tls"));
        await app.StartAsync();
        try
        {
            string presented = null;
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                {
                    presented = certificate?.Thumbprint;
                    return string.Equals(presented, expected, StringComparison.OrdinalIgnoreCase);
                },
            };

            using var client = new HttpClient(handler) { BaseAddress = new Uri(BoundAddress(app)) };
            var response = await client.GetAsync("/probe");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the handshake did not complete");
                Assert.That(body, Is.EqualTo("over tls"));
                Assert.That(presented, Is.EqualTo(expected).IgnoreCase, "Kestrel presented a different certificate than the one configured");
            });
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>Writes a self-signed PEM pair in the shape certbot leaves behind.</summary>
    /// <param name="commonName">The subject name.</param>
    /// <param name="days">How long until it expires.</param>
    /// <param name="chainPath">Where to write the chain; a new file when not given.</param>
    /// <param name="keyPath">Where to write the key; a new file when not given.</param>
    /// <returns>The two paths.</returns>
    private (string Chain, string Key) Write(string commonName, int days, string chainPath = null, string keyPath = null)
    {
        chainPath ??= Path.Combine(_dir, $"{Guid.NewGuid():N}-fullchain.pem");
        keyPath ??= Path.Combine(_dir, $"{Guid.NewGuid():N}-privkey.pem");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days));

        File.WriteAllText(chainPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        // The stamp is write time and length, and a test can write twice inside one filesystem tick. Nudging the timestamp is
        // what keeps this test about the reload rather than about the clock's resolution.
        File.SetLastWriteTimeUtc(chainPath, DateTime.UtcNow.AddSeconds(days));
        File.SetLastWriteTimeUtc(keyPath, DateTime.UtcNow.AddSeconds(days));
        return (chainPath, keyPath);
    }

    /// <summary>The address Kestrel bound, since the port was asked for as zero.</summary>
    /// <param name="app">The started host.</param>
    /// <returns>Its first address.</returns>
    private static string BoundAddress(WebApplication app)
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
