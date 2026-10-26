using System;
using System.Globalization;
using System.Text;

namespace SwgTatooine.Server;

/// <summary>
/// What the world actually ended up containing. Reported alongside every measurement, because a fence cost is
/// meaningless without the population that produced it.
/// </summary>
public sealed class WorldCensus
{
    /// <summary>Buildings, crafting stations, bazaars and mission terminals, shuttleports and point-of-interest props.</summary>
    public int StaticObjects;

    /// <summary>Player houses, factories and harvesters. Static, but a few of them tick.</summary>
    public int PlayerStructures;

    /// <summary>Structures the site planner could not fit and planted overlapping something anyway: 0 on a map that fits.</summary>
    public int Crowded;

    /// <summary>Creature lairs, including those belonging to mission camps.</summary>
    public int Lairs;

    /// <summary>Creatures and hostile NPCs alive at the end of the run.</summary>
    public int Creatures;

    /// <summary>The same creatures broken down by <see cref="CreatureTemplates"/> entry — the world's composition, not just its size (S0-5).</summary>
    /// <remarks>
    /// A tick cost is meaningless without the population that produced it, and 2 M creatures mean something different when they are all womp rats than when a
    /// third of them are banthas: the templates differ in speed and in aggro radius, and so in how much spatial work each one causes. This is the first
    /// reader of <see cref="CreatureBrain.Template"/>, and it is what makes one sweep's world comparable with another's.
    /// </remarks>
    public int[] CreaturesByTemplate = new int[CreatureTemplates.Count];

    /// <summary>Lairs per band, I to V (content-levels.md): every band populated is what makes "every band a short ride from every city" true.</summary>
    public int[] LairsByBand = new int[5];

    /// <summary>Creatures per band, I to V.</summary>
    public int[] CreaturesByBand = new int[5];

    /// <summary>Lairs in the cities' fringes.</summary>
    public int FringeLairs;

    /// <summary>Lairs in the frontier patches.</summary>
    public int FrontierLairs;

    /// <summary>Frontier patches with no valid bearing (<see cref="WorldIndex.FrontiersDisplaced"/>): 0 on a map that fits.</summary>
    public int FrontiersDisplaced;

    /// <summary>City NPCs.</summary>
    public int CityNpcs;

    /// <summary>Simulated players.</summary>
    public int Players;

    /// <summary>AI starships in the space realm (Realms G1c).</summary>
    public int Starships;

    /// <summary>Two planets' censuses summed (Realms G1: the run reports the whole galaxy).</summary>
    public WorldCensus Plus(WorldCensus other) => new()
    {
        StaticObjects = StaticObjects + other.StaticObjects,
        PlayerStructures = PlayerStructures + other.PlayerStructures,
        Crowded = Crowded + other.Crowded,
        Lairs = Lairs + other.Lairs,
        Creatures = Creatures + other.Creatures,
        CityNpcs = CityNpcs + other.CityNpcs,
        Players = Players + other.Players,
        Starships = Starships + other.Starships,
        CreaturesByTemplate = Sum(CreaturesByTemplate, other.CreaturesByTemplate),
        LairsByBand = Sum(LairsByBand, other.LairsByBand),
        CreaturesByBand = Sum(CreaturesByBand, other.CreaturesByBand),
        FringeLairs = FringeLairs + other.FringeLairs,
        FrontierLairs = FrontierLairs + other.FrontierLairs,
        FrontiersDisplaced = FrontiersDisplaced + other.FrontiersDisplaced,
    };

    /// <summary>Counts one lair, its creatures, its band and its site's kind — the build's and the rebuild's one bookkeeping.</summary>
    /// <param name="template">Its kind.</param>
    /// <param name="level">Its level.</param>
    /// <param name="site">What planned its site; <see cref="LairSiteKind.Region"/> for a mission's lair, which has none.</param>
    public void CountLair(int template, int level, LairSiteKind site)
    {
        Lairs++;
        var creatures = CreatureTemplates.SpawnLimit[template];
        Creatures += creatures;
        CreaturesByTemplate[template] += creatures;
        var band = ContentLevels.Band(level) - 1;
        LairsByBand[band]++;
        CreaturesByBand[band] += creatures;
        FringeLairs += site == LairSiteKind.Fringe ? 1 : 0;
        FrontierLairs += site == LairSiteKind.Frontier ? 1 : 0;
    }

    /// <summary>The content levels' census line: lairs and creatures by band, the fringes and frontiers, and any patch the map squeezed.</summary>
    public string Levels()
        => FormattableString.Invariant($"content levels: lairs by band {LairsByBand[0]}/{LairsByBand[1]}/{LairsByBand[2]}/{LairsByBand[3]}/{LairsByBand[4]}")
            + FormattableString.Invariant(
                $", creatures by band {CreaturesByBand[0]}/{CreaturesByBand[1]}/{CreaturesByBand[2]}/{CreaturesByBand[3]}/{CreaturesByBand[4]}")
            + FormattableString.Invariant($"; {FringeLairs} fringe and {FrontierLairs} frontier lairs; frontiers displaced {FrontiersDisplaced}");

    /// <summary>Elementwise sum, for <see cref="Plus"/>.</summary>
    private static int[] Sum(int[] a, int[] b)
    {
        var r = new int[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            r[i] = a[i] + b[i];
        }

        return r;
    }

    /// <summary>Everything, which is the number the fence sees.</summary>
    public int Total => StaticObjects + PlayerStructures + Lairs + Creatures + CityNpcs + Players + Starships;

    /// <summary>Everything that can move — the only population the spatial fence does real work for.</summary>
    public int Mobile => Creatures + Players;

    public override string ToString() =>
        $"{Total:N0} entities: {StaticObjects:N0} static, {PlayerStructures:N0} structures, {Lairs:N0} lairs, "
        + $"{Creatures:N0} creatures, {CityNpcs:N0} NPCs, {Players:N0} players{(Starships > 0 ? $", {Starships:N0} starships" : "")}";

    /// <summary>The creature composition, for a measurement's header. Empty when nothing was spawned.</summary>
    public string Composition()
    {
        var sb = new StringBuilder();
        for (var t = 0; t < CreaturesByTemplate.Length; t++)
        {
            if (CreaturesByTemplate[t] == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(CultureInfo.InvariantCulture, $"{CreatureTemplates.Name(t)} {CreaturesByTemplate[t]:N0}");
        }

        return sb.ToString();
    }
}
