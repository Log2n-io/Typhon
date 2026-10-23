using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using AuthJson = SwgTatooine.Server.Accounts.AuthJson;

namespace SwgTatooine.Tests;

/// <summary>
/// A2 end to end: <c>/auth/signup</c> and <c>/auth/login</c> over real HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>This fixture exists because of one specific way the endpoints can be broken while every other test passes.</b> The demo
/// host builds with <c>WebApplication.CreateSlimBuilder</c>, which configures SOURCE-GENERATED JSON and has no reflection-based
/// resolver to fall back on. A handler returning an anonymous object, or a body type missing from
/// <see cref="Server.AuthJson"/>, compiles cleanly and throws on the first request. Nothing short of an actual round trip catches that.
/// </para>
/// <para>
/// <b>It builds its own slim host rather than calling <c>TatooineHost.ServeAsync</c>.</b> That method also starts the
/// subscriptions transport, the WebSocket route and the static file server — none of which these two endpoints touch — and it
/// never returns. What is shared with it is the thing under test: the same <c>AddAuthJson</c> and the same <c>MapAuth</c>, on the
/// same slim builder. If those two lines move, this fixture has to move with them, which is noted here because that is the one
/// way it could silently stop testing the real configuration.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class AuthEndpointChecks
{
    private string _dir;
    private TatooineSim _sim;
    private WebApplication _app;
    private HttpClient _client;

    private const string GoodPassword = "bantha-poodoo";

    [SetUp]
    public async Task SetUp()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = 0;
        _sim = new TatooineSim(config);
        _sim.Initialize();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);

        // Port 0: the OS picks a free one, so parallel fixtures and a developer's own server cannot collide.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAuthJson();

        _app = builder.Build();

        // A generous capacity, because these cases are about the endpoints and not about the limiter — which has its own fixture.
        _app.MapAuth(_sim.Dbe, new AuthRateLimiter(1_000, TimeSpan.FromSeconds(1), 1_000));

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

        _sim?.Dispose();
        _sim = null;
        Worlds.Delete(_dir);
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

    /// <summary>AC A-1 (the HTTP half): a signup returns a usable token, and a login returns another.</summary>
    [Test]
    [CancelAfter(30_000)]
    [Category("Integration")]
    public async Task SignupAndLogin_BothReturnAValidToken()
    {
        var signup = await _client.PostAsJsonAsync("/auth/signup", new AuthRequest("Han_Solo", GoodPassword), AuthJson.Default.AuthRequest);
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK), await signup.Content.ReadAsStringAsync());

        var created = await signup.Content.ReadFromJsonAsync(AuthJson.Default.AuthResponse);
        Assert.Multiple(() =>
        {
            Assert.That(created, Is.Not.Null);
            Assert.That(created.Name, Is.EqualTo("han_solo"), "the response must carry the NORMALISED name, or a client shows one thing and logs in as another");
            Assert.That(created.ExpiresInSeconds, Is.GreaterThan(0));
            Assert.That(AuthToken.TryValidate(created.Token, out _), Is.True, "the token a signup returns must be one Admit accepts");
        });

        var login = await _client.PostAsJsonAsync("/auth/login", new AuthRequest("HAN_SOLO", GoodPassword), AuthJson.Default.AuthRequest);
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var loggedIn = await login.Content.ReadFromJsonAsync(AuthJson.Default.AuthResponse);
        Assert.Multiple(() =>
        {
            Assert.That(AuthToken.TryValidate(loggedIn.Token, out var a), Is.True);
            Assert.That(AuthToken.TryValidate(created.Token, out var b), Is.True);
            Assert.That(a, Is.EqualTo(b), "both tokens must name the same account");
        });
    }

    /// <summary>AC A-2 over the wire: a taken name is 409 with a sentence a signup form can show.</summary>
    [Test]
    [CancelAfter(30_000)]
    [Category("Integration")]
    public async Task ATakenName_Is409_WithTheSentenceAFormShows()
    {
        await _client.PostAsJsonAsync("/auth/signup", new AuthRequest("Chewie", GoodPassword), AuthJson.Default.AuthRequest);

        var again = await _client.PostAsJsonAsync("/auth/signup", new AuthRequest("chewie", "another-password"), AuthJson.Default.AuthRequest);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        var error = await again.Content.ReadFromJsonAsync(AuthJson.Default.AuthError);
        Assert.That(error?.Error, Is.EqualTo("that name is taken"));
    }

    /// <summary>
    /// A wrong password and an unknown name are the same 401 with the same body.
    /// </summary>
    /// <remarks>
    /// <b>Byte-identical, which is the assertion.</b> Two refusals that differ by a word are a name oracle, and enumerating names
    /// is the first step of a credential-stuffing run. The server's own log may be specific; the wire must not.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [Category("Integration")]
    public async Task AWrongPasswordAndAnUnknownNameAreIndistinguishable()
    {
        await _client.PostAsJsonAsync("/auth/signup", new AuthRequest("Jabba", GoodPassword), AuthJson.Default.AuthRequest);

        var wrongPassword = await _client.PostAsJsonAsync("/auth/login", new AuthRequest("Jabba", "not-the-password"), AuthJson.Default.AuthRequest);
        var unknownName = await _client.PostAsJsonAsync("/auth/login", new AuthRequest("Greedo", GoodPassword), AuthJson.Default.AuthRequest);

        var first = await wrongPassword.Content.ReadAsStringAsync();
        var second = await unknownName.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(wrongPassword.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(unknownName.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(second, Is.EqualTo(first), "the two refusals must be byte-identical, or the endpoint says which names exist");
        });
    }

    /// <summary>A refused name or password comes back as 400 with the rule, so a form can explain itself.</summary>
    [Test]
    [CancelAfter(30_000)]
    [Category("Integration")]
    public async Task ABadNameOrPasswordIs400WithTheRule()
    {
        var badName = await _client.PostAsJsonAsync("/auth/signup", new AuthRequest("9lives", GoodPassword), AuthJson.Default.AuthRequest);
        var badPassword = await _client.PostAsJsonAsync("/auth/signup", new AuthRequest("Wicket", "short"), AuthJson.Default.AuthRequest);

        Assert.Multiple(async () =>
        {
            Assert.That(badName.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await badName.Content.ReadFromJsonAsync(AuthJson.Default.AuthError))?.Error, Is.EqualTo(AccountName.Rule));
            Assert.That(badPassword.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await badPassword.Content.ReadFromJsonAsync(AuthJson.Default.AuthError))?.Error, Does.Contain("password"));
        });
    }

    /// <summary>The limiter is wired, and its refusal is a 429 rather than a dropped connection.</summary>
    [Test]
    [CancelAfter(30_000)]
    [Category("Integration")]
    public async Task PastTheRateLimitTheEndpointAnswers429()
    {
        // This test needs its own tiny limiter, so a second host is built rather than loosening the fixture's.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAuthJson();

        await using var app = builder.Build();
        app.MapAuth(_sim.Dbe, new AuthRateLimiter(2, TimeSpan.FromHours(1), 100));
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };
        var body = new AuthRequest("Lando", GoodPassword);

        Assert.That((await client.PostAsJsonAsync("/auth/login", body, AuthJson.Default.AuthRequest)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await client.PostAsJsonAsync("/auth/login", body, AuthJson.Default.AuthRequest)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var limited = await client.PostAsJsonAsync("/auth/login", body, AuthJson.Default.AuthRequest);
        Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests), "the third attempt is past a capacity of two");

        await app.StopAsync();
    }

    /// <summary>A body that is not the expected shape is a 400, not a 500.</summary>
    /// <remarks>
    /// The endpoints take an untrusted body, so malformed JSON has to be a client error. A 500 here would also mean an exception
    /// in a handler, which is the shape the source-generated-JSON mistake takes.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public async Task MalformedJsonIsAClientErrorAndNeverAServerError()
    {
        foreach (var body in new[] { "{", "[]", "\"just a string\"", "{\"name\":42,\"password\":true}" })
        {
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("/auth/signup", content);
            Assert.That((int)response.StatusCode, Is.InRange(400, 499), $"posting {body} produced {(int)response.StatusCode}");
        }
    }
}
