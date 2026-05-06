namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// Wire shape carried by the <c>progress</c>, <c>done</c>, and <c>error</c> typed SSE events on the
/// build-progress stream (#308). The event type is the SSE <c>event:</c> line; the payload here
/// carries only the per-event data:
/// <list type="bullet">
///   <item><c>progress</c> — bytes / counts populated; <see cref="Message"/> null.</item>
///   <item><c>done</c> — empty payload (<c>{}</c>).</item>
///   <item><c>error</c> — <see cref="Message"/> non-null; counts null.</item>
/// </list>
/// </summary>
public record BuildProgressDto(
    long? BytesRead = null,
    long? TotalBytes = null,
    int? TickCount = null,
    long? EventCount = null,
    string Message = null);
