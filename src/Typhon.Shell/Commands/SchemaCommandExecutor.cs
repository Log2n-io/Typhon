using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Spectre.Console;
using Typhon.Schema.Definition;
using Typhon.Shell.Parsing;
using Typhon.Shell.Session;

namespace Typhon.Shell.Commands;

/// <summary>
/// Handles Phase 5 schema inspection commands: schema-fields, schema-diff, schema-validate, schema-history, schema-export.
/// </summary>
internal sealed class SchemaCommandExecutor
{
    private readonly ShellSession _session;

    public SchemaCommandExecutor(ShellSession session) => _session = session;

    /// <summary>
    /// Dispatches a schema command. Returns null if the command is not recognized.
    /// </summary>
    public CommandResult? Dispatch(string command, List<Token> tokens)
    {
        return command switch
        {
            "schema-fields"   => ExecuteSchemaFields(tokens, 1),
            "schema-diff"     => ExecuteSchemaDiff(tokens, 1),
            "schema-validate" => ExecuteSchemaValidate(tokens, 1),
            "schema-history"  => ExecuteSchemaHistory(tokens, 1),
            "schema-export"   => ExecuteSchemaExport(tokens, 1),
            _                 => null
        };
    }

    // ── schema-fields <component> ─────────────────────────────

    private CommandResult ExecuteSchemaFields(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Error: schema-fields requires a component name. Usage: schema-fields <component>");
        }

        var name = tokens[pos].Value;
        var schemaName = ResolveSchemaName(name);
        if (schemaName == null)
        {
            return CommandResult.Error($"Error: Component '{name}' not found in persisted schema.");
        }

        var fieldsByComp = _session.Engine.PersistedFieldsByComponent;
        if (fieldsByComp == null || !fieldsByComp.TryGetValue(schemaName, out var fields))
        {
            return CommandResult.Error($"Error: No persisted fields for component '{schemaName}'.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"  [white]Persisted Fields: {Markup.Escape(schemaName)}[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");
        sb.AppendLine($"  {"FieldId",-8} {"Name",-20} {"Type",-14} {"Offset",-8} {"Size",-6} {"Index",-12}");
        sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");

        foreach (var f in fields)
        {
            if (f.IsStatic)
            {
                continue;
            }

            var indexInfo = f.HasIndex ? (f.IndexAllowMultiple ? "[cyan]Multi[/]" : "[cyan]Unique[/]") : "[grey]-[/]";
            sb.AppendLine($"  {f.FieldId,-8} {Markup.Escape(f.Name.AsString),-20} {f.Type,-14} {f.OffsetInComponentStorage,-8} {f.SizeInComponentStorage,-6} {indexInfo,-12}");
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── schema-diff <component> ───────────────────────────────

    private CommandResult ExecuteSchemaDiff(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Error: schema-diff requires a component name. Usage: schema-diff <component>");
        }

        var name = tokens[pos].Value;
        var schemaName = ResolveSchemaName(name);
        if (schemaName == null)
        {
            return CommandResult.Error($"Error: Component '{name}' not found in persisted schema.");
        }

        // We need both persisted data and a runtime type to compute a diff
        Type runtimeType = null;
        foreach (var kvp in _session.ComponentTypes)
        {
            var attr = kvp.Value.GetCustomAttribute<ComponentAttribute>();
            if (attr != null && attr.Name == schemaName)
            {
                runtimeType = kvp.Value;
                break;
            }
        }

        if (runtimeType == null)
        {
            return CommandResult.Error($"Error: No runtime type loaded for '{schemaName}'. Use load-schema first.");
        }

        var persisted = _session.Engine.PersistedComponents;
        var fieldsByComp = _session.Engine.PersistedFieldsByComponent;

        if (persisted == null || !persisted.TryGetValue(schemaName, out var comp))
        {
            return CommandResult.Error($"Error: Component '{schemaName}' not found in persisted schema.");
        }

        var persistedFields = fieldsByComp != null && fieldsByComp.TryGetValue(schemaName, out var pf) ? pf : [];
        var resolver = persistedFields.Length > 0 ? new FieldIdResolver(persistedFields) : null;
        var definition = _session.Engine.DBD.CreateFromAccessor(runtimeType, resolver);

        // If already registered, retrieve the existing definition
        if (definition == null)
        {
            var rev = runtimeType.GetCustomAttribute<ComponentAttribute>()?.Revision ?? 1;
            definition = _session.Engine.DBD.GetComponent(schemaName, rev);
        }

        if (definition == null)
        {
            return CommandResult.Error($"Error: Could not build definition for '{schemaName}'.");
        }

        var diff = SchemaValidator.ComputeDiff(schemaName, persistedFields, comp.Comp, definition,
            resolver?.Renames ?? (IReadOnlyList<(string, string, int)>)[]);

        var sb = new StringBuilder();
        sb.AppendLine($"  [white]Schema Diff: {Markup.Escape(schemaName)}[/]");
        sb.AppendLine($"  [grey]Persisted revision:[/] {comp.Comp.SchemaRevision}  [grey]Runtime revision:[/] {definition.Revision}");
        sb.AppendLine($"  [grey]Level:[/] {diff.Level}  [grey]Summary:[/] {Markup.Escape(diff.Summary)}");
        sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");

        foreach (var fc in diff.FieldChanges)
        {
            var color = fc.Level switch
            {
                CompatibilityLevel.Breaking => "red",
                CompatibilityLevel.CompatibleWidening => "yellow",
                CompatibilityLevel.Compatible => "green",
                _ => "grey",
            };

            var prefix = fc.Kind switch
            {
                FieldChangeKind.Added => "+",
                FieldChangeKind.Removed => "-",
                _ => "~",
            };

            sb.AppendLine($"  [{color}]{prefix} {Markup.Escape(fc.FieldName)}[/] ({fc.Kind}, FieldId={fc.FieldId})");
        }

        foreach (var ic in diff.IndexChanges)
        {
            var prefix = ic.Kind == FieldChangeKind.IndexAdded ? "+" : ic.Kind == FieldChangeKind.IndexRemoved ? "-" : "~";
            sb.AppendLine($"  [cyan]{prefix} Index on {Markup.Escape(ic.FieldName)}[/] ({ic.Kind})");
        }

        if (diff.IsIdentical)
        {
            sb.AppendLine("  [green]Schema is identical — no changes needed.[/]");
        }

        // Show migration info
        var oldStride = comp.Comp.CompSize + comp.Comp.CompOverhead;
        var newStride = definition.ComponentStorageTotalSize;
        if (oldStride != newStride)
        {
            sb.AppendLine($"  [grey]Stride change:[/] {oldStride} → {newStride} bytes");
        }

        if (comp.Comp.ComponentSPI != 0)
        {
            var seg = _session.Engine.MMF.GetOrLoadChunkBasedSegment(comp.Comp.ComponentSPI, oldStride);
            sb.AppendLine($"  [grey]Entity count:[/] {Math.Max(0, seg.AllocatedChunkCount - 1)}");
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── schema-validate ───────────────────────────────────────

    private CommandResult ExecuteSchemaValidate(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        var sb = new StringBuilder();
        sb.AppendLine("  [white]Schema Validation[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");

        var persisted = _session.Engine.PersistedComponents;
        var fieldsByComp = _session.Engine.PersistedFieldsByComponent;
        var passCount = 0;
        var failCount = 0;

        foreach (var kvp in _session.ComponentTypes)
        {
            var runtimeType = kvp.Value;
            var attr = runtimeType.GetCustomAttribute<ComponentAttribute>();
            var schemaName = attr?.Name ?? runtimeType.Name;

            if (persisted == null || !persisted.TryGetValue(schemaName, out var comp))
            {
                sb.AppendLine($"  [blue]NEW[/] {Markup.Escape(schemaName)} — not yet persisted");
                passCount++;
                continue;
            }

            var persistedFields = fieldsByComp != null && fieldsByComp.TryGetValue(schemaName, out var pf) ? pf : [];
            var resolver = persistedFields.Length > 0 ? new FieldIdResolver(persistedFields) : null;
            var definition = _session.Engine.DBD.CreateFromAccessor(runtimeType, resolver);

            // If already registered, retrieve the existing definition
            if (definition == null)
            {
                var rev = attr?.Revision ?? 1;
                definition = _session.Engine.DBD.GetComponent(schemaName, rev);
            }

            if (definition == null)
            {
                sb.AppendLine($"  [yellow]?[/] {Markup.Escape(schemaName)} — could not resolve definition");
                passCount++;
                continue;
            }

            var diff = SchemaValidator.ComputeDiff(schemaName, persistedFields, comp.Comp, definition,
                resolver?.Renames ?? (IReadOnlyList<(string, string, int)>)[]);

            if (diff.IsIdentical)
            {
                sb.AppendLine($"  [green]OK[/] {Markup.Escape(schemaName)} — identical");
                passCount++;
            }
            else if (diff.HasBreakingChanges)
            {
                var targetRevision = attr?.Revision ?? 1;
                var chain = _session.Engine.MigrationRegistry?.GetChain(schemaName, comp.Comp.SchemaRevision, targetRevision);
                if (chain != null)
                {
                    sb.AppendLine($"  [yellow]MIGRATE[/] {Markup.Escape(schemaName)} — {Markup.Escape(diff.Summary)} (migration registered)");
                    passCount++;
                }
                else
                {
                    sb.AppendLine($"  [red]FAIL[/] {Markup.Escape(schemaName)} — {Markup.Escape(diff.Summary)} (NO migration path!)");
                    failCount++;
                }
            }
            else
            {
                sb.AppendLine($"  [green]OK[/] {Markup.Escape(schemaName)} — {Markup.Escape(diff.Summary)} (auto-resolvable)");
                passCount++;
            }
        }

        sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");
        if (failCount == 0)
        {
            sb.AppendLine($"  [green]All {passCount} component(s) valid.[/]");
        }
        else
        {
            sb.AppendLine($"  [red]{failCount} component(s) have unresolvable breaking changes.[/] {passCount} passed.");
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── schema-history ────────────────────────────────────────

    private CommandResult ExecuteSchemaHistory(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        var history = _session.Engine.GetSchemaHistory();

        if (history.Count == 0)
        {
            return CommandResult.Ok("  No schema history entries recorded.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("  [white]Schema Change History[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");
        sb.AppendLine($"  {"Timestamp",-22} {"Component",-25} {"Change",-18} {"Entities",-10} {"Time",-8}");
        sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");

        foreach (var entry in history)
        {
            var ts = new DateTime(entry.Timestamp, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss");
            var change = $"rev {entry.FromRevision}→{entry.ToRevision} ({entry.Kind})";
            var entities = entry.EntitiesMigrated > 0 ? entry.EntitiesMigrated.ToString() : "-";
            var time = entry.ElapsedMilliseconds > 0 ? $"{entry.ElapsedMilliseconds}ms" : "-";

            sb.AppendLine($"  {ts,-22} {Markup.Escape(entry.ComponentName.AsString),-25} {Markup.Escape(change),-18} {entities,-10} {time,-8}");
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── schema-export [component] ─────────────────────────────

    private CommandResult ExecuteSchemaExport(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        var persisted = _session.Engine.PersistedComponents;
        var fieldsByComp = _session.Engine.PersistedFieldsByComponent;

        if (persisted == null || persisted.Count == 0)
        {
            return CommandResult.Ok("  No persisted components to export.");
        }

        // Optional component name filter
        string filterName = null;
        if (pos < tokens.Count && tokens[pos].Kind != TokenKind.End)
        {
            filterName = ResolveSchemaName(tokens[pos].Value);
            if (filterName == null)
            {
                return CommandResult.Error($"Error: Component '{tokens[pos].Value}' not found in persisted schema.");
            }
        }

        var format = _session.Format.ToLowerInvariant();

        return format switch
        {
            "json" => ExportJson(persisted, fieldsByComp, filterName),
            "csv" => ExportCsv(persisted, fieldsByComp, filterName),
            _ => ExportTable(persisted, fieldsByComp, filterName),
        };
    }

    private static CommandResult ExportJson(
        IReadOnlyDictionary<string, (int ChunkId, ComponentR1 Comp)> persisted,
        IReadOnlyDictionary<string, FieldR1[]> fieldsByComp,
        string filterName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[");
        var first = true;

        foreach (var kvp in persisted)
        {
            if (filterName != null && kvp.Key != filterName)
            {
                continue;
            }

            if (!first)
            {
                sb.AppendLine(",");
            }

            first = false;
            var comp = kvp.Value.Comp;
            sb.AppendLine($"  {{");
            sb.AppendLine($"    \"name\": \"{EscapeJson(kvp.Key)}\",");
            sb.AppendLine($"    \"revision\": {comp.SchemaRevision},");
            sb.AppendLine($"    \"storageSize\": {comp.CompSize},");
            sb.AppendLine($"    \"overhead\": {comp.CompOverhead},");
            sb.AppendLine($"    \"fields\": [");

            if (fieldsByComp != null && fieldsByComp.TryGetValue(kvp.Key, out var fields))
            {
                var firstField = true;
                foreach (var f in fields)
                {
                    if (f.IsStatic)
                    {
                        continue;
                    }

                    if (!firstField)
                    {
                        sb.AppendLine(",");
                    }

                    firstField = false;
                    sb.Append($"      {{ \"name\": \"{EscapeJson(f.Name.AsString)}\", \"fieldId\": {f.FieldId}, ");
                    sb.Append($"\"type\": \"{f.Type}\", \"offset\": {f.OffsetInComponentStorage}, ");
                    sb.Append($"\"size\": {f.SizeInComponentStorage}");
                    if (f.HasIndex)
                    {
                        sb.Append($", \"index\": {{ \"allowMultiple\": {(f.IndexAllowMultiple ? "true" : "false")} }}");
                    }

                    sb.Append(" }");
                }

                sb.AppendLine();
            }

            sb.AppendLine("    ]");
            sb.Append("  }");
        }

        sb.AppendLine();
        sb.AppendLine("]");
        return CommandResult.Ok(sb.ToString().TrimEnd());
    }

    private static CommandResult ExportCsv(
        IReadOnlyDictionary<string, (int ChunkId, ComponentR1 Comp)> persisted,
        IReadOnlyDictionary<string, FieldR1[]> fieldsByComp,
        string filterName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Component,Revision,FieldName,FieldId,Type,Offset,Size,HasIndex,AllowMultiple");

        foreach (var kvp in persisted)
        {
            if (filterName != null && kvp.Key != filterName)
            {
                continue;
            }

            var comp = kvp.Value.Comp;
            if (fieldsByComp != null && fieldsByComp.TryGetValue(kvp.Key, out var fields))
            {
                foreach (var f in fields)
                {
                    if (f.IsStatic)
                    {
                        continue;
                    }

                    sb.AppendLine($"{kvp.Key},{comp.SchemaRevision},{f.Name.AsString},{f.FieldId},{f.Type},{f.OffsetInComponentStorage},{f.SizeInComponentStorage},{f.HasIndex},{f.IndexAllowMultiple}");
                }
            }
        }

        return CommandResult.Ok(sb.ToString().TrimEnd());
    }

    private static CommandResult ExportTable(
        IReadOnlyDictionary<string, (int ChunkId, ComponentR1 Comp)> persisted,
        IReadOnlyDictionary<string, FieldR1[]> fieldsByComp,
        string filterName)
    {
        var sb = new StringBuilder();

        foreach (var kvp in persisted)
        {
            if (filterName != null && kvp.Key != filterName)
            {
                continue;
            }

            var comp = kvp.Value.Comp;
            sb.AppendLine($"  [white]{Markup.Escape(kvp.Key)}[/] (rev {comp.SchemaRevision}, size={comp.CompSize}, overhead={comp.CompOverhead})");
            sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");
            sb.AppendLine($"  {"FieldId",-8} {"Name",-20} {"Type",-14} {"Offset",-8} {"Size",-6} {"Index",-12}");
            sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────[/]");

            if (fieldsByComp != null && fieldsByComp.TryGetValue(kvp.Key, out var fields))
            {
                foreach (var f in fields)
                {
                    if (f.IsStatic)
                    {
                        continue;
                    }

                    var indexInfo = f.HasIndex ? (f.IndexAllowMultiple ? "[cyan]Multi[/]" : "[cyan]Unique[/]") : "[grey]-[/]";
                    sb.AppendLine($"  {f.FieldId,-8} {Markup.Escape(f.Name.AsString),-20} {f.Type,-14} {f.OffsetInComponentStorage,-8} {f.SizeInComponentStorage,-6} {indexInfo,-12}");
                }
            }

            sb.AppendLine();
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── Helpers ────────────────────────────────────────────────

    private bool RequireDatabase(out CommandResult error)
    {
        if (_session.IsOpen)
        {
            error = default;
            return true;
        }

        error = CommandResult.Error("Error: No database is open. Use 'open <path>' first.");
        return false;
    }

    /// <summary>
    /// Resolves a user-provided component name (short or full) to the persisted schema name.
    /// Tries: direct match, [Component] attribute match via loaded types, suffix match on persisted keys.
    /// </summary>
    private string ResolveSchemaName(string input)
    {
        var persisted = _session.Engine.PersistedComponents;
        if (persisted == null)
        {
            return null;
        }

        // 1. Direct match
        if (persisted.ContainsKey(input))
        {
            return input;
        }

        // 2. Match via loaded component types → [Component] attribute
        foreach (var kvp in _session.ComponentTypes)
        {
            if (string.Equals(kvp.Key, input, StringComparison.OrdinalIgnoreCase))
            {
                var attr = kvp.Value.GetCustomAttribute<ComponentAttribute>();
                if (attr != null && persisted.ContainsKey(attr.Name))
                {
                    return attr.Name;
                }
            }
        }

        // 3. Suffix match on persisted component keys (after last '.')
        foreach (var key in persisted.Keys)
        {
            var lastDot = key.LastIndexOf('.');
            if (lastDot >= 0)
            {
                var suffix = key.Substring(lastDot + 1);
                if (string.Equals(suffix, input, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
        }

        return null;
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
