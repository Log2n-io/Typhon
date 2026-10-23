using System.Globalization;
using System.Text;

namespace SwgTatooine;

/// <summary>
/// What the world actually ended up containing. Reported alongside every measurement, because a fence cost is
/// meaningless without the population that produced it.
/// </summary>
public sealed class WorldCensus
{
    /// <summary>Buildings, terminals, shuttleports and point-of-interest props.</summary>
    public int StaticObjects;

    /// <summary>Player houses, factories and harvesters. Static, but a few of them tick.</summary>
    public int PlayerStructures;

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
        Lairs = Lairs + other.Lairs,
        Creatures = Creatures + other.Creatures,
        CityNpcs = CityNpcs + other.CityNpcs,
        Players = Players + other.Players,
        Starships = Starships + other.Starships,
        CreaturesByTemplate = Sum(CreaturesByTemplate, other.CreaturesByTemplate),
    };

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
