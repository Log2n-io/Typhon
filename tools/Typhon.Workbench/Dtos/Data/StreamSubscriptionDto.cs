namespace Typhon.Workbench.Dtos.Data;

/// <summary>
/// Wire shape for <c>POST /api/sessions/{id}/subscribe</c> and
/// <c>POST /api/sessions/{id}/unsubscribe</c> requests on the unified data stream (#308 Phase C).
/// </summary>
/// <param name="StreamId">
/// The connection identifier emitted on the very first SSE frame as a <c>stream-id</c> event.
/// Required so multi-tab clients can target subscription state at a specific connection rather
/// than the session.
/// </param>
/// <param name="Events">
/// Event types to add to / remove from the subscription set. Unknown event types are accepted
/// silently — the server doesn't validate against a closed enum because the supported event types
/// evolve across API versions and clients negotiate via <c>X-Workbench-Api</c>.
/// </param>
public record StreamSubscriptionRequestDto(Guid StreamId, string[] Events);
