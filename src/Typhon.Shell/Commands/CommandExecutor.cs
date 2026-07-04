using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Typhon.Schema.Definition;
using Typhon.Shell.Formatting;
using Typhon.Shell.Parsing;
using Typhon.Shell.Schema;
using Typhon.Shell.Session;

namespace Typhon.Shell.Commands;

/// <summary>
/// Central command dispatcher. Parses tokenized input and routes to the appropriate handler.
/// Each handler interacts with ShellSession and the Typhon engine.
/// </summary>
internal sealed class CommandExecutor
{
    private readonly ShellSession _session;
    private readonly DiagnosticCommandExecutor _diagnostics;
    private readonly SchemaCommandExecutor _schema;
    private readonly Dictionary<string, IOutputFormatter> _formatters;
    private readonly List<string> _history = [];

    // Cached reflection method infos for generic Transaction methods (runtime type dispatch requires MakeGenericMethod)
    private static readonly MethodInfo ReadComponentMethod = typeof(Transaction)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .First(m => m.Name == "QueryRead" && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2);

    private static readonly MethodInfo WriteComponentMethod = typeof(Transaction)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .First(m => m.Name == "WriteComponent" && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2);

    public CommandExecutor(ShellSession session)
    {
        _session = session;
        _diagnostics = new DiagnosticCommandExecutor(session);
        _schema = new SchemaCommandExecutor(session);
        _formatters = new Dictionary<string, IOutputFormatter>(StringComparer.OrdinalIgnoreCase)
        {
            ["table"] = new TableFormatter(),
            ["full-table"] = new FullTableFormatter(),
            ["json"] = new JsonFormatter(),
            ["csv"] = new CsvFormatter(),
        };
    }

    public IReadOnlyList<string> History => _history;

    /// <summary>
    /// Executes a single command line. Returns a result with output text and status.
    /// </summary>
    public CommandResult Execute(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return CommandResult.Ok();
        }

        var trimmed = input.Trim();

        // Skip comments
        if (trimmed.StartsWith('#'))
        {
            return CommandResult.Ok();
        }

        _history.Add(trimmed);

        var sw = _session.Timing ? Stopwatch.StartNew() : null;

        CommandResult result;
        try
        {
            var tokens = Tokenizer.Tokenize(trimmed);
            result = Dispatch(tokens);
        }
        catch (Exception ex)
        {
            result = CommandResult.Error($"Error: {ex.Message}");
        }

        if (sw != null)
        {
            sw.Stop();
            var timing = $"  ({sw.Elapsed.TotalMilliseconds:F2} ms)";
            result = result.WithAppendedOutput(timing);
        }

        return result;
    }

    private CommandResult Dispatch(List<Token> tokens)
    {
        if (tokens.Count == 0 || tokens[0].Kind == TokenKind.End)
        {
            return CommandResult.Ok();
        }

        var cmd = tokens[0];
        if (cmd.Kind != TokenKind.Identifier)
        {
            return CommandResult.Error($"Syntax error: expected command name, got '{cmd.Value}'");
        }

        // Delegate to diagnostic command executor first (Phase 2)
        var diagResult = _diagnostics.Dispatch(cmd.Value.ToLowerInvariant(), tokens);
        if (diagResult.HasValue)
        {
            return diagResult.Value;
        }

        // Delegate to schema command executor (Phase 5)
        var schemaResult = _schema.Dispatch(cmd.Value.ToLowerInvariant(), tokens);
        if (schemaResult.HasValue)
        {
            return schemaResult.Value;
        }

        var builtinResult = cmd.Value.ToLowerInvariant() switch
        {
            "open"          => ExecuteOpen(tokens, 1),
            "close"         => ExecuteClose(),
            "info"          => ExecuteInfo(),
            "load-schema"   => ExecuteLoadSchema(tokens, 1),
            "reload-schema" => ExecuteReloadSchema(),
            "schema-list"   => ExecuteSchema(),
            "describe"      => ExecuteDescribe(tokens, 1),
            "begin"         => ExecuteBegin(),
            "commit"        => ExecuteCommit(),
            "rollback"      => ExecuteRollback(),
            "create"        => ExecuteCreate(tokens, 1),
            "read"          => ExecuteRead(tokens, 1),
            "update"        => ExecuteUpdate(tokens, 1),
            "delete"        => ExecuteDelete(tokens, 1),
            "set"           => ExecuteSet(tokens, 1),
            "log-level"     => ExecuteLogLevel(tokens, 1),
            "echo"          => ExecuteEcho(tokens, 1),
            "help"          => ExecuteHelp(tokens, 1),
            "migrate"       => ExecuteMigrate(tokens, 1),
            "migrate-legacy" => ExecuteMigrateLegacy(tokens, 1),
            "history"       => ExecuteHistory(),
            "pause"         => ExecutePause(tokens, 1),
            "exit" or "quit" => CommandResult.Exit(),
            _               => (CommandResult?)null
        };

        if (builtinResult.HasValue)
        {
            return builtinResult.Value;
        }

        // Try extension commands
        return DispatchCustomCommand(cmd.Value, tokens);
    }

    private CommandResult DispatchCustomCommand(string name, List<Token> tokens)
    {
        if (!_session.CustomCommands.TryGetValue(name, out var command))
        {
            return CommandResult.Error($"Unknown command: '{name}'. Type 'help' for available commands.");
        }

        if (command.RequiresDatabase && !_session.IsOpen)
        {
            return CommandResult.Error($"Error: '{command.Name}' requires an open database. Use 'open <path>' first.");
        }

        // Build string[] args from tokens
        var args = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.End)
            {
                break;
            }

            args.Add(tokens[i].Value);
        }

        var context = new ShellCommandContext(_session);
        var result = command.Execute(context, args.ToArray());

        // Map ShellCommandResult → CommandResult
        if (!result.Success)
        {
            return CommandResult.Error(result.Output);
        }

        return result.UseMarkup
            ? CommandResult.Markup(result.Output)
            : CommandResult.Ok(result.Output);
    }

    // ── Database Commands ──────────────────────────────────────

    private CommandResult ExecuteOpen(List<Token> tokens, int pos)
    {
        var path = ExpectPath(tokens, ref pos);
        if (path == null)
        {
            return CommandResult.Error("Syntax error: open <path>");
        }

        var message = _session.OpenDatabase(path);
        return CommandResult.Markup($"  [green]{Markup.Escape(message)}[/]");
    }

    // The v4 on-disk header signature (mirrors the internal ManagedPagedMMF.HeaderSignature). Hard-coded here because the
    // engine constant is internal; it is a stable format identifier.
    private const string LegacyHeaderSignature = "TyphonDatabase";

    // Import a pre-bundle legacy database FILE ({name}.bin) into a {name}.typhon bundle DIRECTORY. The v4 data-file format
    // is byte-identical between the legacy .bin and the bundle's inner 'data' file (#450 only changed the container), so
    // this is a MOVE, not a conversion. Safe-by-refusal on the WAL: the legacy shared wal/ can't be attributed to one DB.
    private CommandResult ExecuteMigrateLegacy(List<Token> tokens, int pos)
    {
        var path = ExpectPath(tokens, ref pos);
        if (path == null)
        {
            return CommandResult.Error("Syntax error: migrate-legacy <path-to-.bin>");
        }

        var binPath = Path.GetFullPath(path);
        if (!File.Exists(binPath) && File.Exists(binPath + ".bin"))
        {
            binPath += ".bin";
        }

        if (!File.Exists(binPath))
        {
            return CommandResult.Error($"Error: legacy database file not found: '{binPath}'.");
        }

        if (!LooksLikeTyphonDatabase(binPath))
        {
            return CommandResult.Error($"Error: '{binPath}' is not a Typhon database file (the '{LegacyHeaderSignature}' header signature was not found).");
        }

        var dir = Path.GetDirectoryName(binPath);
        if (dir == null)
        {
            return CommandResult.Error($"Error: cannot resolve the directory of '{binPath}'.");
        }

        var name = Path.GetFileNameWithoutExtension(binPath);
        var bundle = Path.Combine(dir, $"{name}.typhon");

        if (Directory.Exists(bundle) || File.Exists(bundle))
        {
            return CommandResult.Error($"Error: target bundle already exists: '{bundle}'. Remove it (or pick a different name) and retry.");
        }

        // Clean-WAL precondition: the legacy shared wal/ can't be attributed to a single database, so we refuse to migrate a
        // database that may still have un-checkpointed commits in an adjacent wal/. A clean close checkpoints the WAL into
        // the data file — this importer only MOVES that file, it does not replay a WAL.
        var legacyWal = Path.Combine(dir, "wal");
        if (Directory.Exists(legacyWal) && Directory.GetFileSystemEntries(legacyWal).Length > 0)
        {
            return CommandResult.Error(
                $"Error: a non-empty legacy WAL directory '{legacyWal}' is present. It may hold un-checkpointed changes that " +
                "cannot be attributed to this database. Reopen and cleanly close the database (which checkpoints the WAL) " +
                "before migrating, or remove the wal/ directory if you are certain it is obsolete.");
        }

        try
        {
            Directory.CreateDirectory(bundle);
            File.Move(binPath, Path.Combine(bundle, "data"));
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Error: migration failed: {ex.Message}");
        }

        return CommandResult.Markup(
            $"  [green]Migrated to bundle '{Markup.Escape(bundle)}'[/] [grey](moved '{Markup.Escape(Path.GetFileName(binPath))}' → data).[/]\n" +
            $"  [grey]Open it with:[/] [cyan]open {Markup.Escape(bundle)}[/]");
    }

    private static bool LooksLikeTyphonDatabase(string binPath)
    {
        // The signature lives in page 0 (at PageBaseHeaderSize). Rather than depend on the exact offset, scan the first page
        // for the distinctive ASCII signature — a false positive in a non-Typhon file's first 8 KB is negligible.
        Span<byte> probe = stackalloc byte[8192];
        int read;
        using (var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = fs.Read(probe);
        }

        ReadOnlySpan<byte> sig = "TyphonDatabase"u8;
        for (var i = 0; i + sig.Length <= read; i++)
        {
            if (probe.Slice(i, sig.Length).SequenceEqual(sig))
            {
                return true;
            }
        }

        return false;
    }

    private CommandResult ExecuteClose()
    {
        if (!_session.IsOpen)
        {
            return CommandResult.Error("Error: No database is open.");
        }

        _session.CloseDatabase();
        return CommandResult.Markup("  [grey]Database closed.[/]");
    }

    private CommandResult ExecuteInfo()
    {
        if (!_session.IsOpen)
        {
            return CommandResult.Error("Error: No database is open. Use 'open <path>' first.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"  [grey]Database:[/]    [white]{Markup.Escape(_session.DatabaseName)}[/]");
        sb.AppendLine($"  [grey]Path:[/]        [white]{Markup.Escape(_session.DatabasePath)}[/]");
        sb.AppendLine($"  [grey]Components:[/]  [white]{_session.ComponentSchemas.Count}[/]");

        if (_session.ComponentSchemas.Count > 0)
        {
            var names = string.Join("[grey],[/] ", _session.ComponentSchemas.Keys.Select(k => $"[green]{Markup.Escape(k)}[/]"));
            sb.AppendLine($"  [grey]Loaded:[/]      {names}");
        }

        if (_session.HasTransaction)
        {
            sb.Append($"  [grey]Transaction:[/] [cyan]TSN {_session.Transaction.TSN}[/]");
            if (_session.IsDirty)
            {
                sb.Append(" [yellow](dirty)[/]");
            }
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── Migrate Command ────────────────────────────────────────

    private CommandResult ExecuteMigrate(List<Token> tokens, int pos)
    {
        string dbPath = null;

        while (pos < tokens.Count)
        {
            if (dbPath == null)
            {
                dbPath = tokens[pos].Value;
                pos++;
            }
            else
            {
                return CommandResult.Error($"Syntax error: unexpected token '{tokens[pos].Value}'.\nUsage: migrate [<path>]");
            }
        }

        // If DB not open, require a path
        if (!_session.IsOpen && dbPath == null)
        {
            return CommandResult.Error(
                "Usage: migrate [<path>]\n" +
                "  Opens the database and applies any pending schema migrations.\n" +
                "  Compatible changes (add field, type widen) are applied automatically.\n" +
                "  Breaking changes require migration functions registered in your schema assembly.\n" +
                "  Use 'schema-validate' on an already-open database for dry-run validation.");
        }

        // If path provided but DB already open, close first
        if (dbPath != null && _session.IsOpen)
        {
            _session.CloseDatabase();
        }

        // Open the database — this triggers RegisterComponentFromAccessor for all loaded schemas,
        // which runs SchemaEvolutionEngine.Migrate() for compatible changes and MigrateWithFunction()
        // for breaking changes with registered migration chains.
        if (!_session.IsOpen)
        {
            if (dbPath == null)
            {
                return CommandResult.Error("Error: No database is open and no path provided.");
            }

            try
            {
                _session.OpenDatabase(dbPath);
            }
            catch (Exception ex)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"  [red]Database open failed:[/] {Markup.Escape(ex.Message)}");
                sb.AppendLine();
                sb.AppendLine("  Schema validation failed during open. Possible causes:");
                sb.AppendLine("    - Breaking schema change with no migration function registered");
                sb.AppendLine("    - Database created by a newer version of the application");
                sb.AppendLine("  Ensure your schema assembly is loaded first via [yellow]load-schema <assembly>[/]");
                sb.AppendLine("  and that all required migration functions are registered.");
                return CommandResult.Markup(sb.ToString().TrimEnd());
            }
        }

        // DB is now open — run schema-validate logic to show status
        var result = new StringBuilder();
        result.AppendLine("  [white]Schema Migration Report[/]");
        result.AppendLine("  [grey]══════════════════════════════════════════════════════════════════[/]");

        var persisted = _session.Engine.PersistedComponents;
        var fieldsByComp = _session.Engine.PersistedFieldsByComponent;
        int identicalCount = 0, migratedCount = 0, newCount = 0, failCount = 0;

        foreach (var kvp in _session.ComponentTypes)
        {
            var runtimeType = kvp.Value;
            var attr = runtimeType.GetCustomAttribute<ComponentAttribute>();
            var schemaName = attr?.Name ?? runtimeType.Name;

            if (persisted == null || !persisted.TryGetValue(schemaName, out var comp))
            {
                result.AppendLine($"  [blue]NEW[/]      {Markup.Escape(schemaName)} — will be created on first use");
                newCount++;
                continue;
            }

            var persistedFields = fieldsByComp != null && fieldsByComp.TryGetValue(schemaName, out var pf) ? pf : [];
            var resolver = persistedFields.Length > 0 ? new FieldIdResolver(persistedFields) : null;
            var definition = _session.Engine.DBD.CreateFromAccessor(runtimeType, resolver)
                             ?? _session.Engine.DBD.GetComponent(schemaName, attr?.Revision ?? 1);

            if (definition == null)
            {
                result.AppendLine($"  [yellow]?[/]        {Markup.Escape(schemaName)} — could not resolve definition");
                continue;
            }

            var diff = SchemaValidator.ComputeDiff(schemaName, persistedFields, comp.Comp, definition,
                resolver?.Renames ?? (IReadOnlyList<(string, string, int)>)[]);

            if (diff.IsIdentical)
            {
                result.AppendLine($"  [green]OK[/]       {Markup.Escape(schemaName)} — identical");
                identicalCount++;
            }
            else if (diff.HasBreakingChanges)
            {
                var targetRevision = attr?.Revision ?? 1;
                var chain = _session.Engine.MigrationRegistry?.GetChain(schemaName, comp.Comp.SchemaRevision, targetRevision);
                if (chain != null)
                {
                    result.AppendLine($"  [green]MIGRATED[/] {Markup.Escape(schemaName)} — {Markup.Escape(diff.Summary)} (migration applied during open)");
                    migratedCount++;
                }
                else
                {
                    result.AppendLine($"  [red]FAIL[/]     {Markup.Escape(schemaName)} — {Markup.Escape(diff.Summary)} (no migration path registered)");
                    failCount++;
                }
            }
            else
            {
                // Compatible change — auto-migrated during open
                result.AppendLine($"  [green]MIGRATED[/] {Markup.Escape(schemaName)} — {Markup.Escape(diff.Summary)} (auto-resolved during open)");
                migratedCount++;
            }
        }

        result.AppendLine("  [grey]══════════════════════════════════════════════════════════════════[/]");
        if (failCount == 0)
        {
            result.AppendLine($"  [green]Migration complete.[/] {identicalCount} identical, {migratedCount} migrated, {newCount} new.");
        }
        else
        {
            result.AppendLine($"  [red]{failCount} component(s) could not be migrated.[/] Register migration functions and retry.");
            result.AppendLine($"  {identicalCount} identical, {migratedCount} migrated, {newCount} new.");
        }

        return CommandResult.Markup(result.ToString().TrimEnd());
    }

    // ── Schema Commands ────────────────────────────────────────

    private CommandResult ExecuteLoadSchema(List<Token> tokens, int pos)
    {
        var path = ExpectPath(tokens, ref pos);
        if (path == null)
        {
            return CommandResult.Error("Syntax error: load-schema <path>");
        }

        var (assembly, components) = AssemblySchemaLoader.LoadAssembly(path);

        _session.AddAssemblyPath(path);

        foreach (var (name, type, schema) in components)
        {
            _session.RegisterComponent(name, type, schema);
        }

        // Discover extension commands from the loaded assembly itself
        var commands = AssemblySchemaLoader.LoadCommands(assembly);
        foreach (var command in commands)
        {
            _session.RegisterCommand(command);
        }

        // Also discover commands from sibling assemblies in the same directory
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            System.IO.Path.GetFullPath(path)
        };

        var siblingCommands = AssemblySchemaLoader.DiscoverCommandsInDirectory(path, scanned);
        foreach (var (command, _) in siblingCommands)
        {
            _session.RegisterCommand(command);
        }

        // Build output report
        var sb = new StringBuilder();

        if (components.Count > 0)
        {
            var names = string.Join("[grey],[/] ", components.Select(c => $"[green]{Markup.Escape(c.Name)}[/]"));
            sb.Append($"  [white]Loaded {components.Count} component{(components.Count != 1 ? "s" : "")}:[/] {names}");
        }

        // Report each discovered command with its source assembly
        var allCommands = new List<(string Name, string Assembly)>();
        foreach (var cmd in commands)
        {
            allCommands.Add((cmd.Name, assembly.GetName().Name));
        }

        foreach (var (cmd, asmName) in siblingCommands)
        {
            allCommands.Add((cmd.Name, asmName));
        }

        foreach (var (cmdName, asmName) in allCommands)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append($"  [white]Command:[/] [cyan]{Markup.Escape(cmdName)}[/] [grey](from {Markup.Escape(asmName)})[/]");
        }

        if (components.Count == 0 && allCommands.Count == 0)
        {
            return CommandResult.Ok($"  No [Component] types or commands found in {System.IO.Path.GetFileName(path)}");
        }

        return CommandResult.Markup(sb.ToString());
    }

    private CommandResult ExecuteReloadSchema()
    {
        if (!_session.IsOpen)
        {
            return CommandResult.Error("Error: No database is open.");
        }

        var dbPath = _session.DatabasePath;
        var assemblyPaths = _session.AssemblyPaths.ToList();

        if (assemblyPaths.Count == 0)
        {
            return CommandResult.Error("Error: No schema assemblies loaded. Use 'load-schema' first.");
        }

        // Close database
        _session.CloseDatabase();
        _session.ClearSchemas();
        _session.ClearCommands();

        // Reload assemblies
        var totalComponents = 0;
        var totalCommands = 0;
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asmPath in assemblyPaths)
        {
            var (assembly, components) = AssemblySchemaLoader.LoadAssembly(asmPath);
            scanned.Add(System.IO.Path.GetFullPath(asmPath));
            _session.AddAssemblyPath(asmPath);
            foreach (var (name, type, schema) in components)
            {
                _session.RegisterComponent(name, type, schema);
                totalComponents++;
            }

            var commands = AssemblySchemaLoader.LoadCommands(assembly);
            foreach (var command in commands)
            {
                _session.RegisterCommand(command);
                totalCommands++;
            }

            var siblingCommands = AssemblySchemaLoader.DiscoverCommandsInDirectory(asmPath, scanned);
            foreach (var (command, _) in siblingCommands)
            {
                _session.RegisterCommand(command);
                totalCommands++;
            }
        }

        // Reopen database
        _session.OpenDatabase(dbPath);

        var msg = $"  [green]Reloaded {totalComponents} component{(totalComponents != 1 ? "s" : "")}";
        if (totalCommands > 0)
        {
            msg += $", {totalCommands} command{(totalCommands != 1 ? "s" : "")}";
        }

        msg += $" from {assemblyPaths.Count} assembl{(assemblyPaths.Count != 1 ? "ies" : "y")}. Database ready.[/]";
        return CommandResult.Markup(msg);
    }

    private CommandResult ExecuteSchema()
    {
        var schemas = _session.ComponentSchemas;
        if (schemas.Count == 0)
        {
            return CommandResult.Ok("  No components loaded. Use 'load-schema <path>' to load a schema assembly.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"  [white]{schemas.Count} component{(schemas.Count != 1 ? "s" : "")} loaded:[/]");

        foreach (var kvp in schemas)
        {
            var schema = kvp.Value;
            sb.AppendLine($"    [green]{Markup.Escape(schema.Name),-20}[/] [grey][[{schema.StructSize} bytes, {schema.Fields.Count} fields]][/]");
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private CommandResult ExecuteDescribe(List<Token> tokens, int pos)
    {
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: describe <component>");
        }

        var componentName = tokens[pos].Value;
        if (!_session.ComponentSchemas.TryGetValue(componentName, out var schema))
        {
            var known = _session.ComponentSchemas.Count > 0
                ? string.Join(", ", _session.ComponentSchemas.Keys)
                : "(none)";
            return CommandResult.Error($"Error: Component '{componentName}' not found. Loaded: {known}");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"  [green]{Markup.Escape(schema.Name)}[/] [grey][[{schema.StructSize} bytes]][/]");
        sb.AppendLine("  [grey]──────────────────────────────[/]");

        foreach (var field in schema.Fields)
        {
            var indexInfo = field.HasIndex
                ? (field.IndexAllowMultiple ? " [magenta][[indexed, multi]][/]" : " [magenta][[indexed, unique]][/]")
                : "";
            sb.AppendLine($"  [yellow]{Markup.Escape(field.Name),-16}[/] [cyan]{field.Type,-12}[/] [grey]offset={field.Offset,-4} size={field.Size}[/]{indexInfo}");
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── Transaction Commands ───────────────────────────────────

    private CommandResult ExecuteBegin()
    {
        if (!_session.IsOpen)
        {
            return CommandResult.Error("Error: No database is open. Use 'open <path>' first.");
        }

        if (_session.HasTransaction)
        {
            return CommandResult.Error($"Error: A transaction is already active (TSN {_session.Transaction.TSN}). Use 'commit' or 'rollback' first.");
        }

        var tx = _session.BeginTransaction();
        return CommandResult.Markup($"  [cyan]Transaction started (TSN {tx.TSN})[/]");
    }

    private CommandResult ExecuteCommit()
    {
        if (!_session.IsOpen)
        {
            return CommandResult.Error("Error: No database is open.");
        }

        if (!_session.HasTransaction)
        {
            return CommandResult.Error("Error: No active transaction. Use 'begin' to start one.");
        }

        var committed = _session.CommitTransaction();
        return committed
            ? CommandResult.Markup("  [green]Committed.[/]")
            : CommandResult.Error("Conflict: Transaction could not be committed. Use 'rollback' to discard changes.");
    }

    private CommandResult ExecuteRollback()
    {
        if (!_session.IsOpen)
        {
            return CommandResult.Error("Error: No database is open.");
        }

        if (!_session.HasTransaction)
        {
            return CommandResult.Error("Error: No active transaction.");
        }

        _session.RollbackTransaction();
        return CommandResult.Markup("  [yellow]Rolled back.[/]");
    }

    // ── Data Commands ──────────────────────────────────────────

    private CommandResult ExecuteCreate(List<Token> tokens, int pos)
    {
        if (!RequireDatabase())
        {
            return CommandResult.Error("Error: No database is open. Use 'open <path>' first.");
        }

        // Parse optional #entityId
        long explicitId = -1;
        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Hash)
        {
            pos++;
            if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Integer)
            {
                return CommandResult.Error("Syntax error: expected entity ID after '#'");
            }

            explicitId = long.Parse(tokens[pos].Value, CultureInfo.InvariantCulture);
            pos++;
        }

        // Parse component name
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: create [#id] <component> { field=value, ... }");
        }

        var componentName = tokens[pos].Value;
        pos++;

        if (!ResolveComponent(componentName, out var componentType, out var schema, out var error))
        {
            return CommandResult.Error(error);
        }

        // Parse brace expression
        var fieldValues = ParseBraceExpression(tokens, ref pos, out var parseError);
        if (fieldValues == null)
        {
            return CommandResult.Error(parseError);
        }

        if (explicitId >= 0)
        {
            return CommandResult.Error("Error: Explicit entity IDs (create #id) are not yet supported by the engine. Omit the #id to auto-assign.");
        }

        // Get or create transaction
        var tx = GetTransactionForWrite(out var isAutoCommit, out var txError);
        if (tx == null)
        {
            return CommandResult.Error(txError);
        }

        try
        {
            var entityId = CreateEntityReflection(tx, componentType, schema, fieldValues);
            _session.MarkDirty();

            var suffix = isAutoCommit ? " [grey](auto-committed)[/]" : "";
            if (isAutoCommit)
            {
                tx.Commit();
                tx.Dispose();
            }

            return CommandResult.Markup($"  [green]Entity[/] [cyan]{entityId}[/] [green]created[/]{suffix}");
        }
        catch (Exception ex)
        {
            if (isAutoCommit)
            {
                tx.Rollback();
                tx.Dispose();
            }

            return _session.Verbose
                ? CommandResult.Error($"Error: {ex.Message}\n{ex.StackTrace}")
                : CommandResult.Error($"Error: {ex.Message}");
        }
    }

    private CommandResult ExecuteRead(List<Token> tokens, int pos)
    {
        if (!RequireDatabase())
        {
            return CommandResult.Error("Error: No database is open. Use 'open <path>' first.");
        }

        // Parse entity ID
        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Integer)
        {
            return CommandResult.Error("Syntax error: read <entityId> <component>");
        }

        var entityId = long.Parse(tokens[pos].Value, CultureInfo.InvariantCulture);
        pos++;

        // Parse component name
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: read <entityId> <component>");
        }

        var componentName = tokens[pos].Value;

        if (!ResolveComponent(componentName, out var componentType, out var schema, out var error))
        {
            return CommandResult.Error(error);
        }

        // Read can work without an explicit transaction
        var tx = _session.Transaction;
        var tempTx = false;
        if (tx == null)
        {
            tx = _session.Engine.CreateQuickTransaction();
            tempTx = true;
        }

        try
        {
            var fieldValues = ReadEntityReflection(tx, entityId, componentType, schema, out var found);
            if (!found)
            {
                return CommandResult.Error($"Error: Entity {entityId} not found in {componentName}");
            }

            var formatter = GetFormatter();
            var output = formatter.FormatEntity(entityId, componentName, schema, fieldValues);
            return CommandResult.Ok(output);
        }
        finally
        {
            if (tempTx)
            {
                tx.Rollback();
                tx.Dispose();
            }
        }
    }

    private CommandResult ExecuteUpdate(List<Token> tokens, int pos)
    {
        if (!RequireDatabase())
        {
            return CommandResult.Error("Error: No database is open. Use 'open <path>' first.");
        }

        // Parse entity ID
        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Integer)
        {
            return CommandResult.Error("Syntax error: update <entityId> <component> { field=value, ... }");
        }

        var entityId = long.Parse(tokens[pos].Value, CultureInfo.InvariantCulture);
        pos++;

        // Parse component name
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: update <entityId> <component> { field=value, ... }");
        }

        var componentName = tokens[pos].Value;
        pos++;

        if (!ResolveComponent(componentName, out var componentType, out var schema, out var error))
        {
            return CommandResult.Error(error);
        }

        // Parse brace expression
        var fieldValues = ParseBraceExpression(tokens, ref pos, out var parseError);
        if (fieldValues == null)
        {
            return CommandResult.Error(parseError);
        }

        var tx = GetTransactionForWrite(out var isAutoCommit, out var txError);
        if (tx == null)
        {
            return CommandResult.Error(txError);
        }

        try
        {
            // Read-then-write: read current values, overlay specified fields, write back
            var success = UpdateEntityReflection(tx, entityId, componentType, schema, fieldValues);
            if (!success)
            {
                if (isAutoCommit)
                {
                    tx.Rollback();
                    tx.Dispose();
                }
                return CommandResult.Error($"Error: Entity {entityId} not found in {componentName}");
            }

            _session.MarkDirty();

            var suffix = isAutoCommit ? " [grey](auto-committed)[/]" : "";
            if (isAutoCommit)
            {
                tx.Commit();
                tx.Dispose();
            }

            return CommandResult.Markup($"  [green]Entity[/] [cyan]{entityId}[/] [green]updated[/]{suffix}");
        }
        catch (Exception ex)
        {
            if (isAutoCommit)
            {
                tx.Rollback();
                tx.Dispose();
            }

            return _session.Verbose
                ? CommandResult.Error($"Error: {ex.Message}\n{ex.StackTrace}")
                : CommandResult.Error($"Error: {ex.Message}");
        }
    }

    private CommandResult ExecuteDelete(List<Token> tokens, int pos)
    {
        if (!RequireDatabase())
        {
            return CommandResult.Error("Error: No database is open. Use 'open <path>' first.");
        }

        // Parse entity ID
        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Integer)
        {
            return CommandResult.Error("Syntax error: delete <entityId> <component>");
        }

        var entityId = long.Parse(tokens[pos].Value, CultureInfo.InvariantCulture);
        pos++;

        // Parse component name
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: delete <entityId> <component>");
        }

        var componentName = tokens[pos].Value;

        if (!ResolveComponent(componentName, out Type _, out ComponentSchema _, out var error))
        {
            return CommandResult.Error(error);
        }

        var tx = GetTransactionForWrite(out var isAutoCommit, out var txError);
        if (tx == null)
        {
            return CommandResult.Error(txError);
        }

        try
        {
            var deleted = DeleteEntityReflection(tx, entityId);
            if (!deleted)
            {
                if (isAutoCommit)
                {
                    tx.Rollback();
                    tx.Dispose();
                }
                return CommandResult.Error($"Error: Entity {entityId} not found in {componentName}");
            }

            _session.MarkDirty();

            var suffix = isAutoCommit ? " [grey](auto-committed)[/]" : "";
            if (isAutoCommit)
            {
                tx.Commit();
                tx.Dispose();
            }

            return CommandResult.Markup($"  [green]Entity[/] [cyan]{entityId}[/] [green]{Markup.Escape(componentName)} deleted[/]{suffix}");
        }
        catch (Exception ex)
        {
            if (isAutoCommit)
            {
                tx.Rollback();
                tx.Dispose();
            }

            return _session.Verbose
                ? CommandResult.Error($"Error: {ex.Message}\n{ex.StackTrace}")
                : CommandResult.Error($"Error: {ex.Message}");
        }
    }

    // ── Shell Commands ─────────────────────────────────────────

    private CommandResult ExecuteSet(List<Token> tokens, int pos)
    {
        // No args → show all settings
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return ShowAllSettings();
        }

        var key = tokens[pos].Value.ToLowerInvariant();
        pos++;

        // Key only → show single setting
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return ShowSetting(key);
        }

        var value = tokens[pos].Value.ToLowerInvariant();

        return key switch
        {
            "format"      => SetFormat(value),
            "auto-commit" => SetBool(value, v => _session.AutoCommit = v, "auto-commit"),
            "verbose"     => SetBool(value, v => _session.Verbose = v, "verbose"),
            "page-size"   => SetPageSize(value),
            "color"       => SetColor(value),
            "timing"      => SetBool(value, v => _session.Timing = v, "timing"),
            "log-level"   => SetLogLevel(value),
            _             => CommandResult.Error($"Error: Unknown setting '{key}'. Known: format, auto-commit, verbose, page-size, color, timing, log-level")
        };
    }

    private CommandResult ShowAllSettings()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  [white]Current settings:[/]");
        sb.AppendLine($"    [grey]format:[/]       [white]{Markup.Escape(_session.Format)}[/]");
        sb.AppendLine($"    [grey]auto-commit:[/]  [white]{(_session.AutoCommit ? "on" : "off")}[/]");
        sb.AppendLine($"    [grey]verbose:[/]      [white]{(_session.Verbose ? "on" : "off")}[/]");
        sb.AppendLine($"    [grey]page-size:[/]    [white]{_session.PageSize}[/]");
        sb.AppendLine($"    [grey]color:[/]        [white]{Markup.Escape(_session.Color)}[/]");
        sb.AppendLine($"    [grey]timing:[/]       [white]{(_session.Timing ? "on" : "off")}[/]");
        sb.Append($"    [grey]log-level:[/]    [white]{_session.LogLevel}[/]");
        return CommandResult.Markup(sb.ToString());
    }

    private CommandResult ShowSetting(string key)
    {
        var value = key switch
        {
            "format"      => _session.Format,
            "auto-commit" => _session.AutoCommit ? "on" : "off",
            "verbose"     => _session.Verbose ? "on" : "off",
            "page-size"   => _session.PageSize.ToString(),
            "color"       => _session.Color,
            "timing"      => _session.Timing ? "on" : "off",
            "log-level"   => _session.LogLevel.ToString(),
            _             => null
        };

        return value != null
            ? CommandResult.Markup($"  [grey]{Markup.Escape(key)}:[/] [white]{Markup.Escape(value)}[/]")
            : CommandResult.Error($"Error: Unknown setting '{key}'");
    }

    private CommandResult SetFormat(string value)
    {
        if (!_formatters.ContainsKey(value))
        {
            return CommandResult.Error($"Error: Unknown format '{value}'. Valid: table, full-table, json, csv");
        }

        _session.Format = value;
        return CommandResult.Ok();
    }

    private static CommandResult SetBool(string value, Action<bool> setter, string name)
    {
        if (value is "on" or "true" or "1")
        {
            setter(true);
            return CommandResult.Ok();
        }

        if (value is "off" or "false" or "0")
        {
            setter(false);
            return CommandResult.Ok();
        }

        return CommandResult.Error($"Error: Invalid value for '{name}'. Use 'on' or 'off'.");
    }

    private CommandResult SetPageSize(string value)
    {
        if (!int.TryParse(value, out var size) || size < 1)
        {
            return CommandResult.Error("Error: page-size must be a positive integer.");
        }

        _session.PageSize = size;
        return CommandResult.Ok();
    }

    private static CommandResult SetColor(string value)
    {
        if (value is not ("auto" or "on" or "off"))
        {
            return CommandResult.Error("Error: color must be 'auto', 'on', or 'off'.");
        }

        // Color control is handled by Spectre.Console; we store the preference
        return CommandResult.Ok();
    }

    private CommandResult ExecuteLogLevel(List<Token> tokens, int pos)
    {
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Markup($"  [grey]log-level:[/] [white]{_session.LogLevel}[/]");
        }

        return SetLogLevel(tokens[pos].Value);
    }

    private CommandResult SetLogLevel(string value)
    {
        if (!Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level))
        {
            return CommandResult.Error("Error: Invalid log level. Valid: Trace, Debug, Information, Warning, Error, Critical, None");
        }

        _session.LogLevel = level;
        return CommandResult.Markup($"  [grey]log-level set to:[/] [white]{level}[/]");
    }

    private CommandResult ExecuteEcho(List<Token> tokens, int pos)
    {
        // Determine timestamp format: --short (HH:mm:ss), --ms (HH:mm:ss.fff), --us (HH:mm:ss.ffffff), --full (yyyy-MM-dd HH:mm:ss.fffffff)
        // Default is --ms
        var format = "HH:mm:ss.fff";
        var msgParts = new List<string>();

        while (pos < tokens.Count && tokens[pos].Kind != TokenKind.End)
        {
            var val = tokens[pos].Value;
            if (val == "--" && pos + 1 < tokens.Count && tokens[pos + 1].Kind != TokenKind.End)
            {
                pos++;
                var flag = tokens[pos].Value.ToLowerInvariant();
                switch (flag)
                {
                    case "short": format = "HH:mm:ss"; break;
                    case "ms": format = "HH:mm:ss.fff"; break;
                    case "us": format = "HH:mm:ss.ffffff"; break;
                    case "full": format = "yyyy-MM-dd HH:mm:ss.fffffff"; break;
                    default: msgParts.Add("--" + flag); break;
                }
            }
            else
            {
                msgParts.Add(val);
            }
            pos++;
        }

        var timestamp = DateTime.Now.ToString(format);
        var message = msgParts.Count > 0 ? string.Join(" ", msgParts) : "";
        var output = message.Length > 0
            ? $"  [grey][[{Markup.Escape(timestamp)}]][/] [white]{Markup.Escape(message)}[/]"
            : $"  [grey][[{Markup.Escape(timestamp)}]][/]";

        return CommandResult.Markup(output);
    }

    private CommandResult ExecuteHelp(List<Token> tokens, int pos)
    {
        if (pos < tokens.Count && tokens[pos].Kind != TokenKind.End)
        {
            return ShowCommandHelp(tokens[pos].Value.ToLowerInvariant());
        }

        var sb = new StringBuilder();
        sb.AppendLine("  [white]Available commands:[/]");
        sb.AppendLine();
        sb.AppendLine("  [yellow]Database:[/]");
        sb.AppendLine("    [cyan]open[/] <path>                      Open (or create) a database");
        sb.AppendLine("    [cyan]close[/]                            Close current database");
        sb.AppendLine("    [cyan]info[/]                             Show database summary");
        sb.AppendLine("    [cyan]migrate-legacy[/] <path.bin>         Import a pre-bundle .bin into a .typhon bundle");
        sb.AppendLine();
        sb.AppendLine("  [yellow]Schema:[/]");
        sb.AppendLine("    [cyan]load-schema[/] <path>               Load component types from assembly");
        sb.AppendLine("    [cyan]reload-schema[/]                    Close, reload assemblies, reopen");
        sb.AppendLine("    [cyan]schema-list[/]                      List loaded components");
        sb.AppendLine("    [cyan]describe[/] <component>             Show component field layout");
        sb.AppendLine();
        sb.AppendLine("  [yellow]Transaction:[/]");
        sb.AppendLine("    [cyan]begin[/]                            Start a new transaction");
        sb.AppendLine("    [cyan]commit[/]                           Commit current transaction");
        sb.AppendLine("    [cyan]rollback[/]                         Rollback current transaction");
        sb.AppendLine();
        sb.AppendLine("  [yellow]Data:[/]");
        sb.AppendLine("    [cyan]create[/] <comp> { field=val, ... } Create an entity");
        sb.AppendLine("    [cyan]read[/] <id> <comp>                 Read entity component data");
        sb.AppendLine("    [cyan]update[/] <id> <comp> { f=v, ... }  Update entity component data");
        sb.AppendLine("    [cyan]delete[/] <id> <comp>               Delete entity component");
        sb.AppendLine();
        sb.AppendLine("  [yellow]Schema Inspection:[/]");
        sb.AppendLine("    [cyan]schema-fields[/] <component>        Show persisted FieldId assignments");
        sb.AppendLine("    [cyan]schema-diff[/] <component>          Compare persisted vs runtime schema");
        sb.AppendLine("    [cyan]schema-validate[/]                  Dry-run validation for all components");
        sb.AppendLine("    [cyan]schema-history[/]                   Show schema change audit trail");
        sb.AppendLine("    [cyan]schema-export[/] [[component]]        Export persisted schema (respects format)");
        sb.AppendLine();
        sb.AppendLine("  [yellow]Diagnostics ([grey]'set format json' emits CI-parseable output[/]):[/]");
        sb.AppendLine("    [cyan]db-stats[/]                           Database volumetry: pages, segments, chunks, bytes  [grey](json)[/]");
        sb.AppendLine("    [cyan]cache-stats[/]                        Page cache hit rate & state breakdown  [grey](json)[/]");
        sb.AppendLine("    [cyan]memory[/]                             Memory usage by subsystem  [grey](json)[/]");
        sb.AppendLine("    [cyan]transactions[/]                       Active transaction list  [grey](json)[/]");
        sb.AppendLine("    [cyan]btree[/] <Comp.Field>                 B+Tree index statistics  [grey](json)[/]");
        sb.AppendLine("    [cyan]btree-dump[/] <Comp.Field> [[--level/chunk N]]  Dump B+Tree nodes");
        sb.AppendLine("    [cyan]btree-validate[/] <Comp.Field>        Validate B+Tree consistency (CI assertion)");
        sb.AppendLine("    [cyan]revisions[/] <id> <comp>              Show entity revision chain");
        sb.AppendLine("    [cyan]mvcc-stats[/] <comp>                  MVCC revision statistics  [grey](json)[/]");
        sb.AppendLine("    [cyan]resources[/]                          Resource graph as a table");
        sb.AppendLine("    [cyan]stats-show[/] <Comp.Field|Comp|--all>    Index statistics & histogram");
        sb.AppendLine("    [cyan]stats-rebuild[/] <Comp.Field|Comp|--all>  Rebuild histograms");
        sb.AppendLine("  [grey]    Deep page/segment/MVCC exploration now lives in the Workbench GUI; btree*/stats-show/revisions/mvcc-stats are provisional (retire on Workbench parity).[/]");
        sb.AppendLine();
        sb.AppendLine("  [yellow]Shell:[/]");
        sb.AppendLine("    [cyan]set[/] [[key [[value]]]]                View/change shell settings");
        sb.AppendLine("    [cyan]echo[/] [[--short|ms|us|full]] <msg>  Print timestamped message");
        sb.AppendLine("    [cyan]help[/] [[command]]                   Show help");
        sb.AppendLine("    [cyan]history[/]                          Show command history");
        sb.AppendLine("    [cyan]exit[/] / [cyan]quit[/]                      Exit the shell");

        // Extension commands
        if (_session.CustomCommands.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  [yellow]Extensions:[/]");
            foreach (var cmd in _session.CustomCommands.Values)
            {
                var padded = cmd.Name.PadRight(30);
                sb.AppendLine($"    [cyan]{Markup.Escape(padded)}[/]  {Markup.Escape(cmd.Description)}");
            }
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private CommandResult ShowCommandHelp(string command)
    {
        var text = command switch
        {
            "open"          => "  open <path>\n    Opens a .typhon database (a directory). Creates it if it doesn't exist.\n    Closes any currently open database first.",
            "migrate-legacy" => "  migrate-legacy <path-to-.bin>\n    Imports a legacy pre-bundle database file (.bin) into a {name}.typhon\n    bundle directory (moves the data file — the on-disk format is unchanged).\n    The .bin's filename must be its original database name (verified on open).\n    Refuses if the target bundle already exists or a non-empty legacy wal/ is\n    present (cleanly close the database first to checkpoint the WAL).",
            "close"         => "  close\n    Closes the current database and releases the file lock.\n    Warns if a transaction is active.",
            "info"          => "  info\n    Shows database summary: path, component count, transaction state.",
            "load-schema"   => "  load-schema <path>\n    Loads component types from a compiled .NET assembly (.dll).\n    Can be called before or after opening a database.\n    Multiple assemblies can be loaded (additive).",
            "reload-schema" => "  reload-schema\n    Closes the database, reloads all assemblies from disk,\n    and reopens. Use after recompiling your schema assembly.",
            "schema-list"   => "  schema-list\n    Lists all loaded component types with their sizes and field counts.",
            "describe"      => "  describe <component>\n    Shows the field layout: name, type, offset, size, and index info.",
            "begin"         => "  begin\n    Starts a new transaction. Error if one is already active.",
            "commit"        => "  commit\n    Commits the current transaction.\n    Reports conflict if another transaction modified the same entities.",
            "rollback"      => "  rollback\n    Rolls back the current transaction, discarding all changes.",
            "create"        => "  create <component> { field=value, ... }\n    Creates an entity with the given component data.\n    Requires an active transaction (or auto-commit on).",
            "read"          => "  read <entityId> <component>\n    Reads entity data. Works without an active transaction\n    (uses a temporary snapshot).",
            "update"        => "  update <entityId> <component> { field=value, ... }\n    Updates entity data. Reads current, overlays specified fields, writes back.\n    Unspecified fields are preserved.",
            "delete"        => "  delete <entityId> <component>\n    Deletes an entity's component data.",
            "set"           => "  set [key [value]]\n    View or change settings.\n    Settings: format, auto-commit, verbose, page-size, color, timing",
            "echo"          => "  echo [--short|--ms|--us|--full] <message>\n    Prints a timestamped message.\n    Formats: --short (HH:mm:ss), --ms (HH:mm:ss.fff, default),\n             --us (HH:mm:ss.ffffff), --full (yyyy-MM-dd HH:mm:ss.fffffff)",
            "help"          => "  help [command]\n    Shows help for all commands or a specific command.",
            "history"       => "  history\n    Shows recent command history.",
            "exit" or "quit" => "  exit / quit\n    Exits the shell.",
            "db-stats"       => "  db-stats\n    Database volumetry overview: file pages (allocated/capacity), per-component\n    segment breakdown (chunks, bytes, fill%), and totals.\n    Honors 'set format json' → CI/monitoring probe.",
            "cache-stats"    => "  cache-stats\n    Shows page cache hit rate, state breakdown (free/idle/exclusive/dirty),\n    and disk I/O counters.\n    Honors 'set format json' → CI/monitoring probe.",
            "btree"          => "  btree <Component.Field>\n    Shows B+Tree index statistics: node count, capacity, fill factor.\n    Use CompName.PK for the primary key index. Honors 'set format json'.\n    Provisional: retires when the Workbench ships an index-stats panel.",
            "btree-dump"     => "  btree-dump <Component.Field> [--level N | --chunk N]\n    Dumps B+Tree structure. --chunk N shows raw hex of a specific node.\n    Provisional: retires when the Workbench ships a B+Tree walker.",
            "btree-validate" => "  btree-validate <Component.Field>\n    Runs consistency checks on a B+Tree index.\n    Reports pass/fail with error details (CI integrity assertion).",
            "revisions"      => "  revisions <entityId> <component>\n    Shows the MVCC revision chain for an entity: chain length,\n    item count, first revision number.\n    Provisional: retires when the Workbench ships MVCC time-travel.",
            "mvcc-stats"     => "  mvcc-stats <component>\n    Aggregates MVCC statistics: entity count, total revisions,\n    average revisions per entity, max chain length. Honors 'set format json'.\n    Provisional: retires when the Workbench ships an MVCC panel.",
            "transactions"   => "  transactions\n    Lists active transactions with TSN and state.\n    Shows MinTSN and NextTSN from the transaction chain.\n    Honors 'set format json' → CI/monitoring probe.",
            "memory"         => "  memory\n    Shows memory usage grouped by engine subsystem\n    (Storage, DataEngine, Durability, Allocation).\n    Honors 'set format json' → CI/monitoring probe.",
            "resources"      => "  resources\n    Prints the resource graph (memory + capacity per node) as a table.\n    The interactive explorer was removed — use the Workbench Resource Tree for a GUI.",
            "stats-show"     => "  stats-show <Component.Field> | <Component> | --all\n    Shows index statistics: entry count, min/max key, and histogram\n    distribution (if built). Use stats-rebuild to build histograms first.\n    Examples: stats-show Player.Gold, stats-show Player, stats-show --all",
            "stats-rebuild"  => "  stats-rebuild <Component.Field> | <Component> | --all\n    Rebuilds equi-width histograms via full index scan.\n    Reports entity count, key range, and elapsed time per index.\n    Examples: stats-rebuild Player.Gold, stats-rebuild Player, stats-rebuild --all",
            "schema-fields"  => "  schema-fields <component>\n    Shows persisted FieldId assignments, types, offsets, and sizes.\n    Accepts short name (e.g. 'Player') or full schema name.",
            "schema-diff"    => "  schema-diff <component>\n    Compares persisted vs runtime schema and shows field-level changes.\n    Requires a loaded assembly (load-schema). Color-coded output:\n    green=added, red=removed/breaking, yellow=widened.",
            "schema-validate" => "  schema-validate\n    Dry-run validation of all loaded component types against persisted schema.\n    Reports OK/FAIL per component with change summaries.",
            "schema-history"  => "  schema-history\n    Shows the schema change audit trail: timestamp, component, revision changes,\n    entity count, and migration elapsed time.",
            "schema-export"   => "  schema-export [component]\n    Exports persisted schema data. Optional component name filter.\n    Respects 'set format' (table/json/csv).",
            _               => null,
        };

        if (text != null)
        {
            return CommandResult.Ok(text);
        }

        // Check extension commands
        if (_session.CustomCommands.TryGetValue(command, out var customCmd))
        {
            return CommandResult.Ok(customCmd.DetailedHelp);
        }

        return CommandResult.Error($"Error: Unknown command '{command}'. Type 'help' for available commands.");
    }

    private CommandResult ExecuteHistory()
    {
        if (_history.Count == 0)
        {
            return CommandResult.Ok("  (no history)");
        }

        var sb = new StringBuilder();
        for (var i = 0; i < _history.Count; i++)
        {
            sb.AppendLine($"  {i + 1}: {_history[i]}");
        }

        return CommandResult.Ok(sb.ToString().TrimEnd());
    }

    private CommandResult ExecutePause(List<Token> tokens, int pos)
    {
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Usage: pause <seconds>");
        }

        if (!int.TryParse(tokens[pos].Value, out var seconds) || seconds <= 0)
        {
            return CommandResult.Error($"Invalid pause duration: '{tokens[pos].Value}'. Expected a positive integer (seconds).");
        }

        Thread.Sleep(seconds * 1000);
        return CommandResult.Ok($"Paused for {seconds}s.");
    }

    // ── Reflection Bridge ──────────────────────────────────────

    private static unsafe long CreateEntityReflection(
        Transaction tx,
        Type componentType,
        ComponentSchema schema,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        // Discover archetype for this component type
        var componentTypeId = ArchetypeRegistry.GetComponentTypeId(componentType);
        if (componentTypeId < 0)
        {
            throw new InvalidOperationException($"Component type '{componentType.Name}' has no registered ComponentTypeId. Ensure an archetype is defined.");
        }
        var meta = ArchetypeRegistry.FindArchetypeForComponent(componentTypeId);
        if (meta == null)
        {
            throw new InvalidOperationException($"No archetype found containing component '{componentType.Name}'. Define an archetype with this component.");
        }

        // Build the component struct, populate fields, create ComponentValue from raw bytes
        var instance = Activator.CreateInstance(componentType);
        var handle = GCHandle.Alloc(instance, GCHandleType.Pinned);
        ComponentValue cv;
        try
        {
            var ptr = (byte*)handle.AddrOfPinnedObject();
            TextToStructConverter.WriteFields(ptr, schema.StructSize, schema, fieldValues);
            cv = ComponentValue.CreateFromRaw(componentTypeId, ptr, schema.StructSize);
        }
        finally
        {
            handle.Free();
        }

        // Spawn via non-generic public API (no reflection needed)
        var entityId = tx.SpawnByArchetypeId(meta.ArchetypeId, cv);
        return (long)entityId.RawValue;
    }

    private static unsafe IReadOnlyDictionary<string, object> ReadEntityReflection(
        Transaction tx,
        long entityId,
        Type componentType,
        ComponentSchema schema,
        out bool found)
    {
        var instance = Activator.CreateInstance(componentType);
        var method = ReadComponentMethod.MakeGenericMethod(componentType);
        var args = new[] { entityId, instance };
        found = (bool)method.Invoke(tx, args)!;

        if (!found)
        {
            return null;
        }

        instance = args[1]; // out parameter updated in the args array
        var handle = GCHandle.Alloc(instance, GCHandleType.Pinned);
        try
        {
            var ptr = (byte*)handle.AddrOfPinnedObject();
            return TextToStructConverter.ReadFields(ptr, schema.StructSize, schema);
        }
        finally
        {
            handle.Free();
        }
    }

    private static unsafe bool UpdateEntityReflection(
        Transaction tx,
        long entityId,
        Type componentType,
        ComponentSchema schema,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        // Step 1: Read current values
        var instance = Activator.CreateInstance(componentType);
        var readMethod = ReadComponentMethod.MakeGenericMethod(componentType);
        var readArgs = new[] { entityId, instance };
        var found = (bool)readMethod.Invoke(tx, readArgs)!;

        if (!found)
        {
            return false;
        }

        // Step 2: Overlay specified fields onto the current struct
        instance = readArgs[1];
        var handle = GCHandle.Alloc(instance, GCHandleType.Pinned);
        try
        {
            var ptr = (byte*)handle.AddrOfPinnedObject();
            TextToStructConverter.WriteFields(ptr, schema.StructSize, schema, fieldValues);
        }
        finally
        {
            handle.Free();
        }

        // Write updated struct back via ECS WriteComponent
        var writeMethod = WriteComponentMethod.MakeGenericMethod(componentType);
        var writeArgs = new[] { entityId, instance };
        return (bool)writeMethod.Invoke(tx, writeArgs)!;
    }

    private static bool DeleteEntityReflection(Transaction tx, long entityId)
    {
        tx.Destroy(EntityId.FromRaw(entityId));
        return true;
    }

    // ── Helpers ────────────────────────────────────────────────

    private bool RequireDatabase() => _session.IsOpen;

    private bool ResolveComponent(string name, out Type componentType, out ComponentSchema schema, out string error)
    {
        if (!_session.ComponentSchemas.TryGetValue(name, out schema))
        {
            componentType = null;
            var known = _session.ComponentSchemas.Count > 0
                ? string.Join(", ", _session.ComponentSchemas.Keys)
                : "(none)";
            error = $"Error: Component '{name}' not found. Loaded: {known}";
            return false;
        }

        if (!_session.ComponentTypes.TryGetValue(name, out componentType))
        {
            error = $"Error: Component type for '{name}' not available.";
            return false;
        }

        error = null;
        return true;
    }

    private Transaction GetTransactionForWrite(out bool isAutoCommit, out string error)
    {
        var tx = _session.GetOrCreateTransaction(out isAutoCommit);
        if (tx == null)
        {
            error = "Error: No active transaction. Use 'begin' to start one, or 'set auto-commit on'.";
            return null;
        }

        error = null;
        return tx;
    }

    private IOutputFormatter GetFormatter() => _formatters.TryGetValue(_session.Format, out var formatter) ? formatter : _formatters["table"];

    /// <summary>
    /// Extracts a file path from the token stream (quoted string or bare identifier).
    /// </summary>
    private static string ExpectPath(List<Token> tokens, ref int pos)
    {
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return null;
        }

        if (tokens[pos].Kind == TokenKind.String)
        {
            return tokens[pos++].Value;
        }

        // Bare path: consume identifier-like tokens (may include dots, dashes)
        if (tokens[pos].Kind == TokenKind.Identifier)
        {
            return tokens[pos++].Value;
        }

        return null;
    }

    /// <summary>
    /// Parses a brace expression { Field=Value, Field=Value, ... } into field assignments.
    /// </summary>
    private static Dictionary<string, string> ParseBraceExpression(List<Token> tokens, ref int pos, out string error)
    {
        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.OpenBrace)
        {
            error = "Syntax error: expected '{' for field assignments";
            return null;
        }

        pos++; // skip {

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (pos < tokens.Count && tokens[pos].Kind != TokenKind.CloseBrace)
        {
            // Field name
            if (tokens[pos].Kind != TokenKind.Identifier)
            {
                error = $"Syntax error: expected field name, got '{tokens[pos].Value}'";
                return null;
            }

            var fieldName = tokens[pos].Value;
            pos++;

            // Equals
            if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Equals)
            {
                error = $"Syntax error: expected '=' after field name '{fieldName}'";
                return null;
            }

            pos++; // skip =

            // Value
            var value = ParseValue(tokens, ref pos, out var valueError);
            if (value == null)
            {
                error = valueError;
                return null;
            }

            fields[fieldName] = value;

            // Optional comma
            if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Comma)
            {
                pos++;
            }
        }

        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.CloseBrace)
        {
            error = "Syntax error: expected '}' to close field assignments";
            return null;
        }

        pos++; // skip }
        error = null;
        return fields;
    }

    /// <summary>
    /// Parses a single value from the token stream. Returns the text representation.
    /// </summary>
    private static string ParseValue(List<Token> tokens, ref int pos, out string error)
    {
        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            error = "Syntax error: expected value";
            return null;
        }

        error = null;
        var token = tokens[pos];

        switch (token.Kind)
        {
            case TokenKind.Integer:
            case TokenKind.Float:
                pos++;
                return token.Value;

            case TokenKind.String:
                pos++;
                return token.Value; // Already unescaped by tokenizer

            case TokenKind.Char:
                pos++;
                return token.Value;

            case TokenKind.Bool:
                pos++;
                return token.Value;

            case TokenKind.OpenParen:
                // Tuple literal: (v1, v2, ...)
                return ParseTupleLiteral(tokens, ref pos, out error);

            default:
                // Could be an unquoted identifier used as a value
                if (token.Kind == TokenKind.Identifier)
                {
                    pos++;
                    return token.Value;
                }

                error = $"Syntax error: unexpected token '{token.Value}' in value position";
                return null;
        }
    }

    private static string ParseTupleLiteral(List<Token> tokens, ref int pos, out string error)
    {
        pos++; // skip (
        var sb = new StringBuilder("(");
        var first = true;

        while (pos < tokens.Count && tokens[pos].Kind != TokenKind.CloseParen)
        {
            if (!first)
            {
                if (tokens[pos].Kind != TokenKind.Comma)
                {
                    error = "Syntax error: expected ',' between tuple values";
                    return null;
                }

                sb.Append(", ");
                pos++; // skip ,
            }

            if (pos >= tokens.Count || (tokens[pos].Kind != TokenKind.Integer && tokens[pos].Kind != TokenKind.Float))
            {
                error = "Syntax error: tuple values must be numbers";
                return null;
            }

            sb.Append(tokens[pos].Value);
            pos++;
            first = false;
        }

        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.CloseParen)
        {
            error = "Syntax error: expected ')' to close tuple";
            return null;
        }

        pos++; // skip )
        sb.Append(')');
        error = null;
        return sb.ToString();
    }

}
