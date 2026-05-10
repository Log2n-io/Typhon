// unset

using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Internals;

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>
    /// Enumerates key-value entries in the BTree, optionally bounded by [minKey, maxKey], in ascending or descending order.
    /// When created via the unbounded constructor (used by <see cref="EnumerateLeaves"/>), walks the entire leaf-level linked list left to right.
    /// When created via the bounded constructor, seeks to the appropriate leaf and stops when the bound is exceeded.
    /// Direction is controlled by the <c>reverse</c> parameter: forward uses <see cref="EnumerateRange"/>, reverse uses
    /// <see cref="EnumerateRangeDescending"/>.
    /// </summary>
    /// <remarks>
    /// <para>Uses per-leaf OLC validation: reads a leaf's version before reading its entries, then validates after. If the leaf was concurrently modified,
    /// the enumerator re-reads that leaf from the beginning/end (not the whole tree).</para>
    /// <para>The caller must be inside an epoch scope (e.g., via a Transaction).</para>
    /// <para>Supports <c>foreach</c> via duck-typing (GetEnumerator/MoveNext/Current/Dispose).</para>
    /// </remarks>
    public ref struct RangeEnumerator
    {
        private ChunkAccessor<TStore> _accessor;
        private NodeWrapper _currentNode;
        private int _currentIndex;
        private int _nodeItemCount;
        private int _leafVersion;
        private bool _disposed;
        private readonly IComparer<TKey> _comparer;
        private readonly TKey _boundKey;
        private readonly bool _bounded;

        // Phase 6: Data:Index:BTree:RangeScan span (Tier-2 gated). ResultCount/RestartCount filled during enumeration.
        private DataIndexBTreeRangeScanEvent _span;
        private readonly bool _reverse;

        /// <summary>Unbounded forward constructor — walks the entire leaf chain (used by <see cref="EnumerateLeaves"/>).</summary>
        internal RangeEnumerator(BTree<TKey, TStore> tree)
        {
            _accessor = tree._segment.CreateChunkAccessor();
            _comparer = tree.Comparer;
            _bounded = false;
            _reverse = false;
            _boundKey = default;
            _currentNode = tree._linkList;
            _currentIndex = -1;
            _disposed = false;

            if (_currentNode.IsValid)
            {
                ReadLeafState();
            }
            else
            {
                _nodeItemCount = 0;
                _leafVersion = 0;
            }

            _span = TyphonEvent.BeginDataIndexBTreeRangeScan();
        }

        /// <summary>
        /// Bounded constructor — seeks to the appropriate endpoint and iterates toward the bound.
        /// Forward (<paramref name="reverse"/>=false): seeks to <paramref name="minKey"/>, stops at <paramref name="maxKey"/>.
        /// Reverse (<paramref name="reverse"/>=true): seeks to <paramref name="maxKey"/>, stops at <paramref name="minKey"/>.
        /// </summary>
        internal RangeEnumerator(BTree<TKey, TStore> tree, TKey minKey, TKey maxKey, bool reverse = false)
        {
            _accessor = tree._segment.CreateChunkAccessor();
            _comparer = tree.Comparer;
            _bounded = true;
            _reverse = reverse;
            _boundKey = reverse ? minKey : maxKey;
            _disposed = false;
            _currentIndex = -1;
            _nodeItemCount = 0;
            _leafVersion = 0;

            // Inverted range or empty tree — yield nothing
            if (_comparer.Compare(minKey, maxKey) > 0 || tree.IsEmpty())
            {
                _currentNode = default;
                return;
            }

            // Seek to the leaf containing the start key (pessimistic descent)
            var seekKey = reverse ? maxKey : minKey;
            _currentNode = tree.FindLeaf(seekKey, out int index, ref _accessor);
            if (!_currentNode.IsValid)
            {
                return;
            }

            if (reverse)
            {
                InitReverse(index);
            }
            else
            {
                InitForward(index);
            }

            if (_currentNode.IsValid)
            {
                ReadLeafState();
                // Fix sentinel: if reverse moved to previous leaf, start from its last item
                if (_reverse && _currentIndex == -2)
                {
                    _currentIndex = _nodeItemCount;
                }
            }

            _span = TyphonEvent.BeginDataIndexBTreeRangeScan();
        }

        /// <summary>Positions the cursor for forward iteration starting at the leaf containing minKey.</summary>
        private void InitForward(int index)
        {
            if (index >= 0)
            {
                // Exact match — position one before so MoveNext() lands on it
                _currentIndex = index - 1;
            }
            else
            {
                int insertionPoint = ~index;
                int count = _currentNode.GetCount(ref _accessor);
                if (insertionPoint >= count)
                {
                    // All keys in this leaf < minKey — advance to next leaf
                    _currentNode = _currentNode.GetNext(ref _accessor);
                    // _currentIndex stays -1; MoveNext will increment to 0
                }
                else
                {
                    // First key >= minKey is at insertionPoint
                    _currentIndex = insertionPoint - 1;
                }
            }
        }

        /// <summary>Positions the cursor for reverse iteration starting at the leaf containing maxKey.</summary>
        private void InitReverse(int index)
        {
            if (index >= 0)
            {
                // Exact match — position one after so MoveNext() (which decrements) lands on it
                _currentIndex = index + 1;
            }
            else
            {
                // ~index is the insertion point (first key > maxKey)
                int startAt = ~index - 1; // last key <= maxKey
                if (startAt < 0)
                {
                    // All keys in this leaf > maxKey — go to previous leaf
                    _currentNode = _currentNode.GetPrevious(ref _accessor);
                    // Sentinel -2: ReadLeafState will set _nodeItemCount, then constructor fixes to _nodeItemCount
                    _currentIndex = -2;
                }
                else
                {
                    // Position one after so MoveNext() lands on startAt
                    _currentIndex = startAt + 1;
                }
            }
        }

        /// <summary>Reads the current leaf's version and item count under OLC.</summary>
        private void ReadLeafState()
        {
            while (true)
            {
                var latch = _currentNode.GetLatch(ref _accessor);
                var version = latch.ReadVersion();
                if (version == 0)
                {
                    // Locked or obsolete — spin-wait and retry
                    Thread.SpinWait(1);
                    continue;
                }

                _nodeItemCount = _currentNode.GetCount(ref _accessor);

                if (latch.ValidateVersion(version))
                {
                    _leafVersion = version;
                    return;
                }
                // Version changed — retry this leaf
            }
        }

        /// <summary>Returns this enumerator (required for foreach pattern).</summary>
        public RangeEnumerator GetEnumerator() => this;

        /// <summary>Gets the current key-value item.</summary>
        public KeyValueItem Current => _currentNode.GetItem(_currentIndex, ref _accessor);

        /// <summary>Advances to the next entry in iteration order, traversing leaf nodes as needed.</summary>
        public bool MoveNext()
        {
            if (!_currentNode.IsValid)
            {
                return false;
            }

            // Advance within current leaf
            if (_reverse)
            {
                _currentIndex--;
                if (_currentIndex >= 0)
                {
                    var item = _currentNode.GetItem(_currentIndex, ref _accessor);
                    if (_comparer.Compare(item.Key, _boundKey) < 0)
                    {
                        return false;
                    }
                    if (TelemetryConfig.DataIndexBTreeRangeScanActive)
                    {
                        _span.ResultCount++;
                    }

                    return true;
                }
            }
            else
            {
                _currentIndex++;
                if (_currentIndex < _nodeItemCount)
                {
                    if (_bounded)
                    {
                        var item = _currentNode.GetItem(_currentIndex, ref _accessor);
                        if (_comparer.Compare(item.Key, _boundKey) > 0)
                        {
                            return false;
                        }
                    }
                    if (TelemetryConfig.DataIndexBTreeRangeScanActive)
                    {
                        _span.ResultCount++;
                    }

                    return true;
                }
            }

            // Before following the next/previous pointer, validate the leaf version.
            // If the leaf was modified during our scan, re-read and restart from the appropriate end.
            var latch = _currentNode.GetLatch(ref _accessor);
            if (!latch.ValidateVersion(_leafVersion))
            {
                if (TelemetryConfig.DataIndexBTreeRangeScanActive && _span.RestartCount < byte.MaxValue)
                {
                    _span.RestartCount++;
                }

                if (_reverse)
                {
                    ReadLeafState();
                    _currentIndex = _nodeItemCount;
                }
                else
                {
                    _currentIndex = -1;
                    ReadLeafState();
                }
                return MoveNext();
            }

            // Move to next/previous leaf node in the linked list
            _currentNode = _reverse
                ? _currentNode.GetPrevious(ref _accessor)
                : _currentNode.GetNext(ref _accessor);
            if (!_currentNode.IsValid)
            {
                return false;
            }

            ReadLeafState();

            if (_nodeItemCount == 0)
            {
                return false;
            }

            // Position at first/last item of new leaf and check bound
            if (_reverse)
            {
                _currentIndex = _nodeItemCount - 1;
                var item = _currentNode.GetItem(_currentIndex, ref _accessor);
                if (_comparer.Compare(item.Key, _boundKey) < 0)
                {
                    return false;
                }
            }
            else
            {
                _currentIndex = 0;
                if (_bounded)
                {
                    var item = _currentNode.GetItem(0, ref _accessor);
                    if (_comparer.Compare(item.Key, _boundKey) > 0)
                    {
                        return false;
                    }
                }
            }

            if (TelemetryConfig.DataIndexBTreeRangeScanActive)
            {
                _span.ResultCount++;
            }

            return true;
        }

        /// <summary>Releases the chunk accessor.</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _span.Dispose();
                _accessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Enumerates an AllowMultiple BTree in key order, expanding each key's VSBS buffer to yield <see cref="ReadOnlySpan{Int32}"/>
    /// chunks of values. Wraps <see cref="RangeEnumerator"/> for leaf traversal and <see cref="VariableSizedBufferAccessor{T}"/>
    /// for per-key buffer expansion.
    /// </summary>
    /// <remarks>
    /// <para>Iteration is two-level: call <see cref="MoveNextKey"/> to advance to the next distinct key, then read <see cref="CurrentValues"/>
    /// (and call <see cref="NextChunk"/> for multi-chunk buffers).
    /// <see cref="CurrentKey"/> returns the current key.</para>
    /// <para>The caller must be inside an epoch scope (e.g., via a Transaction).</para>
    /// <para>Supports <c>foreach</c> via duck-typing — the <c>foreach</c> loop iterates keys. Within each key, iterate values
    /// via <see cref="CurrentValues"/> + <see cref="NextChunk"/>.</para>
    /// </remarks>
    public ref struct RangeMultipleEnumerator
    {
        private RangeEnumerator _inner;
        private readonly BaseNodeStorage _storage;
        private VariableSizedBufferAccessor<int, TStore> _currentBuffer;
        private bool _hasCurrentBuffer;
        private bool _disposed;

        internal RangeMultipleEnumerator(BTree<TKey, TStore> tree, TKey minKey, TKey maxKey, bool reverse = false)
        {
            _inner = reverse ? new RangeEnumerator(tree, minKey, maxKey, true) : new RangeEnumerator(tree, minKey, maxKey);
            _storage = tree._storage;
            _currentBuffer = default;
            _hasCurrentBuffer = false;
            _disposed = false;
        }

        /// <summary>Returns this enumerator (required for foreach pattern).</summary>
        public RangeMultipleEnumerator GetEnumerator() => this;

        /// <summary>The key at the current position.</summary>
        public TKey CurrentKey => _inner.Current.Key;

        /// <summary>
        /// The current chunk of values for the current key. Valid after <see cref="MoveNextKey"/> returns true.
        /// Call <see cref="NextChunk"/> to advance to subsequent chunks if the buffer spans multiple chunks.
        /// </summary>
        public ReadOnlySpan<int> CurrentValues => _currentBuffer.ReadOnlyElements;

        /// <summary>Advances to the next chunk of the current key's VSBS buffer.</summary>
        /// <returns>True if another chunk is available; false if all values for this key have been yielded.</returns>
        public bool NextChunk() => _currentBuffer.NextChunk();

        /// <summary>
        /// Advances to the next key in iteration order, opening its VSBS buffer.
        /// After this returns true, read <see cref="CurrentValues"/> (+ <see cref="NextChunk"/>) to get the values.
        /// </summary>
        public bool MoveNextKey()
        {
            // Dispose previous buffer if any
            if (_hasCurrentBuffer)
            {
                _currentBuffer.Dispose();
                _hasCurrentBuffer = false;
            }

            if (!_inner.MoveNext())
            {
                return false;
            }

            var bufferId = _inner.Current.Value;
            _currentBuffer = _storage.GetBufferReadOnlyAccessor(bufferId);
            _hasCurrentBuffer = true;

            if (!_currentBuffer.IsValid)
            {
                // Empty buffer — skip to next key
                return MoveNextKey();
            }

            return true;
        }

        /// <summary>Alias for <see cref="MoveNextKey"/> — enables foreach duck-typing.</summary>
        public bool MoveNext() => MoveNextKey();

        /// <summary>Returns the current enumerator position (for foreach — current is this enumerator itself).</summary>
        public RangeMultipleEnumerator Current => this;

        /// <summary>Releases all resources.</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_hasCurrentBuffer)
                {
                    _currentBuffer.Dispose();
                    _hasCurrentBuffer = false;
                }
                _inner.Dispose();
            }
        }
    }
}
