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

    /// <summary>
    /// Snapshot — true while the session's post-init static state is still being assembled (e.g., a
    /// <see cref="TraceSession"/> whose background cache build hasn't completed). Controllers use this to return
    /// <b>202 Accepted</b> from endpoints whose data depends on that state — schema (<see cref="SchemaService"/>),
    /// query catalog (<c>ProfilerController.CatalogNotReadyResponse</c>) — so the SPA hooks poll quietly during the
    /// build window rather than logging a hard error. Default false for sessions whose static state is established
    /// synchronously or whose endpoints don't gate on a build window at all.
    /// </summary>
    bool IsSchemaBuilding => false;
}
