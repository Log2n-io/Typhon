using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>Where a schema migration's commit stands; passed to <see cref="DatabaseEngine.SchemaCommitProbe"/>.</summary>
internal enum SchemaCommitStage
{
    /// <summary>The intent is durable and the commit staged; the checkpoint that commits it has not run.</summary>
    Staged,

    /// <summary>The meta flip carrying the commit is durable; no catalog row has been written yet.</summary>
    Committed,

    /// <summary>Every catalog row is written and flushed; the commit key is still set.</summary>
    Applied,

    /// <summary>At an open that found the commit key set: the component rows are written, the archetype rows are not.</summary>
    ComponentRowsAppliedAtOpen,

    /// <summary>At an open that found the commit key set: every row is written and flushed, the key is still set.</summary>
    AppliedAtOpen,
}

/// <summary>
/// A schema migration commits atomically (SCHEMA-09, #1469, #1473). Its catalog rows are not written in place while it runs: their new images go to an intent
/// segment, and one bootstrap key naming that segment is set in the same meta flip that advances CheckpointLSN — the recovery seal on a crash open, a
/// forced checkpoint on a clean one. Before the flip the file is the pre-migration image, untouched: the old segments are not freed and the WAL window is in
/// the catalog's revision. After it, the rows are written and the key cleared, and an open that finds the key set writes them again before reading the
/// catalog.
/// </summary>
public partial class DatabaseEngine
{
    /// <summary>
    /// Bootstrap key of a committed schema migration whose catalog rows may not all be written yet: (intent segment root, entry count, intent format).
    /// </summary>
    internal const string BK_SchemaCommit = "SchemaCommit";

    /// <summary>Layout of the intent's entries; an open refuses an intent it does not know how to read rather than misapplying it.</summary>
    internal const int SchemaIntentFormat = 1;

    // One catalog row image per chunk: a 16-byte header, then the row bytes. ComponentR1 and ArchetypeR1 rows fit the remaining 240 (a test pins it).
    internal const int SchemaIntentStride = 256;
    internal const int SchemaIntentHeaderSize = 16;

    /// <summary>The migration this open is running, until it commits. Null when the open migrates nothing.</summary>
    private PendingSchemaCommit _pendingSchemaCommit;

    /// <summary>The rows of a commit an earlier session made but did not finish applying, found at open; null when none.</summary>
    private List<CatalogPatch> _catalogPatchesFromIntent;

    private ChunkBasedSegment<PersistentStore> _schemaIntentSegmentAtOpen;

    /// <summary>
    /// Test seam: invoked at each <see cref="SchemaCommitStage"/> of this open's migration commit. A throw stops the open there, as a crash would. Null in
    /// production.
    /// </summary>
    internal Action<SchemaCommitStage> SchemaCommitProbe { get; set; }

    /// <summary>
    /// Test seam: <see cref="SchemaCommitProbe"/> for the at-open stages, which run inside the constructor, before a test can reach the engine. Per thread
    /// (the constructor runs on the resolving thread), so a fixture using it stays parallel-safe. Null in production.
    /// </summary>
    [ThreadStatic]
    internal static Action<SchemaCommitStage> SchemaCommitAtOpenProbeForTest;

    /// <summary>
    /// Test seam ([RuleMutant] of SCHEMA-09): writes the migrated components' catalog rows in place when the commit is staged — ahead of its flip, with the
    /// archetype rows still in the intent — which is how a migration persisted its catalog before SCHEMA-09. False in production.
    /// </summary>
    internal bool WriteComponentRowsBeforeTheFlipForTest { get; set; }

    /// <summary>Test seam: how long one wait for the commit's checkpoint lasts before progress is checked. 30 s in production.</summary>
    internal TimeSpan SchemaCommitWaitStepForTest { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether this open has a migration whose commit has not completed yet.</summary>
    internal bool HasPendingSchemaCommit => _pendingSchemaCommit != null;

    private sealed class PendingSchemaCommit
    {
        public readonly List<StagedComponentRow> ComponentRows = [];
        public readonly List<CatalogPatch> Patches = [];
        public readonly List<SchemaHistoryR1> History = [];
        public readonly List<ChunkBasedSegment<PersistentStore>> Superseded = [];
        public int UserSchemaVersionBumps;
        public ChunkBasedSegment<PersistentStore> Intent;
        public StagedMetaCommit Staged;
    }

    /// <summary>A migrated component's new catalog row, and the field list its old row named, freed once the new row is written.</summary>
    private readonly record struct StagedComponentRow(string SchemaName, int ChunkId, ComponentR1 Row, int OldFieldsBufferId);

    /// <summary>
    /// The new bytes of one catalog row: <see cref="Bytes"/> at <see cref="Offset"/> in chunk <see cref="ChunkId"/> of the segment rooted at
    /// <see cref="SegmentRoot"/>.
    /// </summary>
    private readonly struct CatalogPatch
    {
        public CatalogPatch(int segmentRoot, int stride, int chunkId, int offset, byte[] bytes)
        {
            SegmentRoot = segmentRoot;
            Stride = stride;
            ChunkId = chunkId;
            Offset = offset;
            Bytes = bytes;
        }

        public int SegmentRoot { get; }
        public int Stride { get; }
        public int ChunkId { get; }
        public int Offset { get; }
        public byte[] Bytes { get; }

        /// <summary>The patch <see cref="SystemCrud.Update{T}"/> of <paramref name="row"/> would make.</summary>
        public static CatalogPatch ForRow<T>(ComponentTable table, int chunkId, in T row) where T : unmanaged
        {
            var size = Math.Min(Unsafe.SizeOf<T>(), table.ComponentStorageSize);
            var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in row)).Slice(0, size).ToArray();
            return new CatalogPatch(table.ComponentSegment.RootPageIndex, table.ComponentSegment.Stride, chunkId, table.ComponentOverhead, bytes);
        }
    }

    /// <summary>
    /// Registration's half of a migration (SCHEMA-09): the component's new catalog row, its journal entry and its schema-version bump are held for the
    /// commit, and the segments the migration replaced are freed only after it.
    /// </summary>
    private void StageMigratedComponentRow(int chunkId, string schemaName, DBComponentDefinition definition, MigrationResult migration, SchemaDiff diff,
        int fromRevision)
    {
        var pending = _pendingSchemaCommit ??= new PendingSchemaCommit();

        SystemCrud.Read(_componentsTable, chunkId, out ComponentR1 oldRow, EpochManager);
        var cs = MMF.CreateChangeSet();
        var row = BuildPersistedComponentRow(chunkId, definition, migration, cs);
        cs.SaveChanges();

        // A rename in this same open re-stamps the row's name in place; the image must carry the name too, or applying it would undo the rename.
        row.Name = (String64)schemaName;

        pending.ComponentRows.Add(new StagedComponentRow(schemaName, chunkId, row, oldRow.Fields._bufferId));
        pending.Patches.Add(CatalogPatch.ForRow(_componentsTable, chunkId, in row));
        pending.UserSchemaVersionBumps++;
        if (_schemaHistoryTable != null)
        {
            pending.History.Add(BuildSchemaHistoryEntry(schemaName, diff, migration, fromRevision, definition.Revision));
        }

        if (migration.SupersededSegments != null)
        {
            pending.Superseded.AddRange(migration.SupersededSegments);
        }
    }

    /// <summary>
    /// Refuses a user migration chain whose types do not have the sizes of the layout it converts from and to. Its functions read and write raw component
    /// bytes through those types, so a mismatch would read past the stored value or write past the slot.
    /// </summary>
    private static void ThrowIfMigrationChainDoesNotFit(string schemaName, MigrationChain chain, int storedSize, int declaredSize)
    {
        var steps = chain.Steps;
        for (var i = 1; i < steps.Length; i++)
        {
            if (steps[i].OldSize != steps[i - 1].NewSize)
            {
                throw new InvalidOperationException(
                    $"The migration chain of component '{schemaName}' is inconsistent: step {i} reads a {steps[i].OldSize}-byte value but step {i - 1} "
                    + $"writes a {steps[i - 1].NewSize}-byte one.");
            }
        }

        if (steps[0].OldSize != storedSize || steps[^1].NewSize != declaredSize)
        {
            throw new InvalidOperationException(
                $"The migration chain of component '{schemaName}' converts a {steps[0].OldSize}-byte value into a {steps[^1].NewSize}-byte one, but the "
                + $"component is stored at {storedSize} bytes and this build declares it at {declaredSize}: the chain's types are not the component's.");
        }
    }

    /// <summary>
    /// Refuses a migration that would also change the component's storage mode. No migration path moves data between the two layouts — a Versioned
    /// component's bytes are in its segments and revision chains, a SingleVersion one's only in its archetype's cluster — so it would open one as the other.
    /// </summary>
    private static void ThrowIfMigrationChangesStorageMode(string schemaName, StorageMode persisted, StorageMode declared)
    {
        if (persisted != declared)
        {
            throw new InvalidOperationException(
                $"Component '{schemaName}' is stored as StorageMode.{persisted} and this revision declares StorageMode.{declared}, together with a layout "
                + "change. A migration cannot change the storage mode: keep the mode in this revision, or recreate the database.");
        }
    }

    /// <summary>
    /// The field-list buffers the pending migration's new rows name. Nothing durable names them until the commit, so whatever decides which buffers are live
    /// before it — the crash open's collection sweep — must count them too.
    /// </summary>
    private void NotePendingSchemaCommitBuffers(Dictionary<ChunkBasedSegment<PersistentStore>, HashSet<int>> named,
        ChunkBasedSegment<PersistentStore> segment)
    {
        if (_pendingSchemaCommit == null)
        {
            return;
        }

        foreach (var staged in _pendingSchemaCommit.ComponentRows)
        {
            NoteNamedBuffer(named, segment, staged.Row.Fields._bufferId);
        }
    }

    /// <summary>
    /// Makes the commit ready for the checkpoint that carries it: the archetype rows this open re-pointed join the component rows, every row image is written
    /// to a durable intent segment, and the bootstrap key naming it is staged for the next flip that advances CheckpointLSN.
    /// </summary>
    private void PrepareSchemaCommit()
    {
        var pending = _pendingSchemaCommit;
        if (pending.Staged != null)
        {
            throw new InvalidOperationException("This open's schema migration failed to commit; reopen the database to run it again.");
        }

        // Every archetype row that changed rides the commit — the migrated archetypes' new EntityMap, cluster and index segments above all — so the catalog
        // flips as one. The checkpoint hook stays disarmed until the rows are applied: it would write them in place, ahead of the flip.
        var archetypesTable = GetComponentTable<ArchetypeR1>();
        if (archetypesTable != null && _archetypeStates != null && _persistedArchetypes != null)
        {
            using var guard = EpochGuard.Enter(EpochManager);
            var cs = MMF.CreateChangeSet();
            PersistArchetypeStateInto(archetypesTable, cs, pending.Patches);
            cs.SaveChanges();
        }

        pending.Intent = WriteSchemaIntent(pending.Patches);

        if (WriteComponentRowsBeforeTheFlipForTest)
        {
            var early = MMF.CreateChangeSet();
            using (EpochGuard.Enter(EpochManager))
            {
                ApplyCatalogPatches(_componentsTable.ComponentSegment, [.. pending.Patches], early);
            }
            early.SaveChanges();
            MMF.FlushToDisk();
        }

        // Absolute values, computed here: a flip that failed after the mutation ran is retried, and the undo restores exactly what was there.
        var key = BootstrapDictionary.Value.FromInt3(pending.Intent.RootPageIndex, pending.Patches.Count, SchemaIntentFormat);
        var previousVersion = MMF.Bootstrap.GetInt(BK_UserSchemaVersion);
        var userSchemaVersion = previousVersion + pending.UserSchemaVersionBumps;
        pending.Staged = new StagedMetaCommit(
            () =>
            {
                MMF.Bootstrap.Set(BK_SchemaCommit, key);
                MMF.Bootstrap.SetInt(BK_UserSchemaVersion, userSchemaVersion);
            },
            () =>
            {
                MMF.Bootstrap.Remove(BK_SchemaCommit);
                MMF.Bootstrap.SetInt(BK_UserSchemaVersion, previousVersion);
            });
        CheckpointManager.StageMetaCommit(pending.Staged);

        SchemaCommitProbe?.Invoke(SchemaCommitStage.Staged);
    }

    /// <summary>
    /// Waits for the staged commit's flip for as long as the checkpoint makes progress: a wait step that ends with the commit still pending and no page
    /// written gives up. A fixed deadline would fail a large migration's open on every retry, and the database could never be migrated.
    /// </summary>
    private void WaitForSchemaCommit()
    {
        var staged = _pendingSchemaCommit.Staged;
        while (!staged.Committed)
        {
            var pagesBefore = CheckpointManager.TotalPagesWritten;
            CheckpointManager.ForceCheckpointAndWait(SchemaCommitWaitStepForTest);
            if (!staged.Committed && CheckpointManager.TotalPagesWritten == pagesBefore)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Finishes a commit the checkpoint has made durable: writes the catalog rows, clears the key, and only then frees the intent, the segments the migration
    /// replaced and the ones it abandoned, and journals the change.
    /// </summary>
    /// <exception cref="InvalidOperationException">The checkpoint that commits the migration did not complete; nothing of it is durable.</exception>
    private void CompleteSchemaCommit()
    {
        var pending = _pendingSchemaCommit;

        // Withdrawing it settles the race with a cycle still holding it: either no flip will carry it, or one already has. Withdrawn, no later cycle of this
        // session may advance CheckpointLSN either: the window it would consume was replayed into segments the catalog does not name.
        if (!pending.Staged.Committed && CheckpointManager.UnstageMetaCommit(pending.Staged))
        {
            CheckpointManager.RecoveryInProgress = true;
            throw new InvalidOperationException(
                "The schema migration could not be committed: the checkpoint that commits it did not complete. Nothing of the migration is durable — the "
                + "database is as it was before this open, and the next open runs the migration again.");
        }

        SchemaCommitProbe?.Invoke(SchemaCommitStage.Committed);

        var remaining = new List<CatalogPatch>(pending.Patches);
        var cs = MMF.CreateChangeSet();
        using (EpochGuard.Enter(EpochManager))
        {
            ApplyCatalogPatches(_componentsTable.ComponentSegment, remaining, cs);
            var archetypesTable = GetComponentTable<ArchetypeR1>();
            if (archetypesTable != null)
            {
                ApplyCatalogPatches(archetypesTable.ComponentSegment, remaining, cs);
            }
        }

        if (remaining.Count > 0)
        {
            throw new InvalidOperationException(
                $"{remaining.Count} catalog row(s) of the schema commit name a segment that is not the component or archetype catalog.");
        }

        cs.SaveChanges();
        MMF.FlushToDisk();

        SchemaCommitProbe?.Invoke(SchemaCommitStage.Applied);

        MMF.MutateBootstrapAndPersist(() => MMF.Bootstrap.Remove(BK_SchemaCommit));
        _pendingSchemaCommit = null;

        // The migration is no longer this open's to replay or rebuild: a repeat InitializeArchetypes (#790) finds the migrated catalog and treats it as such.
        _migratedComponents = null;

        foreach (var staged in pending.ComponentRows)
        {
            _persistedComponents[staged.SchemaName] = (staged.ChunkId, staged.Row);
        }

        DeleteSchemaIntent(pending.Intent);
        foreach (var segment in pending.Superseded)
        {
            MMF.DeleteSegment(segment);
        }
        ReleaseAbandonedMigrationSegments();
        FreeReplacedFieldLists(pending.ComponentRows);

        // The journal is an audit trail, written last: a crash between the commit and here loses these rows, never the migration.
        if (pending.History.Count > 0)
        {
            var historyCs = MMF.CreateChangeSet();
            foreach (var entry in pending.History)
            {
                var row = entry;
                SystemCrud.Create(_schemaHistoryTable, ref row, EpochManager, historyCs);
            }
            historyCs.SaveChanges();
        }
    }

    /// <summary>
    /// Gives back the field lists the migrated rows used to name. Leaked otherwise: only a crash open's collection sweep would reclaim them. A crash before
    /// this point leaves them to that sweep, which finds them named by nothing.
    /// </summary>
    private void FreeReplacedFieldLists(List<StagedComponentRow> rows)
    {
        var segment = GetComponentCollectionSegment<FieldR1>();
        var vsbs = GetComponentCollectionVSBS<FieldR1>();
        using var guard = EpochGuard.Enter(EpochManager);
        var cs = MMF.CreateChangeSet();
        var accessor = segment.CreateChunkAccessor(cs);
        try
        {
            foreach (var staged in rows)
            {
                var old = staged.OldFieldsBufferId;
                if (old != 0 && old != staged.Row.Fields._bufferId && segment.IsChunkAllocated(old))
                {
                    vsbs.DeleteBuffer(old, ref accessor);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        cs.SaveChanges();
    }

    /// <summary>The clean-open commit: a forced checkpoint carries the flip. A crash open's commit rides its recovery seal instead.</summary>
    private void CommitPendingSchemaAtCleanOpen()
    {
        PrepareSchemaCommit();
        WaitForSchemaCommit();
        CompleteSchemaCommit();
    }

    private ChunkBasedSegment<PersistentStore> WriteSchemaIntent(List<CatalogPatch> patches)
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var cs = MMF.CreateChangeSet();
        var segment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 1, SchemaIntentStride, cs, StorageSegmentKind.System);
        var accessor = segment.CreateChunkAccessor(cs);
        try
        {
            foreach (var patch in patches)
            {
                if (patch.Bytes.Length > SchemaIntentStride - SchemaIntentHeaderSize)
                {
                    throw new InvalidOperationException($"A catalog row of {patch.Bytes.Length} bytes does not fit a schema intent entry.");
                }

                var chunkId = segment.AllocateChunk(cs, ref accessor);
                var dst = accessor.GetChunkAsSpan(chunkId, true);
                BinaryPrimitives.WriteInt32LittleEndian(dst, patch.SegmentRoot);
                BinaryPrimitives.WriteInt32LittleEndian(dst[4..], patch.Stride);
                BinaryPrimitives.WriteInt32LittleEndian(dst[8..], patch.ChunkId);
                BinaryPrimitives.WriteUInt16LittleEndian(dst[12..], (ushort)patch.Offset);
                BinaryPrimitives.WriteUInt16LittleEndian(dst[14..], (ushort)patch.Bytes.Length);
                patch.Bytes.CopyTo(dst[SchemaIntentHeaderSize..]);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        // Durable before the key that names it can be: the flip comes after.
        cs.SaveChanges();
        MMF.FlushToDisk();
        return segment;
    }

    /// <summary>The intent's entries, in the order they were written — the order they are applied in.</summary>
    private static List<CatalogPatch> ReadSchemaIntent(ChunkBasedSegment<PersistentStore> segment, int expectedCount)
    {
        var patches = new List<CatalogPatch>(expectedCount);
        var accessor = segment.CreateChunkAccessor();
        try
        {
            for (var chunkId = 1; chunkId < segment.ChunkCapacity; chunkId++)
            {
                if (!segment.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                var src = accessor.GetChunkAsReadOnlySpan(chunkId);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(src[14..]);
                if (length > SchemaIntentStride - SchemaIntentHeaderSize)
                {
                    throw new InvalidDataException($"The schema commit intent is corrupt: entry {chunkId} claims {length} bytes.");
                }

                patches.Add(new CatalogPatch(
                    BinaryPrimitives.ReadInt32LittleEndian(src),
                    BinaryPrimitives.ReadInt32LittleEndian(src[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(src[8..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(src[12..]),
                    src.Slice(SchemaIntentHeaderSize, length).ToArray()));
            }
        }
        finally
        {
            accessor.Dispose();
        }

        if (patches.Count != expectedCount)
        {
            throw new InvalidDataException(
                $"The schema commit intent is corrupt: the commit names {expectedCount} catalog row(s), the intent holds {patches.Count}.");
        }

        return patches;
    }

    private void DeleteSchemaIntent(ChunkBasedSegment<PersistentStore> segment)
    {
        var cs = MMF.CreateChangeSet();
        MMF.DeleteSegment(segment, cs);
        cs.SaveChanges();
    }

    /// <summary>
    /// Writes, through <paramref name="cs"/> and in list order, each patch of <paramref name="patches"/> that targets <paramref name="segment"/>, removing
    /// it from the list.
    /// </summary>
    private static void ApplyCatalogPatches(ChunkBasedSegment<PersistentStore> segment, List<CatalogPatch> patches, ChangeSet cs)
    {
        var accessor = segment.CreateChunkAccessor(cs);
        var kept = 0;
        try
        {
            for (var i = 0; i < patches.Count; i++)
            {
                var patch = patches[i];
                if (patch.SegmentRoot != segment.RootPageIndex)
                {
                    patches[kept++] = patch;
                    continue;
                }

                if (patch.Stride != segment.Stride)
                {
                    throw new InvalidDataException(
                        $"A schema commit row targets segment {patch.SegmentRoot} at stride {patch.Stride}, but the segment's stride is {segment.Stride}.");
                }

                var dst = accessor.GetChunkAsSpan(patch.ChunkId, true);
                patch.Bytes.CopyTo(dst[patch.Offset..]);
            }

            patches.RemoveRange(kept, patches.Count - kept);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// At open, before the catalog is read: a commit key left set means an earlier session committed a migration and crashed before writing all its rows.
    /// The component rows are written here; the archetype rows wait for their table (<see cref="ApplyCommittedSchemaIntentToArchetypes"/>).
    /// </summary>
    private void ApplyCommittedSchemaIntentToComponents()
    {
        if (!MMF.Bootstrap.TryGet(BK_SchemaCommit, out var commit))
        {
            return;
        }

        if (commit.GetInt(2) != SchemaIntentFormat)
        {
            throw new InvalidDataException(
                $"This database holds a committed schema migration in intent format {commit.GetInt(2)}, which this build does not know "
                + $"(it reads format {SchemaIntentFormat}). Open it with the build that wrote it.");
        }

        _schemaIntentSegmentAtOpen = MMF.LoadChunkBasedSegment(commit.GetInt(), SchemaIntentStride)
            ?? throw new InvalidDataException("The schema commit intent named by the bootstrap could not be loaded.");
        _catalogPatchesFromIntent = ReadSchemaIntent(_schemaIntentSegmentAtOpen, commit.GetInt(1));

        var cs = MMF.CreateChangeSet();
        using (EpochGuard.Enter(EpochManager))
        {
            ApplyCatalogPatches(_componentsTable.ComponentSegment, _catalogPatchesFromIntent, cs);
        }
        cs.SaveChanges();

        SchemaCommitAtOpenProbeForTest?.Invoke(SchemaCommitStage.ComponentRowsAppliedAtOpen);

        if (_catalogPatchesFromIntent.Count == 0)
        {
            FinishCommittedSchemaIntentAtOpen();
        }
    }

    /// <summary>
    /// The archetype half of <see cref="ApplyCommittedSchemaIntentToComponents"/>, once the ArchetypeR1 table is loaded and before it is read.
    /// </summary>
    private void ApplyCommittedSchemaIntentToArchetypes()
    {
        if (_catalogPatchesFromIntent == null)
        {
            return;
        }

        var cs = MMF.CreateChangeSet();
        using (EpochGuard.Enter(EpochManager))
        {
            ApplyCatalogPatches(GetComponentTable<ArchetypeR1>().ComponentSegment, _catalogPatchesFromIntent, cs);
        }
        cs.SaveChanges();

        if (_catalogPatchesFromIntent.Count > 0)
        {
            throw new InvalidDataException(
                $"{_catalogPatchesFromIntent.Count} catalog row(s) of a committed schema migration name a segment that is not the component or archetype "
                + "catalog.");
        }

        FinishCommittedSchemaIntentAtOpen();
    }

    private void FinishCommittedSchemaIntentAtOpen()
    {
        MMF.FlushToDisk();
        SchemaCommitAtOpenProbeForTest?.Invoke(SchemaCommitStage.AppliedAtOpen);
        MMF.MutateBootstrapAndPersist(() => MMF.Bootstrap.Remove(BK_SchemaCommit));
        DeleteSchemaIntent(_schemaIntentSegmentAtOpen);
        _schemaIntentSegmentAtOpen = null;
        _catalogPatchesFromIntent = null;
    }
}
