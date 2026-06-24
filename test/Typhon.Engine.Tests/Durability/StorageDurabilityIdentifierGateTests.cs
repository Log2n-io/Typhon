using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Grep gate (MinimalWal design 08 §7.1): <c>src/Typhon.Engine/Storage/**</c> must contain
/// <b>no WAL / LSN / FPI identifiers</b> — the Storage layer is durability-agnostic; it persists opaque pages,
/// it does not know about the write-ahead log, log-sequence-numbers, or the retired Full-Page-Image machinery.
/// <para>
/// The gate is <b>identifier-scoped</b> (08 §7.1's own wording is "identifiers"): comments and string/char
/// literals are stripped before matching, so explanatory prose ("...the FPI replacement for the bitmap...") is
/// allowed while a leaked WAL/LSN/FPI identifier (a re-introduced <c>WalCrc</c>, a <c>CheckpointLsn</c>, an
/// <c>opFpi</c>) fails the build.
/// </para>
/// <para>
/// "allowlist: none" is honored without a maintained exemption list: the only benign collision is the English
/// word "Walk" (<c>WalkIndicesMap</c>, <c>maxWalk</c>, …), excluded by the <c>Wal(?!k)</c> negative lookahead.
/// File-Page-Index abbreviations are renamed away (AC2) so any surviving <c>fpi</c> means Full-Page-Image.
/// </para>
/// </summary>
[TestFixture]
public class StorageDurabilityIdentifierGateTests
{
    // Whole identifier that embeds the WAL/LSN/FPI acronym. Wal(?!k) keeps the "Walk" chain-traversal vocabulary.
    private static readonly Regex ForbiddenIdentifier =
        new(@"\b\w*(?:Lsn|Fpi|Wal(?!k))\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Test]
    public void StorageLayer_HasNoWalLsnFpiIdentifiers()
    {
        var storageDir = LocateStorageDir();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(storageDir, "*.cs", SearchOption.AllDirectories))
        {
            var stripped = StripCommentsAndStringLiterals(File.ReadAllText(file));
            var lines = stripped.Split('\n');
            var relative = Path.GetRelativePath(storageDir, file);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in ForbiddenIdentifier.Matches(lines[i]))
                {
                    offenders.Add($"{relative}:{i + 1}: {m.Value}");
                }
            }
        }

        Assert.That(
            offenders,
            Is.Empty,
            $"Storage/ must contain no WAL/LSN/FPI identifiers (MinimalWal 08 §7.1). Offenders:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// Replaces every comment and string/char-literal character with a space, preserving newlines so reported
    /// line numbers stay accurate. A small hand-rolled scanner — robust enough for this controlled folder and free
    /// of a Roslyn dependency. Handles //, /* */, "...", verbatim @"...", and '...'; interpolation holes are
    /// treated as string content (a leaked identifier always also appears in plain declarations/signatures).
    /// </summary>
    private static string StripCommentsAndStringLiterals(string src)
    {
        var sb = new StringBuilder(src.Length);
        var n = src.Length;
        var i = 0;

        while (i < n)
        {
            var c = src[i];

            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                i += 2;
                while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/'))
                {
                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                i += 2; // consume the closing */
                continue;
            }

            if (c == '@' && i + 1 < n && src[i + 1] == '"')
            {
                i += 2;
                while (i < n)
                {
                    if (src[i] == '"')
                    {
                        if (i + 1 < n && src[i + 1] == '"') // "" escape inside a verbatim string
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                i++;
                while (i < n)
                {
                    if (src[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (src[i] == '"')
                    {
                        i++;
                        break;
                    }

                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (src[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (src[i] == '\'')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolves <c>src/Typhon.Engine/Storage</c> by walking up from the test assembly's location (the project
    /// uses DeterministicSourcePaths, so <c>[CallerFilePath]</c> is remapped to <c>/_/…</c> and unusable here).
    /// The test <c>bin/</c> always lives under the repo, so the ancestor walk resolves it for both local and CI runs.
    /// </summary>
    private static string LocateStorageDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "Typhon.Engine", "Storage");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Could not locate src/Typhon.Engine/Storage walking up from '{AppContext.BaseDirectory}'.");
    }
}
