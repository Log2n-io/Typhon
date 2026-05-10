using System.Collections.Generic;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Compares persisted schema metadata against runtime component definitions and produces a <see cref="SchemaDiff"/> categorizing each change as compatible,
/// breaking, or informational.
/// </summary>
internal static class SchemaValidator
{
    /// <summary>
    /// Computes the schema diff between persisted FieldR1 entries and the runtime definition.
    /// </summary>
    internal static SchemaDiff ComputeDiff(string componentName, FieldR1[] persistedFields, ComponentR1 persistedComponent, 
        DBComponentDefinition runtimeDefinition, IReadOnlyList<(string OldName, string NewName, int FieldId)> renames)
    {
        var fieldChanges = new List<FieldChange>();
        var indexChanges = new List<IndexChange>();

        // Build lookup by FieldId for both sides
        var persistedById = new Dictionary<int, FieldR1>(persistedFields.Length);
        foreach (var f in persistedFields)
        {
            if (!f.IsStatic)
            {
                persistedById[f.FieldId] = f;
            }
        }

        var runtimeById = new Dictionary<int, DBComponentDefinition.Field>();
        foreach (var kvp in runtimeDefinition.FieldsByName)
        {
            var field = kvp.Value;
            if (!field.IsStatic)
            {
                runtimeById[field.FieldId] = field;
            }
        }

        // Compare each persisted field against runtime
        foreach (var kvp in persistedById)
        {
            var fieldId = kvp.Key;
            var pField = kvp.Value;
            var pName = pField.Name.AsString;

            if (!runtimeById.TryGetValue(fieldId, out var rField))
            {
                // Field removed in runtime
                fieldChanges.Add(new FieldChange(FieldChangeKind.Removed, pName, fieldId, CompatibilityLevel.Compatible, pField.Type));
                continue;
            }

            // Type comparison
            if (pField.Type != rField.Type)
            {
                if (IsCompatibleWidening(pField.Type, rField.Type))
                {
                    fieldChanges.Add(new FieldChange(FieldChangeKind.TypeWidened, rField.Name, fieldId, CompatibilityLevel.CompatibleWidening, pField.Type, rField.Type));
                }
                else
                {
                    fieldChanges.Add(new FieldChange(FieldChangeKind.TypeChanged, rField.Name, fieldId, CompatibilityLevel.Breaking, pField.Type, rField.Type));
                }
            }

            // Offset comparison
            if (pField.OffsetInComponentStorage != rField.OffsetInComponentStorage)
            {
                fieldChanges.Add(new FieldChange(
                    FieldChangeKind.OffsetChanged, rField.Name, fieldId, CompatibilityLevel.Compatible,
                    oldOffset: pField.OffsetInComponentStorage, newOffset: rField.OffsetInComponentStorage));
            }

            if (pField.SizeInComponentStorage != rField.SizeInComponentStorage)
            {
                // Size change from array length change is breaking; from type widening is already captured above
                var sizeLevel = pField.ArrayLength != rField.ArrayLength ? CompatibilityLevel.Breaking : CompatibilityLevel.Compatible;
                fieldChanges.Add(new FieldChange(FieldChangeKind.SizeChanged, rField.Name, fieldId, sizeLevel, 
                    oldSize: pField.SizeInComponentStorage, newSize: rField.SizeInComponentStorage));
            }

            // Index changes
            if (!pField.HasIndex && rField.HasIndex)
            {
                indexChanges.Add(new IndexChange(FieldChangeKind.IndexAdded, rField.Name, fieldId));
            }
            else if (pField.HasIndex && !rField.HasIndex)
            {
                indexChanges.Add(new IndexChange(FieldChangeKind.IndexRemoved, rField.Name, fieldId));
            }
            else if (pField.HasIndex && rField.HasIndex && pField.IndexAllowMultiple != rField.IndexAllowMultiple)
            {
                indexChanges.Add(new IndexChange(FieldChangeKind.IndexTypeChanged, rField.Name, fieldId));
            }
        }

        // Detect additions (in runtime but not in persisted)
        foreach (var kvp in runtimeById)
        {
            if (!persistedById.ContainsKey(kvp.Key))
            {
                var rField = kvp.Value;
                fieldChanges.Add(new FieldChange(FieldChangeKind.Added, rField.Name, rField.FieldId, CompatibilityLevel.Compatible, newType: rField.Type));
            }
        }

        // Add rename entries (informational only — already handled by FieldIdResolver)
        if (renames != null)
        {
            foreach (var (oldName, newName, fieldId) in renames)
            {
                fieldChanges.Add(new FieldChange(FieldChangeKind.Renamed, newName, fieldId, CompatibilityLevel.InformationOnly));
            }
        }

        return new SchemaDiff(componentName, persistedComponent.SchemaRevision, fieldChanges, indexChanges);
    }

    /// <summary>
    /// Lookup table of all valid lossless type widening pairs.
    /// </summary>
    private static readonly HashSet<(FieldType From, FieldType To)> WideningPairs =
    [
        // Signed integer chain
        (FieldType.Byte, FieldType.Short),
        (FieldType.Byte, FieldType.Int),
        (FieldType.Byte, FieldType.Long),
        (FieldType.Short, FieldType.Int),
        (FieldType.Short, FieldType.Long),
        (FieldType.Int, FieldType.Long),

        // Unsigned integer chain
        (FieldType.UByte, FieldType.UShort),
        (FieldType.UByte, FieldType.UInt),
        (FieldType.UByte, FieldType.ULong),
        (FieldType.UShort, FieldType.UInt),
        (FieldType.UShort, FieldType.ULong),
        (FieldType.UInt, FieldType.ULong),

        // Cross-sign: unsigned → wider signed (value always fits)
        (FieldType.UByte, FieldType.Short),
        (FieldType.UByte, FieldType.Int),
        (FieldType.UByte, FieldType.Long),
        (FieldType.UShort, FieldType.Int),
        (FieldType.UShort, FieldType.Long),
        (FieldType.UInt, FieldType.Long),

        // Float
        (FieldType.Float, FieldType.Double),

        // Vectors: float → double precision
        (FieldType.Point2F, FieldType.Point2D),
        (FieldType.Point3F, FieldType.Point3D),
        (FieldType.Point4F, FieldType.Point4D),
        (FieldType.QuaternionF, FieldType.QuaternionD),

        // String widening
        (FieldType.String64, FieldType.String1024),
    ];

    /// <summary>
    /// Returns true if <paramref name="oldType"/> can be losslessly widened to <paramref name="newType"/>.
    /// </summary>
    internal static bool IsCompatibleWidening(FieldType oldType, FieldType newType) => WideningPairs.Contains((oldType, newType));
}
