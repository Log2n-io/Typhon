// unset

namespace Typhon.Engine.Internals;

/// <summary>
/// Non-generic interface for B+Tree indexes. Provides the store-agnostic surface used by
/// <see cref="IndexedFieldInfo"/>, selectivity estimation, and query planning.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BTreeBase{TStore}"/> implements this interface, allowing a holder to keep indexes backed by either <see cref="PersistentStore"/> or
/// <see cref="TransientStore"/> without becoming generic itself — today that holder is <c>ClusterIndexField&lt;TStore&gt;</c> on the archetype; it was
/// <c>IndexedFieldInfo</c> on the ComponentTable until that home was removed (#629). Only cold-path properties are exposed here; hot-path operations
/// (Add, Remove, RangeScan, etc.) require casting to the concrete <see cref="BTreeBase{TStore}"/> type.
/// </para>
/// </remarks>
internal interface IBTreeIndex
{
    /// <summary>Number of leaf entries in the B+Tree.</summary>
    int EntryCount { get; }

    /// <summary>Whether this index allows multiple values per key.</summary>
    bool AllowMultiple { get; }

    /// <summary>Returns the minimum key encoded as a <see cref="long"/>.</summary>
    long GetMinKeyAsLong();

    /// <summary>Returns the maximum key encoded as a <see cref="long"/>.</summary>
    long GetMaxKeyAsLong();
}
