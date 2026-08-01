using System.Collections.Concurrent;
using System.Collections.Immutable;
using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

public sealed class OpenSession : ISession, IDisposable
{
    public Guid Id { get; }
    public string FilePath { get; }
    public EngineLifecycle Engine { get; }

    public SessionKind Kind => SessionKind.Open;
    public SessionState State { get; }

    // ── Profiles (#617, design D-10) ─────────────────────────────────────────────────────────────────────────────
    //
    // A capture attaches TO this session rather than being a peer session: the database is the persistent context, the
    // profile a transient lens. That asymmetry is what keeps one session id, one token and one customFetch — the
    // client's single-session choke point is never reached, so the deferred SessionContext rewrite is not paid.

    private readonly ConcurrentDictionary<Guid, TraceSessionRuntime> _profiles = new();

    /// <summary>
    /// Guards <see cref="_activeProfileId"/>. The dictionary is concurrent, but the active-id field is a
    /// <see cref="Nullable{Guid}"/> — 20 bytes, so reads and writes are not atomic and a concurrent attach could hand a reader a torn value. Attach and
    /// detach are user gestures and reads are once per request, so a plain lock costs nothing measurable and removes the question entirely.
    /// </summary>
    private readonly Lock _activeLock = new();
    private Guid? _activeProfileId;

    /// <summary>The two capability sets an Open session can have. Cached because <see cref="Capabilities"/> is read on every session projection.</summary>
    private static readonly ImmutableHashSet<string> DatabaseOnly = [SessionCapability.Database];
    private static readonly ImmutableHashSet<string> DatabaseAndProfiler = [SessionCapability.Database, SessionCapability.Profiler];

    /// <summary>
    /// Captures attached to this database, keyed by profile id.
    /// </summary>
    /// <remarks>
    /// Plural from day one even though the UI opens one at a time. It costs nothing now and it is what lets two captures of the same database be compared
    /// side by side later without peer sessions — the overwhelmingly common diff case, and the one thing the sub-resource model would otherwise have given up.
    /// </remarks>
    public IReadOnlyDictionary<Guid, TraceSessionRuntime> Profiles => _profiles;

    /// <inheritdoc />
    public Guid? ActiveProfileId
    {
        get { lock (_activeLock) { return _activeProfileId; } }
    }

    /// <summary>The runtime backing <see cref="ActiveProfileId"/>, or <c>null</c> when no profile is attached.</summary>
    public TraceSessionRuntime ActiveProfile
    {
        get
        {
            lock (_activeLock)
            {
                return _activeProfileId is { } id && _profiles.TryGetValue(id, out var runtime) ? runtime : null;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>The profiler capability is acquired and released as profiles come and go, which is exactly why it cannot be derived from the session kind.</remarks>
    public IReadOnlySet<string> Capabilities => _profiles.IsEmpty ? DatabaseOnly : DatabaseAndProfiler;

    /// <inheritdoc />
    /// <remarks>
    /// Delegated to the active profile, because a freshly attached capture is exactly as "still building" as a trace session is (#618). Without this the
    /// session inherits the interface default of <c>false</c> and the not-ready answers in the seconds after an attach come back as <b>409 Conflict</b> —
    /// permanent, a hard client error — instead of <b>202 Accepted</b>, which is what they are: the client polls and the request succeeds a moment later.
    /// The capability flips the instant a profile is attached, so this window is reached on every single attach.
    /// </remarks>
    public bool IsSchemaBuilding => ActiveProfile is { } active && !active.IsBuildComplete;

    /// <summary>Attaches a capture and makes it the active profile. Returns its new profile id.</summary>
    public Guid AttachProfile(TraceSessionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var id = Guid.NewGuid();
        _profiles[id] = runtime;
        lock (_activeLock)
        {
            _activeProfileId = id;
        }
        return id;
    }

    /// <summary>
    /// Detaches and disposes one profile. When it was the active one, focus falls back to any remaining profile rather than leaving the session advertising
    /// the profiler capability with nothing behind it.
    /// </summary>
    public bool DetachProfile(Guid profileId)
    {
        if (!_profiles.TryRemove(profileId, out var runtime))
        {
            return false;
        }

        try { runtime.Dispose(); }
        catch { /* a profile that failed to close must not take the session down with it */ }

        lock (_activeLock)
        {
            if (_activeProfileId == profileId)
            {
                _activeProfileId = _profiles.IsEmpty ? null : _profiles.Keys.First();
            }
        }
        return true;
    }

    /// <inheritdoc />
    public IStaticSchemaProvider StaticSchemaProvider => _staticSchemaProvider ??= new LiveSchemaProvider(Engine.Engine);
    private IStaticSchemaProvider _staticSchemaProvider;

    /// <summary>"convention" (adjacent *.schema.dll), "user-specified" (explicit paths), or "schemaless" (no DLLs).</summary>
    public string SchemaStatus { get; }
    public string[] SchemaDllPaths { get; }
    public int LoadedComponentTypes { get; }
    public SchemaCompatibility.Diagnostic[] SchemaDiagnostics { get; }

    public OpenSession(
        Guid id,
        string filePath,
        EngineLifecycle engine,
        SessionState state,
        string schemaStatus,
        string[] schemaDllPaths,
        int loadedComponentTypes,
        SchemaCompatibility.Diagnostic[] schemaDiagnostics)
    {
        Id = id;
        FilePath = filePath;
        Engine = engine;
        State = state;
        SchemaStatus = schemaStatus;
        SchemaDllPaths = schemaDllPaths;
        LoadedComponentTypes = loadedComponentTypes;
        SchemaDiagnostics = schemaDiagnostics;
    }

    public void Dispose()
    {
        // Profiles first: they hold file handles on captures inside the bundle the engine is about to release.
        foreach (var id in _profiles.Keys.ToArray())
        {
            DetachProfile(id);
        }
        Engine.Dispose();
    }
}
