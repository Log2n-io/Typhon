using System.Buffers;
using K4os.Compression.LZ4;
using Typhon.Profiler;
using Typhon.Profiler.Events;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services;

/// <summary>
/// One-pass trace walker that materializes the Query Catalog data (issue #337 / P4 of #342).
/// </summary>
/// <remarks>
/// <para>
/// The walker iterates every chunk in the source trace, decoding events via the existing
/// <see cref="TraceEventDecoder.DecodeBlock"/> pipeline. While walking it builds four in-memory
/// accumulators:
/// </para>
/// <list type="bullet">
///   <item><b>Definitions</b> — keyed by <c>(Kind, LocalId)</c>; populated from
///   <see cref="QueryDefinitionDescribeEventDto"/> emissions (one per distinct instance per session,
///   producer-side deduped at emission time).</item>
///   <item><b>Span-to-definition</b> — maps a <see cref="QueryPlanEvent"/> span's SpanId to the
///   definition key it carries. Used to attribute trailing child phase spans (Filter / Iterate /
///   Sort / Pagination / Count) to the right execution.</item>
///   <item><b>Executions</b> — one entry per <see cref="QueryPlanEvent"/> span, accumulating its
///   phase chain as child spans arrive.</item>
///   <item><b>Per-definition stats</b> — running wall-time samples + rows-scanned/returned for
///   percentile and selectivity computation at finalize.</item>
/// </list>
/// <para>
/// After the walk, the builder finalizes by computing p50/p95/p99 wall-times from per-definition
/// sample arrays, rolling up owner-system attribution (each execution carries the owning system ID
/// derived from the chunk's running system context), and resolving the source-location string IDs
/// against the trace's <c>QuerySourceStringTable</c>.
/// </para>
/// <para>
/// <b>Performance.</b> Hot path is the chunk-walk loop. Chunk decompression is the dominant cost
/// (LZ4 decode); event dispatch is per-record. The acceptance criterion (<c>&lt;100 ms for 10k
/// executions</c>) is achievable because 10k executions fit in a handful of chunks at typical
/// density. The build is performed lazily on first endpoint access and cached for the session
/// lifetime — repeat requests are O(1).
/// </para>
/// </remarks>
public static class QueryCatalogBuilder
{
    /// <summary>
    /// Walk the trace's chunk stream and materialize the catalog. Returns the fully-resolved DTOs.
    /// Safe to call on a trace that contains no query events — produces an empty result.
    /// </summary>
    /// <param name="provider">Chunk provider for the session (Trace or Attach).</param>
    /// <param name="metadata">Session metadata — used for resolving component-type / system names.</param>
    /// <param name="querySourceStrings">
    /// The trace's <c>QuerySourceStringTable</c>, indexed by ID. Slot 0 is the sentinel
    /// ("no source"). Pass an empty array for traces without source-string data.
    /// </param>
    public static async Task<QueryCatalogData> BuildAsync(
        IChunkProvider provider,
        ProfilerMetadataDto metadata,
        string[] querySourceStrings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(metadata);
        querySourceStrings ??= [];

        var ticksPerUs = provider.TimestampFrequency / 1_000_000;

        var defs = new Dictionary<ulong, DefinitionInProgress>();
        var execs = new List<ExecutionInProgress>();
        var execBySpanId = new Dictionary<long, int>(); // QueryPlan SpanId → index in execs
        var spanToDefKey = new Dictionary<long, ulong>(); // QueryPlan/QueryExecute child SpanId → defKey

        var chunkCount = metadata.ChunkManifest?.Length ?? 0;
        // Reused across chunks (cleared each time) — avoids a per-chunk List allocation + backing-array growth.
        var eventScratch = new List<TraceEventDto>(4096);
        for (var chunkIdx = 0; chunkIdx < chunkCount; chunkIdx++)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessChunkAsync(provider, chunkIdx, metadata, ticksPerUs, defs, execs, execBySpanId, spanToDefKey, eventScratch, ct);
        }

        return Finalize(defs, execs, querySourceStrings);
    }

    /// <summary>
    /// Test-friendly entry point: feed a pre-decoded event sequence (no chunk I/O) and get the finalized
    /// catalog data back. Used by <c>QueryCatalogBuilderTests</c> to exercise the per-event business
    /// logic with synthetic event lists. Production callers go through <see cref="BuildAsync"/> which
    /// drives the same pipeline via real chunk reads.
    /// </summary>
    /// <remarks>Public for test access; not intended as a stable surface for non-test consumers.</remarks>
    public static QueryCatalogData BuildFromEventsForTest(IEnumerable<TraceEventDto> events, string[] querySourceStrings = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        querySourceStrings ??= [];

        var defs = new Dictionary<ulong, DefinitionInProgress>();
        var execs = new List<ExecutionInProgress>();
        var execBySpanId = new Dictionary<long, int>();
        var spanToDefKey = new Dictionary<long, ulong>();

        var list = events as List<TraceEventDto> ?? new List<TraceEventDto>(events);
        ProcessEvents(list, defs, execs, execBySpanId, spanToDefKey);
        return Finalize(defs, execs, querySourceStrings);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Chunk walk
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task ProcessChunkAsync(
        IChunkProvider provider,
        int chunkIdx,
        ProfilerMetadataDto metadata,
        long ticksPerUs,
        Dictionary<ulong, DefinitionInProgress> defs,
        List<ExecutionInProgress> execs,
        Dictionary<long, int> execBySpanId,
        Dictionary<long, ulong> spanToDefKey,
        List<TraceEventDto> eventScratch,
        CancellationToken ct)
    {
        ChunkManifestEntry entry;
        try
        {
            entry = await provider.GetChunkManifestEntryAsync(chunkIdx);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        var isContinuation = (entry.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;
        var uncompressedSize = (int)entry.UncompressedBytes;
        if (uncompressedSize <= 0 || entry.EventCount == 0)
        {
            return;
        }

        var (compressed, compressedLength) = await provider.ReadChunkCompressedAsync(chunkIdx);
        var uncompressed = ArrayPool<byte>.Shared.Rent(uncompressedSize);
        try
        {
            var decoded = LZ4Codec.Decode(compressed.AsSpan(0, compressedLength), uncompressed.AsSpan(0, uncompressedSize));
            if (decoded != uncompressedSize)
            {
                return; // bad chunk — surface as gap, don't crash the build
            }

            var seedTick = isContinuation ? (int)entry.FromTick : (int)entry.FromTick - 1;
            eventScratch.Clear();
            TraceEventDecoder.DecodeBlock(uncompressed.AsSpan(0, uncompressedSize), seedTick, ticksPerUs, eventScratch);

            ProcessEvents(eventScratch, defs, execs, execBySpanId, spanToDefKey);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
            ArrayPool<byte>.Shared.Return(uncompressed);
        }
    }

    private static void ProcessEvents(
        List<TraceEventDto> events,
        Dictionary<ulong, DefinitionInProgress> defs,
        List<ExecutionInProgress> execs,
        Dictionary<long, int> execBySpanId,
        Dictionary<long, ulong> spanToDefKey)
    {
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];

            // 1. QueryDefinitionDescribe — upsert the definition row.
            if (ev is QueryDefinitionDescribeEventDto describe)
            {
                UpsertDefinition(defs, describe);
                continue;
            }

            // 2. QueryArgs — attach to the most recently-seen execution on this thread, if any.
            if (ev is QueryArgsEventDto args)
            {
                AttachArgsToLatestExecution(execs, args);
                continue;
            }

            // 3. Spans — track query-plan correlation.
            if (ev is TraceSpanEventDto span)
            {
                ProcessSpan(span, defs, execs, execBySpanId, spanToDefKey);
                continue;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-event logic
    // ─────────────────────────────────────────────────────────────────────────

    private static void UpsertDefinition(Dictionary<ulong, DefinitionInProgress> defs, QueryDefinitionDescribeEventDto e)
    {
        var key = ((ulong)e.Kind << 32) | e.LocalId;
        if (!defs.ContainsKey(key))
        {
            defs[key] = new DefinitionInProgress
            {
                Kind = e.Kind,
                LocalId = e.LocalId,
                TargetComponentType = e.TargetComponentType,
                PrimaryIndexFieldIdx = e.PrimaryIndexFieldIdx,
                SortFieldIdx = e.SortFieldIdx,
                SortDescending = e.SortDescending,
                DefinitionSourceFileId = e.DefinitionSourceFileId,
                DefinitionSourceLine = e.DefinitionSourceLine,
                DefinitionSourceMethodId = e.DefinitionSourceMethodId,
                Evaluators = e.Evaluators ?? [],
                FieldDependencies = e.FieldDependencies ?? [],
                Stats = new StatsAccumulator(),
                OwnerSystemIds = new HashSet<int>(),
            };
        }
    }

    private static void AttachArgsToLatestExecution(List<ExecutionInProgress> execs, QueryArgsEventDto e)
    {
        // QueryArgs is emitted immediately after the QueryPlan span on the SAME thread, but the chunk
        // decoder produces a time-sorted stream that interleaves events across threads — walking back
        // for "the latest execution with null Args" without matching by thread slot would swap args
        // between concurrently-emitted plans (e.g. A.plan, B.plan, A.args, B.args → A.args lands on
        // B.plan and vice-versa). Match by ThreadSlot to keep attribution correct.
        for (var i = execs.Count - 1; i >= 0; i--)
        {
            if (execs[i].Args == null && execs[i].ThreadSlot == e.ThreadSlot)
            {
                execs[i].Args = e.Thresholds ?? [];
                return;
            }
        }
    }

    private static void ProcessSpan(
        TraceSpanEventDto span,
        Dictionary<ulong, DefinitionInProgress> defs,
        List<ExecutionInProgress> execs,
        Dictionary<long, int> execBySpanId,
        Dictionary<long, ulong> spanToDefKey)
    {
        // The DTO carries SpanId / ParentSpanId as decimal strings — parse to long for keying.
        if (!long.TryParse(span.SpanId, out var spanId)) return;
        long.TryParse(span.ParentSpanId, out var parentSpanId);

        var kind = (TraceEventKind)span.KindByte;

        // System attribution: deferred to a follow-up. Events arrive as complete spans (no Begin/End
        // separation), so a stack-based "current system" doesn't naturally apply here. Surface
        // SystemId = -1 and OwnerSystemIds = []; a follow-up can do a time-range lookup pass.

        // QueryPlan span → start a new execution and record the span-to-definition mapping.
        if (kind == TraceEventKind.QueryPlan)
        {
            var defKey = TryExtractDefinitionKey(span);
            if (defKey.HasValue)
            {
                spanToDefKey[spanId] = defKey.Value;

                // Runtime-emitted per-tick QueryPlan spans carry OwnerSystemIdx (optional, mask 0x20)
                // when emitted from TyphonRuntime.OnSystemEnd. PlanBuilder-emitted spans leave it null.
                // Pattern-match against the typed DTO — when present, use it as the SystemId attribution
                // so the Workbench profiler detail pane can round-trip from a clicked chunk to the
                // matching execution via (systemIdx, tickIndex).
                var systemId = -1;
                if (span is QueryPlanEventDto qp && qp.OwnerSystemIdx is ushort ownerIdx)
                {
                    systemId = ownerIdx;
                }

                var exec = new ExecutionInProgress
                {
                    DefKey = defKey.Value,
                    SpanId = spanId,
                    // Parented span — for runtime-emitted per-tick QueryPlan spans this is the owning
                    // Scheduler:System:SingleThreaded span's SpanId in single-threaded mode. Zero in
                    // multi-threaded mode (worker threads have no enclosing Typhon span at SystemEnd);
                    // use SystemId + TickIndex as the fallback round-trip key in that case.
                    ParentSpanId = parentSpanId,
                    SystemId = systemId,
                    ThreadSlot = span.ThreadSlot,
                    TickNumber = span.TickNumber,
                    StartTs = span.TimestampUs,
                    DurationUs = span.DurationUs,
                    Phases = new List<QueryExecutionPhaseDto>(8),
                };
                execBySpanId[spanId] = execs.Count;
                execs.Add(exec);

                // Update per-definition stats with this execution's wall-time.
                if (defs.TryGetValue(defKey.Value, out var def))
                {
                    var wallNs = (long)(span.DurationUs * 1000.0);
                    def.Stats.Record(wallNs);
                }
            }
        }
        else if (IsQueryChildPhaseKind(kind) && parentSpanId != 0 && execBySpanId.TryGetValue(parentSpanId, out var execIdx))
        {
            // Child phase of a tracked query execution — append to the execution's Phases list.
            var phase = BuildPhaseDto(kind, span);
            execs[execIdx].Phases.Add(phase);

            // Capture rows-scanned / rows-returned for selectivity.
            UpdateRowsForExecution(execs[execIdx], span);
        }
    }

    private static ulong? TryExtractDefinitionKey(TraceSpanEventDto span)
    {
        // QueryPlanEvent carries the optional QueryInstanceKind + QueryInstanceLocalId fields (v9).
        // Typed pattern match against the concrete DTO — no reflection, no boxing.
        if (span is not QueryPlanEventDto qp) return null;
        if (qp.QueryInstanceKind is not byte kind || qp.QueryInstanceLocalId is not uint localId) return null;
        if (kind == 0 && localId == 0) return null; // unset (v8) — skip
        return ((ulong)kind << 32) | localId;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Child phase classification
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsQueryChildPhaseKind(TraceEventKind kind) => kind switch
    {
        TraceEventKind.QueryParse => true,
        TraceEventKind.QueryParseDnf => true,
        TraceEventKind.QueryEstimate => true,
        TraceEventKind.QueryPlanSort => true,
        TraceEventKind.QueryExecuteIndexScan => true,
        TraceEventKind.QueryExecuteIterate => true,
        TraceEventKind.QueryExecuteFilter => true,
        TraceEventKind.QueryExecutePagination => true,
        TraceEventKind.QueryCount => true,
        TraceEventKind.EcsQueryExecute => true,
        TraceEventKind.EcsQueryCount => true,
        TraceEventKind.EcsQueryAny => true,
        TraceEventKind.EcsQuerySubtreeExpand => true,
        _ => false,
    };

    // Per-phase enum→name cache: BuildPhaseDto runs once per phase (~50k for a busy trace) and Enum.ToString does a value
    // lookup on each call. Precomputing the 256 byte-enum names turns it into a single array index (no lookup, no alloc).
    private static readonly string[] _kindNames = BuildKindNames();

    private static string[] BuildKindNames()
    {
        var names = new string[256];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = ((TraceEventKind)i).ToString();
        }
        return names;
    }

    /// <summary>
    /// Build the per-phase row for the Workbench Execution Inspector. Typed pattern matching against
    /// the concrete DTO from the source generator — no reflection, no boxing. Each branch reads only
    /// the fields documented in <see cref="QueryEcsViewEvents"/>.
    /// </summary>
    private static QueryExecutionPhaseDto BuildPhaseDto(TraceEventKind kind, TraceSpanEventDto span)
    {
        var wallNs = (long)(span.DurationUs * 1000.0);
        long? estimate = null;
        long? actual = null;
        var notes = string.Empty;

        switch (span)
        {
            case QueryEstimateEventDto e:
                actual = e.Cardinality;
                notes = $"field={e.FieldIdx}";
                break;
            case QueryPlanSortEventDto e:
                actual = e.SortNs;
                break;
            case QueryExecuteIndexScanEventDto e:
                notes = $"primary={e.PrimaryFieldIdx}";
                break;
            case QueryExecuteIterateEventDto e:
                actual = e.EntryCount;
                break;
            case QueryExecuteFilterEventDto e:
                actual = e.RejectedCount;
                break;
            case QueryExecutePaginationEventDto e:
                notes = e.EarlyTerm == 1 ? "early_term=true" : string.Empty;
                break;
            case QueryCountEventDto e:
                actual = e.ResultCount;
                break;
            case EcsQueryExecuteEventDto e:
                actual = e.ResultCount;
                notes = $"archetype={e.ArchetypeTypeId}";
                break;
            case EcsQueryCountEventDto e:
                actual = e.ResultCount;
                notes = $"archetype={e.ArchetypeTypeId}";
                break;
            case EcsQueryAnyEventDto e:
                notes = $"found={e.Found}";
                break;
            case EcsQuerySubtreeExpandEventDto e:
                actual = e.SubtreeCount;
                notes = $"root={e.RootId}";
                break;
        }

        return new QueryExecutionPhaseDto(_kindNames[(byte)kind], estimate, actual, wallNs, notes);
    }

    private static void UpdateRowsForExecution(ExecutionInProgress exec, TraceSpanEventDto span)
    {
        switch (span)
        {
            case QueryExecuteIterateEventDto e:
                exec.RowsScanned += e.EntryCount;
                break;
            case QueryCountEventDto e:
                // Accumulate; the polymorphic ECS case can emit multiple Count spans per execution.
                exec.RowsReturned += e.ResultCount;
                break;
            case EcsQueryExecuteEventDto e:
                exec.RowsReturned += e.ResultCount ?? 0;
                break;
            case EcsQueryCountEventDto e:
                exec.RowsReturned += e.ResultCount ?? 0;
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Finalization — percentiles, name resolution, DTO projection
    // ─────────────────────────────────────────────────────────────────────────

    private static QueryCatalogData Finalize(
        Dictionary<ulong, DefinitionInProgress> defs,
        List<ExecutionInProgress> execs,
        string[] querySourceStrings)
    {
        // Build a component-type-id → name lookup for FieldEvaluatorShapeDto resolution.
        // The trace's component-type table stores ComponentTypeId → Name; for FieldEvaluator field
        // names we'd need a finer-grained field-name table — which doesn't exist yet (fields are
        // referenced by ordinal). For now, surface field names as "Field[idx]" and rely on the
        // Workbench to render that until a richer field-name source lands.
        var defDtos = new List<QueryDefinitionDto>(defs.Count);
        foreach (var (_, def) in defs)
        {
            var evals = new FieldEvaluatorShapeDto[def.Evaluators.Length];
            for (var i = 0; i < def.Evaluators.Length; i++)
            {
                var ev = def.Evaluators[i];
                evals[i] = new FieldEvaluatorShapeDto(
                    ev.FieldIdx,
                    $"Field[{ev.FieldIdx}]",
                    ev.Op,
                    OpDisplay(ev.Op));
            }

            var aggregate = def.Stats.Finalize(def.RowsScannedTotal, def.RowsReturnedTotal);
            var source = ResolveSourceLocation(querySourceStrings, def.DefinitionSourceFileId, def.DefinitionSourceLine, def.DefinitionSourceMethodId);

            defDtos.Add(new QueryDefinitionDto(
                InstanceId: new QueryInstanceIdDto(def.Kind, def.LocalId),
                TargetComponentType: def.TargetComponentType,
                PrimaryIndexFieldIdx: def.PrimaryIndexFieldIdx,
                SortFieldIdx: def.SortFieldIdx,
                SortDescending: def.SortDescending,
                Evaluators: evals,
                FieldDependencies: def.FieldDependencies,
                OwnerSystemIds: def.OwnerSystemIds.OrderBy(x => x).ToArray(),
                Aggregate: aggregate,
                UserSource: source));
        }

        // Project executions, rolling per-execution rows-scanned/returned back into per-definition totals
        // for the next aggregate Finalize. Two-pass because the first pass walks all spans and we don't
        // know each execution's scanned/returned until its child phases land.
        var execDtos = new List<QueryExecutionDto>(execs.Count);
        foreach (var exec in execs)
        {
            if (defs.TryGetValue(exec.DefKey, out var def))
            {
                def.RowsScannedTotal += exec.RowsScanned;
                def.RowsReturnedTotal += exec.RowsReturned;
            }

            var endTs = (long)((exec.StartTs + exec.DurationUs) * 1000.0);
            execDtos.Add(new QueryExecutionDto(
                DefinitionId: new QueryInstanceIdDto((byte)(exec.DefKey >> 32), (uint)(exec.DefKey & 0xFFFFFFFFu)),
                SpanId: exec.SpanId,
                ParentSpanId: exec.ParentSpanId,
                TickIndex: exec.TickNumber,
                SystemId: exec.SystemId,
                StartTs: (long)(exec.StartTs * 1000.0),
                EndTs: endTs,
                Args: exec.Args ?? [],
                Phases: exec.Phases?.ToArray() ?? []));
        }

        // Backfill OwnerSystemIds on definitions from the runtime-attributed SystemId now present on
        // each execution (OwnerSystemIdx on the QueryPlanEvent wire format, populated by
        // TyphonRuntime.OnSystemEnd). Definitions don't carry owner attribution at descriptor time —
        // the descriptor is emitted once per (kind, localId) per session, before per-tick consumption
        // attributes a specific system. Aggregating systems across executions gives the Workbench
        // catalog the System dropdown filter and the System DAG "Queries" badge data.
        var ownersByDefKey = new Dictionary<ulong, SortedSet<int>>(execDtos.Count);
        foreach (var exec in execDtos)
        {
            if (exec.SystemId < 0) continue;
            var key = ((ulong)exec.DefinitionId.Kind << 32) | exec.DefinitionId.LocalId;
            if (!ownersByDefKey.TryGetValue(key, out var set))
            {
                set = new SortedSet<int>();
                ownersByDefKey[key] = set;
            }
            set.Add(exec.SystemId);
        }

        // Re-finalize aggregates now that RowsScanned/Returned totals are correct, and merge in the
        // execution-derived owner set when the descriptor's OwnerSystemIds was empty.
        for (var i = 0; i < defDtos.Count; i++)
        {
            var dto = defDtos[i];
            var defKey = ((ulong)dto.InstanceId.Kind << 32) | dto.InstanceId.LocalId;
            if (defs.TryGetValue(defKey, out var def))
            {
                var refreshed = def.Stats.Finalize(def.RowsScannedTotal, def.RowsReturnedTotal);
                var owners = dto.OwnerSystemIds;
                if ((owners == null || owners.Count == 0) && ownersByDefKey.TryGetValue(defKey, out var derived))
                {
                    owners = derived.ToArray();
                }
                defDtos[i] = dto with { Aggregate = refreshed, OwnerSystemIds = owners };
            }
        }

        // Build endpoint-lookup indexes once, here, instead of paying per-request linear scans.
        var defsByKey = new Dictionary<ulong, QueryDefinitionDto>(defDtos.Count);
        foreach (var d in defDtos)
        {
            defsByKey[((ulong)d.InstanceId.Kind << 32) | d.InstanceId.LocalId] = d;
        }

        var execDtoArray = execDtos.ToArray();
        var execsByDef = new Dictionary<ulong, List<QueryExecutionDto>>(defDtos.Count);
        var execsBySpan = new Dictionary<long, QueryExecutionDto>(execDtoArray.Length);
        var execsByParent = new Dictionary<long, List<QueryExecutionDto>>(execDtoArray.Length);
        foreach (var e in execDtoArray)
        {
            var key = ((ulong)e.DefinitionId.Kind << 32) | e.DefinitionId.LocalId;
            if (!execsByDef.TryGetValue(key, out var list))
            {
                list = new List<QueryExecutionDto>(8);
                execsByDef[key] = list;
            }
            list.Add(e);
            if (e.SpanId != 0)
            {
                execsBySpan[e.SpanId] = e;
            }
            // Parent-span bucket lets the profiler detail pane round-trip from a selected system span
            // to its corresponding query executions. Multi-value because a single system span can
            // bracket several views (one definition per consumed view), each emitted at OnSystemEnd.
            if (e.ParentSpanId != 0)
            {
                if (!execsByParent.TryGetValue(e.ParentSpanId, out var parentList))
                {
                    parentList = new List<QueryExecutionDto>(2);
                    execsByParent[e.ParentSpanId] = parentList;
                }
                parentList.Add(e);
            }
        }

        var execsByDefFinal = new Dictionary<ulong, QueryExecutionDto[]>(execsByDef.Count);
        foreach (var (k, v) in execsByDef)
        {
            execsByDefFinal[k] = v.ToArray();
        }
        var execsByParentFinal = new Dictionary<long, QueryExecutionDto[]>(execsByParent.Count);
        foreach (var (k, v) in execsByParent)
        {
            execsByParentFinal[k] = v.ToArray();
        }

        return new QueryCatalogData(defDtos.ToArray(), execDtoArray, defsByKey, execsByDefFinal, execsBySpan, execsByParentFinal);
    }

    private static QuerySourceLocationDto ResolveSourceLocation(string[] strings, ushort fileId, int line, ushort methodId)
    {
        // ID 0 is the "no source" sentinel.
        if (fileId == 0 && methodId == 0 && line == 0)
        {
            return new QuerySourceLocationDto(string.Empty, 0, string.Empty);
        }
        var file = fileId > 0 && fileId < strings.Length ? strings[fileId] : string.Empty;
        var method = methodId > 0 && methodId < strings.Length ? strings[methodId] : string.Empty;
        return new QuerySourceLocationDto(file ?? string.Empty, line, method ?? string.Empty);
    }

    private static string OpDisplay(byte op) => op switch
    {
        0 => "==",
        1 => "<",
        2 => "<=",
        3 => ">",
        4 => ">=",
        5 => "!=",
        _ => $"op{op}",
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Internal accumulator types
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class DefinitionInProgress
    {
        public byte Kind;
        public uint LocalId;
        public ushort TargetComponentType;
        public short PrimaryIndexFieldIdx;
        public short SortFieldIdx;
        public bool SortDescending;
        public ushort DefinitionSourceFileId;
        public int DefinitionSourceLine;
        public ushort DefinitionSourceMethodId;
        public QueryFieldEvaluatorShapeDto[] Evaluators;
        public ushort[] FieldDependencies;
        public StatsAccumulator Stats;
        public HashSet<int> OwnerSystemIds;
        public long RowsScannedTotal;
        public long RowsReturnedTotal;
    }

    private sealed class ExecutionInProgress
    {
        public ulong DefKey;
        public long SpanId;
        public long ParentSpanId;
        public int SystemId;
        public byte ThreadSlot;
        public long TickNumber;
        public double StartTs;
        public double DurationUs;
        public long[] Args;
        public long RowsScanned;
        public long RowsReturned;
        public List<QueryExecutionPhaseDto> Phases;
    }
}

/// <summary>
/// Output of <see cref="QueryCatalogBuilder.BuildAsync"/>. Held by <see cref="QueryCatalogService"/> for the session lifetime.
/// Pre-bucketed indexes keep all endpoint lookups O(1) on the catalog side — only the page-slice and per-row predicate cost remains.
/// </summary>
public sealed record QueryCatalogData(
    QueryDefinitionDto[] Definitions,
    QueryExecutionDto[] Executions,
    IReadOnlyDictionary<ulong, QueryDefinitionDto> DefinitionsByKey,
    IReadOnlyDictionary<ulong, QueryExecutionDto[]> ExecutionsByDefKey,
    IReadOnlyDictionary<long, QueryExecutionDto> ExecutionsBySpanId,
    IReadOnlyDictionary<long, QueryExecutionDto[]> ExecutionsByParentSpanId);
