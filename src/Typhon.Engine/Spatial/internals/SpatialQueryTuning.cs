namespace Typhon.Engine.Internals;

/// <summary>
/// Process-wide switches for the R-Tree query optimisations of #872 step 17, so every arm of the campaign runs in ONE
/// binary and can be interleaved against its own baseline.
/// </summary>
/// <remarks>
/// <para><b>These are measurement switches, not configuration.</b> They default to the optimised behaviour; the
/// benchmark flips them off to reproduce the pre-change path. Once the numbers are recorded each one either becomes
/// unconditional or is deleted — neither outcome wants a public knob, which is why this is an internal static rather
/// than a field on <c>SpatialOptions</c>. Same shape, and the same reasoning, as
/// <c>ArchetypeClusterState.PluralityStableRepack</c> in step 17's repair campaign.</para>
/// <para>Plain statics, not <c>Volatile</c>: they are written once by a benchmark before it starts an arm and read on
/// the query path, never flipped while a query is in flight.</para>
/// </remarks>
internal static class SpatialQueryTuning
{
    /// <summary>Resolve a leaf's chunk address ONCE per leaf instead of once per entry.</summary>
    internal static bool HoistLeafBase = true;

    /// <summary>Begin the query telemetry span only when its own gate is on, so the JIT can drop it when it is not.</summary>
    internal static bool GateQuerySpan = true;

    /// <summary>Skip per-entry overlap tests under a subtree the query box fully contains.</summary>
    internal static bool FullyContained = true;

    /// <summary>Test a whole leaf's entries with SIMD against f32 bounds, instead of six widening scalar reads per entry.</summary>
    internal static bool SimdLeafScan = true;

    /// <summary>Classify a whole internal node's children with SIMD — overlap and containment in one pass.</summary>
    internal static bool SimdInternalScan = true;

    /// <summary>Take the query box as f32 from the caller instead of routing it through the f64 coordinate array.</summary>
    internal static bool DirectFloatBox = true;

    /// <summary>Match the linear per-cell index a batch of 64 clusters at a time with SIMD, instead of one at a time.</summary>
    internal static bool SimdLinearScan = true;

    /// <summary>Test an AABB2F cluster's entities sixteen at a time with SIMD (<c>NarrowphaseAabb2F</c>), instead of one at a time. Read when a query is
    /// constructed.</summary>
    internal static bool SimdNarrowphase = true;
}
