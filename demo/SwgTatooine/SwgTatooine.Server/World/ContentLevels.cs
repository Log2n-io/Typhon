using System;
using System.Collections.Generic;

namespace SwgTatooine.Server;

/// <summary>
/// Content levels (content-levels.md): every lair and creature has a level 1-50, set by where it stands, and its health and damage follow from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ramp</b>: level 1 at a city's edge and one more every 100 m, so level 10 at 1 km (Loïc: "Level 1 should always be at city's edge"). <b>Beyond it</b>
/// every site carries a band, II to V, and a lair there draws any of its band's ten levels (<see cref="LevelMap"/>).
/// </para>
/// <para>
/// <b>The stats port <c>balance_model.py</c>'s <c>creature()</c></b> (claude/design/SwgTatooine/design/balance): an on-level player kills an on-level beast in
/// 8 s of hitting, and one on-level beast alone kills an on-level player in 75 s. The knobs below mirror the script's and must move with it. The tables are
/// computed once, at type initialisation, so nothing on the tick path does the arithmetic.
/// </para>
/// </remarks>
public static class ContentLevels
{
    /// <summary>The highest level: the shared cap, a character's CL included.</summary>
    public const int MaxLevel = 50;

    /// <summary>Levels 1-10 over the first kilometre beyond a city's edge: 100 m a level. × <see cref="SimConfig.ContentScale"/>.</summary>
    public const float RampM = 1000f;

    /// <summary>How far a lair keeps clear of a city's edge, metres (Core3's cities are <c>NOSPAWNAREA</c>). × <see cref="SimConfig.ContentScale"/>.</summary>
    public const float NoSpawnMarginM = 30f;

    // balance_model.py KNOBS — mirrors, keep in step with the script.
    private const double DpsBand1 = 90;          // dps_band1
    private const double DpsGrowth = 1.25;       // dps_growth
    private const double HpBase = 1000;          // hp_base
    private const double HpPerCl = 40;           // hp_per_cl
    private const double MitCl5 = 0.12;          // mit_cl5
    private const double MitPerCl = 0.006;       // mit_per_cl
    private const double TtkKillS = 8.0;         // ttk_kill_s
    private const double TtkDieS = 75.0;         // ttk_die_s
    private const double FoeHp = 1.5;            // foe_hp
    private const double FoeDmg = 1.3;           // foe_dmg

    // Indexed by level, 0 unused; [beast, foe].
    private static readonly int[] BeastHealth = new int[MaxLevel + 1];
    private static readonly int[] FoeHealth = new int[MaxLevel + 1];
    private static readonly int[] BeastHit = new int[MaxLevel + 1];
    private static readonly int[] FoeHit = new int[MaxLevel + 1];

    static ContentLevels()
    {
        // A creature attacks every MinAttackDelaySec to MaxAttackDelaySec: 2 s on average, the script's "hit = dps × 2.0".
        var avgDelay = (TatooineData.MinAttackDelaySec + TatooineData.MaxAttackDelaySec) / 2.0;
        for (var l = 1; l <= MaxLevel; l++)
        {
            var dps = DpsBand1 * Math.Pow(DpsGrowth, (l - 5) / 10.0);
            var effHp = (HpBase + (HpPerCl * l)) / (1 - (MitCl5 + (MitPerCl * (l - 5))));
            BeastHealth[l] = (int)Math.Round(dps * TtkKillS);
            FoeHealth[l] = (int)Math.Round(dps * TtkKillS * FoeHp);
            BeastHit[l] = (int)Math.Round(effHp / TtkDieS * avgDelay);
            FoeHit[l] = (int)Math.Round(effHp / TtkDieS * FoeDmg * avgDelay);
        }
    }

    /// <summary>The band of a level: I for 1-10 … V for 41-50 (§ 14's <c>band()</c>, a loot Mk).</summary>
    public static int Band(int level) => Math.Clamp(((level - 1) / 10) + 1, 1, 5);

    /// <summary>A band's first level: 1, 11, 21, 31, 41.</summary>
    public static int BandFloor(int band) => (10 * band) - 9;

    /// <summary>The ramp's level at <paramref name="edgeM"/> metres beyond a city's edge: 1 within the first 100 m, 10 from 900 m.</summary>
    public static int RampLevel(float edgeM, float scale) => Math.Clamp(1 + (int)(10f * Math.Max(0f, edgeM) / (RampM * scale)), 1, 10);

    /// <summary>A level clamped into 1-50.</summary>
    public static int Clamp(int level) => Math.Clamp(level, 1, MaxLevel);

    /// <summary>A creature's hit points at a level: a beast's, or a camp foe's (× 1.5).</summary>
    public static int CreatureHealth(int level, bool foe) => (foe ? FoeHealth : BeastHealth)[Clamp(level)];

    /// <summary>A creature's damage per attack at a level: a beast's, or a camp foe's (× 1.3).</summary>
    public static int CreatureHit(int level, bool foe) => (foe ? FoeHit : BeastHit)[Clamp(level)];

    /// <summary>
    /// A lair's hit points: three of its occupants' — 24 s of on-level hitting once its guards are down, so it outlasts any foe it holds (Loïc,
    /// 2026-10-24) without becoming a long fight against a prop.
    /// </summary>
    public static int LairHealth(int level, int template) => 3 * CreatureHealth(level, CreatureTemplates.IsCamp(template));

    /// <summary>Whether a template's level range meets a band's ten levels.</summary>
    public static bool Covers(int template, int band)
        => CreatureTemplates.MinLevel[template] <= 10 * band && CreatureTemplates.MaxLevel[template] >= BandFloor(band);

    /// <summary>The wild kinds (0-8) whose range meets a band: what a frontier patch of that band spawns.</summary>
    public static int[] WildKindsOfBand(int band)
    {
        var kinds = new List<int>();
        for (var t = 0; t < CreatureTemplates.WildCount; t++)
        {
            if (Covers(t, band))
            {
                kinds.Add(t);
            }
        }

        return [.. kinds];
    }
}

/// <summary>A site beyond the ramp that names a band, for <see cref="LevelMap.BandAt"/>.</summary>
/// <param name="X">Its centre.</param>
/// <param name="Z">Its centre.</param>
/// <param name="Radius">Its disc.</param>
/// <param name="Band">II to V.</param>
public readonly record struct LevelAnchor(float X, float Z, float Radius, int Band);

/// <summary>
/// One planet's content levels: the ramp round its cities and the bands of the sites beyond it (content-levels.md § 2). Planned with the sites
/// (<c>WorldBuilder.PlanSites</c>) from the map and the seed alone, so a reopened world has the same map.
/// </summary>
/// <remarks>Read-only once planned; read by the build, the lair director and the mission desk, all serial.</remarks>
public sealed class LevelMap
{
    private readonly float[] _cityX;
    private readonly float[] _cityZ;
    private readonly float[] _cityR;
    private readonly List<LevelAnchor> _anchors = [];

    /// <summary>A planet's level map, with no anchors yet.</summary>
    /// <param name="cities">The planet's cities, scaled.</param>
    /// <param name="scale">The run's <see cref="SimConfig.ContentScale"/>.</param>
    public LevelMap(IReadOnlyList<CityDef> cities, float scale)
    {
        ArgumentNullException.ThrowIfNull(cities);
        Scale = scale;
        _cityX = new float[cities.Count];
        _cityZ = new float[cities.Count];
        _cityR = new float[cities.Count];
        for (var i = 0; i < cities.Count; i++)
        {
            _cityX[i] = cities[i].X;
            _cityZ[i] = cities[i].Z;
            _cityR[i] = cities[i].Radius;
        }
    }

    /// <summary>The run's content scale: the ramp is <see cref="ContentLevels.RampM"/> × this.</summary>
    public float Scale { get; }

    /// <summary>The ramp's width here, metres.</summary>
    public float RampM => ContentLevels.RampM * Scale;

    /// <summary>Every site beyond the ramp that names a band: the landmarks, the regions and the frontier patches.</summary>
    public IReadOnlyList<LevelAnchor> Anchors => _anchors;

    /// <summary>Adds a banded site.</summary>
    internal void AddAnchor(float x, float z, float radius, int band) => _anchors.Add(new LevelAnchor(x, z, radius, band));

    /// <summary>The distance to the nearest city's edge, metres; negative inside a city.</summary>
    public float EdgeDistance(float x, float z) => EdgeDistance(x, z, out _);

    /// <summary>The distance to the nearest city's edge, and which city that is.</summary>
    public float EdgeDistance(float x, float z, out int city)
    {
        var best = float.MaxValue;
        city = -1;
        for (var i = 0; i < _cityX.Length; i++)
        {
            var dx = x - _cityX[i];
            var dz = z - _cityZ[i];
            var d = MathF.Sqrt((dx * dx) + (dz * dz)) - _cityR[i];
            if (d < best)
            {
                best = d;
                city = i;
            }
        }

        return best;
    }

    /// <summary>Whether a point is inside a city or within <paramref name="marginM"/> of its edge.</summary>
    public bool NearCity(float x, float z, float marginM) => _cityX.Length > 0 && EdgeDistance(x, z) < marginM;

    /// <summary>
    /// The band at a point: I in the ramp; beyond it the band of the anchor that minimises (distance outside its disc, its radius) — the containing disc
    /// wins, and of overlapping discs the smaller, so a patch inside a region keeps its band. Defined everywhere.
    /// </summary>
    public int BandAt(float x, float z)
    {
        if (EdgeDistance(x, z) < RampM || _anchors.Count == 0)
        {
            return 1;
        }

        var bestOut = float.MaxValue;
        var bestR = float.MaxValue;
        var band = 1;
        foreach (var a in _anchors)
        {
            var dx = x - a.X;
            var dz = z - a.Z;
            var outside = MathF.Max(0f, MathF.Sqrt((dx * dx) + (dz * dz)) - a.Radius);
            if (outside < bestOut || (outside == bestOut && a.Radius < bestR))
            {
                bestOut = outside;
                bestR = a.Radius;
                band = a.Band;
            }
        }

        return band;
    }

    /// <summary>The level at a point: the ramp's, which draws nothing, or any of the band's ten levels beyond it, drawn from <paramref name="rng"/>.</summary>
    public int LevelAt(float x, float z, ref Rng rng)
    {
        var edge = EdgeDistance(x, z);
        if (edge < RampM)
        {
            return ContentLevels.RampLevel(edge, Scale);
        }

        return ContentLevels.BandFloor(BandAt(x, z)) + rng.NextInt(0, 10);
    }

    /// <summary>Whether a level is what the map says at a point: the ramp's exactly, or inside the band beyond it.</summary>
    public bool Agrees(float x, float z, int level)
    {
        var edge = EdgeDistance(x, z);
        return edge < RampM ? level == ContentLevels.RampLevel(edge, Scale) : ContentLevels.Band(level) == BandAt(x, z);
    }
}
