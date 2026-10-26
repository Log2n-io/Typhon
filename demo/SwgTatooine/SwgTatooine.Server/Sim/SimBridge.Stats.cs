using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Counters sampled once a second, so a rate is read off the server rather than computed by whoever polls it: the last sample against the one
/// <c>window</c> seconds older.
/// </summary>
internal sealed class RateWindow
{
    private readonly double[] _at;
    private readonly long[][] _values;
    private int _count;
    private int _next;

    public RateWindow(int series, int seconds)
    {
        _at = new double[seconds + 1];
        _values = new long[seconds + 1][];
        for (var i = 0; i < _values.Length; i++)
        {
            _values[i] = new long[series];
        }
    }

    /// <summary>Records the counters as they stand at <paramref name="seconds"/>.</summary>
    public void Sample(double seconds, ReadOnlySpan<long> values)
    {
        lock (_values)
        {
            _at[_next] = seconds;
            values.CopyTo(_values[_next]);
            _next = (_next + 1) % _at.Length;
            _count = Math.Min(_count + 1, _at.Length);
        }
    }

    /// <summary>Each series' rate per second over the last <paramref name="window"/> seconds, or over what has been sampled when that is less.</summary>
    public void Rates(int window, Span<double> into)
    {
        lock (_values)
        {
            into.Clear();
            if (_count < 2)
            {
                return;
            }

            var newest = (_next - 1 + _at.Length) % _at.Length;
            var back = Math.Min(window, _count - 1);
            var oldest = (newest - back + _at.Length) % _at.Length;
            var dt = _at[newest] - _at[oldest];
            if (dt <= 0d)
            {
                return;
            }

            for (var i = 0; i < into.Length; i++)
            {
                into[i] = (_values[newest][i] - _values[oldest][i]) / dt;
            }
        }
    }
}

/// <summary>
/// Who drives the players, counted once a second: the scripted crowd, the bots, the people — and where the bots are; the people's and the bots' combat levels
/// by band of ten (1-10 … 41-50), and the sum of each group's levels.
/// </summary>
internal sealed record PopulationCensus(
    int Crowd,
    int Bots,
    int People,
    int BotsIndoors,
    int[] BotsByCity,
    int[] PeopleByBand,
    int[] BotsByBand,
    long PeopleLevels,
    long BotLevels)
{
    /// <summary>Bands of ten levels, 1-10 to 41-50.</summary>
    public const int Bands = 5;

    public static PopulationCensus None { get; } = new(0, 0, 0, 0, [], new int[Bands], new int[Bands], 0, 0);

    /// <summary>The band of a combat level: 0 for 1-10, 4 for 41-50.</summary>
    public static int BandOf(int level) => Math.Clamp((level - 1) / 10, 0, Bands - 1);
}

/// <summary>
/// The demo's own numbers, served at <c>GET /swg/stats</c> (Loïc, 2026-10-26): the economy with its rates, the missions, who is playing and where, and
/// the crowd's lair objectives.
/// </summary>
/// <remarks>
/// Sampled once a second in the lair director's pass, which already keeps that clock for the lair census: the counters into a <see cref="RateWindow"/>,
/// and the players by who drives them. Nothing here runs per tick.
/// </remarks>
public sealed partial class SimBridge
{
    // The series of the rate window, in this order.
    private const int SeriesTransfers = 0;
    private const int SeriesTips = 1;
    private const int SeriesRewards = 2;
    private const int SeriesGrants = 3;
    private const int SeriesTipsRefused = 4;
    private const int SeriesMissionsTaken = 5;
    private const int SeriesMissionsCompleted = 6;
    private const int SeriesMissionsCancelled = 7;
    private const int SeriesLairHits = 8;
    private const int SeriesXpKills = 9;
    private const int SeriesXpMissions = 10;
    private const int SeriesLevelUps = 11;
    private const int SeriesCount = 12;

    /// <summary>The cities as a person sees them named, index for index with the server's map (world-data.ts: Port Sable is Mos Eisley, …).</summary>
    private static readonly string[] CityDisplayNames = ["Port Sable", "Whitestone", "Dry Wells", "Red Mesa", "Tern's Rest", "Tallow", "Far Reach"];

    private readonly RateWindow _rates = new(SeriesCount, 60);
    private readonly long _statsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    private long _lastStatsTick = long.MinValue / 2;
    private PopulationCensus _population = PopulationCensus.None;

    // Cumulative, unlike the sampler's interval counters (SimStats), which reset at each sample.
    private long _missionsTakenTotal;
    private long _missionsCompletedTotal;
    private long _missionsCancelledTotal;

    /// <summary>Samples the counters and counts the players by who drives them; once a second whatever the caller's cadence.</summary>
    private void SampleStats(Transaction tx, long tick)
    {
        if (tick - _lastStatsTick < Math.Max(1, _config.TickRateHz))
        {
            return;
        }

        _lastStatsTick = tick;
        Span<long> values = stackalloc long[SeriesCount];
        values[SeriesTransfers] = Transfers;
        values[SeriesTips] = Tips;
        values[SeriesRewards] = RewardsPaid;
        values[SeriesGrants] = WalletsOpened;
        values[SeriesTipsRefused] = TipsRefused;
        values[SeriesMissionsTaken] = Interlocked.Read(ref _missionsTakenTotal);
        values[SeriesMissionsCompleted] = Interlocked.Read(ref _missionsCompletedTotal);
        values[SeriesMissionsCancelled] = Interlocked.Read(ref _missionsCancelledTotal);
        values[SeriesLairHits] = LairHitsTotal;
        values[SeriesXpKills] = XpGrantedKills;
        values[SeriesXpMissions] = XpGrantedMissions;
        values[SeriesLevelUps] = LevelUps;
        _rates.Sample(System.Diagnostics.Stopwatch.GetElapsedTime(_statsStarted).TotalSeconds, values);

        Volatile.Write(ref _population, CountPopulation(tx));
    }

    private PopulationCensus CountPopulation(Transaction tx)
    {
        var cities = _index.Cities;
        var byCity = new int[cities.Count];
        var peopleByBand = new int[PopulationCensus.Bands];
        var botsByBand = new int[PopulationCensus.Bands];
        long peopleLevels = 0, botLevels = 0;
        int crowd = 0, bots = 0, people = 0, indoors = 0;
        foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
        {
            var controls = cluster.GetReadOnlySpan(Player.Control);
            var looks = cluster.GetReadOnlySpan(Player.Look);
            var places = cluster.GetReadOnlySpan(Player.Bounds);
            var realm = cluster.Realm.Value;
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                if (controls[slot].Kind == ControllerKind.Scripted)
                {
                    crowd++;
                    continue;
                }

                var level = Math.Max(1, (int)looks[slot].Level);
                if (looks[slot].Bot == 0)
                {
                    people++;
                    peopleByBand[PopulationCensus.BandOf(level)]++;
                    peopleLevels += level;
                    continue;
                }

                bots++;
                botsByBand[PopulationCensus.BandOf(level)]++;
                botLevels += level;
                if (realm >= _config.Planets)
                {
                    indoors++;
                    continue;
                }

                if (realm != 0)
                {
                    continue;
                }

                var p = places[slot];
                for (var c = 0; c < cities.Count; c++)
                {
                    var dx = p.X - cities[c].X;
                    var dz = p.Z - cities[c].Z;
                    if ((dx * dx) + (dz * dz) <= cities[c].Radius * cities[c].Radius)
                    {
                        byCity[c]++;
                        break;
                    }
                }
            }
        }

        return new PopulationCensus(crowd, bots, people, indoors, byCity, peopleByBand, botsByBand, peopleLevels, botLevels);
    }

    /// <summary><c>GET /swg/stats</c>: the economy and its rates, the missions, who is playing and where, the crowd's lair objectives.</summary>
    public string DemoStatsJson()
    {
        Span<double> r10 = stackalloc double[SeriesCount];
        Span<double> r60 = stackalloc double[SeriesCount];
        _rates.Rates(10, r10);
        _rates.Rates(60, r60);
        var pop = Volatile.Read(ref _population);
        var j = new StringBuilder(1024);
        var inv = CultureInfo.InvariantCulture;

        j.Append("{\"economy\":{");
        Num(j, "transfers", Transfers).Append(',');
        Num(j, "walletsOpened", WalletsOpened).Append(',');
        Num(j, "tips", Tips).Append(',');
        Num(j, "tipsRefused", TipsRefused).Append(',');
        Num(j, "rewardsPaid", RewardsPaid).Append(',');
        Num(j, "commitsFailed", EconomyCommitsFailed).Append(',');
        Rate(j, "transfersPerSec", r10[SeriesTransfers], r60[SeriesTransfers], 1d).Append(',');
        Rate(j, "tipsPerSec", r10[SeriesTips], r60[SeriesTips], 1d).Append(',');
        Rate(j, "rewardsPerMin", r10[SeriesRewards], r60[SeriesRewards], 60d).Append(',');
        Rate(j, "walletsOpenedPerMin", r10[SeriesGrants], r60[SeriesGrants], 60d).Append(',');
        Rate(j, "tipsRefusedPerMin", r10[SeriesTipsRefused], r60[SeriesTipsRefused], 60d);
        j.Append("},\"missions\":{");
        Num(j, "held", MissionsHeld).Append(',');
        Num(j, "offers", MissionOffersStanding).Append(',');
        Num(j, "taken", Interlocked.Read(ref _missionsTakenTotal)).Append(',');
        Num(j, "completed", Interlocked.Read(ref _missionsCompletedTotal)).Append(',');
        Num(j, "cancelled", Interlocked.Read(ref _missionsCancelledTotal)).Append(',');
        Num(j, "lairsRedrawn", MissionLairsRedrawn).Append(',');
        Rate(j, "takenPerMin", r10[SeriesMissionsTaken], r60[SeriesMissionsTaken], 60d).Append(',');
        Rate(j, "completedPerMin", r10[SeriesMissionsCompleted], r60[SeriesMissionsCompleted], 60d).Append(',');
        Rate(j, "cancelledPerMin", r10[SeriesMissionsCancelled], r60[SeriesMissionsCancelled], 60d);
        j.Append("},\"population\":{");
        Num(j, "crowd", pop.Crowd).Append(',');
        Num(j, "bots", pop.Bots).Append(',');
        Num(j, "people", pop.People).Append(',');
        Num(j, "botsIndoors", pop.BotsIndoors).Append(",\"botsByCity\":{");
        var botsInCities = 0;
        for (var c = 0; c < pop.BotsByCity.Length; c++)
        {
            // The names a person sees — the browser's and the bots' (world-data.ts CITIES) — not the server map's own, index for index.
            var name = c < CityDisplayNames.Length ? CityDisplayNames[c] : _map.Cities[c].Name;
            j.Append(c == 0 ? "\"" : ",\"").Append(name).Append("\":").Append(pop.BotsByCity[c].ToString(inv));
            botsInCities += pop.BotsByCity[c];
        }

        j.Append("},");
        Num(j, "botsElsewhere", Math.Max(0, pop.Bots - pop.BotsIndoors - botsInCities));
        j.Append("},\"crowd\":{");
        Num(j, "lairHits", LairHitsTotal).Append(',');
        Num(j, "objectivesTaken", ObjectivesTaken).Append(',');
        Num(j, "objectivesFallen", ObjectivesFallen).Append(',');
        Num(j, "objectivesGivenUp", ObjectivesGivenUp).Append(',');
        Num(j, "heldInRange", ObjectivesHeldInRange).Append(',');
        Num(j, "outOfRange", ObjectivesOutOfRange).Append(',');
        Rate(j, "lairHitsPerSec", r10[SeriesLairHits], r60[SeriesLairHits], 1d);

        // Content levels (content-levels.md § Monitoring): every band populated, and the crowd kept at or below its level.
        var levels = Levels;
        j.Append("},\"levels\":{");
        Bands(j, "lairsByBand", levels.LairsByBand).Append(',');
        Bands(j, "creaturesByBand", levels.CreaturesByBand).Append(',');
        Num(j, "lairsBelowCrowdLevel", levels.LairsBelowCrowdLevel).Append(',');
        Num(j, "crowdObjectiveLevelMax", CrowdObjectiveLevelMax);
        AppendProgression(j, pop, r10, r60);
        return j.Append("}}").ToString();

        static StringBuilder Bands(StringBuilder b, string name, int[] values)
        {
            b.Append('"').Append(name).Append("\":[");
            for (var i = 0; i < values.Length; i++)
            {
                b.Append(i == 0 ? "" : ",").Append(values[i].ToString(CultureInfo.InvariantCulture));
            }

            return b.Append(']');
        }

        static StringBuilder Num(StringBuilder b, string name, long value)
            => b.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));

        static StringBuilder Rate(StringBuilder b, string name, double per10, double per60, double scale)
            => b.Append('"').Append(name).Append("\":{\"10s\":").Append((per10 * scale).ToString("0.##", CultureInfo.InvariantCulture))
                .Append(",\"60s\":").Append((per60 * scale).ToString("0.##", CultureInfo.InvariantCulture)).Append('}');
    }

    /// <summary>
    /// The <c>progression</c> section (character-level.md § 10): level-ups and XP by source, the gates that dropped a kill, the online combat levels by band,
    /// and XP per character-hour against the model's <c>1 200 × mean CL</c> — the ratio is the tuning number.
    /// </summary>
    private void AppendProgression(StringBuilder j, PopulationCensus pop, ReadOnlySpan<double> r10, ReadOnlySpan<double> r60)
    {
        var inv = CultureInfo.InvariantCulture;
        var characters = pop.People + pop.Bots;
        var meanCl = characters == 0 ? 0d : (double)(pop.PeopleLevels + pop.BotLevels) / characters;
        var perChar = characters == 0 ? 0d : 3600d / characters;
        j.Append("},\"progression\":{");
        j.Append("\"levelUps\":").Append(LevelUps.ToString(inv));
        j.Append(",\"killXp\":").Append(XpGrantedKills.ToString(inv));
        j.Append(",\"missionXp\":").Append(XpGrantedMissions.ToString(inv));
        j.Append(",\"greyKills\":").Append(KillsGrey.ToString(inv));
        j.Append(",\"crowdKills\":").Append(KillsByCrowd.ToString(inv));
        j.Append(",\"noKillerKills\":").Append(KillsNoKiller.ToString(inv));
        j.Append(",\"missionLairKills\":").Append(MissionLairKills.ToString(inv));
        j.Append(",\"killsDrained\":").Append(KillsDrained.ToString(inv));
        j.Append(",\"commitsFailed\":").Append(ProgressCommitsFailed.ToString(inv));
        j.Append(",\"xpRatePercent\":").Append(XpRatePercent.ToString(inv));
        j.Append(",\"bands\":{\"people\":[").AppendJoin(',', pop.PeopleByBand).Append("],\"bots\":[").AppendJoin(',', pop.BotsByBand).Append("]}");
        j.Append(",\"meanCl\":{\"people\":").Append(Mean(pop.PeopleLevels, pop.People)).Append(",\"bots\":").Append(Mean(pop.BotLevels, pop.Bots)).Append('}');
        j.Append(",\"xpPerCharHour\":{\"10s\":")
            .Append(((r10[SeriesXpKills] + r10[SeriesXpMissions]) * perChar).ToString("0", inv))
            .Append(",\"60s\":")
            .Append(((r60[SeriesXpKills] + r60[SeriesXpMissions]) * perChar).ToString("0", inv))
            .Append('}');
        j.Append(",\"modelXpPerCharHour\":").Append((Progression.XpPerHourPerLevel * meanCl).ToString("0", inv));
        j.Append(",\"levelUpsPerMin\":{\"10s\":")
            .Append((r10[SeriesLevelUps] * 60d).ToString("0.##", inv))
            .Append(",\"60s\":")
            .Append((r60[SeriesLevelUps] * 60d).ToString("0.##", inv))
            .Append('}');

        string Mean(long sum, int count) => (count == 0 ? 0d : (double)sum / count).ToString("0.##", inv);
    }
}
