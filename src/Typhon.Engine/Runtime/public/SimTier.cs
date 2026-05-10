using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Simulation tier for tier-filtered system dispatch (issue #231). A spatial cell's tier determines which game systems process the clusters attached to that
/// cell — Tier 0 cells run every system every tick, Tier 3 cells may only be touched by aggregate models.
/// </summary>
/// <remarks>
/// <para><b>Not to be confused with <see cref="SpatialTier"/></b>, which classifies cluster storage by dimensionality and precision (2F/3F/2D/3D) and is an
/// internal concern of the per-cell R-Tree (issue #230). <see cref="SimTier"/> is orthogonal: it controls dispatch frequency, not storage layout.</para>
/// <para>Values are flag bits so systems can target multiple tiers at once (e.g. a movement system running in both near-camera tiers would
/// use <see cref="Near"/>). The encoding <c>Tier0=1, Tier1=2, Tier2=4, Tier3=8</c> lets <see cref="TierExtensions.ToIndex"/> map a single-bit flag to its
/// array index via <see cref="BitOperations.TrailingZeroCount(uint)"/> in ~1 cycle.</para>
/// </remarks>
[Flags]
[PublicAPI]
public enum SimTier : byte
{
    /// <summary>Sentinel: no tiers. Invalid as a system tier filter (rejected at <c>RuntimeSchedule.Build</c>).</summary>
    None = 0,

    /// <summary>Full simulation — every tick, near the observer.</summary>
    Tier0 = 1 << 0,

    /// <summary>Reduced simulation — every tick but lighter work (e.g. movement only).</summary>
    Tier1 = 1 << 1,

    /// <summary>Coarse simulation — amortized across ticks, typically combined with <c>cellAmortize</c>.</summary>
    Tier2 = 1 << 2,

    /// <summary>Statistical / dormant — aggregate model, no per-entity work.</summary>
    Tier3 = 1 << 3,

    /// <summary>Convenience combination: <see cref="Tier0"/> + <see cref="Tier1"/> + <see cref="Tier2"/>. Excludes Tier 3.</summary>
    Active = Tier0 | Tier1 | Tier2,

    /// <summary>Convenience combination: <see cref="Tier0"/> + <see cref="Tier1"/>.</summary>
    Near = Tier0 | Tier1,

    /// <summary>Convenience combination: all four tiers. The default system tier filter — matches pre-#231 behaviour (no filtering).</summary>
    All = Tier0 | Tier1 | Tier2 | Tier3,
}

/// <summary>Extension helpers for <see cref="SimTier"/>.</summary>
[PublicAPI]
public static class TierExtensions
{
    /// <summary>Number of tier slots — fixed at 4 (<see cref="SimTier.Tier0"/> through <see cref="SimTier.Tier3"/>).</summary>
    public const int TierCount = 4;

    /// <summary>
    /// Map a single-bit <see cref="SimTier"/> flag to its 0-based array index (<c>Tier0→0, Tier1→1, Tier2→2, Tier3→3</c>).
    /// </summary>
    /// <remarks>
    /// Undefined result when <paramref name="tier"/> has zero or multiple bits set — callers must gate this method with <see cref="IsSingleTier"/>.
    /// The implementation uses <see cref="BitOperations.TrailingZeroCount(uint)"/>, which the JIT folds into a single TZCNT instruction on x86-64.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToIndex(this SimTier tier) => BitOperations.TrailingZeroCount((uint)(byte)tier);

    /// <summary>True when <paramref name="tier"/> has exactly one bit set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSingleTier(this SimTier tier) => BitOperations.PopCount((byte)tier) == 1;

    /// <summary>Number of tier bits set in <paramref name="tier"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TierCountOf(this SimTier tier) => BitOperations.PopCount((byte)tier);
}
