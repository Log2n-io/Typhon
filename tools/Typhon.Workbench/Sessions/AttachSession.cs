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

    /// <inheritdoc />
    /// <remarks>
    /// An attach session streams a capture live, so it profiles. It advertises no database capability: the engine it watches has one, but the Workbench
    /// reaches it over TCP and cannot browse it — see blocker B1, a running engine holds its database exclusively.
    /// </remarks>
    public IReadOnlySet<string> Capabilities { get; } = System.Collections.Immutable.ImmutableHashSet.Create(SessionCapability.Profiler);

    // ── Profiles (#621) ──────────────────────────────────────────────────────────────────────────────────────────
    //
    // An attach session can hold attached captures for one reason: "capture from the running app, then analyse it"
    // (the capture-and-analyse command) saves a replay and must open it somewhere. With the standalone trace session
    // removed, a capture is always attached TO a session — normally its database, but a replay taken over TCP has no
    // database the Workbench can reach (B1), so it attaches to the live session it came from.

    private readonly ProfileHost _profileHost = new();

    /// <summary>Captures attached to this live session — replays saved from the stream.</summary>
    public IReadOnlyDictionary<Guid, TraceSessionRuntime> Profiles => _profileHost.Profiles;

    /// <inheritdoc />
    public Guid? ActiveProfileId => _profileHost.ActiveProfileId;

    /// <summary>The attached capture in focus, or <c>null</c> when the session is showing its live stream.</summary>
    public TraceSessionRuntime ActiveProfile => _profileHost.ActiveProfile;

    /// <summary>Attaches a saved replay and makes it the active profile.</summary>
    public Guid AttachProfile(TraceSessionRuntime runtime) => _profileHost.Attach(runtime);

    /// <summary>Detaches a replay; focus falls back to the live stream when none remain.</summary>
    public bool DetachProfile(Guid profileId) => _profileHost.Detach(profileId);

    /// <inheritdoc />
    /// <remarks>
    /// An attached replay is exactly as "still building" as any capture is while its sidecar cache is assembled. Without this the not-ready window after a
    /// capture-and-analyse comes back as 409 (permanent) instead of 202 (poll me).
    /// </remarks>
    public bool IsSchemaBuilding => ActiveProfile is { } active && !active.IsBuildComplete;

    public AttachSession(Guid id, string endpointAddress, AttachSessionRuntime runtime)
    {
        Id = id;
        EndpointAddress = endpointAddress;
        Runtime = runtime;
    }

    public void Dispose()
    {
        _profileHost.DetachAll();
        Runtime.Dispose();
    }
}
