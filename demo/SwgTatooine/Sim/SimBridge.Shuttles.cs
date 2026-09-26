using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace SwgTatooine;

/// <summary>
/// Shuttle travel — #910's workload: a player in a city walks to that city's shuttleport, queues, and is transported to another city's port while the
/// shuttle is down.
/// </summary>
/// <remarks>
/// <para><b>Why it is here.</b> #910 proposes that a mass arrival — many entities landing in one cell on one tick — should land tight. Nothing in this
/// simulation arrived in groups before, so the proposal had no workload to be measured on.</para>
/// <para><b>Two boarding modes.</b> By default each queued passenger boards on its own hashed tick inside the landed window, as a travel ticket used while
/// the shuttle is down transported that one player at once: a destination then receives a trickle. <see cref="SimConfig.ShuttleBurst"/> boards a port's
/// whole queue on the landing tick instead — the group #910 is designed for. Every schedule number is an estimate; no SWG timetable was published.</para>
/// <para><b>The jump is an ordinary write.</b> The Shuttle system writes the destination through <c>WriteSpatial</c>, so the engine sees a
/// barrier-flagged cell crossing whose destination happens to be far away — which is what #910 says a teleport is.</para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>Radius around the destination port an arriving passenger appears in, metres.</summary>
    private const float ArrivalScatterM = 15f;

    /// <summary>Ticks after an arrival during which the probe reads a port as just arrived at.</summary>
    private const int ProbeWindowTicks = 20;

    /// <summary>A port with no arrival for this many ticks is the probe's steady-state control.</summary>
    private const int SteadyAfterTicks = 60;

    /// <summary>Radius queries the probe times per probed port per tick.</summary>
    private const int ProbeQueries = 32;

    private int[] _portArrivals;
    private long[] _lastArrivalTick;
    private long _shuttleBoardings;
    private long _probeCursor;
    private long _probeHits;
    private readonly List<int> _arrivalBursts = [];
    private readonly List<double> _probeSteadyNs = [];
    private readonly List<double>[] _probePostNs = NewBuckets(ProbeWindowTicks + 1);
    private readonly List<double> _probeSteadyHits = [];
    private readonly List<double>[] _probePostHits = NewBuckets(ProbeWindowTicks + 1);
    private readonly List<double> _tightSteady = [];
    private readonly List<double> _tightAt1 = [];
    private readonly List<double> _tightAt5 = [];
    private readonly List<double> _tightAt20 = [];
    private List<ProbeSample>[] _steadyByPort;
    private List<ProbeSample>[] _postByPort;

    /// <summary>One probe of one port: the queries timed cold and re-run warm, and the work the engine's traversal does for them — all per query.</summary>
    private readonly record struct ProbeSample(double Ns, double WarmNs, double Hits, double Cells, double Scanned, double Overlapping, double Tested,
        double Pages, bool First);

    private int ShuttleIntervalTicks => Math.Max(1, (int)(_config.ShuttleIntervalS * _config.TickRateHz));

    private int BoardingWindowTicks => Math.Clamp((int)(_config.BoardingWindowS * _config.TickRateHz), 1, ShuttleIntervalTicks);

    /// <summary>How long a queued passenger waits before giving up: two missed shuttles.</summary>
    private int ShuttleWaitTicks => 2 * (ShuttleIntervalTicks + BoardingWindowTicks);

    private bool ShuttlesActive => _config.Shuttles && _index.Cities.Count >= 2 && _index.Shuttleports.Count == _index.Cities.Count;

    private static List<double>[] NewBuckets(int count)
    {
        var buckets = new List<double>[count];
        for (var i = 0; i < count; i++)
        {
            buckets[i] = [];
        }

        return buckets;
    }

    /// <summary>Sized once, at construction: the Shuttle system's parallel workers must never race a lazy allocation.</summary>
    private void InitShuttles()
    {
        var ports = _index.Shuttleports.Count;
        _portArrivals = new int[ports];
        _lastArrivalTick = new long[ports];
        Array.Fill(_lastArrivalTick, long.MinValue / 2);
        _steadyByPort = new List<ProbeSample>[ports];
        _postByPort = new List<ProbeSample>[ports];
        for (var p = 0; p < ports; p++)
        {
            _steadyByPort[p] = [];
            _postByPort[p] = [];
        }
    }

    /// <summary>Tick offset of one port's schedule: landings are staggered evenly across the interval, so the ports do not land together.</summary>
    private long PortPhase(int port) => (long)port * ShuttleIntervalTicks / Math.Max(1, _index.Shuttleports.Count);

    /// <summary>
    /// A travel decision in a city takes the shuttle with probability <see cref="SimConfig.ShuttleShare"/>: point the player at its own city's port and
    /// remember where it is going. False leaves the decision to the ordinary travel branch, untouched.
    /// </summary>
    private bool TryTakeShuttle(ref PlayerState state, ref PlayerMotion move, float x, float z, ushort planet, uint shareSalt, uint destSalt)
    {
        if (!ShuttlesActive || Hash01(shareSalt) >= _config.ShuttleShare)
        {
            return false;
        }

        var from = CityAt(x, z);
        if (from < 0)
        {
            return false;   // out in the desert: there is no port to walk to, so it travels as it always did
        }

        var dest = PickCityIndex(destSalt);
        if (dest == from)
        {
            dest = (dest + 1) % _index.Cities.Count;
        }

        var (portX, portZ) = PortsOf(planet)[from];
        move.DestX = portX;
        move.DestZ = portZ;
        move.SpeedMps = TatooineData.PlayerRunSpeedMps;
        state.Activity = PlayerActivity.ToShuttle;
        state.ActivityTicks = 200 * _config.TickRateHz;
        state.ShuttleFrom = from;
        state.ShuttleDest = dest;
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
        return true;
    }

    /// <summary>A planet's shuttleports: the map's cities are every planet's, the ports' coordinates are each planet's own draw.</summary>
    private List<(float X, float Z)> PortsOf(ushort planet) => PlanetIndexes != null && planet < PlanetIndexes.Length
        ? PlanetIndexes[planet].Shuttleports
        : _index.Shuttleports;

    /// <summary>The city whose disc contains the point, or -1.</summary>
    private int CityAt(float x, float z)
    {
        for (var i = 0; i < _index.Cities.Count; i++)
        {
            var c = _index.Cities[i];
            var dx = x - c.X;
            var dz = z - c.Z;
            if ((dx * dx) + (dz * dz) <= c.Radius * c.Radius)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Board every queued passenger whose port's shuttle is down this tick — or, in burst mode, landing this tick — and transport it to its destination
    /// port.
    /// </summary>
    public void ShuttleTick(TickContext ctx)
    {
        if (!ShuttlesActive)
        {
            return;
        }

        var tick = ctx.TickNumber;
        var interval = ShuttleIntervalTicks;
        var window = BoardingWindowTicks;
        var half = _config.WorldEdgeM * 0.5f;
        var ports = _index.Shuttleports;
        long boardings = 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Player>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Player>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits0 = cluster.OccupancyBits;
            if (bits0 == 0)
            {
                continue;
            }

            // Read-only first: almost no cluster holds a queued passenger on a given tick, and taking the writable spans of every Player cluster to
            // find out would be work the fence pays for.
            var queued = 0UL;
            var peek = cluster.GetReadOnlySpan(Player.State);
            for (var bits = bits0; bits != 0; bits &= bits - 1)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                if (peek[idx].Activity == PlayerActivity.AwaitingShuttle)
                {
                    queued |= 1UL << idx;
                }
            }

            if (queued == 0)
            {
                continue;
            }

            var states = cluster.GetSpan(Player.State);
            var motions = cluster.GetSpan(Player.Move);
            var places = cluster.GetReadOnlySpan(Player.Bounds);
            var chunk = cluster.ChunkId;
            var planet = cluster.Realm.Value;
            var planetPorts = PortsOf(planet);
            var k = ctx.Realms.TicksPerVisit(cluster.Realm);   // Realms G2: a divided planet's cluster is seen once in k ticks
            while (queued != 0)
            {
                var idx = BitOperations.TrailingZeroCount(queued);
                queued &= queued - 1;

                ref var state = ref states[idx];
                var from = state.ShuttleFrom;
                var dest = state.ShuttleDest;
                if ((uint)from >= (uint)ports.Count || (uint)dest >= (uint)ports.Count)
                {
                    state.Activity = PlayerActivity.Idle;
                    state.ActivityTicks = 0;
                    TatooineReplication.Replicate(in cluster, idx);
                    continue;
                }

                var phase = (int)((tick + PortPhase(from)) % interval);
                if (phase >= window)
                {
                    continue;   // the shuttle is away
                }

                // Burst: the whole queue on the landing tick. Trickle: each passenger with probability 1 / (ticks left in the window), which is uniform
                // over what remains and certain on the last tick, so nobody queued before the window closes misses the shuttle.
                // At divisor k a visit stands for k ticks: the landing tick falls inside it when phase < k, and a trickle boarding is k times as likely
                // (review #4: `phase == 0` let a divided planet's burst passengers miss nearly every shuttle).
                var board = _config.ShuttleBurst
                    ? phase < k
                    : Hash01(Salt(tick, chunk, idx, 0x5A17EE21u)) * (window - phase) < k;
                if (!board)
                {
                    continue;
                }

                var h = places[idx].HalfExtent;
                var interPlanet = _config.Planets > 1 && planet < _config.Planets && Hash01(Salt(tick, chunk, idx, 0x0B4E1A37u)) < _config.InterPlanetShare;
                if (interPlanet)
                {
                    // Bound for another planet: a realm change, applied by TeleportSystem after this system.
                    BoardInterPlanet(cluster.GetEntityId(idx), planet, dest, h, Salt(tick, chunk, idx, 0x5D2A0C8Fu));
                }
                else
                {
                    var (portX, portZ) = planetPorts[dest];
                    var r = ArrivalScatterM * MathF.Sqrt(Hash01(Salt(tick, chunk, idx, 0x2F9B1D63u)));
                    var a = Hash01(Salt(tick, chunk, idx, 0x6C8E9CF5u)) * MathF.PI * 2f;
                    var nb = default(PlayerPlacement);
                    nb.SetAt(Math.Clamp(portX + (MathF.Cos(a) * r), -half + h, half - h), Math.Clamp(portZ + (MathF.Sin(a) * r), -half + h, half - h),
                        h);
                    cluster.WriteSpatial(Player.Bounds, idx, nb);
                }

                ref var move = ref motions[idx];
                move.VelX = 0f;
                move.VelZ = 0f;
                state.Activity = PlayerActivity.Idle;
                state.ActivityTicks = (20 * _config.TickRateHz) + (int)(Hash01(Salt(tick, chunk, idx, 0x1B56C4E9u)) * 100 * _config.TickRateHz);
                TatooineReplication.Replicate(in cluster, idx);
                boardings++;

                // The port probe reads planet 0's ports: only arrivals there count (review #4).
                if (planet == 0 && !interPlanet)
                {
                    Interlocked.Increment(ref _portArrivals[dest]);
                }
            }
        }

        if (boardings != 0)
        {
            Interlocked.Add(ref _shuttleBoardings, boardings);
        }
    }

    /// <summary>
    /// Report phase, every tick: fold this tick's arrivals per port, and with <see cref="SimConfig.Probe"/> time radius queries at the ports arrived at in
    /// the last <see cref="ProbeWindowTicks"/> ticks against one steady port, and read those ports' cells' Player-half tightness.
    /// </summary>
    /// <remarks>
    /// An arrival written in this tick's Spawn phase is migrated by this tick's fence, which runs after every system, so the probe sees it in its
    /// destination cell from the NEXT tick — which is why the offsets start at 1.
    /// </remarks>
    public void ShuttleProbeTick(TickContext ctx)
    {
        var ports = _index.Shuttleports;
        if (ports.Count == 0)
        {
            return;
        }

        var tick = ctx.TickNumber;
        for (var p = 0; p < ports.Count; p++)
        {
            var n = Interlocked.Exchange(ref _portArrivals[p], 0);
            if (n > 0)
            {
                _lastArrivalTick[p] = tick;
                _arrivalBursts.Add(n);
            }
        }

        if (!_config.Probe)
        {
            return;
        }

        var cs = Dbe._archetypeStates[Archetype<Player>.CatalogId]?.ClusterState;
        var steadyProbed = false;
        var probes = 0;
        for (var k = 0; k < ports.Count; k++)
        {
            var p = (int)((_probeCursor + k) % ports.Count);
            var since = tick - _lastArrivalTick[p];
            if (since >= 1 && since <= ProbeWindowTicks)
            {
                var sample = ProbePort(cs, ports[p], probes++ == 0);
                _probePostNs[(int)since].Add(sample.Ns);
                _probePostHits[(int)since].Add(sample.Hits);
                if (since <= 2)
                {
                    _postByPort[p].Add(sample);
                }

                var into = since switch
                {
                    1 => _tightAt1,
                    5 => _tightAt5,
                    ProbeWindowTicks => _tightAt20,
                    _ => null,
                };
                if (into != null)
                {
                    RecordPortTightness(cs, ports[p], into);
                }
            }
            else if (!steadyProbed && since > SteadyAfterTicks)
            {
                steadyProbed = true;
                var sample = ProbePort(cs, ports[p], probes++ == 0);
                _probeSteadyNs.Add(sample.Ns);
                _probeSteadyHits.Add(sample.Hits);
                _steadyByPort[p].Add(sample);
                RecordPortTightness(cs, ports[p], _tightSteady);
            }
        }

        _probeCursor++;
    }

    /// <summary>The <paramref name="q"/>-th probe centre: a ring 5-29 m from the port, where arrivals stand. Engine X/Y is world X/Z.</summary>
    private static (float X, float Y) ProbeCentre((float X, float Z) port, int q)
    {
        var angle = q * (MathF.PI * 2f / ProbeQueries);
        var r = 5f + ((q % 4) * 8f);
        return (port.X + (MathF.Cos(angle) * r), port.Z + (MathF.Sin(angle) * r));
    }

    /// <param name="cs">The Player cluster state.</param>
    /// <param name="port">The port probed.</param>
    /// <param name="first">Whether this is the tick's first probe — the one that runs on whatever the phases before Report left in the caches.</param>
    private ProbeSample ProbePort(ArchetypeClusterState cs, (float X, float Z) port, bool first)
    {
        var (ns, warmNs, hits) = TimePortQueries(port);
        var (cells, scanned, overlapping, tested, pages) = CountPortWork(cs, port);
        return new ProbeSample(ns, warmNs, hits, cells, scanned, overlapping, tested, pages, first);
    }

    /// <summary>
    /// Mean nanoseconds and mean hits of one Player radius query over the probe ring, then the same queries again: the second pass runs on lines the first
    /// just touched, so a first pass slower only after an arrival and a second pass that is not says the cost was cold cache, not work. Caller holds an
    /// epoch.
    /// </summary>
    private (double Ns, double WarmNs, double Hits) TimePortQueries((float X, float Z) port)
    {
        long hits = 0;
        var start = Stopwatch.GetTimestamp();
        for (var q = 0; q < ProbeQueries; q++)
        {
            var (x, y) = ProbeCentre(port, q);
            var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = AwarenessRadius };
            hits += CountInRadius<Player>(in sphere, RealmId.Default);   // the probe watches planet 0's ports
        }

        var cold = Stopwatch.GetTimestamp();
        for (var q = 0; q < ProbeQueries; q++)
        {
            var (x, y) = ProbeCentre(port, q);
            var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = AwarenessRadius };
            CountInRadius<Player>(in sphere, RealmId.Default);
        }

        var warm = Stopwatch.GetTimestamp();
        _probeHits += hits;
        var perQuery = 1e9 / Stopwatch.Frequency / ProbeQueries;
        return ((cold - start) * perQuery, (warm - cold) * perQuery, hits / (double)ProbeQueries);
    }

    /// <summary>
    /// The work the engine's radius query does for the same centres, counted rather than timed, per query: cell halves opened, clusters the broadphase
    /// scans in them, clusters whose bound overlaps the query box, and entities the narrowphase then tests. Mirrors <c>AabbClusterEnumerator</c>'s linear
    /// path — no cell is promoted to a tree at the default threshold. Runs inside a system body, so RT-01 supplies the epoch scope.
    /// </summary>
    private unsafe (double Cells, double Scanned, double Overlapping, double Tested, double Pages) CountPortWork(ArchetypeClusterState cs,
        (float X, float Z) port)
    {
        var perCell = cs?.PerCellIndex;
        if (perCell == null)
        {
            return default;
        }

        var grid = Dbe.SpatialGrid;
        long cells = 0;
        long scanned = 0;
        long overlapping = 0;
        long tested = 0;
        long distinctPages = 0;
        // Pages the query's clusters live on, deduplicated per query: every query builds a fresh ChunkAccessor, so each distinct page is one slow-path load.
        Span<int> pages = stackalloc int[64];
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var q = 0; q < ProbeQueries; q++)
            {
                var pageCount = 0;
                var (cx, cy) = ProbeCentre(port, q);
                double minX = cx - AwarenessRadius, minY = cy - AwarenessRadius, maxX = cx + AwarenessRadius, maxY = cy + AwarenessRadius;
                grid.WorldToCellRange(minX, minY, 0d, maxX, maxY, 0d, out var x0, out var y0, out _, out var x1, out var y1, out _);
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        if (!grid.TryGetCellKey(x, y, grid.FlatPlaneZ, out var cellKey) || cellKey >= perCell.Length || perCell[cellKey] == null)
                        {
                            continue;
                        }

                        grid.CellOrigin(cellKey, out var ox, out var oy, out _);
                        var qMinX = ClusterSpatialAabb.ToCellRelativeMin(minX, ox);
                        var qMinY = ClusterSpatialAabb.ToCellRelativeMin(minY, oy);
                        var qMaxX = ClusterSpatialAabb.ToCellRelativeMax(maxX, ox);
                        var qMaxY = ClusterSpatialAabb.ToCellRelativeMax(maxY, oy);
                        for (var half = 0; half < 2; half++)
                        {
                            var index = perCell[cellKey].ReadIndex(half == 1);
                            if (index == null || index.ClusterCount == 0)
                            {
                                continue;
                            }

                            cells++;
                            scanned += index.ClusterCount;
                            for (var i = 0; i < index.ClusterCount; i++)
                            {
                                if (index.MaxX[i] < qMinX || index.MinX[i] > qMaxX || index.MaxY[i] < qMinY || index.MinY[i] > qMaxY)
                                {
                                    continue;
                                }

                                overlapping++;
                                var chunkId = index.ClusterIds[i];
                                tested += BitOperations.PopCount(*(ulong*)accessor.GetChunkAddress(chunkId));
                                var page = cs.ClusterSegment.GetChunkLocation(chunkId).segmentIndex;
                                if (pageCount < pages.Length && pages[..pageCount].IndexOf(page) < 0)
                                {
                                    pages[pageCount++] = page;
                                }
                            }
                        }
                    }
                }

                distinctPages += pageCount;
            }
        }
        finally
        {
            accessor.Dispose();
        }

        const double n = ProbeQueries;
        return (cells / n, scanned / n, overlapping / n, tested / n, distinctPages / n);
    }

    private void RecordPortTightness(ArchetypeClusterState cs, (float X, float Z) port, List<double> into)
    {
        if (cs == null)
        {
            return;
        }

        var cellKey = Dbe.SpatialGrid.WorldToCellKey(port.X, port.Z, 0d);
        var (clusters, toBound, _, _) = SpatialCensus.CellTightness(Dbe, cs, cellKey);
        if (clusters > 0)
        {
            into.Add(toBound);
        }
    }

    /// <summary>What the shuttles did and, with the probe, what the arrivals cost the ports' queries.</summary>
    public void PrintShuttleReport()
    {
        if (!ShuttlesActive)
        {
            return;
        }

        long arrivals = 0;
        var largest = 0;
        var atLeast16 = 0;
        var atLeast64 = 0;
        foreach (var n in _arrivalBursts)
        {
            arrivals += n;
            largest = Math.Max(largest, n);
            atLeast16 += n >= 16 ? 1 : 0;
            atLeast64 += n >= 64 ? 1 : 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  shuttles ({(_config.ShuttleBurst ? "burst" : "trickle")}, every {_config.ShuttleIntervalS:G} s, {_config.BoardingWindowS:G} s window, "
            + $"share {_config.ShuttleShare:G}): {arrivals:N0} arrivals in {_arrivalBursts.Count:N0} port-ticks; largest {largest} in one port-tick, "
            + $">= 16 in {atLeast16}, >= 64 in {atLeast64}");
        if (!_config.Probe)
        {
            return;
        }

        Console.WriteLine($"  probe, ns per Player radius query at a port: steady {Median(_probeSteadyNs),7:F0} (n={_probeSteadyNs.Count}) | after an arrival "
            + $"+1..2 {MedianOf(_probePostNs, 1, 2),7:F0} | +3..5 {MedianOf(_probePostNs, 3, 5),7:F0} | +6..20 {MedianOf(_probePostNs, 6, ProbeWindowTicks),7:F0}");
        Console.WriteLine($"  probe, Player hits per query:               steady {Median(_probeSteadyHits),7:F1}          | after an arrival "
            + $"+1..2 {MedianOf(_probePostHits, 1, 2),7:F1} | +3..5 {MedianOf(_probePostHits, 3, 5),7:F1} | +6..20 "
            + $"{MedianOf(_probePostHits, 6, ProbeWindowTicks),7:F1}");
        Console.WriteLine($"  port-cell Player extent/bound: steady {Median(_tightSteady):F2} (n={_tightSteady.Count}) | +1 {Median(_tightAt1):F2} "
            + $"(n={_tightAt1.Count}) | +5 {Median(_tightAt5):F2} | +20 {Median(_tightAt20):F2}");

        var steady = _steadyByPort.SelectMany(static l => l).ToList();
        var post = _postByPort.SelectMany(static l => l).ToList();
        if (steady.Count == 0 || post.Count == 0)
        {
            return;
        }

        Console.WriteLine($"  probe work per query, steady | +1..2: cell halves {Med(steady, static s => s.Cells):F2} | {Med(post, static s => s.Cells):F2}, "
            + $"clusters scanned {Med(steady, static s => s.Scanned):F1} | {Med(post, static s => s.Scanned):F1}, overlapping "
            + $"{Med(steady, static s => s.Overlapping):F1} | {Med(post, static s => s.Overlapping):F1}, entities tested "
            + $"{Med(steady, static s => s.Tested):F0} | {Med(post, static s => s.Tested):F0}, distinct pages {Med(steady, static s => s.Pages):F1} | "
            + $"{Med(post, static s => s.Pages):F1}");
        Console.WriteLine($"  probe ns per entity tested, steady | +1..2: cold {Med(steady, static s => s.Ns / Math.Max(1d, s.Tested)):F2} | "
            + $"{Med(post, static s => s.Ns / Math.Max(1d, s.Tested)):F2}, warm re-run {Med(steady, static s => s.WarmNs / Math.Max(1d, s.Tested)):F2} | "
            + $"{Med(post, static s => s.WarmNs / Math.Max(1d, s.Tested)):F2}");
        Console.WriteLine($"  probe ns per entity tested by position in the tick, first | later: steady {MedWhere(steady, true)} | "
            + $"{MedWhere(steady, false)}, +1..2 {MedWhere(post, true)} | {MedWhere(post, false)}");
        PrintSamePortRatios();
    }

    private static string MedWhere(List<ProbeSample> samples, bool first)
    {
        var picked = samples.Where(s => s.First == first).Select(static s => s.Ns / Math.Max(1d, s.Tested)).ToList();
        return picked.Count == 0 ? "-" : $"{Median(picked):F2} (n={picked.Count})";
    }

    /// <summary>
    /// The confound the aggregate cannot remove: the steady control is a different port from the one just arrived at. Each ratio is one port's +1..2
    /// median over the same port's steady median; the line reports the median of those ratios across the ports that have both.
    /// </summary>
    private void PrintSamePortRatios()
    {
        Func<ProbeSample, double>[] selectors =
        [
            static s => s.Ns, static s => s.WarmNs, static s => s.Hits, static s => s.Scanned, static s => s.Overlapping, static s => s.Tested,
            static s => s.Ns / Math.Max(1d, s.Tested), static s => s.Pages,
        ];
        var ratios = new List<double>[selectors.Length];
        for (var k = 0; k < selectors.Length; k++)
        {
            ratios[k] = [];
        }

        for (var p = 0; p < _steadyByPort.Length; p++)
        {
            if (_steadyByPort[p].Count == 0 || _postByPort[p].Count == 0)
            {
                continue;
            }

            for (var k = 0; k < selectors.Length; k++)
            {
                ratios[k].Add(Med(_postByPort[p], selectors[k]) / Med(_steadyByPort[p], selectors[k]));
            }
        }

        Console.WriteLine($"  same port, +1..2 over steady (median of {ratios[0].Count} ports): ns {Median(ratios[0]):F2}, warm ns {Median(ratios[1]):F2}, "
            + $"hits {Median(ratios[2]):F2}, scanned {Median(ratios[3]):F2}, overlapping {Median(ratios[4]):F2}, tested {Median(ratios[5]):F2}, "
            + $"ns/tested {Median(ratios[6]):F2}, pages {Median(ratios[7]):F2}");
    }

    private static double Med(List<ProbeSample> samples, Func<ProbeSample, double> select) => Median(samples.Select(select).ToList());

    private static double MedianOf(List<double>[] buckets, int firstOffset, int lastOffset)
    {
        var all = new List<double>();
        for (var d = firstOffset; d <= lastOffset && d < buckets.Length; d++)
        {
            all.AddRange(buckets[d]);
        }

        return Median(all);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var copy = values.ToArray();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }
}
