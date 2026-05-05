using System.Buffers;
using Microsoft.AspNetCore.Mvc;
using Typhon.Profiler;
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
    /// #302 Phase 4: source-location manifest for the session — maps span <c>siteId</c>s to file/line/method.
    /// Works for both Attach (received in init handshake) and Trace (read from the file's trailer) sessions.
    /// Returns an empty manifest when the trace doesn't carry source attribution (engine emitted no
    /// intercepted call sites). See claude/design/observability/10-profiler-source-attribution.md §4.7.
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
}
