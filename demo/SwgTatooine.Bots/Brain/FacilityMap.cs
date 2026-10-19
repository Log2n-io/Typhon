using System.Collections.Generic;

namespace SwgTatooine.Bots.Brain;

/// <summary>The facilities a bot has seen, remembered by the city they belong to.</summary>
/// <remarks>
/// <para>
/// <b>Learned by looking, which is what a new player does.</b> A bot ships the cities' coordinates — a client ships the
/// planet map — but not the facilities inside them: which building of a city is its shuttleport is a draw the world
/// generator made, so the only way to know is to be sent it. A bot walks into a city, sees a structure whose
/// <c>kind</c> says shuttleport and whose <c>region</c> says which city, and remembers it. The second visit is informed.
/// </para>
/// <para>
/// <b>The source is pluggable by design, and this is the version that needs nothing from the server.</b> A city's
/// facilities can also be delivered on entry, through the group set the server declares — which removes the first
/// visit's walk rather than the ability to manage without one. Both fill this table; the brain reads it and does not
/// care which filled it.
/// </para>
/// <para>
/// <b>Per bot, not shared.</b> One table per fleet would make every bot omniscient the moment any of them walked into a
/// city, which is a different load and a less honest one: the measurement this fleet exists for includes what a client
/// has to learn.
/// </para>
/// </remarks>
public sealed class FacilityMap
{
    private readonly Dictionary<long, (float X, float Z)> _seen = [];

    /// <summary>How many facilities this bot has learned.</summary>
    public int Count => _seen.Count;

    /// <summary>Remembers a facility, or confirms one.</summary>
    /// <param name="city">The city it belongs to.</param>
    /// <param name="kind">Its <c>StructureKind</c>.</param>
    /// <param name="x">Where it is, east.</param>
    /// <param name="z">Where it is, north.</param>
    /// <remarks>
    /// <b>The last sighting wins, and it does not matter.</b> These structures never move, so a second sighting carries
    /// the same coordinates as the first; overwriting is simply cheaper than testing. A city with two of a kind —
    /// several terminals, which the generator does make — is remembered as whichever was seen last, which is the
    /// correct answer to "where is a terminal in this city" and the wrong answer to "where are all of them". The brain
    /// only ever asks the first.
    /// </remarks>
    public void Observe(int city, int kind, float x, float z)
    {
        if (city < 0)
        {
            return;
        }

        _seen[Key(city, kind)] = (x, z);
    }

    /// <summary>Where a city's facility of a kind is, if this bot has seen one.</summary>
    /// <param name="city">The city.</param>
    /// <param name="kind">Its <c>StructureKind</c>.</param>
    /// <param name="at">Where it is.</param>
    /// <returns>Whether it is known.</returns>
    public bool TryFind(int city, int kind, out (float X, float Z) at) => _seen.TryGetValue(Key(city, kind), out at);

    private static long Key(int city, int kind) => ((long)city << 8) | (uint)(kind & 0xFF);
}
