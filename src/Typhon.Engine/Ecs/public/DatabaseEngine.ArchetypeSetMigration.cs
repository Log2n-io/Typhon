using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

// DatabaseEngine — archetype component-set evolution (partial, #1394, claude/design/Schema/07-archetype-set-evolution.md). A change of an archetype's
// component set, or of its order, is matched by component NAME (SCHEMA-11, ClassifyArchetypeSchema) and migrates at open: the existing entities are re-placed
// into the new cluster geometry by the field-migration rebuild (RebuildClusterFromChains, SCHEMA-12), and the archetype row is rewritten in the schema
// commit's single flip (SCHEMA-09). This file holds the state that carries one such change from the classification to the commit.
public partial class DatabaseEngine
{
    /// <summary>
    /// One archetype whose persisted row this open rewrites (design 07 §5.2): a component-set migration (<see cref="ArchetypeSchemaVerdict.SetChange"/>), or
    /// a metadata-only change (<see cref="ArchetypeSchemaVerdict.RevisionOnly"/>, <see cref="ArchetypeSchemaVerdict.NamesRefreshed"/>). A class, not a
    /// record: the commit and the re-placement fill its outcome in place, and value equality over mutable state would be a trap. Named "Plan" so it does
    /// not collide with <see cref="DatabaseEngineOptions.ArchetypeSetMigration"/>.
    /// </summary>
    private sealed class ArchetypeSetMigrationPlan
    {
        /// <param name="archetypeName">The durable name.</param>
        /// <param name="verdict">What the classification found.</param>
        /// <param name="diff">Persisted (rename-resolved) slot order against runtime slot order. Identity for a metadata-only change.</param>
        /// <param name="fromRevision">The persisted revision.</param>
        /// <param name="toRevision">The runtime revision.</param>
        /// <param name="oldLayout">The geometry the persisted cluster was written at (<see cref="BuildPersistedLayout"/>); default for a metadata-only
        /// change.</param>
        /// <param name="oldStorageModes">Per persisted slot, its storage mode; null for a metadata-only change.</param>
        /// <param name="oldVersionedSlotMask">The persisted Versioned slots.</param>
        public ArchetypeSetMigrationPlan(string archetypeName, ArchetypeSchemaVerdict verdict, ArchetypeSetDiff diff, int fromRevision, int toRevision,
            ArchetypeClusterInfo oldLayout, StorageMode[] oldStorageModes, ushort oldVersionedSlotMask)
        {
            ArchetypeName = archetypeName;
            Verdict = verdict;
            Diff = diff;
            FromRevision = fromRevision;
            ToRevision = toRevision;
            OldLayout = oldLayout;
            OldStorageModes = oldStorageModes;
            OldVersionedSlotMask = oldVersionedSlotMask;
        }

        public string ArchetypeName { get; }

        public ArchetypeSchemaVerdict Verdict { get; }

        public ArchetypeSetDiff Diff { get; }

        public int FromRevision { get; }

        public int ToRevision { get; }

        public ArchetypeClusterInfo OldLayout { get; }

        public StorageMode[] OldStorageModes { get; }

        public ushort OldVersionedSlotMask { get; }

        /// <summary>Whether the change moves data: a set change re-places every entity; a metadata-only change rewrites the row alone.</summary>
        public bool MovesData => Verdict == ArchetypeSchemaVerdict.SetChange;

        /// <summary>The names buffer the commit's row names, allocated in <see cref="PrepareSchemaCommit"/>; 0 until then.</summary>
        public int NewComponentNamesBufferId;

        /// <summary>The entities the re-placement placed.</summary>
        public int EntitiesMigrated;

        /// <summary>Wall-clock time of the re-placement and of the rebuilds over it.</summary>
        public double ElapsedMs;
    }

    /// <summary>
    /// The archetypes whose persisted row this open rewrites, keyed by CATALOG id (SCHEMA-03); null when none. Filled by the classification, consumed by the
    /// rebuild and by <see cref="PersistArchetypeStateInto"/>, reset by <see cref="CompleteSchemaCommit"/>.
    /// </summary>
    private Dictionary<ushort, ArchetypeSetMigrationPlan> _archetypeSetMigrations;

    /// <summary>
    /// The data-moving entries of <see cref="_archetypeSetMigrations"/>, keyed by ROUTING id: what the WAL addresses, whose window above CheckpointLSN is in
    /// the committed slot numbering (SCHEMA-09). Null on every open that migrates no archetype set.
    /// </summary>
    private ArchetypeSetMigrationPlan[] _setMigrationByRouting;

    private List<ArchetypeSetMigrationReport> _lastOpenArchetypeSetMigrations;

    /// <summary>
    /// Every archetype whose component set the last <see cref="InitializeArchetypes"/> migrated, with what changed, how many entities moved and how long it
    /// took. Empty when none did. Metadata-only changes (a revision bump with the same set, stored names refreshed after a component rename) are not listed.
    /// </summary>
    public IReadOnlyList<ArchetypeSetMigrationReport> LastOpenArchetypeSetMigrations
        => (IReadOnlyList<ArchetypeSetMigrationReport>)_lastOpenArchetypeSetMigrations?.AsReadOnly() ?? [];

    private List<ArchetypeSetChange> _refusedArchetypeSetChanges;

    /// <summary>
    /// Every archetype the last <see cref="InitializeArchetypes"/> skipped under <see cref="ArchetypeSetMigrationPolicy.Refuse"/> because it would have
    /// migrated: its row and segments are as stored, and it has no storage in this engine. Also lists each archetype skipped for an unregistered component
    /// whose stored set differs from its declaration: as a set change, or as <see cref="ArchetypeSetChangeKind.Refused"/> when an open consuming it would
    /// refuse it. Empty under <see cref="ArchetypeSetMigrationPolicy.Apply"/>.
    /// </summary>
    public IReadOnlyList<ArchetypeSetChange> RefusedArchetypeSetChanges
        => (IReadOnlyList<ArchetypeSetChange>)_refusedArchetypeSetChanges?.AsReadOnly() ?? [];

    /// <summary>
    /// Classifies every resolved archetype before any is built (design 07 §5.1, §8): every refusal (<see cref="ArchetypeSchemaException"/>: R1, R3-R6) is
    /// thrown here, before the build loop allocates the first fresh segment. Under <see cref="ArchetypeSetMigrationPolicy.Refuse"/> (D4) it also SKIPS each
    /// archetype whose verdict would rewrite its row — a set change, a revision-only bump, stale stored names: it leaves <paramref name="resolved"/>, is
    /// recorded in <see cref="_skippedArchetypes"/> (reason <see cref="ArchetypeSkipReason.ArchetypeSetChange"/>) and in
    /// <see cref="RefusedArchetypeSetChanges"/>, and nothing of it is built, so its row and segments stay as stored. Returns the classification of every
    /// archetype it kept, so the build loop does not classify twice; null when the file holds no archetype row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both policies classify here. Under Apply the classification used to run inside the build loop, so a refusal of a later archetype came after earlier
    /// ones had allocated fresh EntityMaps and clusters, orphaned until the failed-open sweep.
    /// </para>
    /// <para>
    /// Runs before <see cref="GuardUnopenedHoldersOfMigratedComponents"/>, so a component migrating under a skipped archetype still refuses the open (R10).
    /// Under Refuse a refusal of one archetype (R1, R3-R6) skips that archetype and is listed with its message, rather than refusing an inspector's whole
    /// open (D4: the rest of the database opens); nothing of the archetype is built, so nothing illegal is ever applied. Under Apply it refuses the open.
    /// </para>
    /// <para>
    /// An inspector registers only the components the file holds (it never creates one), so a component a set change ADDS is usually unregistered, and the
    /// archetype was already skipped for that unresolved slot before this runs. Under Refuse such an archetype is reported too, when no class of its durable
    /// name is consumed and its declared names differ from the stored ones: otherwise the most common set change would reach an inspector as nothing at all.
    /// </para>
    /// </remarks>
    private Dictionary<ushort, (string[] PersistedNames, ArchetypeSetMigrationPlan Set)> ClassifyArchetypesBeforeBuild(
        Dictionary<ushort, ComponentTable[]> resolved, Dictionary<string, string> renameMap)
    {
        if (_persistedArchetypes.Count == 0)
        {
            return null;
        }

        var refusePolicy = _options.ArchetypeSetMigration == ArchetypeSetMigrationPolicy.Refuse;
        var kept = new Dictionary<ushort, (string[], ArchetypeSetMigrationPlan)>();
        List<ushort> refused = null;
        foreach (var (archetypeId, slotToTable) in resolved)
        {
            var meta = ArchetypeRegistry.GetMetadata(archetypeId);
            ArchetypeSchemaVerdict verdict;
            string[] persistedNames;
            ArchetypeSetMigrationPlan set;
            try
            {
                verdict = ClassifyArchetypeSchema(meta, slotToTable, renameMap, out persistedNames, out set);
            }
            catch (ArchetypeSchemaException ex) when (refusePolicy && IsPerArchetypeRefusal(ex.Kind))
            {
                // D4: an inspector opens the rest of the database. The archetype a build's declaration would refuse is skipped and reported with the
                // refusal, as a set change it would not apply is; under Apply the same refusal refuses the open.
                (refused ??= []).Add(archetypeId);
                (_skippedArchetypes ??= [])[archetypeId] = new SkippedArchetype(ArchetypeSkipReason.SchemaRefused, -1, 0);
                TryGetPersistedArchetype(meta, out var refusedRow);
                var refusedChange = new ArchetypeSetChange(meta.Name, refusedRow.Arch.Revision, meta.Revision, ArchetypeSetChangeKind.Refused, ex.Message);
                (_refusedArchetypeSetChanges ??= []).Add(refusedChange);
                LogArchetypeSetChangeRefused(refusedChange.ArchetypeName, refusedChange.FromRevision, refusedChange.ToRevision, refusedChange.Kind,
                    refusedChange.Changes);
                continue;
            }

            if (!refusePolicy || verdict is ArchetypeSchemaVerdict.New or ArchetypeSchemaVerdict.Identical)
            {
                kept[archetypeId] = (persistedNames, set);
                continue;
            }

            (refused ??= []).Add(archetypeId);
            (_skippedArchetypes ??= [])[archetypeId] = new SkippedArchetype(ArchetypeSkipReason.ArchetypeSetChange, -1, 0);
            TryGetPersistedArchetype(meta, out var row);
            var changes = verdict == ArchetypeSchemaVerdict.SetChange ? set.Diff.Describe() : string.Empty;
            var kind = verdict switch
            {
                ArchetypeSchemaVerdict.SetChange => ArchetypeSetChangeKind.SetChange,
                ArchetypeSchemaVerdict.RevisionOnly => ArchetypeSetChangeKind.RevisionOnly,
                _ => ArchetypeSetChangeKind.NamesRefreshed,
            };
            var refusal = new ArchetypeSetChange(meta.Name, row.Arch.Revision, meta.Revision, kind, changes);
            (_refusedArchetypeSetChanges ??= []).Add(refusal);
            LogArchetypeSetChangeRefused(refusal.ArchetypeName, refusal.FromRevision, refusal.ToRevision, refusal.Kind, refusal.Changes);
        }

        if (refused != null)
        {
            foreach (var archetypeId in refused)
            {
                resolved.Remove(archetypeId);
            }
        }

        if (refusePolicy)
        {
            foreach (var (refusal, _) in CollectUnresolvedArchetypeSetChanges(ConsumedArchetypeNames(resolved.Keys), renameMap))
            {
                (_refusedArchetypeSetChanges ??= []).Add(refusal);
                LogArchetypeSetChangeRefused(refusal.ArchetypeName, refusal.FromRevision, refusal.ToRevision, refusal.Kind, refusal.Changes);
            }
        }

        return kept;
    }

    /// <summary>
    /// The refusals an archetype's classification raises about that archetype alone (R1, R3, R4, R5, R6), which under
    /// <see cref="ArchetypeSetMigrationPolicy.Refuse"/> skip it instead of refusing the open. R8 (two classes of one name) and R10 (a component migrating
    /// under an unopened holder) are not of one archetype, and refuse under either policy.
    /// </summary>
    private static bool IsPerArchetypeRefusal(ArchetypeSchemaErrorKind kind) => kind is ArchetypeSchemaErrorKind.RevisionNotBumped
        or ArchetypeSchemaErrorKind.Downgrade or ArchetypeSchemaErrorKind.ComponentRemoved or ArchetypeSchemaErrorKind.NamesUnreadable
        or ArchetypeSchemaErrorKind.CatalogRowMissing;

    /// <summary>One archetype an open skips for an unresolved slot although its stored set differs (<see cref="PreviewArchetypeSetChanges"/>).</summary>
    /// <param name="Change">What the stored row and the declaration differ by; <see cref="ArchetypeSetChangeKind.Refused"/> when an open that consumed it
    /// would refuse it, the refusal in <see cref="ArchetypeSetChange.Changes"/>.</param>
    /// <param name="Reason">Why the archetype is skipped: the slot this engine does not resolve.</param>
    internal readonly record struct ArchetypeSetPreviewSkip(ArchetypeSetChange Change, string Reason);

    /// <summary>The dry run of the archetype half of an open (<see cref="PreviewArchetypeSetChanges"/>).</summary>
    /// <param name="Changes">Every archetype whose stored row the open would rewrite (a migration, or a metadata-only change).</param>
    /// <param name="Skipped">Every archetype the open would skip for an unresolved slot although its stored set differs.</param>
    /// <param name="Refusals">The message of every refusal the open would throw that the preview can decide.</param>
    /// <param name="NotChecked">The refusals that depend on the entities and revision chains, which only the open reads, one line per archetype that could
    /// meet one.</param>
    internal sealed record ArchetypeSetPreview(IReadOnlyList<ArchetypeSetChange> Changes, IReadOnlyList<ArchetypeSetPreviewSkip> Skipped,
        IReadOnlyList<string> Refusals, IReadOnlyList<string> NotChecked);

    /// <summary>
    /// A dry run of the archetype half of the next <see cref="InitializeArchetypes"/> (#1394, <c>tsh schema-validate</c>), under this engine's policy: every
    /// archetype whose stored row it would rewrite, every archetype it would skip for an unresolved slot although its stored set differs, the message of
    /// every refusal it would throw that can be decided without the data (R1, R3-R6, R8, R10, and R7 / R12 / R9 for an archetype that never held an entity),
    /// and a line for each archetype whose data-dependent refusals (R7, R9, R12) only the open can decide. Builds nothing and writes nothing: no archetype
    /// state, no schema commit, and component migrations staged by registration stay staged. Call it before <see cref="InitializeArchetypes"/>; afterwards
    /// the rows already describe the runtime sets.
    /// </summary>
    /// <remarks>
    /// Not exact in one corner: on a file whose last open committed a schema intent it did not finish applying (SCHEMA-09), the rows read here predate
    /// the patches <see cref="InitializeArchetypes"/> re-applies first. And R11 (the pre-migration cluster cannot be loaded or validated) is never checked:
    /// it needs the segments, which a preview must not load (a segment loaded here would be registered, and the open's own load of it refused).
    /// </remarks>
    internal ArchetypeSetPreview PreviewArchetypeSetChanges()
    {
        var changes = new List<ArchetypeSetChange>();
        var skipped = new List<ArchetypeSetPreviewSkip>();
        var refusals = new List<string>();
        var notChecked = new List<string>();
        var preview = new ArchetypeSetPreview(changes, skipped, refusals, notChecked);
        if (GetComponentTable<ArchetypeR1>() == null)
        {
            // Registering a table the file does not hold would create it: a file with no archetype row has nothing to preview.
            var rowComponent = typeof(ArchetypeR1).GetCustomAttribute<ComponentAttribute>()?.Name;
            if (rowComponent == null || _persistedComponents == null || !_persistedComponents.ContainsKey(rowComponent))
            {
                return preview;
            }

            RegisterComponentFromAccessor<ArchetypeR1>();
        }

        // The same passes as the open, on state of the preview's own, restored after: the persisted rows (the open reads them only after re-applying a
        // committed intent, which the preview does not), and the skip reasons a use of an archetype would report. The superseded warning is the open's to
        // log, once.
        var savedPersisted = _persistedArchetypes;
        var savedSkipped = _skippedArchetypes;
        _persistedArchetypes = new Dictionary<string, (int, ArchetypeR1)>();
        _skippedArchetypes = null;
        var refusePolicy = _options.ArchetypeSetMigration == ArchetypeSetMigrationPolicy.Refuse;
        try
        {
            LoadPersistedArchetypes();
            if (_persistedArchetypes.Count == 0)
            {
                return preview;
            }

            var resolved = ResolveArchetypeSlots();
            ResolveDurableNameDuplicates(resolved, log: false);
            var renameMap = BuildComponentRenameMap();
            List<ushort> skippedByPolicy = null;
            foreach (var (archetypeId, slotToTable) in resolved)
            {
                var meta = ArchetypeRegistry.GetMetadata(archetypeId);
                try
                {
                    var verdict = ClassifyArchetypeSchema(meta, slotToTable, renameMap, out _, out var set);
                    if (verdict is ArchetypeSchemaVerdict.New or ArchetypeSchemaVerdict.Identical)
                    {
                        continue;
                    }

                    if (refusePolicy)
                    {
                        // As the open records it, so R10 below names the policy as the reason the row is not opened (the open clears it first).
                        (skippedByPolicy ??= []).Add(archetypeId);
                        (_skippedArchetypes ??= [])[archetypeId] = new SkippedArchetype(ArchetypeSkipReason.ArchetypeSetChange, -1, 0);
                    }

                    TryGetPersistedArchetype(meta, out var row);
                    var kind = verdict switch
                    {
                        ArchetypeSchemaVerdict.SetChange => ArchetypeSetChangeKind.SetChange,
                        ArchetypeSchemaVerdict.RevisionOnly => ArchetypeSetChangeKind.RevisionOnly,
                        _ => ArchetypeSetChangeKind.NamesRefreshed,
                    };
                    changes.Add(new ArchetypeSetChange(meta.Name, row.Arch.Revision, meta.Revision, kind,
                        verdict == ArchetypeSchemaVerdict.SetChange ? set.Diff.Describe() : string.Empty));
                    if (!refusePolicy && set is { MovesData: true })
                    {
                        PreviewDataDependentRefusals(meta, slotToTable, set, row.Arch, notChecked);
                    }
                }
                catch (ArchetypeSchemaException ex)
                {
                    refusals.Add(ex.Message);
                }
            }

            if (skippedByPolicy != null)
            {
                foreach (var archetypeId in skippedByPolicy)
                {
                    resolved.Remove(archetypeId);
                }
            }

            // Under Apply an unresolved archetype is skipped without being classified: it neither migrates nor refuses. What an open that consumed it
            // would do is said beside it.
            foreach (var (change, meta) in CollectUnresolvedArchetypeSetChanges(ConsumedArchetypeNames(resolved.Keys), renameMap))
            {
                skipped.Add(new ArchetypeSetPreviewSkip(change, SkippedArchetypeReason(meta.ArchetypeId)));
            }

            // R10 last, as in the open: it reads which rows the open consumes, after the policy has skipped its archetypes.
            try
            {
                GuardUnopenedHoldersOfMigratedComponents(resolved, renameMap);
            }
            catch (ArchetypeSchemaException ex)
            {
                refusals.Add(ex.Message);
            }
        }
        catch (ArchetypeSchemaException ex)
        {
            // R8: two consumed classes of one name at one revision; the open refuses before classifying anything.
            refusals.Add(ex.Message);
        }
        finally
        {
            _persistedArchetypes = savedPersisted;
            _skippedArchetypes = savedSkipped;
        }

        return preview;
    }

    /// <summary>
    /// The data-dependent refusals of a set change, for the preview: R12 (an added component carries the spatial field or the realm key), R7 (an added
    /// component's unique dense index) and R9 (an added Versioned component's stale chains) need the entities and the chains, which only the open reads.
    /// An archetype whose row never handed out an entity key holds neither, and passes all three; any other that could meet one gets a line saying it is
    /// not checked.
    /// </summary>
    private static void PreviewDataDependentRefusals(ArchetypeMetadata meta, ComponentTable[] slotToTable, ArchetypeSetMigrationPlan set, in ArchetypeR1 row,
        List<string> notChecked)
    {
        if (row.NextEntityKey == 0)
        {
            return;
        }

        List<string> rules = null;
        for (var rest = (uint)set.Diff.AddedMask; rest != 0; rest &= rest - 1)
        {
            var table = slotToTable[BitOperations.TrailingZeroCount(rest)];
            var definition = table.Definition;
            if (table.SpatialIndex != null || definition.RealmKeyField != null)
            {
                (rules ??= []).Add($"'{definition.Name}' carries its spatial field or realm key");
            }

            if (HasUniqueDenseIndex(definition, out var field))
            {
                (rules ??= []).Add($"'{definition.Name}.{field}' is a unique, non-sparse index");
            }

            if (table.StorageMode == StorageMode.Versioned)
            {
                (rules ??= []).Add($"'{definition.Name}' is Versioned and may meet leftover revision chains");
            }
        }

        if (rules != null)
        {
            notChecked.Add($"Archetype '{meta.Name}' gains components whose refusal depends on its entities ({string.Join("; ", rules)}), which only the open "
                           + "reads: not checked by this preview.");
        }
    }

    /// <summary>
    /// After an open: every archetype it skipped for an unresolved slot although its stored set differs from its declaration — what <c>tsh migrate</c>
    /// reports as not migrated. Empty when none.
    /// </summary>
    internal IReadOnlyList<ArchetypeSetPreviewSkip> UnresolvedArchetypeSetChanges()
    {
        if (_skippedArchetypes == null || _persistedArchetypes == null || _persistedArchetypes.Count == 0)
        {
            return [];
        }

        var consumed = new List<ushort>();
        foreach (var meta in OwnArchetypes())
        {
            if (_archetypeStates[meta.ArchetypeId]?.SlotToComponentTable != null)
            {
                consumed.Add(meta.ArchetypeId);
            }
        }

        var result = new List<ArchetypeSetPreviewSkip>();
        foreach (var (change, meta) in CollectUnresolvedArchetypeSetChanges(ConsumedArchetypeNames(consumed), BuildComponentRenameMap()))
        {
            result.Add(new ArchetypeSetPreviewSkip(change, SkippedArchetypeReason(meta.ArchetypeId)));
        }

        return result;
    }

    /// <summary>The durable names of the archetypes an open consumes, plus those it skipped under Refuse (they have their report already).</summary>
    private HashSet<string> ConsumedArchetypeNames(IEnumerable<ushort> consumed)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archetypeId in consumed)
        {
            names.Add(ArchetypeRegistry.GetMetadata(archetypeId).Name);
        }

        if (_refusedArchetypeSetChanges != null)
        {
            foreach (var r in _refusedArchetypeSetChanges)
            {
                names.Add(r.ArchetypeName);
            }
        }

        return names;
    }

    /// <summary>
    /// Every archetype skipped for an unresolved slot whose durable name has a stored row, no class in <paramref name="consumedNames"/>, and a declared set
    /// that differs from the stored one by name, newest declaration first, one per durable name. Each is judged as <see cref="ClassifyArchetypeSchema"/>
    /// would judge it once consumed: a downgrade (R3), a change with no effective revision increase (R1) or a removal (R4) is
    /// <see cref="ArchetypeSetChangeKind.Refused"/>, the refusal in <see cref="ArchetypeSetChange.Changes"/>; anything else is a
    /// <see cref="ArchetypeSetChangeKind.SetChange"/>. Its skip reason stays the unresolved slot, which is what any use of it reports.
    /// </summary>
    /// <remarks>A report, never a refusal: an unreadable list (R5) is refused by the open that consumes the archetype, not by one that skips it.</remarks>
    private List<(ArchetypeSetChange Change, ArchetypeMetadata Meta)> CollectUnresolvedArchetypeSetChanges(HashSet<string> consumedNames,
        Dictionary<string, string> renameMap)
    {
        var result = new List<(ArchetypeSetChange, ArchetypeMetadata)>();
        if (_skippedArchetypes == null)
        {
            return result;
        }

        // Highest revision first, so one report per durable name names the newest declaration.
        List<ArchetypeMetadata> candidates = null;
        foreach (var (archetypeId, skipped) in _skippedArchetypes)
        {
            var meta = ArchetypeRegistry.GetMetadata(archetypeId);
            if (skipped.Reason == ArchetypeSkipReason.UnresolvedSlot && meta?._slotToComponentType != null && !consumedNames.Contains(meta.Name)
                && TryGetPersistedArchetype(meta, out _))
            {
                (candidates ??= []).Add(meta);
            }
        }

        if (candidates == null)
        {
            return result;
        }

        candidates.Sort((a, b) => b.Revision.CompareTo(a.Revision));
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meta in candidates)
        {
            if (!reported.Add(meta.Name) || !TryGetPersistedArchetype(meta, out var row))
            {
                continue;
            }

            var storedRevision = row.Arch.Revision;
            if (meta.Revision < storedRevision)
            {
                result.Add((Refused(meta, storedRevision, DowngradeRefusal(meta.Name, storedRevision, meta.Revision)), meta));
                continue;
            }

            var runtime = DeclaredComponentNames(meta);
            string[] stored;
            try
            {
                stored = ReadPersistedComponentNames(row.Arch, meta.Name);
            }
            catch (ArchetypeSchemaException)
            {
                continue;
            }

            for (var o = 0; o < stored.Length; o++)
            {
                stored[o] = ResolvePersistedComponentName(stored[o], renameMap);
            }

            if (runtime == null || ArchetypeSetDiff.FindDuplicate(stored) >= 0 || ArchetypeSetDiff.FindDuplicate(runtime) >= 0)
            {
                continue;
            }

            var diff = ArchetypeSetDiff.Compute(stored, runtime);
            if (diff.IsIdentical)
            {
                continue;
            }

            if (meta.Revision == storedRevision && !AncestorRevisionIncreased(meta, renameMap, stored, runtime))
            {
                result.Add((Refused(meta, storedRevision, RevisionNotBumpedRefusal(meta.Name, diff, storedRevision)), meta));
            }
            else if (diff.HasRemovals)
            {
                result.Add((Refused(meta, storedRevision, ComponentRemovedRefusal(meta.Name, diff, storedRevision, meta.Revision)), meta));
            }
            else
            {
                result.Add((new ArchetypeSetChange(meta.Name, storedRevision, meta.Revision, ArchetypeSetChangeKind.SetChange, diff.Describe()), meta));
            }
        }

        return result;

        static ArchetypeSetChange Refused(ArchetypeMetadata meta, int storedRevision, ArchetypeSchemaException refusal)
            => new(meta.Name, storedRevision, meta.Revision, ArchetypeSetChangeKind.Refused, refusal.Message);
    }

    /// <summary>
    /// The archetype a crash open's WAL record names by <paramref name="routingId"/> when this engine opened none for it (#1309): its durable name, why it is
    /// not open, and whether the reason is the <see cref="ArchetypeSetMigrationPolicy.Refuse"/> policy, so <c>RecoveryDriver</c> refuses with the cause
    /// rather than with "not registered". Null name when no stored row has that routing id; null reason when no class of the row was skipped.
    /// </summary>
    internal (string Name, string Reason, bool RefusedByPolicy) DescribeUnopenedArchetypeOfRouting(ushort routingId)
    {
        string rowName = null;
        if (_persistedArchetypes != null)
        {
            foreach (var (persistedName, persisted) in _persistedArchetypes)
            {
                if (persisted.Arch.RoutingId == routingId)
                {
                    rowName = persistedName;
                    break;
                }
            }
        }

        if (rowName == null || _skippedArchetypes == null)
        {
            return (rowName, null, false);
        }

        // Several classes can name one row (D3): the policy skip says most, a superseded class least.
        ushort best = 0;
        var bestRank = -1;
        foreach (var meta in OwnArchetypes())
        {
            if ((meta.Name != rowName && meta.PreviousName != rowName) || !_skippedArchetypes.TryGetValue(meta.ArchetypeId, out var skip))
            {
                continue;
            }

            var rank = skip.Reason switch
            {
                ArchetypeSkipReason.ArchetypeSetChange or ArchetypeSkipReason.SchemaRefused => 2,
                ArchetypeSkipReason.UnresolvedSlot => 1,
                _ => 0,
            };
            if (rank > bestRank)
            {
                (best, bestRank) = (meta.ArchetypeId, rank);
            }
        }

        return bestRank < 0 ? (rowName, null, false) : (rowName, SkippedArchetypeReason(best), bestRank == 2);
    }

    /// <summary>
    /// Test hook: a <see cref="ArchetypeSchemaVerdict.NamesRefreshed"/> verdict opens without its metadata commit, as every build before #1394 did, so a test
    /// can stage a file whose stored names are stale. Per engine; false in production.
    /// </summary>
    internal bool SkipArchetypeNamesRefreshForTest;

    /// <summary>
    /// Test hook: an archetype class this predicate selects is not consumed by the open, as if the build did not declare it. Lets a test stage two revisions
    /// of one archetype that declare the same components — a reorder — which the process-wide registry otherwise hands every open together (D3 then
    /// consumes the higher one from the first open on). Per engine; null in production.
    /// </summary>
    internal Func<ArchetypeMetadata, bool> HideArchetypeForTest;

    /// <summary>
    /// The commit's half of an archetype row change (design 07 §5.2): the schema commit is created if no component migration created it, the user schema
    /// version is bumped once, and the archetype is recorded so the rebuild re-places it (a set change) and <see cref="PersistArchetypeStateInto"/> rewrites
    /// its row. The journal row is written by <see cref="PrepareSchemaCommit"/>, once the entity count and the time are known.
    /// </summary>
    private void StageArchetypeSetChange(ArchetypeMetadata meta, ArchetypeSetMigrationPlan set)
    {
        var pending = _pendingSchemaCommit ??= new PendingSchemaCommit();
        _archetypeSetMigrations ??= [];

        // A repeat InitializeArchetypes before the commit (#790) classifies again; the bump is per archetype, not per pass.
        if (_archetypeSetMigrations.TryAdd(meta.ArchetypeId, set))
        {
            pending.UserSchemaVersionBumps++;
        }
        else
        {
            _archetypeSetMigrations[meta.ArchetypeId] = set;
        }
    }

    /// <summary>
    /// Whether this open re-places <paramref name="meta"/>'s entities into fresh structures: a component of it changed layout, or its component set changed.
    /// Its EntityMap and cluster are then freshly allocated, its pre-migration cluster loaded, and its entities re-placed by
    /// <see cref="RebuildClusterFromChains"/> rather than treated as recoverable state. A metadata-only change moves nothing.
    /// </summary>
    private bool IsMigratingArchetype(ArchetypeMetadata meta, ComponentTable[] slotToTable)
    {
        if (_archetypeSetMigrations != null && _archetypeSetMigrations.TryGetValue(meta.ArchetypeId, out var set) && set.MovesData)
        {
            return true;
        }

        if (_migratedComponents == null || slotToTable == null)
        {
            return false;
        }

        for (var slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            if (table != null && _migratedComponents.ContainsKey(table.Definition.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The set migration of <paramref name="meta"/> that moves data, or null.</summary>
    private ArchetypeSetMigrationPlan DataMovingSetMigrationOf(ArchetypeMetadata meta)
        => _archetypeSetMigrations != null && _archetypeSetMigrations.TryGetValue(meta.ArchetypeId, out var set) && set.MovesData ? set : null;

    /// <summary>Whether any slot <paramref name="set"/> adds has a revision chain carrying the archetype's routing id: the only case R9 has anything to
    /// count, and the common one is none.</summary>
    private static bool AddedSlotHasChainHeads(ArchetypeSetMigrationPlan set, Dictionary<long, int>[] chainHeads)
    {
        for (var rest = (uint)set.Diff.AddedMask; rest != 0; rest &= rest - 1)
        {
            if (chainHeads[BitOperations.TrailingZeroCount(rest)] is { Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// R9 (design 07 §8): an added Versioned slot must have no revision chain carrying the archetype's routing id whose entity the migration places. Such a
    /// chain was left by an earlier build that declared the component and then dropped it, and re-placed under the new set it would read as present — a value
    /// revived from a revision the archetype no longer declared. A chain whose entity is not placed — a destroyed entity's, whose tombstoned head a crash
    /// open has not dropped yet — is not counted: it revives nothing (owner, 2026-11-02). Checked before the first claim.
    /// </summary>
    /// <param name="meta">The archetype.</param>
    /// <param name="state">Its engine state.</param>
    /// <param name="set">Its set migration.</param>
    /// <param name="chainHeads">Per runtime slot, its chain heads.</param>
    /// <param name="placed">Every entity the placement places: those of the pre-migration cluster and those found from a chain head alone (§6.2).</param>
    /// <param name="routingId">The archetype's routing id.</param>
    private static void ThrowIfAddedVersionedSlotHasChains(ArchetypeMetadata meta, ArchetypeEngineState state, ArchetypeSetMigrationPlan set,
        Dictionary<long, int>[] chainHeads, HashSet<long> placed, ushort routingId)
    {
        for (var rest = (uint)set.Diff.AddedMask; rest != 0; rest &= rest - 1)
        {
            var slot = BitOperations.TrailingZeroCount(rest);
            var heads = chainHeads[slot];
            if (heads == null || heads.Count == 0)
            {
                continue;
            }

            var live = 0;
            foreach (var entityPK in heads.Keys)
            {
                live += placed.Contains(entityPK) ? 1 : 0;
            }

            if (live == 0)
            {
                continue;
            }

            var name = state.SlotToComponentTable[slot].Definition.Name;
            throw new ArchetypeSchemaException(meta.Name, ArchetypeSchemaErrorKind.StaleChains,
                $"Archetype '{meta.Name}' gains Versioned component '{name}', but {live} revision chains of it already carry this archetype's "
                + $"routing id {routingId}, left by an earlier build that removed it. Opening would revive those values as present. Nothing of this "
                + "archetype was modified.");
        }
    }

    /// <summary>
    /// Whether <paramref name="set"/> adds a component whose refusal depends on how many entities the archetype holds: one carrying the spatial field or the
    /// realm key (R12), or one with a unique dense index (R7). Only then must the re-placement count its chain-only entities before the first claim, and the
    /// crash open's replay count the window's spawns.
    /// </summary>
    private static bool AddsPopulationSensitiveComponent(ArchetypeEngineState state, ArchetypeSetMigrationPlan set)
    {
        ref readonly var ss = ref state.ClusterState.SpatialSlot;
        var added = set.Diff.AddedMask;
        if (ss.HasSpatialIndex && ((added & (1 << ss.Slot)) != 0 || (ss.HasRealmKey && (added & (1 << ss.RealmKeySlot)) != 0)))
        {
            return true;
        }

        for (var rest = (uint)added; rest != 0; rest &= rest - 1)
        {
            if (HasUniqueDenseIndex(state.SlotToComponentTable[BitOperations.TrailingZeroCount(rest)].Definition, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="definition"/> has a field with a unique, non-sparse index (R7); <paramref name="fieldName"/> names the first.</summary>
    private static bool HasUniqueDenseIndex(DBComponentDefinition definition, out string fieldName)
    {
        foreach (var (_, field) in definition.FieldsByName)
        {
            if (field.HasIndex && !field.IndexAllowMultiple && !field.IndexSparse)
            {
                fieldName = field.Name;
                return true;
            }
        }

        fieldName = null;
        return false;
    }

    /// <summary>
    /// "Its {n} existing entities", or, on a crash open whose WAL window spawned some, how many of each: the population an R7 or R12 message counts.
    /// </summary>
    private static string DescribePopulation(int existing, int replayedSpawns)
        => replayedSpawns == 0
            ? $"Its {existing} existing entities"
            : $"Its {existing + replayedSpawns} entities ({existing} placed from the checkpoint and alive at the end of the WAL window, {replayedSpawns} "
              + "spawned in that window)";

    /// <summary>
    /// What a crash open refused by R7 or R12 adds to the remedy: its WAL window was not replayed, and the build that wrote the database replays it.
    /// </summary>
    private static string CrashRemedy(bool crashOpen)
        => crashOpen
            ? " This open was also a crash recovery, refused before its WAL window was replayed: open the database once with the build that wrote it to "
              + "finish the recovery, then migrate."
            : string.Empty;

    /// <summary>
    /// The crash open's R7 and R12 (design 07 §8), over the population the archetype holds at the END of its WAL window: the entities the re-placement
    /// placed from the checkpoint, less those the window destroys, plus the window's spawns it does not destroy — each absent in every component the set
    /// adds, since the window was written under the committed set, which did not hold it. Called by <c>RecoveryDriver</c> before any record is applied,
    /// for every archetype whose set this open migrates, with the window's per-archetype spawn and destroy entity ids (none for an archetype the window
    /// does not touch). On a crash open the re-placement leaves R7 and R12 to this, as only the window says the final population.
    /// </summary>
    /// <remarks>
    /// Exact. The recovery's aggregation already knows each entity's net fate in the window: a spawn it also destroys is not passed in, and a destroy of an
    /// entity the checkpoint held is passed as a destroy. What the window spawns or destroys is looked up in the migration's fresh EntityMap: a spawn the
    /// checkpoint already holds (a checkpoint that copied it before the crash, AP-12) is not counted twice, and a destroy of an entity the re-placement did
    /// not place subtracts nothing.
    /// </remarks>
    /// <exception cref="ArchetypeSchemaException">R12: the archetype gains its spatial field or realm key and holds an entity at the end of the window. R7:
    /// it gains a unique dense index and holds more than one.</exception>
    internal unsafe void ThrowIfSetMigrationPopulationsRefused(Dictionary<ushort, (List<long> Spawns, List<long> Destroys)> window)
    {
        var map = _setMigrationByRouting;
        if (map == null)
        {
            return;
        }

        var recordBuf = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        for (var routing = 0; routing < map.Length; routing++)
        {
            var set = map[routing];
            var state = set == null || routing >= _stateByRouting.Length ? null : _stateByRouting[routing];
            var meta = set == null || routing >= _metaByRouting.Length ? null : _metaByRouting[routing];
            if (state?.ClusterState == null || meta == null || !AddsPopulationSensitiveComponent(state, set))
            {
                continue;
            }

            var spawned = 0;
            var destroyed = 0;
            if (window != null && window.TryGetValue((ushort)routing, out var w))
            {
                var accessor = state.EntityMap.Segment.CreateChunkAccessor();
                try
                {
                    foreach (var raw in w.Spawns)
                    {
                        spawned += state.EntityMap.TryGet(EntityId.FromRaw(raw).EntityKey, recordBuf, ref accessor) ? 0 : 1;
                    }

                    foreach (var raw in w.Destroys)
                    {
                        destroyed += state.EntityMap.TryGet(EntityId.FromRaw(raw).EntityKey, recordBuf, ref accessor) ? 1 : 0;
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            var existing = set.EntitiesMigrated - destroyed;
            ThrowIfAddedComponentCarriesPlacement(meta, state, set, existing, spawned, crashOpen: true);
            ThrowIfAddedUniqueDenseIndex(meta, state, set, existing, spawned, crashOpen: true);
        }
    }

    /// <summary>
    /// R12 (design 07 §8): an added component must not carry the archetype's spatial field or its realm key while the archetype is populated. Every existing
    /// entity is absent in it (D5, zero bytes), so all of them would be placed at position 0, or in realm 0 — a world collapsed onto one cell. An empty
    /// archetype migrates. On a clean open, checked before the first claim over every entity the re-placement places (the chain-only ones included); on a
    /// crash open, over the population at the end of the WAL window (<see cref="ThrowIfSetMigrationPopulationsRefused"/>).
    /// </summary>
    private static void ThrowIfAddedComponentCarriesPlacement(ArchetypeMetadata meta, ArchetypeEngineState state, ArchetypeSetMigrationPlan set, int existing,
        int replayedSpawns, bool crashOpen)
    {
        ref readonly var ss = ref state.ClusterState.SpatialSlot;
        if (existing + replayedSpawns == 0 || !ss.HasSpatialIndex)
        {
            return;
        }

        var added = set.Diff.AddedMask;
        int slot;
        string carries;
        string placedAt;
        if ((added & (1 << ss.Slot)) != 0)
        {
            (slot, carries, placedAt) = (ss.Slot, "spatial field", "at position 0");
        }
        else if (ss.HasRealmKey && (added & (1 << ss.RealmKeySlot)) != 0)
        {
            (slot, carries, placedAt) = (ss.RealmKeySlot, "realm key", "in realm 0");
        }
        else
        {
            return;
        }

        var name = state.SlotToComponentTable[slot].Definition.Name;
        throw new ArchetypeSchemaException(meta.Name, ArchetypeSchemaErrorKind.PlacementOnAddedComponent,
            $"Archetype '{meta.Name}' gains component '{name}', which carries its {carries}. {DescribePopulation(existing, replayedSpawns)} have no value "
            + $"for it, so they would all be placed {placedAt}. Add it in a revision that recreates these entities, or add it before the archetype is "
            + $"populated; until then, open the database with the build that wrote it.{CrashRemedy(crashOpen)} Nothing of this archetype was modified.");
    }

    /// <summary>
    /// R7 (design 07 §8): an added component must not carry a unique, non-sparse index on an archetype with more than one entity. Every existing entity is
    /// absent in it (D5, zero bytes), so all of them would share key 0 in a tree that admits one entity per key. On a clean open, checked before the first
    /// claim over every entity the re-placement places (the chain-only ones included); on a crash open, over the population at the end of the WAL window
    /// (<see cref="ThrowIfSetMigrationPopulationsRefused"/>).
    /// </summary>
    private static void ThrowIfAddedUniqueDenseIndex(ArchetypeMetadata meta, ArchetypeEngineState state, ArchetypeSetMigrationPlan set, int existing,
        int replayedSpawns, bool crashOpen)
    {
        if (existing + replayedSpawns <= 1)
        {
            return;
        }

        for (var rest = (uint)set.Diff.AddedMask; rest != 0; rest &= rest - 1)
        {
            var definition = state.SlotToComponentTable[BitOperations.TrailingZeroCount(rest)].Definition;
            if (!HasUniqueDenseIndex(definition, out var field))
            {
                continue;
            }

            throw new ArchetypeSchemaException(meta.Name, ArchetypeSchemaErrorKind.UniqueIndexOnAddedComponent,
                $"Archetype '{meta.Name}' gains component '{definition.Name}', whose field '{field}' carries a unique index. "
                + $"{DescribePopulation(existing, replayedSpawns)} have no value for it and would all share key 0. Declare the index Sparse, or add it in a "
                + $"later revision once every entity has a value; until then, open the database with the build that wrote it.{CrashRemedy(crashOpen)} Nothing "
                + "of this archetype was modified.");
        }
    }

    /// <summary>
    /// Whether a crash open's WAL replay must translate slot and enabled-bit numbers (design 07 §7.2): some archetype migrates its component set in this open.
    /// False on every other open, which is the one check the replay pays per record.
    /// </summary>
    internal bool TranslatesWalSlots => _setMigrationByRouting != null;

    /// <summary>
    /// Translates a WAL record's component slot from the COMMITTED numbering — every record above CheckpointLSN is in it (SCHEMA-09) — to the runtime one
    /// (design 07 §7.2). Returns false, with <paramref name="slot"/> = <paramref name="walSlot"/>, when <paramref name="routingId"/>'s set does not migrate.
    /// </summary>
    /// <exception cref="InvalidDataException">The slot is beyond the committed set, or names a component the runtime set dropped. Removal is refused (D2), so
    /// neither can be a record of the committed set.</exception>
    internal bool TranslateWalSlot(ushort routingId, ushort walSlot, out ushort slot)
    {
        var set = SetMigrationOfRouting(routingId);
        if (set == null)
        {
            slot = walSlot;
            return false;
        }

        var oldToNew = set.Diff.OldToNew;
        var runtime = walSlot < oldToNew.Length ? oldToNew[walSlot] : ArchetypeSetDiff.NoSlot;
        if (runtime == ArchetypeSetDiff.NoSlot)
        {
            throw new InvalidDataException(
                $"A WAL record of archetype '{set.ArchetypeName}' (routing id {routingId}) names slot {walSlot}, which the committed component set "
                + $"({oldToNew.Length} components) "
                + (walSlot < oldToNew.Length ? $"holds as '{set.Diff.PersistedNames[walSlot]}', a component this build no longer declares" : "does not have")
                + ". The window above the checkpoint is in the committed numbering, so the record cannot be placed; recovery refuses it rather than guess.");
        }

        slot = runtime;
        return true;
    }

    /// <summary>
    /// Translates a WAL record's enabled-bits mask (a Spawn's or a SetEnabledBits') from the committed numbering to the runtime one: each persisted slot's bit
    /// moves to its runtime slot, an added slot's bit is clear (design 07 §7.2). The mask itself when <paramref name="routingId"/>'s set does not migrate.
    /// </summary>
    internal ushort TranslateWalEnabledBits(ushort routingId, ushort walBits)
        => SetMigrationOfRouting(routingId) is { } set ? set.Diff.RemapEnabledBits(walBits) : walBits;

    /// <summary>
    /// Refuses a WAL value whose length is not the size its component had under the committed set (design 07 §7.2), as
    /// <c>SchemaEvolutionEngine.MigrateWalPayload</c> refuses one for a field migration: it was not written under the layout being migrated from.
    /// </summary>
    /// <param name="routingId">The archetype's routing id; its set must migrate.</param>
    /// <param name="walSlot">The record's slot in the committed numbering.</param>
    /// <param name="payloadLength">The value's length.</param>
    /// <exception cref="InvalidDataException">The length is not the committed layout's component size.</exception>
    internal void ThrowIfWalPayloadIsNotCommittedSize(ushort routingId, ushort walSlot, int payloadLength)
    {
        var set = SetMigrationOfRouting(routingId);
        var size = set.OldLayout.ComponentSize(walSlot);
        if (payloadLength != size)
        {
            throw new InvalidDataException(
                $"A WAL value of component '{set.Diff.PersistedNames[walSlot]}' of archetype '{set.ArchetypeName}' is {payloadLength} bytes, but the committed "
                + $"component set stores it as {size} bytes. The value was not written under the component set being migrated from, so it cannot be placed.");
        }
    }

    /// <summary>Whether the archetype <paramref name="routingId"/> names migrates its set in this open, so its WAL records need translating.</summary>
    internal bool MigratesSetOfRouting(ushort routingId) => SetMigrationOfRouting(routingId) != null;

    /// <summary>The data-moving set migration of the archetype a WAL record's routing id names, or null.</summary>
    private ArchetypeSetMigrationPlan SetMigrationOfRouting(ushort routingId)
    {
        var map = _setMigrationByRouting;
        return map != null && routingId < map.Length ? map[routingId] : null;
    }

    /// <summary>Records a set migration's outcome for <see cref="LastOpenArchetypeSetMigrations"/>, the journal row, and the log.</summary>
    private void NoteArchetypeSetMigrated(ArchetypeSetMigrationPlan set, int entities, double elapsedMs)
    {
        set.EntitiesMigrated = entities;
        set.ElapsedMs = elapsedMs;
        var changes = set.Diff.Describe();
        (_lastOpenArchetypeSetMigrations ??= []).Add(
            new ArchetypeSetMigrationReport(set.ArchetypeName, set.FromRevision, set.ToRevision, changes, entities, elapsedMs));
        LogArchetypeSetMigrated(set.ArchetypeName, set.FromRevision, set.ToRevision, changes, entities, elapsedMs);
    }

    /// <summary>
    /// The archetype half of <see cref="PersistArchetypeStateInto"/>'s row rewrite for a staged archetype (design 07 §5.5): the row takes the runtime count,
    /// revision and a NEW names buffer, filled through <paramref name="cs"/> with the runtime names in slot order. The replaced buffer is queued for
    /// <see cref="FreeReplacedComponentNameLists"/>, which frees it only after the flip.
    /// </summary>
    /// <remarks>
    /// Called from <see cref="PrepareSchemaCommit"/> only, so the new buffer is never seen unnamed by a crash open's collection sweep: that sweep runs
    /// earlier in the same open (<see cref="SweepCollectionOrphans"/>, before the seal's commit), and a crash before the flip leaves the buffer named by
    /// nothing, which the next crash open's sweep reclaims. The buffer is durable before the flip: the change set is saved, then the intent is flushed.
    /// </remarks>
    private void RewriteArchetypeRowForSetChange(ref ArchetypeR1 arch, ArchetypeMetadata meta, ArchetypeEngineState state, ArchetypeSetMigrationPlan set,
        ChangeSet cs)
    {
        arch.ComponentCount = meta.ComponentCount;
        arch.Revision = meta.Revision;
        // Informational, process-local ids (identity is the name, the parent chain the declaration's), refreshed with the rest of the declaration-derived
        // fields so a row rewritten for a parent change names this build's parent, not the creating build's.
        arch.ArchetypeId = meta.ArchetypeId;
        arch.ParentArchetypeId = meta.ParentArchetypeId;

        var oldBufferId = arch.ComponentNames._bufferId;
        arch.ComponentNames = default;
        var vsbs = GetComponentCollectionVSBS<String64>();
        using (var cca = new ComponentCollectionAccessor<String64>(cs, vsbs, ref arch.ComponentNames))
        {
            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                cca.Add((String64)state.SlotToComponentTable[slot].Definition.Name);
            }
        }

        set.NewComponentNamesBufferId = arch.ComponentNames._bufferId;
        if (oldBufferId != 0)
        {
            _pendingSchemaCommit.ReplacedComponentNameBuffers.Add(oldBufferId);
        }
    }

    /// <summary>
    /// The journal rows of the staged archetype row changes (AC-27): <see cref="SchemaObjectKind.Archetype"/>, <see cref="SchemaChangeKind.Migration"/> for a
    /// set change and <see cref="SchemaChangeKind.Compatible"/> for a metadata-only one; components added / removed in the field counts.
    /// </summary>
    private void JournalArchetypeSetChanges(PendingSchemaCommit pending)
    {
        if (_archetypeSetMigrations == null || _schemaHistoryTable == null)
        {
            return;
        }

        foreach (var (_, set) in _archetypeSetMigrations)
        {
            pending.History.Add(new SchemaHistoryR1
            {
                Timestamp = DateTime.UtcNow.Ticks,
                ComponentName = (String64)set.ArchetypeName,
                FromRevision = set.FromRevision,
                ToRevision = set.ToRevision,
                FieldsAdded = BitOperations.PopCount(set.Diff.AddedMask),
                FieldsRemoved = BitOperations.PopCount(set.Diff.RemovedOldMask),
                EntitiesMigrated = set.EntitiesMigrated,
                ElapsedMilliseconds = (int)set.ElapsedMs,
                Kind = set.MovesData ? SchemaChangeKind.Migration : SchemaChangeKind.Compatible,
                Target = SchemaObjectKind.Archetype,
            });
        }
    }

    /// <summary>
    /// Gives back the names buffers the rewritten archetype rows used to name, once the rows naming the new ones are written. A crash before this point leaves
    /// them named by nothing, which the next crash open's collection sweep reclaims (as <see cref="FreeReplacedFieldLists"/>).
    /// </summary>
    private void FreeReplacedComponentNameLists(PendingSchemaCommit pending)
    {
        if (pending.ReplacedComponentNameBuffers.Count == 0)
        {
            return;
        }

        var segment = GetComponentCollectionSegment<String64>();
        var vsbs = GetComponentCollectionVSBS<String64>();
        using var guard = EpochGuard.Enter(EpochManager);
        var cs = MMF.CreateChangeSet();
        var accessor = segment.CreateChunkAccessor(cs);
        try
        {
            foreach (var bufferId in pending.ReplacedComponentNameBuffers)
            {
                if (segment.IsChunkAllocated(bufferId))
                {
                    vsbs.DeleteBuffer(bufferId, ref accessor);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        cs.SaveChanges();
    }

    [LoggerMessage(LogLevel.Information,
        "Archetype '{name}' migrated its component set from revision {fromRevision} to revision {toRevision} ({changes}): {entities} entities re-placed in "
        + "{ms:F1} ms.")]
    private partial void LogArchetypeSetMigrated(string name, int fromRevision, int toRevision, string changes, int entities, double ms);

    [LoggerMessage(LogLevel.Warning,
        "Archetype '{name}' is stored at revision {fromRevision} and this build declares revision {toRevision} ({kind}: {changes}). This engine does not "
        + "migrate archetype sets (ArchetypeSetMigration = Refuse), so the archetype is not opened and the file is left as stored.")]
    private partial void LogArchetypeSetChangeRefused(string name, int fromRevision, int toRevision, ArchetypeSetChangeKind kind, string changes);
}
