namespace Typhon.Workbench.Dtos.Sessions;

public record SessionDto(
    Guid SessionId,
    string Kind,
    string State,              // kept — old consumers read this
    string FilePath,
    string[] SchemaDllPaths = null,
    string SchemaStatus = null,
    int LoadedComponentTypes = 0,
    SessionDiagnosticDto[] SchemaDiagnostics = null,
    // v1 lifecycle fields:
    string Lifecycle = null,               // "Loading" | "Ready" | "Closed"
    bool IsStreaming = false,
    bool IsPaused = false,
    bool IsReattaching = false,
    string SchemaCompatibility = null,     // "Compatible" | "MigrationRequired" | "Incompatible"
    string Reason = null,
    // #617 — what the session can DO, so the client stops inferring it from Kind. An Open session gains and loses
    // "profiler" as profiles are attached and detached, which no kind enum can express.
    string[] Capabilities = null,
    Guid? ActiveProfileId = null,
    // P5 — live attach as a capability of a paused Open session. The endpoint comes from the holder's `db.lock`, so a
    // non-null value means "the process holding this database is advertising a profiler port": exactly the condition
    // under which offering to watch it makes sense. Without this the client cannot tell a watchable pause from an
    // ordinary one, and `DatabaseHolder.IsWatchable` was server-only.
    string ProfilerEndpoint = null,
    bool IsWatchingLive = false);

public record SessionDiagnosticDto(string ComponentName, string Kind, string Detail);
