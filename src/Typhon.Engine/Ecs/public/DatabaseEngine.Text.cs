using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>The out-of-line text segment behind <c>String1024</c> fields (#1094): one per database, persistent, 1 024-byte chunks.</summary>
public partial class DatabaseEngine
{
    /// <summary>Bootstrap key of the text segment's root page; absent until a component with a <c>String1024</c> field is first registered.</summary>
    internal const string BK_TextSegment = "text.String1024";

    private const int TextSegmentStartingSize = 4;

    private readonly object _textSegmentCreation = new();
    private ChunkBasedSegment<PersistentStore> _textSegment;

    /// <summary>The persistent text segment, or null when no component of this database has ever declared a <c>String1024</c> field.</summary>
    internal ChunkBasedSegment<PersistentStore> TextSegment => Volatile.Read(ref _textSegment);

    /// <summary>
    /// The persistent text segment, created on the first call. A created segment is durable at once — its pages, then the bootstrap key the open finds it
    /// through — before it is published, so no text reaches it before a reopen can find it (the #1283 lesson of the collection segments). Its frees are held
    /// until a checkpoint (CK-17): a Versioned revision on disk may still name a chunk this process freed, and a chunk re-issued before the checkpoint that
    /// drops that revision would be shared by two owners after a crash.
    /// </summary>
    internal ChunkBasedSegment<PersistentStore> GetOrCreateTextSegment()
    {
        var segment = Volatile.Read(ref _textSegment);
        if (segment != null)
        {
            return segment;
        }

        lock (_textSegmentCreation)
        {
            segment = _textSegment;
            if (segment != null)
            {
                return segment;
            }

            var creation = MMF.CreateChangeSet();
            segment = MMF.AllocateChunkBasedSegment(PageBlockType.None, TextSegmentStartingSize, TextChunks.Stride, creation, StorageSegmentKind.Text);
            creation.SaveChanges();

            var created = segment;
            MMF.MutateBootstrapAndPersist(() => MMF.Bootstrap.SetInt(BK_TextSegment, created.RootPageIndex));
            segment.QuarantineFrees(MMF);
            Volatile.Write(ref _textSegment, segment);
            return segment;
        }
    }

    /// <summary>Text chunks a crash open found named by the data file and free in the segment's bitmap, and took before the replay (TXT-03).
    /// Diagnostic.</summary>
    internal int TextChunksReservedAtOpen { get; private set; }

    /// <summary>Text chunks a crash open freed as named by nothing once the replay and the scrub were done (TXT-03). Diagnostic.</summary>
    internal int TextOrphansSweptAtOpen { get; private set; }

    /// <summary>
    /// Every text chunk a handle in the data file names — a superset of what is live: the handles of every allocated content chunk of a persistent table
    /// with a text field (a Versioned chain's revisions, a flat archetype's rows) that no free holds back, and of every slot of every allocated cluster of
    /// an archetype with one — occupied or not: a slot a destroy hid (VIS-09) still owns its texts, and a released slot's handles are zeroed (TXT-01). A
    /// stray handle only keeps a chunk; it frees nothing that is named.
    /// </summary>
    private unsafe HashSet<int> CollectNamedTextChunks()
    {
        var named = new HashSet<int>();
        foreach (var table in GetAllComponentTables())
        {
            if (!table.HasText || table.TextSegment == null || table.ComponentSegment == null)
            {
                continue;
            }

            var scan = table.ComponentSegment.CreateScanAccessor();
            try
            {
                foreach (var chunkId in table.ComponentSegment.AllocatedChunks())
                {
                    if (table.ComponentSegment.IsFreeHeld(chunkId))
                    {
                        continue;   // a dropped revision's content, freed and held until a checkpoint: its texts went with it
                    }

                    var data = scan.GetChunkAddress(chunkId) + table.ComponentOverhead;
                    foreach (var f in table.TextFields)
                    {
                        NoteNamedText(named, table.TextSegment, TextChunks.HandleAt(data, f.OffsetInComponentStorage));
                    }
                }
            }
            finally
            {
                scan.Dispose();
            }
        }

        foreach (var meta in OwnArchetypes())
        {
            var es = _archetypeStates[meta.ArchetypeId];
            var cs = es?.ClusterState;
            if (cs?.OutOfLineTableBySlot == null || cs.ClusterSegment == null)
            {
                continue;
            }

            var layout = cs.Layout;
            var scan = cs.ClusterSegment.CreateScanAccessor();
            try
            {
                foreach (var clusterChunkId in cs.ClusterSegment.AllocatedChunks())
                {
                    var clusterBase = scan.GetChunkAddress(clusterChunkId);
                    var occupancy = layout.FullMask;
                    for (var slot = 0; slot < es.SlotToComponentTable.Length; slot++)
                    {
                        var table = es.SlotToComponentTable[slot];
                        if (table == null || !table.HasText || table.TextSegment == null)
                        {
                            continue;   // no text, or a Transient one: its bytes are not in this segment and do not outlive the process
                        }

                        for (var rest = occupancy; rest != 0; rest &= rest - 1)
                        {
                            var i = System.Numerics.BitOperations.TrailingZeroCount(rest);
                            var compBase = clusterBase + layout.ComponentOffset(slot) + i * layout.ComponentSize(slot);
                            foreach (var f in table.TextFields)
                            {
                                NoteNamedText(named, table.TextSegment, TextChunks.HandleAt(compBase, f.OffsetInComponentStorage));
                            }
                        }
                    }
                }
            }
            finally
            {
                scan.Dispose();
            }
        }

        return named;
    }

    /// <summary>The allocated text chunks <paramref name="named"/> does not hold, a free held back excluded: freeing one again would be a second
    /// free.</summary>
    private static List<int> CollectTextOrphans(ChunkBasedSegment<PersistentStore> segment, HashSet<int> named)
    {
        var orphans = new List<int>();
        foreach (var chunkId in segment.AllocatedChunks())
        {
            if (chunkId != 0 && !named.Contains(chunkId) && !segment.IsFreeHeld(chunkId))
            {
                orphans.Add(chunkId);
            }
        }

        return orphans;
    }

    private static void NoteNamedText(HashSet<int> named, ChunkBasedSegment<PersistentStore> segment, int chunkId)
    {
        if (chunkId > 0 && chunkId < segment.ChunkCapacity)
        {
            named.Add(chunkId);
        }
    }

    /// <summary>
    /// TXT-03, before the replay allocates anything: every text chunk a handle in the data file names is taken, so the replay — which gives each text it
    /// applies a fresh chunk — never hands out one a row it has not reached yet still names. The bitmap a crash left is not trusted to have it.
    /// </summary>
    private void ReserveTextChunksAfterCrash()
    {
        var segment = TextSegment;
        TextChunksReservedAtOpen = 0;
        if (segment == null)
        {
            return;
        }

        using var guard = EpochGuard.Enter(EpochManager);
        foreach (var chunkId in CollectNamedTextChunks())
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                segment.ReserveChunk(chunkId);
                TextChunksReservedAtOpen++;
            }
        }

        // Every named chunk is taken: the replay may allocate below the floor LoadTextSegment raised.
        segment.ResetAllocationFloor(0);
    }

    /// <summary>
    /// TXT-03, once the replay and the scrub are done: every allocated text chunk no live row and no remaining revision names is freed — the chunk a replayed
    /// row had before the replay gave it a fresh one, a chunk whose free a checkpoint had not written, a dropped revision's. Stands down when a handle could be
    /// where this process does not look, as the collection sweep does.
    /// </summary>
    private void SweepTextOrphans()
    {
        var segment = TextSegment;
        TextOrphansSweptAtOpen = 0;
        if (segment == null)
        {
            return;
        }

        var unloaded = FirstUnloadedTextHandleSource();
        if (unloaded != null)
        {
            LogTextSweepStoodDown(unloaded);
            return;
        }

        using var guard = EpochGuard.Enter(EpochManager);
        var orphans = CollectTextOrphans(segment, CollectNamedTextChunks());
        foreach (var chunkId in orphans)
        {
            segment.FreeChunk(chunkId);
        }

        TextOrphansSweptAtOpen = orphans.Count;
    }

    // A source of text handles this process does not load, or null: a persisted component with a String1024 field and no table, a persisted archetype with
    // no state.
    private string FirstUnloadedTextHandleSource()
    {
        var loadedComponents = new HashSet<string>();
        foreach (var table in GetAllComponentTables())
        {
            loadedComponents.Add(table.Definition.Name);
        }

        foreach (var (name, fields) in _persistedFieldsByComponent ?? [])
        {
            if (loadedComponents.Contains(name))
            {
                continue;
            }

            foreach (var field in fields)
            {
                if (field.Type == Typhon.Schema.Definition.FieldType.String1024)
                {
                    return $"component '{name}'";
                }
            }
        }

        var loadedArchetypes = new HashSet<string>();
        foreach (var meta in OwnArchetypes())
        {
            if (_archetypeStates[meta.ArchetypeId] != null)
            {
                loadedArchetypes.Add(meta.Name);
            }
        }

        foreach (var name in (_persistedArchetypes ?? []).Keys)
        {
            if (!loadedArchetypes.Contains(name))
            {
                return $"archetype '{name}'";
            }
        }

        return null;
    }

    [LoggerMessage(LogLevel.Warning,
        "Crash open: the text sweep stood down — {source} is in the data file and not loaded, so a text it names would read as named by nothing. Orphaned "
        + "text chunks stay allocated")]
    private partial void LogTextSweepStoodDown(string source);

    /// <summary>
    /// The message for an archetype whose entities are too large for eight of them to share a page (<see cref="ArchetypeClusterInfo.SelectClusterSize"/>):
    /// what each component costs per entity, largest first, and what to do about it. Before #1094 this surfaced as a bare stride/page-size pair, naming
    /// neither the archetype nor a field.
    /// </summary>
    private static string DescribeClusterOverflow(ArchetypeMetadata meta, ComponentTable[] slotToTable)
    {
        var parts = new List<(string Name, int Size)>();
        var total = 0;
        for (var slot = 0; slot < slotToTable.Length; slot++)
        {
            var size = slotToTable[slot].Definition.ComponentStorageSize;
            parts.Add((slotToTable[slot].Definition.Name, size));
            total += size;
        }

        parts.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        var sb = new System.Text.StringBuilder();
        foreach (var (name, size) in parts)
        {
            sb.Append(sb.Length == 0 ? "" : ", ").Append(name).Append(" = ").Append(size).Append(" B");
        }

        return $"Archetype '{meta.Name}' is too large for cluster storage: its entities take {total} bytes of components each ({sb}), and at least eight "
            + $"must share one {PagedMMF.PageRawDataSize}-byte page. Move large fixed data out of line: a String1024 for text (a 4-byte handle), a "
            + "ComponentCollection<T> for a variable list.";
    }

    /// <summary>
    /// The text half of <see cref="RunStorageIntegrityCheck"/> (TXT-01): every handle a live row or a revision holds names an allocated chunk that no free
    /// holds back, and every such chunk is named. Meaningful with no transaction in flight — an uncommitted text is named by nothing durable yet.
    /// </summary>
    private void CheckTextChunks(List<StorageIntegrityIssue> issues)
    {
        var segment = TextSegment;
        if (segment == null)
        {
            return;
        }

        using var guard = EpochGuard.Enter(EpochManager);
        var named = CollectNamedTextChunks();
        var dangling = new List<int>();
        foreach (var chunkId in named)
        {
            if (!segment.IsChunkAllocated(chunkId) || segment.IsFreeHeld(chunkId))
            {
                dangling.Add(chunkId);
            }
        }

        var orphans = CollectTextOrphans(segment, named);
        if (dangling.Count > 0)
        {
            dangling.Sort();
            issues.Add(new StorageIntegrityIssue(StorageIntegrityIssueKind.TextHandleDangling, segment.RootPageIndex, -1, 0,
                $"{dangling.Count} text handle(s) name a free chunk of the text segment; the first is chunk {dangling[0]}."));
        }

        if (orphans.Count > 0)
        {
            orphans.Sort();
            issues.Add(new StorageIntegrityIssue(StorageIntegrityIssueKind.TextChunkOrphan, segment.RootPageIndex, -1, 0,
                $"{orphans.Count} text chunk(s) are allocated and named by no row or revision; the first is chunk {orphans[0]}."));
        }
    }

    /// <summary>The text context of a migration to <paramref name="definition"/>, when its layout has a <c>String1024</c> field; null otherwise.</summary>
    private TextMigration TextMigrationFor(DBComponentDefinition definition)
    {
        foreach (var field in definition.FieldsByName.Values)
        {
            if (field.Type == Typhon.Schema.Definition.FieldType.String1024 && !field.IsStatic)
            {
                return new TextMigration(this);
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the pages of the texts this open's migrations produced in the cluster and WAL passes, which run after the per-component migration saved its
    /// own: before the commit that publishes the rows naming them (DC-01).
    /// </summary>
    internal void SaveMigrationTexts()
    {
        if (_migratedComponents != null)
        {
            TextMigration.SaveAll(_migratedComponents.Values);
        }
    }

    /// <summary>
    /// After a schema migration commits (#1094, TXT-04): every text chunk no live row and no revision names is freed — a dropped String1024 field's, the texts
    /// of the revisions the migration did not carry (it keeps HEAD only), a text a migration function replaced. Not before the commit: until then the
    /// pre-migration image is the durable one and names them (SCHEMA-09). The frees wait for a checkpoint, as every free of the segment does (CK-17).
    /// </summary>
    internal void SweepTextOrphansAfterMigration()
    {
        var segment = TextSegment;
        if (segment == null || FirstUnloadedTextHandleSource() != null)
        {
            return;
        }

        using var guard = EpochGuard.Enter(EpochManager);
        var orphans = CollectTextOrphans(segment, CollectNamedTextChunks());
        foreach (var chunkId in orphans)
        {
            segment.FreeChunk(chunkId);
        }

        TextOrphansSweptAfterMigration += orphans.Count;
    }

    /// <summary>Text chunks freed by <see cref="SweepTextOrphansAfterMigration"/> in this process. Diagnostic.</summary>
    internal int TextOrphansSweptAfterMigration { get; private set; }

    /// <summary>Loads the text segment a previous session created, before any component table reads a handle into it.</summary>
    private void LoadTextSegment(BootstrapDictionary bootstrap)
    {
        var spi = bootstrap.GetInt(BK_TextSegment);
        if (spi == 0)
        {
            return;
        }

        var segment = MMF.LoadChunkBasedSegment(spi, TextChunks.Stride);
        segment.QuarantineFrees(MMF);

        // A crash open does not trust the bitmap until ReserveTextChunksAfterCrash has taken every named chunk, and a schema migration of this open
        // allocates texts before that (at registration, in the cluster pass): until then the allocator hands out only chunks past what the file has.
        if (CrashRecoveryAtOpen)
        {
            segment.RaiseAllocationFloor(segment.ChunkCapacity);
        }

        Volatile.Write(ref _textSegment, segment);
    }
}
