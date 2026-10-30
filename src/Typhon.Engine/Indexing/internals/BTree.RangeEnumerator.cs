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
    /// <para>OLC per row (IXS-09): the cursor's position and every row it hands out are read under ONE validated version of the leaf they come from. The
    /// seek takes the leaf's version, then finds the key in it, then validates; a row is validated before it is emitted. A version change re-descends from
    /// the last key emitted. A position computed under an older version — the descent's own index, taken before a split, a merge or a borrow moved the
    /// key — answered a single-key read empty, or with another key's entries (#1235).</para>
    /// <para>The caller must be inside an epoch scope (e.g., via a Transaction).</para>
    /// <para>Supports <c>foreach</c> via duck-typing (GetEnumerator/MoveNext/Current/Dispose).</para>
    /// </remarks>
    [MustDispose(Reason = "owns a ChunkAccessor ('_accessor'): until disposed its pages stay pinned and registered as written, and the checkpoint cannot cover them (CP-13)")]
    [NoCopy(Reason = "holds a ChunkAccessor by value ('_accessor'): a copy duplicates its slots and pins")]
    public ref struct RangeEnumerator
    {
        /// <summary>How long to spin on a write-locked leaf before treating it as "go around again" rather than "wait".</summary>
        private const int LockSpinLimit = 64;

        /// <summary>Test seam: runs in <see cref="Position"/> between the descent and the read of the leaf it found. Null in production.</summary>
        [ThreadStatic]
        internal static Action SeekProbe;

        private readonly BTree<TKey, TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private NodeWrapper _currentNode;
        private int _currentIndex;
        private int _nodeItemCount;
        private int _leafVersion;
        private KeyValueItem _current;   // the row last emitted, read under a validated version — Current never re-reads the leaf
        private bool _disposed;
        private readonly bool _countedLive;   // counted in EntityAccessor.LiveIndexCursors: holds a node across calls, so no per-operation epoch refresh (#1199)
        private readonly IComparer<TKey> _comparer;
        private readonly TKey _boundKey;
        private readonly bool _bounded;

        /// <summary>The key this scan started from, used to restart when the very first leaf is invalidated before anything is emitted.</summary>
        private readonly TKey _seekKey;

        /// <summary>The last key handed to the caller. This, not a leaf index, is what the scan resumes from.</summary>
        private TKey _lastKey;

        private bool _hasLastKey;

        /// <summary>True once the range is finished; further calls return false without touching the tree.</summary>
        private bool _finished;

        // Phase 6: Data:Index:BTree:RangeScan span (Tier-2 gated). ResultCount/RestartCount filled during enumeration.
        private DataIndexBTreeRangeScanEvent _span;
        private readonly bool _reverse;

        /// <summary>Unbounded forward constructor — walks the entire leaf chain (used by <see cref="EnumerateLeaves"/>).</summary>
        internal RangeEnumerator(BTree<TKey, TStore> tree)
        {
            _tree = tree;
            _accessor = tree._segment.CreateScanAccessor();
            _comparer = tree.Comparer;
            _bounded = false;
            _reverse = false;
            _boundKey = default;
            _seekKey = default;
            _lastKey = default;
            _hasLastKey = false;
            _finished = false;
            _currentNode = tree._linkList;
            _currentIndex = -1;
            _disposed = false;
            _countedLive = false;

            try
            {
                // The head leaf write-locked past the spin limit, or replaced: read the head again rather than answer for an empty leaf.
                for (var attempt = 0; _currentNode.IsValid && !TryReadLeafState(); attempt++)
                {
                    CountRestartOrThrow(attempt, default, "the first leaf");
                    _currentNode = tree._linkList;
                }

                if (!_currentNode.IsValid)
                {
                    _nodeItemCount = 0;
                    _leafVersion = 0;
                    _finished = true;
                }
            }
            catch
            {
                _accessor.Dispose();   // never reaches the caller's Dispose (EP-02)
                throw;
            }

            _span = TyphonEvent.BeginDataIndexBTreeRangeScan();
            _countedLive = true;
            EntityAccessor.LiveIndexCursors++;
        }

        /// <summary>
        /// Bounded constructor — seeks to the appropriate endpoint and iterates toward the bound.
        /// Forward (<paramref name="reverse"/>=false): seeks to <paramref name="minKey"/>, stops at <paramref name="maxKey"/>.
        /// Reverse (<paramref name="reverse"/>=true): seeks to <paramref name="maxKey"/>, stops at <paramref name="minKey"/>.
        /// </summary>
        internal RangeEnumerator(BTree<TKey, TStore> tree, TKey minKey, TKey maxKey, bool reverse = false)
        {
            _tree = tree;
            _accessor = tree._segment.CreateScanAccessor();
            _comparer = tree.Comparer;
            _bounded = true;
            _reverse = reverse;
            _boundKey = reverse ? minKey : maxKey;
            _seekKey = reverse ? maxKey : minKey;
            _lastKey = default;
            _hasLastKey = false;
            _finished = false;
            _disposed = false;
            _countedLive = false;
            _currentIndex = -1;
            _nodeItemCount = 0;
            _leafVersion = 0;

            // Inverted range or empty tree — yield nothing
            if (_comparer.Compare(minKey, maxKey) > 0 || tree.IsEmpty())
            {
                _currentNode = default;
                _finished = true;
                return;
            }

            // A fault while positioning never reaches the caller's Dispose: the window's pins are slot references, released here or never (EP-02).
            try
            {
                if (!Position(_seekKey, false))
                {
                    _finished = true;   // the tree is empty
                    return;
                }
            }
            catch
            {
                _accessor.Dispose();
                throw;
            }

            _span = TyphonEvent.BeginDataIndexBTreeRangeScan();
            _countedLive = true;
            EntityAccessor.LiveIndexCursors++;
        }

        /// <summary>
        /// Positions the cursor at <paramref name="key"/> in the leaf the tree routes it to NOW, under one validated version of that leaf: the leaf's version,
        /// then the key's index in it, then the validation. <paramref name="after"/> parks exactly on the key, so the next step leaves it behind (a resume);
        /// otherwise the next step lands on it (a seek). Returns false only when the tree is empty.
        /// </summary>
        /// <remarks>
        /// The descent's own index is not used: it was taken under the version the descent saw, and a split, a merge or a borrow landing before this reads
        /// the leaf moves the key — the cursor then stood at a stale position and answered past the key, or before it (#1235). A leaf that cannot be read —
        /// replaced, or write-locked past the spin limit — sends the cursor down again; it never ends the scan, which answered empty for a key the tree holds.
        /// </remarks>
        private bool Position(TKey key, bool after)
        {
            for (var attempt = 0; ; attempt++)
            {
                _currentNode = _tree.FindLeaf(key, out _, ref _accessor);
                if (!_currentNode.IsValid)
                {
                    return false;
                }

                SeekProbe?.Invoke();
                if (TryReadLeafState())
                {
                    var index = _currentNode.Find(key, _comparer, ref _accessor);
                    if (_currentNode.GetLatch(ref _accessor).ValidateVersion(_leafVersion))
                    {
                        ParkAt(index, after);
                        return true;
                    }
                }

                CountRestartOrThrow(attempt, key, "the leaf its key routes to");
            }
        }

        /// <summary>
        /// The cursor's index from <c>Find</c>'s answer in the current leaf. A key past the leaf's last entry (or before its first, in reverse) parks at the
        /// leaf's edge: the next step leaves the leaf through the validated link-follow in <see cref="MoveNext"/>, never through an unvalidated neighbour read.
        /// </summary>
        private void ParkAt(int index, bool after)
        {
            var insertionPoint = index >= 0 ? index : ~index;
            if (_reverse)
            {
                // Reverse steps down: the next step lands on the key itself (seek), or below it (resume, or a key the leaf does not hold).
                _currentIndex = index >= 0 && !after ? index + 1 : insertionPoint;
            }
            else
            {
                // Forward steps up: the next step lands on the key or the first entry above it (seek), or above it (resume).
                _currentIndex = index >= 0 && after ? index : insertionPoint - 1;
            }
        }

        /// <summary>A re-descent counted; past the tree's restart budget, a liveness defect named by key rather than an endless loop (IXW-01).</summary>
        private void CountRestartOrThrow(int attempt, TKey key, string what)
        {
            if (TelemetryConfig.DataIndexBTreeRangeScanActive && _span.RestartCount < byte.MaxValue)
            {
                _span.RestartCount++;
            }

            Interlocked.Increment(ref _tree._pessimisticRestarts);
            if (attempt >= MaxPessimisticRestarts)
            {
                ThrowHelper.ThrowInvalidOp(
                    $"B+Tree range scan from key {key} could not read {what} under one version in {MaxPessimisticRestarts} attempts: it is modified, replaced or "
                    + "locked on every pass. This is a liveness defect in the tree, not contention (IXW-01).");
            }

            Thread.SpinWait(1 << Math.Min(attempt, 6));
        }

        /// <summary>
        /// Reads the current leaf's version and item count under OLC. Returns false when the leaf is obsolete — replaced by a structure-modifying operation
        /// and never valid again — so the caller must re-descend rather than wait.
        /// </summary>
        /// <remarks>
        /// This used to loop forever on <c>version == 0</c>, which conflates two states that need opposite responses: a write lock, released in nanoseconds,
        /// and the obsolete bit, which is permanent. A reader that reached an obsolete leaf spun until the process was killed.
        /// </remarks>
        private bool TryReadLeafState()
        {
            var spins = 0;
            while (true)
            {
                var latch = _currentNode.GetLatch(ref _accessor);
                var version = latch.ReadVersion();
                if (version == 0)
                {
                    if (latch.IsObsolete || ++spins > LockSpinLimit)
                    {
                        return false;
                    }

                    Thread.SpinWait(1);
                    continue;
                }

                _nodeItemCount = _currentNode.GetCount(ref _accessor);

                if (latch.ValidateVersion(version))
                {
                    _leafVersion = version;
                    return true;
                }
                // Version changed — retry this leaf
            }
        }

        /// <summary>Returns this enumerator (required for foreach pattern).</summary>
        [AllowCopy]   // foreach takes this enumerator: the copy is the one it walks and disposes
        public RangeEnumerator GetEnumerator() => this;

        /// <summary>Gets the current key-value item: the row <see cref="MoveNext"/> validated, not a re-read of the leaf.</summary>
        public KeyValueItem Current => _current;

        /// <summary>Advances to the next entry in iteration order, traversing leaf nodes as needed.</summary>
        /// <remarks>
        /// <para>
        /// The scan's contract is that the keys it emits are STRICTLY monotonic. It is not a snapshot — an entry inserted ahead of the cursor may or may not
        /// be seen, and one inserted behind it will not be — but it may never hand out the same entry twice, because a caller applying <c>Take(N)</c> or
        /// filling a result list cannot tell a duplicate from a genuine second row.
        /// </para>
        /// <para>
        /// That is why an invalidated leaf resumes from <see cref="_lastKey"/> and not from the leaf's start. Re-reading the leaf and restarting at index 0
        /// re-emitted everything already handed out from it: measured at 18 899 keys returned from a 4 500-entry tree under a writer touching the cursor's
        /// leaf every eight steps.
        /// </para>
        /// </remarks>
        public bool MoveNext()
        {
            while (true)
            {
                if (_finished || !_currentNode.IsValid)
                {
                    return false;
                }

                // Advance within the current leaf.
                if (_reverse)
                {
                    _currentIndex--;
                    if (_currentIndex >= 0)
                    {
                        var item = _currentNode.GetItem(_currentIndex, ref _accessor);

                        // Monotonicity is checked at the point of emission, against the last key handed out, and this is the ONLY thing that catches a writer
                        // inserting or removing an entry BEHIND the cursor inside the leaf it is standing on. No version check runs on this path — validation
                        // happens when the cursor steps off a leaf — so a shift of the entry array silently re-presented an entry that had already been
                        // emitted. One comparison per row, next to the bound comparison already here.
                        if (_hasLastKey ? _comparer.Compare(item.Key, _lastKey) >= 0 : _comparer.Compare(item.Key, _seekKey) > 0)
                        {
                            continue;
                        }

                        if (!RowStillValid())
                        {
                            if (!RestartAfterLastKey())
                            {
                                return false;
                            }

                            continue;
                        }

                        if (_comparer.Compare(item.Key, _boundKey) < 0)
                        {
                            _finished = true;
                            return false;
                        }

                        return Emit(item);
                    }
                }
                else
                {
                    _currentIndex++;
                    if (_currentIndex < _nodeItemCount)
                    {
                        var item = _currentNode.GetItem(_currentIndex, ref _accessor);

                        if (_hasLastKey ? _comparer.Compare(item.Key, _lastKey) <= 0 : _bounded && _comparer.Compare(item.Key, _seekKey) < 0)
                        {
                            continue;
                        }

                        if (!RowStillValid())
                        {
                            if (!RestartAfterLastKey())
                            {
                                return false;
                            }

                            continue;
                        }

                        if (_bounded && _comparer.Compare(item.Key, _boundKey) > 0)
                        {
                            _finished = true;
                            return false;
                        }

                        return Emit(item);
                    }
                }

                // Before following the next/previous pointer, validate the leaf version.
                var latch = _currentNode.GetLatch(ref _accessor);
                if (!latch.ValidateVersion(_leafVersion))
                {
                    if (!RestartAfterLastKey())
                    {
                        return false;
                    }

                    continue;
                }

                // Move to next/previous leaf node in the linked list
                _currentNode = _reverse ? _currentNode.GetPrevious(ref _accessor) : _currentNode.GetNext(ref _accessor);
                if (!_currentNode.IsValid)
                {
                    _finished = true;
                    return false;
                }

                if (!TryReadLeafState())
                {
                    if (!RestartAfterLastKey())
                    {
                        return false;
                    }

                    continue;
                }

                // Park on the sentinel and let the loop's own step land on the first (or last) entry, so the new leaf goes through exactly the same
                // monotonicity and bound checks as every other row. An empty leaf falls straight back to the link-follow above rather than ending the scan,
                // which is what it should always have done — an empty leaf in the middle of the chain is not the end of the range.
                _currentIndex = _reverse ? _nodeItemCount : -1;
            }
        }

        /// <summary>
        /// Whether the leaf still holds the version the row just read was read under. A row read while a writer shifted the leaf's entries can pair one
        /// entry's key with another's value, or be an entry the shift has moved away; validated, it is one the leaf held whole at that version.
        /// </summary>
        private bool RowStillValid() => _currentNode.GetLatch(ref _accessor).ValidateVersion(_leafVersion);

        /// <summary>Records the row being handed out, so an invalidated leaf can be resumed from its key, and counts it.</summary>
        private bool Emit(KeyValueItem item)
        {
            _current = item;
            _lastKey = item.Key;
            _hasLastKey = true;

            if (TelemetryConfig.DataIndexBTreeRangeScanActive)
            {
                _span.ResultCount++;
            }

            return true;
        }

        /// <summary>
        /// Re-descends from the last key emitted and repositions strictly past it. Returns false when the scan cannot continue at all.
        /// </summary>
        private bool RestartAfterLastKey()
        {
            if (TelemetryConfig.DataIndexBTreeRangeScanActive && _span.RestartCount < byte.MaxValue)
            {
                _span.RestartCount++;
            }

            // Asking the tree where the key lives NOW is what makes this correct under a split, a merge or a reclaim alike — the cursor's own leaf may no
            // longer answer for it, or may no longer exist. Nothing emitted yet: resume AT the seek key; otherwise strictly past the last key emitted.
            if (!Position(_hasLastKey ? _lastKey : _seekKey, _hasLastKey))
            {
                _finished = true;
                return false;
            }

            return true;
        }

        /// <summary>Releases the chunk accessor.</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_countedLive && EntityAccessor.LiveIndexCursors > 0)
                {
                    EntityAccessor.LiveIndexCursors--;
                }
                _span.Dispose();
                _accessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Enumerates an AllowMultiple BTree in key order, expanding each key's VSBS buffer to yield <see cref="ReadOnlySpan{Int32}"/>
    /// chunks of values. Wraps <see cref="RangeEnumerator"/> for leaf traversal and <see cref="VariableSizedBufferAccessor{T,TStore}"/>
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
    [MustDispose(Reason = "owns a RangeEnumerator ('_inner'): until disposed its pages stay pinned and registered as written, and the checkpoint cannot cover them (CP-13)")]
    [NoCopy(Reason = "holds a RangeEnumerator by value ('_inner'): a copy duplicates its slots and pins")]
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
        [AllowCopy]   // foreach takes this enumerator: the copy is the one it walks and disposes
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
        /// Copies every value of the current key into <paramref name="values"/>, grown from <see cref="System.Buffers.ArrayPool{T}.Shared"/> as needed
        /// (the old array returned to it), and releases the key's buffer: its count. <see cref="CurrentValues"/> and <see cref="NextChunk"/> are spent.
        /// </summary>
        /// <remarks>
        /// For a caller that resolves each value through a revision chain (IXS-10). The buffer's shared lock, held across a chain walk, closed a cycle with
        /// a publish that holds the chain and moves the entity off this key, which takes the buffer exclusively: each waited the other out, and the
        /// publish's leaf latch, held across its buffer wait, sent every other writer of the key into its retry bound (IXW-01).
        /// </remarks>
        public int DrainCurrentKey(ref int[] values)
        {
            var count = 0;
            if (!_hasCurrentBuffer)
            {
                return 0;
            }

            do
            {
                var chunk = _currentBuffer.ReadOnlyElements;
                if (count + chunk.Length > values.Length)
                {
                    var grown = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(values.Length * 2, count + chunk.Length));
                    values.AsSpan(0, count).CopyTo(grown);
                    if (values.Length > 0)
                    {
                        System.Buffers.ArrayPool<int>.Shared.Return(values);
                    }

                    values = grown;
                }

                chunk.CopyTo(values.AsSpan(count));
                count += chunk.Length;
            } while (_currentBuffer.NextChunk());

            _currentBuffer.Dispose();
            _hasCurrentBuffer = false;
            return count;
        }

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
        [AllowCopy]   // foreach duck-typing: the loop variable is a view of this enumerator, never disposed
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
