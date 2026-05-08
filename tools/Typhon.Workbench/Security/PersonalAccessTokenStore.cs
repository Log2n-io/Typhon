using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typhon.Workbench.Security;

/// <summary>
/// File-backed Personal Access Tokens (PATs). Complements <see cref="BootstrapTokenGate"/> by
/// providing long-lived, user-named, revocable tokens for tooling that lives outside the SPA — CLI
/// scripts, debug shells, automated probes — anything that needs to query <c>/api/*</c> without
/// reading the per-process bootstrap token file (which rotates every restart).
///
/// **Threat model.** PATs do NOT weaken the loopback-only posture. The Workbench still binds to
/// 127.0.0.1, browser sandboxes still cannot read files from <c>%LOCALAPPDATA%</c>, so a malicious
/// webpage cannot exfiltrate a PAT and forge an <c>Authorization: Bearer</c> request. The only
/// new risk vs. bootstrap-only is that PATs persist across restarts; we mitigate by:
///   • Storing only SHA-256 hashes on disk (plaintext is shown ONCE at mint time, never recoverable).
///   • Using the same per-user <c>%LOCALAPPDATA%</c> trust boundary as the bootstrap token.
///   • Supporting explicit revoke (delete file → next process restart drops it from the cache).
///
/// **Single-user assumption.** This store inherits whatever ACL the OS gives <c>%LOCALAPPDATA%</c> —
/// no explicit chmod / DACL hardening. On Windows the user-profile directory is owned by the user
/// and inaccessible to other accounts by default; on a multi-user dev box where the Workbench is
/// shared, a different user with read access to <c>{user}/AppData/Local</c> could read token hashes
/// (still not plaintext, but enough for an offline brute-force on a weak token). This is consistent
/// with the bootstrap token's own posture; harden both in lockstep if the threat model changes.
///
/// **On-disk format.** One JSON file per token at <c>{tokenDir}/tokens/{name}.token</c>:
/// <code>
/// { "name": "claude-cli", "hash": "&lt;sha256-hex&gt;", "createdAt": "2026-05-07T10:30:00Z" }
/// </code>
/// Names are constrained to <c>[A-Za-z0-9_-]{1,64}</c> to map safely to filenames (no traversal,
/// no case-folding ambiguity on case-insensitive filesystems).
///
/// **Caching.** Hashes are loaded into memory at construction. <see cref="Mint"/> updates the cache
/// in-process so a freshly minted token validates immediately; revoke / external file changes only
/// take effect on next process restart, which matches the CLI mint/revoke workflow (those flags run
/// before the web host starts).
/// </summary>
public sealed class PersonalAccessTokenStore
{
    public const string AuthorizationScheme = "Bearer";

    /// <summary>Plaintext token length in hex chars (32 bytes → 64 hex chars).</summary>
    public const int TokenHexLength = 64;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _directory;

    // Cache of hash bytes keyed by token name. Hashes are small (32 bytes) and the count is tiny
    // (a handful of named tools per developer), so a flat dictionary is fine. Validation iterates
    // all entries with a constant-time compare per entry — O(n) where n is typically < 5.
    private readonly ConcurrentDictionary<string, byte[]> _hashesByName = new(StringComparer.Ordinal);

    public PersonalAccessTokenStore() : this(DefaultTokenDirectory())
    {
    }

    public PersonalAccessTokenStore(string parentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        _directory = Path.Combine(parentDirectory, "tokens");
        System.IO.Directory.CreateDirectory(_directory);
        LoadFromDisk();
    }

    public static string DefaultTokenDirectory() => BootstrapTokenGate.DefaultTokenDirectory();

    /// <summary>Absolute path of the directory where PAT files are stored (one JSON file per token).</summary>
    public string Directory => _directory;

    private void LoadFromDisk()
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.token"))
        {
            try
            {
                var json = File.ReadAllText(path);
                var record = JsonSerializer.Deserialize<TokenRecord>(json, JsonOpts);
                if (record is null || string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.Hash))
                {
                    continue;
                }
                if (!IsValidName(record.Name))
                {
                    continue;
                }
                var hashBytes = Convert.FromHexString(record.Hash);
                if (hashBytes.Length != 32)
                {
                    continue;
                }
                _hashesByName[record.Name] = hashBytes;
            }
            catch
            {
                // Best-effort: a malformed file should not stop the server starting. The token is
                // simply unusable until the file is fixed or removed.
            }
        }
    }

    /// <summary>
    /// Generates a new 256-bit token, persists its SHA-256 hash, and returns the plaintext token to
    /// the caller. The plaintext is NOT stored — losing it means revoke-and-mint-anew. Throws if a
    /// token with the same name already exists or the name is invalid.
    /// </summary>
    public string Mint(string name)
    {
        ValidateName(name);
        if (_hashesByName.ContainsKey(name))
        {
            throw new InvalidOperationException($"A token named '{name}' already exists. Revoke it first or pick another name.");
        }

        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(raw);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        var record = new TokenRecord
        {
            Name = name,
            Hash = Convert.ToHexString(hash),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var path = PathFor(name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, JsonOpts));
        File.Move(tmp, path, overwrite: true);

        _hashesByName[name] = hash;
        return token;
    }

    /// <summary>Deletes the named token. Returns false if the token did not exist.</summary>
    public bool Revoke(string name)
    {
        ValidateName(name);
        var path = PathFor(name);
        var existed = File.Exists(path);
        if (existed) File.Delete(path);
        _hashesByName.TryRemove(name, out _);
        return existed;
    }

    /// <summary>Returns metadata for every stored token (without the hash, which is irrelevant to callers).</summary>
    public IReadOnlyList<TokenInfo> List()
    {
        var results = new List<TokenInfo>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.token"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<TokenRecord>(File.ReadAllText(path), JsonOpts);
                if (record is null || string.IsNullOrWhiteSpace(record.Name)) continue;
                results.Add(new TokenInfo(record.Name, record.CreatedAt));
            }
            catch
            {
                // skip malformed files
            }
        }
        results.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return results;
    }

    /// <summary>
    /// Constant-time check of a presented plaintext token against every stored hash. We hash the
    /// presented token once and then compare to each cached hash with <see cref="CryptographicOperations.FixedTimeEquals"/>.
    /// Returns false for any structural problem (wrong length, non-hex chars).
    /// </summary>
    public bool Validate(string presented)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length != TokenHexLength)
        {
            return false;
        }
        // Reject anything that isn't pure hex without allocating — `Convert.FromHexString` would
        // throw, and the catch path is wasteful per request. Cheap inline check is enough.
        for (var i = 0; i < presented.Length; i++)
        {
            var c = presented[i];
            var isHex = c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!isHex) return false;
        }
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));

        foreach (var stored in _hashesByName.Values)
        {
            if (CryptographicOperations.FixedTimeEquals(stored, presentedHash))
            {
                return true;
            }
        }
        return false;
    }

    private string PathFor(string name) => Path.Combine(_directory, name + ".token");

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!IsValidName(name))
        {
            throw new ArgumentException(
                "Token name must be 1-64 chars of [A-Za-z0-9_-]. No spaces, no path separators.",
                nameof(name));
        }
    }

    private static bool IsValidName(string name)
    {
        if (name.Length is < 1 or > 64) return false;
        foreach (var c in name)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>On-disk record. Hash is hex-encoded SHA-256 of the plaintext token's UTF-8 bytes.</summary>
    private sealed class TokenRecord
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("hash")] public string Hash { get; set; }
        [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    }

    public readonly record struct TokenInfo(string Name, DateTimeOffset CreatedAt);
}
