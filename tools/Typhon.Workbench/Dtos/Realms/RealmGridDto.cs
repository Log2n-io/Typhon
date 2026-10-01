namespace Typhon.Workbench.Dtos.Realms;

/// <summary>
/// A realm's spatial identity: the fields that decide which cell a position files into, and so the fields a later open may not contradict (Realms D-1, RLM-01).
/// </summary>
/// <remarks>
/// <b><see cref="Deep"/> is derived, not stored.</b> The catalog carries six bounds and no dimensionality flag, so a realm is deep exactly when its Z extent is
/// non-zero. That is the same test the engine's own grid makes, and deriving it here beats adding a field the file does not have.
/// </remarks>
/// <param name="MinX">World bounds, minimum corner.</param>
/// <param name="MinY">See <paramref name="MinX"/>.</param>
/// <param name="MinZ">See <paramref name="MinX"/>. Equal to <paramref name="MaxZ"/> for a flat realm.</param>
/// <param name="MaxX">World bounds, maximum corner.</param>
/// <param name="MaxY">See <paramref name="MaxX"/>.</param>
/// <param name="MaxZ">See <paramref name="MaxX"/>.</param>
/// <param name="CellSize">Cell edge length, in world units.</param>
/// <param name="MigrationHysteresisRatio">The cell-crossing hysteresis band as a fraction of the cell (CC-02), part of identity because it decides when an entity changes cell.</param>
/// <param name="Deep">Whether the realm is three-dimensional, derived from a non-zero Z extent.</param>
public sealed record RealmGridDto(
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ,
    double CellSize,
    double MigrationHysteresisRatio,
    bool Deep);
