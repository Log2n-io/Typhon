namespace Typhon.Workbench.Security;

/// <summary>
/// CLI front-end for <see cref="PersonalAccessTokenStore"/>: parses <c>--new-token</c>,
/// <c>--revoke-token</c>, and <c>--list-tokens</c> from <c>args</c>, executes the action against
/// the on-disk store, and reports the result on stdout. Returns a non-null exit code when the
/// caller should terminate before starting the web host (i.e. the args specified a CLI action),
/// otherwise null (let the host start normally).
///
/// Lives in the security namespace because it directly touches the same on-disk artefacts as
/// <see cref="PersonalAccessTokenStore"/> and shares its name-validation rules.
/// </summary>
public static class TokenCli
{
    /// <summary>
    /// Returns the exit code if a CLI action was requested, or null if no recognized flag was
    /// found in <paramref name="args"/> (so <c>Program.cs</c> proceeds with the normal host run).
    /// </summary>
    public static int? TryHandle(string[] args, TextWriter output, TextWriter error)
    {
        if (args is null || args.Length == 0) return null;

        // A single store instance is enough — every action runs in process and exits immediately.
        // Tests can call the underlying methods directly; this entry point exists for the CLI flow.
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--new-token":
                    return MintAction(args, i, output, error);
                case "--revoke-token":
                    return RevokeAction(args, i, output, error);
                case "--list-tokens":
                    return ListAction(output);
            }
        }
        return null;
    }

    private static int MintAction(string[] args, int idx, TextWriter output, TextWriter error)
    {
        if (idx + 1 >= args.Length)
        {
            error.WriteLine("--new-token requires a name (e.g. --new-token claude-cli).");
            return 2;
        }
        var name = args[idx + 1];
        try
        {
            var store = new PersonalAccessTokenStore();
            var token = store.Mint(name);
            output.WriteLine($"Token '{name}' created.");
            output.WriteLine();
            output.WriteLine("Plaintext token (shown ONCE — copy it now):");
            output.WriteLine($"  {token}");
            output.WriteLine();
            output.WriteLine("Use it as: Authorization: Bearer <token>");
            output.WriteLine($"Stored hash file: {Path.Combine(store.Directory, name + ".token")}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RevokeAction(string[] args, int idx, TextWriter output, TextWriter error)
    {
        if (idx + 1 >= args.Length)
        {
            error.WriteLine("--revoke-token requires a name (e.g. --revoke-token claude-cli).");
            return 2;
        }
        var name = args[idx + 1];
        try
        {
            var store = new PersonalAccessTokenStore();
            var existed = store.Revoke(name);
            output.WriteLine(existed ? $"Token '{name}' revoked." : $"No token named '{name}'. Nothing to do.");
            return existed ? 0 : 1;
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int ListAction(TextWriter output)
    {
        var store = new PersonalAccessTokenStore();
        var tokens = store.List();
        if (tokens.Count == 0)
        {
            output.WriteLine("No personal access tokens. Mint one with: --new-token <name>");
            return 0;
        }
        output.WriteLine($"Personal access tokens (stored at {store.Directory}):");
        foreach (var t in tokens)
        {
            output.WriteLine($"  {t.Name,-40} created {t.CreatedAt:u}");
        }
        return 0;
    }
}
