namespace Typhon.Workbench.Sessions;

/// <summary>
/// How a session got its data. Two entry modes, deliberately (#621): open a database, or attach to a live engine.
/// </summary>
/// <remarks>
/// <c>Trace</c> was removed when the standalone trace session was deleted. A capture is no longer a session of its own —
/// it attaches to the database it was recorded against, which is what makes correlation structural rather than inferred.
/// Panels ask <see cref="SessionCapability"/> what a session can do; this enum only records where it came from.
/// </remarks>
public enum SessionKind
{
    Open,
    Attach,
}
