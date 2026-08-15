using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Profiler;
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
public sealed class DataController : WorkbenchControllerBase
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
        // ── v3 tracks (#327) — Workbench Data Flow module ────────────────────
        // Per-archetype rollups: sum of entity touches across every system that targeted the archetype this tick.
        // Addressed as `archetype/<label>`; <label> is `ArchetypeDto.Label` from the topology endpoint.
        new TrackSchemaDto("archetype/<label>", "perTickPerArchetype",
        [
            new TrackFieldDescriptorDto("tickNumber",        "u32"),
            new TrackFieldDescriptorDto("entitiesProcessed", "u32"),
            new TrackFieldDescriptorDto("chunkCount",        "u32"),
        ]),
        // Per-(system, archetype) cross-section: the L4 granularity of the Data Flow Timeline.
        // Addressed as `system-archetype/<systemName>/<archetypeLabel>`.
        new TrackSchemaDto("system-archetype/<system>/<archetype>", "perTickPerSystemPerArchetype",
        [
            new TrackFieldDescriptorDto("tickNumber",        "u32"),
            new TrackFieldDescriptorDto("entitiesProcessed", "u32"),
            new TrackFieldDescriptorDto("chunkCount",        "u32"),
        ]),
        // Per-component-family rollup (L2 granularity): sums entity counts across every (system, archetype) pair
        // whose archetype carries at least one component in the family. Addressed as `component-family/<name>`;
        // <name> matches an entry in `TopologyDto.ComponentFamilies.FamilyOrder`.
        new TrackSchemaDto("component-family/<family>", "perTickPerFamily",
        [
            new TrackFieldDescriptorDto("tickNumber",        "u32"),
            new TrackFieldDescriptorDto("entitiesProcessed", "u32"),
            new TrackFieldDescriptorDto("chunkCount",        "u32"),
        ]),
        // ── v4 tracks (#620) — Workbench entity lens ─────────────────────────
        // Entities born / destroyed per tick. These exist so a spawn storm is VISIBLE before it can be selected: without a
        // per-tick series there is no way to find tick 4,102 in the first place. Addressed as `lifecycle/spawn`,
        // `lifecycle/destroy`, or `lifecycle/spawn/<archetypeLabel>` to scope either to one archetype.
        new TrackSchemaDto("lifecycle/<kind>[/<archetype>]", "perTickLifecycle",
        [
            new TrackFieldDescriptorDto("tickNumber",   "u32"),
            new TrackFieldDescriptorDto("entityCount",  "u32"),
            new TrackFieldDescriptorDto("runCount",     "u32"),
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
            return NotReady();
        }

        return Ok(new TopologyDto(
            metadata.Systems,
            metadata.Archetypes,
            metadata.ComponentTypes,
            metadata.Phases,
            metadata.Tracks,
            ResolveFamilies(metadata).TopologyMap));
    }

    // Workbench Data Flow module (#327): server-side family resolution. Heuristic-only here — the attribute path runs
    // engine-side at session attach when reflection is available; for trace sessions only the component name survives.
    // Both the topology family map (component → family) and the per-family archetype-id sets derive purely from the
    // session's immutable metadata, so they are computed once and memoized by metadata instance — GetTopology rebuilt the
    // map on every fetch and GetComponentFamilyTrackData re-ran the heuristic over every archetype × component on every
    // component-family track slice. The entry is auto-evicted when the session's metadata is collected.
    private static readonly ConditionalWeakTable<ProfilerMetadataDto, FamilyResolution> _familyResolutionCache = new();

    // Shared empty result for a requested family that no archetype carries — RollupByTick over an empty set yields no rows.
    private static readonly HashSet<ushort> _noArchetypes = [];

    private sealed class FamilyResolution
    {
        public required ComponentFamilyMapDto TopologyMap { get; init; }
        public required Dictionary<string, HashSet<ushort>> ArchetypeIdsByFamily { get; init; }
    }

    private static FamilyResolution ResolveFamilies(ProfilerMetadataDto metadata)
        => _familyResolutionCache.GetValue(metadata, BuildFamilyResolution);

    private static FamilyResolution BuildFamilyResolution(ProfilerMetadataDto metadata)
    {
        var componentTypes = metadata.ComponentTypes;
        var map = new Dictionary<string, string>(componentTypes.Length, StringComparer.Ordinal);
        var familiesUsed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < componentTypes.Length; i++)
        {
            var name = componentTypes[i].Name;
            var family = ComponentFamilyResolver.ResolveByHeuristic(name);
            map[name] = family;
            familiesUsed.Add(family);
        }

        // Stable render order: pick the canonical entries that this session actually has, preserving canonical order.
        var orderedFamilies = ComponentFamilyResolver.CanonicalFamilyOrder.Where(familiesUsed.Contains).ToArray();

        // family → archetype ids carrying at least one component in the family (one pass over archetypes × components).
        var archIdsByFamily = new Dictionary<string, HashSet<ushort>>(StringComparer.Ordinal);
        for (var i = 0; i < metadata.Archetypes.Length; i++)
        {
            var a = metadata.Archetypes[i];
            for (var c = 0; c < a.ComponentTypeNames.Length; c++)
            {
                var family = ComponentFamilyResolver.ResolveByHeuristic(a.ComponentTypeNames[c]);
                if (!archIdsByFamily.TryGetValue(family, out var set))
                {
                    set = [];
                    archIdsByFamily[family] = set;
                }
                set.Add(a.ArchetypeId);
            }
        }

        return new FamilyResolution
        {
            TopologyMap = new ComponentFamilyMapDto(map, orderedFamilies),
            ArchetypeIdsByFamily = archIdsByFamily,
        };
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
        if (metadata == null) { return NotReady(); }

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
        if (metadata == null) { return NotReady(); }

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
            return NotReady();
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
            // Order matters: system-archetype/* before system/* (the latter is a strict prefix of nothing relevant here, but stay explicit).
            && !trackId.StartsWith("system-archetype/", StringComparison.Ordinal)
            && !trackId.StartsWith("system/", StringComparison.Ordinal)
            && !trackId.StartsWith("queue/", StringComparison.Ordinal)
            && !trackId.StartsWith("posttick/", StringComparison.Ordinal)
            && !trackId.StartsWith("archetype/", StringComparison.Ordinal)
            && !trackId.StartsWith("component-family/", StringComparison.Ordinal)
            && !trackId.StartsWith("lifecycle/", StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "unknown-track",
                Detail = $"Unknown track: '{trackId}'. Available tracks: tick/summary, metronome/wait, system/<name>, queue/<name>, posttick/*, "
                    + "archetype/<label>, system-archetype/<sys>/<arch>, component-family/<name>, lifecycle/<spawn|destroy>[/<archetype>].",
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
            return NotReady();
        }

        // v12 (#311) + v3 (#327): dispatch to the new track families before the v1 record layout assumes shape.
        // Order: system-archetype/* must precede system/* (StartsWith would otherwise misroute).
        if (trackId.StartsWith("system-archetype/", StringComparison.Ordinal))
        {
            return GetSystemArchetypeTrackData(metadata, trackId, from, to);
        }
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
        if (trackId.StartsWith("archetype/", StringComparison.Ordinal))
        {
            return GetArchetypeTrackData(metadata, trackId, from, to);
        }
        if (trackId.StartsWith("component-family/", StringComparison.Ordinal))
        {
            return GetComponentFamilyTrackData(metadata, trackId, from, to);
        }
        if (trackId.StartsWith("lifecycle/", StringComparison.Ordinal))
        {
            return GetLifecycleTrackData(metadata, trackId, from, to);
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

    private ActionResult<TrackDataResponseDto> GetSystemArchetypeTrackData(ProfilerMetadataDto metadata, string trackId, uint from, uint to)
    {
        var rest = trackId["system-archetype/".Length..];
        var sep = rest.IndexOf('/');
        if (sep <= 0 || sep >= rest.Length - 1)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "bad-trackid",
                Detail = $"Invalid system-archetype track id '{trackId}'. Expected 'system-archetype/<systemName>/<archetypeLabel>'.",
                Status = StatusCodes.Status400BadRequest,
            });
        }
        var sysName = rest[..sep];
        var archLabel = rest[(sep + 1)..];

        ushort? sysIdx = null;
        for (var i = 0; i < metadata.Systems.Length; i++)
        {
            if (metadata.Systems[i].Name == sysName) { sysIdx = metadata.Systems[i].Index; break; }
        }
        if (sysIdx == null)
        {
            return NotFound(new ProblemDetails { Title = "unknown-system", Detail = $"No system named '{sysName}' in topology.", Status = StatusCodes.Status404NotFound });
        }

        ushort? archId = null;
        for (var i = 0; i < metadata.Archetypes.Length; i++)
        {
            var a = metadata.Archetypes[i];
            if (a.Label == archLabel || a.Name == archLabel) { archId = a.ArchetypeId; break; }
        }
        if (archId == null)
        {
            return NotFound(new ProblemDetails { Title = "unknown-archetype", Detail = $"No archetype labelled '{archLabel}' in topology.", Status = StatusCodes.Status404NotFound });
        }

        var rows = metadata.SystemArchetypeTouches;
        var output = new List<SystemArchetypeRecordDto>();
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r.SystemIndex != sysIdx.Value || r.ArchetypeId != archId.Value) continue;
            if (r.TickNumber < from || r.TickNumber > to) continue;
            output.Add(new SystemArchetypeRecordDto(r.TickNumber, r.EntityCount, r.ChunkCount));
        }
        return Ok(new TrackDataResponseDto(trackId, output.ToArray()));
    }

    private ActionResult<TrackDataResponseDto> GetArchetypeTrackData(ProfilerMetadataDto metadata, string trackId, uint from, uint to)
    {
        var label = trackId["archetype/".Length..];
        ushort? archId = null;
        for (var i = 0; i < metadata.Archetypes.Length; i++)
        {
            var a = metadata.Archetypes[i];
            if (a.Label == label || a.Name == label) { archId = a.ArchetypeId; break; }
        }
        if (archId == null)
        {
            return NotFound(new ProblemDetails { Title = "unknown-archetype", Detail = $"No archetype labelled '{label}' in topology.", Status = StatusCodes.Status404NotFound });
        }

        var rows = metadata.SystemArchetypeTouches;
        var output = RollupByTick(rows, archetypeId: archId.Value, archetypeIds: null, from, to);
        return Ok(new TrackDataResponseDto(trackId, output));
    }

    private ActionResult<TrackDataResponseDto> GetComponentFamilyTrackData(ProfilerMetadataDto metadata, string trackId, uint from, uint to)
    {
        var family = trackId["component-family/".Length..];
        // Archetypes carrying at least one component in the family — resolved once per session (memoized), not per request.
        var familyArchIds = ResolveFamilies(metadata).ArchetypeIdsByFamily.GetValueOrDefault(family, _noArchetypes);
        var rows = metadata.SystemArchetypeTouches;
        var output = RollupByTick(rows, archetypeId: 0, archetypeIds: familyArchIds, from, to);
        return Ok(new TrackDataResponseDto(trackId, output));
    }

    // Walks the (tick, sys, arch)-sorted SystemArchetypeTouches array, summing matching rows per tick into a single output entry.
    // archetypeIds != null → match any archetype in the set; otherwise match the single archetypeId.
    private static object[] RollupByTick(
        Typhon.Profiler.SystemArchetypeTouchSummary[] rows,
        ushort archetypeId, HashSet<ushort> archetypeIds, uint from, uint to)
    {
        var output = new List<ArchetypeRollupRecordDto>();
        uint currentTick = 0;
        var currentEntities = 0u;
        var currentChunks = 0u;
        var currentHasData = false;
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r.TickNumber < from || r.TickNumber > to) continue;
            var match = archetypeIds != null ? archetypeIds.Contains(r.ArchetypeId) : r.ArchetypeId == archetypeId;
            if (!match) continue;

            if (currentHasData && r.TickNumber != currentTick)
            {
                output.Add(new ArchetypeRollupRecordDto(currentTick, currentEntities, currentChunks));
                currentEntities = 0;
                currentChunks = 0;
            }
            currentTick = r.TickNumber;
            currentEntities += r.EntityCount;
            currentChunks += r.ChunkCount;
            currentHasData = true;
        }
        if (currentHasData)
        {
            output.Add(new ArchetypeRollupRecordDto(currentTick, currentEntities, currentChunks));
        }
        return output.ToArray();
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
            return NotReady();
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
    /// Sets <paramref name="mismatchResult"/> when the session has no capture to serve (caller should return it).
    /// </summary>
    /// <remarks>
    /// Resolved through <see cref="WorkbenchControllerBase.TryGetProfilerRuntime"/> rather than by testing the session kind (#618, design §4.1). The topology
    /// and <c>who-writes</c>/<c>who-reads</c> routes are what give a database its <b>system dimension</b> — a database on disk has none of its own — so they
    /// have to answer for an open database with a capture attached, which is not a <c>TraceSession</c> and never will be. #617 rewired the profiler routes the
    /// same way; these were left behind because they live on a different controller.
    /// </remarks>
    private ProfilerMetadataDto ResolveMetadata(out ActionResult mismatchResult)
    {
        mismatchResult = null;
        var session = HttpContext.Items["Session"];

        if (TryGetProfilerRuntime(session, out var runtime))
        {
            return runtime.Metadata;
        }

        if (session is AttachSession attach)
        {
            return attach.Runtime.Metadata;
        }

        // Name the missing thing, not the session's kind: for an open database the answer is "attach a capture", which the kind-based message hid behind
        // "only available for Trace and Attach sessions" — advice that was actively wrong once a capture could be attached to a database.
        mismatchResult = session is OpenSession
            ? ConflictKindMismatch("System topology comes from a profiling capture. Attach one to this database to see which systems touch its components.")
            : ConflictKindMismatch("Topology is only available for a session with a profiling capture.");
        return null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4d. Entity lifecycle (#620) — spawn/destroy series + cohorts
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-tick spawn or destroy volume: <c>lifecycle/spawn</c>, <c>lifecycle/destroy</c>, or either scoped to one archetype
    /// (<c>lifecycle/spawn/&lt;archetypeLabel&gt;</c>). This is the series that makes a spawn storm findable — without it there is no way to know that
    /// tick 4,102 is the interesting one.
    /// </summary>
    private ActionResult<TrackDataResponseDto> GetLifecycleTrackData(ProfilerMetadataDto metadata, string trackId, uint from, uint to)
    {
        var rest = trackId["lifecycle/".Length..];
        var slash = rest.IndexOf('/');
        var kindText = slash < 0 ? rest : rest[..slash];
        var archetypeLabel = slash < 0 ? null : rest[(slash + 1)..];

        if (!TryParseLifecycleKind(kindText, out var kind))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "unknown-track",
                Detail = $"Unknown lifecycle kind '{kindText}'. Expected 'spawn' or 'destroy'.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (!TryResolveLifecycleRouting(metadata, archetypeLabel, out var routingFilter, out var problem))
        {
            return problem;
        }

        var points = EntityLifecycleService.GetSeries(metadata.EntityLifecycleRuns, kind, from, to, routingFilter);
        var records = new object[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            records[i] = new LifecycleTickRecordDto(points[i].TickNumber, points[i].EntityCount, points[i].RunCount);
        }
        return Ok(new TrackDataResponseDto(trackId, records));
    }

    /// <summary>
    /// Returns a page of the entities spawned (or destroyed) within a tick range, plus the identity evidence a caller needs before joining the cohort to
    /// a database.
    /// </summary>
    /// <remarks>
    /// The response deliberately carries <b>both</b> archetype identifiers. <c>routingId</c> is the durable per-database id embedded in every entity id
    /// and is safe to join on; <c>catalogArchetypeId</c> is the trace's per-process id, which is a <i>different number for the same archetype</i> whenever
    /// registration order differs from persisted routing order (design §5.3). Returning only one would leave the caller to guess which it had.
    /// </remarks>
    [HttpGet("lifecycle/cohort")]
    public ActionResult<EntityCohortDto> GetLifecycleCohort(
        Guid sessionId,
        [FromQuery] string kind = "spawn",
        [FromQuery] uint from = 0,
        [FromQuery] uint to = uint.MaxValue,
        [FromQuery] string archetype = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 0)
    {
        if (!TryParseLifecycleKind(kind, out var lifecycleKind))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "bad-kind",
                Detail = $"Unknown lifecycle kind '{kind}'. Expected 'spawn' or 'destroy'.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (from > to)
        {
            return BadRequest(new ProblemDetails { Title = "bad-range", Detail = "from must be <= to.", Status = StatusCodes.Status400BadRequest });
        }

        var metadata = ResolveMetadata(out var mismatch);
        if (mismatch != null)
        {
            return mismatch;
        }
        if (metadata == null)
        {
            return NotReady();
        }

        if (!TryResolveLifecycleRouting(metadata, archetype, out var routingFilter, out var problem))
        {
            return problem;
        }

        var cohort = EntityLifecycleService.GetCohort(metadata.EntityLifecycleRuns, lifecycleKind, from, to, routingFilter, offset, limit);

        // The name is resolved from the ROUTING id, not the catalog id, because the routing id is what the cohort's entity ids actually carry. Null when
        // the cohort spans archetypes or the capture predates D-3 — absent, rather than a name that might belong to another type.
        var archetypeName = ResolveArchetypeNameByRouting(metadata, cohort.RoutingId);

        return Ok(new EntityCohortDto(
            Kind: lifecycleKind.ToString().ToLowerInvariant(),
            FromTick: cohort.FromTick,
            ToTick: cohort.ToTick,
            TotalEntities: cohort.TotalEntities,
            Offset: cohort.Offset,
            EntityIds: [.. cohort.EntityIds],
            HasMore: cohort.HasMore,
            RoutingId: cohort.RoutingId == EntityLifecycleService.MixedRoutingId ? null : cohort.RoutingId,
            CatalogArchetypeId: cohort.CatalogArchetypeId < 0 ? null : cohort.CatalogArchetypeId,
            ArchetypeName: archetypeName));
    }

    private static bool TryParseLifecycleKind(string text, out Typhon.Profiler.EntityLifecycleKind kind)
    {
        switch (text)
        {
            case "spawn": kind = Typhon.Profiler.EntityLifecycleKind.Spawn; return true;
            case "destroy": kind = Typhon.Profiler.EntityLifecycleKind.Destroy; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>Turns an optional archetype label into the routing-id filter the lifecycle section is keyed by.</summary>
    /// <remarks>
    /// A capture whose archetype table predates D-3 carries no routing ids, so scoping to an archetype is <i>impossible</i> rather than merely empty.
    /// Saying so with a 409 beats returning zero rows, which would read as "this archetype spawned nothing".
    /// </remarks>
    private bool TryResolveLifecycleRouting(ProfilerMetadataDto metadata, string archetypeLabel, out ushort? routingFilter, out ActionResult problem)
    {
        routingFilter = null;
        problem = null;
        if (string.IsNullOrEmpty(archetypeLabel))
        {
            return true;
        }

        foreach (var a in metadata.Archetypes)
        {
            if (a.Label != archetypeLabel && a.Name != archetypeLabel)
            {
                continue;
            }

            if (a.RoutingId == Typhon.Profiler.ArchetypeRecord.UnknownRoutingId)
            {
                problem = Conflict(new ProblemDetails
                {
                    Title = "routing-id-unavailable",
                    Detail = $"Archetype '{archetypeLabel}' has no routing id in this capture, so its entities cannot be identified. "
                        + "The capture predates the routing-id field, or it was withheld because multiple engines were observed.",
                    Status = StatusCodes.Status409Conflict,
                });
                return false;
            }

            routingFilter = a.RoutingId;
            return true;
        }

        problem = NotFound(new ProblemDetails
        {
            Title = "unknown-archetype",
            Detail = $"No archetype named '{archetypeLabel}' in this capture.",
            Status = StatusCodes.Status404NotFound,
        });
        return false;
    }

    /// <summary>Archetype display name for a routing id, or null when the capture cannot tell (mixed cohort, or a pre-D-3 archetype table).</summary>
    private static string ResolveArchetypeNameByRouting(ProfilerMetadataDto metadata, ushort routingId)
    {
        if (routingId == EntityLifecycleService.MixedRoutingId)
        {
            return null;
        }

        foreach (var a in metadata.Archetypes)
        {
            if (a.RoutingId == routingId)
            {
                return string.IsNullOrEmpty(a.Label) ? a.Name : a.Label;
            }
        }
        return null;
    }
}
