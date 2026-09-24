using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Reads the centre of the entity a Sphere follows (09 § 6): its spatial field after the tick's fence, through the EntityMap and the cluster layout — no
/// transaction and no replication entry, so the entity's archetype need not be observed.
/// </summary>
/// <remarks>
/// <para>
/// <b>One reader per frame worker, on its stack, for its whole share of the sessions.</b> A chunk accessor is ~500 B of page cache whose creation zeroes it
/// and whose disposal releases its pins; made per call, two of them cost ~1.4 µs a session. Kept, the followed entities' pages stay cached across the
/// sessions a worker serves, and the accessors are rebuilt only when the next entity is of another archetype. Disposed when the worker's share ends.
/// </para>
/// <para>
/// <b>Safe in the frame stage.</b> The EntityMap's lookup is lock-free (OLC) and, inside the replication window, nothing writes it: the fence has patched
/// every location and no system runs until the frames are published (EW-01). A spatial field is SingleVersion or Transient, so its bytes live in the
/// cluster slot and no revision chain is walked.
/// </para>
/// </remarks>
internal unsafe ref struct BoundViewpoint : IDisposable, IEventEntities
{
    private readonly DatabaseEngine _engine;
    private ArchetypeEngineState _state;
    private int _routing;
    private bool _hasCluster;
    private bool _hasTransient;
    private ChunkAccessor<PersistentStore> _map;
    private ChunkAccessor<PersistentStore> _cluster;
    private ChunkAccessor<TransientStore> _transient;

    // The last entity read and its centre: every session of a Bind profile follows the same one, and reads it once per worker.
    private EntityId _lastEntity;
    private Vector3D _lastCentre;
    private bool _lastFound;

    /// <summary>A reader over <paramref name="engine"/>; no accessor is made until the first read.</summary>
    public BoundViewpoint(DatabaseEngine engine)
    {
        _engine = engine;
        _routing = -1;
    }

    /// <summary>The centre of <paramref name="entity"/>'s spatial field, or <see langword="false"/> when it is gone or has none.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="centre">Its centre, in world space; z is 0 for a 2D field.</param>
    /// <returns>Whether the entity is alive and spatially indexed.</returns>
    public bool TryRead(EntityId entity, out Vector3D centre)
    {
        centre = default;
        if (entity == _lastEntity && !entity.IsNull)
        {
            centre = _lastCentre;
            return _lastFound;
        }

        _lastEntity = entity;
        _lastFound = Read(entity, out _lastCentre);
        centre = _lastCentre;
        return _lastFound;
    }

    private bool Read(EntityId entity, out Vector3D centre)
    {
        centre = default;
        var states = _engine?._stateByRouting;
        var routing = entity.ArchetypeId;
        if (entity.IsNull || states == null || routing >= states.Length)
        {
            return false;
        }

        if (routing != _routing && !Open(states[routing], routing))
        {
            return false;
        }

        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        // A destroyed entity keeps its EntityMap record, tombstoned with its death TSN, until the cleanup passes MinTSN; its slot is already freed
        // and may hold another entity. Dead is gone.
        if (!_state.EntityMap.TryGet(entity.EntityKey, record, ref _map) || !ClusterEntityRecordAccessor.GetHeader(record).IsAlive)
        {
            return false;
        }

        var clusters = _state.ClusterState;
        var chunk = ClusterEntityRecordAccessor.GetClusterChunkId(record);
        var slot = ClusterEntityRecordAccessor.GetSlotIndex(record);
        ref readonly var spatial = ref clusters.SpatialSlot;
        var layout = clusters.Layout;
        var offset = layout.ComponentOffset(spatial.Slot) + (slot * layout.ComponentSize(spatial.Slot)) + spatial.FieldOffset;

        // A mixed archetype keeps its transient components in a second store whose clusters share the layout (ClusterRef.ResolveBase's rule).
        var transient = _hasTransient && (!_hasCluster || (layout.TransientSlotMask & (1 << spatial.Slot)) != 0);
        var field = (transient ? _transient.GetChunkAddress(chunk) : _cluster.GetChunkAddress(chunk)) + offset;
        SpatialGrid.ReadSpatialCenter3D(field, spatial.FieldInfo.FieldType, out var x, out var y, out var z);
        centre = new Vector3D(x, y, z);
        return true;
    }

    /// <summary>Where a live entity is: its archetype's cluster state, cluster chunk and slot (09 § 11: an event's entity reference).</summary>
    public bool TryLocate(EntityId entity, out ArchetypeClusterState clusters, out int chunk, out int slot)
    {
        clusters = null;
        chunk = 0;
        slot = 0;
        var states = _engine?._stateByRouting;
        var routing = entity.ArchetypeId;
        if (entity.IsNull || states == null || routing >= states.Length)
        {
            return false;
        }

        if (routing != _routing && !Open(states[routing], routing))
        {
            return false;
        }

        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        if (!_state.EntityMap.TryGet(entity.EntityKey, record, ref _map) || !ClusterEntityRecordAccessor.GetHeader(record).IsAlive)
        {
            return false;
        }

        clusters = _state.ClusterState;
        chunk = ClusterEntityRecordAccessor.GetClusterChunkId(record);
        slot = ClusterEntityRecordAccessor.GetSlotIndex(record);
        return true;
    }

    /// <summary>The push replication an event's entity field is resolved against; set by the frame prologue.</summary>
    public PushReplication Push;

    /// <inheritdoc />
    public bool TryResolve(EntityId entity, out uint netId, out float x, out float y, out float z)
    {
        netId = 0;
        x = y = z = 0f;
        return Push != null && TryLocate(entity, out var clusters, out var chunk, out var slot) && Push.TryEntityAt(clusters, chunk, slot, entity, out netId,
            out x, out y, out z);
    }

    /// <summary>Releases the accessors.</summary>
    public void Dispose() => Close();

    private bool Open(ArchetypeEngineState state, int routing)
    {
        Close();
        var clusters = state?.ClusterState;
        if (clusters == null || state.EntityMap == null || !clusters.SpatialSlot.HasSpatialIndex)
        {
            return false;
        }

        _state = state;
        _routing = routing;
        _map = state.EntityMap.Segment.CreateChunkAccessor();
        _hasCluster = clusters.ClusterSegment != null;
        _hasTransient = clusters.TransientSegment != null;
        if (_hasCluster)
        {
            _cluster = clusters.ClusterSegment!.CreateChunkAccessor();
        }

        if (_hasTransient)
        {
            _transient = clusters.TransientSegment!.CreateChunkAccessor();
        }

        return true;
    }

    private void Close()
    {
        if (_routing < 0)
        {
            return;
        }

        _map.Dispose();
        if (_hasCluster)
        {
            _cluster.Dispose();
        }

        if (_hasTransient)
        {
            _transient.Dispose();
        }

        _routing = -1;
        _state = null;
        _hasCluster = false;
        _hasTransient = false;
    }
}
