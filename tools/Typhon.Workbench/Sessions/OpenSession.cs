using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

public sealed class OpenSession : ISession, IDisposable
{
    public Guid Id { get; }
    public string FilePath { get; }
    public EngineLifecycle Engine { get; }

    public SessionKind Kind => SessionKind.Open;
    public SessionState State { get; }

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

    public void Dispose() => Engine.Dispose();
}
