using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Services;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Session-scoped Data API v1 endpoints. Serves topology, track schemas, track data slices, and
/// aggregation results. Backed by <see cref="ProfilerMetadataDto"/> on the session's runtime.
/// </summary>
[ApiController]
[Route("api/sessions/{sessionId:guid}")]
[Tags("Data")]
[RequireBootstrapToken]
[RequireSession]
[RequireApiVersion]
public sealed class DataController : ControllerBase
{
    private readonly StreamSubscriptionRegistry _subscriptionRegistry;

    public DataController(StreamSubscriptionRegistry subscriptionRegistry)
    {
        _subscriptionRegistry = subscriptionRegistry;
    }

    // Static track schema — constructed once, returned on every /tracks request.
    private static readonly TracksResponseDto _tracksSchema = new(
    [
        new TrackSchemaDto("tick/summary", "perTick",
        [
            new TrackFieldDescriptorDto("tickNumber",           "u32"),
            new TrackFieldDescriptorDto("startUs",              "f64"),
            new TrackFieldDescriptorDto("durationUs",           "f32"),
            new TrackFieldDescriptorDto("eventCount",           "u32"),
            new TrackFieldDescriptorDto("maxSystemDurationUs",  "f32"),
            new TrackFieldDescriptorDto("overloadLevel",        "u8"),
            new TrackFieldDescriptorDto("tickMultiplier",       "u8"),
            new TrackFieldDescriptorDto("consecutiveOverrun",   "u16"),
            new TrackFieldDescriptorDto("consecutiveUnderrun",  "u16"),
        ]),
        new TrackSchemaDto("metronome/wait", "perTick",
        [
            new TrackFieldDescriptorDto("tickNumber",  "u32"),
            new TrackFieldDescriptorDto("waitUs",      "u16"),
            new TrackFieldDescriptorDto("intentClass", "u8"),
        ]),
        // ── v2 tracks (#311) ─────────────────────────────────────────────────
        // Per-system tracks: one logical track per system, addressed as `system/<name>`. The schema is identical for
        // every system; the client substitutes the name when constructing the URL.
        new TrackSchemaDto("system/<name>", "perTickPerSystem",
        [
            new TrackFieldDescriptorDto("tickNumber",        "u32"),
            new TrackFieldDescriptorDto("startUs",           "f64"),
            new TrackFieldDescriptorDto("endUs",             "f64"),
            new TrackFieldDescriptorDto("readyUs",           "f64"),
            new TrackFieldDescriptorDto("durationUs",        "f32"),
            new TrackFieldDescriptorDto("entitiesProcessed", "u32"),
            new TrackFieldDescriptorDto("workersTouched",    "u8"),
            new TrackFieldDescriptorDto("chunksProcessed",   "u16"),
            new TrackFieldDescriptorDto("skipReason",        "u8"),
            new TrackFieldDescriptorDto("totalCpuUs",        "u32"),
        ]),
        // Per-queue tracks: one logical track per event queue, addressed as `queue/<name>`.
        new TrackSchemaDto("queue/<name>", "perTickPerQueue",
        [
            new TrackFieldDescriptorDto("tickNumber",     "u32"),
            new TrackFieldDescriptorDto("peakDepth",      "u32"),
            new TrackFieldDescriptorDto("endOfTickDepth", "u32"),
            new TrackFieldDescriptorDto("overflowCount",  "u32"),
            new TrackFieldDescriptorDto("produced",       "u32"),
            new TrackFieldDescriptorDto("consumed",       "u32"),
        ]),
        // Post-tick tracks: per-tick scalar duration for one of the named post-tick phases. Track id family:
        // posttick/walFlush, posttick/writeTickFence, posttick/tierBudget, posttick/subscriptionOutput,
        // posttick/tierIndexRebuild, posttick/dormancySweep.
        new TrackSchemaDto("posttick/<phase>", "perTick",
        [
            new TrackFieldDescriptorDto("tickNumber", "u32"),
            new TrackFieldDescriptorDto("durationUs", "f32"),
        ]),
    ]);

    // ──────────────────────────────────────────────────────────────────────────
    // 4a. Topology
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the ECS topology snapshot — systems, archetypes, and component types — once the session
    /// has finished loading. Returns 202 Accepted while metadata is still in flight; 409 Conflict for
    /// session kinds that carry no topology.
    /// </summary>
    [HttpGet("topology")]
    public ActionResult<TopologyDto> GetTopology(Guid sessionId)
    {
        var metadata = ResolveMetadata(out var mismatch);
        if (mismatch != null)
        {
            return mismatch;
        }

        if (metadata == null)
        {
            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted);
        }

        return Ok(new TopologyDto(metadata.Systems, metadata.Archetypes, metadata.ComponentTypes, metadata.Phases));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4a-bis. Topology queries (RFC 07 surfacing — #275 mvp)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the systems that write the named component (any of <c>Writes</c> + <c>SideWrites</c>). O(systems × declarations).
    /// Component name is matched against <see cref="SystemDefinitionDto"/> arrays exactly — typically a CLR <c>FullName</c>.
    /// </summary>
    [HttpGet("queries/who-writes/{component}")]
    public ActionResult<SystemListDto> GetWhoWrites(Guid sessionId, string component)
    {
        var metadata = ResolveMetadata(out var mismatch);
        if (mismatch != null) return mismatch;
        if (metadata == null) { Response.Headers["Retry-After"] = "1"; return StatusCode(StatusCodes.Status202Accepted); }

        var matches = new List<SystemDefinitionDto>();
        foreach (var s in metadata.Systems)
        {
            if (Array.IndexOf(s.Writes, component) >= 0 || Array.IndexOf(s.SideWrites, component) >= 0)
            {
                matches.Add(s);
            }
        }
        return Ok(new SystemListDto(component, matches.ToArray()));
    }

    /// <summary>
    /// Returns the systems that read the named component (any of <c>Reads</c> + <c>ReadsFresh</c> + <c>ReadsSnapshot</c> +
    /// <c>AdditionalReads</c>). O(systems × declarations).
    /// </summary>
    [HttpGet("queries/who-reads/{component}")]
    public ActionResult<SystemListDto> GetWhoReads(Guid sessionId, string component)
    {
        var metadata = ResolveMetadata(out var mismatch);
        if (mismatch != null) return mismatch;
        if (metadata == null) { Response.Headers["Retry-After"] = "1"; return StatusCode(StatusCodes.Status202Accepted); }

        var matches = new List<SystemDefinitionDto>();
        foreach (var s in metadata.Systems)
        {
            if (Array.IndexOf(s.Reads, component) >= 0
                || Array.IndexOf(s.ReadsFresh, component) >= 0
                || Array.IndexOf(s.ReadsSnapshot, component) >= 0
                || Array.IndexOf(s.AdditionalReads, component) >= 0)
            {
                matches.Add(s);
            }
        }
        return Ok(new SystemListDto(component, matches.ToArray()));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4b. Track discovery
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the static v1 track schema — the list of available tracks and their field descriptors.
    /// Requires the session to be ready (same 202/409 guards as <see cref="GetTopology"/>).
    /// </summary>
    [HttpGet("tracks")]
    public ActionResult<TracksResponseDto> GetTracks(Guid sessionId)
    {
        var metadata = ResolveMetadata(out var mismatch);
        if (mismatch != null)
        {
            return mismatch;
        }

        if (metadata == null)
        {
            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted);
        }

        return Ok(_tracksSchema);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4c. Track data
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a slice of per-tick records for the requested track. <paramref name="from"/> and
    /// <paramref name="to"/> are inclusive tick-number bounds; omitting <paramref name="to"/> returns
    /// all ticks from <paramref name="from"/> onward.
    /// </summary>
    [HttpGet("track/{**trackId}")]
    public ActionResult<TrackDataResponseDto> GetTrackData(
        Guid sessionId,
        string trackId,
        [FromQuery] uint from = 0,
        [FromQuery] uint to = uint.MaxValue)
    {
        if (trackId != "tick/summary" && trackId != "metronome/wait"
            && !trackId.StartsWith("system/", StringComparison.Ordinal)
            && !trackId.StartsWith("queue/", StringComparison.Ordinal)
            && !trackId.StartsWith("posttick/", StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "unknown-track",
                Detail = $"Unknown track: '{trackId}'. Available tracks: tick/summary, metronome/wait, system/<name>, queue/<name>, posttick/*.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (from > to)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "bad-range",
                Detail = "from must be <= to.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var metadata = ResolveMetadata(out var mismatch);
        if (mismatch != null)
        {
            return mismatch;
        }

        if (metadata == null)
        {
            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted);
        }

        // v12 (#311): dispatch to the new track families before the v1 record layout assumes shape.
        if (trackId.StartsWith("system/", StringComparison.Ordinal))
        {
            return GetSystemTrackData(metadata, trackId, from, to);
        }
        if (trackId.StartsWith("queue/", StringComparison.Ordinal))
        {
            return GetQueueTrackData(metadata, trackId, from, to);
        }
        if (trackId.StartsWith("posttick/", StringComparison.Ordinal))
        {
            return GetPostTickTrackData(metadata, trackId, from, to);
        }

        var ticks = metadata.TickSummaries;
        object[] records;

        if (trackId == "tick/summary")
        {
            // Pass 1: count matching ticks so we can pre-size the array and avoid a List + copy.
            var count = 0;
            for (var i = 0; i < ticks.Length; i++)
            {
                var n = ticks[i].TickNumber;
                if (n >= from && n <= to) { count++; }
                else if (n > to) { break; }
            }

            var typed = new TickSummaryRecordDto[count];
            var idx = 0;
            for (var i = 0; i < ticks.Length && idx < count; i++)
            {
                var t = ticks[i];
                if (t.TickNumber >= from && t.TickNumber <= to)
                {
                    typed[idx++] = new TickSummaryRecordDto(
                        t.TickNumber, t.StartUs, t.DurationUs, t.EventCount,
                        t.MaxSystemDurationUs, t.OverloadLevel, t.TickMultiplier,
                        t.ConsecutiveOverrun, t.ConsecutiveUnderrun);
                }
            }

            records = typed; // TickSummaryRecordDto is a reference type — covariant cast, no copy.
        }
        else
        {
            // metronome/wait — same two-pass pattern.
            var count = 0;
            for (var i = 0; i < ticks.Length; i++)
            {
                var n = ticks[i].TickNumber;
                if (n >= from && n <= to) { count++; }
                else if (n > to) { break; }
            }

            var typed = new MetronomeWaitRecordDto[count];
            var idx = 0;
            for (var i = 0; i < ticks.Length && idx < count; i++)
            {
                var t = ticks[i];
                if (t.TickNumber >= from && t.TickNumber <= to)
                {
                    typed[idx++] = new MetronomeWaitRecordDto(t.TickNumber, t.MetronomeWaitUs, t.MetronomeIntentClass);
                }
            }

            records = typed; // MetronomeWaitRecordDto is a reference type — covariant cast, no copy.
        }

        return Ok(new TrackDataResponseDto(trackId, records));
    }

    // ── v12 track family handlers (#311) ─────────────────────────────────────

    private ActionResult<TrackDataResponseDto> GetSystemTrackData(ProfilerMetadataDto metadata, string trackId, uint from, uint to)
    {
        var systemName = trackId["system/".Length..];
        ushort? sysIdx = null;
        for (var i = 0; i < metadata.Systems.Length; i++)
        {
            if (metadata.Systems[i].Name == systemName) { sysIdx = metadata.Systems[i].Index; break; }
        }
        if (sysIdx == null)
        {
            return NotFound(new ProblemDetails { Title = "unknown-system", Detail = $"No system named '{systemName}' in topology.", Status = StatusCodes.Status404NotFound });
        }
        var rows = metadata.SystemTickSummaries;
        var output = new List<SystemTickRecordDto>();
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r.SystemIndex != sysIdx.Value) continue;
            if (r.TickNumber < from || r.TickNumber > to) continue;
            output.Add(new SystemTickRecordDto(r.TickNumber, r.StartUs, r.EndUs, r.ReadyUs, r.DurationUs,
                r.EntitiesProcessed, r.WorkersTouched, r.ChunksProcessed, r.SkipReasonCode, r.TotalCpuUs));
        }
        return Ok(new TrackDataResponseDto(trackId, output.ToArray()));
    }

    private ActionResult<TrackDataResponseDto> GetQueueTrackData(ProfilerMetadataDto metadata, string trackId, uint from, uint to)
    {
        var queueName = trackId["queue/".Length..];
        ushort? qid = null;
        foreach (var (id, name) in metadata.QueueIdToName)
        {
            if (name == queueName) { qid = id; break; }
        }
        if (qid == null)
        {
            return NotFound(new ProblemDetails { Title = "unknown-queue", Detail = $"No queue named '{queueName}' in topology.", Status = StatusCodes.Status404NotFound });
        }
        var rows = metadata.QueueTickSummaries;
        var output = new List<QueueTickRecordDto>();
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r.QueueId != qid.Value) continue;
            if (r.TickNumber < from || r.TickNumber > to) continue;
            output.Add(new QueueTickRecordDto(r.TickNumber, r.PeakDepth, r.EndOfTickDepth, r.OverflowCount, r.Produced, r.Consumed));
        }
        return Ok(new TrackDataResponseDto(trackId, output.ToArray()));
    }

    private ActionResult<TrackDataResponseDto> GetPostTickTrackData(ProfilerMetadataDto metadata, string trackId, uint from, uint to)
    {
        var phase = trackId["posttick/".Length..];
        var rows = metadata.PostTickSummaries;
        var output = new List<PostTickRecordDto>();
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r.TickNumber < from || r.TickNumber > to) continue;
            var us = phase switch
            {
                "walFlush" => r.WalFlushUs,
                "writeTickFence" => r.WriteTickFenceUs,
                "tierBudget" => r.TierBudgetUs,
                "subscriptionOutput" => r.SubscriptionOutputUs,
                "tierIndexRebuild" => r.TierIndexRebuildUs,
                "dormancySweep" => r.DormancySweepUs,
                _ => float.NaN,
            };
            if (float.IsNaN(us))
            {
                return BadRequest(new ProblemDetails { Title = "unknown-posttick-phase", Detail = $"Unknown post-tick phase '{phase}'. Available: walFlush, writeTickFence, tierBudget, subscriptionOutput, tierIndexRebuild, dormancySweep.", Status = StatusCodes.Status400BadRequest });
            }
            output.Add(new PostTickRecordDto(r.TickNumber, us));
        }
        return Ok(new TrackDataResponseDto(trackId, output.ToArray()));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4d. Aggregations
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates one or more aggregation queries (mean, min, max, sum, percentiles, …) over the session's
    /// tick summaries and returns one result per query. Computation is delegated to
    /// <see cref="AggregationService.Compute"/>; invalid queries surface as 400 via the global exception handler.
    /// </summary>
    [HttpPost("aggregate")]
    public ActionResult<AggregationResponseDto> Aggregate(
        Guid sessionId,
        [FromBody] AggregationRequestDto request)
    {
        if (request == null || request.Queries == null || request.Queries.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "invalid_request",
                Detail = "queries must be a non-empty array.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var metadata = ResolveMetadata(out var mismatch);
        if (mismatch != null)
        {
            return mismatch;
        }

        if (metadata == null)
        {
            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted);
        }

        var results = AggregationService.Compute(metadata, request.Queries);
        return Ok(new AggregationResponseDto(results));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4e. Stream subscription management (#308 Phase C)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds <paramref name="request.Events"/> to the subscription set for the unified data stream
    /// connection identified by <paramref name="request.StreamId"/>. The streamId comes from the
    /// <c>stream-id</c> SSE event emitted on connect to <c>GET /api/sessions/{id}/stream</c>.
    /// </summary>
    /// <returns>
    /// 204 NoContent on success. 400 if the body is missing / malformed. 404 if the streamId is
    /// unknown — the connection has likely already disconnected.
    /// </returns>
    [HttpPost("subscribe")]
    public ActionResult Subscribe(Guid sessionId, [FromBody] StreamSubscriptionRequestDto request)
    {
        var validation = ValidateSubscriptionRequest(request);
        if (validation != null)
        {
            return validation;
        }
        if (!_subscriptionRegistry.Subscribe(request.StreamId, request.Events))
        {
            return UnknownStreamIdResult(request.StreamId);
        }
        return NoContent();
    }

    /// <summary>
    /// Removes <paramref name="request.Events"/> from the subscription set. Mirror of
    /// <see cref="Subscribe"/>; same error semantics.
    /// </summary>
    [HttpPost("unsubscribe")]
    public ActionResult Unsubscribe(Guid sessionId, [FromBody] StreamSubscriptionRequestDto request)
    {
        var validation = ValidateSubscriptionRequest(request);
        if (validation != null)
        {
            return validation;
        }
        if (!_subscriptionRegistry.Unsubscribe(request.StreamId, request.Events))
        {
            return UnknownStreamIdResult(request.StreamId);
        }
        return NoContent();
    }

    private ActionResult ValidateSubscriptionRequest(StreamSubscriptionRequestDto request)
    {
        if (request == null || request.StreamId == Guid.Empty || request.Events == null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "invalid_request",
                Detail = "Body must contain a non-empty streamId and an events array (events MAY be empty for a no-op).",
                Status = StatusCodes.Status400BadRequest,
            });
        }
        return null;
    }

    private ActionResult UnknownStreamIdResult(Guid streamId)
    {
        return NotFound(new ProblemDetails
        {
            Title = "unknown_stream",
            Detail = $"No active stream with id '{streamId}'. The connection may have already closed.",
            Status = StatusCodes.Status404NotFound,
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves session metadata from <c>HttpContext.Items["Session"]</c>.
    /// Returns <c>null</c> metadata when the session is valid but not yet ready (caller should 202).
    /// Sets <paramref name="mismatchResult"/> when the session kind is unsupported (caller should return it).
    /// </summary>
    private ProfilerMetadataDto ResolveMetadata(out ActionResult mismatchResult)
    {
        mismatchResult = null;
        var session = HttpContext.Items["Session"];

        if (session is TraceSession trace)
        {
            return trace.Runtime.Metadata;
        }

        if (session is AttachSession attach)
        {
            return attach.Runtime.Metadata;
        }

        mismatchResult = Conflict(new ProblemDetails
        {
            Title = "session_kind_mismatch",
            Detail = "Topology is only available for Trace and Attach sessions.",
            Status = StatusCodes.Status409Conflict,
        });
        return null;
    }
}
