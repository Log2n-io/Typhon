namespace Typhon.Engine.Internals;

/// <summary>
/// Breakdown of the (entity, Versioned slot) pairs an open-time <c>RebuildVersionedHeadFromChain</c> pass could not rebuild.
/// </summary>
/// <remarks>
/// Every one of these leaves a cluster slot holding whatever it already held — zero on a fresh reopen — and that value is then served to a caller as committed
/// state. <c>IsValid</c> passes, the rebuild count is non-zero, and only the value is wrong, which is the <c>[fatal][silent]</c> shape #688 reports: a reopened
/// database returning 0 for a component that was committed as 500.
/// <para>
/// The rebuild genuinely cannot repair these — the chain it needs is not reachable from where it stands — so this does not convert them into successes. It
/// converts them from invisible into counted, which is the difference between a defect you can diagnose and one that takes a 1-in-4 arm64 nightly to notice.
/// The counters are surfaced on <c>DatabaseEngine.LastOpenVersionedHeadRebuildSkips</c>; those in <see cref="Total"/> are logged as a warning when non-zero.
/// <see cref="AbsentByDesign"/> is accumulated but deliberately not warned about and not named in the log — it is an expected state, and its only consumer
/// today is test assertions.
/// </para>
/// </remarks>
internal struct VersionedHeadRebuildSkips
{
    /// <summary>
    /// The cluster holds the entity but <c>EntityMap.TryGet</c> did not. RB-01's documented ordering caveat is the known route: on the crash path the loaded
    /// EntityMap is not yet trusted, and a MIXED cluster archetype runs this pass before the map is rebuilt.
    /// </summary>
    public int EntityNotInMap;

    /// <summary>
    /// The record resolves, the slot's enabled bit is CLEAR, and it has no chain root — a component that was never supplied. Expected, not a defect (#845).
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="ChainRootLost"/> when an unsupplied Versioned component became genuinely absent rather than zero-initialised. Before that, every
    /// rootless slot was a suspect and counting them all was right; afterwards a partial spawn produces them by design, on every healthy database. Leaving them
    /// in the warned total would have made a <c>Warning</c>-level log routine — and a log that fires when nothing is wrong is one operators learn to ignore,
    /// which is precisely how #688 stayed silent. Deliberately excluded from <see cref="Total"/>.
    /// </remarks>
    public int AbsentByDesign;

    /// <summary>
    /// The record resolves and the slot's enabled bit is SET, but it records no chain root: the component should have a value and the pointer to it is gone.
    /// Never benign.
    /// </summary>
    /// <remarks>
    /// The enabled bit is what separates this from <see cref="AbsentByDesign"/>. Absence is (bit clear, root 0); a lost root is (bit set, root 0). Both were
    /// indistinguishable while absence was not representable, so both were counted here.
    /// </remarks>
    public int ChainRootLost;

    /// <summary>A chain root exists and <c>RevisionChainReader.WalkChain</c> failed. Never benign.</summary>
    public int ChainWalkFailed;

    /// <summary>
    /// A rootless slot on a pass where the enabled bit could not be trusted, so absence and a lost root are indistinguishable. Counted as a defect.
    /// </summary>
    /// <remarks>
    /// The bit separating <see cref="AbsentByDesign"/> from <see cref="ChainRootLost"/> is only as good as its source. On the crash path the EntityMap is
    /// re-derived by <c>RebuildEntityMapsFromPersistedData</c>, which reconstructs <c>EnabledBits</c> from the cluster SoA copy — and the durability of that
    /// copy is the open gap tracked in #398. An entity that lost BOTH its chain root and its SoA bit would then read as "bit clear, root 0" and be filed as
    /// expected absence, which is exactly the #688 case being silently dropped by the counter that exists to catch it. Where the bit cannot be trusted the
    /// honest answer is neither bucket, and the safe one is to warn: this feeds <see cref="Total"/>.
    /// </remarks>
    public int RootlessUnclassifiable;

    /// <summary>Total pairs left un-rebuilt by this pass that indicate a DEFECT — <see cref="AbsentByDesign"/> is expected and excluded.</summary>
    public readonly int Total => EntityNotInMap + ChainRootLost + ChainWalkFailed + RootlessUnclassifiable;

    /// <summary>Accumulate another pass's counts into this one.</summary>
    public void Add(in VersionedHeadRebuildSkips other)
    {
        EntityNotInMap += other.EntityNotInMap;
        AbsentByDesign += other.AbsentByDesign;
        ChainRootLost += other.ChainRootLost;
        ChainWalkFailed += other.ChainWalkFailed;
        RootlessUnclassifiable += other.RootlessUnclassifiable;
    }
}
