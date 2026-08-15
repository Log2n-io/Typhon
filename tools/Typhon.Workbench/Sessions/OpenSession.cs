using System.Collections.Immutable;
using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

public sealed class OpenSession : ISession, ILiveProfilerHost, IDisposable
{
    public Guid Id { get; }
    public string FilePath { get; }

    // ── Watching the application that holds this database ────────────────────────────────────────────────────────
    //
    // The pairing this exists for: your application is running and holds the database, so this session opened PAUSED.
    // From there it can watch the application's live profiler over TCP, and when the application exits the coordinator
    // promotes the session to a real open — at which point the database AND the capture the engine wrote into its own
    // profilings/ are both available, already correlated, with no import step.
    //
    // Blocker B1 (a live engine holds its database exclusively) is what makes this a sequence rather than a
    // simultaneity: you cannot read the data while the application runs. You were never trying to. You were trying to
    // be positioned while it ran, and to read afterwards.

    private AttachSessionRuntime _liveRuntime;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="Volatile"/> for the same reason <see cref="Engine"/> is: written when watching starts or stops,
    /// read from request threads.
    /// </remarks>
    public AttachSessionRuntime LiveRuntime => Volatile.Read(ref _liveRuntime);

    /// <summary>True while this session is streaming from the application that holds its database.</summary>
    public bool IsWatchingLive => Volatile.Read(ref _liveRuntime) != null;

    /// <summary>
    /// Begin watching a live engine. Replaces any previous runtime, so a re-watch after a dropped connection is a
    /// plain call rather than a state machine the caller has to drive.
    /// </summary>
    public void StartWatching(AttachSessionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        // The recorded window is saved WITH the database, in its own profilings/ directory.
        //
        // This used to set SuppressAutoSave, on the reasoning that the engine writes a complete capture there anyway so
        // a second file would only duplicate it. That reasoning was built on a defect: enabling a live port had been
        // made to force a default file destination, so the "complete capture" being cited as already-durable was an
        // artifact the feature was never supposed to produce. With the engine writing nothing for a live-only run, the
        // window recorded here is the ONLY artifact — suppressing its save would silently discard the very ticks the
        // operator armed. Co-locating it with the database is also what makes it reachable: the Profiles list reads
        // that directory.
        runtime.SuppressAutoSave = false;
        runtime.AutoSaveDirectory = TraceLocation.ProfilingsDirectoryOf(FilePath);
        var previous = Interlocked.Exchange(ref _liveRuntime, runtime);
        previous?.Dispose();
    }

    /// <summary>Stop watching and dispose the runtime. Idempotent — returns false when nothing was being watched.</summary>
    public bool StopWatching()
    {
        var previous = Interlocked.Exchange(ref _liveRuntime, null);
        previous?.Dispose();
        return previous != null;
    }

    // ── Pause / resume (#621) ────────────────────────────────────────────────────────────────────────────────────
    //
    // Pause is a DISPOSE, not a lock mode: there is no way to hold a mapped, write-open file "softly", so releasing the
    // database means unmapping it and dropping db.lock. Resume is a fresh EngineLifecycle.OpenAsync.
    //
    // That the whole engine is rebuilt is not a compromise, it is the only correct option. ArchetypeRegistry
    // .UnregisterEngineUse (called from DatabaseEngine.Dispose) clears every registry entry whose Type came from a
    // COLLECTIBLE ALC — exactly the per-session schema DLLs — and the generated [ModuleInitializer] barrier that
    // repopulates them runs at most once per module per ALC. An ALC kept alive across the pause therefore could never
    // re-register what dispose removed. EngineLifecycleTests.PauseResumeCycles_WithSchemaLoaded_StayStable pins that
    // the full cycle is stable and drift-free.
    //
    // What survives the gap is this object: the session id, its token, the attached profiles and all client state. The
    // SPA never re-handshakes, which is what makes pause invisible to everything that isn't database-backed.

    private EngineLifecycle _engine;

    /// <summary>
    /// The live engine host, or <c>null</c> while this session is paused (#621).
    /// </summary>
    /// <remarks>
    /// <see cref="Volatile"/> because the pause coordinator writes it from a watcher thread while request threads read
    /// it — a publication edge, so it needs the release/acquire pair rather than a plain field access.
    /// </remarks>
    public EngineLifecycle Engine => Volatile.Read(ref _engine);

    /// <summary>True while the database has been released and only file-backed capabilities remain.</summary>
    public bool IsPaused => Volatile.Read(ref _engine) == null;

    /// <summary>Who holds the database while this session is paused; <c>null</c> when live, or when the holder could not be identified.</summary>
    public DatabaseHolder PausedBy { get; private set; }

    public SessionKind Kind => SessionKind.Open;
    public SessionState State { get; private set; }

    // ── Profiles (#617, design D-10) ─────────────────────────────────────────────────────────────────────────────
    //
    // A capture attaches TO this session rather than being a peer session: the database is the persistent context, the
    // profile a transient lens. That asymmetry is what keeps one session id, one token and one customFetch — the
    // client's single-session choke point is never reached, so the deferred SessionContext rewrite is not paid.

    private readonly ProfileHost _profileHost = new();

    /// <summary>Captures attached to this database, keyed by profile id.</summary>
    public IReadOnlyDictionary<Guid, TraceSessionRuntime> Profiles => _profileHost.Profiles;

    /// <inheritdoc />
    public Guid? ActiveProfileId => _profileHost.ActiveProfileId;

    /// <summary>The runtime backing <see cref="ActiveProfileId"/>, or <c>null</c> when no profile is attached.</summary>
    public TraceSessionRuntime ActiveProfile => _profileHost.ActiveProfile;

    /// <summary>Attaches a capture and makes it the active profile. Returns its new profile id.</summary>
    public Guid AttachProfile(TraceSessionRuntime runtime) => _profileHost.Attach(runtime);

    /// <summary>Detaches and disposes one profile, falling focus back to any remaining one.</summary>
    public bool DetachProfile(Guid profileId) => _profileHost.Detach(profileId);

    /// <summary>The capability sets an Open session can have. Cached because <see cref="Capabilities"/> is read on every session projection.</summary>
    private static readonly ImmutableHashSet<string> DatabaseOnly = [SessionCapability.Database];
    private static readonly ImmutableHashSet<string> DatabaseAndProfiler = [SessionCapability.Database, SessionCapability.Profiler];
    private static readonly ImmutableHashSet<string> ProfilerOnly = [SessionCapability.Profiler];
    private static readonly ImmutableHashSet<string> Nothing = [];

    /// <inheritdoc />
    /// <remarks>
    /// <para>The profiler capability is acquired and released as profiles come and go, which is exactly why it cannot be derived from the session kind.</para>
    /// <para>Since #621 the <i>database</i> capability is equally dynamic: a paused session has released the engine, so it keeps <c>profiler</c> and loses
    /// <c>database</c>. That asymmetry is the point of pausing rather than closing — captures are files on disk, not engine state, so a paused session stays
    /// fully useful as a profiler while the application owns the database. Reading a capture while your app runs against that database is the entire reason
    /// the Workbench belongs in a dev loop.</para>
    /// </remarks>
    public IReadOnlySet<string> Capabilities
    {
        get
        {
            var hasDatabase = Volatile.Read(ref _engine) != null;
            // Watching a live engine profiles just as much as holding an attached capture does — both serve
            // /profiler/*. Panels ask for the capability, never the kind, which is what lets a paused Open session
            // light up the profiler UI without pretending to be an Attach session.
            var hasProfiler = !_profileHost.IsEmpty || Volatile.Read(ref _liveRuntime) != null;
            return hasDatabase
                ? (hasProfiler ? DatabaseAndProfiler : DatabaseOnly)
                : (hasProfiler ? ProfilerOnly : Nothing);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegated to the active profile, because a freshly attached capture is exactly as "still building" as a trace session is (#618). Without this the
    /// session inherits the interface default of <c>false</c> and the not-ready answers in the seconds after an attach come back as <b>409 Conflict</b> —
    /// permanent, a hard client error — instead of <b>202 Accepted</b>, which is what they are: the client polls and the request succeeds a moment later.
    /// The capability flips the instant a profile is attached, so this window is reached on every single attach.
    /// </remarks>
    public bool IsSchemaBuilding => ActiveProfile is { } active && !active.IsBuildComplete;


    /// <inheritdoc />
    /// <remarks>
    /// While paused there is no engine to ask, so this falls back to the active profile's <c>TraceSchemaProvider</c> — the schema <i>as the capture recorded
    /// it</i>. That keeps the Schema Inspector populated across a pause instead of blanking, and it is honest: a capture's schema is a real fact about the
    /// recorded run. Callers must keep presenting it as trace-time rather than now-time (§5.7 — never present the two as the same instant). With no profile
    /// attached this is null, which controllers already map to the "schema unavailable" empty state.
    /// </remarks>
    public IStaticSchemaProvider StaticSchemaProvider
    {
        get
        {
            var engine = Volatile.Read(ref _engine);
            if (engine == null)
            {
                return ActiveProfile?.StaticSchemaProvider;
            }
            return _staticSchemaProvider ??= new LiveSchemaProvider(engine.Engine);
        }
    }

    /// <summary>
    /// Memoised <see cref="LiveSchemaProvider"/> over the live engine. <b>Must be cleared on pause</b> — it captures the <see cref="DatabaseEngine"/>
    /// reference, so a stale one would answer schema questions from a disposed engine after resume. This is the third session-keyed engine cache, alongside
    /// <c>DataBrowserService._snapshots</c>; <c>StorageMapService._mapCache</c> needs no such treatment because it is keyed on the engine itself and so
    /// self-invalidates when the engine is replaced.
    /// </summary>
    private IStaticSchemaProvider _staticSchemaProvider;

    /// <summary>"convention" (adjacent *.schema.dll), "user-specified" (explicit paths), or "schemaless" (no DLLs).</summary>
    public string SchemaStatus { get; private set; }
    public string[] SchemaDllPaths { get; }
    public int LoadedComponentTypes { get; private set; }
    public SchemaCompatibility.Diagnostic[] SchemaDiagnostics { get; private set; }

    /// <summary>
    /// Releases the database: drops the memoised schema provider, detaches the engine so every reader sees a paused session, then disposes it — which unmaps
    /// the MMF, flushes the WAL and deletes <c>db.lock</c>.
    /// </summary>
    /// <remarks>
    /// The field is cleared <i>before</i> the dispose so a concurrent reader observes "paused" rather than reaching a half-disposed engine. Engine-backed
    /// caches held elsewhere (<c>DataBrowserService</c>) are the caller's responsibility — they are DI services this type cannot reach, and serving a
    /// pre-pause snapshot after the application has written to the database is precisely the silently-wrong-data failure this feature must not introduce.
    /// </remarks>
    /// <param name="holder">Who took the database, for the banner; null when pausing on request rather than because someone claimed it.</param>
    public void Pause(DatabaseHolder holder)
    {
        var engine = Volatile.Read(ref _engine);
        _staticSchemaProvider = null;
        PausedBy = holder;
        Volatile.Write(ref _engine, null);

        if (engine != null)
        {
            SnapshotSchemaFacts(engine);
            try { engine.Dispose(); }
            catch { /* a failed unmap must not strand the session in a half-paused state */ }
        }
    }

    /// <summary>Re-attaches a freshly opened engine, restoring the database capability and the live schema provider.</summary>
    public void Resume(EngineLifecycle engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _staticSchemaProvider = null;
        SnapshotSchemaFacts(engine);
        PausedBy = null;
        Volatile.Write(ref _engine, engine);
    }

    /// <summary>
    /// Copies the engine's schema verdicts onto the session so they survive the paused window. Without this the banner would lose the compatibility state and
    /// loaded-type count the moment the engine went away, and a resumed session would keep reporting the <i>first</i> open's verdicts even if the schema
    /// changed underneath it.
    /// </summary>
    private void SnapshotSchemaFacts(EngineLifecycle engine)
    {
        State = engine.State switch
        {
            SchemaCompatibility.State.Ready => SessionState.Ready,
            SchemaCompatibility.State.MigrationRequired => SessionState.MigrationRequired,
            _ => SessionState.Incompatible,
        };
        SchemaStatus = engine.SchemaStatus;
        LoadedComponentTypes = engine.LoadedComponentTypes;
        SchemaDiagnostics = engine.Diagnostics ?? [];
    }

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
        _engine = engine;
        State = state;
        SchemaStatus = schemaStatus;
        SchemaDllPaths = schemaDllPaths;
        LoadedComponentTypes = loadedComponentTypes;
        SchemaDiagnostics = schemaDiagnostics;
    }

    /// <summary>
    /// Creates a session for a database that could not be opened because another process holds it — born <b>paused</b> rather than refused (#621).
    /// </summary>
    /// <remarks>
    /// <para>Without this, the cold-start order (application first, Workbench second) has no session at all, so there is nothing to pause and nothing to show:
    /// the exact case a developer hits when they reach for the profiler <i>while</i> their app is running. A paused session still resolves its bundle path, so
    /// it lists and attaches captures from <c>profilings/</c> with no engine involved, and the watcher promotes it to a real open the moment the lock drops.</para>
    /// <para>Only a <i>locked</i> database earns this. A corrupt, missing or schema-incompatible database still fails the open outright — waiting for a lock
    /// that is not the problem would turn a clear error into a session that never resumes and never says why.</para>
    /// </remarks>
    public OpenSession(Guid id, string filePath, DatabaseHolder holder, string[] schemaDllPaths)
    {
        Id = id;
        FilePath = filePath;
        _engine = null;
        PausedBy = holder;
        State = SessionState.Ready;
        SchemaStatus = "unknown";
        SchemaDllPaths = schemaDllPaths ?? [];
        LoadedComponentTypes = 0;
        SchemaDiagnostics = [];
    }

    public void Dispose()
    {
        // The live runtime first: it owns a socket and a temp file, and its auto-save must run while the builder is
        // still alive. Dispose deletes that temp file, so anything that wants the capture has to happen before it.
        StopWatching();
        // Profiles next: they hold file handles on captures inside the bundle the engine is about to release.
        _profileHost.DetachAll();
        // Null while paused — a paused session owns no engine, and disposing one is the whole of what pausing did.
        Volatile.Read(ref _engine)?.Dispose();
    }
}
