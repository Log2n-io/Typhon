using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session handle for a live Typhon app attached over TCP. Owns an <see cref="AttachSessionRuntime"/> that manages
/// the socket + frame-read loop + SSE subscriber fan-out.
/// </summary>
public sealed class AttachSession : ISession, ILiveProfilerHost, IDisposable
{
    public Guid Id { get; }
    public string EndpointAddress { get; }
    public AttachSessionRuntime Runtime { get; }

    /// <inheritdoc />
    /// <remarks>An attach session exists to watch; its live runtime is never null for the session's lifetime.</remarks>
    public AttachSessionRuntime LiveRuntime => Runtime;

    public SessionKind Kind => SessionKind.Attach;
    public SessionState State => SessionState.Attached;

    // ISession.FilePath — DTO compat. For attach sessions the endpoint fills the "where from" slot in the UI.
    public string FilePath => EndpointAddress;

    /// <inheritdoc />
    /// <remarks>
    /// Since #WB-01 the engine writes its six v7 static-structure tables into the Init frame, so an attach session has real schema — the same
    /// <see cref="TraceSchemaProvider"/> a trace session uses, over tables that arrived down the socket instead of off a disk. It stays <c>null</c>
    /// until the first Init, and for an engine that sends the tables empty (an older build, or a schema too large for one frame), which surfaces the
    /// "schema unavailable for this session type" empty state rather than rendering as "schema present but empty".
    /// </remarks>
    public IStaticSchemaProvider StaticSchemaProvider => Runtime.StaticSchema;

    /// <summary>The two capability sets an attach session can have. Cached because <see cref="Capabilities"/> is read on every session projection.</summary>
    private static readonly System.Collections.Immutable.ImmutableHashSet<string> ProfilerOnly = [SessionCapability.Profiler];
    private static readonly System.Collections.Immutable.ImmutableHashSet<string> ProfilerAndSchema = [SessionCapability.Profiler, SessionCapability.Schema];

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// An attach session streams a capture live, so it profiles. It advertises no database capability: the engine it watches has one, but the Workbench
    /// reaches it over TCP and cannot browse it — see blocker B1, a running engine holds its database exclusively.
    /// </para>
    /// <para>
    /// The schema capability is acquired when the first Init frame arrives carrying static-structure tables (#WB-01), which is why this cannot be a
    /// fixed set: a session is projected to the client before its first frame, and an engine that sends the tables empty never acquires it at all.
    /// </para>
    /// </remarks>
    public IReadOnlySet<string> Capabilities => Runtime.StaticSchema != null ? ProfilerAndSchema : ProfilerOnly;

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
