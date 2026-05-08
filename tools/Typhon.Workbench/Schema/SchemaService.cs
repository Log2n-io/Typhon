using Typhon.Workbench.Dtos.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Resolves the right <see cref="IStaticSchemaProvider"/> for a session and dispatches the request through it.
/// Stateless — looks sessions up on demand via <see cref="SessionManager"/>.
/// </summary>
/// <remarks>
/// Behaviour matrix:
/// <list type="bullet">
/// <item>Session not found → <see cref="SessionNotFoundException"/> (controller maps to 404).</item>
/// <item>Session has no schema provider (live AttachSession today) → <see cref="SchemaUnavailableException"/> (controller maps to 404 with a clear "schema unavailable" message).</item>
/// <item>Provider exists but the requested component / archetype isn't there → <see cref="KeyNotFoundException"/> (controller maps to 404).</item>
/// </list>
/// The historical <c>SessionKindException</c> is gone — kind-specific behaviour is encoded in the provider, not in this service.
/// </remarks>
public sealed class SchemaService
{
    private readonly SessionManager _sessions;

    public SchemaService(SessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    public ComponentSummaryDto[] ListComponents(Guid sessionId) => RequireProvider(sessionId).ListComponents();
    public ComponentSchemaDto GetComponentSchema(Guid sessionId, string typeName) => RequireProvider(sessionId).GetComponentSchema(typeName);
    public ArchetypeInfoDto[] ListArchetypes(Guid sessionId) => RequireProvider(sessionId).ListArchetypes();
    public ArchetypeInfoDto[] GetArchetypesForComponent(Guid sessionId, string typeName) => RequireProvider(sessionId).GetArchetypesForComponent(typeName);
    public IndexInfoDto[] GetIndexesForComponent(Guid sessionId, string typeName) => RequireProvider(sessionId).GetIndexesForComponent(typeName);
    public SystemRelationshipsResponseDto GetSystemRelationships(Guid sessionId, string typeName) => RequireProvider(sessionId).GetSystemRelationships(typeName);

    private IStaticSchemaProvider RequireProvider(Guid sessionId)
    {
        if (!_sessions.TryGet(sessionId, out var session))
        {
            throw new SessionNotFoundException(sessionId);
        }
        var provider = session.StaticSchemaProvider;
        if (provider == null)
        {
            throw new SchemaUnavailableException(sessionId, session.Kind.ToString());
        }
        return provider;
    }
}

/// <summary>The requested session id is not registered with the <see cref="SessionManager"/>.</summary>
public sealed class SessionNotFoundException(Guid sessionId)
    : Exception($"Session {sessionId} not found.")
{
    public Guid SessionId { get; } = sessionId;
}

/// <summary>
/// The session exists but has no <see cref="IStaticSchemaProvider"/> (e.g., live AttachSession — schema isn't pushed
/// over the live socket yet). Controllers map this to 404 with a clear message so the UI can render a dedicated
/// "schema unavailable for this session type" empty state.
/// </summary>
public sealed class SchemaUnavailableException(Guid sessionId, string sessionKind)
    : Exception($"Session {sessionId} ({sessionKind}) has no schema data available.")
{
    public Guid SessionId { get; } = sessionId;
    public string SessionKind { get; } = sessionKind;
}
