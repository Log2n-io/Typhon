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

    /// <summary>City NPCs.</summary>
    public int CityNpcs;

    /// <summary>Simulated players.</summary>
    public int Players;

    /// <summary>Everything, which is the number the fence sees.</summary>
    public int Total => StaticObjects + PlayerStructures + Lairs + Creatures + CityNpcs + Players;

    /// <summary>Everything that can move — the only population the spatial fence does real work for.</summary>
    public int Mobile => Creatures + Players;

    public override string ToString() =>
        $"{Total:N0} entities: {StaticObjects:N0} static, {PlayerStructures:N0} structures, {Lairs:N0} lairs, "
        + $"{Creatures:N0} creatures, {CityNpcs:N0} NPCs, {Players:N0} players";
}
