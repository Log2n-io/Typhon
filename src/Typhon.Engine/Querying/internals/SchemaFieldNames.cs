using System.Collections.Generic;
using System.Reflection;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// The parser names a predicate's field by its C# member; the schema keys a field by <c>[Field(Name = ...)]</c> when one is given. This maps one to the
/// other for <typeparamref name="T"/>, built once per component type.
/// </summary>
internal static class SchemaFieldNames<T>
{
    /// <summary>Member name → schema name, for the members whose schema name differs. Empty for most components.</summary>
    private static readonly Dictionary<string, string> Renamed = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>();
        foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var name = field.GetCustomAttribute<FieldAttribute>()?.Name;
            if (name != null && name != field.Name)
            {
                map[field.Name] = name;
            }
        }
        return map;
    }

    /// <summary><paramref name="branches"/> with every field named as the schema names it; the same array when nothing is renamed.</summary>
    public static FieldPredicate[][] ToSchemaNames(FieldPredicate[][] branches)
    {
        if (Renamed.Count == 0)
        {
            return branches;
        }

        var result = new FieldPredicate[branches.Length][];
        for (var b = 0; b < branches.Length; b++)
        {
            var branch = branches[b];
            var copy = new FieldPredicate[branch.Length];
            for (var i = 0; i < branch.Length; i++)
            {
                var p = branch[i];
                copy[i] = Renamed.TryGetValue(p.FieldName, out var schemaName) ? new FieldPredicate(schemaName, p.Operator, p.Value) : p;
            }
            result[b] = copy;
        }
        return result;
    }
}
