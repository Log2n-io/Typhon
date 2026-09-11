using System;
using System.Collections.Generic;

namespace SwgTatooine;

/// <summary>A named settlement, with the extent it occupies and the population it carries.</summary>
public readonly struct CityDef
{
    public readonly string Name;

    /// <summary>Centre X in SWG world coordinates, before any world-size scaling.</summary>
    public readonly float X;

    /// <summary>Centre Z in SWG world coordinates.</summary>
    public readonly float Z;

    /// <summary>Radius of the built-up area in metres.</summary>
    public readonly float Radius;

    /// <summary>Buildings placed inside the radius.</summary>
    public readonly int Buildings;

    /// <summary>Ambient and functional NPCs standing in it.</summary>
    public readonly int Npcs;

    /// <summary>Relative share of the planet's players who consider this their hub. Normalised across the city list.</summary>
    public readonly float PlayerWeight;

    public CityDef(string name, float x, float z, float radius, int buildings, int npcs, float playerWeight)
    {
        Name = name;
        X = x;
        Z = z;
        Radius = radius;
        Buildings = buildings;
        Npcs = npcs;
        PlayerWeight = playerWeight;
    }
}

/// <summary>
/// A named non-city location: a landmark, a dungeon entrance, a static camp, a wreck. Smaller than a city, scattered
/// across the wilderness, and the thing players travel to.
/// </summary>
public readonly struct PoiDef
{
    public readonly string Name;
    public readonly float X;
    public readonly float Z;

    /// <summary>Radius of the site in metres.</summary>
    public readonly float Radius;

    /// <summary>Props placed at the site — rocks, tents, wreck pieces, tombs.</summary>
    public readonly int Props;

    /// <summary>Creature lairs anchored here. Zero for a purely decorative landmark.</summary>
    public readonly int Lairs;

    public PoiDef(string name, float x, float z, float radius, int props, int lairs)
    {
        Name = name;
        X = x;
        Z = z;
        Radius = radius;
        Props = props;
        Lairs = lairs;
    }
}

/// <summary>
/// A region of the wilderness that spawns creatures at a density. Between the cities and the points of interest, this is
/// what fills the map.
/// </summary>
public readonly struct SpawnRegionDef
{
    public readonly string Name;
    public readonly float X;
    public readonly float Z;
    public readonly float Radius;

    /// <summary>Lairs per square kilometre inside the region.</summary>
    public readonly float LairsPerSqKm;

    /// <summary>Which creature template the region's lairs spawn.</summary>
    public readonly int CreatureTemplate;

    public SpawnRegionDef(string name, float x, float z, float radius, float lairsPerSqKm, int creatureTemplate)
    {
        Name = name;
        X = x;
        Z = z;
        Radius = radius;
        LairsPerSqKm = lairsPerSqKm;
        CreatureTemplate = creatureTemplate;
    }
}

/// <summary>
/// Tatooine, reconstructed. Coordinates come from the game and are scaled to the configured world size at build time, so
/// the authentic numbers stay legible in the source and the inflation is applied in exactly one place.
/// </summary>
public sealed class TatooineMap
{
    private TatooineMap(SimConfig config)
    {
        Config = config;
        Cities = [];
        Pois = [];
        SpawnRegions = [];
    }

    public SimConfig Config { get; }

    public List<CityDef> Cities { get; private set; }

    public List<PoiDef> Pois { get; private set; }

    public List<SpawnRegionDef> SpawnRegions { get; private set; }

    /// <summary>
    /// Build the map for a configuration, scaling every authentic coordinate by <see cref="SimConfig.ContentScale"/>.
    /// </summary>
    public static TatooineMap Build(SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var map = new TatooineMap(config);
        var s = config.ContentScale;

        // Radii are scaled too, deliberately. The alternative — real-sized cities spread across a 128 km world — would
        // make the inflated worlds a study of empty space rather than of a bigger planet, and the swarm scenario in the
        // benchmark already covers empty space. Scaling everything keeps entities-per-cell comparable across the axis so
        // that a cell-size sweep means the same thing at every world size.
        foreach (var c in TatooineData.Cities)
        {
            map.Cities.Add(new CityDef(c.Name, c.X * s, c.Z * s, c.Radius * s, c.Buildings, c.Npcs, c.PlayerWeight));
        }

        foreach (var p in TatooineData.Pois)
        {
            map.Pois.Add(new PoiDef(p.Name, p.X * s, p.Z * s, p.Radius * s, p.Props, p.Lairs));
        }

        foreach (var r in TatooineData.SpawnRegions)
        {
            map.SpawnRegions.Add(new SpawnRegionDef(r.Name, r.X * s, r.Z * s, r.Radius * s, r.LairsPerSqKm / (s * s), r.CreatureTemplate));
        }

        return map;
    }
}
