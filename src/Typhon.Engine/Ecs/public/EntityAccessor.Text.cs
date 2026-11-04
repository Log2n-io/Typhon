using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// The engine side of <c>String1024</c> (#1094): storing a text into a component's stored value, and reading one back. The text of a field lives in one
/// 1 024-byte chunk of a text segment (<see cref="TextChunks"/>); the component holds the chunk id.
/// </summary>
/// <remarks>
/// <para><b>Who owns a chunk (rule TXT-01).</b> Exactly one live row, or one contiguous run of revisions of a Versioned entity. So how a store treats the
/// chunk it
/// finds depends on whether anything else may still read it:</para>
/// <list type="bullet">
/// <item><b>In place</b> — SingleVersion under the TickFence discipline, and Transient: the stored value is the only owner, a text is rewritten in its
/// chunk and
/// a clear frees it.</item>
/// <item><b>Copy on write</b> — Versioned, and SingleVersion under the Commit discipline: the committed value (an older revision, or the cluster HEAD that must
/// not change before commit, CM-01) still names the chunk. The first store of a transaction therefore takes a fresh chunk, which the transaction owns until it
/// ends; later stores of the same transaction rewrite that chunk in place. The chunk it replaced belongs to the committed value and goes with it: a Versioned
/// one when revision cleanup drops the last revision naming it, a Commit HEAD's at publish.</item>
/// </list>
/// </remarks>
public unsafe partial class EntityAccessor
{
    /// <summary>
    /// Text chunks this accessor allocated under a copy-on-write store and has not committed: rewritten in place by its later stores, freed if it rolls back.
    /// Persistent segment only — the copy-on-write modes never touch a Transient component.
    /// </summary>
    private HashSet<int> _ownedTextChunks;

    /// <summary>
    /// The texts this accessor cleared under the Commit discipline — (table, entity, field offset): a staged value with no handle is either untouched (HEAD
    /// had none) or cleared, and the publish must tell them apart (<c>Transaction.PublishStagedValue</c>).
    /// </summary>
    private HashSet<(ComponentTable Table, long Entity, int Offset)> _clearedTexts;

    /// <summary>Whether this accessor cleared the text at <paramref name="offset"/> of <paramref name="entity"/>'s <paramref name="table"/>
    /// component.</summary>
    internal bool ClearedText(ComponentTable table, long entity, int offset) => _clearedTexts != null && _clearedTexts.Contains((table, entity, offset));

    /// <summary>Whether a store into <paramref name="table"/> through this accessor must not touch the chunk the stored value names (see the type
    /// remarks).</summary>
    private bool IsTextCopyOnWrite(ComponentTable table) =>
        table.StorageMode == StorageMode.Versioned || (table.StorageMode == StorageMode.SingleVersion && Discipline == CommitDiscipline.Commit);

    /// <summary>Whether this accessor owns <paramref name="chunkId"/> as an uncommitted copy-on-write chunk.</summary>
    internal bool OwnsTextChunk(int chunkId) => chunkId != 0 && _ownedTextChunks != null && _ownedTextChunks.Contains(chunkId);

    /// <summary>
    /// Stores <paramref name="utf8"/> (already fitted to <see cref="Typhon.Schema.Definition.String1024.MaxUtf8Bytes"/>), or no text when
    /// <paramref name="clear"/>, into the text field at <paramref name="fieldOffset"/> of <paramref name="storedValue"/> — the stored component's value bytes
    /// in engine memory, already recorded as written (dirty bit, copy-on-write revision or staged slot). Returns the handle the field now holds.
    /// </summary>
    internal int StoreText(ComponentTable table, long entity, byte* storedValue, int fieldOffset, ReadOnlySpan<byte> utf8, bool clear)
    {
        var handle = TextChunks.HandleAt(storedValue, fieldOffset);
        if (table.StorageMode == StorageMode.Transient)
        {
            var segment = table.TransientTextSegment;
            var accessor = segment.CreateChunkAccessor();
            try
            {
                return StoreTextInPlace(segment, ref accessor, null, storedValue, fieldOffset, handle, utf8, clear);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        var persistent = table.TextSegment;
        var pAccessor = persistent.CreateChunkAccessor(_changeSet);
        try
        {
            if (!IsTextCopyOnWrite(table))
            {
                return StoreTextInPlace(persistent, ref pAccessor, _changeSet, storedValue, fieldOffset, handle, utf8, clear);
            }

            var owned = OwnsTextChunk(handle);
            if (clear)
            {
                if (owned)
                {
                    // Never committed: nothing on disk can name it, so it is free for reuse at once (CK-17's exception).
                    _ownedTextChunks.Remove(handle);
                    persistent.FreeChunkUnheld(handle);
                }

                // Not ours: the committed value still names it, and its own reclamation frees it. Under the Commit discipline the publish frees HEAD's.
                TextChunks.SetHandleAt(storedValue, fieldOffset, 0);
                if (table.StorageMode == StorageMode.SingleVersion)
                {
                    (_clearedTexts ??= []).Add((table, entity, fieldOffset));
                }

                return 0;
            }

            if (owned)
            {
                TextChunks.Write(ref pAccessor, handle, utf8);
                return handle;
            }

            _clearedTexts?.Remove((table, entity, fieldOffset));
            var chunkId = TextChunks.Allocate(persistent, ref pAccessor, _changeSet, utf8);
            (_ownedTextChunks ??= []).Add(chunkId);
            TextChunks.SetHandleAt(storedValue, fieldOffset, chunkId);
            return chunkId;
        }
        finally
        {
            pAccessor.Dispose();
        }
    }

    private static int StoreTextInPlace<TStore>(ChunkBasedSegment<TStore> segment, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, byte* storedValue,
        int fieldOffset, int handle, ReadOnlySpan<byte> utf8, bool clear) where TStore : struct, IPageStore
    {
        if (clear)
        {
            if (handle != 0)
            {
                TextChunks.SetHandleAt(storedValue, fieldOffset, 0);
                segment.FreeChunk(handle);
            }

            return 0;
        }

        if (handle != 0)
        {
            TextChunks.Write(ref accessor, handle, utf8);
            return handle;
        }

        var chunkId = TextChunks.Allocate(segment, ref accessor, changeSet, utf8);
        TextChunks.SetHandleAt(storedValue, fieldOffset, chunkId);
        return chunkId;
    }

    /// <summary>The text chunk <paramref name="chunkId"/> of <paramref name="table"/> holds, as a string; empty for no text.</summary>
    internal string ReadText(ComponentTable table, int chunkId)
    {
        if (chunkId == 0)
        {
            return string.Empty;
        }

        BeginOperation();
        return ReadTextOf(table, chunkId);
    }

    /// <summary>Copies the UTF-8 text chunk <paramref name="chunkId"/> of <paramref name="table"/> holds into <paramref name="destination"/>; returns the
    /// bytes copied.</summary>
    internal int ReadText(ComponentTable table, int chunkId, Span<byte> destination)
    {
        if (chunkId == 0)
        {
            return 0;
        }

        BeginOperation();
        return ReadTextOf(table, chunkId, destination);
    }

    /// <summary>The read core, for a caller inside an epoch scope with no accessor of its own (a cluster enumeration).</summary>
    internal static string ReadTextOf(ComponentTable table, int chunkId)
    {
        if (chunkId == 0)
        {
            return string.Empty;
        }

        if (table.StorageMode == StorageMode.Transient)
        {
            var accessor = table.TransientTextSegment.CreateChunkAccessor();
            try
            {
                return TextChunks.ReadString(ref accessor, CheckTextHandle(table.TransientTextSegment, chunkId));
            }
            finally
            {
                accessor.Dispose();
            }
        }

        var pAccessor = table.TextSegment.CreateChunkAccessor();
        try
        {
            return TextChunks.ReadString(ref pAccessor, CheckTextHandle(table.TextSegment, chunkId));
        }
        finally
        {
            pAccessor.Dispose();
        }
    }

    /// <summary>The UTF-8 read core, as <see cref="ReadTextOf(ComponentTable, int)"/>.</summary>
    internal static int ReadTextOf(ComponentTable table, int chunkId, Span<byte> destination)
    {
        if (chunkId == 0)
        {
            return 0;
        }

        if (table.StorageMode == StorageMode.Transient)
        {
            var accessor = table.TransientTextSegment.CreateChunkAccessor();
            try
            {
                return TextChunks.Read(ref accessor, CheckTextHandle(table.TransientTextSegment, chunkId), destination);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        var pAccessor = table.TextSegment.CreateChunkAccessor();
        try
        {
            return TextChunks.Read(ref pAccessor, CheckTextHandle(table.TextSegment, chunkId), destination);
        }
        finally
        {
            pAccessor.Dispose();
        }
    }

    /// <summary>The non-throwing read core, for the tick path: a handle past its segment reads as no text.</summary>
    internal static int TryReadTextOf(ComponentTable table, int chunkId, Span<byte> destination)
    {
        var capacity = table.StorageMode == StorageMode.Transient ? table.TransientTextSegment.ChunkCapacity : table.TextSegment.ChunkCapacity;
        return (uint)chunkId < (uint)capacity ? ReadTextOf(table, chunkId, destination) : 0;
    }

    /// <summary>A handle past the segment is no chunk of it: refused before it addresses memory the segment does not own.</summary>
    private static int CheckTextHandle<TStore>(ChunkBasedSegment<TStore> segment, int chunkId) where TStore : struct, IPageStore
    {
        if ((uint)chunkId >= (uint)segment.ChunkCapacity)
        {
            ThrowHelper.ThrowInvalidOp($"String1024 handle {chunkId} names no chunk of the text segment (capacity {segment.ChunkCapacity}).");
        }

        return chunkId;
    }

    /// <summary>
    /// Frees the text chunks this accessor took under copy-on-write stores and never committed: its rollback. A chunk named only by the discarded revision or
    /// staged value is no one's.
    /// </summary>
    internal void ReleaseOwnedTextChunks()
    {
        if (_ownedTextChunks is not { Count: > 0 })
        {
            return;
        }

        var segment = _dbe.TextSegment;
        foreach (var chunkId in _ownedTextChunks)
        {
            segment.FreeChunkUnheld(chunkId);
        }

        _ownedTextChunks.Clear();
        _clearedTexts?.Clear();
    }

    /// <summary>Frees one owned, never-committed chunk whose value is dropped before the transaction ends (a destroyed entity's staged value).</summary>
    internal void ReleaseOwnedTextChunk(int chunkId)
    {
        if (_ownedTextChunks != null && _ownedTextChunks.Remove(chunkId))
        {
            _dbe.TextSegment.FreeChunkUnheld(chunkId);
        }
    }

    /// <summary>
    /// Frees the owned chunks a never-published value names — a revision content chunk or a staged value this transaction drops — and zeroes their handles.
    /// A handle it shares with the committed value is not this transaction's and is left.
    /// </summary>
    internal void ReleaseOwnedTextsIn(ComponentTable table, byte* value)
    {
        if (!table.HasText || _ownedTextChunks is not { Count: > 0 })
        {
            return;
        }

        foreach (var f in table.TextFields)
        {
            var chunkId = TextChunks.HandleAt(value, f.OffsetInComponentStorage);
            if (OwnsTextChunk(chunkId))
            {
                TextChunks.SetHandleAt(value, f.OffsetInComponentStorage, 0);
                ReleaseOwnedTextChunk(chunkId);
            }
        }
    }

    /// <summary>
    /// Frees every text a spawned value's payload names when the spawn never reaches a cluster (rolled back, or destroyed in its own transaction), and
    /// zeroes the handles. An owned chunk leaves the owned set as it goes; an in-place one (SingleVersion under TickFence, Transient) was never tracked.
    /// Never committed either way, so nothing on disk names it and it is free for reuse at once.
    /// </summary>
    internal void ReleaseSpawnTexts(ComponentTable table, byte* value)
    {
        foreach (var f in table.TextFields)
        {
            var chunkId = TextChunks.HandleAt(value, f.OffsetInComponentStorage);
            if (chunkId == 0)
            {
                continue;
            }

            TextChunks.SetHandleAt(value, f.OffsetInComponentStorage, 0);
            if (OwnsTextChunk(chunkId))
            {
                ReleaseOwnedTextChunk(chunkId);
            }
            else if (table.StorageMode == StorageMode.Transient)
            {
                table.TransientTextSegment.FreeChunk(chunkId);
            }
            else
            {
                table.TextSegment.FreeChunkUnheld(chunkId);
            }
        }
    }

    /// <summary>The transaction committed: the chunks it took now belong to the committed values that name them.</summary>
    internal void AdoptOwnedTextChunks()
    {
        _ownedTextChunks?.Clear();
        _clearedTexts?.Clear();
    }
}
