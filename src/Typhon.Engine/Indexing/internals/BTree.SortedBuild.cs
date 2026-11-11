using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>
    /// Starts a bottom-up build of this tree from entries that will arrive in ascending key order (#1588, design §5.1). The tree must be empty and stay
    /// private to the caller's thread until <see cref="SortedBuilder.Finish"/> publishes it.
    /// </summary>
    /// <param name="leafFillPercent">The share of a leaf's capacity each leaf is filled to, 50..100; the last two leaves of the tree are balanced.</param>
    /// <param name="changeSet">The ChangeSet the node and buffer writes are tracked in.</param>
    internal SortedBuilder BeginSortedBuild(int leafFillPercent, ChangeSet changeSet)
    {
        if (_rootChunkId != 0 || _count != 0)
        {
            ThrowHelper.ThrowInvalidOp("A sorted build needs an empty tree.");
        }

        return new SortedBuilder(this, leafFillPercent, changeSet);
    }

    /// <summary>
    /// Builds a tree bottom-up from an ascending stream, writing every node once (#1588, design §5.1, IXB-01): leaves are filled left to right to the
    /// requested fill, each internal level full, and the last two nodes of every level balanced so no node but the root holds fewer than half its capacity.
    /// The result is a tree the live insert path could have produced: items from slot 0 (<c>Start = 0</c>), strictly ascending; leaves doubly linked, each
    /// one's HighKey the next one's first key and the tail's the sentinel; an internal node's <c>LeftValue</c> its first child and <c>Keys[i]</c> the first
    /// key of child <c>i + 1</c>; every node initialised by <see cref="BaseNodeStorage.InitializeNode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One node held back per level.</b> A node is complete once it reaches its fill, but it is written only when the next node of its level completes,
    /// because a leaf needs its successor's id and first key (Next, HighKey) — and because at the end the last two nodes of a level are rebalanced: merged
    /// when they fit one node, split evenly otherwise. Every written node pushes its first key and id to the level above.
    /// </para>
    /// <para>
    /// <b>No page address is held between calls</b>: every write goes through the storage's own accessor-resolved setters, after the allocation it needs,
    /// so the caller may commit its accessor and make a batch point between any two <see cref="Append"/>s.
    /// </para>
    /// <para>
    /// Multi-value trees: the first entry of a key creates its value buffer, every entry of the key appends to it, and <see cref="Append"/> returns the
    /// element id the live path would have. Sparse key 0 never reaches the builder (the caller skips it, IX-09).
    /// </para>
    /// </remarks>
    internal sealed class SortedBuilder
    {
        private readonly BTree<TKey, TStore> _tree;
        private readonly BaseNodeStorage _storage;
        private readonly ChangeSet _changeSet;
        private readonly IComparer<TKey> _comparer;
        private readonly bool _multi;
        private readonly int _capacity;
        private readonly int _leafFill;
        private readonly List<Level> _levels = [];

        private bool _hasRun;
        private TKey _runKey;
        private int _runValue;
        private int _firstLeaf;
        private int _lastLeaf;
        private int _leafItems;

        internal SortedBuilder(BTree<TKey, TStore> tree, int leafFillPercent, ChangeSet changeSet)
        {
            _tree = tree;
            _storage = tree._storage;
            _changeSet = changeSet;
            _comparer = tree.Comparer;   // the tree's own order, the one its descents use
            _multi = tree.AllowMultiple;
            _capacity = _storage.GetNodeCapacity();
            var percent = Math.Clamp(leafFillPercent, 50, 100);
            _leafFill = Math.Min(_capacity, Math.Max((_capacity * percent + 99) / 100, _capacity / 2 + 1));
        }

        /// <summary>A node's capacity in keys.</summary>
        internal int NodeCapacity => _capacity;

        /// <summary>The leaf fill this build uses, in items.</summary>
        internal int LeafFill => _leafFill;

        /// <summary>Nodes written so far.</summary>
        internal long NodesWritten { get; private set; }

        /// <summary>
        /// Adds one entry. Keys must not descend; a unique tree's keys must ascend strictly (the caller drops duplicates, RB-07), a multi-value tree's equal
        /// keys share one buffer. Returns the element id (0 for a unique tree).
        /// </summary>
        internal int Append(TKey key, int value, ref ChunkAccessor<TStore> accessor)
        {
            if (_hasRun)
            {
                var c = CompareKeys(key, _runKey, _comparer);
                if (c < 0 || (c == 0 && !_multi))
                {
                    ThrowHelper.ThrowInvalidOp(c < 0
                        ? "A sorted build received a key below the previous one."
                        : "A sorted build of a unique tree received a duplicate key.");
                }

                if (c == 0)
                {
                    return _storage.Append(_runValue, value, ref accessor);
                }

                CloseRun(ref accessor);
            }

            _hasRun = true;
            _runKey = key;
            if (!_multi)
            {
                _runValue = value;
                return 0;
            }

            _runValue = _storage.CreateBuffer(ref accessor);
            return _storage.Append(_runValue, value, ref accessor);
        }

        /// <summary>
        /// Writes every node still held and publishes the tree: height, leaf-chain ends, count and last-key cache, then the root — the store a reader
        /// starts from — and the directory entry.
        /// </summary>
        internal void Finish(ref ChunkAccessor<TStore> accessor)
        {
            if (_hasRun)
            {
                CloseRun(ref accessor);
                _hasRun = false;
            }

            // Flush level by level; every node written pushes to the level above, so the loop ends at the first level that received a single child: the
            // node below it is the root. A level reached by two children or more gets an internal node of at least one key.
            var root = 0;
            var height = 0;
            for (var l = 0; l < _levels.Count; l++)
            {
                var level = _levels[l];
                if (l > 0 && level.Emitted == 0 && level.PendingCount == 0 && level.CurrentCount == 1)
                {
                    root = level.Current[0].Value;
                    height = l;
                    break;
                }

                FlushLevel(l, ref accessor);
            }

            if (root == 0)
            {
                return;   // no entry at all: the tree stays empty, as Add would leave it
            }

            var tree = _tree;
            tree.Height = height;                                     // before the root (IXW-10)
            tree._linkList = new NodeWrapper(_storage, _firstLeaf, true);
            tree._reverseLinkList = new NodeWrapper(_storage, _lastLeaf, true);
            tree._count = _leafItems;
            tree._hasCachedLastKey = false;                           // duplicates are decided on the leaf (IXW-11)
            tree.Root = new NodeWrapper(_storage, root, height == 1); // last: everything a reader reaches from it is in place
            tree.SyncHeader(ref accessor);
        }

        private void CloseRun(ref ChunkAccessor<TStore> accessor)
        {
            _leafItems++;
            Push(0, new KeyValueItem(_runKey, _runValue), ref accessor);
        }

        private Level LevelAt(int index)
        {
            while (_levels.Count <= index)
            {
                var leaf = _levels.Count == 0;
                var entries = leaf ? _capacity : _capacity + 1;
                _levels.Add(new Level(leaf, entries, leaf ? _leafFill : entries));
            }

            return _levels[index];
        }

        /// <summary>
        /// Adds an entry to level <paramref name="l"/>; a node that reaches its fill becomes the held node, and the one held before is written.
        /// </summary>
        private void Push(int l, KeyValueItem entry, ref ChunkAccessor<TStore> accessor)
        {
            var level = LevelAt(l);
            level.Current[level.CurrentCount++] = entry;
            if (level.CurrentCount < level.Fill)
            {
                return;
            }

            // The current node is complete: write the one held before it, which now knows its successor, and hold this one.
            if (level.PendingCount > 0)
            {
                if (level.PendingId == 0)
                {
                    level.PendingId = Allocate(ref accessor);   // before its successor's, so the leaf chain's ids ascend
                }

                level.CurrentId = Allocate(ref accessor);
                WriteNode(l, level.Pending, level.PendingCount, level.PendingId, level.CurrentId, level.Current[0].Key, ref accessor);
            }
            else
            {
                level.CurrentId = 0;
            }

            (level.Pending, level.Current) = (level.Current, level.Pending);
            level.PendingCount = level.CurrentCount;
            level.PendingId = level.CurrentId;
            level.CurrentCount = 0;
            level.CurrentId = 0;
        }

        /// <summary>Writes level <paramref name="l"/>'s last nodes, the held one and the partial one balanced, and pushes them up.</summary>
        private void FlushLevel(int l, ref ChunkAccessor<TStore> accessor)
        {
            var level = _levels[l];
            if (level.PendingCount == 0)
            {
                if (level.CurrentCount > 0)
                {
                    WriteNode(l, level.Current, level.CurrentCount, 0, 0, default, ref accessor);
                    level.CurrentCount = 0;
                }

                return;
            }

            if (level.CurrentCount == 0)
            {
                WriteNode(l, level.Pending, level.PendingCount, level.PendingId, 0, default, ref accessor);
                level.PendingCount = 0;
                return;
            }

            var total = level.PendingCount + level.CurrentCount;
            var combined = new KeyValueItem[total];
            Array.Copy(level.Pending, combined, level.PendingCount);
            Array.Copy(level.Current, 0, combined, level.PendingCount, level.CurrentCount);
            if (total <= level.Capacity)
            {
                WriteNode(l, combined, total, level.PendingId, 0, default, ref accessor);
            }
            else
            {
                // Two nodes, as even as they come: each then holds at least half a node, the minimum a non-root node may hold.
                var left = (total + 1) / 2;
                var right = new KeyValueItem[total - left];
                Array.Copy(combined, left, right, 0, total - left);
                var rightId = Allocate(ref accessor);
                WriteNode(l, combined, left, level.PendingId, rightId, right[0].Key, ref accessor);
                WriteNode(l, right, right.Length, rightId, 0, default, ref accessor);
            }

            level.PendingCount = 0;
            level.CurrentCount = 0;
        }

        /// <summary>
        /// Writes one node of level <paramref name="l"/> from its first <paramref name="count"/> entries, in chunk <paramref name="id"/> (allocated now when
        /// 0), linked to <paramref name="nextId"/> when a leaf, and pushes its first key and id to the level above.
        /// </summary>
        private void WriteNode(int l, KeyValueItem[] entries, int count, int id, int nextId, TKey nextFirstKey, ref ChunkAccessor<TStore> accessor)
        {
            var level = _levels[l];
            if (id == 0)
            {
                id = Allocate(ref accessor);
            }

            var node = new NodeWrapper(_storage, id, level.Leaf);
            _storage.InitializeNode(node, level.Leaf ? NodeStates.IsLeaf : NodeStates.None, ref accessor);
            if (level.Leaf)
            {
                for (var i = 0; i < count; i++)
                {
                    _storage.SetItem(node, i, entries[i], false, ref accessor);
                }

                _storage.SetCount(node, count, ref accessor);
                _storage.SetPreviousNode(node, _lastLeaf, ref accessor);
                _storage.SetNextNode(node, nextId, ref accessor);
                if (_firstLeaf == 0)
                {
                    _firstLeaf = id;
                }

                _lastLeaf = id;
            }
            else
            {
                _storage.SetLeftNode(node, entries[0].Value, ref accessor);
                for (var i = 1; i < count; i++)
                {
                    _storage.SetItem(node, i - 1, entries[i], false, ref accessor);
                }

                _storage.SetCount(node, count - 1, ref accessor);
            }

            // IXW-03: the next node's first key, the separator a split would have given it, on every level as the live split sets it; the last node of a
            // level keeps the sentinel.
            if (nextId != 0)
            {
                _storage.SetHighKey(node, nextFirstKey, ref accessor);
            }

            NodesWritten++;
            level.Emitted++;
            Push(l + 1, new KeyValueItem(entries[0].Key, id), ref accessor);
        }

        private int Allocate(ref ChunkAccessor<TStore> accessor) => _tree._segment.AllocateChunk(_changeSet, ref accessor);

        /// <summary>One level's held node and the node being filled. A level's entries are (key, value) at the leaves, (first key, child) above.</summary>
        private sealed class Level
        {
            public Level(bool leaf, int capacity, int fill)
            {
                Leaf = leaf;
                Capacity = capacity;
                Fill = fill;
                Pending = new KeyValueItem[capacity];
                Current = new KeyValueItem[capacity];
            }

            public readonly bool Leaf;
            public readonly int Capacity;
            public readonly int Fill;
            public KeyValueItem[] Pending;
            public KeyValueItem[] Current;
            public int PendingCount;
            public int PendingId;
            public int CurrentCount;
            public int CurrentId;
            public int Emitted;
        }
    }
}
