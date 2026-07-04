using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;
using Typhon.Shell.Session;

namespace Typhon.Shell;

/// <summary>
/// PrettyPrompt callbacks providing context-sensitive completions and syntax highlighting
/// for the Typhon shell REPL.
/// </summary>
internal sealed class TshPromptCallbacks : PromptCallbacks
{
    private readonly ShellSession _session;

    // All command names (Phase 1 + Phase 2)
    private static readonly string[] Commands =
    [
        "open", "close", "info",
        "load-schema", "reload-schema", "schema-list", "describe",
        "begin", "commit", "rollback",
        "create", "read", "update", "delete",
        "set", "help", "history", "exit", "quit",
        // Phase 2: Diagnostics
        "cache-stats",
        "btree", "btree-dump", "btree-validate",
        "revisions", "mvcc-stats",
        "transactions", "memory", "resources",
        "stats-show", "stats-rebuild",
        // Phase 5: Schema Inspection
        "schema-fields", "schema-diff", "schema-validate", "schema-history", "schema-export"
    ];

    // Commands that take a component name as next argument
    private static readonly HashSet<string> ComponentCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "create", "read", "update", "delete", "describe", "mvcc-stats",
        "stats-show", "stats-rebuild"
    };

    // Commands that take a component name as second arg (after entity ID)
    private static readonly HashSet<string> ComponentAfterIdCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "revisions"
    };

    // Setting keys
    private static readonly string[] SettingKeys =
    [
        "format", "auto-commit", "verbose", "page-size", "color", "timing"
    ];

    // Setting value options
    private static readonly Dictionary<string, string[]> SettingValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["format"] = ["table", "full-table", "json", "csv"],
        ["auto-commit"] = ["on", "off"],
        ["verbose"] = ["on", "off"],
        ["color"] = ["auto", "on", "off"],
        ["timing"] = ["on", "off"],
    };

    // Keywords for syntax highlighting
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "close", "info", "load-schema", "reload-schema", "schema-list", "describe",
        "begin", "commit", "rollback", "create", "read", "update", "delete",
        "set", "help", "history", "exit", "quit", "true", "false",
        // Phase 2
        "cache-stats",
        "btree", "btree-dump", "btree-validate", "revisions", "mvcc-stats",
        "transactions", "memory", "resources", "stats-show", "stats-rebuild", "where",
        // Phase 5
        "schema-fields", "schema-diff", "schema-validate", "schema-history", "schema-export"
    };

    public TshPromptCallbacks(ShellSession session)
    {
        _session = session;
    }

    protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(string text, int caret, TextSpan spanToBeReplaced, CancellationToken cancellationToken)
    {
        var items = new List<CompletionItem>();
        var words = text[..caret].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length <= 1)
        {
            // At start or typing first word: suggest commands
            var prefix = words.Length == 1 ? words[0] : "";
            foreach (var cmd in Commands)
            {
                if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new CompletionItem(
                        replacementText: cmd,
                        getExtendedDescription: _ => Task.FromResult<FormattedString>(GetCommandDescription(cmd))
                    ));
                }
            }

            // Extension commands
            foreach (var kvp in _session.CustomCommands)
            {
                if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var desc = kvp.Value.Description;
                    items.Add(new CompletionItem(
                        replacementText: kvp.Key,
                        getExtendedDescription: _ => Task.FromResult<FormattedString>(desc)
                    ));
                }
            }
        }
        else
        {
            var command = words[0].ToLowerInvariant();

            if (ComponentCommands.Contains(command))
            {
                // After read/update/delete: second arg is entity ID, third is component name
                // After create/describe/mvcc-stats: next arg is component name
                var componentArgPos = command is "read" or "update" or "delete" ? 2 : 1;

                if (words.Length == componentArgPos + 1)
                {
                    var prefix = words[^1];
                    foreach (var name in _session.ComponentSchemas.Keys)
                    {
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            items.Add(new CompletionItem(replacementText: name));
                        }
                    }
                }
            }
            else if (ComponentAfterIdCommands.Contains(command))
            {
                // revisions: <id> <component> — component at position 2
                if (words.Length == 3)
                {
                    var prefix = words[^1];
                    foreach (var name in _session.ComponentSchemas.Keys)
                    {
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            items.Add(new CompletionItem(replacementText: name));
                        }
                    }
                }
            }
            else if (command == "set")
            {
                if (words.Length == 2)
                {
                    var prefix = words[1];
                    foreach (var key in SettingKeys)
                    {
                        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            items.Add(new CompletionItem(replacementText: key));
                        }
                    }
                }
                else if (words.Length == 3 && SettingValues.TryGetValue(words[1], out var values))
                {
                    var prefix = words[2];
                    foreach (var val in values)
                    {
                        if (val.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            items.Add(new CompletionItem(replacementText: val));
                        }
                    }
                }
            }
            else if (command == "help")
            {
                if (words.Length == 2)
                {
                    var prefix = words[1];
                    foreach (var cmd in Commands)
                    {
                        if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            items.Add(new CompletionItem(replacementText: cmd));
                        }
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<CompletionItem>>(items);
    }

    protected override Task<IReadOnlyCollection<FormatSpan>> HighlightCallbackAsync(string text, CancellationToken cancellationToken)
    {
        var spans = new List<FormatSpan>();
        var i = 0;

        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                // String literal — yellow
                var start = i;
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }

                    i++;
                }

                if (i < text.Length)
                {
                    i++;
                }

                spans.Add(new FormatSpan(start, i - start, AnsiColor.Yellow));
            }
            else if (text[i] == '#')
            {
                // Comment or entity ID prefix
                var start = i;
                i++;
                if (i < text.Length && char.IsDigit(text[i]))
                {
                    // #entityId — cyan
                    while (i < text.Length && char.IsDigit(text[i]))
                    {
                        i++;
                    }

                    spans.Add(new FormatSpan(start, i - start, AnsiColor.Cyan));
                }
                else if (start == 0)
                {
                    // Comment — grey (only at start of line)
                    spans.Add(new FormatSpan(start, text.Length, AnsiColor.BrightBlack));
                    break;
                }
            }
            else if (char.IsDigit(text[i]) || (text[i] == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                // Number — skip (no highlighting)
                if (text[i] == '-')
                {
                    i++;
                }

                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                while (i < text.Length && "uUlLdDfF".Contains(text[i]))
                {
                    i++;
                }
            }
            else if (char.IsLetter(text[i]) || text[i] == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '-'))
                {
                    i++;
                }

                var word = text[start..i];

                if (Keywords.Contains(word) || _session.CustomCommands.ContainsKey(word))
                {
                    spans.Add(new FormatSpan(start, i - start, AnsiColor.BrightBlue));
                }
                else if (_session.ComponentSchemas.ContainsKey(word))
                {
                    spans.Add(new FormatSpan(start, i - start, AnsiColor.Green));
                }
            }
            else
            {
                i++;
            }
        }

        return Task.FromResult<IReadOnlyCollection<FormatSpan>>(spans);
    }

    private static string GetCommandDescription(string cmd) =>
        cmd switch
        {
            "open"          => "Open (or create) a database",
            "close"         => "Close current database",
            "info"          => "Show database summary",
            "load-schema"   => "Load component types from assembly",
            "reload-schema" => "Reload all schema assemblies",
            "schema-list"   => "List loaded components",
            "describe"      => "Show component field layout",
            "begin"         => "Start a new transaction",
            "commit"        => "Commit current transaction",
            "rollback"      => "Rollback current transaction",
            "create"        => "Create an entity",
            "read"          => "Read entity component data",
            "update"        => "Update entity component data",
            "delete"        => "Delete entity component",
            "set"           => "View or change shell settings",
            "help"          => "Show help for commands",
            "history"       => "Show command history",
            "exit"          => "Exit the shell",
            "quit"          => "Exit the shell",
            // Phase 2: Diagnostics
            "cache-stats"    => "Page cache hit rate & state breakdown",
            "btree"          => "B+Tree index statistics",
            "btree-dump"     => "Dump B+Tree nodes",
            "btree-validate" => "Validate B+Tree consistency",
            "revisions"      => "Show entity revision chain",
            "mvcc-stats"     => "MVCC revision statistics",
            "transactions"   => "Active transaction list",
            "memory"         => "Memory usage by subsystem",
            "resources"      => "Resource graph as a table",
            "stats-show"     => "Index statistics & histogram",
            "stats-rebuild"  => "Rebuild histograms for indexes",
            // Phase 5: Schema Inspection
            "schema-fields"   => "Show persisted FieldId assignments",
            "schema-diff"     => "Compare persisted vs runtime schema",
            "schema-validate" => "Dry-run validation for all components",
            "schema-history"  => "Show schema change audit trail",
            "schema-export"   => "Export persisted schema data",
            _               => ""
        };
}
