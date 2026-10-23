using System;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SwgTatooine.Accounts;

/// <summary>The body a client posts to <c>/auth/signup</c> and <c>/auth/login</c>.</summary>
/// <param name="Name">The account name.</param>
/// <param name="Password">The plaintext password. Never logged, never echoed, never put in a URL.</param>
/// <remarks>
/// <b>A POST body and not a query string</b>, which is the whole reason this is a record rather than two route parameters: a
/// query string is written to every access log, every proxy's history and the browser's own, and a password in one is a
/// password on disk in four places nobody audits.
/// </remarks>
public sealed record AuthRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("password")] string Password);

/// <summary>What a successful <c>/auth</c> call returns.</summary>
/// <param name="Token">The bearer token to present on the WebSocket <c>HELLO</c>.</param>
/// <param name="Name">The normalised name, so a client can show what it actually logged in as.</param>
/// <param name="ExpiresInSeconds">How long the token is good for, so a client can plan a re-login rather than discover one.</param>
public sealed record AuthResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds);

/// <summary>Every refusal's body: one field, one sentence, fit to show a person.</summary>
/// <param name="Error">What went wrong, in the words a client may display.</param>
/// <remarks>
/// <b>A declared type and not an anonymous object, because the host builds with <c>CreateSlimBuilder</c>.</b> That configures
/// SOURCE-GENERATED JSON, which cannot serialise an anonymous type at all: <c>Results.Ok(new { error = … })</c> compiles and then
/// throws at the first refusal — the path least likely to be exercised before a deployment. Every shape these endpoints send or
/// receive is therefore named here and listed in <see cref="AuthJson"/>.
/// </remarks>
public sealed record AuthError([property: JsonPropertyName("error")] string Error);

/// <summary>
/// The source-generated serializer context for the <c>/auth</c> bodies.
/// </summary>
/// <remarks>
/// Registered by <see cref="AuthEndpoints.MapAuth"/> through <c>ConfigureHttpJsonOptions</c>. Without it the slim builder has no
/// resolver for these types and both endpoints fail at run time while compiling cleanly — which is exactly how the rest of this
/// host came to hand-write its JSON with a <c>Utf8JsonWriter</c>. These bodies are small and regular enough to be worth doing
/// properly instead.
/// </remarks>
[JsonSerializable(typeof(AuthRequest))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(AuthError))]
public sealed partial class AuthJson : JsonSerializerContext
{
}

/// <summary>
/// <c>/auth/signup</c> and <c>/auth/login</c> (A2).
/// </summary>
/// <remarks>
/// <para>
/// <b>These run on a Kestrel thread and touch the engine directly, with no hand-off to a system.</b> That is legal for the one
/// reason set out in <see cref="AccountStore"/>: <c>Account</c> and <c>AccountSecret</c> are Versioned on a non-spatial
/// archetype, the one combination <c>EW-02</c> lets a thread outside the DAG write. The PBKDF2 cost belongs here too — a few
/// hundred microseconds is a slow HTTP request and would be a visible frame on the tick.
/// </para>
/// <para>
/// <b>Every refusal is deliberately vague, and the status codes carry the distinction the body does not.</b> A wrong password
/// and an unknown name are both <c>401</c> with the same text, because telling a caller which it was is a name oracle. A taken
/// name IS specific — <c>409</c>, "that name is taken" — because a signup form cannot work otherwise, and the name's existence
/// is something a signup attempt reveals no matter what we say.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>The one message both login failures carry, so the two cannot drift apart and become distinguishable.</summary>
    private const string LoginRefusal = "that name and password do not match an account";

    /// <summary>
    /// Maps the two endpoints.
    /// </summary>
    /// <param name="app">The host's route builder.</param>
    /// <param name="dbe">The engine accounts live in.</param>
    /// <param name="limiter">The rate limiter both endpoints share.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    /// <remarks>
    /// The caller must also have registered <see cref="AuthJson"/> through <see cref="AddAuthJson"/> while the services were
    /// still being built, which is why that is a separate call: <c>ConfigureHttpJsonOptions</c> needs an
    /// <c>IServiceCollection</c>, and by the time there is an <c>IEndpointRouteBuilder</c> the container is built.
    /// </remarks>
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app, DatabaseEngine dbe, AuthRateLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(limiter);

        app.MapPost("/auth/signup", (AuthRequest request, HttpContext context) => Signup(dbe, limiter, request, context));
        app.MapPost("/auth/login", (AuthRequest request, HttpContext context) => Login(dbe, limiter, request, context));
        return app;
    }

    /// <summary>
    /// Registers <see cref="AuthJson"/> so the slim host can read and write the <c>/auth</c> bodies.
    /// </summary>
    /// <param name="services">The host's service collection, before it is built.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <b>Inserted at the FRONT of the resolver chain, and that is not arbitrary.</b> The chain is consulted in order and the
    /// first resolver that knows a type wins, so a context added behind whatever the slim builder installed could be shadowed.
    /// Being first also means a type this context does not list still falls through to the rest of the chain rather than failing.
    /// </remarks>
    public static IServiceCollection AddAuthJson(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AuthJson.Default));
        return services;
    }

    /// <summary>Creates an account and hands back a token, so a signup does not need a second round trip to log in.</summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="limiter">The shared limiter.</param>
    /// <param name="request">The posted body.</param>
    /// <param name="context">The request, for the client's address.</param>
    /// <returns>The HTTP result.</returns>
    private static IResult Signup(DatabaseEngine dbe, AuthRateLimiter limiter, AuthRequest request, HttpContext context)
    {
        if (request == null)
        {
            return Results.BadRequest(new AuthError("a name and a password are required"));
        }

        if (!limiter.TryTake(AddressKey(context), NameKey(request.Name)))
        {
            return TooMany();
        }

        switch (AccountStore.TrySignup(dbe, request.Name, request.Password, out var account))
        {
            case AccountStore.SignupOutcome.Created:
                return Results.Ok(Minted(account, request.Name));

            case AccountStore.SignupOutcome.NameTaken:
                // 409 and a plain sentence. This is the unique index's refusal, surfaced — see AccountStore.TrySignup for why it is not a pre-check.
                return Results.Conflict(new AuthError("that name is taken"));

            case AccountStore.SignupOutcome.BadName:
                return Results.BadRequest(new AuthError(AccountName.Rule));

            default:
                return Results.BadRequest(new AuthError(
                    $"a password is between {PasswordHash.MinimumPasswordLength} and {PasswordHash.MaximumPasswordLength} characters"));
        }
    }

    /// <summary>Verifies a credential and hands back a token.</summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="limiter">The shared limiter.</param>
    /// <param name="request">The posted body.</param>
    /// <param name="context">The request, for the client's address.</param>
    /// <returns>The HTTP result.</returns>
    private static IResult Login(DatabaseEngine dbe, AuthRateLimiter limiter, AuthRequest request, HttpContext context)
    {
        if (request == null)
        {
            return Results.BadRequest(new AuthError("a name and a password are required"));
        }

        if (!limiter.TryTake(AddressKey(context), NameKey(request.Name)))
        {
            return TooMany();
        }

        var outcome = AccountStore.TryLogin(dbe, request.Name, request.Password, out var account);
        if (outcome != AccountStore.LoginOutcome.Ok)
        {
            // One body and one status for WrongPassword, NoSuchAccount and BadName alike. The server knows which; the wire must not.
            return Results.Json(new AuthError(LoginRefusal), statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(Minted(account, request.Name));
    }

    /// <summary>Mints the token and packages it with the normalised name.</summary>
    /// <param name="account">The authenticated account.</param>
    /// <param name="offeredName">The name as typed, normalised again for the response.</param>
    /// <returns>The response body.</returns>
    private static AuthResponse Minted(EntityId account, string offeredName)
    {
        AccountName.TryNormalize(offeredName, out var name);
        return new AuthResponse(AuthToken.Mint(account), name, (int)AuthToken.DefaultLifetime.TotalSeconds);
    }

    /// <summary>The refusal a rate limit produces. <c>429</c>, with a hint and no detail about which bucket was empty.</summary>
    /// <returns>The HTTP result.</returns>
    private static IResult TooMany()
        => Results.Json(new AuthError("too many attempts — wait a moment and try again"), statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// The limiter key for the client's address.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>A key, or <see langword="null"/> when there is no address to key on.</returns>
    /// <remarks>
    /// <b><c>RemoteIpAddress</c> and deliberately NOT <c>X-Forwarded-For</c>.</b> A forwarded header is written by the client
    /// unless a proxy that strips and rewrites it is known to be in front, so keying on one lets any caller mint a fresh
    /// allowance per request by changing a header — the limit then protects nothing. Phase I's deployment terminates TLS at the
    /// instance, so the socket's address IS the client's; when a load balancer goes in front, this is the line that has to
    /// change, together with <c>ForwardedHeadersOptions.KnownProxies</c>.
    /// <para>
    /// <b>Keyed on the PREFIX rather than the exact address, because one client has more than one of them.</b> A single address was
    /// a bound on IPv4 and no bound at all on IPv6: the smallest allocation a residential line or a VPS gets is a routed /64, which
    /// is 2^64 source addresses that all belong to the same person and each of which produced a bucket of its own. Keying on /64
    /// makes the per-address limit mean "per client" again. IPv4 is keyed on /24 for the same reason at a coarser scale — a /24 is
    /// the smallest block that is routed as a unit — which does mean a shared NAT counts as one client; the per-NAME key is what
    /// keeps that from locking anybody out, since an attacker exhausting the address key cannot also exhaust a stranger's name.
    /// </para>
    /// </remarks>
    private static string AddressKey(HttpContext context)
    {
        var address = context?.Connection?.RemoteIpAddress;
        if (address == null)
        {
            return null;
        }

        // IPv4-mapped v6 ("::ffff:1.2.3.4") is the shape Kestrel hands back on a dual-stack socket, so it has to be unwrapped or
        // every v4 client would be keyed as a v6 /64 and the two paths would disagree about what an address is.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> octets = stackalloc byte[16];
        if (!address.TryWriteBytes(octets, out var written))
        {
            // Cannot happen for either family at 16 bytes, and if it ever did, keying on the whole address is the safe fallback.
            return "ip:" + address;
        }

        var kept = written == 4 ? 3 : 8;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ip/{kept}:{Convert.ToHexString(octets[..kept])}");
    }

    /// <summary>
    /// The limiter key for an account name.
    /// </summary>
    /// <param name="offered">The name as typed.</param>
    /// <returns>A key over the NORMALISED name, or <see langword="null"/> when the name cannot be normalised.</returns>
    /// <remarks>
    /// Keyed on the normalised form, or <c>Leia</c> and <c>leia</c> would be two buckets against one account and the per-name
    /// limit would be worth as many tries as there are ways to case a name.
    /// </remarks>
    private static string NameKey(string offered)
        => AccountName.TryNormalize(offered, out var name) ? "name:" + name : null;
}
