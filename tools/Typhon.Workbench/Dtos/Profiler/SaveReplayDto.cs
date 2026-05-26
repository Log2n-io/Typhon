namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// Request body for <c>POST /api/sessions/{id}/profiler/save-replay</c>. The path may be relative or absolute; the server
/// normalises via <see cref="System.IO.Path.GetFullPath(string)"/> and validates that the parent directory exists before
/// touching the live runtime.
/// </summary>
/// <param name="Path">
/// Absolute or relative target path. Conventional extension is <c>.typhon-replay</c>. When empty (default), the server
/// picks a default under <c>%LOCALAPPDATA%/Typhon/Workbench/captures/</c> (or the XDG equivalent on POSIX) named
/// <c>typhon-capture-{ISO timestamp}.typhon-replay</c> — used by Stage 4 Phase 4 "Capture &amp; Analyse" (#377) to deliver
/// the one-gesture flow without prompting the user for a filename. The resolved path is echoed in the response.
/// </param>
public record SaveReplayRequest(string Path = "");

/// <summary>
/// Response body for a successful save. Echoes the resolved (absolute) target path and the resulting file size.
/// </summary>
public record SaveReplayResponse(string Path, long BytesWritten);
