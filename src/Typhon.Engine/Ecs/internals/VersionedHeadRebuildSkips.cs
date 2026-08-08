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
/// The counters are surfaced on <c>DatabaseEngine.LastOpenVersionedHeadRebuildSkips</c> and logged as a warning when non-zero.
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
    /// The entity record resolved but records no chain root for this Versioned slot. Legitimate for a slot never written, and from here indistinguishable from
    /// a record whose root was lost — which is exactly why it is counted rather than assumed benign.
    /// </summary>
    public int NoChainRoot;

    /// <summary>A chain root exists and <c>RevisionChainReader.WalkChain</c> failed. Never benign.</summary>
    public int ChainWalkFailed;

    /// <summary>Total pairs left un-rebuilt by this pass.</summary>
    public readonly int Total => EntityNotInMap + NoChainRoot + ChainWalkFailed;

    /// <summary>Accumulate another pass's counts into this one.</summary>
    public void Add(in VersionedHeadRebuildSkips other)
    {
        EntityNotInMap += other.EntityNotInMap;
        NoChainRoot += other.NoChainRoot;
        ChainWalkFailed += other.ChainWalkFailed;
    }
}
