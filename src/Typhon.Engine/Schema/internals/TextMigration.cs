using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// The text segment as a schema migration writes to it (#1094): a migration whose new layout has a <c>String1024</c> field gives each text it produces — a
/// widened <c>String64</c>, or one a migration function sets — a fresh chunk. Never the chunk an old value names: until the migration commits, the
/// pre-migration image must stay intact for an open that fails before the commit (SCHEMA-09). The chunks the migration leaves unnamed — a dropped field's, a
/// dropped revision's, a replaced text's — are swept once it has committed.
/// </summary>
/// <remarks>
/// Its pages are a structural write: tracked by its own change set and saved by each migration pass once it is done (<see cref="Save"/>), before the commit
/// that publishes the rows naming them — a reference reaches the disk after its referent (DC-01).
/// </remarks>
internal sealed class TextMigration
{
    private readonly ChunkBasedSegment<PersistentStore> _segment;
    private readonly ChangeSet _changeSet;

    internal TextMigration(DatabaseEngine dbe)
    {
        _segment = dbe.GetOrCreateTextSegment();
        _changeSet = dbe.MMF.CreateChangeSet();
    }

    /// <summary>Texts this migration wrote. Diagnostic.</summary>
    internal int TextsWritten { get; private set; }

    /// <summary>A fresh chunk holding <paramref name="utf8"/> (already fitted), or 0 for an empty text.</summary>
    internal int Allocate(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return 0;
        }

        var accessor = _segment.CreateChunkAccessor(_changeSet);
        try
        {
            TextsWritten++;
            return TextChunks.Allocate(_segment, ref accessor, _changeSet, utf8);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>The text chunk <paramref name="chunkId"/> holds; empty for 0 or a handle past the segment.</summary>
    internal string Read(int chunkId)
    {
        if (chunkId <= 0 || chunkId >= _segment.ChunkCapacity)
        {
            return string.Empty;
        }

        var accessor = _segment.CreateChunkAccessor();
        try
        {
            return TextChunks.ReadString(ref accessor, chunkId);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Writes the pages of every text this migration produced so far.</summary>
    internal void Save() => _changeSet.SaveChanges();

    /// <summary>Saves the texts of every migration in <paramref name="migrations"/> that produced some.</summary>
    internal static void SaveAll(System.Collections.Generic.IEnumerable<MigrationResult> migrations)
    {
        foreach (var m in migrations)
        {
            m.Text?.Save();
        }
    }
}
