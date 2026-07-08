using Typhon.Workbench.Hosting;
using Typhon.Workbench.Security;

// Personal Access Token CLI — `--new-token`, `--revoke-token`, `--list-tokens`. Runs to completion
// and exits before the web host starts, so the user can mint/revoke without a running Workbench.
var tokenCliExitCode = TokenCli.TryHandle(args, Console.Out, Console.Error);
if (tokenCliExitCode is { } code)
{
    return code;
}

// Standalone entry point: run the Workbench host with default options (loopback bind; no browser — in dev the
// browser is opened against the Vite server, not Kestrel). The host bootstrap now lives in WorkbenchHost so the
// `typhon ui` CLI command can reuse it in-process (Feature #435 / #429, decision D-6). The bootstrap token,
// static SPA serving, and middleware pipeline all moved there.
return WorkbenchHost.Run(WorkbenchHostOptions.Default, args);

// Exposes the implicit Program class for WebApplicationFactory<Program> in tests.
public partial class Program { }
