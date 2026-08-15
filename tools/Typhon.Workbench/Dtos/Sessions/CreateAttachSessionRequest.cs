namespace Typhon.Workbench.Dtos.Sessions;

/// <summary>Request body for <c>POST /api/sessions/attach</c>.</summary>
/// <param name="EndpointAddress">The engine's live profiler endpoint, e.g. <c>localhost:9100</c>.</param>
/// <param name="CherryPick">
/// On-demand tick capture (#805). <c>false</c> (the default) records everything for as long as the session lives —
/// the behaviour of every attach session before #805, preserved bit-for-bit for existing callers. <c>true</c> starts
/// the session idle: only the per-tick skeleton is retained until the operator arms a window with
/// <c>POST /api/sessions/{id}/profiler/capture</c>.
/// </param>
/// <remarks>
/// The choice is made per attach rather than remembered, because the two modes answer different questions and the
/// right answer is not sticky: "show me everything this run does" and "let me grab the 100 ticks around that spike"
/// are both legitimate on the same engine minutes apart.
/// </remarks>
public record CreateAttachSessionRequest(string EndpointAddress, bool CherryPick = false);
