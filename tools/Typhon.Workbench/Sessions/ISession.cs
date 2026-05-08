using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Sessions;

public interface ISession
{
    Guid Id { get; }
    SessionKind Kind { get; }
    SessionState State { get; }
    string FilePath { get; }

    /// <summary>
    /// Schema-data source for this session — null when schema is unavailable for this session kind (live attach today).
    /// Drives the Schema Inspector panels via <see cref="SchemaService"/>; controllers map null to a 404 so the UI
    /// shows a "schema unavailable" empty state without hard-failing the request.
    /// </summary>
    IStaticSchemaProvider StaticSchemaProvider { get; }
}
