using JetBrains.Annotations;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[PublicAPI]
public enum FieldChangeKind
{
    Added,
    Removed,
    TypeChanged,
    TypeWidened,
    Renamed,
    OffsetChanged,
    SizeChanged,
    IndexAdded,
    IndexRemoved,
    IndexTypeChanged,
}

[PublicAPI]
public enum CompatibilityLevel
{
    Identical,
    InformationOnly,
    Compatible,
    CompatibleWidening,
    Breaking,
}

[PublicAPI]
public class FieldChange
{
    public FieldChangeKind Kind { get; }
    public string FieldName { get; }
    public int FieldId { get; }
    public FieldType OldType { get; }
    public FieldType NewType { get; }
    public int OldOffset { get; }
    public int NewOffset { get; }
    public int OldSize { get; }
    public int NewSize { get; }
    public CompatibilityLevel Level { get; }

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

[PublicAPI]
public class IndexChange
{
    public FieldChangeKind Kind { get; }
    public string FieldName { get; }
    public int FieldId { get; }

    public IndexChange(FieldChangeKind kind, string fieldName, int fieldId)
    {
        Kind = kind;
        FieldName = fieldName;
        FieldId = fieldId;
    }
}

[PublicAPI]
public class SchemaDiff
{
    public string ComponentName { get; }
    public int PersistedRevision { get; }
    public List<FieldChange> FieldChanges { get; }
    public List<IndexChange> IndexChanges { get; }
    public CompatibilityLevel Level { get; }

    public bool IsIdentical => Level == CompatibilityLevel.Identical;
    public bool HasBreakingChanges => Level == CompatibilityLevel.Breaking;
    public bool HasCompatibleChanges => Level >= CompatibilityLevel.Compatible;

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
