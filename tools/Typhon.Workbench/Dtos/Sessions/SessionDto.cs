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
    string Reason = null);

public record SessionDiagnosticDto(string ComponentName, string Kind, string Detail);
