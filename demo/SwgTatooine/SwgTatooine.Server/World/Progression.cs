using System;

namespace SwgTatooine.Server;

/// <summary>
/// A character's combat level and XP (character-level.md § 3): the level table, what a kill and a mission are worth, the ±4 % level factor, the health of a
/// level. Static, pure, integer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table is computed from the balance model's knobs, not typed</b> (crafting-catalog § 14.6): CL 50 after <see cref="HoursToCap"/> hours played at the
/// model's rates, each level <see cref="LevelWeight"/> longer than the one before. A retuned knob moves every level, and the check pins the seven values § 14.6
/// prints, so the model and the server cannot drift apart silently.
/// </para>
/// <para>
/// <b>Weights follow the hit points</b>: a foe has 1.5 times a beast's health and a lair four occupants', so XP per second of hitting stays the model's whatever
/// is hit, and the 45-hour calibration holds.
/// </para>
/// </remarks>
public static class Progression
{
    /// <summary>The highest combat level.</summary>
    public const int MaxLevel = 50;

    /// <summary>Play-hours from CL 1 to CL 50 at the model's rates (§ 14 B1).</summary>
    public const int HoursToCap = 45;

    /// <summary><c>hours_per_level</c>: level <c>l</c> takes <c>1 + w·l</c> units of time, as a fraction of 100.</summary>
    public const int LevelWeightPct = 15;

    /// <summary><c>xp_rates</c>: kills an hour at the model's pace.</summary>
    public const int KillsPerHour = 90;

    /// <summary><c>xp_rates</c>: XP of an on-level beast, per level of it.</summary>
    public const int KillXpPerLevel = 10;

    /// <summary><c>xp_rates</c>: missions an hour at the model's pace.</summary>
    public const int MissionsPerHour = 2;

    /// <summary><c>xp_rates</c>: XP of an on-level mission, per level of it.</summary>
    public const int MissionXpPerLevel = 150;

    /// <summary>XP an hour at CL 1 at the model's rates (<c>90 × 10 + 2 × 150</c>); at CL <c>l</c>, <c>l</c> times this.</summary>
    public const int XpPerHourPerLevel = (KillsPerHour * KillXpPerLevel) + (MissionsPerHour * MissionXpPerLevel);

    /// <summary>A victim this many levels or more below the killer gives nothing (a grey kill).</summary>
    public const int GreyGap = 10;

    /// <summary>XP to go from level <c>l</c> to <c>l + 1</c>, indexed by <c>l</c>; 0 at 0 and at <see cref="MaxLevel"/>.</summary>
    private static readonly int[] ToNext = BuildToNext();

    /// <summary>Total XP at which level <c>l</c> starts, indexed by <c>l</c>, 1 to <see cref="MaxLevel"/>.</summary>
    private static readonly int[] Start = BuildStart();

    /// <summary>
    /// <c>XpToNext(l) = round(HoursToCap × (1 + w·l) / Σₖ₌₁⁴⁹(1 + w·k) × XpPerHourPerLevel × l)</c>, in integers: with <c>w = 15/100</c>, <c>Σ(1 + w·k)</c>
    /// over 1..49 is <c>(4 900 + 15 × 1 225) / 100</c>, so the whole is <c>HoursToCap × XpPerHourPerLevel × l × (100 + 15 l) / (4 900 + 18 375)</c>.
    /// </summary>
    private static int[] BuildToNext()
    {
        var weights = 0L;
        for (var k = 1; k < MaxLevel; k++)
        {
            weights += 100 + (LevelWeightPct * k);
        }

        var table = new int[MaxLevel + 1];
        for (var l = 1; l < MaxLevel; l++)
        {
            var num = (long)HoursToCap * XpPerHourPerLevel * l * (100 + (LevelWeightPct * l));
            table[l] = (int)(((2 * num) + weights) / (2 * weights));
        }

        return table;
    }

    private static int[] BuildStart()
    {
        var start = new int[MaxLevel + 1];
        for (var l = 2; l <= MaxLevel; l++)
        {
            start[l] = start[l - 1] + ToNext[l - 1];
        }

        return start;
    }

    /// <summary>XP from level <paramref name="level"/> to the next; 0 at the cap or outside 1..49.</summary>
    public static int XpToNext(int level) => level is >= 1 and < MaxLevel ? ToNext[level] : 0;

    /// <summary>Total XP at which <paramref name="level"/> starts (1 691 050 at 50); 0 for level 1 and below.</summary>
    public static int XpAt(int level) => Start[Math.Clamp(level, 0, MaxLevel)];

    /// <summary>The XP a character can hold: the start of the cap.</summary>
    public static int MaxXp => Start[MaxLevel];

    /// <summary><c>lvl_mod</c>, in percent: 4 % a level of advantage, clamped to ×0.5–1.5. Shared by XP and damage — the one level factor.</summary>
    public static int LevelPercent(int advantage) => Math.Clamp(100 + (4 * advantage), 50, 150);

    /// <summary>A kill's weight by victim kind, in percent: beast 100, foe 150, lair 400 — their hit points'.</summary>
    public static int KindWeightPct(byte kind) => kind switch
    {
        KillVictimKind.Foe => 150,
        KillVictimKind.Lair => 400,
        KillVictimKind.Creature => 100,
        _ => 0,
    };

    /// <summary>
    /// XP for a kill: <c>10 × victim level × kind weight × level factor × rate</c>, rounded; 0 for a victim without a level, a killer without a character
    /// (<paramref name="cl"/> 0), or a victim <see cref="GreyGap"/> or more levels below.
    /// </summary>
    public static int KillXp(byte kind, int victimLevel, int cl, int ratePct = 100)
    {
        if (victimLevel <= 0 || cl <= 0 || victimLevel <= cl - GreyGap)
        {
            return 0;
        }

        var num = (long)KillXpPerLevel * victimLevel * KindWeightPct(kind) * LevelPercent(victimLevel - cl) * Math.Max(0, ratePct);
        return (int)Math.Min(int.MaxValue, (num + 500_000) / 1_000_000);
    }

    /// <summary>XP for a completed mission: <c>150 × mission level × level factor × rate</c>, rounded; 0 when the mission is ten or more levels below.</summary>
    public static int MissionXp(int level, int cl, int ratePct = 100)
    {
        if (level <= 0 || cl <= 0 || level <= cl - GreyGap)
        {
            return 0;
        }

        var num = (long)MissionXpPerLevel * level * LevelPercent(level - cl) * Math.Max(0, ratePct);
        return (int)Math.Min(int.MaxValue, (num + 5_000) / 10_000);
    }

    /// <summary>
    /// Adds <paramref name="xp"/> to a character's progress — the total, clamped at <see cref="MaxXp"/>, and the source's — and raises its level across every
    /// threshold crossed. A level is never lost.
    /// </summary>
    /// <returns>The levels gained, several at once possible.</returns>
    public static int AddXp(ref CharacterProgress p, int xp, bool mission)
    {
        if (xp <= 0)
        {
            return 0;
        }

        p.Xp = (int)Math.Min(MaxXp, (long)p.Xp + xp);
        if (mission)
        {
            p.MissionXp = (int)Math.Min(int.MaxValue, (long)p.MissionXp + xp);
        }
        else
        {
            p.KillXp = (int)Math.Min(int.MaxValue, (long)p.KillXp + xp);
        }

        var before = p.Level;
        if (p.Level < 1)
        {
            p.Level = 1;
        }

        while (p.Level < MaxLevel && p.Xp >= Start[p.Level + 1])
        {
            p.Level++;
        }

        return p.Level - Math.Max((byte)1, before);
    }

    /// <summary>The way to the next level in 256ths, for the body's <see cref="PlayerLook.Xp"/>: 255 at the cap.</summary>
    public static byte XpByte(in CharacterProgress p)
    {
        if (p.Level >= MaxLevel)
        {
            return 255;
        }

        var level = Math.Max(1, (int)p.Level);
        var into = Math.Clamp(p.Xp - Start[level], 0, ToNext[level]);
        return (byte)Math.Min(255, (long)into * 255 / ToNext[level]);
    }

    /// <summary>A body's max health at combat level <paramref name="cl"/>: 1 000 + 40 a level (1 400 at 10, the crowd's).</summary>
    public static int HealthFor(int cl) => 1000 + (40 * Math.Max(1, cl));

    /// <summary>The B3 equipment gate: a Mk <paramref name="mk"/> item needs CL 10(mk − 1) + 1. Nothing calls it until equipment exists.</summary>
    public static bool CanEquip(int cl, int mk) => cl >= (10 * (mk - 1)) + 1;
}
