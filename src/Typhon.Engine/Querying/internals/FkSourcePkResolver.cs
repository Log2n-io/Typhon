using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Resolves an FK index entry to the primary key of the entity that owns it, for either persistent storage mode.
/// </summary>
/// <remarks>
/// <para>
/// A foreign-key index maps <c>fkValue → chunkId</c>, but what that chunk id addresses depends on the source component's
/// <see cref="StorageMode"/>, and the two modes are not interchangeable:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="StorageMode.Versioned"/> — the value is a CompRev chunk id, and the owning entity's PK lives in that
///     chunk's <c>CompRevStorageHeader</c>.
///   </description></item>
///   <item><description>
///     <see cref="StorageMode.SingleVersion"/> — there is no CompRev table at all (<c>ComponentTable</c> allocates it only
///     for Versioned), so the value is a component chunk id and the PK is the inline 8-byte overhead at the start of the
///     chunk — the same read <c>PipelineExecutor.ExecutePKsTypedSV</c> performs.
///   </description></item>
/// </list>
/// <para>
/// Navigation used to hard-code the Versioned shape, which made every FK query throw a bare
/// <see cref="NullReferenceException"/> against a SingleVersion source (issue #623). Routing both modes through this type
/// keeps the two layouts described in exactly one place.
/// </para>
/// <para>
/// <b>Scope: the per-ComponentTable index only.</b> This type turns a chunk id from that tree into an entity PK. A cluster-backed archetype keeps its field
/// indexes on the ARCHETYPE, where the value is a packed <c>ClusterLocation</c> and the PK comes from the cluster's own entity-id array — no chunk header is
/// involved, so that path does not use this type at all. See <see cref="FkReverseLookup"/>, which runs both.
/// </para>
/// <para>
/// The SingleVersion branch below is therefore for a SingleVersion component in a NON-cluster archetype. It was unreachable while
/// <see cref="Create"/> rejected the mode outright (a deliberate loud failure, because the cluster-backed case would otherwise have returned a silently
/// empty result); that guard is gone since #662 gave the reverse lookup the cluster path it was missing.
/// </para>
/// <para>
/// Holds an open <c>ChunkAccessor</c> for the whole enumeration — construct once outside the loop and dispose it, as the
/// query paths do.
/// </para>
/// </remarks>
internal unsafe struct FkSourcePkResolver : IDisposable
{
    private ChunkAccessor<PersistentStore> _accessor;
    private readonly bool _singleVersion;

    private FkSourcePkResolver(ChunkAccessor<PersistentStore> accessor, bool singleVersion)
    {
        _accessor = accessor;
        _singleVersion = singleVersion;
    }

    /// <summary>
    /// Opens an accessor over whichever segment carries the entity PK for <paramref name="table"/>'s storage mode.
    /// </summary>
    /// <param name="table">Source component table the FK index belongs to.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown for <see cref="StorageMode.Transient"/> — Transient data has no persistent index to navigate.
    /// </exception>
    internal static FkSourcePkResolver Create(ComponentTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.StorageMode == StorageMode.Transient)
        {
            throw new NotSupportedException(
                $"FK navigation is not supported on Transient component '{table.Name}' — Transient data has no persistent index to navigate.");
        }

        var singleVersion = table.StorageMode == StorageMode.SingleVersion;
        var segment = singleVersion ? table.ComponentSegment : table.CompRevTableSegment;
        return new FkSourcePkResolver(segment.CreateChunkAccessor(), singleVersion);
    }

    /// <summary>Maps one FK index value to the primary key of the entity that owns the indexed component.</summary>
    /// <param name="indexValue">Chunk id taken from the FK index — a CompRev chunk for Versioned, a component chunk for SingleVersion.</param>
    internal long Resolve(int indexValue)
        => _singleVersion
            ? *(long*)_accessor.GetChunkAddress(indexValue)
            : _accessor.GetChunk<CompRevStorageHeader>(indexValue).EntityPK;

    /// <summary>Releases the underlying chunk accessor.</summary>
    public void Dispose() => _accessor.Dispose();
}
