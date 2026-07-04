using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using Typhon.Schema.Definition;
using Typhon.Shell.Parsing;
using Typhon.Shell.Session;

namespace Typhon.Shell.Commands;

/// <summary>
/// Handles all Phase 2 diagnostic commands: cache, segments, B+Trees, MVCC, memory, resources.
/// Peer to <see cref="CommandExecutor"/> which handles Phase 1 CRUD/schema/transaction commands.
/// </summary>
internal sealed class DiagnosticCommandExecutor
{
    private readonly ShellSession _session;

    public DiagnosticCommandExecutor(ShellSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Dispatches a diagnostic command. Returns null if the command is not recognized as diagnostic.
    /// </summary>
    public CommandResult? Dispatch(string command, List<Token> tokens)
    {
        return command switch
        {
            "cache-stats"      => ExecuteCacheStats(),
            "cache-pages"      => ExecuteCachePages(tokens, 1),
            "page-dump"        => ExecutePageDump(tokens, 1),
            "segments"         => ExecuteSegments(),
            "segment-detail"   => ExecuteSegmentDetail(tokens, 1),
            "btree"            => ExecuteBTree(tokens, 1),
            "btree-dump"       => ExecuteBTreeDump(tokens, 1),
            "btree-validate"   => ExecuteBTreeValidate(tokens, 1),
            "revisions"        => ExecuteRevisions(tokens, 1),
            "transactions"     => ExecuteTransactions(),
            "mvcc-stats"       => ExecuteMvccStats(tokens, 1),
            "memory"           => ExecuteMemory(),
            "resources"        => ExecuteResources(tokens, 1),
            "stats-show"       => ExecuteStatsShow(tokens, 1),
            "stats-rebuild"    => ExecuteStatsRebuild(tokens, 1),
            "db-stats"         => ExecuteDbStats(),
            "list-archetypes"  => ExecuteListArchetypes(),
            "entity-info"      => ExecuteEntityInfo(tokens, 1),
            _                  => null
        };
    }

    // ── Page Cache Diagnostics ────────────────────────────────

    private CommandResult ExecuteCacheStats()
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        var sb = new StringBuilder();
        sb.AppendLine("  [white]Page Cache[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");

        var mmf = _session.Engine.MMF;
        var metrics = mmf.GetMetrics();
        metrics.GetMemPageExtraInfo(out var extra);

        var totalPages = extra.FreeMemPageCount + extra.AllocatingMemPageCount +
                         extra.IdleMemPageCount + extra.ExclusiveMemPageCount;
        var totalBytes = (long)totalPages * PagedMMF.PageSize;
        sb.AppendLine($"  [grey]Total pages:[/]     [white]{totalPages}[/] [grey]({FormatBytes(totalBytes)})[/]");

        sb.AppendLine("  [grey]State breakdown:[/]");
        sb.AppendLine($"    [grey]Free:[/]          [white]{extra.FreeMemPageCount}[/]  [grey]({Pct(extra.FreeMemPageCount, totalPages)})[/]");
        sb.AppendLine($"    [grey]Idle:[/]          [white]{extra.IdleMemPageCount}[/]  [grey]({Pct(extra.IdleMemPageCount, totalPages)})[/]");
        sb.AppendLine($"    [grey]Exclusive:[/]     [white]{extra.ExclusiveMemPageCount}[/]  [grey]({Pct(extra.ExclusiveMemPageCount, totalPages)})[/]");
        sb.AppendLine($"    [grey]Dirty:[/]         [white]{extra.DirtyPageCount}[/]  [grey]({Pct(extra.DirtyPageCount, totalPages)})[/]");
        sb.AppendLine($"    [grey]Allocating:[/]    [white]{extra.AllocatingMemPageCount}[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");

        var totalRequests = metrics.MemPageCacheHit + metrics.MemPageCacheMiss;
        if (totalRequests > 0)
        {
            var hitRate = (double)metrics.MemPageCacheHit / totalRequests;
            sb.AppendLine($"  [grey]Hit rate:[/]        [white]{hitRate:P1}[/]  [grey]({metrics.MemPageCacheHit:N0} / {totalRequests:N0})[/]");
        }
        else
        {
            sb.AppendLine("  [grey]Hit rate:[/]        [white]N/A[/]  [grey](no requests)[/]");
        }

        sb.AppendLine($"  [grey]Reads from disk:[/] [white]{metrics.ReadFromDiskCount:N0}[/]");
        sb.AppendLine($"  [grey]Writes to disk:[/]  [white]{metrics.PageWrittenToDiskCount:N0}[/]  [grey]({metrics.WrittenOperationCount:N0} ops)[/]");

        if (extra.BackpressureWaitCount > 0 || extra.EpochProtectedPageCount > 0 || extra.SlotRefPageCount > 0)
        {
            sb.AppendLine("  [grey]──────────────────────────────────────[/]");
            if (extra.EpochProtectedPageCount > 0)
            {
                sb.AppendLine($"  [grey]Epoch-protected:[/] [white]{extra.EpochProtectedPageCount}[/]  [grey]({Pct(extra.EpochProtectedPageCount, totalPages)})[/]");
            }
            if (extra.SlotRefPageCount > 0)
            {
                sb.AppendLine($"  [grey]Slot-referenced:[/] [white]{extra.SlotRefPageCount}[/]  [grey]({Pct(extra.SlotRefPageCount, totalPages)})[/]");
            }
            if (extra.BackpressureWaitCount > 0)
            {
                sb.AppendLine($"  [grey]Backpressure:[/]    [yellow]{extra.BackpressureWaitCount:N0}[/] [grey]waits[/]");
            }
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private CommandResult ExecuteCachePages(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        // Parse optional where clause
        string filterState = null;

        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Identifier &&
            tokens[pos].Value.Equals("where", StringComparison.OrdinalIgnoreCase))
        {
            pos++;
            while (pos < tokens.Count && tokens[pos].Kind == TokenKind.Identifier)
            {
                var key = tokens[pos].Value.ToLowerInvariant();
                pos++;
                if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Equals)
                {
                    pos++;
                    if (pos < tokens.Count && tokens[pos].Kind != TokenKind.End)
                    {
                        var value = tokens[pos].Value;
                        pos++;
                        switch (key)
                        {
                            case "state":
                                filterState = value.ToLowerInvariant();
                                break;
                            default:
                                return CommandResult.Error($"Error: Unknown filter key '{key}'. Valid: state");
                        }
                    }
                }
            }
        }

        // Use GetMemPageExtraInfo for the state breakdown since we can't iterate memory pages directly
        var mmf = _session.Engine.MMF;
        var metrics = mmf.GetMetrics();
        metrics.GetMemPageExtraInfo(out var extra);

        var sb = new StringBuilder();
        sb.AppendLine("  [white]Memory Page State Summary[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");

        var states = new (string Name, int Count)[]
        {
            ("Free", extra.FreeMemPageCount),
            ("Allocating", extra.AllocatingMemPageCount),
            ("Idle", extra.IdleMemPageCount),
            ("Exclusive", extra.ExclusiveMemPageCount),
            ("Dirty", extra.DirtyPageCount),
        };

        sb.AppendLine("  [grey]State          Count[/]");
        sb.AppendLine("  [grey]─────          ─────[/]");

        foreach (var (name, count) in states)
        {
            if (filterState != null && !name.Equals(filterState, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.AppendLine($"  {name,-14} {count}");
        }

        sb.AppendLine();
        sb.AppendLine($"  [grey]Pending I/O reads:[/] [white]{extra.PendingIOReadCount}[/]");
        sb.AppendLine($"  [grey]Clock sweep range:[/] [white]{extra.MinClockSweepCounter}–{extra.MaxClockSweepCounter}[/]");

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private CommandResult ExecutePageDump(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Integer)
        {
            return CommandResult.Error("Syntax error: page-dump <pageNumber> [--raw]");
        }

        var pageIndex = int.Parse(tokens[pos].Value, CultureInfo.InvariantCulture);
        pos++;

        var raw = false;
        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.DoubleDash)
        {
            pos++;
            if (pos < tokens.Count && tokens[pos].Value.Equals("raw", StringComparison.OrdinalIgnoreCase))
            {
                raw = true;
            }
        }

        var mmf = _session.Engine.MMF;
        var epochManager = _session.Engine.EpochManager;
        using var guard = EpochGuard.Enter(epochManager);
        var epoch = epochManager.GlobalEpoch;

        if (!mmf.RequestPageEpoch(pageIndex, epoch, out var memPageIdx))
        {
            return CommandResult.Error($"Error: Could not access page {pageIndex}.");
        }

        unsafe
        {
            var pageAddr = mmf.GetMemPageAddress(memPageIdx);
            var sb = new StringBuilder();

            if (raw)
            {
                // Raw hex dump of the entire page via direct pointer
                var wholePage = new ReadOnlySpan<byte>(pageAddr, PagedMMF.PageSize);
                for (var offset = 0; offset < wholePage.Length; offset += 16)
                {
                    sb.Append($"  {offset:X4}: ");
                    var end = Math.Min(offset + 16, wholePage.Length);
                    for (var j = offset; j < end; j++)
                    {
                        sb.Append($"{wholePage[j]:X2} ");
                        if (j == offset + 7)
                        {
                            sb.Append(' ');
                        }
                    }

                    sb.Append(' ');
                    for (var j = offset; j < end; j++)
                    {
                        var b = wholePage[j];
                        sb.Append(b is >= 32 and < 127 ? (char)b : '.');
                    }

                    sb.AppendLine();
                }
            }
            else
            {
                // Structured view: parse the PageBaseHeader from the header span
                var headerSpan = new ReadOnlySpan<byte>(pageAddr, PagedMMF.PageHeaderSize);
                var baseHeader = MemoryMarshal.Read<PageBaseHeader>(headerSpan);

                sb.AppendLine($"  [white]Page {pageIndex} — Structured View[/]");
                sb.AppendLine("  [grey]══════════════════════════════════════[/]");
                sb.AppendLine();
                sb.AppendLine("  [white]PageBaseHeader[/]");
                sb.AppendLine("  [grey]──────────────────────────────────────[/]");
                sb.AppendLine($"  [grey]Flags:[/]           [white]{baseHeader.Flags}[/]");
                sb.AppendLine($"  [grey]Type:[/]            [white]{baseHeader.Type}[/]");
                sb.AppendLine($"  [grey]FormatRevision:[/]  [white]{baseHeader.FormatRevision}[/]");
                sb.AppendLine($"  [grey]ChangeRevision:[/]  [white]{baseHeader.ChangeRevision}[/]");
                sb.AppendLine();

                // Hex dump of first 128 bytes of raw data area
                sb.AppendLine("  [white]Data Preview (first 128 bytes)[/]");
                sb.AppendLine("  [grey]──────────────────────────────────────[/]");
                var rawData = new ReadOnlySpan<byte>(pageAddr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize);
                var previewLen = Math.Min(128, rawData.Length);
                for (var offset = 0; offset < previewLen; offset += 16)
                {
                    sb.Append($"  {offset:X4}: ");
                    var end = Math.Min(offset + 16, previewLen);
                    for (var j = offset; j < end; j++)
                    {
                        sb.Append($"{rawData[j]:X2} ");
                        if (j == offset + 7)
                        {
                            sb.Append(' ');
                        }
                    }

                    sb.Append("  ");
                    for (var j = offset; j < end; j++)
                    {
                        var b = rawData[j];
                        sb.Append(b is >= 32 and < 127 ? (char)b : '.');
                    }

                    sb.AppendLine();
                }
            }

            return raw ? CommandResult.Ok(sb.ToString().TrimEnd()) : CommandResult.Markup(sb.ToString().TrimEnd());
        }
    }

    // ── Database Volumetry ──────────────────────────────────────

    private CommandResult ExecuteDbStats()
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        var sb = new StringBuilder();
        var mmf = _session.Engine.MMF;

        // ── File Pages ──
        var capPages = mmf.OccupancyCapacityPages;
        var capBytes = (long)capPages * PagedMMF.PageSize;

        sb.AppendLine("  [white]File Pages[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────────────────────────────────────────[/]");
        sb.AppendLine($"  [grey]Capacity:[/]        [white]{capPages:N0}[/] pages  [grey]({FormatBytes(capBytes)})[/]");
        sb.AppendLine($"  [grey]Page size:[/]       [white]{PagedMMF.PageSize:N0} B[/]");
        sb.AppendLine();

        // ── Per-component segment breakdown ──
        var tables = GetComponentTables();
        if (tables.Count == 0)
        {
            sb.AppendLine("  [yellow]No component tables registered.[/]");
            return CommandResult.Markup(sb.ToString().TrimEnd());
        }

        sb.AppendLine("  [white]Segments[/]");
        sb.AppendLine("  [grey]Segment                    ChunkSz   Used     Cap       Fill      Pages  Data Size[/]");
        sb.AppendLine("  [grey]─────────────────────────   ───────   ──────   ───────   ───────   ─────  ─────────[/]");

        long totalChunksUsed = 0;
        long totalChunksCap = 0;
        long totalDataBytes = 0;
        int totalPages = 0;

        foreach (var (name, table) in tables)
        {
            AppendDbStatsSegmentRow(sb, $"{name}.Data", table.ComponentSegment, ref totalChunksUsed, ref totalChunksCap, ref totalDataBytes, ref totalPages);
            AppendDbStatsSegmentRow(sb, $"{name}.RevTable", table.CompRevTableSegment, ref totalChunksUsed, ref totalChunksCap, ref totalDataBytes, ref totalPages);

            if (table.DefaultIndexSegment != null)
            {
                AppendDbStatsSegmentRow(sb, $"{name}.PK_Index", table.DefaultIndexSegment, ref totalChunksUsed, ref totalChunksCap, ref totalDataBytes, ref totalPages);
            }

            if (table.String64IndexSegment != null)
            {
                AppendDbStatsSegmentRow(sb, $"{name}.Str64_Index", table.String64IndexSegment, ref totalChunksUsed, ref totalChunksCap, ref totalDataBytes, ref totalPages);
            }

            if (table.TailIndexSegment != null)
            {
                AppendDbStatsSegmentRow(sb, $"{name}.Tail_Index", table.TailIndexSegment, ref totalChunksUsed, ref totalChunksCap, ref totalDataBytes, ref totalPages);
            }
        }

        sb.AppendLine("  [grey]─────────────────────────   ───────   ──────   ───────   ───────   ─────  ─────────[/]");
        var totalFill = totalChunksCap > 0 ? (double)totalChunksUsed / totalChunksCap : 0.0;
        sb.AppendLine($"  {"[white]Total[/]",-35} {"",9}   {totalChunksUsed,6:N0}   {totalChunksCap,7:N0}   {totalFill,7:P1}   {totalPages,5:N0}  [white]{FormatBytes(totalDataBytes)}[/]");

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private static void AppendDbStatsSegmentRow(StringBuilder sb, string name, ChunkBasedSegment<PersistentStore> seg,
        ref long totalChunksUsed, ref long totalChunksCap, ref long totalDataBytes, ref int totalPages)
    {
        if (seg == null)
        {
            return;
        }

        var used = seg.AllocatedChunkCount;
        var cap = seg.ChunkCapacity;
        var fill = cap > 0 ? (double)used / cap : 0.0;
        var pages = seg.Length;
        var dataBytes = (long)used * seg.Stride;

        totalChunksUsed += used;
        totalChunksCap += cap;
        totalDataBytes += dataBytes;
        totalPages += pages;

        var chunkSz = seg.Stride < 1024 ? $"{seg.Stride} B" : $"{seg.Stride / 1024.0:F1} KB";
        sb.AppendLine($"  {Markup.Escape(name),-27} {chunkSz,7}   {used,6:N0}   {cap,7:N0}   {fill,7:P1}   {pages,5:N0}  {FormatBytes(dataBytes)}");
    }

    // ── Segment Diagnostics ───────────────────────────────────

    private CommandResult ExecuteSegments()
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        var sb = new StringBuilder();
        sb.AppendLine("  [grey]Segment               ChunkSize  Occupancy  Chunks (used/total)[/]");
        sb.AppendLine("  [grey]─────────────────     ─────────  ─────────  ───────────────────[/]");

        var tables = GetComponentTables();
        if (tables.Count == 0)
        {
            sb.AppendLine("  [yellow]No component tables registered.[/]");
            return CommandResult.Markup(sb.ToString().TrimEnd());
        }

        foreach (var (name, table) in tables)
        {
            AppendSegmentRow(sb, $"{name}.Data", table.ComponentSegment);
            AppendSegmentRow(sb, $"{name}.RevTable", table.CompRevTableSegment);

            if (table.DefaultIndexSegment != null)
            {
                AppendSegmentRow(sb, $"{name}.PK_Index", table.DefaultIndexSegment);
            }

            if (table.String64IndexSegment != null)
            {
                AppendSegmentRow(sb, $"{name}.Str64_Index", table.String64IndexSegment);
            }
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private static void AppendSegmentRow(StringBuilder sb, string name, ChunkBasedSegment<PersistentStore> seg)
    {
        if (seg == null)
        {
            return;
        }

        var used = seg.AllocatedChunkCount;
        var total = seg.ChunkCapacity;
        var pct = total > 0 ? (double)used / total : 0.0;
        sb.AppendLine($"  {Markup.Escape(name),-21} {seg.Stride + " B",-10} {pct:P1,-10} {used:N0} / {total:N0}");
    }

    private CommandResult ExecuteSegmentDetail(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: segment-detail <Component.Segment>");
        }

        var segName = tokens[pos].Value;
        var seg = ResolveSegment(segName);
        if (seg == null)
        {
            return CommandResult.Error($"Error: Segment '{segName}' not found. Use 'segments' to list available segments.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"  [white]{Markup.Escape(segName)} — ChunkBasedSegment<PersistentStore>[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");
        sb.AppendLine($"  [grey]Chunk size:[/]      [white]{seg.Stride} bytes[/]");

        var used = seg.AllocatedChunkCount;
        var total = seg.ChunkCapacity;
        var pct = total > 0 ? (double)used / total : 0.0;
        sb.AppendLine($"  [grey]Occupancy:[/]       [white]{pct:P1}[/] [grey]({used:N0} / {total:N0} chunks)[/]");
        sb.AppendLine($"  [grey]Free chunks:[/]     [white]{seg.FreeChunkCount:N0}[/]");
        sb.AppendLine($"  [grey]Root page cap:[/]   [white]{seg.ChunkCountRootPage} chunks[/]");

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── B+Tree Diagnostics ────────────────────────────────────

    private CommandResult ExecuteBTree(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: btree <Component.IndexName>");
        }

        var indexName = tokens[pos].Value;
        var (btree, resolveError) = ResolveIndex(indexName);
        if (btree == null)
        {
            return CommandResult.Error(resolveError);
        }

        var seg = btree.Segment;
        var sb = new StringBuilder();
        var multiStr = btree.AllowMultiple ? "multi" : "unique";
        sb.AppendLine($"  [white]B+Tree: {Markup.Escape(indexName)} ({multiStr})[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");

        var used = seg.AllocatedChunkCount;
        var total = seg.ChunkCapacity;
        var pct = total > 0 ? (double)used / total : 0.0;
        sb.AppendLine($"  [grey]Total nodes:[/]     [white]{used:N0}[/]");
        sb.AppendLine($"  [grey]Chunk capacity:[/]  [white]{total:N0}[/]");
        sb.AppendLine($"  [grey]Fill factor:[/]     [white]{pct:P1}[/]");
        sb.AppendLine($"  [grey]Node size:[/]       [white]{seg.Stride} bytes[/]");

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private CommandResult ExecuteBTreeDump(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: btree-dump <Component.IndexName> [--level N | --chunk N]");
        }

        var indexName = tokens[pos].Value;
        pos++;

        var (btree, resolveError) = ResolveIndex(indexName);
        if (btree == null)
        {
            return CommandResult.Error(resolveError);
        }

        // Parse optional --level or --chunk
        int? level = null;
        int? chunk = null;
        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.DoubleDash)
        {
            pos++;
            if (pos < tokens.Count)
            {
                var flag = tokens[pos].Value.ToLowerInvariant();
                pos++;
                if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Integer)
                {
                    var val = int.Parse(tokens[pos].Value, CultureInfo.InvariantCulture);
                    switch (flag)
                    {
                        case "level":
                            level = val;
                            break;
                        case "chunk":
                            chunk = val;
                            break;
                        default:
                            return CommandResult.Error($"Error: Unknown option '--{flag}'. Use --level or --chunk.");
                    }
                }
            }
        }

        using var btreeGuard = EpochGuard.Enter(_session.Engine.EpochManager);
        var seg = btree.Segment;
        var sb = new StringBuilder();
        sb.AppendLine($"  [white]B+Tree dump: {Markup.Escape(indexName)}[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");
        sb.AppendLine($"  [grey]Allocated nodes:[/] [white]{seg.AllocatedChunkCount}[/]");
        sb.AppendLine($"  [grey]Node size:[/]       [white]{seg.Stride} bytes[/]");

        if (level.HasValue)
        {
            sb.AppendLine($"  [grey]Filter:[/]          [white]level {level.Value}[/]");
        }
        else if (chunk.HasValue)
        {
            sb.AppendLine($"  [grey]Filter:[/]          [white]chunk {chunk.Value}[/]");

            // Dump raw chunk data
            if (chunk.Value >= 0 && chunk.Value < seg.AllocatedChunkCount)
            {
                using var accessor = seg.CreateChunkAccessor();
                unsafe
                {
                    var ptr = accessor.GetChunkAddress(chunk.Value);
                    sb.AppendLine();
                    sb.AppendLine("  [white]Raw chunk data:[/]");
                    var chunkSize = Math.Min(seg.Stride, 128);
                    for (var offset = 0; offset < chunkSize; offset += 16)
                    {
                        sb.Append($"  {offset:X4}: ");
                        var end = Math.Min(offset + 16, chunkSize);
                        for (var j = offset; j < end; j++)
                        {
                            sb.Append($"{ptr[j]:X2} ");
                        }

                        sb.AppendLine();
                    }
                }
            }
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private CommandResult ExecuteBTreeValidate(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: btree-validate <Component.IndexName>");
        }

        var indexName = tokens[pos].Value;
        var (btree, resolveError) = ResolveIndex(indexName);
        if (btree == null)
        {
            return CommandResult.Error(resolveError);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"  Validating B+Tree [white]{Markup.Escape(indexName)}[/]...");

        var checkEpochManager = _session.Engine.EpochManager;
        var checkDepth = checkEpochManager.EnterScope();
        try
        {
            var accessor = btree.Segment.CreateChunkAccessor();
            btree.CheckConsistency(ref accessor);
            sb.AppendLine("  [green]Validation passed[/]");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  [red]Validation FAILED: {Markup.Escape(ex.Message)}[/]");
        }
        finally
        {
            checkEpochManager.ExitScope(checkDepth);
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── MVCC Diagnostics ──────────────────────────────────────

    private CommandResult ExecuteRevisions(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Integer)
        {
            return CommandResult.Error("Syntax error: revisions <entityId> <component>");
        }

        var entityId = long.Parse(tokens[pos].Value, CultureInfo.InvariantCulture);
        pos++;

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: revisions <entityId> <component>");
        }

        var componentName = tokens[pos].Value;

        if (!_session.ComponentTypes.TryGetValue(componentName, out var componentType))
        {
            return CommandResult.Error($"Error: Component '{componentName}' not found.");
        }

        var table = _session.Engine.GetComponentTable(componentType);
        if (table == null)
        {
            return CommandResult.Error($"Error: No component table for '{componentName}'.");
        }

        // For the revisions command, we need the entity's EntityRecord to get the Location ChunkId,
        // then access the CompRevTableSegment from that.
        if (table.StorageMode != StorageMode.Versioned)
        {
            return CommandResult.Error($"Error: Component '{componentName}' is {table.StorageMode} — no revision chain (only Versioned components have revisions).");
        }

        // Resolve entity via EntityMap (ECS path)
        var eid = EntityId.FromRaw(entityId);
        var meta = ArchetypeRegistry.GetMetadata(eid.ArchetypeId);
        if (meta == null)
        {
            return CommandResult.Error($"Error: ArchetypeId {eid.ArchetypeId} not registered. Use the full EntityId (EntityKey << 12 | ArchetypeId).");
        }

        // Find the slot for this component in the archetype
        var compTypeId = ArchetypeRegistry.GetComponentTypeId(componentType);
        if (!meta.TryGetSlot(compTypeId, out var slot))
        {
            return CommandResult.Error($"Error: Archetype '{meta.ArchetypeType?.Name}' does not contain component '{componentName}'.");
        }

        var dbe = _session.Engine;
        var engineState = dbe._archetypeStates[meta.ArchetypeId];
        if (engineState?.EntityMap == null)
        {
            return CommandResult.Error($"Error: Archetype '{meta.ArchetypeType?.Name}' has no EntityMap.");
        }

        var epochManager = dbe.EpochManager;
        var epochDepth = epochManager.EnterScope();
        try
        {
            // Look up entity in EntityMap
            var readBuf = new byte[meta._entityRecordSize];
            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            unsafe
            {
                fixed (byte* ptr = readBuf)
                {
                    bool found = engineState.EntityMap.TryGet(eid.EntityKey, ptr, ref accessor);
                    accessor.Dispose();

                    if (!found)
                    {
                        return CommandResult.Error($"Error: Entity {entityId} not found in EntityMap.");
                    }

                    // Get the CompRevTable first chunk ID from the EntityRecord location
                    int compRevFirstChunkId = EntityRecordAccessor.GetLocation(ptr, slot);
                    if (compRevFirstChunkId == 0)
                    {
                        return CommandResult.Error($"Error: Entity {entityId} has no revision chain for '{componentName}' (ChunkId=0).");
                    }

                    // Walk the revision chain
                    var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();
                    try
                    {
                        ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
                        var sb = new StringBuilder();
                        sb.AppendLine($"  [white]Revisions for Entity {entityId} / {Markup.Escape(componentName)}[/]");
                        sb.AppendLine("  [grey]══════════════════════════════════════[/]");
                        sb.AppendLine($"  [grey]EntityPK:[/]     [white]{header.EntityPK}[/]");
                        sb.AppendLine($"  [grey]ItemCount:[/]    [white]{header.ItemCount}[/]");
                        sb.AppendLine($"  [grey]FirstChunkId:[/] [white]{compRevFirstChunkId}[/]");
                        sb.AppendLine();
                        sb.AppendLine("  [grey]  #  ChunkId    TSN     UoW   Iso[/]");
                        sb.AppendLine("  [grey]───  ────────  ──────  ────  ───[/]");

                        var enumerator = new RevisionEnumerator(ref compRevAccessor, compRevFirstChunkId, false, true);
                        int revIndex = 0;
                        while (enumerator.MoveNext())
                        {
                            ref var el = ref enumerator.Current;
                            var isoFlag = el.IsolationFlag ? "[yellow]Y[/]" : "[grey]N[/]";
                            var chunkStr = el.ComponentChunkId == 0 ? "[red]tomb[/]  " : $"[white]{el.ComponentChunkId,6}[/]  ";
                            sb.AppendLine($"  [grey]{revIndex,3}[/]  {chunkStr}[white]{el.TSN,6}[/]  [white]{el.UowId,4}[/]  {isoFlag}");
                            revIndex++;
                        }
                        enumerator.Dispose();

                        return CommandResult.Markup(sb.ToString().TrimEnd());
                    }
                    finally
                    {
                        compRevAccessor.Dispose();
                    }
                }
            }
        }
        finally
        {
            epochManager.ExitScope(epochDepth);
        }
    }

    private CommandResult ExecuteTransactions()
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        var chain = _session.Engine.TransactionChain;
        var sb = new StringBuilder();
        sb.AppendLine("  [white]Active Transactions[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");
        sb.AppendLine($"  [grey]Active:[/]    [white]{chain.ActiveCount}[/]");
        sb.AppendLine($"  [grey]MinTSN:[/]    [white]{chain.MinTSN}[/]");
        sb.AppendLine($"  [grey]NextTSN:[/]   [white]{chain.NextFreeId}[/]");

        // Walk the chain to list individual transactions
        var tx = chain.Head;
        if (tx != null)
        {
            sb.AppendLine();
            sb.AppendLine("  [grey]TSN   State[/]");
            sb.AppendLine("  [grey]───   ─────[/]");
            var count = 0;
            while (tx != null && count < 50)
            {
                var isCurrent = _session.Transaction != null && tx.TSN == _session.Transaction.TSN;
                var marker = isCurrent ? " <- [cyan]current[/]" : "";
                sb.AppendLine($"  {tx.TSN,-5} {tx.State}{marker}");
                tx = tx.Next;
                count++;
            }
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private CommandResult ExecuteMvccStats(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: mvcc-stats <component>");
        }

        var componentName = tokens[pos].Value;
        if (!_session.ComponentTypes.TryGetValue(componentName, out var componentType))
        {
            return CommandResult.Error($"Error: Component '{componentName}' not found.");
        }

        var table = _session.Engine.GetComponentTable(componentType);
        if (table == null)
        {
            return CommandResult.Error($"Error: No component table for '{componentName}'.");
        }

        using var mvccGuard = EpochGuard.Enter(_session.Engine.EpochManager);
        var revSeg = table.CompRevTableSegment;
        var allocated = revSeg.AllocatedChunkCount;

        var sb = new StringBuilder();
        sb.AppendLine($"  [white]MVCC Statistics — {Markup.Escape(componentName)}[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");

        var dataSeg = table.ComponentSegment;
        sb.AppendLine($"  [grey]Data chunks:[/]       [white]{dataSeg.AllocatedChunkCount:N0} / {dataSeg.ChunkCapacity:N0}[/]");
        sb.AppendLine($"  [grey]Revision chunks:[/]   [white]{allocated:N0} / {revSeg.ChunkCapacity:N0}[/]");

        // Walk revision headers to compute statistics
        if (allocated > 0)
        {
            using var accessor = revSeg.CreateChunkAccessor();
            var totalItems = 0L;
            var maxChain = 0;
            var singleRevCount = 0;
            var multiChunkCount = 0;
            var entityCount = 0;

            unsafe
            {
                for (var i = 0; i < allocated; i++)
                {
                    try
                    {
                        var ptr = (CompRevStorageHeader*)accessor.GetChunkAddress(i);
                        if (ptr->ItemCount > 0)
                        {
                            entityCount++;
                            totalItems += ptr->ItemCount;
                            if (ptr->ItemCount > maxChain)
                            {
                                maxChain = ptr->ItemCount;
                            }

                            if (ptr->ItemCount == 1)
                            {
                                singleRevCount++;
                            }

                            if (ptr->ChainLength > 1)
                            {
                                multiChunkCount++;
                            }
                        }
                    }
                    catch
                    {
                        // Skip inaccessible chunks
                    }
                }
            }

            if (entityCount > 0)
            {
                sb.AppendLine($"  [grey]Entities tracked:[/]  [white]{entityCount:N0}[/]");
                sb.AppendLine($"  [grey]Total revisions:[/]   [white]{totalItems:N0}[/]");
                sb.AppendLine($"  [grey]Avg revs/entity:[/]   [white]{(double)totalItems / entityCount:F2}[/]");
                sb.AppendLine($"  [grey]Max chain length:[/]  [white]{maxChain}[/]");
                sb.AppendLine($"  [grey]Single-revision:[/]   [white]{singleRevCount:N0}[/] [grey]({(double)singleRevCount / entityCount:P1})[/]");
                sb.AppendLine($"  [grey]Multi-chunk chains:[/] [white]{multiChunkCount:N0}[/]");
            }
        }

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── Memory Diagnostics ────────────────────────────────────

    private CommandResult ExecuteMemory()
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        var snapshot = _session.ResourceGraph.GetSnapshot();
        var sb = new StringBuilder();
        sb.AppendLine("  [white]Memory Usage[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");

        // Group by top-level subsystem
        var subsystems = new[] { "Storage", "DataEngine", "Durability", "Allocation" };
        var totalBytes = 0L;

        foreach (var subsystem in subsystems)
        {
            var path = $"Root/{subsystem}";
            var mem = snapshot.GetSubtreeMemory(path);
            if (mem > 0)
            {
                sb.AppendLine($"  [grey]{subsystem,-18}[/] [white]{FormatBytes(mem)}[/]");
                totalBytes += mem;
            }
        }

        // Also check for nodes not under known subsystems
        var allMem = snapshot.Nodes.Values
            .Where(n => n.Memory.HasValue)
            .Sum(n => n.Memory.Value.AllocatedBytes);

        if (allMem > totalBytes)
        {
            sb.AppendLine($"  [grey]{"Other",-18}[/] [white]{FormatBytes(allMem - totalBytes)}[/]");
        }

        sb.AppendLine("  [grey]──────────────────────────────────────[/]");
        sb.AppendLine($"  [grey]{"Total",-18}[/] [white]{FormatBytes(allMem)}[/]");

        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    // ── Resource Graph ────────────────────────────────────────

    private CommandResult ExecuteResources(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        // Check for --flat flag
        var flat = false;
        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.DoubleDash)
        {
            pos++;
            if (pos < tokens.Count && tokens[pos].Value.Equals("flat", StringComparison.OrdinalIgnoreCase))
            {
                flat = true;
            }
        }

        if (!flat)
        {
            // Non-flat mode requires interactive terminal
            if (Console.IsInputRedirected)
            {
                return CommandResult.Error("Error: 'resources' (interactive) requires a terminal. Use 'resources --flat' in pipe/batch mode.");
            }

            // Launch Terminal.Gui TUI
            try
            {
                var explorer = new Tui.ResourceExplorer(_session);
                explorer.Run();
                return CommandResult.Ok();
            }
            catch (Exception ex)
            {
                return CommandResult.Error($"Error launching resource explorer: {ex.Message}");
            }
        }

        // Flat mode: enumerate all resources as a table
        var snapshot = _session.ResourceGraph.GetSnapshot();
        var sb = new StringBuilder();
        sb.AppendLine("  [grey]Resource                            Type            Memory    Capacity[/]");
        sb.AppendLine("  [grey]──────────────────────              ──────          ──────    ────────[/]");

        foreach (var node in snapshot.Nodes.Values.OrderBy(n => n.Path))
        {
            var memStr = node.Memory.HasValue ? FormatBytes(node.Memory.Value.AllocatedBytes) : "--";
            var capStr = node.Capacity.HasValue ? $"{node.Capacity.Value.Utilization:P1}" : "--";

            sb.AppendLine($"  {Markup.Escape(node.Path),-35} {node.Type,-15} {memStr,-9} {capStr}");
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

    private List<(string Name, ComponentTable Table)> GetComponentTables()
    {
        var result = new List<(string, ComponentTable)>();
        foreach (var kvp in _session.ComponentTypes)
        {
            var table = _session.Engine.GetComponentTable(kvp.Value);
            if (table != null)
            {
                result.Add((kvp.Key, table));
            }
        }

        return result;
    }

    private ChunkBasedSegment<PersistentStore> ResolveSegment(string name)
    {
        // Format: ComponentName.SegmentSuffix (e.g., ARPG.Position.Data, ARPG.Position.PK_Index)
        var dotPos = name.LastIndexOf('.');
        if (dotPos < 0)
        {
            return null;
        }

        var componentName = name[..dotPos];
        var suffix = name[(dotPos + 1)..];

        if (!_session.ComponentTypes.TryGetValue(componentName, out var componentType))
        {
            return null;
        }

        var table = _session.Engine.GetComponentTable(componentType);
        if (table == null)
        {
            return null;
        }

        return suffix.ToLowerInvariant() switch
        {
            "data"        => table.ComponentSegment,
            "revtable"    => table.CompRevTableSegment,
            "pk_index"    => table.DefaultIndexSegment,
            "str64_index" => table.String64IndexSegment,
            _             => null
        };
    }

    private (BTreeBase<PersistentStore> Tree, string Error) ResolveIndex(string name)
    {
        // Format: ComponentName.FieldName (e.g., ARPG.Position.PK or ARPG.Position.PlayerId)
        var dotPos = name.LastIndexOf('.');
        if (dotPos < 0)
        {
            return (null, $"Error: Index name must be Component.Field (e.g., CompA.PK). Got '{name}'.");
        }

        var componentName = name[..dotPos];
        var fieldName = name[(dotPos + 1)..];

        if (!_session.ComponentTypes.TryGetValue(componentName, out var componentType))
        {
            return (null, $"Error: Component '{componentName}' not found.");
        }

        var table = _session.Engine.GetComponentTable(componentType);
        if (table == null)
        {
            return (null, $"Error: No component table for '{componentName}'.");
        }

        // Entity routing goes through EntityMap
        if (fieldName.Equals("PK", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Error: PK B+Tree has been eliminated. Entity routing now uses the per-archetype EntityMap (LinearHash). Use secondary index field names instead.");
        }

        // Look through indexed fields via the definition's field map
        if (table.IndexedFieldInfos != null && table.Definition.FieldsByName != null)
        {
            // Match by field name → find the corresponding IndexedFieldInfo
            if (table.Definition.FieldsByName.TryGetValue(fieldName, out var field))
            {
                foreach (var info in table.IndexedFieldInfos)
                {
                    if (info.OffsetToField == field.OffsetInComponentStorage)
                    {
                        return (info.PersistentIndex, null);
                    }
                }
            }
        }

        return (null, $"Error: Index '{fieldName}' not found on component '{componentName}'. Use 'describe {componentName}' to see indexed fields.");
    }

    // ── Statistics & Histograms ─────────────────────────────

    private CommandResult ExecuteStatsShow(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: stats-show <Component.Field> | <Component> | --all");
        }

        // --all: all components
        if (tokens[pos].Kind == TokenKind.DoubleDash)
        {
            pos++;
            if (pos >= tokens.Count || !tokens[pos].Value.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Error("Syntax error: stats-show --all");
            }

            var tables = GetComponentTables();
            if (tables.Count == 0)
            {
                return CommandResult.Ok("  No component tables found.");
            }

            var sb = new StringBuilder();
            foreach (var (name, table) in tables)
            {
                AppendComponentStats(sb, name, table);
            }

            return CommandResult.Markup(sb.ToString().TrimEnd());
        }

        var target = tokens[pos].Value;

        // Try as a component name first (handles dotted names like ARPG.Position)
        if (_session.ComponentTypes.TryGetValue(target, out var componentType))
        {
            var compTable = _session.Engine.GetComponentTable(componentType);
            if (compTable == null)
            {
                return CommandResult.Error($"Error: No component table for '{target}'.");
            }

            var sb = new StringBuilder();
            AppendComponentStats(sb, target, compTable);
            return CommandResult.Markup(sb.ToString().TrimEnd());
        }

        // Not a known component — try as Component.Field
        if (target.Contains('.'))
        {
            var resolved = ResolveIndexStats(target);
            if (resolved.Error != null)
            {
                return CommandResult.Error(resolved.Error);
            }

            var sb = new StringBuilder();
            AppendSingleIndexStats(sb, target, resolved.Stats, resolved.Index);
            return CommandResult.Markup(sb.ToString().TrimEnd());
        }

        return CommandResult.Error($"Error: Component '{target}' not found.");
    }

    private CommandResult ExecuteStatsRebuild(List<Token> tokens, int pos)
    {
        if (!RequireDatabase(out var error))
        {
            return error;
        }

        if (pos >= tokens.Count || tokens[pos].Kind == TokenKind.End)
        {
            return CommandResult.Error("Syntax error: stats-rebuild <Component.Field> | <Component> | --all");
        }

        // --all: all components
        if (tokens[pos].Kind == TokenKind.DoubleDash)
        {
            pos++;
            if (pos >= tokens.Count || !tokens[pos].Value.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Error("Syntax error: stats-rebuild --all");
            }

            var tables = GetComponentTables();
            if (tables.Count == 0)
            {
                return CommandResult.Ok("  No component tables found.");
            }

            var sb = new StringBuilder();
            var totalSw = Stopwatch.StartNew();
            var totalRebuilt = 0;
            foreach (var (name, table) in tables)
            {
                totalRebuilt += RebuildComponentHistograms(sb, name, table);
            }

            totalSw.Stop();
            sb.AppendLine();
            sb.AppendLine($"  [white]Rebuilt {totalRebuilt} histogram(s) in {FormatElapsed(totalSw.Elapsed)}[/]");
            return CommandResult.Markup(sb.ToString().TrimEnd());
        }

        var target = tokens[pos].Value;

        // Try as a component name first (handles dotted names like ARPG.Position)
        if (_session.ComponentTypes.TryGetValue(target, out var componentType))
        {
            var compTable = _session.Engine.GetComponentTable(componentType);
            if (compTable == null)
            {
                return CommandResult.Error($"Error: No component table for '{target}'.");
            }

            var sb = new StringBuilder();
            var count = RebuildComponentHistograms(sb, target, compTable);
            sb.AppendLine();
            sb.AppendLine($"  [white]Rebuilt {count} histogram(s)[/]");
            return CommandResult.Markup(sb.ToString().TrimEnd());
        }

        // Not a known component — try as Component.Field
        if (target.Contains('.'))
        {
            var resolved = ResolveIndexStats(target);
            if (resolved.Error != null)
            {
                return CommandResult.Error(resolved.Error);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"  [white]Rebuilding histogram: {Markup.Escape(target)}[/]");
            sb.AppendLine("  [grey]──────────────────────────────────────[/]");

            var sw = Stopwatch.StartNew();
            resolved.Stats.RebuildHistogram();
            sw.Stop();

            var multiStr = resolved.Index.AllowMultiple ? " [yellow]AllowMultiple[/]" : "";
            sb.AppendLine($"  [grey]Entries:[/]      [white]{resolved.Stats.EntryCount:N0}[/]{multiStr}");

            if (resolved.Stats.Histogram != null)
            {
                AppendHistogramSummary(sb, resolved.Stats.Histogram);
                AppendHistogramChart(sb, resolved.Stats.Histogram);
            }
            else
            {
                sb.AppendLine("  [grey]Histogram:[/]    [dim](empty index — no histogram)[/]");
            }

            sb.AppendLine($"  [grey]Rebuilt in:[/]   [green]{FormatElapsed(sw.Elapsed)}[/]");
            return CommandResult.Markup(sb.ToString().TrimEnd());
        }

        return CommandResult.Error($"Error: Component '{target}' not found.");
    }

    private void AppendComponentStats(StringBuilder sb, string componentName, ComponentTable table)
    {
        sb.AppendLine($"  [white]Index Statistics — {Markup.Escape(componentName)}[/]");
        sb.AppendLine("  [grey]──────────────────────────────────────[/]");

        if (table.IndexedFieldInfos == null || table.IndexedFieldInfos.Length == 0)
        {
            sb.AppendLine("  [dim](no indexed fields)[/]");
            sb.AppendLine();
            return;
        }

        for (var i = 0; i < table.IndexedFieldInfos.Length; i++)
        {
            var info = table.IndexedFieldInfos[i];
            var stats = table.IndexStats[i];
            var fieldName = ResolveFieldName(table, info.OffsetToField);
            var qualifiedName = $"{componentName}.{fieldName}";
            AppendSingleIndexStats(sb, qualifiedName, stats, info.Index);
        }

        sb.AppendLine();
    }

    private static void AppendSingleIndexStats(StringBuilder sb, string qualifiedName, IndexStatistics stats, IBTreeIndex index)
    {
        var multiStr = index.AllowMultiple ? " [yellow]AllowMultiple[/]" : " [dim]Unique[/]";
        sb.AppendLine($"  [cyan]{Markup.Escape(qualifiedName)}[/]{multiStr}");
        sb.AppendLine($"    [grey]Entries:[/]    [white]{stats.EntryCount:N0}[/]");

        if (stats.EntryCount > 0)
        {
            sb.AppendLine($"    [grey]Min:[/]        [white]{stats.MinValue:N0}[/]");
            sb.AppendLine($"    [grey]Max:[/]        [white]{stats.MaxValue:N0}[/]");
        }

        if (stats.Histogram != null)
        {
            AppendHistogramSummary(sb, stats.Histogram, indent: 4);
            AppendHistogramChart(sb, stats.Histogram, indent: 4);
        }
        else
        {
            sb.AppendLine("    [grey]Histogram:[/]  [dim](not built — use stats-rebuild)[/]");
        }
    }

    private static void AppendHistogramSummary(StringBuilder sb, Histogram histogram, int indent = 2)
    {
        var pad = new string(' ', indent);
        sb.AppendLine($"{pad}[grey]Histogram:[/]  [white]{histogram.TotalCount:N0} entities[/], range [[{histogram.MinValue:N0}..{histogram.MaxValue:N0}]], bucket width {histogram.BucketWidth:N0}");

        // Show non-empty bucket count
        var nonEmpty = 0;
        for (var i = 0; i < Histogram.BucketCount; i++)
        {
            if (histogram.BucketCounts[i] > 0)
            {
                nonEmpty++;
            }
        }

        sb.AppendLine($"{pad}[grey]Buckets:[/]    [white]{nonEmpty}[/] / {Histogram.BucketCount} non-empty");
    }

    /// <summary>
    /// Renders a compact sparkline-style histogram using Unicode block characters.
    /// Each of the 100 buckets maps to one character: ▁▂▃▄▅▆▇█ (scaled relative to max bucket count).
    /// Empty buckets render as a space. The sparkline is split into two rows of 50 characters for readability.
    /// </summary>
    private static void AppendHistogramChart(StringBuilder sb, Histogram histogram, int indent = 2)
    {
        var pad = new string(' ', indent);
        var buckets = histogram.BucketCounts;
        var max = 0;
        for (var i = 0; i < Histogram.BucketCount; i++)
        {
            if (buckets[i] > max)
            {
                max = buckets[i];
            }
        }

        if (max == 0)
        {
            return;
        }

        ReadOnlySpan<char> blocks = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

        // Build the sparkline characters
        var chars = new char[Histogram.BucketCount];
        for (var i = 0; i < Histogram.BucketCount; i++)
        {
            if (buckets[i] == 0)
            {
                chars[i] = ' ';
            }
            else
            {
                // Scale to 1..8 (never 0 for non-empty buckets)
                var level = (int)((long)buckets[i] * 7 / max);
                chars[i] = blocks[level];
            }
        }

        // Render two rows of 50 characters each
        var row1 = new string(chars, 0, 50);
        var row2 = new string(chars, 50, 50);
        sb.AppendLine($"{pad}[grey]Distribution:[/]");
        sb.AppendLine($"{pad}  [green]{Markup.Escape(row1)}[/] [dim]0-49[/]");
        sb.AppendLine($"{pad}  [green]{Markup.Escape(row2)}[/] [dim]50-99[/]");
    }

    private int RebuildComponentHistograms(StringBuilder sb, string componentName, ComponentTable table)
    {
        sb.AppendLine($"  [white]{Markup.Escape(componentName)}[/]");

        if (table.IndexedFieldInfos == null || table.IndexedFieldInfos.Length == 0)
        {
            sb.AppendLine("    [dim](no indexed fields)[/]");
            return 0;
        }

        var count = 0;
        for (var i = 0; i < table.IndexedFieldInfos.Length; i++)
        {
            var info = table.IndexedFieldInfos[i];
            var stats = table.IndexStats[i];
            var fieldName = ResolveFieldName(table, info.OffsetToField);

            var sw = Stopwatch.StartNew();
            stats.RebuildHistogram();
            sw.Stop();

            var multiStr = info.Index.AllowMultiple ? " [yellow]multi[/]" : "";
            if (stats.Histogram != null)
            {
                sb.AppendLine($"    [cyan]{Markup.Escape(fieldName)}[/]{multiStr}  [white]{stats.Histogram.TotalCount:N0} entities[/], range [[{stats.Histogram.MinValue:N0}..{stats.Histogram.MaxValue:N0}]]  [green]{FormatElapsed(sw.Elapsed)}[/]");
            }
            else
            {
                sb.AppendLine($"    [cyan]{Markup.Escape(fieldName)}[/]{multiStr}  [dim](empty)[/]  [green]{FormatElapsed(sw.Elapsed)}[/]");
            }

            count++;
        }

        return count;
    }

    private (IndexStatistics Stats, IBTreeIndex Index, string Error) ResolveIndexStats(string name)

    {
        var dotPos = name.LastIndexOf('.');
        if (dotPos < 0)
        {
            return (null, null, $"Error: Index name must be Component.Field (e.g., CompA.Gold). Got '{name}'.");
        }

        var componentName = name[..dotPos];
        var fieldName = name[(dotPos + 1)..];

        if (!_session.ComponentTypes.TryGetValue(componentName, out var componentType))
        {
            return (null, null, $"Error: Component '{componentName}' not found.");
        }

        var table = _session.Engine.GetComponentTable(componentType);
        if (table == null)
        {
            return (null, null, $"Error: No component table for '{componentName}'.");
        }

        if (table.IndexedFieldInfos == null || table.Definition.FieldsByName == null)
        {
            return (null, null, $"Error: Component '{componentName}' has no indexed fields.");
        }

        if (!table.Definition.FieldsByName.TryGetValue(fieldName, out var field))
        {
            return (null, null, $"Error: Field '{fieldName}' not found on component '{componentName}'.");
        }

        for (var i = 0; i < table.IndexedFieldInfos.Length; i++)
        {
            if (table.IndexedFieldInfos[i].OffsetToField == field.OffsetInComponentStorage)
            {
                return (table.IndexStats[i], table.IndexedFieldInfos[i].Index, null);
            }
        }

        return (null, null, $"Error: Field '{fieldName}' on component '{componentName}' is not indexed.");
    }

    private static string ResolveFieldName(ComponentTable table, int offsetToField)
    {
        if (table.Definition.FieldsByName != null)
        {
            foreach (var kvp in table.Definition.FieldsByName)
            {
                if (kvp.Value.OffsetInComponentStorage == offsetToField)
                {
                    return kvp.Key;
                }
            }
        }

        return $"@offset{offsetToField}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < 1)
        {
            return $"{elapsed.TotalMicroseconds:F0}µs";
        }

        return elapsed.TotalMilliseconds < 1000
            ? $"{elapsed.TotalMilliseconds:F1}ms"
            : $"{elapsed.TotalSeconds:F2}s";
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024 * 1024        => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024               => $"{bytes / 1024.0:F1} KB",
            _                     => $"{bytes} B"
        };
    }

    private static string Pct(int part, int total) => total > 0 ? $"{(double)part / total:P1}" : "0.0%";

    // ═══════════════════════════════════════════════════════════════
    // ECS Diagnostics
    // ═══════════════════════════════════════════════════════════════

    private CommandResult ExecuteListArchetypes()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  [white]Registered Archetypes[/]");
        sb.AppendLine("  [grey]══════════════════════════════════════════════════════════════════[/]");

        var dbe = _session.Engine;
        var states = dbe._archetypeStates;
        int count = 0;

        for (int id = 0; id < states.Length; id++)
        {
            var state = states[id];
            if (state == null)
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)id);
            if (meta == null)
            {
                continue;
            }

            count++;
            long entityCount = state.EntityMap?.EntryCount ?? 0;
            string parentName = meta.ParentArchetypeId == 0xFFFF
                ? "(root)"
                : ArchetypeRegistry.GetMetadata(meta.ParentArchetypeId)?.ArchetypeType?.Name ?? "?";

            sb.AppendLine($"  [white]{Markup.Escape(meta.ArchetypeType?.Name ?? "?")}[/]  " +
                          $"[grey]Id=[/][yellow]{meta.ArchetypeId}[/]  " +
                          $"[grey]Components=[/][yellow]{meta.ComponentCount}[/]  " +
                          $"[grey]Parent=[/][yellow]{Markup.Escape(parentName)}[/]  " +
                          $"[grey]Entities=[/][yellow]{entityCount}[/]");

            // List component slots
            if (state.SlotToComponentTable != null)
            {
                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = state.SlotToComponentTable[slot];
                    string compName = meta._slotToComponentType?[slot]?.Name ?? "?";
                    string mode = table?.StorageMode.ToString() ?? "?";
                    sb.AppendLine($"    [grey]Slot {slot}:[/] [white]{Markup.Escape(compName)}[/] [grey]({mode})[/]");
                }
            }

            // List cascade targets
            if (meta._cascadeTargets != null && meta._cascadeTargets.Count > 0)
            {
                foreach (var target in meta._cascadeTargets)
                {
                    string childName = target.ChildArchetypeType?.Name ?? "?";
                    sb.AppendLine($"    [grey]Cascade →[/] [red]{Markup.Escape(childName)}[/] [grey](FK slot {target.FkSlotIndex})[/]");
                }
            }
        }

        sb.AppendLine($"\n  [grey]Total:[/] [white]{count} archetype(s)[/]");
        return CommandResult.Markup(sb.ToString().TrimEnd());
    }

    private CommandResult ExecuteEntityInfo(List<Token> tokens, int pos)
    {
        if (tokens.Count <= pos)
        {
            return CommandResult.Error("Syntax error: entity-info <entityId>\n  entityId is the raw packed value (EntityKey << 12 | ArchetypeId).");
        }

        if (!long.TryParse(tokens[pos].Value, out long rawId))
        {
            return CommandResult.Error($"Error: '{tokens[pos].Value}' is not a valid integer.");
        }

        var entityId = EntityId.FromRaw(rawId);
        var meta = ArchetypeRegistry.GetMetadata(entityId.ArchetypeId);
        if (meta == null)
        {
            return CommandResult.Error($"Error: ArchetypeId {entityId.ArchetypeId} not registered.");
        }

        var dbe = _session.Engine;
        var engineState = dbe._archetypeStates[meta.ArchetypeId];
        if (engineState?.EntityMap == null)
        {
            return CommandResult.Error($"Error: Archetype {meta.ArchetypeType?.Name} has no EntityMap.");
        }

        // Look up in EntityMap
        int recordSize = meta._entityRecordSize;
        var readBuf = new byte[recordSize];

        var epochManager = dbe.EpochManager;
        var epochDepth = epochManager.EnterScope();
        try
        {
            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            unsafe
            {
                fixed (byte* ptr = readBuf)
                {
                    bool found = engineState.EntityMap.TryGet(entityId.EntityKey, ptr, ref accessor);
                    accessor.Dispose();

                    if (!found)
                    {
                        return CommandResult.Error($"Error: Entity {entityId} not found in EntityMap.");
                    }

                    ref var header = ref EntityRecordAccessor.GetHeader(ptr);
                    var sb = new StringBuilder();
                    sb.AppendLine($"  [white]Entity {entityId}[/]");
                    sb.AppendLine("  [grey]══════════════════════════════════════[/]");
                    sb.AppendLine($"  [grey]Archetype:[/]    [yellow]{Markup.Escape(meta.ArchetypeType?.Name ?? "?")}[/] [grey](Id={meta.ArchetypeId})[/]");
                    sb.AppendLine($"  [grey]EntityKey:[/]    [white]{entityId.EntityKey}[/]");
                    sb.AppendLine($"  [grey]BornTSN:[/]      [white]{header.BornTSN}[/]{(header.BornTSN == 0 ? " [grey](genesis)[/]" : "")}");
                    sb.AppendLine($"  [grey]DiedTSN:[/]      [white]{header.DiedTSN}[/]{(header.DiedTSN == 0 ? " [grey](alive)[/]" : " [red](dead)[/]")}");
                    sb.AppendLine($"  [grey]EnabledBits:[/]  [white]0x{header.EnabledBits:X4}[/] [grey]({Convert.ToString(header.EnabledBits, 2).PadLeft(meta.ComponentCount, '0')})[/]");

                    sb.AppendLine($"  [grey]Locations:[/]");
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        int chunkId = EntityRecordAccessor.GetLocation(ptr, slot);
                        string compName = meta._slotToComponentType?[slot]?.Name ?? "?";
                        bool enabled = (header.EnabledBits & (1 << slot)) != 0;
                        string enabledTag = enabled ? "[green]ON[/]" : "[grey]off[/]";
                        sb.AppendLine($"    [grey]Slot {slot}:[/] [white]{Markup.Escape(compName)}[/] → ChunkId [yellow]{chunkId}[/]  {enabledTag}");
                    }

                    return CommandResult.Markup(sb.ToString().TrimEnd());
                }
            }
        }
        finally
        {
            epochManager.ExitScope(epochDepth);
        }
    }
}
