using System;
using System.Collections.Generic;

namespace SwgTatooine.Server;

/// <summary>
/// Places square footprints so that none of them overlaps another, inside a disc or at a point the caller drew.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every structure used to be dropped at an independent <c>PointInDisc</c> draw, so a city of 190
/// buildings put several of them inside each other — visible from the first second in the browser, and worse than ugly:
/// a building you can walk into because another one is standing in it is not a thing a player can read. Nothing else in
/// the world had an opinion about it, because nothing else looked.
/// </para>
    /// <para>
/// <b>Rejection sampling against a uniform grid, not a sweep.</b> A candidate is drawn exactly as before and tested only
/// against the structures in the cells its box touches, which is a handful whatever the city holds — so planning a city
/// is linear in its structures rather than quadratic. 17 000 structures are planned once at build time, so this is not a
/// hot path; it is written this way because the quadratic version is quadratic in the number a city is growing towards.
/// </para>
/// <para>
/// <b>ONE planner holds a whole planet, and it used to be one per site.</b> Per-site planners cannot see each other, so
/// nothing stopped a player-city knot from being planted across a city or a point of interest: 74 overlapping pairs
/// survived in a built world at the real planet size, every one of them a pair no single planner had ever compared. The
/// grid is what makes the planet-wide version free — two structures a kilometre apart are in different cells and are
/// never compared — so the original reasoning for splitting it ("testing across cities is work with no possible hit")
/// was describing a cost the grid does not charge.
/// </para>
/// <para>
/// <b>It gives up rather than looping, and says so.</b> A disc can be asked for more structures than fit — the caller
/// does not know the packing limit and should not have to — so after <see cref="Attempts"/> draws the planner accepts the
/// last candidate and counts it in <see cref="Crowded"/>. The alternative, searching until it finds a spot, hangs the
/// build on a map typo. A non-zero <see cref="Crowded"/> is the signal that a city's building count has outgrown its
/// radius, and the builder prints it.
/// </para>
/// <para>
/// <b>Deterministic, and the determinism is NOT the old one.</b> The same seed gives the same world, but it is a
/// different world from the one this replaced: rejected candidates consume draws, so every structure after the first
/// rejection lands somewhere else. Numbers measured against a seed before this change are not comparable with numbers
/// measured against it after.
/// </para>
/// </remarks>
public sealed class SitePlanner
{
    /// <summary>Candidate draws before a structure is planted where it last landed.</summary>
    /// <remarks>
    /// <b>Measured on the real map rather than chosen.</b> Six of the seven cities are at 12-16 % of their ground and
    /// place every structure first or second try at any cap. Anchorhead is the one that decides this number: 55
    /// buildings in a 125 m disc is 44.8 % packed, near where random sequential placement of squares saturates, and the
    /// acceptance rate per draw collapses as the last few go in. Over 64 seeds: 64 attempts gave up on 3 of them, 128
    /// on none, and so did 256, 512, 1024 and 4096. The whole sweep ran in about 5 ms for 64 cities, so the cap is set
    /// past the cliff rather than at it -- the cost of a draw that is never needed is nothing, and the cost of giving up
    /// is a building standing inside its neighbour.
    /// </remarks>
    public const int Attempts = 256;

    private readonly List<(float X, float Z, float Half)> _placed = [];
    private readonly Dictionary<long, List<int>> _cells = [];
    private readonly float _cellM;

    /// <summary>A planner for one site.</summary>
    /// <param name="largestHalfExtentM">The biggest half-extent it will be asked to place, which sets the grid's cell.</param>
    /// <remarks>
    /// The cell is sized so a box never spans more than two cells on an axis, which is what keeps the neighbour scan to a
    /// 3 x 3 block. A cell smaller than the largest box would mean walking an unbounded number of cells for it.
    /// </remarks>
    /// <param name="attempts">Candidate draws before a structure is planted anyway; <see cref="Attempts"/> when not given.</param>
    public SitePlanner(float largestHalfExtentM, int attempts = Attempts)
    {
        _cellM = Math.Max(1f, largestHalfExtentM * 2f);
        _attempts = Math.Max(1, attempts);
    }

    private readonly int _attempts;

    /// <summary>How many structures were planted without room, because the site is full.</summary>
    public int Crowded { get; private set; }

    /// <summary>How many structures this planner has placed.</summary>
    public int Count => _placed.Count;

    /// <summary>Draws a spot inside a disc that no structure already placed here overlaps.</summary>
    /// <param name="rng">The world's RNG; every attempt consumes a draw.</param>
    /// <param name="cx">Disc centre, east.</param>
    /// <param name="cz">Disc centre, north.</param>
    /// <param name="radius">Disc radius in metres.</param>
    /// <param name="halfM">Half the edge of the square to place.</param>
    /// <returns>Where it goes, and whether the site had room for it.</returns>
    public (float X, float Z, bool Fitted) Place(ref Rng rng, float cx, float cz, float radius, float halfM)
    {
        if (TryPlace(ref rng, cx, cz, radius, halfM, out var x, out var z))
        {
            return (x, z, true);
        }

        // Out of attempts: plant it where the last draw put it, which keeps the structure count the map asked for. A caller that would rather put the
        // structure somewhere else entirely uses TryPlace, which does not record a failure.
        Crowded++;
        Add(x, z, halfM);
        return (x, z, false);
    }

    /// <summary>
    /// Records a box at a spot the caller chose, counting a collision the way <see cref="Place"/> does.
    /// </summary>
    /// <param name="x">East.</param>
    /// <param name="z">North.</param>
    /// <param name="halfM">Half-edge.</param>
    /// <returns>Whether the spot was clear.</returns>
    /// <remarks>
    /// <b>For a structure whose position is decided rather than drawn</b> — the shuttleport, which stands at its city's
    /// centre. <see cref="TryPlaceAt"/> is the wrong tool there: it records nothing on a collision, so the structure
    /// would exist in the world and not in the planner, and every later building could be planted inside it with
    /// <see cref="Crowded"/> reporting nothing. This records either way and counts the overlap, which is exactly
    /// <see cref="Place"/>'s contract for a draw that ran out of attempts.
    /// </remarks>
    public bool PlaceAt(float x, float z, float halfM)
    {
        var clear = !Overlaps(x, z, halfM);
        if (!clear)
        {
            Crowded++;
        }

        Add(x, z, halfM);
        return clear;
    }

    /// <summary>Draws a spot inside a disc, and records it ONLY if one was found.</summary>
    /// <param name="rng">The world's RNG; every attempt consumes a draw.</param>
    /// <param name="cx">Disc centre, east.</param>
    /// <param name="cz">Disc centre, north.</param>
    /// <param name="radius">Disc radius in metres.</param>
    /// <param name="halfM">Half the edge of the square to place.</param>
    /// <param name="x">Where it goes, east — the last candidate drawn when the answer is false.</param>
    /// <param name="z">Where it goes, north.</param>
    /// <returns>Whether the disc had room.</returns>
    /// <remarks>
    /// For a caller with somewhere else to put the structure. The player-city knots use this: a knot that cannot fit one more house is full, and its next
    /// structure becomes a lone harvester out in the desert instead of a house standing inside its neighbour. That is a better answer than
    /// <see cref="Place"/>'s, and only the caller knows it exists.
    /// </remarks>
    public bool TryPlace(ref Rng rng, float cx, float cz, float radius, float halfM, out float x, out float z)
    {
        x = 0f;
        z = 0f;
        for (var attempt = 0; attempt < _attempts; attempt++)
        {
            (x, z) = rng.PointInDisc(cx, cz, radius);
            if (!Overlaps(x, z, halfM))
            {
                Add(x, z, halfM);
                return true;
            }
        }

        return false;
    }

    /// <summary>Records a spot the CALLER drew, if nothing already placed overlaps it.</summary>
    /// <param name="x">East.</param>
    /// <param name="z">North.</param>
    /// <param name="halfM">Half the edge of the square to place.</param>
    /// <returns>Whether it fitted and was recorded.</returns>
    /// <remarks>
    /// For a caller whose candidates cannot come from a disc. The lone player structures out in the desert are drawn by
    /// <c>WorldBuilder.RandomWildernessPoint</c>, which rejects points inside a city's keep-out rather than sampling a circle, so
    /// <see cref="Place"/> cannot produce them — and they still have to not stand inside each other.
    /// </remarks>
    public bool TryPlaceAt(float x, float z, float halfM)
    {
        if (Overlaps(x, z, halfM))
        {
            return false;
        }

        Add(x, z, halfM);
        return true;
    }

    /// <summary>Whether a square would fit here without overlapping anything planted, without planting it: for a caller choosing between candidates.</summary>
    /// <param name="x">Its centre, east.</param>
    /// <param name="z">Its centre, north.</param>
    /// <param name="halfM">Half its edge.</param>
    /// <returns>Whether it fits.</returns>
    public bool Fits(float x, float z, float halfM) => !Overlaps(x, z, halfM);

    /// <summary>Keeps a square clear of anything planted later, whatever already stands round it: a reservation, never counted as crowding.</summary>
    /// <param name="x">Its centre, east.</param>
    /// <param name="z">Its centre, north.</param>
    /// <param name="halfM">Half its edge.</param>
    /// <remarks>
    /// For ground the world needs empty rather than a structure — the corridor passengers walk out of a shuttleport's gate into. It may sit inside the
    /// margins of what stands beside it, which <see cref="TryPlaceAt"/> would refuse; it only has to keep everything after it out.
    /// </remarks>
    public void Reserve(float x, float z, float halfM) => Add(x, z, halfM);

    /// <summary>Whether a box at this spot would touch one already placed.</summary>
    /// <param name="x">East.</param>
    /// <param name="z">North.</param>
    /// <param name="halfM">Half-edge.</param>
    /// <returns>Whether it overlaps.</returns>
    /// <remarks>
    /// <b>The margin is counted ONCE for a pair, not once per box, and the difference decided whether the map fits.</b>
    /// Charging both boxes made the exclusion distance 2 x half + 2 x margin, which at Anchorhead is a 22 m square per
    /// building and 54 % of the disc -- the jamming limit, where the planner gave up on one building and left two
    /// overlapping pairs. One margin per pair is a 20 m square, 44 %, and the city plants with attempts to spare.
    /// Separating-axis on two axis-aligned squares is two comparisons.
    /// </remarks>
    private bool Overlaps(float x, float z, float halfM)
    {
        var reach = halfM;
        var minCx = CellOf(x - reach - _cellM);
        var maxCx = CellOf(x + reach + _cellM);
        var minCz = CellOf(z - reach - _cellM);
        var maxCz = CellOf(z + reach + _cellM);
        for (var cxi = minCx; cxi <= maxCx; cxi++)
        {
            for (var czi = minCz; czi <= maxCz; czi++)
            {
                if (!_cells.TryGetValue(Key(cxi, czi), out var bucket))
                {
                    continue;
                }

                for (var i = 0; i < bucket.Count; i++)
                {
                    var other = _placed[bucket[i]];
                    var span = reach + other.Half + StructureFootprint.MarginM;

                    if (Math.Abs(x - other.X) < span && Math.Abs(z - other.Z) < span)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Files a placed box in every cell its box touches.</summary>
    /// <param name="x">East.</param>
    /// <param name="z">North.</param>
    /// <param name="halfM">Half-edge.</param>
    private void Add(float x, float z, float halfM)
    {
        var index = _placed.Count;
        _placed.Add((x, z, halfM));
        var minCx = CellOf(x - halfM);
        var maxCx = CellOf(x + halfM);
        var minCz = CellOf(z - halfM);
        var maxCz = CellOf(z + halfM);
        for (var cxi = minCx; cxi <= maxCx; cxi++)
        {
            for (var czi = minCz; czi <= maxCz; czi++)
            {
                var key = Key(cxi, czi);
                if (!_cells.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    _cells[key] = bucket;
                }

                bucket.Add(index);
            }
        }
    }

    private int CellOf(float v) => (int)MathF.Floor(v / _cellM);

    private static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
}
