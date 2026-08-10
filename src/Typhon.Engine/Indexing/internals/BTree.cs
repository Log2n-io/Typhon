// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//
//                                                   BASED ON https://github.com/MkazemAkhgary/BPlusTree
// Adapted :
//  - To allow multiple values per key
//  - Thread-safe
//  - Storage of data in a ChunkBasedSegment
//
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

namespace Typhon.Engine.Internals;

#region Chunk definitions

[Flags]
public enum NodeStates
{
    None     = 0x00,
    IsLeaf   = 0x02
}

#endregion

[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct BTreeHeader
{
    unsafe public static readonly int Size = sizeof(BTreeHeader);
    public static readonly int TotalSize =  ChunkBasedSegmentHeader.TotalSize + Size;
    public static readonly int Offset = ChunkBasedSegmentHeader.TotalSize;

    public int Count;
    public int RootChunkId;
}

/// <summary>
/// Header of the BTree directory stored at the start of chunk 0.
/// Tracks how many BTree entries are registered in this segment.
/// Directory chunks are zeroed on first reservation (<see cref="ChunkBasedSegment{TStore}.ReserveChunk(int, bool, ChangeSet)"/>),
/// so <see cref="EntryCount"/> == 0 reliably means "empty directory".
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct BTreeDirectoryHeader
{
    unsafe public static readonly int Size = sizeof(BTreeDirectoryHeader);

    public ushort EntryCount;
}

/// <summary>
/// One entry in the BTree directory (chunk 0). Each BTree on the segment gets a unique entry, keyed by the <see cref="BTreeStableKey"/> pair
/// (<see cref="StableId"/>, <see cref="Slot"/>).
/// </summary>
/// <remarks>12 bytes: short StableId + short Slot + int RootChunkId + int Count.</remarks>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct BTreeDirectoryEntry
{
    unsafe public static readonly int Size = sizeof(BTreeDirectoryEntry);

    /// <summary>Stable key: -1 for PK, Field.FieldId for secondary indexes, 0 for standalone/test BTrees.</summary>
    public short StableId;

    /// <summary>
    /// Component slot within the archetype, for the per-archetype index segments that several component slots share; 0 on a per-ComponentTable segment,
    /// which hosts one component's trees only. See <see cref="BTreeStableKey"/> for why the slot is part of the key (#657).
    /// </summary>
    public short Slot;

    public int RootChunkId;
    public int Count;
}

/// <summary>
/// Identity of one B+Tree within a shared segment's chunk-0 directory: the pair (<see cref="StableId"/>, <see cref="Slot"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StableId"/> alone is NOT unique on a per-archetype index segment. Field ids restart at 0 for every component, so two components in the same
/// archetype that each index their field #0 both register with StableId 0. Creation appends two distinct entries and works, but on reopen
/// <c>FindInDirectory</c> returns the FIRST match for both trees — they end up sharing one root, and one component's index silently resolves the other's
/// entities (#657). <see cref="Slot"/> — the component's slot in the archetype — disambiguates them.
/// </para>
/// <para>
/// Per-ComponentTable segments host a single component's trees, so they use slot 0 via the implicit conversion from <see cref="short"/>.
/// </para>
/// </remarks>
internal readonly struct BTreeStableKey : IEquatable<BTreeStableKey>
{
    /// <summary>-1 for PK, Field.FieldId for secondary indexes, 0 for standalone/test BTrees.</summary>
    public readonly short StableId;

    /// <summary>Component slot within the archetype; 0 when the segment is not shared across slots.</summary>
    public readonly short Slot;

    public BTreeStableKey(short stableId, short slot)
    {
        StableId = stableId;
        Slot = slot;
    }

    /// <summary>
    /// A tree on a segment it does not share with other component slots, addressed by field id alone.
    /// </summary>
    /// <remarks>
    /// The slot half of the key exists because one archetype's index segment hosts the trees of every component slot it
    /// carries, so <c>(fieldId, slot)</c> is what disambiguates them. This conversion is for the segments where that is
    /// not the case and slot 0 is the only occupant — it is NOT the "per-ComponentTable" case its previous summary
    /// named, which no longer exists (#629).
    /// </remarks>
    public static implicit operator BTreeStableKey(short stableId) => new(stableId, 0);

    public bool Equals(BTreeStableKey other) => StableId == other.StableId && Slot == other.Slot;

    public override bool Equals(object obj) => obj is BTreeStableKey other && Equals(other);

    public override int GetHashCode() => (Slot << 16) | (ushort)StableId;

    public override string ToString() => $"stableId {StableId}, slot {Slot}";
}

#region Misc Helpers

/// <summary>
/// provides some mathematical and numeric extensions.
/// </summary>
internal static class BTreeExtensions
{
    /// <summary>
    /// fast sign function that uses bitwise operations instead of branches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining|MethodImplOptions.AggressiveOptimization)]
    public static int Sign(this int x) => (x >> 31) | 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    unsafe internal static int BinarySearch<T>(T* array, int index, int length, T value, IComparer<T> comparer, int arrayStride) where T : unmanaged
    {
        int num1 = index;
        int num2 = index + length - 1;
        while (num1 <= num2)
        {
            int index1 = num1 + (num2 - num1 >> 1);
            int num3 = comparer.Compare(*(T*)((byte*)array + (arrayStride*index1)), value);
            if (num3 == 0)
            {
                return index1;
            }

            if (num3 < 0)
            {
                num1 = index1 + 1;
            }
            else
            {
                num2 = index1 - 1;
            }
        }
        return ~num1;
    }
}

#endregion

#region BTree+ main class

internal abstract partial class BTree<TKey, TStore> : BTreeBase<TStore> where TKey : unmanaged where TStore : struct, IPageStore
{
    [DebuggerDisplay("Key: {Key}, Value: {Value}")]
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyValueItem
    {
        public KeyValueItem(TKey key, int value)
        {
            Key = key;
            Value = value;
        }
        public TKey Key;
        public int Value;

        public static void ChangeKey(ref KeyValueItem item, TKey newKey) => item = new KeyValueItem(newKey, item.Value);

        public static void SwapKeys(ref KeyValueItem x, ref KeyValueItem y)
        {
            var xKey = x.Key;
            ChangeKey(ref x, y.Key);
            ChangeKey(ref y, xKey);
        }
    }

    public ref struct InsertArguments
    {
        public InsertArguments(TKey key, int value, IComparer<TKey> comparer, ref ChunkAccessor<TStore> accessor, ref ChunkAccessor<TStore> sibAccessor)
        {
            _value = value;
            _keyComparer = comparer ?? Comparer<TKey>.Default;
            Key = key;
            Added = false;
            ElementId = 0;
            Accessor = ref accessor;
            SiblingAccessor = ref sibAccessor;
        }
        public readonly TKey Key;
        public bool Added { get; private set; }

        public int ElementId;
        public int BufferRootId;

        public ref ChunkAccessor<TStore> Accessor;
        /// <summary>Dedicated accessor for horizontal (sibling) navigation — prevents sibling page loads from evicting parent path pages in the primary accessor.</summary>
        public ref ChunkAccessor<TStore> SiblingAccessor;

        private readonly int _value;
        private readonly IComparer<TKey> _keyComparer;

        public int GetValue()
        {
            Added = true;
            return _value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TKey left, TKey right)
        {
            // typeof(TKey) is a JIT intrinsic for value types — dead branches are eliminated at JIT time,
            // turning this into a direct comparison instead of an IComparer interface dispatch.
            if (typeof(TKey) == typeof(long))
            {
                var l = (long)(object)left;
                var r = (long)(object)right;
                return l.CompareTo(r);
            }
            if (typeof(TKey) == typeof(int))
            {
                var l = (int)(object)left;
                var r = (int)(object)right;
                return l.CompareTo(r);
            }
            if (typeof(TKey) == typeof(short))
            {
                var l = (short)(object)left;
                var r = (short)(object)right;
                return l.CompareTo(r);
            }
            return _keyComparer.Compare(left, right);
        }

        public IComparer<TKey> KeyComparer => _keyComparer;
    }

    public ref struct RemoveArguments
    {
        public readonly TKey Key;
        public readonly IComparer<TKey> Comparer;
        public ref ChunkAccessor<TStore> Accessor;
        /// <summary>Dedicated accessor for horizontal (sibling) navigation — prevents sibling page loads from evicting parent path pages in the primary accessor.</summary>
        public ref ChunkAccessor<TStore> SiblingAccessor;

        /// <summary>
        /// result is set once when the value is found at leaf node.
        /// </summary>
        public int Value { get; private set; }

        /// <summary>
        /// true if item is removed.
        /// </summary>
        public bool Removed { get; private set; }

        public RemoveArguments(in TKey key, in IComparer<TKey> comparer, ref ChunkAccessor<TStore> accessor, ref ChunkAccessor<TStore> sibAccessor)
        {
            Key = key;
            Comparer = comparer;

            Value = 0;
            Removed = false;
            Accessor = ref accessor;
            SiblingAccessor = ref sibAccessor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TKey left, TKey right)
        {
            if (typeof(TKey) == typeof(long))
            {
                var l = (long)(object)left;
                var r = (long)(object)right;
                return l.CompareTo(r);
            }
            if (typeof(TKey) == typeof(int))
            {
                var l = (int)(object)left;
                var r = (int)(object)right;
                return l.CompareTo(r);
            }
            if (typeof(TKey) == typeof(short))
            {
                var l = (short)(object)left;
                var r = (short)(object)right;
                return l.CompareTo(r);
            }
            return Comparer.Compare(left, right);
        }

        public void SetRemovedValue(int value)
        {
            Value = value;
            Removed = true;
        }
    }

    /// <summary>
    /// contains information about relatives of each node, such as ancestors and siblings.
    /// this information is used for borrow and spill operations.
    /// Siblings are lazily resolved to avoid loading chunks that are only needed during
    /// split/merge/spill/borrow operations (~5% of inserts).
    /// </summary>
    public struct NodeRelatives
    {
        /*  Note: "/" is left pointer. "\" is right pointer.
         *
         *               [LeftAncestor][RightAncestor]
         *              /              \              \
         *       [LeftSibling]       [Node]     [RightSibling]
         *
         *
         *                    [LeftAncestor][...]
         *                   /              \    ...
         *                [X]       [RightAncestor]  ...
         *                   \     /               \
         *         [LeftSibling][Node]       [RightSibling]
         *
         *                      [RightAncestor][...]
         *                     /        \         ...
         *          [LeftAncestor]      [X][...]     ...
         *         /              \    /
         *   [LeftSibling]     [Node][RightSibling]
         */

        /// <summary>
        /// nearest ancestor of node and its left sibling.
        /// </summary>
        public readonly NodeWrapper LeftAncestor;

        /// <summary>
        /// parent or ancestor used to get right sibling.
        /// </summary>
        public readonly NodeWrapper RightAncestor;

        /// <summary>
        /// index of item in ancestor that shares left sibling.
        /// </summary>
        public readonly int LeftAncestorIndex;

        /// <summary>
        /// index of item in ancestor that shares right sibling.
        /// </summary>
        public readonly int RightAncestorIndex;

        /// <summary>
        /// if left sibling is sibling and not cousin
        /// </summary>
        public readonly bool HasTrueLeftSibling;

        /// <summary>
        /// if right sibling is sibling and not cousin
        /// </summary>
        public readonly bool HasTrueRightSibling;

        // Context for lazy cousin edge resolution (only set for edge children)
        private readonly NodeWrapper _cousinLeftSource;
        private readonly NodeWrapper _cousinRightSource;

        // Lazy-cached siblings
        private NodeWrapper _leftSibling;
        private NodeWrapper _rightSibling;
        private bool _leftLoaded;
        private bool _rightLoaded;

        private NodeRelatives(NodeWrapper leftAncestor, int leftAncestorIndex, bool hasTrueLeftSibling, NodeWrapper rightAncestor, int rightAncestorIndex, 
            bool hasTrueRightSibling, NodeWrapper cousinLeftSource, NodeWrapper cousinRightSource)
        {
            LeftAncestor = leftAncestor;
            LeftAncestorIndex = leftAncestorIndex;
            HasTrueLeftSibling = hasTrueLeftSibling;

            RightAncestor = rightAncestor;
            RightAncestorIndex = rightAncestorIndex;
            HasTrueRightSibling = hasTrueRightSibling;

            _cousinLeftSource = cousinLeftSource;
            _cousinRightSource = cousinRightSource;
            _leftSibling = default;
            _rightSibling = default;
            _leftLoaded = false;
            _rightLoaded = false;
        }

        /// <summary>
        /// Lazily resolves and returns the left sibling. Caches result on first access.
        /// For true siblings, reads from the ancestor node. For cousin edges, traverses
        /// the parent's left sibling to find the rightmost child.
        /// </summary>
        public NodeWrapper GetLeftSibling(ref ChunkAccessor<TStore> accessor)
        {
            if (!_leftLoaded)
            {
                _leftLoaded = true;
                _leftSibling = HasTrueLeftSibling ? 
                    LeftAncestor.GetChild(LeftAncestorIndex - 1, ref accessor) : _cousinLeftSource.IsValid ? _cousinLeftSource.GetLastChild(ref accessor) : default;
            }
            return _leftSibling;
        }

        /// <summary>
        /// Lazily resolves and returns the right sibling. Caches result on first access.
        /// For true siblings, reads from the ancestor node. For cousin edges, traverses
        /// the parent's right sibling to find the leftmost child.
        /// </summary>
        public NodeWrapper GetRightSibling(ref ChunkAccessor<TStore> accessor)
        {
            if (!_rightLoaded)
            {
                _rightLoaded = true;
                _rightSibling = HasTrueRightSibling ?
                    RightAncestor.GetChild(RightAncestorIndex, ref accessor) : _cousinRightSource.IsValid ? _cousinRightSource.GetFirstChild(ref accessor) : default;
            }
            return _rightSibling;
        }

        /// <summary>
        /// creates new relatives for child node.
        /// Ancestor fields are set eagerly (no chunk reads — just copies).
        /// Sibling fields are lazily resolved on first access via GetLeftSibling/GetRightSibling.
        /// </summary>
        public static void Create(NodeWrapper child, int index, NodeWrapper parent, int parentCount, ref NodeRelatives parentRelatives, out NodeRelatives res, ref ChunkAccessor<TStore> accessor, ref ChunkAccessor<TStore> sibAccessor)
        {

            // assign nearest ancestors between child and siblings.
            NodeWrapper leftAncestor, rightAncestor;
            int leftAncestorIndex, rightAncestorIndex;
            bool hasTrueLeftSibling, hasTrueRightSibling;
            NodeWrapper cousinLeftSource = default, cousinRightSource = default;

            if (index == -1) // if child is left most, use left cousin as left sibling.
            {
                leftAncestor = parentRelatives.LeftAncestor;
                leftAncestorIndex = parentRelatives.LeftAncestorIndex;
                hasTrueLeftSibling = false;
                // Cousin resolution uses sibling CA — prevents cousin page from evicting parent path pages in the primary CA
                cousinLeftSource = parentRelatives.GetLeftSibling(ref sibAccessor);

                rightAncestor = parent;
                rightAncestorIndex = index + 1;
                hasTrueRightSibling = true;
            }
            else if (index == parentCount - 1) // if child is right most, use right cousin as right sibling.
            {
                leftAncestor = parent;
                leftAncestorIndex = index;
                hasTrueLeftSibling = true;

                rightAncestor = parentRelatives.RightAncestor;
                rightAncestorIndex = parentRelatives.RightAncestorIndex;
                hasTrueRightSibling = false;
                cousinRightSource = parentRelatives.GetRightSibling(ref sibAccessor);
            }
            else // child is not right most nor left most.
            {
                leftAncestor = parent;
                leftAncestorIndex = index;
                hasTrueLeftSibling = true;

                rightAncestor = parent;
                rightAncestorIndex = index + 1;
                hasTrueRightSibling = true;
            }

            res = new NodeRelatives(leftAncestor, leftAncestorIndex, hasTrueLeftSibling, rightAncestor, rightAncestorIndex, hasTrueRightSibling, 
                cousinLeftSource, cousinRightSource);
        }
    }

    #region Private data

    public abstract override bool AllowMultiple { get; }
    protected abstract BaseNodeStorage GetStorage();
    protected IComparer<TKey> Comparer;

    private readonly ChunkBasedSegment<TStore> _segment;
    private readonly BaseNodeStorage _storage;
    // Lightweight mutex protecting DeferredNodeList, which is accessed by concurrent merge operations.
    private SpinLock _deferredLock = new(false);

    // Per-instance count and root tracking used for ALL runtime operations.
    // Multiple BTrees can share the same ChunkBasedSegment<TStore> (an archetype's indexed fields share its index segment). Runtime code MUST use these
    // per-instance fields instead of reading from a single shared offset, which would cause cross-BTree corruption.
    // Each BTree has a unique entry in the chunk 0 directory, keyed by its BTreeStableKey (key + component slot).
    private int _count;

    // Cached last key for append fast-path: avoids reading ReverseLinkList chunk on sequential inserts.
    // TKey is unmanaged (value type), so no heap allocation.
    private TKey _cachedLastKey;
    private bool _hasCachedLastKey;

    // Epoch-deferred deallocation: nodes marked obsolete during merges are freed once all readers have exited.
    // Protected by _deferredLock for thread safety under concurrent merge operations.
    private DeferredNodeList _deferredNodes;

    // Batching counter for DeferredReclaim: only reclaim every 64 mutations to reduce MinActiveEpoch calls.
    // Non-atomic by design — racy reads are harmless (DeferredReclaim is idempotent, serialized by _deferredLock).
    private int _deferredReclaimSkip;

    // OLC diagnostics counters (always-on, only incremented on slow paths)
    internal long _optimisticRestarts;
    internal long _pessimisticFallbacks;
    internal long _writeLockFailures;
    internal long _splitCount;
    internal long _mergeCount;
    internal long _moveRightCount;
    internal long _contentionSplitCount;
    internal long _obsoleteRestarts;
    internal long _obsoleteSmoSiblingLocks;
    internal long _emptyInitRacesLost;

    internal const int MaxTreeDepth = 32;
    internal const int MaxOptimisticRestarts = 3;

    /// <summary>
    /// Bound on the PESSIMISTIC retry loops in <c>AddOrUpdateCorePessimistic</c> and <c>RemoveCorePessimistic</c>. Exhausting it throws.
    /// </summary>
    /// <remarks>
    /// Both loops were <c>while (true)</c> (#695). Their inner step returns "not completed" for a leaf whose <c>ReadVersion()</c> is 0, and that is 0 for a
    /// LOCKED leaf (transient — retrying is right) and for an OBSOLETE one (permanent — retrying can never help). Conflating them is what IXS-03 forbids, and
    /// its stated consequence is exactly what was observed: four threads spinning 24+ minutes with CPU climbing and no progress, after the WAL append, with
    /// no exception, no timeout and no way out but killing the process.
    /// <para>
    /// Not a contention limit. A legitimately contended operation completes in a handful of retries; this is three orders of magnitude above that, so
    /// reaching it means no amount of further retrying would have helped. Turning a permanent silent hang into a loud, diagnosable error is the same trade
    /// IX-03 makes elsewhere — the failure is silent and permanent, the exception is not.
    /// </para>
    /// </remarks>
    internal const int MaxPessimisticRestarts = 10_000;
    internal const int ContentionSplitThreshold = 3;

    #region OLC Path Buffers

    /// <summary>Stack-allocated int buffer for tree traversal path (max 32 levels).</summary>
    [InlineArray(MaxTreeDepth)]
    internal struct PathIntBuffer
    {
        private int _element0;
    }

    /// <summary>Stack-allocated NodeWrapper buffer for tree traversal path.</summary>
    [InlineArray(MaxTreeDepth)]
    internal struct PathNodesBuffer
    {
        private NodeWrapper _element0;
    }

    /// <summary>Stack-allocated NodeRelatives buffer for tree traversal path.</summary>
    [InlineArray(MaxTreeDepth)]
    internal struct PathRelativesBuffer
    {
        private NodeRelatives _element0;
    }

    /// <summary>
    /// Stack-allocated traversal context for a single BTree operation.
    /// Replaces instance-level path arrays that were protected by the whole-tree lock.
    /// ~4KB on the stack per mutation. PathVersions adds 128 bytes (32 x 4) for OLC validation.
    /// </summary>
    internal ref struct MutationContext
    {
        public PathRelativesBuffer PathRelatives;
        public PathNodesBuffer PathNodes;
        public PathIntBuffer PathChildIndices;
        public PathIntBuffer PathVersions;     // OLC version snapshots for validation
        public int Depth;
    }

    #endregion

    #region Epoch-Deferred Deallocation

    /// <summary>
    /// Tracks nodes marked obsolete during merges for epoch-deferred deallocation.
    /// Inline buffer of 8 entries covers typical case; overflows to List for cascading merges.
    /// All access must be under _deferredLock (via DeferredAdd / DeferredReclaim).
    /// </summary>
    internal struct DeferredNodeList
    {
        private struct Entry
        {
            public int ChunkId;
            public long RetireEpoch;
        }

        [InlineArray(8)]
        private struct EntryBuffer
        {
            private Entry _element0;
        }

        private EntryBuffer _entries;
        private int _inlineCount;
        private List<Entry> _overflow;

        /// <summary>Record a chunk for deferred deallocation at the given epoch.</summary>
        public void Add(int chunkId, long retireEpoch)
        {
            if (_inlineCount < 8)
            {
                _entries[_inlineCount] = new Entry { ChunkId = chunkId, RetireEpoch = retireEpoch };
                _inlineCount++;
            }
            else
            {
                _overflow ??= new List<Entry>();
                _overflow.Add(new Entry { ChunkId = chunkId, RetireEpoch = retireEpoch });
            }
        }

        /// <summary>
        /// Free nodes whose retire epoch is strictly less than safeEpoch (meaning all threads that could have observed the node have since exited their epoch scope).
        /// </summary>
        public void Reclaim(ChunkBasedSegment<TStore> segment, long safeEpoch)
        {
            // Reclaim from inline buffer (compact in-place)
            int write = 0;
            for (int read = 0; read < _inlineCount; read++)
            {
                if (_entries[read].RetireEpoch < safeEpoch)
                {
                    segment.FreeChunk(_entries[read].ChunkId);
                }
                else
                {
                    if (write != read)
                    {
                        _entries[write] = _entries[read];
                    }
                    write++;
                }
            }
            _inlineCount = write;

            // Reclaim from overflow list
            if (_overflow is { Count: > 0 })
            {
                for (int i = _overflow.Count - 1; i >= 0; i--)
                {
                    if (_overflow[i].RetireEpoch < safeEpoch)
                    {
                        segment.FreeChunk(_overflow[i].ChunkId);
                        _overflow.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Number of deferred entries pending reclamation.</summary>
        public readonly int Count => _inlineCount + (_overflow?.Count ?? 0);
    }

    #endregion

    // Optimization: warm ChunkAccessor<TStore> for exclusive operations. Reused across exclusive-lock calls to avoid per-operation creation (~15ns) and keep the
    // Cached location of this BTree's entry in the chunk 0 directory.
    // Computed once at construction, used by SyncHeader for O(1) writes.
    private int _dirChunkId;
    private int _dirEntryOffset;

    // DirectoryChunkCount hoisted to BTreeBase<TStore> (inherited here) so the torn-safe ClearSharedSegment helper can reference it without a TKey.

    public bool IsEmpty() => _count == 0;

    public override int EntryCount => _count;

    /// <summary>Number of deferred nodes pending reclamation (test visibility).</summary>
    internal int DeferredNodeCount => _deferredNodes.Count;

    /// <summary>Number of OLC optimistic read restarts (version validation failures).</summary>
    public long OptimisticRestarts => Interlocked.Read(ref _optimisticRestarts);

    /// <summary>Number of fallbacks from optimistic to pessimistic path.</summary>
    public long PessimisticFallbacks => Interlocked.Read(ref _pessimisticFallbacks);

    /// <summary>Number of SpinWriteLock spin iterations (contention on write locks).</summary>
    public long WriteLockFailures => Interlocked.Read(ref _writeLockFailures);

    /// <summary>Number of node splits (leaf + internal).</summary>
    public long SplitCount => Interlocked.Read(ref _splitCount);

    /// <summary>Number of node merges (leaf + internal).</summary>
    public long MergeCount => Interlocked.Read(ref _mergeCount);

    /// <summary>Number of move-right operations during insert (B-link following).</summary>
    public long MoveRightCount => Interlocked.Read(ref _moveRightCount);

    /// <summary>Number of contention splits (proactive splits of hot leaves).</summary>
    public long ContentionSplitCount => Interlocked.Read(ref _contentionSplitCount);

    /// <summary>
    /// Number of times a pessimistic writer refused a node a concurrent SMO had detached, and restarted its descent instead of writing into it (IXW-02, #716).
    /// Non-zero is HEALTHY — it is the defect being caught. It was zero before the fix only because the write went through.
    /// </summary>
    public long ObsoleteRestarts => Interlocked.Read(ref _obsoleteRestarts);

    /// <summary>
    /// Number of times the pessimistic insert path lost the race to publish the first root and freed its own node instead of overwriting the winner's (#679).
    /// Non-zero is HEALTHY — it is the defect being caught. Before the CAS it was unreachable by construction, because the overwrite simply went through:
    /// the winner's root was orphaned with the key it held, and <c>Height</c> was left one too high for the life of the tree.
    /// </summary>
    public long EmptyInitRacesLost => Interlocked.Read(ref _emptyInitRacesLost);

    /// <summary>
    /// Number of times a latch-coupled SMO sibling lock was taken on an already-obsolete node — the residual IXW-02 does NOT cover, because those four sites are
    /// mid-algorithm with no restart point. Expected 0: both phases hold the sibling's parent lock, so only a COUSIN could get here. Non-zero means the
    /// cousin case is real and the sibling has to become droppable.
    /// </summary>
    public long ObsoleteSmoSiblingLocks => Interlocked.Read(ref _obsoleteSmoSiblingLocks);

    internal void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _optimisticRestarts, 0);
        Interlocked.Exchange(ref _pessimisticFallbacks, 0);
        Interlocked.Exchange(ref _writeLockFailures, 0);
        Interlocked.Exchange(ref _splitCount, 0);
        Interlocked.Exchange(ref _mergeCount, 0);
        Interlocked.Exchange(ref _moveRightCount, 0);
        Interlocked.Exchange(ref _contentionSplitCount, 0);
        Interlocked.Exchange(ref _obsoleteRestarts, 0);
        Interlocked.Exchange(ref _obsoleteSmoSiblingLocks, 0);
        Interlocked.Exchange(ref _emptyInitRacesLost, 0);
    }

    /// <summary>
    /// Returns an enumerator that walks the leaf-level linked list, yielding all entries in ascending key order.
    /// The caller must be inside an epoch scope. Uses per-leaf OLC validation (lock-free for readers).
    /// </summary>
    public RangeEnumerator EnumerateLeaves() => new RangeEnumerator(this);

    /// <summary>
    /// Returns an enumerator that yields entries in ascending key order within [<paramref name="minKey"/>, <paramref name="maxKey"/>].
    /// For unique indexes only — throws on AllowMultiple. Use <see cref="EnumerateRangeMultiple"/> for AllowMultiple indexes.
    /// The caller must be inside an epoch scope. Uses per-leaf OLC validation (lock-free for readers).
    /// </summary>
    public RangeEnumerator EnumerateRange(TKey minKey, TKey maxKey)
    {
        if (AllowMultiple)
        {
            ThrowHelper.ThrowEnumerateRangeOnAllowMultiple();
        }
        return new RangeEnumerator(this, minKey, maxKey);
    }

    /// <summary>
    /// Returns an enumerator that yields entries in descending key order within [<paramref name="minKey"/>, <paramref name="maxKey"/>].
    /// For unique indexes only — throws on AllowMultiple. Use <see cref="EnumerateRangeMultipleDescending"/> for AllowMultiple indexes.
    /// The caller must be inside an epoch scope. Uses per-leaf OLC validation (lock-free for readers).
    /// </summary>
    public RangeEnumerator EnumerateRangeDescending(TKey minKey, TKey maxKey)
    {
        if (AllowMultiple)
        {
            ThrowHelper.ThrowEnumerateRangeOnAllowMultiple();
        }
        return new RangeEnumerator(this, minKey, maxKey, true);
    }

    /// <summary>
    /// Returns an enumerator that yields keys with their expanded VSBS values in ascending key order within [<paramref name="minKey"/>, <paramref name="maxKey"/>].
    /// For AllowMultiple indexes only — throws on unique indexes. Use <see cref="EnumerateRange"/> for unique indexes.
    /// The caller must be inside an epoch scope.
    /// </summary>
    public RangeMultipleEnumerator EnumerateRangeMultiple(TKey minKey, TKey maxKey)
    {
        if (!AllowMultiple)
        {
            ThrowHelper.ThrowEnumerateRangeMultipleOnUnique();
        }
        return new RangeMultipleEnumerator(this, minKey, maxKey);
    }

    /// <summary>
    /// Returns an enumerator that yields keys with their expanded VSBS values in descending key order within [<paramref name="minKey"/>, <paramref name="maxKey"/>].
    /// For AllowMultiple indexes only — throws on unique indexes. Use <see cref="EnumerateRangeDescending"/> for unique indexes.
    /// The caller must be inside an epoch scope.
    /// </summary>
    public RangeMultipleEnumerator EnumerateRangeMultipleDescending(TKey minKey, TKey maxKey)
    {
        if (!AllowMultiple)
        {
            ThrowHelper.ThrowEnumerateRangeMultipleOnUnique();
        }
        return new RangeMultipleEnumerator(this, minKey, maxKey, true);
    }

    /// <summary>
    /// Returns the minimum key in the BTree. Single-threaded use only (engine init / selectivity estimation).
    /// </summary>
    public TKey GetMinKey()
    {
        if (_count == 0)
        {
            return default;
        }

        using var guard = EpochGuard.Enter(_segment.Store.EpochManager);
        var accessor = _segment.CreateChunkAccessor();
        try
        {
            return GetFirst(ref accessor).Key;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Returns the maximum key in the BTree. Single-threaded use only (engine init).
    /// </summary>
    public TKey GetMaxKey()
    {
        if (_count == 0)
        {
            return default;
        }

        using var guard = EpochGuard.Enter(_segment.Store.EpochManager);
        var accessor = _segment.CreateChunkAccessor();
        try
        {
            return GetLast(ref accessor).Key;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Compares two keys, bypassing the <see cref="IComparer{T}"/> interface dispatch for the primitive key types. Falls back to
    /// <paramref name="comparer"/> for everything else.
    /// </summary>
    /// <remarks>
    /// <c>typeof(TKey)</c> against a concrete type is a JIT intrinsic for value types, so every branch but one is eliminated when the generic is instantiated
    /// and this becomes a direct <c>CompareTo</c>. The same trick already lives inside <c>InsertArguments.Compare</c> and <c>RemoveArguments.Compare</c>; the
    /// leaf-authority guards sit on the same hot paths but are handed a bare <see cref="IComparer{T}"/>, so without this they would pay a virtual call per
    /// insert for a comparison the surrounding code does directly.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CompareKeys(TKey left, TKey right, IComparer<TKey> comparer)
    {
        if (typeof(TKey) == typeof(int))
        {
            return ((int)(object)left).CompareTo((int)(object)right);
        }
        if (typeof(TKey) == typeof(long))
        {
            return ((long)(object)left).CompareTo((long)(object)right);
        }
        if (typeof(TKey) == typeof(short))
        {
            return ((short)(object)left).CompareTo((short)(object)right);
        }
        if (typeof(TKey) == typeof(uint))
        {
            return ((uint)(object)left).CompareTo((uint)(object)right);
        }
        if (typeof(TKey) == typeof(ulong))
        {
            return ((ulong)(object)left).CompareTo((ulong)(object)right);
        }
        return comparer.Compare(left, right);
    }

    /// <summary>
    /// Converts a <typeparamref name="TKey"/> to <see cref="long"/> using the same encoding as
    /// <see cref="QueryResolverHelper.EncodeThreshold"/>. JIT eliminates dead branches for each concrete TKey.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long KeyToLong(TKey key)
    {
        if (typeof(TKey) == typeof(sbyte))
        {
            return (sbyte)(object)key;
        }
        if (typeof(TKey) == typeof(byte))
        {
            return (byte)(object)key;
        }
        if (typeof(TKey) == typeof(short))
        {
            return (short)(object)key;
        }
        if (typeof(TKey) == typeof(ushort))
        {
            return (ushort)(object)key;
        }
        if (typeof(TKey) == typeof(char))
        {
            return (char)(object)key;
        }
        if (typeof(TKey) == typeof(int))
        {
            return (int)(object)key;
        }
        if (typeof(TKey) == typeof(uint))
        {
            return (uint)(object)key;
        }
        if (typeof(TKey) == typeof(long))
        {
            return (long)(object)key;
        }
        if (typeof(TKey) == typeof(ulong))
        {
            return (long)(ulong)(object)key;
        }
        if (typeof(TKey) == typeof(float))
        {
            var f = (float)(object)key;
            return Unsafe.As<float, int>(ref f);
        }
        if (typeof(TKey) == typeof(double))
        {
            var d = (double)(object)key;
            return Unsafe.As<double, long>(ref d);
        }

        throw new NotSupportedException($"Key type {typeof(TKey).Name} is not supported for long encoding.");
    }

    /// <summary>
    /// Converts a long-encoded key (produced by <see cref="KeyToLong"/> or <see cref="QueryResolverHelper.EncodeThreshold"/>)
    /// back into a typed <typeparamref name="TKey"/>. JIT eliminates dead branches for each TKey instantiation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TKey LongToKey(long encoded)
    {
        if (typeof(TKey) == typeof(sbyte))
        {
            return (TKey)(object)(sbyte)encoded;
        }
        if (typeof(TKey) == typeof(byte))
        {
            return (TKey)(object)(byte)(ulong)encoded;
        }
        if (typeof(TKey) == typeof(short))
        {
            return (TKey)(object)(short)encoded;
        }
        if (typeof(TKey) == typeof(ushort))
        {
            return (TKey)(object)(ushort)(ulong)encoded;
        }
        if (typeof(TKey) == typeof(char))
        {
            return (TKey)(object)(char)(ulong)encoded;
        }
        if (typeof(TKey) == typeof(int))
        {
            return (TKey)(object)(int)encoded;
        }
        if (typeof(TKey) == typeof(uint))
        {
            return (TKey)(object)(uint)(ulong)encoded;
        }
        if (typeof(TKey) == typeof(long))
        {
            return (TKey)(object)encoded;
        }
        if (typeof(TKey) == typeof(ulong))
        {
            return (TKey)(object)(ulong)encoded;
        }
        if (typeof(TKey) == typeof(float))
        {
            var i = (int)encoded;
            return (TKey)(object)Unsafe.As<int, float>(ref i);
        }
        if (typeof(TKey) == typeof(double))
        {
            return (TKey)(object)Unsafe.As<long, double>(ref encoded);
        }

        throw new NotSupportedException($"Key type {typeof(TKey).Name} is not supported for long decoding.");
    }

    /// <inheritdoc/>
    public override long GetMinKeyAsLong() => _count == 0 ? 0L : KeyToLong(GetMinKey());

    /// <inheritdoc/>
    public override long GetMaxKeyAsLong() => _count == 0 ? 0L : KeyToLong(GetMaxKey());

    public int IncCount() => Interlocked.Increment(ref _count);

    public int DecCount() => Interlocked.Decrement(ref _count);

    /// <summary>
    /// Writes <c>_count</c> and <c>Root.ChunkId</c> to this BTree's directory entry in chunk 0 (or chained chunks 1-3).
    /// Each BTree on a shared segment has a unique entry so they don't collide.
    /// </summary>
    private unsafe void SyncHeader(ref ChunkAccessor<TStore> accessor)
    {
        var addr = accessor.GetChunkAddress(_dirChunkId, true);
        ref var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(addr + _dirEntryOffset);
        entry.Count = _count;
        entry.RootChunkId = _rootChunkId;
    }

    // Volatile root chunk ID: atomically readable by concurrent readers under OLC.
    // NodeWrapper is reconstructed on demand from _storage + _rootChunkId.
    private volatile int _rootChunkId;

    private NodeWrapper Root
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // Issue #297: do NOT cache isLeaf via `_height == 1`. _rootChunkId and _height are written separately during root split/collapse
        // (e.g., Root = ... ; Height++/--). A concurrent reader that lands between the two writes can observe new _rootChunkId + stale _height, and would cache
        // the wrong isLeaf value — causing the descent to treat a leaf root as internal, read user values as child chunk-ids, and crash with bogus reads.
        // Pay the extra chunk read on the descent's first GetIsLeaf instead.
        get => _rootChunkId == 0 ? default : new NodeWrapper(_storage, _rootChunkId);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _rootChunkId = value.IsValid ? value.ChunkId : 0;
    }

    private NodeWrapper _linkList;
    private NodeWrapper _reverseLinkList;

    /// <summary>
    /// Structural handles for tests only: the root, and the head of the leaf chain. Never call these from engine code.
    /// </summary>
    /// <remarks>
    /// They exist so the <c>[RuleMutant]</c> tests can BREAK a tree on purpose and require the consistency validators to notice. Without a way to author a
    /// violating tree, every one of those validators is a green light nobody has ever seen turn red — which is the exact state #765 found the whole checker in,
    /// and is worth strictly less than no check at all because it also stops anyone looking. Read-only handles: mutation goes through <c>NodeWrapper</c>'s own
    /// API, so this widens visibility, not capability.
    /// </remarks>
    internal NodeWrapper DiagnosticRoot => Root;

    /// <inheritdoc cref="DiagnosticRoot"/>
    internal NodeWrapper DiagnosticLeafChainHead => _linkList;

    // Volatile height: atomically readable by concurrent readers under OLC.
    // Only modified under exclusive lock; volatile prevents compiler reordering for readers.
    private volatile int _height;

    public int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _height;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _height = value;
    }

    protected KeyValueItem GetFirst(ref ChunkAccessor<TStore> accessor) => _linkList.GetFirst(ref accessor);
    protected KeyValueItem GetLast(ref ChunkAccessor<TStore> accessor) => _reverseLinkList.GetLast(ref accessor);

    #endregion

    #region Public API
    
    public override ChunkBasedSegment<TStore> Segment => _segment;

    protected BTree(ChunkBasedSegment<TStore> segment, bool load, BTreeStableKey key = default, ChangeSet changeSet = null)
    {
        Comparer = Comparer<TKey>.Default;
        _segment = segment;

        // ReSharper disable once VirtualMemberCallInConstructor
        _storage = GetStorage();
        _storage.Initialize(this, _segment);

        // Both create and load paths need a ChunkAccessor<TStore>, which requires an active epoch scope.
        // The BTree constructor may be called during DatabaseEngine init (outside any epoch scope),
        // so we enter one here. EpochGuard supports nesting, so this is a no-op if already in scope.
        using var guard = EpochGuard.Enter(_segment.Store.EpochManager);

        if (!load)
        {
            // Reserve chunks 0-3 for the BTree directory overflow entries.
            // Only clear content for chunks not yet allocated — subsequent BTrees sharing this
            // segment must NOT re-clear, as that would wipe existing directory entries.
            for (int i = 0; i < DirectoryChunkCount; i++)
            {
                if (!_segment.IsChunkAllocated(i))
                {
                    _segment.ReserveChunk(i, true, changeSet);
                }
            }

            // Register this BTree in the directory (append a new entry, cache its location)
            var accessor = _segment.CreateChunkAccessor(changeSet);
            try
            {
                RegisterInDirectory(key, ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        else
        {
            // Load path: find our entry in the directory by key, reconstruct per-instance state.
            var accessor = _segment.CreateChunkAccessor();
            try
            {
                FindInDirectory(key, ref accessor);

                if (_count > 0)
                {
                    // Equivalent to `Root` (which is also non-caching since #297) — kept explicit because we are about to compute and assign Height inside the
                    // loop below, so reading the property here (which was the historical cache source) would be misleading.
                    var rootNode = new NodeWrapper(_storage, _rootChunkId);

                    // Traverse the leftmost path to find Height and LinkList (leftmost leaf)
                    Height = 1;
                    var node = rootNode;
                    while (!node.GetIsLeaf(ref accessor))
                    {
                        node = node.GetLeft(ref accessor);
                        Height++;
                    }
                    _linkList = node;

                    // Traverse the rightmost path to find ReverseLinkList (rightmost leaf)
                    node = rootNode;
                    while (!node.GetIsLeaf(ref accessor))
                    {
                        node = node.GetLastChild(ref accessor);
                    }
                    _reverseLinkList = node;
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Appends a new entry to the chunk 0 directory for this BTree.
    /// Sets <c>_dirChunkId</c> and <c>_dirEntryOffset</c> for subsequent <see cref="SyncHeader"/> calls.
    /// </summary>
    /// <remarks>
    /// Rejects a key already present rather than appending a second entry for it. A duplicate would create fine — each instance caches its own entry
    /// location — and only surface on reopen, where <see cref="FindInDirectory"/> hands both trees the first entry's root. Failing at registration turns
    /// that latent, data-losing bug into an immediate, obvious one (#657).
    /// </remarks>
    private unsafe void RegisterInDirectory(BTreeStableKey key, ref ChunkAccessor<TStore> accessor)
    {
        var chunk0Addr = accessor.GetChunkAddress(0, true);
        ref var header = ref Unsafe.AsRef<BTreeDirectoryHeader>(chunk0Addr);

        // Directory chunks are zeroed on first reservation, so EntryCount is reliably 0 for a fresh segment.
        int entryIndex = header.EntryCount;
        int stride = _segment.Stride;
        var maxEntries = MaxDirectoryEntriesFor(stride);
        if (entryIndex >= maxEntries)
        {
            throw new InvalidOperationException($"Maximum number of BTree indexes per segment exceeded ({maxEntries} at stride {stride})");
        }

        if (TryFindEntry(key, entryIndex, stride, ref accessor, out _, out _))
        {
            throw new InvalidOperationException($"A BTree with key ({key}) is already registered in this segment's directory");
        }

        (_dirChunkId, _dirEntryOffset) = ComputeEntryLocation(entryIndex, stride);

        var entryChunkAddr = accessor.GetChunkAddress(_dirChunkId, true);
        ref var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(entryChunkAddr + _dirEntryOffset);
        entry.StableId = key.StableId;
        entry.Slot = key.Slot;
        entry.RootChunkId = 0;
        entry.Count = 0;

        header.EntryCount = (ushort)(entryIndex + 1);
    }

    /// <summary>
    /// Scans the chunk 0 directory for the entry matching <paramref name="key"/>.
    /// Populates <c>_dirChunkId</c>, <c>_dirEntryOffset</c>, <c>_count</c>, and <c>Root</c>.
    /// </summary>
    private unsafe void FindInDirectory(BTreeStableKey key, ref ChunkAccessor<TStore> accessor)
    {
        var chunk0Addr = accessor.GetChunkAddress(0);
        ref var header = ref Unsafe.AsRef<BTreeDirectoryHeader>(chunk0Addr);

        int totalEntries = header.EntryCount;
        if (!TryFindEntry(key, totalEntries, _segment.Stride, ref accessor, out var chunkId, out var offset))
        {
            throw new InvalidOperationException($"BTree with key ({key}) not found in directory (entries: {totalEntries})");
        }

        _dirChunkId = chunkId;
        _dirEntryOffset = offset;

        var entryChunkAddr = accessor.GetChunkAddress(chunkId);
        ref var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(entryChunkAddr + offset);
        _count = entry.Count;

        var rootChunkId = entry.RootChunkId;
        if (_count > 0 && rootChunkId > 0)
        {
            Root = new NodeWrapper(_storage, rootChunkId);
        }
    }

    /// <summary>
    /// Linear scan of the first <paramref name="totalEntries"/> directory entries for <paramref name="key"/>. O(trees on the segment) — bounded by
    /// <see cref="BTreeBase{TStore}.MaxDirectoryEntriesFor"/> and only ever walked at registration / load time, never on a data path.
    /// </summary>
    private static unsafe bool TryFindEntry(BTreeStableKey key, int totalEntries, int stride, ref ChunkAccessor<TStore> accessor,
        out int chunkId, out int offsetInChunk)
    {
        for (var i = 0; i < totalEntries; i++)
        {
            var (candidateChunkId, candidateOffset) = ComputeEntryLocation(i, stride);
            var entryChunkAddr = accessor.GetChunkAddress(candidateChunkId);
            ref var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(entryChunkAddr + candidateOffset);

            if (entry.StableId == key.StableId && entry.Slot == key.Slot)
            {
                chunkId = candidateChunkId;
                offsetInChunk = candidateOffset;
                return true;
            }
        }

        chunkId = 0;
        offsetInChunk = 0;
        return false;
    }

    /// <summary>
    /// Computes which directory chunk and byte offset a given entry index maps to.
    /// Chunk 0 gives up <see cref="BTreeDirectoryHeader"/> (2 bytes) to its header; chunks 1-3 are pure entry storage.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int chunkId, int offsetInChunk) ComputeEntryLocation(int entryIndex, int stride)
    {
        int entriesInChunk0 = (stride - BTreeDirectoryHeader.Size) / BTreeDirectoryEntry.Size;
        if (entryIndex < entriesInChunk0)
        {
            return (0, BTreeDirectoryHeader.Size + entryIndex * BTreeDirectoryEntry.Size);
        }

        int entriesPerChunk = stride / BTreeDirectoryEntry.Size;
        int adjusted = entryIndex - entriesInChunk0;
        return (1 + adjusted / entriesPerChunk, (adjusted % entriesPerChunk) * BTreeDirectoryEntry.Size);
    }

    public override unsafe int Add(void* keyAddr, int value, ref ChunkAccessor<TStore> accessor) => Add(Unsafe.AsRef<TKey>(keyAddr), value, ref accessor, out _);
    public override unsafe int Add(void* keyAddr, int value, ref ChunkAccessor<TStore> accessor, out int bufferRootId)
        => Add(Unsafe.AsRef<TKey>(keyAddr), value, ref accessor, out bufferRootId);
    public override unsafe bool Remove(void* keyAddr, out int value, ref ChunkAccessor<TStore> accessor)
        => Remove(Unsafe.AsRef<TKey>(keyAddr), out value, ref accessor);
    public override unsafe Result<int, BTreeLookupStatus> TryGet(void* keyAddr, ref ChunkAccessor<TStore> accessor)
        => TryGet(Unsafe.AsRef<TKey>(keyAddr), ref accessor);
    public override unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ref ChunkAccessor<TStore> accessor)
        => RemoveValue(Unsafe.AsRef<TKey>(keyAddr), elementId, value, ref accessor);
    public override unsafe VariableSizedBufferAccessor<int, TStore> TryGetMultiple(void* keyAddr, ref ChunkAccessor<TStore> accessor)
        => TryGetMultiple(Unsafe.AsRef<TKey>(keyAddr), ref accessor);
    public override unsafe bool Move(void* oldKeyAddr, void* newKeyAddr, int value, ref ChunkAccessor<TStore> accessor)
        => Move(Unsafe.AsRef<TKey>(oldKeyAddr), Unsafe.AsRef<TKey>(newKeyAddr), value, ref accessor);
    public override unsafe int MoveValue(void* oldKeyAddr, void* newKeyAddr, int elementId, int value,
        ref ChunkAccessor<TStore> accessor, out int oldHeadBufferId, out int newHeadBufferId)
        => MoveValue(Unsafe.AsRef<TKey>(oldKeyAddr), Unsafe.AsRef<TKey>(newKeyAddr), elementId, value,
            ref accessor, out oldHeadBufferId, out newHeadBufferId);

    public int Add(TKey key, int value, ref ChunkAccessor<TStore> accessor) => Add(key, value, ref accessor, out _);

    public int Add(TKey key, int value, ref ChunkAccessor<TStore> accessor, out int bufferRootId)
    {
        // The outer `using var` was adding a second EH region on a per-key hot path; we keep the inner try/finally for accessor return and rely on the body
        // being throw-free in practice.
        var scope = TyphonEvent.BeginBTreeInsert();

        // Per-operation accessor for thread safety under OLC (thread-local warm cache)
        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
        int elementId;
        try
        {
            // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw. AddOrUpdateCore + SyncHeader are engine-internal storage manipulation. If a future change
            // adds a throw path here, this site MUST be re-tagged to variant B — a throw silently drops the span AND leaves CurrentOpenSpanId dangling,
            // corrupting parent-linkage for every subsequent span on this thread.
            var args = new InsertArguments(key, value, Comparer, ref opAccessor, ref sibAccessor);
            AddOrUpdateCore(ref args);
            SyncHeader(ref opAccessor);
            bufferRootId = args.BufferRootId;
            elementId = args.ElementId;
            // PROFILING-SPAN-NO-THROW-END
        }
        finally
        {
            _segment.ReturnWarmSiblingAccessor();
            _segment.ReturnWarmAccessor();
        }
        scope.Dispose();
        return elementId;
    }

    public bool Remove(TKey key, out int value, ref ChunkAccessor<TStore> accessor)
    {
        var scope = TyphonEvent.BeginBTreeDelete();

        // Per-operation accessor for thread safety under OLC (thread-local warm cache)
        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
        bool removed;
        try
        {
            // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw. RemoveCore + SyncHeader are engine-internal storage manipulation. If a future change adds
            // a throw path, re-tag to variant B.
            var args = new RemoveArguments(key, Comparer, ref opAccessor, ref sibAccessor);
            RemoveCore(ref args);
            SyncHeader(ref opAccessor);
            value = args.Value;
            removed = args.Removed;
            // PROFILING-SPAN-NO-THROW-END
        }
        finally
        {
            _segment.ReturnWarmSiblingAccessor();
            _segment.ReturnWarmAccessor();
        }
        scope.Dispose();
        return removed;
    }

    /// <summary>
    /// Validates that every leaf sits at the same depth and that <see cref="Height"/> equals it. Returns <c>null</c> when the tree is sound, else a detail
    /// string. Test/diagnostic use only — walks without locks, so the caller must ensure no concurrent modification.
    /// </summary>
    /// <remarks>
    /// This runs BEFORE the recursive walk for a reason. <c>NodeWrapper.CheckConsistency</c>'s first assertion compares each node's leaf flag against the depth
    /// it was reached at, counting DOWN from <see cref="Height"/> — so a <see cref="Height"/> that is merely one too large makes every leaf trip it, and the
    /// walk aborts before a single separator, ordering rule or child pointer is examined. In #679 that turned a root published twice into the message
    /// "Mismatch node's Height 2 with True", which describes a leaf at the wrong level. The tree was perfectly balanced; only the scalar had drifted. Asserting
    /// the two facts separately means a drift reports as a drift and everything downstream still gets checked.
    /// </remarks>
    internal string ValidateLeafDepths(ref ChunkAccessor<TStore> accessor, int maxReported = 6)
    {
        var depths = new Dictionary<int, int>();
        var samples = new List<string>();
        int visited = 0;
        var stack = new Stack<(NodeWrapper Node, int Depth, string Path)>();
        if (Root.IsValid)
        {
            stack.Push((Root, 1, "root"));
        }

        while (stack.Count > 0)
        {
            // #765 S1: a cap that truncates the walk and then renders a verdict is a checker reporting PASSED on the part of the tree it never looked at.
            if (++visited > MaxNodesVisited)
            {
                return $"depth walk exceeded {MaxNodesVisited:N0} nodes — the tree is cyclic or impossibly large; no verdict is possible";
            }

            var (node, depth, path) = stack.Pop();
            if (!node.IsValid)
            {
                continue;
            }

            if (node.GetIsLeaf(ref accessor))
            {
                depths.TryGetValue(depth, out var seen);
                depths[depth] = seen + 1;
                if (seen == 0 && samples.Count < maxReported)
                {
                    samples.Add($"depth={depth} firstSuchLeaf={node.ChunkId} via[{path}]");
                }
                continue;
            }

            int count = node.GetCount(ref accessor);
            var left = node.GetLeft(ref accessor);
            if (left.IsValid)
            {
                stack.Push((left, depth + 1, path + " -> left"));
            }
            for (int i = 0; i < count; i++)
            {
                var child = node.GetChild(i, ref accessor);
                if (child.IsValid)
                {
                    stack.Push((child, depth + 1, path + $" -> [{i}]"));
                }
            }
        }

        if (depths.Count == 1 && depths.ContainsKey(Height))
        {
            return null;
        }

        var perDepth = new List<string>();
        foreach (var kv in depths)
        {
            perDepth.Add($"{kv.Key}:{kv.Value}");
        }
        perDepth.Sort(StringComparer.Ordinal);
        var what = depths.Count > 1 ? "leaves sit at differing depths" : $"Height={Height} disagrees with the real leaf depth";
        return $"{what} — leafDepths={{{string.Join(",", perDepth)}}}" + (samples.Count > 0 ? $" :: {string.Join(" ;; ", samples)}" : "");
    }

    /// <summary>
    /// Validates that no separator pointing at a LEAF exceeds that leaf's first key. Returns <c>null</c> when sound, else a detail string. Test/diagnostic use
    /// only — walks without locks.
    /// </summary>
    /// <remarks>
    /// A leaf's first key is the separator its parent holds, and #679's mode 1 broke that: an insert landing at slot 0 of an interior leaf lowered the first
    /// key BELOW the separator routing to it, so descent for the new key went left of the separator and never reached the leaf. The key was counted, sat in a
    /// correctly chained leaf, and was unreachable — and the recursive walk did not notice, because it only asserts the ordering bound
    /// <c>parentKey &lt;= childKeys</c>, which a too-high separator satisfies.
    /// <para>
    /// One-sided on purpose, and this is the part that is easy to get wrong: only <c>separator &gt; firstKey</c> loses keys. The opposite slack is the normal
    /// residue of a removal — deleting a leaf's first key raises its minimum above a separator nobody rewrites, and routing stays correct because a search in
    /// the gap still descends to this leaf and correctly finds nothing. Asserting equality instead fails a plain single-threaded delete test
    /// (<c>DeferredDeallocation_PinnedEpoch_DefersReclamation</c>: <c>separator=392 -> leaf firstKey=400</c>), which is how a check earns a suppression rather
    /// than a defect.
    /// </para>
    /// <para>
    /// Restricted to leaf children because an internal split promotes a median key that does not remain the child's first key; the relationship holds at the
    /// leaf boundary only.
    /// </para>
    /// </remarks>
    internal string ValidateLeafSeparators(ref ChunkAccessor<TStore> accessor, int maxReported = 6)
    {
        List<string> broken = null;
        int pairs = 0, visited = 0;
        var stack = new Stack<NodeWrapper>();
        if (Root.IsValid)
        {
            stack.Push(Root);
        }

        while (stack.Count > 0)
        {
            if (++visited > MaxNodesVisited)
            {
                return $"separator walk exceeded {MaxNodesVisited:N0} nodes — the tree is cyclic or impossibly large; no verdict is possible";
            }

            var node = stack.Pop();
            if (!node.IsValid || node.GetIsLeaf(ref accessor))
            {
                continue;
            }

            int count = node.GetCount(ref accessor);
            var left = node.GetLeft(ref accessor);
            if (left.IsValid)
            {
                stack.Push(left);
            }

            for (int i = 0; i < count; i++)
            {
                var child = node.GetChild(i, ref accessor);
                if (!child.IsValid)
                {
                    continue;
                }
                stack.Push(child);

                if (child.GetCount(ref accessor) == 0 || !child.GetIsLeaf(ref accessor))
                {
                    continue;
                }
                pairs++;

                var item = node.GetItem(i, ref accessor);
                var childFirst = child.GetFirst(ref accessor).Key;
                if (Comparer.Compare(item.Key, childFirst) > 0)
                {
                    broken ??= [];
                    if (broken.Count < maxReported)
                    {
                        broken.Add($"parent={node.ChunkId} slot={i}/{count}: separator={item.Key} is ABOVE leaf={child.ChunkId} "
                                 + $"firstKey={childFirst} (lastKey={child.GetLast(ref accessor).Key}) — keys in between route left of this leaf");
                    }
                }
            }
        }

        return broken == null
            ? null
            : $"{broken.Count}+ of {pairs} separator/leaf pair(s) route AROUND their leaf :: {string.Join(" ;; ", broken)}";
    }

    /// <summary>
    /// Validates that no leaf's <c>HighKey</c> reaches past its right sibling's first key. Returns <c>null</c> when sound, else a detail string.
    /// Test/diagnostic use only — walks without locks.
    /// </summary>
    /// <remarks>
    /// <c>HighKey</c> is the EXCLUSIVE upper bound the B-link protocol reads to decide "this key is past me, follow the right link". When it reaches ABOVE the
    /// next leaf's first key, every key in the overlap looks in-range for a leaf that does not hold it: the descent stops there, and the key is inserted out of
    /// order or reported missing. That is #679's mode 2 — <c>SplitRight</c> set the left node's HighKey to the right half's first key, and <c>InsertLeaf</c>
    /// then moved a smaller key into that half, lowering the very key HighKey had been pinned to. Nothing else in the consistency walk reads HighKey, so the
    /// bound the whole B-link descent steers by had no enforcement at all.
    /// <para>
    /// One-sided for the same reason as <see cref="ValidateLeafSeparators"/>: a HighKey left BELOW the next first key is the normal residue of a removal and
    /// costs at most a restart, never a key.
    /// </para>
    /// </remarks>
    internal string ValidateLeafHighKeys(ref ChunkAccessor<TStore> accessor, int maxReported = 6)
    {
        List<string> broken = null;
        int links = 0;
        var cur = _linkList;
        for (int guard = 0; cur.IsValid; guard++)
        {
            if (guard >= MaxNodesVisited)
            {
                return $"HighKey walk exceeded {MaxNodesVisited:N0} leaves — see ValidateLeafChain, which names the cycle; no verdict is possible";
            }

            var next = cur.GetNext(ref accessor);
            if (next.IsValid && cur.GetCount(ref accessor) > 0 && next.GetCount(ref accessor) > 0)
            {
                links++;
                var high = cur.GetHighKey(ref accessor);
                var nextFirst = next.GetFirst(ref accessor).Key;
                if (Comparer.Compare(high, nextFirst) > 0)
                {
                    broken ??= [];
                    if (broken.Count < maxReported)
                    {
                        broken.Add($"leaf={cur.ChunkId} highKey={high} reaches past next={next.ChunkId} firstKey={nextFirst} "
                                 + $"(leafLast={cur.GetLast(ref accessor).Key}) — keys in between stop at a leaf that does not hold them");
                    }
                }
            }
            cur = next;
        }

        return broken == null
            ? null
            : $"{broken.Count}+ of {links} leaf link(s) have a HighKey reaching past the next leaf :: {string.Join(" ;; ", broken)}";
    }

    /// <summary>
    /// Validates that the leaf sibling chain is a simple, doubly-consistent list: no node revisited, every back-pointer agreeing with the forward walk, and
    /// the tail matching <c>_reverseLinkList</c>. Returns <c>null</c> when sound, else a detail string. Test/diagnostic use only — walks without locks.
    /// </summary>
    /// <remarks>
    /// #679: every chain walk in this file and in the stress harness was written as <c>while (cur.IsValid)</c> or with a 1,000,000-iteration guard, so a cycle
    /// in the chain either hung the walker or was silently absorbed — including inside <see cref="CheckConsistency"/> itself, which is supposed to be the thing
    /// that catches this. A cycle IS reachable: it is what left writers walking the B-link move-right loop forever, and the reason the loop now carries a hop
    /// bound. Detect it explicitly and name the node, so the corruption is reported where it is rather than as a hang somewhere downstream.
    /// </remarks>
    internal string ValidateLeafChain(ref ChunkAccessor<TStore> accessor)
    {
        var seen = new HashSet<int>();
        var cur = _linkList;
        NodeWrapper prev = default;
        while (cur.IsValid)
        {
            if (!seen.Add(cur.ChunkId))
            {
                return $"leaf chain has a CYCLE: chunk {cur.ChunkId} revisited after {seen.Count} node(s), reached from {prev.ChunkId}";
            }

            var back = cur.GetPrevious(ref accessor);
            if (prev.IsValid ? (back.ChunkId != prev.ChunkId) : back.IsValid)
            {
                return $"leaf chain back-pointer disagrees at chunk {cur.ChunkId}: previous={back.ChunkId}, forward walk arrived from "
                     + $"{(prev.IsValid ? prev.ChunkId.ToString() : "head")}";
            }

            prev = cur;
            cur = cur.GetNext(ref accessor);
        }

        if (prev.IsValid && _reverseLinkList.IsValid && prev.ChunkId != _reverseLinkList.ChunkId)
        {
            return $"leaf chain tail is chunk {prev.ChunkId} but _reverseLinkList names {_reverseLinkList.ChunkId}";
        }

        return null;
    }

    /// <summary>
    /// The bound every structural walk in this file shares. Exceeding it is a FAILURE, never a silent truncation.
    /// </summary>
    /// <remarks>
    /// The walks used to be written <c>while (stack.Count > 0 &amp;&amp; visited &lt; 1_000_000)</c> and then rendered a verdict on whatever they had seen. A tree
    /// that trips this cap is either far larger than any test builds or structurally cyclic, and in both cases "PASSED" is the one answer that cannot be right.
    /// </remarks>
    private const int MaxNodesVisited = 1_000_000;

    /// <summary>
    /// Validates that every node's items are strictly ascending. Returns <c>null</c> when sound, else a detail string. Test/diagnostic use only.
    /// </summary>
    /// <remarks>
    /// The one property the SIMD key search actually depends on, and nothing checked it. <c>NodeWrapper.CheckConsistency</c> compares each item against the
    /// PARENT separator and pins only the endpoints, so a leaf holding <c>[1, 9, 3, 5, 12]</c> satisfies every assertion in this file: its first key is above the
    /// separator, its last is below the next one, and the chain ordering only ever reads <c>GetFirst</c> and <c>GetLast</c>. A binary or vectorised search over
    /// that node returns "not found" for keys that are present, which is the #297 symptom exactly, and no instrument could tell you the node was the reason.
    /// </remarks>
    internal string ValidateNodeKeyOrder(ref ChunkAccessor<TStore> accessor, int maxReported = 6)
    {
        List<string> broken = null;
        int visited = 0;
        var stack = new Stack<NodeWrapper>();
        if (Root.IsValid)
        {
            stack.Push(Root);
        }

        while (stack.Count > 0)
        {
            if (++visited > MaxNodesVisited)
            {
                return $"node walk exceeded {MaxNodesVisited:N0} nodes — the tree is cyclic or impossibly large; no verdict is possible";
            }

            var node = stack.Pop();
            if (!node.IsValid)
            {
                continue;
            }

            int count = node.GetCount(ref accessor);
            for (int i = 1; i < count; i++)
            {
                var previous = node.GetItem(i - 1, ref accessor).Key;
                var current = node.GetItem(i, ref accessor).Key;
                if (Comparer.Compare(previous, current) >= 0)
                {
                    broken ??= [];
                    if (broken.Count < maxReported)
                    {
                        broken.Add($"chunk={node.ChunkId} leaf={node.GetIsLeaf(ref accessor)} slot {i - 1}->{i}: {previous} then {current}");
                    }
                }
            }

            if (node.GetIsLeaf(ref accessor))
            {
                continue;
            }

            var left = node.GetLeft(ref accessor);
            if (left.IsValid)
            {
                stack.Push(left);
            }
            for (int i = 0; i < count; i++)
            {
                var child = node.GetChild(i, ref accessor);
                if (child.IsValid)
                {
                    stack.Push(child);
                }
            }
        }

        return broken == null ? null : $"{broken.Count}+ node(s) hold keys out of order :: {string.Join(" ;; ", broken)}";
    }

    /// <summary>
    /// Validates that the set of leaves reachable by descending from the root is exactly the set reachable by walking the sibling chain. Returns <c>null</c> when
    /// sound.
    /// </summary>
    /// <remarks>
    /// The two are separate structures maintained by separate code, and every defect in this subsystem's history has been one of them disagreeing with the other.
    /// A leaf on the chain but not under the root holds keys no descent can reach and only the B-link right-walk ever finds — #297's "present key reported
    /// missing", one stale hop away. A leaf under the root but not on the chain is invisible to every range scan while lookups still return its keys.
    /// </remarks>
    internal string ValidateDescentAndChainAgree(ref ChunkAccessor<TStore> accessor, int maxReported = 10)
    {
        var byDescent = new HashSet<int>();
        int visited = 0;
        var stack = new Stack<NodeWrapper>();
        if (Root.IsValid)
        {
            stack.Push(Root);
        }

        while (stack.Count > 0)
        {
            if (++visited > MaxNodesVisited)
            {
                return $"descent walk exceeded {MaxNodesVisited:N0} nodes — the tree is cyclic or impossibly large; no verdict is possible";
            }

            var node = stack.Pop();
            if (!node.IsValid)
            {
                continue;
            }
            if (node.GetIsLeaf(ref accessor))
            {
                byDescent.Add(node.ChunkId);
                continue;
            }

            int count = node.GetCount(ref accessor);
            var left = node.GetLeft(ref accessor);
            if (left.IsValid)
            {
                stack.Push(left);
            }
            for (int i = 0; i < count; i++)
            {
                var child = node.GetChild(i, ref accessor);
                if (child.IsValid)
                {
                    stack.Push(child);
                }
            }
        }

        var byChain = new HashSet<int>();
        var cur = _linkList;
        int chainSteps = 0;
        while (cur.IsValid)
        {
            if (++chainSteps > MaxNodesVisited)
            {
                return $"chain walk exceeded {MaxNodesVisited:N0} nodes — see ValidateLeafChain, which names the cycle";
            }
            byChain.Add(cur.ChunkId);
            cur = cur.GetNext(ref accessor);
        }

        var chainOnly = new List<int>();
        foreach (var id in byChain)
        {
            if (!byDescent.Contains(id))
            {
                chainOnly.Add(id);
            }
        }
        var descentOnly = new List<int>();
        foreach (var id in byDescent)
        {
            if (!byChain.Contains(id))
            {
                descentOnly.Add(id);
            }
        }

        if (chainOnly.Count == 0 && descentOnly.Count == 0)
        {
            return null;
        }

        chainOnly.Sort();
        descentOnly.Sort();
        return $"descent and chain disagree: {byDescent.Count} leaves by descent, {byChain.Count} on the chain"
             + (chainOnly.Count > 0 ? $" :: on the chain but unreachable by descent: {Join(chainOnly, maxReported)}" : "")
             + (descentOnly.Count > 0 ? $" :: reachable by descent but off the chain: {Join(descentOnly, maxReported)}" : "");
    }

    /// <summary>
    /// Validates that <c>EntryCount</c> equals the number of items actually present on the leaf chain. Returns <c>null</c> when sound.
    /// </summary>
    /// <remarks>
    /// <c>EntryCount</c> is maintained by <c>IncCount</c>/<c>Interlocked.Decrement</c> calls scattered across the write paths, and every one of them is a chance
    /// to count an insert that did not happen or miss one that did. It is also the number the tests assert on most often, so a drifted counter both hides a lost
    /// key and manufactures a phantom failure elsewhere. Comparing it against the materialised cardinality is the only way to know which of the two it is.
    /// </remarks>
    internal string ValidateEntryCountMatchesChain(ref ChunkAccessor<TStore> accessor)
    {
        long counted = 0;
        int steps = 0;
        var cur = _linkList;
        while (cur.IsValid)
        {
            if (++steps > MaxNodesVisited)
            {
                return $"chain walk exceeded {MaxNodesVisited:N0} nodes — see ValidateLeafChain, which names the cycle";
            }
            counted += cur.GetCount(ref accessor);
            cur = cur.GetNext(ref accessor);
        }

        return counted == EntryCount
            ? null
            : $"EntryCount is {EntryCount} but the leaf chain holds {counted} item(s) across {steps} leaf(s) — drift of {EntryCount - counted}";
    }

    /// <summary>
    /// Validates that no reachable node is left write-locked or marked obsolete once the tree is quiescent. Returns <c>null</c> when sound.
    /// </summary>
    /// <remarks>
    /// Only meaningful with no concurrent writer, which is exactly the state the stress fixtures are in when they check. A latch still held after every worker
    /// has joined is a write path that returned without unlocking, and the next writer to reach that node spins on it forever — the shape #695 was. Obsolete is
    /// equally terminal: the node is reachable from the root and every descent that touches it must restart, so the tree still answers, just never from here.
    /// </remarks>
    internal string ValidateNoLatchResidue(ref ChunkAccessor<TStore> accessor, int maxReported = 6)
    {
        List<string> stuck = null;
        int visited = 0;
        var stack = new Stack<NodeWrapper>();
        if (Root.IsValid)
        {
            stack.Push(Root);
        }

        while (stack.Count > 0)
        {
            if (++visited > MaxNodesVisited)
            {
                return $"latch walk exceeded {MaxNodesVisited:N0} nodes — the tree is cyclic or impossibly large; no verdict is possible";
            }

            var node = stack.Pop();
            if (!node.IsValid)
            {
                continue;
            }

            // ReadVersion answers 0 for locked OR obsolete; both are illegal at quiescence and the caller needs to know which, so report the raw word.
            if (node.GetLatch(ref accessor).ReadVersion() == 0)
            {
                stuck ??= [];
                if (stuck.Count < maxReported)
                {
                    stuck.Add($"chunk={node.ChunkId} leaf={node.GetIsLeaf(ref accessor)}");
                }
            }

            if (node.GetIsLeaf(ref accessor))
            {
                continue;
            }

            int count = node.GetCount(ref accessor);
            var left = node.GetLeft(ref accessor);
            if (left.IsValid)
            {
                stack.Push(left);
            }
            for (int i = 0; i < count; i++)
            {
                var child = node.GetChild(i, ref accessor);
                if (child.IsValid)
                {
                    stack.Push(child);
                }
            }
        }

        return stuck == null
            ? null
            : $"{stuck.Count}+ node(s) are still locked or obsolete with no writer running :: {string.Join(" ;; ", stuck)}";
    }

    private static string Join(List<int> ids, int max)
    {
        var take = Math.Min(max, ids.Count);
        var shown = string.Join(", ", ids.GetRange(0, take));
        return ids.Count > take ? $"{shown}, … (+{ids.Count - take} more)" : shown;
    }

    public override void CheckConsistency(ref ChunkAccessor<TStore> accessor)
    {
        // Recursive check from Root to leaf
        if (IsEmpty())
        {
            return;
        }

        // #679: three invariants the recursive walk below does NOT enforce, checked first because each one, when broken, either masks that walk or slips
        // straight through it.
        //   - depth/Height: the walk's first assertion derives each node's expected leaf-ness by counting DOWN from Height, so a Height one too large makes
        //     every leaf trip it and aborts before any structural check runs.
        //   - separator == leaf's first key: the walk only asserts the ORDERING bound parentKey <= childKeys, which a too-HIGH separator satisfies.
        //   - HighKey == next leaf's first key: nothing in the walk reads HighKey at all, so the bound the B-link descent steers by had no enforcement.
        // The first cost a long hunt in #297/#679 by reporting as "Mismatch node's Height 2 with True" — a leaf at the wrong level — on trees that were in
        // fact perfectly balanced. The other two were the mode 1 / mode 2 defects themselves, invisible to a check that reported "PASSED".
        // Chain first: everything below walks the chain, and the walks are unbounded — a cycle would hang the checker instead of failing it.
        var chainDetail = ValidateLeafChain(ref accessor);
        ConsistencyAssert(chainDetail == null, chainDetail);

        var depthDetail = ValidateLeafDepths(ref accessor);
        ConsistencyAssert(depthDetail == null, depthDetail);
        var separatorDetail = ValidateLeafSeparators(ref accessor);
        ConsistencyAssert(separatorDetail == null, separatorDetail);
        var highKeyDetail = ValidateLeafHighKeys(ref accessor);
        ConsistencyAssert(highKeyDetail == null, highKeyDetail);

        // #765 S1. Four properties the checks above cannot see, ordered cheapest-diagnosis-first so the message names the most specific thing that is wrong.
        // Intra-node ordering comes first because everything below it — every Find, every separator comparison, the SIMD search — assumes it and reports
        // nonsense without it. Then the two structures agreeing with each other, then the counter, then the latches.
        var keyOrderDetail = ValidateNodeKeyOrder(ref accessor);
        ConsistencyAssert(keyOrderDetail == null, keyOrderDetail);
        var reachabilityDetail = ValidateDescentAndChainAgree(ref accessor);
        ConsistencyAssert(reachabilityDetail == null, reachabilityDetail);
        var countDetail = ValidateEntryCountMatchesChain(ref accessor);
        ConsistencyAssert(countDetail == null, countDetail);
        var latchDetail = ValidateNoLatchResidue(ref accessor);
        ConsistencyAssert(latchDetail == null, latchDetail);

        // Debug/test-only: runs without locks (caller must ensure no concurrent modification)
        Root.CheckConsistency(default, NodeWrapper.CheckConsistencyParent.Root, Comparer, Height, ref accessor);

        // Check the linked link of leaves in forward
        NodeWrapper prev = default;
        var cur = _linkList;
        TKey prevValue = default;

        while (cur.IsValid)
        {
            if (cur != _linkList)
            {
                ConsistencyAssert(prev.GetNext(ref accessor) == cur, "Prev.Next doesn't link to current");
                ConsistencyAssert(cur.GetPrevious(ref accessor) == prev, "Cur.Previous doesn't link to previous");

                ConsistencyAssert(Comparer.Compare(prevValue, cur.GetFirst(ref accessor).Key) < 0,
                    $"Previous Node's first key '{prevValue}' should be less than current node's first key '{cur.GetFirst(ref accessor).Key}'.");
            }

            prevValue = cur.GetLast(ref accessor).Key;
            prev = cur;
            cur = cur.GetNext(ref accessor);
        }
        ConsistencyAssert(prev == _reverseLinkList, "Last Node of the forward chain doesn't match ReverseLinkList");

        // Check the linked link of leaves in reverse
        NodeWrapper next = default;
        cur = _reverseLinkList;
        TKey nextValue = default;

        while (cur.IsValid)
        {
            if (cur != _reverseLinkList)
            {
                ConsistencyAssert(next.GetPrevious(ref accessor) == cur, "Next.Previous doesn't link to current");
                ConsistencyAssert(cur.GetNext(ref accessor) == next, "Cur.Next doesn't link to next");

                ConsistencyAssert(Comparer.Compare(nextValue, cur.GetLast(ref accessor).Key) > 0,
                    $"Next Node's last key '{nextValue}' should be greater than current node's last key '{cur.GetLast(ref accessor).Key}'.");
            }

            nextValue = cur.GetFirst(ref accessor).Key;
            next = cur;
            cur = cur.GetPrevious(ref accessor);
        }
        ConsistencyAssert(next == _linkList, "Last Node of the reverse chain doesn't match LinkedList");
    }

    [ExcludeFromCodeCoverage]
    private static void ConsistencyAssert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Consistency check: {message}");
        }
    }

    public int this[TKey key]
    {
        get
        {
            ref var ca = ref _segment.RentWarmAccessor();
            try
            {
                var result = TryGet(key, ref ca);
                if (result.IsFailure)
                {
                    throw new KeyNotFoundException();
                }

                return result.Value;
            }
            finally
            {
                _segment.ReturnWarmAccessor();
            }
        }
    }

    public Result<int, BTreeLookupStatus> TryGet(TKey key, ref ChunkAccessor<TStore> accessor)
    {
        // OLC optimistic path: zero locks, zero writes to shared state
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(key, ref accessor);
            if (leafChunkId == 0)
            {
                if (IsEmpty())
                {
                    return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
                }
                Interlocked.Increment(ref _optimisticRestarts);
                continue; // restart
            }

            if (keyIndex < 0)
            {
                // Key not found — validate leaf version one more time
                var leaf = _storage.LoadNode(leafChunkId);
                var latch = leaf.GetLatch(ref accessor);
                if (latch.ValidateVersion(leafVersion))
                {
                    return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
                }
                Interlocked.Increment(ref _optimisticRestarts);
                continue; // leaf was modified — restart
            }

            // Key found — read value and validate
            var leafNode = _storage.LoadNode(leafChunkId);
            int value = leafNode.GetItem(keyIndex, ref accessor).Value;
            var leafLatch = leafNode.GetLatch(ref accessor);
            if (leafLatch.ValidateVersion(leafVersion))
            {
                return new Result<int, BTreeLookupStatus>(value);
            }
            // Value read may be stale — restart
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // Pessimistic fallback after MaxOptimisticRestarts
        Interlocked.Increment(ref _pessimisticFallbacks);
        return TryGetPessimistic(key, ref accessor);
    }

    private Result<int, BTreeLookupStatus> TryGetPessimistic(TKey key, ref ChunkAccessor<TStore> accessor)
    {
        // Unlimited OLC retries — guaranteed to complete as long as writers make progress
        SpinWait spin = default;
        while (true)
        {
            var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(key, ref accessor);
            if (leafChunkId == 0)
            {
                if (IsEmpty())
                {
                    return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
                }
                spin.SpinOnce();
                continue;
            }

            if (keyIndex < 0)
            {
                var leaf = _storage.LoadNode(leafChunkId);
                if (leaf.GetLatch(ref accessor).ValidateVersion(leafVersion))
                {
                    return new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
                }
                continue;
            }

            var leafNode = _storage.LoadNode(leafChunkId);
            int value = leafNode.GetItem(keyIndex, ref accessor).Value;
            if (leafNode.GetLatch(ref accessor).ValidateVersion(leafVersion))
            {
                return new Result<int, BTreeLookupStatus>(value);
            }
        }
    }

    public bool RemoveValue(TKey key, int elementId, int value, ref ChunkAccessor<TStore> accessor)
    {
        var scope = TyphonEvent.BeginBTreeDelete();

        // Per-operation accessor for thread safety under OLC (thread-local warm cache)
        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
        bool result = true;
        try
        {
            // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw. FindLeaf, latch lock/unlock, _storage.* and RemoveCorePessimistic are all engine-internal
            // storage manipulation.
            // FindLeaf traversal is safe under OLC: internal nodes are stable.
            // #716: FindLeaf can land on a leaf a concurrent merge has since detached. Writing into it would remove the value from a node unreachable from the
            // root — the removal reports success and the value stays visible. Re-descend instead; the retry is unbounded for the same reason
            // TryGetMultiplePessimistic's is: it terminates as long as writers make progress, and each pass sees a tree one merge closer to settled.
            NodeWrapper leaf;
            SpinWait descentSpin = default;
            while (true)
            {
                leaf = FindLeaf(key, out _, ref opAccessor);
                if (!leaf.IsValid)
                {
                    break;
                }
                // WriteLock leaf for consistent index and to prevent concurrent OLC modification
                leaf.PreDirtyForWrite(ref opAccessor);
                if (SpinWriteLock(leaf.GetLatch(ref opAccessor)) != WriteLockOutcome.Obsolete)
                {
                    break;
                }
                Interlocked.Increment(ref _obsoleteRestarts);
                descentSpin.SpinOnce(-1);
            }

            if (!leaf.IsValid)
            {
                result = false;
            }
            else
            {
                // Re-find under lock (index might have shifted due to concurrent OLC fast path remove)
                var index = leaf.Find(key, Comparer, ref opAccessor);
                if (index < 0)
                {
                    leaf.GetLatch(ref opAccessor).WriteUnlock();
                    result = false;
                }
                else
                {
                    var bufferId = leaf.GetItem(index, ref opAccessor).Value;
                    var res = _storage.RemoveFromBuffer(bufferId, elementId, value, ref sibAccessor);

                    // WriteUnlock leaf — buffer manipulation is done, version bumped for OLC readers
                    leaf.GetLatch(ref opAccessor).WriteUnlock();

                    if (res == -1)
                    {
                        result = false;
                    }
                    else if (res == 0)
                    {
                        // Remove the key if we no longer have values stored there.
                        var args = new RemoveArguments(key, Comparer, ref opAccessor, ref sibAccessor);
                        RemoveCorePessimistic(ref args);

                        if (args.Removed)
                        {
                            _storage.DeleteBuffer(args.Value, ref sibAccessor);
                        }

                        SyncHeader(ref opAccessor);
                    }
                }
            }
            // PROFILING-SPAN-NO-THROW-END
        }
        finally
        {
            _segment.ReturnWarmSiblingAccessor();
            _segment.ReturnWarmAccessor();
        }
        scope.Dispose();
        return result;
    }

    public VariableSizedBufferAccessor<int, TStore> TryGetMultiple(TKey key, ref ChunkAccessor<TStore> accessor)
    {
        // OLC optimistic path: zero locks, zero writes to shared state
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(key, ref accessor);
            if (leafChunkId == 0)
            {
                if (IsEmpty())
                {
                    return default;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                continue; // restart
            }

            if (keyIndex < 0)
            {
                var leaf = _storage.LoadNode(leafChunkId);
                var latch = leaf.GetLatch(ref accessor);
                if (latch.ValidateVersion(leafVersion))
                {
                    return default;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                continue; // leaf was modified — restart
            }

            // Key found — read buffer ID and validate
            var leafNode = _storage.LoadNode(leafChunkId);
            int bufferId = leafNode.GetItem(keyIndex, ref accessor).Value;
            var leafLatch = leafNode.GetLatch(ref accessor);
            if (leafLatch.ValidateVersion(leafVersion))
            {
                return _storage.GetBufferReadOnlyAccessor(bufferId, ref accessor);
            }
            // Buffer ID read may be stale — restart
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // Pessimistic fallback
        Interlocked.Increment(ref _pessimisticFallbacks);
        return TryGetMultiplePessimistic(key, ref accessor);
    }

    private VariableSizedBufferAccessor<int, TStore> TryGetMultiplePessimistic(TKey key, ref ChunkAccessor<TStore> accessor)
    {
        // Unlimited OLC retries — guaranteed to complete as long as writers make progress
        SpinWait spin = default;
        while (true)
        {
            var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(key, ref accessor);
            if (leafChunkId == 0)
            {
                if (IsEmpty())
                {
                    return default;
                }
                spin.SpinOnce();
                continue;
            }

            if (keyIndex < 0)
            {
                var leaf = _storage.LoadNode(leafChunkId);
                if (leaf.GetLatch(ref accessor).ValidateVersion(leafVersion))
                {
                    return default;
                }
                continue;
            }

            var leafNode = _storage.LoadNode(leafChunkId);
            var bufferId = leafNode.GetItem(keyIndex, ref accessor).Value;
            if (leafNode.GetLatch(ref accessor).ValidateVersion(leafVersion))
            {
                return _storage.GetBufferReadOnlyAccessor(bufferId, ref accessor);
            }
        }
    }

    #endregion

    #region Private API

    protected internal NodeWrapper AllocNode(NodeStates states, ref ChunkAccessor<TStore> accessor)
    {
        var node = new NodeWrapper(_storage, _segment.AllocateChunk(false, accessor.ChangeSet), (states & NodeStates.IsLeaf) != 0);
        _storage.InitializeNode(node, states, ref accessor);
        return node;
    }

    /// <summary>Outcome of a pessimistic write-lock acquisition.</summary>
    protected internal enum WriteLockOutcome : byte
    {
        /// <summary>Lock acquired with no contention.</summary>
        Acquired = 0,

        /// <summary>Lock acquired, but only after spinning — the node is contended.</summary>
        AcquiredContended = 1,

        /// <summary>
        /// NOT acquired. The node is obsolete: a concurrent SMO detached it, so it is never a legal write target and never becomes one (IXW-02, #716). The
        /// caller must restart its descent — waiting is waiting for something that cannot happen, which is how #695 livelocked.
        /// </summary>
        Obsolete = 2,
    }

    /// <summary>
    /// Spin-waits until the write lock is acquired. Counts contention spins for diagnostics.
    /// Returns <see cref="WriteLockOutcome.Obsolete"/> — WITHOUT the lock — when the node has been detached by a concurrent SMO.
    /// </summary>
    /// <remarks>
    /// Two-phase spin policy tuned for OLC latch hold times (~100-500 ns):
    /// Phase 1: Tight PAUSE loop (64 iterations, ~100 ns on Zen / ~2 μs on Skylake+) — covers
    ///          the common case of a leaf insert/remove completing on another core.
    /// Phase 2: SpinWait with Sleep(1) disabled — escalates to Yield/Sleep(0) for rare splits/merges
    ///          or SMT core-sharing, but never pays the 15 ms Windows timer-tick penalty.
    /// <para>
    /// The obsolete check sits in the spin, not before it, and that placement is the point. A node that is merely LOCKED right now may be locked by the very
    /// merge that is about to detach it, so a pre-check would read "not obsolete" and then spin forever once the merge unlocks with the bit set — since #716
    /// <see cref="OlcLatch.TryWriteLock"/> refuses that node for good. Re-testing inside the loop converts that permanent wait into a restart.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WriteLockOutcome SpinWriteLock(OlcLatch latch)
    {
        if (latch.TryWriteLock())
        {
            return WriteLockOutcome.Acquired; // no contention
        }

        // Phase 1: tight PAUSE spin — stays on-core, covers typical latch hold time + cross-core coherence
        for (int i = 0; i < 64; i++)
        {
            if (latch.IsObsolete)
            {
                return WriteLockOutcome.Obsolete;
            }
            Interlocked.Increment(ref _writeLockFailures);
            Thread.SpinWait(1);
            if (latch.TryWriteLock())
            {
                return WriteLockOutcome.AcquiredContended;
            }
        }

        // Phase 2: yield-capped SpinWait — holder is likely doing a split/merge or sharing our core.
        // sleep1Threshold: -1 disables Sleep(1) which would stall for ~15 ms (Windows timer tick).
        SpinWait spin = default;
        do
        {
            if (latch.IsObsolete)
            {
                return WriteLockOutcome.Obsolete;
            }
            Interlocked.Increment(ref _writeLockFailures);
            spin.SpinOnce(-1);
        }
        while (!latch.TryWriteLock());
        return WriteLockOutcome.AcquiredContended;
    }

    /// <summary>
    /// Spin-waits for the write lock on a node reached from a latch-coupled SMO's sibling resolution, where the caller is mid-algorithm and has no restart
    /// point. Admits an obsolete node — see <see cref="OlcLatch.TryWriteLockOnSmoPath"/> for why, and what bounds it — and counts the occurrence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SpinWriteLockOnSmoPath(OlcLatch latch)
    {
        bool acquired = latch.TryWriteLockOnSmoPath(out var wasObsolete);
        for (int i = 0; !acquired && i < 64; i++)
        {
            Interlocked.Increment(ref _writeLockFailures);
            Thread.SpinWait(1);
            acquired = latch.TryWriteLockOnSmoPath(out wasObsolete);
        }

        SpinWait spin = default;
        while (!acquired)
        {
            Interlocked.Increment(ref _writeLockFailures);
            spin.SpinOnce(-1);
            acquired = latch.TryWriteLockOnSmoPath(out wasObsolete);
        }

        if (wasObsolete)
        {
            Interlocked.Increment(ref _obsoleteSmoSiblingLocks);
        }
    }

    /// <summary>Thread-safe addition to the epoch-deferred node list (protected by _deferredLock).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DeferredAdd(int chunkId, long retireEpoch)
    {
        bool lockTaken = false;
        _deferredLock.Enter(ref lockTaken);
        try
        {
            _deferredNodes.Add(chunkId, retireEpoch);
        }
        finally
        {
            _deferredLock.Exit(false);
        }
    }

    /// <summary>Thread-safe reclamation of epoch-deferred nodes (protected by _deferredLock).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DeferredReclaim()
    {
        bool lockTaken = false;
        _deferredLock.Enter(ref lockTaken);
        try
        {
            _deferredNodes.Reclaim(_segment, _segment.Store.EpochManager.MinActiveEpoch);
        }
        finally
        {
            _deferredLock.Exit(false);
        }
    }

    /// <summary>Force-flush all pending deferred nodes, bypassing the batching counter. Test-only.</summary>
    internal void FlushDeferredNodes()
    {
        _deferredReclaimSkip = 0;
        DeferredReclaim();
    }

    private NodeWrapper FindLeaf(TKey key, out int index, ref ChunkAccessor<TStore> accessor)
    {
        index = -1;
        if (IsEmpty())
        {
            return default;
        }

        var node = Root;
        while (!node.GetIsLeaf(ref accessor))
        {
            node = node.GetNearestChild(key, Comparer, ref accessor);
        }
        index = node.Find(key, Comparer, ref accessor);

        // B-link: follow right links if key is beyond this leaf's range (stale parent separator).
        // Multiple hops may be needed when consecutive forceSplit operations left unpropagated separators.
        // The HighKey read is OLC-validated: during a concurrent split, HighKey is updated after the key array,
        // so a lock-free reader could see a stale HighKey and incorrectly break. If the node is write-locked or
        // the version changed between read and validate, we conservatively follow the right link.
        for (int hop = 0; hop < 16 && index < 0 && node.GetCount(ref accessor) > 0; hop++)
        {
            var latch = node.GetLatch(ref accessor);
            var version = latch.ReadVersion();
            if (version != 0 && Comparer.Compare(key, node.GetHighKey(ref accessor)) < 0 && latch.ValidateVersion(version))
            {
                break; // key is confirmed within this leaf's range (high key is stable)
            }

            var next = node.GetNext(ref accessor);
            if (!next.IsValid)
            {
                break;
            }

            node = next;
            index = node.Find(key, Comparer, ref accessor);
        }

        return node;
    }

    /// <summary>
    /// Optimistic descent from root to leaf using OLC version validation.
    /// Returns (leafChunkId, leafVersion, keyIndex). leafChunkId=0 signals restart needed.
    /// Zero writes to shared state — readers never acquire any lock.
    /// </summary>
    private (int leafChunkId, int leafVersion, int keyIndex) OptimisticDescendToLeaf(TKey key, ref ChunkAccessor<TStore> accessor, bool followRightLink = true)
    {
        var node = Root;
        if (!node.IsValid)
        {
            return (0, 0, -1);
        }

        var latch = node.GetLatch(ref accessor);
        int version = latch.ReadVersion();
        if (version == 0)
        {
            return (0, 0, -1); // locked or obsolete — restart
        }

        // Descend through internal nodes
        while (!node.GetIsLeaf(ref accessor))
        {
            var index = node.Find(key, Comparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1;
            }

            // Read child pointer
            var child = node.GetChild(index, ref accessor);

            // Validate parent version after reading child pointer
            if (!latch.ValidateVersion(version))
            {
                return (0, 0, -1); // parent was modified — restart
            }

            // Move to child
            if (!child.IsValid)
            {
                return (0, 0, -1); // invalid child — restart
            }
            OlcDescentTrace.RecordStep?.Invoke(OlcDescentTrace.OpDescend, node.ChunkId, version, index, child.ChunkId);
            node = child;
            latch = node.GetLatch(ref accessor);
            version = latch.ReadVersion();
            if (version == 0)
            {
                return (0, 0, -1); // locked or obsolete — restart
            }
        }

        // At leaf: search for key
        var keyIndex = node.Find(key, Comparer, ref accessor);

        // B-link right-link following: if key not found in this leaf, walk forward in the chain until we find the key or hit a leaf whose actual content places
        // key strictly within its range (key <= leaf.GetItem(count-1).Key). This is more robust than relying on the cached HighKey, which can be transiently
        // out-of-sync with the actual chain ordering/ under concurrent operations.
        if (followRightLink && keyIndex < 0)
        {
            // #679: every caller reads `keyIndex < 0` as "definitively not in the tree", so this loop must only end that way when it has actually ESTABLISHED
            // it. It had two exits that established nothing and still fell through to that answer:
            //   - the hop budget running out, and
            //   - meeting an empty leaf, which ended the loop via its own condition.
            // Both are reachable while the key sits further right, and the second is routine during merges: a leaf is emptied before the merge that unlinks it.
            // Measured in Remove_Merges — key 584 removed by nobody, still on the chain, with branch `general path (descend keyIndex<0)` the only one to fire.
            // The same descent backs TryGet, so the identical false "not found" was reachable on the READ path.
            // Empty leaves are now hopped OVER rather than treated as an answer, and an inconclusive exit restarts instead of lying.
            const int maxHops = 16;
            bool conclusive = false;
            for (int hop = 0; hop < maxHops; hop++)
            {
                int leafCount = node.GetCount(ref accessor);
                if (leafCount > 0)
                {
                    // key <= leaf's last → key would be in this leaf if anywhere on this side of the chain. Re-validate to guard against torn reads (key/last
                    // from inconsistent version snapshot), then conclude NotFound.
                    if (Comparer.Compare(key, node.GetItem(leafCount - 1, ref accessor).Key) <= 0)
                    {
                        if (!latch.ValidateVersion(version))
                        {
                            return (0, 0, -1);
                        }
                        conclusive = true;
                        break;
                    }
                }
                if (!latch.ValidateVersion(version))
                {
                    return (0, 0, -1);
                }

                var nextNode = node.GetNext(ref accessor);
                if (!nextNode.IsValid)
                {
                    conclusive = true; // chain exhausted — key is beyond every leaf, which IS an answer
                    break;
                }

                var nextLatch = nextNode.GetLatch(ref accessor);
                int nextVersion = nextLatch.ReadVersion();
                if (nextVersion == 0)
                {
                    return (0, 0, -1);
                }

                node = nextNode;
                latch = nextLatch;
                version = nextVersion;
                keyIndex = node.Find(key, Comparer, ref accessor);
                if (keyIndex >= 0)
                {
                    conclusive = true;
                    break;
                }
            }

            if (!conclusive)
            {
                return (0, 0, -1); // ran out of hops without settling the question — restart rather than report a not-found we did not establish
            }
        }

        // Validate final leaf version after reading
        if (!latch.ValidateVersion(version))
        {
            return (0, 0, -1); // leaf was modified during search — restart
        }

        return (node.ChunkId, version, keyIndex);
    }

    #endregion
}

#endregion