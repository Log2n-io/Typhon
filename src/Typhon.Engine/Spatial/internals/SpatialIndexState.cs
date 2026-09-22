namespace Typhon.Engine.Internals;

/// <summary>
/// Per-<see cref="ComponentTable"/> spatial metadata. Null on a table whose component has no <c>[SpatialIndex]</c> field.
/// </summary>
/// <remarks>
/// <para><b>What this used to be, and why the difference matters.</b> Until #872 step 13 this type also owned an entity-level
/// <c>SpatialRTree&lt;PersistentStore&gt;</c>, its back-pointer segment and a Layer-1 occupancy hashmap — three persisted segments per spatial component. That
/// tree lost its last writer to #666 ("EVERY archetype is cluster-backed"), which made <c>IsClusterEligible</c> unconditionally true and left every
/// entity-tree writer either behind a <c>!IsClusterEligible</c> guard or with no caller at all. It was still allocated, still written into the file, still
/// reloaded on open and still traversed by every spatial query — permanently empty. Step 13 removed it once its last unique capabilities (ray and frustum)
/// existed on the cluster path.</para>
/// <para>What is left is metadata that describes the spatial FIELD rather than any index over it — the offset and shape the cluster path decodes bounds with —
/// plus the fan-out list of cluster archetypes that actually hold the data, and the two systems layered on them.</para>
/// </remarks>
internal class SpatialIndexState
{
    public SpatialFieldInfo FieldInfo { get; }

    /// <summary>
    /// Node layout for this component's spatial variant.
    /// </summary>
    /// <remarks>
    /// Retained after the entity tree's removal because the PER-CELL cluster trees share the layout, and because query code reads
    /// <see cref="SpatialNodeDescriptor.CoordCount"/> as the authority on whether a component is 2D or 3D.
    /// </remarks>
    public SpatialNodeDescriptor Descriptor { get; }

    /// <summary>Trigger volume system for this spatial index. Null until first <see cref="GetOrCreateTriggerSystem"/> call.</summary>
    public SpatialTriggerSystem TriggerSystem { get; private set; }

    /// <summary>Interest management system for this spatial index. Null until first <see cref="GetOrCreateInterestSystem"/> call.</summary>
    public SpatialInterestSystem InterestSystem { get; private set; }

    /// <summary>Serialises the two lazy constructions below. Uncontended after first use.</summary>
    private readonly object _systemsLock = new();

    /// <summary>Get or create the interest management system for this spatial index.</summary>
    /// <remarks>
    /// <b>Locked, not <c>??=</c>.</b> Two racing callers each ran the null check, each built a system, and each stored theirs — after which one caller's
    /// observers were registered on an instance nothing else could reach, and its deltas silently stopped arriving. Harmless while the only callers were
    /// tests calling this once; #872 step 13 made it public API, where "get the observer set" is exactly the kind of call two systems make on startup.
    /// </remarks>
    internal SpatialInterestSystem GetOrCreateInterestSystem(ComponentTable table)
    {
        if (InterestSystem != null)
        {
            return InterestSystem;
        }

        lock (_systemsLock)
        {
            return InterestSystem ??= new SpatialInterestSystem(table, this);
        }
    }

    /// <summary>Get or create the trigger system for this spatial index.</summary>
    /// <inheritdoc cref="GetOrCreateInterestSystem" path="/remarks"/>
    internal SpatialTriggerSystem GetOrCreateTriggerSystem(ComponentTable table)
    {
        if (TriggerSystem != null)
        {
            return TriggerSystem;
        }

        lock (_systemsLock)
        {
            return TriggerSystem ??= new SpatialTriggerSystem(table, this);
        }
    }

    // ── Cluster archetype references for fan-out ────────────

    /// <summary>Per-archetype cluster spatial state references, registered during InitializeArchetypes.</summary>
    internal System.Collections.Generic.List<ArchetypeClusterState> ClusterArchetypes { get; private set; }

    /// <summary>Register a cluster archetype that has spatial fields for this component. Called from DatabaseEngine.InitializeArchetypes.</summary>
    internal void RegisterClusterArchetype(ArchetypeClusterState clusterState)
    {
        ClusterArchetypes ??= new System.Collections.Generic.List<ArchetypeClusterState>(4);
        ClusterArchetypes.Add(clusterState);
    }

    internal SpatialIndexState(SpatialFieldInfo fieldInfo, SpatialNodeDescriptor descriptor)
    {
        FieldInfo = fieldInfo;
        Descriptor = descriptor;
    }
}
