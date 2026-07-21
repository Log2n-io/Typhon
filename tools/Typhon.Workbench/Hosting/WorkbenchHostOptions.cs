namespace Typhon.Workbench.Hosting;

/// <summary>
/// Options controlling how <see cref="WorkbenchHost"/> starts the Workbench web host. The same bootstrap serves
/// both the standalone <c>dotnet run</c> entry point (<see cref="Default"/>) and the <c>typhon ui</c> CLI command
/// (in-process hosting, decision D-6 of Feature #435).
/// </summary>
/// <remarks>
/// Distinct from <see cref="WorkbenchOptions"/>, which is the user-facing persisted settings record (editor,
/// profiler, schema). This type is purely host-startup wiring and is never persisted.
/// </remarks>
public sealed class WorkbenchHostOptions
{
    /// <summary>
    /// The loopback URL Kestrel binds to. Must stay a <c>127.0.0.1</c> loopback address — the Workbench is a
    /// single-user local tool and never binds a routable interface (see the threat model in
    /// <c>tools/Typhon.Workbench/CLAUDE.md</c>). Bound in code because the packaged tool has no
    /// <c>launchSettings.json</c> to supply the URL.
    /// </summary>
    public string Url { get; init; } = "http://127.0.0.1:5200";

    /// <summary>
    /// Optional database path to open in the initial Workbench session. When set it is handed to the SPA via the
    /// launch-URL fragment (never sent to the server) so the browser auto-opens it; null shows the welcome screen.
    /// </summary>
    public string DbPath { get; init; }

    /// <summary>
    /// Optional profiler-trace path to open in the initial Workbench session. Mirrors <see cref="DbPath"/>: when set
    /// it is handed to the SPA via the launch-URL fragment (never sent to the server) so the browser auto-opens it
    /// into the profiler (POST <c>/api/sessions/trace</c>); null leaves the profiler closed.
    /// </summary>
    public string TracePath { get; init; }

    /// <summary>
    /// Optional schema-assembly (.dll) path used to interpret <see cref="DbPath"/>. A <c>.typhon</c> database records
    /// the assembly that defined its schema; without that DLL the Workbench can only show engine internals (0
    /// archetypes). Handed to the SPA via the launch-URL fragment (like <see cref="DbPath"/>) so the browser passes it
    /// as an explicit schema DLL when auto-opening the database. Ignored when <see cref="DbPath"/> is null.
    /// </summary>
    public string SchemaPath { get; init; }

    /// <summary>
    /// When true the host opens the default browser at the tokenized launch URL once Kestrel is listening. Used by
    /// <c>typhon ui</c>; the standalone host leaves it false (in dev the browser is opened against the Vite server).
    /// </summary>
    public bool OpenBrowser { get; init; }

    /// <summary>The defaults for the standalone <c>dotnet run</c> entry point: loopback bind, no browser launch.</summary>
    public static WorkbenchHostOptions Default => new();
}
