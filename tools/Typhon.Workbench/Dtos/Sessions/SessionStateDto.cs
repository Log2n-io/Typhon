namespace Typhon.Workbench.Dtos.Sessions;

public record SessionStateDto(
    string Kind,               // "Open" | "Attach" | "Trace"
    string Lifecycle,          // "Loading" | "Ready" | "Closed"
    bool IsStreaming,
    bool IsPaused,
    bool IsReattaching,
    string SchemaCompatibility, // "Compatible" | "MigrationRequired" | "Incompatible"
    string Reason);            // null unless Closed
