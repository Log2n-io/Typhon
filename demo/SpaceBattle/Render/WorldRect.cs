namespace SpaceBattle;

/// <summary>An axis-aligned rectangle in world space (metres).</summary>
internal readonly struct WorldRect
{
    public readonly float MinX;
    public readonly float MinY;
    public readonly float MaxX;
    public readonly float MaxY;

    public WorldRect(float minX, float minY, float maxX, float maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;

    /// <summary>
    /// Overlap test. Written as a rejection — four comparisons, any one of which proves disjointness — which is the
    /// same shape the engine's broadphase uses, and the reason a NaN bound reads as "no overlap" rather than as a
    /// match: every comparison against NaN is false, so the negation returns false and the box is skipped.
    /// </summary>
    public bool Overlaps(float minX, float minY, float maxX, float maxY) =>
        maxX >= MinX && minX <= MaxX && maxY >= MinY && minY <= MaxY;

    public bool Contains(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
}
