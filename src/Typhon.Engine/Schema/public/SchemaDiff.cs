using JetBrains.Annotations;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>Kind of change detected on a field or its index between the persisted schema and the current one.</summary>
[PublicAPI]
public enum FieldChangeKind
{
    /// <summary>Field present in the new schema but not in the persisted one.</summary>
    Added,

    /// <summary>Field present in the persisted schema but not in the new one.</summary>
    Removed,

    /// <summary>Field type changed in a way that is not a safe widening.</summary>
    TypeChanged,

    /// <summary>Field type widened to a larger compatible type (e.g. a narrower numeric type to a wider one).</summary>
    TypeWidened,

    /// <summary>Field renamed (same identity, different name).</summary>
    Renamed,

    /// <summary>Field's byte offset within the component storage changed.</summary>
    OffsetChanged,

    /// <summary>Field's byte size changed.</summary>
    SizeChanged,

    /// <summary>An index was added to the field.</summary>
    IndexAdded,

    /// <summary>An index was removed from the field.</summary>
    IndexRemoved,

    /// <summary>The field's index type changed.</summary>
    IndexTypeChanged,
}

/// <summary>Severity of a schema change, ordered from harmless to <see cref="CompatibilityLevel.Breaking"/>.</summary>
[PublicAPI]
public enum CompatibilityLevel
{
    /// <summary>No difference.</summary>
    Identical,

    /// <summary>Recorded for information only; no effect on data compatibility.</summary>
    InformationOnly,

    /// <summary>Change can be applied automatically without a data migration.</summary>
    Compatible,

    /// <summary>Compatible widening conversion applied automatically.</summary>
    CompatibleWidening,

    /// <summary>Change requires an explicit migration; persisted data cannot be read safely otherwise.</summary>
    Breaking,
}

/// <summary>A single field-level difference between the persisted schema and the current one, with its severity.</summary>
[PublicAPI]
public class FieldChange
{
    /// <summary>Kind of change.</summary>
    public FieldChangeKind Kind { get; }

    /// <summary>Name of the affected field.</summary>
    public string FieldName { get; }

    /// <summary>Unique field identifier of the affected field.</summary>
    public int FieldId { get; }

    /// <summary>Field type in the persisted schema; <see cref="FieldType.None"/> when not applicable (e.g. an added field).</summary>
    public FieldType OldType { get; }

    /// <summary>Field type in the new schema; <see cref="FieldType.None"/> when not applicable (e.g. a removed field).</summary>
    public FieldType NewType { get; }

    /// <summary>Byte offset in the persisted layout; <c>0</c> when not applicable.</summary>
    public int OldOffset { get; }

    /// <summary>Byte offset in the new layout; <c>0</c> when not applicable.</summary>
    public int NewOffset { get; }

    /// <summary>Byte size in the persisted layout; <c>0</c> when not applicable.</summary>
    public int OldSize { get; }

    /// <summary>Byte size in the new layout; <c>0</c> when not applicable.</summary>
    public int NewSize { get; }

    /// <summary>Severity of this change.</summary>
    public CompatibilityLevel Level { get; }

    /// <summary>Creates a field change record. Type, offset, and size arguments are optional and default to unset.</summary>
    /// <param name="kind">Kind of change.</param>
    /// <param name="fieldName">Name of the affected field.</param>
    /// <param name="fieldId">Unique field identifier.</param>
    /// <param name="level">Severity of the change.</param>
    /// <param name="oldType">Field type in the persisted schema.</param>
    /// <param name="newType">Field type in the new schema.</param>
    /// <param name="oldOffset">Byte offset in the persisted layout.</param>
    /// <param name="newOffset">Byte offset in the new layout.</param>
    /// <param name="oldSize">Byte size in the persisted layout.</param>
    /// <param name="newSize">Byte size in the new layout.</param>
    public FieldChange(FieldChangeKind kind, string fieldName, int fieldId, CompatibilityLevel level, FieldType oldType = default, FieldType newType = default, 
        int oldOffset = 0, int newOffset = 0, int oldSize = 0, int newSize = 0)
    {
        Kind = kind;
        FieldName = fieldName;
        FieldId = fieldId;
        Level = level;
        OldType = oldType;
        NewType = newType;
        OldOffset = oldOffset;
        NewOffset = newOffset;
        OldSize = oldSize;
        NewSize = newSize;
    }
}

/// <summary>A single index-level difference between the persisted schema and the current one.</summary>
[PublicAPI]
public class IndexChange
{
    /// <summary>Kind of change (one of the index-related <see cref="FieldChangeKind"/> values).</summary>
    public FieldChangeKind Kind { get; }

    /// <summary>Name of the field whose index changed.</summary>
    public string FieldName { get; }

    /// <summary>Unique field identifier of the affected field.</summary>
    public int FieldId { get; }

    /// <summary>Creates an index change record.</summary>
    /// <param name="kind">Kind of change.</param>
    /// <param name="fieldName">Name of the affected field.</param>
    /// <param name="fieldId">Unique field identifier.</param>
    public IndexChange(FieldChangeKind kind, string fieldName, int fieldId)
    {
        Kind = kind;
        FieldName = fieldName;
        FieldId = fieldId;
    }
}

/// <summary>
/// The full set of differences between a persisted component schema and the current one, with an overall <see cref="CompatibilityLevel"/>.
/// </summary>
[PublicAPI]
public class SchemaDiff
{
    /// <summary>Component (schema) name.</summary>
    public string ComponentName { get; }

    /// <summary>Revision of the persisted schema being compared against.</summary>
    public int PersistedRevision { get; }

    /// <summary>All field-level changes.</summary>
    public List<FieldChange> FieldChanges { get; }

    /// <summary>All index-level changes.</summary>
    public List<IndexChange> IndexChanges { get; }

    /// <summary>Overall severity — the maximum <see cref="CompatibilityLevel"/> across all changes.</summary>
    public CompatibilityLevel Level { get; }

    /// <summary>True when there are no differences (<see cref="Level"/> is <see cref="CompatibilityLevel.Identical"/>).</summary>
    public bool IsIdentical => Level == CompatibilityLevel.Identical;

    /// <summary>True when the change requires an explicit migration (<see cref="Level"/> is <see cref="CompatibilityLevel.Breaking"/>).</summary>
    public bool HasBreakingChanges => Level == CompatibilityLevel.Breaking;

    /// <summary>True when the overall severity is at least <see cref="CompatibilityLevel.Compatible"/>.</summary>
    public bool HasCompatibleChanges => Level >= CompatibilityLevel.Compatible;

    /// <summary>Creates a diff and computes its overall <see cref="Level"/> from the given changes (index changes raise the level to at least compatible).</summary>
    /// <param name="componentName">Component (schema) name.</param>
    /// <param name="persistedRevision">Revision of the persisted schema.</param>
    /// <param name="fieldChanges">Field-level changes.</param>
    /// <param name="indexChanges">Index-level changes.</param>
    public SchemaDiff(string componentName, int persistedRevision, List<FieldChange> fieldChanges, List<IndexChange> indexChanges)
    {
        ComponentName = componentName;
        PersistedRevision = persistedRevision;
        FieldChanges = fieldChanges;
        IndexChanges = indexChanges;

        // Compute overall level as max severity across all changes
        var max = CompatibilityLevel.Identical;
        foreach (var fc in fieldChanges)
        {
            if (fc.Level > max)
            {
                max = fc.Level;
            }
        }

        // Index changes are compatible
        if (indexChanges.Count > 0 && max < CompatibilityLevel.Compatible)
        {
            max = CompatibilityLevel.Compatible;
        }

        Level = max;
    }

    /// <summary>Short human-readable summary of the changes (e.g. "+2 added, -1 removed, 3 index changes"), or "identical" when there are none.</summary>
    public string Summary
    {
        get
        {
            if (IsIdentical)
            {
                return "identical";
            }

            var parts = new List<string>();
            var added = FieldChanges.Count(c => c.Kind == FieldChangeKind.Added);
            var removed = FieldChanges.Count(c => c.Kind == FieldChangeKind.Removed);
            var widened = FieldChanges.Count(c => c.Kind == FieldChangeKind.TypeWidened);
            var breaking = FieldChanges.Count(c => c.Kind == FieldChangeKind.TypeChanged);
            var offsetChanged = FieldChanges.Count(c => c.Kind == FieldChangeKind.OffsetChanged);
            var renamed = FieldChanges.Count(c => c.Kind == FieldChangeKind.Renamed);

            if (added > 0)
            {
                parts.Add($"+{added} added");
            }

            if (removed > 0)
            {
                parts.Add($"-{removed} removed");
            }

            if (widened > 0)
            {
                parts.Add($"~{widened} widened");
            }

            if (breaking > 0)
            {
                parts.Add($"!{breaking} breaking");
            }

            if (offsetChanged > 0)
            {
                parts.Add($">{offsetChanged} reordered");
            }

            if (renamed > 0)
            {
                parts.Add($"={renamed} renamed");
            }

            if (IndexChanges.Count > 0)
            {
                parts.Add($"{IndexChanges.Count} index changes");
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Formats a detailed multi-line message suitable for exception output.
    /// </summary>
    internal string FormatDetailedMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Schema mismatch for component '{ComponentName}' (persisted revision {PersistedRevision})");

        var breakingChanges = FieldChanges.Where(c => c.Level == CompatibilityLevel.Breaking).ToList();
        if (breakingChanges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Breaking changes (require migration):");
            foreach (var c in breakingChanges)
            {
                sb.AppendLine($"    - Field '{c.FieldName}' {FormatChangeDescription(c)} (FieldId={c.FieldId})");
            }
        }

        var compatibleChanges = FieldChanges.Where(c => c.Level < CompatibilityLevel.Breaking && c.Level > CompatibilityLevel.Identical).ToList();
        if (compatibleChanges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Compatible changes (auto-resolvable):");
            foreach (var c in compatibleChanges)
            {
                var prefix = c.Kind == FieldChangeKind.Added ? "+" : c.Kind == FieldChangeKind.Removed ? "-" : "~";
                sb.AppendLine($"    {prefix} Field '{c.FieldName}' {FormatChangeDescription(c)} (FieldId={c.FieldId})");
            }
        }

        if (breakingChanges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Or suppress validation (UNSAFE — may cause data corruption):");
            sb.AppendLine("    dbe.RegisterComponentFromAccessor<T>(schemaValidation: SchemaValidationMode.Skip);");
        }

        return sb.ToString();
    }

    private static string FormatChangeDescription(FieldChange c) =>
        c.Kind switch
        {
            FieldChangeKind.Added => $"added (type: {c.NewType})",
            FieldChangeKind.Removed => $"removed (was type: {c.OldType})",
            FieldChangeKind.TypeChanged => $"type changed: {c.OldType} → {c.NewType}",
            FieldChangeKind.TypeWidened => $"widened: {c.OldType} → {c.NewType}",
            FieldChangeKind.Renamed => "renamed",
            FieldChangeKind.OffsetChanged => $"offset changed: {c.OldOffset} → {c.NewOffset}",
            FieldChangeKind.SizeChanged => $"size changed: {c.OldSize} → {c.NewSize}",
            _ => c.Kind.ToString(),
        };
}
