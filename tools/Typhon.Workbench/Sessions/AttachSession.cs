using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session handle for a live Typhon app attached over TCP. Owns an <see cref="AttachSessionRuntime"/> that manages
/// the socket + frame-read loop + SSE subscriber fan-out.
/// </summary>
public sealed class AttachSession : ISession, IDisposable
{
    public Guid Id { get; }
    public string EndpointAddress { get; }
    public AttachSessionRuntime Runtime { get; }

    public SessionKind Kind => SessionKind.Attach;
    public SessionState State => SessionState.Attached;

    // ISession.FilePath — DTO compat. For attach sessions the endpoint fills the "where from" slot in the UI.
    public string FilePath => EndpointAddress;

    /// <inheritdoc />
    /// <remarks>
    /// Live attach doesn't currently push schema over the socket — TcpExporter's BuildInitPayload writes empty
    /// placeholder sections (count=0 for each v7 table). Returning null here surfaces the right "schema unavailable
    /// for this session type" empty state in the UI rather than rendering as "schema present but empty". Surfacing
    /// real schema for attach sessions is a follow-up — engine needs to publish the static-data tables on the wire.
    /// </remarks>
    public IStaticSchemaProvider StaticSchemaProvider => null;

    public AttachSession(Guid id, string endpointAddress, AttachSessionRuntime runtime)
    {
        Id = id;
        EndpointAddress = endpointAddress;
        Runtime = runtime;
    }

    public void Dispose() => Runtime.Dispose();
}
