using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session handle for a loaded <c>.typhon-trace</c> file. Owns a <see cref="TraceSessionRuntime"/> that manages
/// the sidecar cache lifecycle + metadata projection. No engine is hosted — traces are self-contained recordings.
/// </summary>
public sealed class TraceSession : ISession, IDisposable
{
    public Guid Id { get; }
    public string FilePath { get; }
    public TraceSessionRuntime Runtime { get; }

    public SessionKind Kind => SessionKind.Trace;
    public SessionState State => SessionState.Trace;

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <see cref="TraceSessionRuntime.StaticSchemaProvider"/> populated during trace parse. Null until
    /// build completes; controller callers (which gate on session readiness) won't hit this in the null window.
    /// </remarks>
    public IStaticSchemaProvider StaticSchemaProvider => Runtime.StaticSchemaProvider;

    public TraceSession(Guid id, string filePath, TraceSessionRuntime runtime)
    {
        Id = id;
        FilePath = filePath;
        Runtime = runtime;
    }

    public void Dispose() => Runtime.Dispose();
}
