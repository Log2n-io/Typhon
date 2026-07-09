using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Precomputed cluster layout for an archetype. Immutable after creation.
/// Stores byte offsets, component sizes, cluster size N, and stride.
/// One instance per cluster-eligible archetype, shared by all EntityRef and ClusterRef instances.
/// </summary>
/// <remarks>
/// <para>Cluster memory layout (all offsets from cluster base):</para>
/// <code>
/// Offset  Size              Field
/// ──────  ────              ─────
/// 0       8                 OccupancyBits (u64, lower N bits used)
/// 8       8 × C             EnabledBits[C] (u64 per component slot)
/// 8+8C    8 × N             EntityKeys[N] (long per slot)
/// ...     sizeof(Comp₀)×N   Component₀[N] (SoA array)
/// ...     sizeof(Compᵢ)×N   Componentᵢ[N] (SoA array)
/// ...     4 × N             IndexElementIds[0][N] (one section per AllowMultiple indexed field)
/// ...     4 × N             IndexElementIds[1][N]
/// ...     4 × N             IndexElementIds[M-1][N]
/// </code>
/// <para>The IndexElementIds tail section stores, per entity, the <c>elementId</c> returned by <see cref="BTreeBase{TStore}.Add"/> when a field value was
/// inserted into the cluster's per-archetype B+Tree. It is used by the destroy and migration paths to call <see cref="BTreeBase{TStore}.RemoveValue"/>
/// with the correct elementId, so removal only wipes the specific <c>(key, clusterLocation)</c> entry rather than the entire buffer at the key (which would
/// corrupt siblings sharing the same key value on a non-unique index). Only fields marked <c>AllowMultiple = true</c> consume a section — archetypes
/// without any multi-value indexed fields have a zero-byte tail.</para>
/// </remarks>
internal sealed class ArchetypeClusterInfo
{
    /// <summary>Number of entities per cluster (8..64).</summary>
    public readonly int ClusterSize;

    /// <summary>Total byte size of one cluster (= ChunkBasedSegment stride).</summary>
    public readonly int ClusterStride;

    /// <summary>Number of component slots in this archetype.</summary>
    public readonly int ComponentCount;

    /// <summary>Byte size of the fixed header: 8 (OccupancyBits) + 8 * ComponentCount (EnabledBits).</summary>
    public readonly int HeaderSize;

    /// <summary>Byte offset of the EntityIds array (packed 64-bit EntityId) from cluster base (= HeaderSize).</summary>
    public readonly int EntityIdsOffset;

    /// <summary>Bitmask with the lower N bits set: (1UL &lt;&lt; N) - 1. Used for iteration and full-cluster detection.</summary>
    public readonly ulong FullMask;

    /// <summary>Per-component byte offsets from cluster base. Length == ComponentCount.</summary>
    private readonly int[] _componentOffsets;

    /// <summary>Per-component data sizes in bytes (pure struct size, no overhead). Length == ComponentCount.</summary>
    private readonly int[] _componentSizes;

    /// <summary>
    /// Maps component slot index to versioned index (0-based position within Versioned-only slots).
    /// -1 for SV/Transient slots. E.g. archetype [SV, V, SV, V] → [-1, 0, -1, 1].
    /// Null if no Versioned components.
    /// </summary>
    internal readonly sbyte[] SlotToVersionedIndex;

    /// <summary>Bitmask of Transient component slots. Stored here for WAL recovery access (which has layout but not ArchetypeMetadata).</summary>
    internal readonly ushort TransientSlotMask;

    /// <summary>
    /// Base byte offset (from cluster base) of the tail section holding elementId arrays for AllowMultiple indexed fields.
    /// Each of <see cref="MultipleIndexedFieldCount"/> sections occupies <c>ClusterSize × sizeof(int)</c> bytes and is laid out sequentially starting at this
    /// offset. Use <see cref="IndexElementIdOffset"/> to compute the per-entity slot address.
    /// </summary>
    public readonly int IndexElementIdsBaseOffset;

    /// <summary>
    /// Number of AllowMultiple indexed fields in this archetype (flat across all component slots) — determines the total size of the elementId tail section.
    /// Zero when the archetype has no multi-value indexed fields (no tail).
    /// </summary>
    public readonly int MultipleIndexedFieldCount;

    private ArchetypeClusterInfo(int clusterSize, int componentCount, int[] componentOffsets, int[] componentSizes, sbyte[] slotToVersionedIndex, 
        ushort transientSlotMask, int indexElementIdsBaseOffset, int multipleIndexedFieldCount)
    {
        ClusterSize = clusterSize;
        ComponentCount = componentCount;
        _componentOffsets = componentOffsets;
        _componentSizes = componentSizes;
        SlotToVersionedIndex = slotToVersionedIndex;
        TransientSlotMask = transientSlotMask;
        IndexElementIdsBaseOffset = indexElementIdsBaseOffset;
        MultipleIndexedFieldCount = multipleIndexedFieldCount;

        HeaderSize = 8 + 8 * componentCount;
        EntityIdsOffset = HeaderSize;
        FullMask = clusterSize == 64 ? ulong.MaxValue : (1UL << clusterSize) - 1;

        // Stride = offset past the elementId tail (or past the last component array when M=0), rounded up to the
        // chunk-start alignment so every cluster in the segment lands on a cache line (the segment uses a constant
        // pitch == stride, so the stride itself must be a multiple of the alignment for chunk 1..N to stay aligned).
        // The rounding adds at most ChunkStartAlignment-1 unused tail bytes per cluster; all field offsets are below
        // the unrounded end, so they are unaffected. SelectClusterSize scores against this same rounded stride.
        ClusterStride = AlignStride(indexElementIdsBaseOffset + multipleIndexedFieldCount * clusterSize * sizeof(int));
    }

    /// <summary>Byte offset of component data for the given slot from the cluster base.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComponentOffset(int slot) => _componentOffsets[slot];

    /// <summary>Data size in bytes of the component at the given slot (sizeof(T), no overhead).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComponentSize(int slot) => _componentSizes[slot];

    /// <summary>Byte offset of the EnabledBits u64 for the given component slot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int EnabledBitsOffset(int slot) => 8 + slot * 8;

    /// <summary>
    /// Byte offset (from the cluster base) of the elementId slot for a given (multi-index field, cluster slot) pair.
    /// Valid only when the entity at <paramref name="slotIndex"/> is occupied and the field is <c>AllowMultiple</c>.
    /// </summary>
    /// <param name="multiFieldIndex">Sequential index of the AllowMultiple field (0..<see cref="MultipleIndexedFieldCount"/>-1).</param>
    /// <param name="slotIndex">Slot within the cluster (0..<see cref="ClusterSize"/>-1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IndexElementIdOffset(int multiFieldIndex, int slotIndex) => IndexElementIdsBaseOffset + multiFieldIndex * ClusterSize * sizeof(int) + slotIndex * sizeof(int);

    /// <summary>
    /// Compute the optimal cluster layout for an archetype with the given component sizes.
    /// </summary>
    /// <param name="componentCount">Number of component slots (1..16).</param>
    /// <param name="componentSizes">Per-slot component data sizes in bytes (pure struct size, no overhead).</param>
    /// <param name="multipleIndexedFieldCount">
    /// Flat count of <c>AllowMultiple == true</c> indexed fields across all component slots in this archetype.
    /// Each field reserves <c>ClusterSize × sizeof(int)</c> bytes in the cluster tail for per-entity elementId storage, used by the destroy/migration path to
    /// call <see cref="BTreeBase{TStore}.RemoveValue"/>.
    /// Pass 0 when the archetype has no multi-value indexed fields (no tail reserved).
    /// </param>
    /// <param name="versionedSlotMask">Bitmask of Versioned component slots (0 for pure-SV archetypes).</param>
    /// <param name="transientSlotMask">Bitmask of Transient component slots (0 when the archetype has no Transient components).</param>
    /// <returns>A fully initialized <see cref="ArchetypeClusterInfo"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown if components are too large to fit even N=8 in one page.</exception>
    public static ArchetypeClusterInfo Compute(int componentCount, ReadOnlySpan<int> componentSizes, int multipleIndexedFieldCount = 0, 
        ushort versionedSlotMask = 0, ushort transientSlotMask = 0)
    {
        int fixedHeader = 8 + 8 * componentCount; // OccupancyBits + EnabledBits[C]
        int perEntitySize = 8; // EntityKey (long)
        for (int i = 0; i < componentCount; i++)
        {
            perEntitySize += componentSizes[i];
        }
        // Each AllowMultiple indexed field reserves sizeof(int) bytes per entity in the tail.
        perEntitySize += multipleIndexedFieldCount * sizeof(int);

        int bestN = SelectClusterSize(fixedHeader, perEntitySize);

        // Build per-component offsets
        var offsets = new int[componentCount];
        var sizes = new int[componentCount];
        int offset = fixedHeader + 8 * bestN; // Past header + EntityKeys[N]
        for (int i = 0; i < componentCount; i++)
        {
            offsets[i] = offset;
            sizes[i] = componentSizes[i];
            offset += componentSizes[i] * bestN;
        }

        // Elementid tail begins past the last component SoA block (or past EntityKeys when componentCount == 0).
        int indexElementIdsBaseOffset = offset;

        // Build slot-to-versioned-index mapping
        sbyte[] slotToVersionedIndex = null;
        if (versionedSlotMask != 0)
        {
            slotToVersionedIndex = new sbyte[componentCount];
            int vi = 0;
            for (int i = 0; i < componentCount; i++)
            {
                if ((versionedSlotMask & (1 << i)) != 0)
                {
                    slotToVersionedIndex[i] = (sbyte)vi++;
                }
                else
                {
                    slotToVersionedIndex[i] = -1;
                }
            }
        }

        return new ArchetypeClusterInfo(bestN, componentCount, offsets, sizes, slotToVersionedIndex, transientSlotMask, indexElementIdsBaseOffset, 
            multipleIndexedFieldCount);
    }

    /// <summary>
    /// Rounds a raw cluster stride up to the chunk-start alignment so the segment's constant pitch keeps every chunk
    /// cache-aligned. <see cref="PagedMMF.ChunkStartAlignment"/> must stay a power of two for the mask round-up.
    /// </summary>
    internal static int AlignStride(int stride) => (stride + (PagedMMF.ChunkStartAlignment - 1)) & ~(PagedMMF.ChunkStartAlignment - 1);

    /// <summary>
    /// Select the cluster size N in [8..64] that maximizes entities per page.
    /// </summary>
    /// <param name="fixedHeader">Fixed bytes per cluster: 8 + 8 * ComponentCount.</param>
    /// <param name="perEntitySize">Bytes per entity slot: 8 (EntityKey) + sum(sizeof(Component_i)).</param>
    /// <returns>Optimal N.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when even N=8 produces a stride exceeding <see cref="PagedMMF.PageRawDataSize"/>.
    /// </exception>
    internal static int SelectClusterSize(int fixedHeader, int perEntitySize)
    {
        int pageSize = PagedMMF.PageRawDataSize;
        int bestN = 0;
        int bestEntitiesPerPage = 0;

        for (int n = 8; n <= 64; n++)
        {
            // Score against the *aligned* stride and the segment's real non-root geometry. Chunk starts are aligned
            // to ChunkStartAlignment (64); since PageHeaderSize and PageRawDataSize are both multiples of 64 the
            // non-root alignment padding is zero, so clustersPerPage = PageRawDataSize / alignedStride. Scoring the
            // raw stride here (the previous bug) over-counted: it ignored that ChunkBasedSegment lays chunks at the
            // aligned pitch, so it picked an N that fit 2 clusters only in theory and 1 in practice.
            int stride = AlignStride(fixedHeader + perEntitySize * n);
            if (stride > pageSize)
            {
                break;
            }

            int clustersPerPage = pageSize / stride;
            int entitiesPerPage = clustersPerPage * n;
            if (entitiesPerPage > bestEntitiesPerPage)
            {
                bestEntitiesPerPage = entitiesPerPage;
                bestN = n;
            }
        }

        if (bestN == 0)
        {
            throw new InvalidOperationException(
                $"Components too large for cluster storage (fixedHeader={fixedHeader}, perEntity={perEntitySize}, " +
                $"min aligned stride at N=8 = {AlignStride(fixedHeader + perEntitySize * 8)}, page size = {PagedMMF.PageRawDataSize})");
        }

        return bestN;
    }
}
