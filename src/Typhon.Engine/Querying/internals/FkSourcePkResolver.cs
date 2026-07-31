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
/// <b>The SingleVersion branch is not reachable from navigation yet</b> — <see cref="Create"/> rejects that mode. Fixing the
/// read layout was necessary but not sufficient: navigation resolves the FK index off the <c>ComponentTable</c>, and a
/// SingleVersion archetype is cluster-backed, so its field indexes live on the archetype with packed <c>ClusterLocation</c>
/// values and the component-table index is never populated. The branch is kept because it is the correct layout and the
/// cluster-aware FK path needs it; it goes live when that path lands.
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
    /// Thrown for <see cref="StorageMode.Transient"/> (no persistent index to navigate) and, for now, for
    /// <see cref="StorageMode.SingleVersion"/> — see the remarks on this type.
    /// </exception>
    internal static FkSourcePkResolver Create(ComponentTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.StorageMode == StorageMode.Transient)
        {
            throw new NotSupportedException(
                $"FK navigation is not supported on Transient component '{table.Name}' — Transient data has no persistent index to navigate.");
        }

        // Guard, not a capability statement: the read layouts below are correct for both modes, but navigation resolves the
        // FK index off the ComponentTable, and a SingleVersion archetype keeps its field indexes on the ARCHETYPE instead
        // (values are packed ClusterLocation, not chunk ids). The ComponentTable index is therefore empty and the reverse
        // lookup silently returns nothing — a wrong answer with no signal, which is worse than the NullReferenceException
        // this replaced. Fail loudly until the cluster-aware FK path lands.
        if (table.StorageMode == StorageMode.SingleVersion)
        {
            throw new NotSupportedException(
                $"FK navigation currently requires a Versioned source component; '{table.Name}' is SingleVersion. A SingleVersion archetype keeps its "
                + "field indexes on the archetype (cluster-local) rather than on the component table, so the FK reverse lookup has no index to scan. "
                + "Track: https://github.com/Log2n-io/Typhon/issues/623");
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
