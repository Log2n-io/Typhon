namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// Wire shape for <c>GET /api/sessions/{id}/profiler/trace-status</c>. Reports whether the source
/// <c>.typhon-trace</c> file behind a Trace session has been overwritten on disk since the session's
/// sidecar cache was built — typically because a profiling re-run regenerated the file. The Workbench
/// polls this so the profiler header can offer the user an in-place reload.
/// </summary>
/// <param name="NewVersionAvailable">
/// True once the source file's SHA-256 fingerprint diverges from the version this session was built
/// from. Always false for <c>.typhon-replay</c> sessions — they are self-contained and have no source.
/// </param>
public record TraceStatusDto(bool NewVersionAvailable);
