using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SwgTatooine.Dashboard;

/// <summary>
/// The accounts page's way to the game server's back office (<c>/admin/*</c>, accounts.md § 2): the operators' key, sent with every call, and the calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key is <c>SWG_ADMIN_KEY</c>, the server's own</b>, read from this process's environment rather than its command line, which any user of the box can
/// list. Without it, or shorter than the server accepts, the page says the back office is off and calls nothing.
/// </para>
/// <para>
/// <b>The page asks its operator for the same key</b> before it shows anything (<see cref="Matches"/>), once per page load (a Blazor circuit): this
/// dashboard binds the LAN,
/// and holding the key is what lets it write. The header's name and the length are the server's (<c>AdminEndpoints</c>), repeated here because this project
/// references no other.
/// </para>
/// </remarks>
public sealed class AdminLink
{
    /// <summary>The environment variable both processes read.</summary>
    public const string KeyVariable = "SWG_ADMIN_KEY";

    private const string KeyHeader = "X-Swg-Admin-Key";
    private const int MinKeyLength = 16;

    private readonly HttpClient _http;
    private readonly byte[] _key;
    private JsonNode _catalog;

    public AdminLink(Uri server, string key)
    {
        _key = string.IsNullOrEmpty(key) ? [] : Encoding.UTF8.GetBytes(key);
        Configured = key is { Length: >= MinKeyLength };
        _http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(20) };
        if (Configured)
        {
            _http.DefaultRequestHeaders.Add(KeyHeader, key);
        }
    }

    /// <summary>Whether this dashboard holds a key the server accepts the length of.</summary>
    public bool Configured { get; }

    /// <summary>Whether what an operator typed is the key, compared in constant time.</summary>
    public bool Matches(string typed)
        => Configured && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(typed ?? ""), _key);

    /// <summary>The accounts matching <paramref name="q"/> among <paramref name="who"/> (<c>all</c>, <c>people</c>, <c>bots</c>).</summary>
    public Task<JsonNode> Accounts(string q, string who, int limit)
        => Get($"admin/accounts?q={Uri.EscapeDataString(q ?? "")}&who={Uri.EscapeDataString(who ?? "all")}&limit={limit}");

    /// <summary>One character: its account, progress, credits and inventory.</summary>
    public Task<JsonNode> Character(string id) => Get($"admin/character/{Uri.EscapeDataString(id)}");

    /// <summary>The item catalogue with its defaults: read once, it is the server build's.</summary>
    public async Task<JsonNode> Catalog() => _catalog ??= await Get("admin/catalog").ConfigureAwait(false);

    /// <summary>
    /// A write: <c>credits</c>, <c>progress</c>, <c>items</c>, <c>items/remove</c> or <c>items/stats</c>. Answers whether it was applied, and what or why.
    /// </summary>
    public async Task<(bool Ok, string Message)> Post(string character, string what, JsonObject body)
    {
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"admin/character/{Uri.EscapeDataString(character)}/{what}", content).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (false, "the game server refused the key: is SWG_ADMIN_KEY the same on both?");
            }

            var answer = text.Length > 0 && text[0] == '{' ? JsonNode.Parse(text) : null;
            return ((bool?)answer?["ok"] ?? false, (string)answer?["message"] ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (false, "the game server did not answer: " + e.Message);
        }
    }

    /// <summary>The running (or last) horde with its live metrics (horde.md § 8), or null when there has been none.</summary>
    public Task<JsonNode> Horde() => Get("admin/horde");

    /// <summary>Starts a horde with its knobs (horde.md § 7), or ends the running one: whether it was applied, and what or why.</summary>
    public async Task<(bool Ok, string Message)> Horde(JsonObject knobs)
    {
        try
        {
            using var content = new StringContent((knobs ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(knobs == null ? "admin/horde/end" : "admin/horde", content).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (false, "the game server refused the key: is SWG_ADMIN_KEY the same on both?");
            }

            var answer = text.Length > 0 && text[0] == '{' ? JsonNode.Parse(text) : null;
            return ((bool?)answer?["ok"] ?? false, (string)answer?["message"] ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (false, "the game server did not answer: " + e.Message);
        }
    }

    private async Task<JsonNode> Get(string path)
    {
        using var response = await _http.GetAsync(path).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && path.StartsWith("admin/accounts", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the game server has no back office: start it with SWG_ADMIN_KEY set");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("the game server refused the key: is SWG_ADMIN_KEY the same on both?");
        }

        response.EnsureSuccessStatusCode();
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (System.Text.Json.JsonException e)
        {
            // Said as the page says every other failure, rather than thrown through a handler that does not catch it and ending the circuit (review).
            throw new InvalidOperationException("the game server answered something that is not JSON: " + e.Message, e);
        }
    }
}

/// <summary>One browser session's (one circuit's) unlock of the accounts page.</summary>
public sealed class AdminSession
{
    public bool Unlocked { get; set; }
}
