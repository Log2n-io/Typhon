using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Small builder helpers shared by the workload scenarios. Mirrors the inline struct-construction style of
/// tools/Typhon.Workbench.Fixtures/FixtureDatabase.cs (S64 / PlaceAabb) so scenarios stay readable while spawning the
/// multi-component SWG Full archetypes.
/// </summary>
internal static class SwgWorkload
{
    /// <summary>Spatial world extent — every Position AABB is placed within [0, WorldSize]². Must match the grid
    /// configured in <see cref="TyphonContext"/>.</summary>
    private const float WorldSize = 10_000f;

    /// <summary>Half-extent of each entity's placed AABB box.</summary>
    private const float EntityExtent = 5f;

    /// <summary>Build a <see cref="String64"/> from a managed string.</summary>
    public static String64 S64(string s)
    {
        String64 v = default;
        v.AsString = s;
        return v;
    }

    /// <summary>Place an entity's AABB as a small box at a random point inside [EntityExtent, WorldSize-EntityExtent]².</summary>
    public static AABB2F RandomBounds(Random rand)
    {
        var x = (float)rand.NextDouble() * (WorldSize - 2 * EntityExtent) + EntityExtent;
        var y = (float)rand.NextDouble() * (WorldSize - 2 * EntityExtent) + EntityExtent;
        return new AABB2F { MinX = x - EntityExtent, MinY = y - EntityExtent, MaxX = x + EntityExtent, MaxY = y + EntityExtent };
    }

    /// <summary>Shift an AABB box by (dx, dy), keeping its centre clamped within [EntityExtent, WorldSize-EntityExtent]².</summary>
    public static AABB2F Move(AABB2F b, float dx, float dy)
    {
        var cx = Math.Clamp((b.MinX + b.MaxX) * 0.5f + dx, EntityExtent, WorldSize - EntityExtent);
        var cy = Math.Clamp((b.MinY + b.MaxY) * 0.5f + dy, EntityExtent, WorldSize - EntityExtent);
        return new AABB2F { MinX = cx - EntityExtent, MinY = cy - EntityExtent, MaxX = cx + EntityExtent, MaxY = cy + EntityExtent };
    }
}
