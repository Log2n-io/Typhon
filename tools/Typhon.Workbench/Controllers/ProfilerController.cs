using System.Buffers;
using K4os.Compression.LZ4;
using Microsoft.AspNetCore.Mvc;
using Typhon.Profiler;
using Typhon.Profiler.Events;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Session-scoped profiler endpoints. Serves metadata + binary chunk payloads for the Workbench's Trace session mode.
/// Backed by the session's <see cref="TraceSessionRuntime"/>, which owns the sidecar cache reader.
/// </summary>
[ApiController]
[Route("api/sessions/{sessionId:guid}/profiler")]
[Tags("Profiler")]
[RequireBootstrapToken]
[RequireSession]
public sealed class ProfilerController : ControllerBase
{
    /// <summary>
    /// Returns the full metadata DTO once the session is ready. For Trace sessions this means the sidecar cache build
    /// completed; for Attach sessions it means the first Init frame arrived. Returns 202 Accepted with an empty body
    /// while the session is still waiting — clients poll via TanStack Query <c>refetchInterval</c>, or for Trace mode
    /// they can subscribe to <c>GET /api/sessions/{id}/profiler/build-progress</c> for incremental UX, and for Attach
    /// mode <c>GET /api/sessions/{id}/profiler/stream</c> for live updates.
    /// </summary>
    [HttpGet("metadata")]
    public ActionResult<ProfilerMetadataDto> GetMetadata(Guid sessionId)
    {
        var session = HttpContext.Items["Session"];

        if (session is TraceSession trace)
        {
            var runtime = trace.Runtime;
            if (runtime.IsBuildComplete)
            {
                if (runtime.Metadata != null)
                {
                    return Ok(runtime.Metadata);
                }
                return Problem(
                    title: "trace_build_failed",
                    detail: "Trace cache build failed. See server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Build in flight — client should retry (TanStack Query handles this via refetchInterval).
            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted);
        }

        if (session is AttachSession attach)
        {
            if (attach.Runtime.Metadata != null)
            {
                return Ok(attach.Runtime.Metadata);
            }
            // Init frame hasn't arrived yet — client retries.
            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted);
        }

        return Conflict(new ProblemDetails
        {
            Title = "session_kind_mismatch",
            Detail = "Profiler metadata is only available for Trace and Attach sessions.",
            Status = StatusCodes.Status409Conflict,
        });
    }

    /// <summary>
    /// Reports whether the source <c>.typhon-trace</c> behind a Trace session has been overwritten on disk
    /// since the session's sidecar cache was built — e.g. a profiling re-run against the same app regenerated
    /// the file. The Workbench polls this (~3 s) so the profiler header can offer the user an in-place reload.
    /// Detection is debounced + fingerprint-verified server-side; see <see cref="TraceSessionRuntime.NewVersionAvailable"/>.
    /// Always reports <c>false</c> for self-contained <c>.typhon-replay</c> sessions — they have no source file.
    /// </summary>
    [HttpGet("trace-status")]
    public ActionResult<TraceStatusDto> GetTraceStatus(Guid sessionId)
    {
        var session = HttpContext.Items["Session"];
        if (session is TraceSession trace)
        {
            return Ok(new TraceStatusDto(trace.Runtime.NewVersionAvailable));
        }

        return Conflict(new ProblemDetails
        {
            Title = "session_kind_mismatch",
            Detail = "Trace status is only available for Trace sessions.",
            Status = StatusCodes.Status409Conflict,
        });
    }

    /// <summary>
    /// #302 Phase 4: source-location manifest for the session — maps span <c>siteId</c>s to file/line/method.
    /// Works for both Attach (received in init handshake) and Trace (read from the file's trailer) sessions.
    /// Returns an empty manifest when the trace doesn't carry source attribution (engine emitted no
    /// intercepted call sites). See claude/design/Profiler/10-profiler-source-attribution.md §4.7.
    /// </summary>
    [HttpGet("source-locations")]
    public ActionResult<SourceLocationManifestDto> GetSourceLocations(Guid sessionId)
    {
        var session = HttpContext.Items["Session"];

        if (session is AttachSession attach)
        {
            return Ok(attach.Runtime.SourceLocationManifest);
        }

        if (session is TraceSession trace)
        {
            // Manifest was loaded once at build completion in TraceSessionRuntime; we just hand it
            // back. Pre-feature traces (or traces without attribution) get SourceLocationManifestDto.Empty.
            return Ok(trace.Runtime.SourceLocationManifest);
        }

        return Conflict(new ProblemDetails
        {
            Title = "session_kind_mismatch",
            Detail = "Source-location manifest is only available for Trace and Attach sessions.",
            Status = StatusCodes.Status409Conflict,
        });
    }

    /// <summary>
    /// User-initiated disconnect for an Attach session. Drops the TCP connection to the engine and pins the
    /// runtime status to <c>disconnected</c>; the session itself stays alive in the <see cref="SessionManager"/>
    /// so the client can keep inspecting the captured tick buffer. Idempotent — repeated calls are 204 no-ops.
    /// To free the session entirely, the client should call <c>DELETE /api/sessions/{id}</c>.
    /// </summary>
    [HttpPost("disconnect")]
    public IActionResult Disconnect(Guid sessionId)
    {
        var session = HttpContext.Items["Session"];
        if (session is not AttachSession attach)
        {
            return Conflict(new ProblemDetails
            {
                Title = "session_kind_mismatch",
                Detail = "Disconnect is only valid on Attach sessions.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        attach.Runtime.RequestDisconnect();
        return NoContent();
    }

    /// <summary>
    /// Snapshot the current live attach session into a self-contained <c>.typhon-replay</c> file. The output is byte-format-identical to
    /// a <c>.typhon-trace-cache</c> sidecar but includes an embedded <see cref="CacheSectionId.SourceMetadata"/> section + the
    /// <see cref="CacheHeaderFlags.IsSelfContained"/> flag, so the file opens with no companion <c>.typhon-trace</c> required.
    /// </summary>
    /// <remarks>
    /// Available only on Attach sessions. Trace sessions already have on-disk artifacts (the source <c>.typhon-trace</c> + sidecar cache)
    /// so save-as-replay would be redundant. The flow takes a builder lock for the duration of the chunk re-feed + trailer write —
    /// expect record processing to be paused for sub-second-to-few-seconds depending on session size.
    /// </remarks>
    [HttpPost("save-replay")]
    public async Task<ActionResult<SaveReplayResponse>> SaveReplay(
        Guid sessionId,
        [FromBody] SaveReplayRequest request,
        CancellationToken ct)
    {
        var session = HttpContext.Items["Session"];
        if (session is not AttachSession attach)
        {
            return Conflict(new ProblemDetails
            {
                Title = "session_kind_mismatch",
                Detail = "Save Replay is only valid on Attach sessions.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        if (string.IsNullOrWhiteSpace(request?.Path))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "invalid_path",
                Detail = "path is required.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var resolved = System.IO.Path.GetFullPath(request.Path);
        var parent = System.IO.Path.GetDirectoryName(resolved);
        if (string.IsNullOrEmpty(parent) || !System.IO.Directory.Exists(parent))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "parent_directory_missing",
                Detail = $"Parent directory does not exist: {parent}",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        try
        {
            var bytesWritten = await attach.Runtime.SaveSessionAsync(resolved, ct);
            return Ok(new SaveReplayResponse(resolved, bytesWritten));
        }
        catch (InvalidOperationException ex)
        {
            // Init not yet received → 409 (session not ready).
            return Conflict(new ProblemDetails
            {
                Title = "session_not_ready",
                Detail = ex.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            // Read-only directory, ACL denial, etc. The user picked a path they can't write to — surface it
            // as 403 with the OS message rather than a raw 500.
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "save_path_unauthorized",
                Detail = ex.Message,
                Status = StatusCodes.Status403Forbidden,
            });
        }
        catch (IOException ex)
        {
            // Disk full, file locked by another process, transient I/O error. Surface as 400 — re-issuing
            // the request after the user fixes the underlying condition is the recovery path.
            return BadRequest(new ProblemDetails
            {
                Title = "save_io_error",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest,
            });
        }
    }

    /// <summary>
    /// Returns the raw LZ4-compressed bytes of a single chunk. Response headers carry everything the browser worker
    /// needs to decode the payload:
    /// <list type="bullet">
    ///   <item><c>X-Chunk-From-Tick</c> / <c>X-Chunk-To-Tick</c> — chunk's tick range (ToTick exclusive).</item>
    ///   <item><c>X-Chunk-Event-Count</c> — number of records in the chunk.</item>
    ///   <item><c>X-Chunk-Uncompressed-Bytes</c> — decompressed size (needed to size the output buffer).</item>
    ///   <item><c>X-Chunk-Is-Continuation</c> — <c>"1"</c> for mid-tick split chunks, <c>"0"</c> otherwise.</item>
    ///   <item><c>X-Timestamp-Frequency</c> — source Stopwatch frequency (ticks/sec) for µs conversion.</item>
    ///   <item><c>Access-Control-Expose-Headers</c> — lists the above so browsers expose them to JS.</item>
    /// </list>
    /// </summary>
    [HttpGet("chunks/{chunkIdx:int}")]
    public async Task GetChunk(Guid sessionId, int chunkIdx, CancellationToken ct)
    {
        // #289 — both Trace and Attach sessions implement IChunkProvider, so this method handles both modes uniformly.
        var session = HttpContext.Items["Session"];
        IChunkProvider provider = session switch
        {
            TraceSession trace => trace.Runtime,
            AttachSession attach => attach.Runtime,
            _ => null,
        };
        if (provider == null)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (!provider.IsReady)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            await Response.WriteAsync("Runtime not ready — call /profiler/metadata first.", ct);
            return;
        }

        ChunkManifestEntry entry;
        try
        {
            entry = await provider.GetChunkManifestEntryAsync(chunkIdx);
        }
        catch (ArgumentOutOfRangeException)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var isContinuation = (entry.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;

        Response.Headers["X-Chunk-From-Tick"] = entry.FromTick.ToString();
        Response.Headers["X-Chunk-To-Tick"] = entry.ToTick.ToString();
        Response.Headers["X-Chunk-Event-Count"] = entry.EventCount.ToString();
        Response.Headers["X-Chunk-Uncompressed-Bytes"] = entry.UncompressedBytes.ToString();
        Response.Headers["X-Chunk-Is-Continuation"] = isContinuation ? "1" : "0";
        Response.Headers["X-Timestamp-Frequency"] = provider.TimestampFrequency.ToString();
        Response.Headers["Access-Control-Expose-Headers"] = string.Join(", ", new[]
        {
            "X-Chunk-From-Tick",
            "X-Chunk-To-Tick",
            "X-Chunk-Event-Count",
            "X-Chunk-Uncompressed-Bytes",
            "X-Chunk-Is-Continuation",
            "X-Timestamp-Frequency",
        });
        Response.ContentType = "application/octet-stream";
        Response.ContentLength = (int)entry.CacheByteLength;

        var (bytes, length) = await provider.ReadChunkCompressedAsync(chunkIdx);
        try
        {
            await Response.Body.WriteAsync(bytes.AsMemory(0, length), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// JSON projection of a single chunk: server-side LZ4 decompresses the chunk, walks the packed
    /// records via <see cref="TraceEventDecoder.DecodeBlock"/>, and returns a <see cref="DecodedChunkDto"/> with a
    /// fully deserialized event array. Mirrors the binary <see cref="GetChunk"/> endpoint for
    /// callers that don't have the Typhon profiler codec on hand (curl / scripts / quick-look).
    ///
    /// Optional filters scope the response server-side:
    /// <list type="bullet">
    ///   <item><c>?kinds=10,20,21</c> — CSV of <see cref="TraceEventKind"/> integer values; only matching events are returned.</item>
    ///   <item><c>?tick=1768</c> — only events whose <c>TickNumber</c> equals the value (after the decoder's stateful tick derivation).</item>
    /// </list>
    /// Both filters compose with AND. Invalid filter values are silently dropped (a malformed
    /// <c>kinds=</c> token is skipped; a non-integer <c>tick=</c> would 400 via model binding).
    /// The unfiltered chunk is the worst case (~50K events / few-hundred-KB JSON); filtered
    /// payloads are typically a few hundred records at most.
    /// </summary>
    [HttpGet("chunks/{chunkIdx:int}/decoded")]
    public async Task<ActionResult<DecodedChunkDto>> GetChunkDecoded(
        Guid sessionId,
        int chunkIdx,
        [FromQuery] string kinds,
        [FromQuery] int? tick,
        CancellationToken ct)
    {
        var session = HttpContext.Items["Session"];
        IChunkProvider provider = session switch
        {
            TraceSession trace => trace.Runtime,
            AttachSession attach => attach.Runtime,
            _ => null,
        };
        if (provider == null)
        {
            return Conflict();
        }
        if (!provider.IsReady)
        {
            return Conflict("Runtime not ready — call /profiler/metadata first.");
        }

        ChunkManifestEntry entry;
        try
        {
            entry = await provider.GetChunkManifestEntryAsync(chunkIdx);
        }
        catch (ArgumentOutOfRangeException)
        {
            return NotFound();
        }

        var isContinuation = (entry.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;

        // Pull compressed bytes via the same provider path as the binary endpoint, then inflate
        // into a pooled buffer. Both buffers are returned in the finally — the JSON serializer
        // captures the decoded event list before then, so reuse is safe.
        var uncompressedSize = (int)entry.UncompressedBytes;
        var (compressedBytes, compressedLength) = await provider.ReadChunkCompressedAsync(chunkIdx);
        var uncompressedBuffer = ArrayPool<byte>.Shared.Rent(uncompressedSize);
        try
        {
            ct.ThrowIfCancellationRequested();
            var decoded = LZ4Codec.Decode(
                compressedBytes.AsSpan(0, compressedLength),
                uncompressedBuffer.AsSpan(0, uncompressedSize));
            if (decoded != uncompressedSize)
            {
                return Problem(
                    detail: $"LZ4 decode size mismatch for chunk [{entry.FromTick}, {entry.ToTick}): expected {uncompressedSize}, got {decoded}.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Seed the consumer-side tick counter the same way the legacy decoder did. NORMAL chunks
            // begin with a TickStart record, which the walker increments — seed at (FromTick - 1) so
            // that increment lands on FromTick. CONTINUATION chunks lack a leading TickStart (the prior
            // chunk consumed it), so seed directly at FromTick.
            var seedTick = isContinuation ? (int)entry.FromTick : (int)entry.FromTick - 1;
            // Stopwatch frequency is ticks/second; ticks-per-µs = freq / 1e6 (rounded; Decode does
            // double-precision conversion downstream).
            var ticksPerUs = provider.TimestampFrequency / 1_000_000;

            var capacity = entry.EventCount > 0 ? (int)Math.Min(entry.EventCount, int.MaxValue) : 64;
            var allEvents = new List<TraceEventDto>(capacity);
            TraceEventDecoder.DecodeBlock(uncompressedBuffer.AsSpan(0, uncompressedSize), seedTick, ticksPerUs, allEvents);

            var kindsFilter = ParseKindsFilter(kinds);
            IReadOnlyList<TraceEventDto> projected = allEvents;
            if (kindsFilter != null || tick.HasValue)
            {
                projected = ApplyFilters(allEvents, kindsFilter, tick);
            }

            return new DecodedChunkDto(
                FromTick: (int)entry.FromTick,
                ToTick: (int)entry.ToTick,
                EventCount: allEvents.Count,
                UncompressedBytes: uncompressedSize,
                IsContinuation: isContinuation,
                TimestampFrequency: provider.TimestampFrequency,
                FilteredEventCount: projected.Count,
                Events: projected);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedBytes);
            ArrayPool<byte>.Shared.Return(uncompressedBuffer);
        }
    }

    /// <summary>
    /// Parses a comma-separated list of <see cref="TraceEventKind"/> integer values into a HashSet.
    /// Returns <c>null</c> when the parameter is null/empty (i.e. no filtering). Tokens that fail
    /// to parse are silently skipped — a fully-unparseable list returns an empty set, which means
    /// "no events match" rather than "no filter".
    /// </summary>
    private static HashSet<int> ParseKindsFilter(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var set = new HashSet<int>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var k))
            {
                set.Add(k);
            }
        }
        return set;
    }

    private static List<TraceEventDto> ApplyFilters(List<TraceEventDto> source, HashSet<int> kinds, int? tick)
    {
        var result = new List<TraceEventDto>();
        foreach (var ev in source)
        {
            if (kinds != null && !kinds.Contains(ev.KindByte)) continue;
            if (tick.HasValue && ev.TickNumber != tick.Value) continue;
            result.Add(ev);
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════
    // #337 (P4 of #342) — Query Catalog endpoints.
    // Backed by Services.QueryCatalogService (per-session lazy walker over QueryDefinitionDescribe /
    // QueryArgs / QueryPlan / Query{Execute*,Plan,Count} spans). First call triggers a one-pass build;
    // subsequent calls are O(1) on the cached result.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lists all query definitions observed in the trace's catalog. One entry per distinct
    /// <c>(Kind, LocalId)</c> identity emitted via <see cref="TraceEventKind.QueryDefinitionDescribe"/>.
    /// </summary>
    [HttpGet("queries")]
    public async Task<ActionResult<QueryDefinitionDto[]>> GetQueries(Guid sessionId, CancellationToken ct)
    {
        var catalog = ResolveCatalog();
        if (catalog == null)
        {
            return Conflict(new ProblemDetails
            {
                Title = "session_not_ready",
                Detail = "Query catalog is only available after the session metadata is built.",
                Status = StatusCodes.Status409Conflict,
            });
        }
        var defs = await catalog.GetAllDefinitionsAsync(ct);
        return Ok(defs);
    }

    /// <summary>
    /// Returns a single query definition by its <c>(Kind, LocalId)</c> identity. <c>kind</c> is 0 for
    /// View-based queries and 1 for EcsQuery-based queries.
    /// </summary>
    [HttpGet("queries/{kind:int}/{localId:long}")]
    public async Task<ActionResult<QueryDefinitionDto>> GetQuery(Guid sessionId, int kind, long localId, CancellationToken ct)
    {
        if (kind < 0 || kind > 255 || localId < 0 || localId > uint.MaxValue) return BadRequest();
        var catalog = ResolveCatalog();
        if (catalog == null) return Conflict();
        var def = await catalog.GetDefinitionAsync((byte)kind, (uint)localId, ct);
        if (def == null) return NotFound();
        return Ok(def);
    }

    /// <summary>
    /// Lists executions for a given definition, with optional filters (<c>from</c>/<c>to</c> tick range
    /// + <c>system</c> id) and pagination (<c>pageOffset</c>, <c>pageSize</c>; default 100, max 500).
    /// </summary>
    [HttpGet("queries/{kind:int}/{localId:long}/executions")]
    public async Task<ActionResult<QueryExecutionDto[]>> GetQueryExecutions(
        Guid sessionId,
        int kind,
        long localId,
        [FromQuery] long? from,
        [FromQuery] long? to,
        [FromQuery] int? system,
        [FromQuery] int? pageOffset,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        if (kind < 0 || kind > 255 || localId < 0 || localId > uint.MaxValue) return BadRequest();
        var resolvedPageSize = pageSize ?? 100;
        var resolvedPageOffset = pageOffset ?? 0;
        if (resolvedPageSize <= 0 || resolvedPageSize > Services.QueryCatalogService.MaxPageSize)
        {
            return BadRequest(new { error = "pageSize_out_of_range", min = 1, max = Services.QueryCatalogService.MaxPageSize });
        }
        if (resolvedPageOffset < 0)
        {
            return BadRequest(new { error = "pageOffset_out_of_range", min = 0 });
        }

        var catalog = ResolveCatalog();
        if (catalog == null) return Conflict();
        var execs = await catalog.GetExecutionsAsync(
            (byte)kind,
            (uint)localId,
            from,
            to,
            system,
            resolvedPageSize,
            resolvedPageOffset,
            ct);
        return Ok(execs);
    }

    /// <summary>
    /// Returns a single execution by its <see cref="QueryExecutionDto.SpanId"/>. Indexed lookup against
    /// the pre-built per-session map in <see cref="QueryCatalogData.ExecutionsBySpanId"/>.
    /// </summary>
    [HttpGet("executions/{spanId:long}")]
    public async Task<ActionResult<QueryExecutionDto>> GetExecution(Guid sessionId, long spanId, CancellationToken ct)
    {
        var catalog = ResolveCatalog();
        if (catalog == null) return Conflict();
        var exec = await catalog.GetExecutionBySpanIdAsync(spanId, ct);
        if (exec == null) return NotFound();
        return Ok(exec);
    }

    /// <summary>
    /// Returns the query executions whose parent span id matches <paramref name="parentSpanId"/>. The profiler
    /// detail pane uses this to round-trip from a selected <c>Scheduler:System:SingleThreaded</c> span to the
    /// matching per-tick QueryPlan execution(s) — typically one per system tick for a pull-mode view.
    /// Returns an empty array when no executions are parented under the given span id.
    /// </summary>
    [HttpGet("executions/by-parent/{parentSpanId:long}")]
    public async Task<ActionResult<QueryExecutionDto[]>> GetExecutionsByParent(Guid sessionId, long parentSpanId, CancellationToken ct)
    {
        var catalog = ResolveCatalog();
        if (catalog == null) return Conflict();
        var execs = await catalog.GetExecutionsByParentSpanIdAsync(parentSpanId, ct);
        return Ok(execs);
    }

    /// <summary>
    /// Returns the query executions for the given (systemIdx, tickIndex) pair. Multi-threaded scheduler
    /// chunks carry the system index natively; runtime-emitted per-tick QueryPlan spans carry
    /// <c>OwnerSystemIdx</c> on the wire — together they let the profiler detail pane round-trip from a
    /// clicked chunk to its matching execution without relying on parent-span linkage (which is null in
    /// multi-threaded mode worker threads).
    /// </summary>
    [HttpGet("executions/by-system-tick/{systemIdx:int}/{tickIndex:long}")]
    public async Task<ActionResult<QueryExecutionDto[]>> GetExecutionsBySystemTick(Guid sessionId, int systemIdx, long tickIndex, CancellationToken ct)
    {
        var catalog = ResolveCatalog();
        if (catalog == null) return Conflict();
        var execs = await catalog.GetExecutionsBySystemTickAsync(systemIdx, tickIndex, ct);
        return Ok(execs);
    }

    private Services.QueryCatalogService ResolveCatalog()
    {
        var session = HttpContext.Items["Session"];
        return session switch
        {
            TraceSession trace => trace.Runtime.IsReady ? trace.Runtime.QueryCatalog : null,
            AttachSession attach => attach.Runtime.QueryCatalog,
            _ => null,
        };
    }
}
