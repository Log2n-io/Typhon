using System.Buffers;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services;

/// <summary>
/// Computes Tier 1 (mean/sum/count/min/max/stddev/variance/percentiles) and Tier 2
/// (histogram/topk/cdf) aggregation operators over v1 and v2 track families:
/// <c>tick/summary</c>, <c>metronome/wait</c>, <c>system/&lt;name&gt;</c>, <c>queue/&lt;name&gt;</c>,
/// <c>posttick/&lt;phase&gt;</c>.
/// </summary>
public static class AggregationService
{
    private const int StackallocThreshold = 1024;

    // Allowed posttick phase names (must match DataController.GetPostTickTrackData).
    private static readonly HashSet<string> PostTickPhases = new(StringComparer.Ordinal)
    {
        "walFlush", "writeTickFence", "tierBudget", "subscriptionOutput", "tierIndexRebuild", "dormancySweep",
    };

    // ──────────────────────────────────────────────────────────────────────────
    // Public entry points
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>v2 entry — supports all track families. Used by the controller.</summary>
    public static AggregationResultDto[] Compute(ProfilerMetadataDto metadata, AggregationQueryDto[] queries)
        => ComputeAll(metadata.TickSummaries, queries, metadata);

    /// <summary>
    /// Legacy v1 entry. Only <c>tick/summary</c> and <c>metronome/wait</c> tracks are valid.
    /// Throws <see cref="WorkbenchException"/> if a v2 track family is requested.
    /// </summary>
    public static AggregationResultDto[] Compute(TickSummaryDto[] ticks, AggregationQueryDto[] queries)
        => ComputeAll(ticks, queries, metadata: null);

    private static AggregationResultDto[] ComputeAll(
        TickSummaryDto[] ticks,
        AggregationQueryDto[] queries,
        ProfilerMetadataDto metadata)
    {
        var results = new AggregationResultDto[queries.Length];
        for (var i = 0; i < queries.Length; i++)
        {
            results[i] = ComputeOne(ticks, queries[i], metadata);
        }

        return results;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Single-query evaluation
    // ──────────────────────────────────────────────────────────────────────────

    private static AggregationResultDto ComputeOne(
        TickSummaryDto[] ticks,
        AggregationQueryDto query,
        ProfilerMetadataDto metadata)
    {
        ValidateQuery(query, metadata);

        // topk needs parallel (value, tickNumber) arrays — dedicated path.
        if (query.Op == "topk")
        {
            return ComputeTopK(ticks, query, metadata);
        }

        var count = CountMatching(ticks, query, metadata);
        if (count == 0)
        {
            return new AggregationResultDto();
        }

        if (count <= StackallocThreshold)
        {
            Span<double> buf = stackalloc double[count];
            FillValues(buf, ticks, query, metadata);
            return RunScalarOrShape(query, buf);
        }

        var rented = ArrayPool<double>.Shared.Rent(count);
        try
        {
            var span = rented.AsSpan(0, count);
            FillValues(span, ticks, query, metadata);
            return RunScalarOrShape(query, span);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static AggregationResultDto RunScalarOrShape(AggregationQueryDto query, Span<double> values)
    {
        return query.Op switch
        {
            "mean"      => new AggregationResultDto(Mean(values)),
            "sum"       => new AggregationResultDto(Sum(values)),
            "count"     => new AggregationResultDto(values.Length),
            "min"       => new AggregationResultDto(Min(values)),
            "max"       => new AggregationResultDto(Max(values)),
            "stddev"    => new AggregationResultDto(StdDev(values, out _)),
            "variance"  => new AggregationResultDto(VarianceOnly(values)),
            "median"    => new AggregationResultDto(Percentile(values, 50)),
            "p50"       => new AggregationResultDto(Percentile(values, 50)),
            "p75"       => new AggregationResultDto(Percentile(values, 75)),
            "p90"       => new AggregationResultDto(Percentile(values, 90)),
            "p95"       => new AggregationResultDto(Percentile(values, 95)),
            "p99"       => new AggregationResultDto(Percentile(values, 99)),
            "histogram" => new AggregationResultDto(Histogram: ComputeHistogram(values, query.Buckets!.Value)),
            "cdf"       => new AggregationResultDto(Cdf: ComputeCdf(values, query.Samples!.Value)),
            _           => new AggregationResultDto(),
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Validation
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> ValidOps = new(StringComparer.Ordinal)
    {
        "mean", "min", "max", "sum", "count",
        "median", "p50", "p75", "p90", "p95", "p99",
        "stddev", "variance",
        // Tier 2 (#312)
        "histogram", "topk", "cdf",
    };

    private static readonly HashSet<string> TickSummaryFields = new(StringComparer.Ordinal)
    {
        "durationUs", "eventCount", "maxSystemDurationUs", "startUs", "overloadLevel",
        "tickMultiplier", "consecutiveOverrun", "consecutiveUnderrun",
    };

    private static readonly HashSet<string> MetronomeFields = new(StringComparer.Ordinal)
    {
        "waitUs", "intentClass",
    };

    private static readonly HashSet<string> SystemFields = new(StringComparer.Ordinal)
    {
        "durationUs", "startUs", "endUs", "readyUs",
        "entitiesProcessed", "workersTouched", "chunksProcessed", "skipReason",
        "totalCpuUs",
    };

    private static readonly HashSet<string> QueueFields = new(StringComparer.Ordinal)
    {
        "peakDepth", "endOfTickDepth", "overflowCount", "produced", "consumed",
    };

    // Workbench Data Flow module (#327): three new track families share the same field set.
    private static readonly HashSet<string> SystemArchetypeFields = new(StringComparer.Ordinal)
    {
        "entitiesProcessed", "chunkCount",
    };

    private static void ValidateQuery(AggregationQueryDto query, ProfilerMetadataDto metadata)
    {
        if (query.Range == null || query.Range.Length != 2)
        {
            throw new WorkbenchException(400, "bad-range", "Range must be an array of exactly 2 elements [t0, t1]");
        }
        if (query.Range[0] > query.Range[1])
        {
            throw new WorkbenchException(400, "bad-range", $"Range start ({query.Range[0]}) must not exceed range end ({query.Range[1]})");
        }

        ValidateTrackAndField(query, metadata);

        if (!ValidOps.Contains(query.Op))
        {
            throw new WorkbenchException(400, "unknown-op", $"Unknown operator: '{query.Op}'.");
        }

        ValidateOpParams(query);
    }

    private static void ValidateTrackAndField(AggregationQueryDto query, ProfilerMetadataDto metadata)
    {
        var trackId = query.TrackId;

        if (trackId == "tick/summary")
        {
            if (!TickSummaryFields.Contains(query.Field))
            {
                throw new WorkbenchException(400, "unknown-field", $"Unknown field '{query.Field}' for track '{trackId}'");
            }
            return;
        }
        if (trackId == "metronome/wait")
        {
            if (!MetronomeFields.Contains(query.Field))
            {
                throw new WorkbenchException(400, "unknown-field", $"Unknown field '{query.Field}' for track '{trackId}'");
            }
            return;
        }

        // v2 tracks all need metadata.
        if (metadata == null)
        {
            throw new WorkbenchException(400, "unknown-track", $"Unknown track ID: '{trackId}' (or v2 tracks unavailable in this context).");
        }

        if (trackId.StartsWith("system/", StringComparison.Ordinal))
        {
            var name = trackId["system/".Length..];
            if (!TryFindSystemIndex(metadata, name, out _))
            {
                throw new WorkbenchException(400, "unknown-system", $"No system named '{name}' in topology.");
            }
            if (!SystemFields.Contains(query.Field))
            {
                throw new WorkbenchException(400, "unknown-field", $"Unknown field '{query.Field}' for track '{trackId}'");
            }
            return;
        }
        if (trackId.StartsWith("queue/", StringComparison.Ordinal))
        {
            var name = trackId["queue/".Length..];
            if (!TryFindQueueId(metadata, name, out _))
            {
                throw new WorkbenchException(400, "unknown-queue", $"No queue named '{name}' in topology.");
            }
            if (!QueueFields.Contains(query.Field))
            {
                throw new WorkbenchException(400, "unknown-field", $"Unknown field '{query.Field}' for track '{trackId}'");
            }
            return;
        }
        if (trackId.StartsWith("posttick/", StringComparison.Ordinal))
        {
            var phase = trackId["posttick/".Length..];
            if (!PostTickPhases.Contains(phase))
            {
                throw new WorkbenchException(400, "unknown-posttick-phase",
                    $"Unknown post-tick phase '{phase}'. Available: walFlush, writeTickFence, tierBudget, subscriptionOutput, tierIndexRebuild, dormancySweep.");
            }
            if (query.Field != "durationUs")
            {
                throw new WorkbenchException(400, "unknown-field", $"Unknown field '{query.Field}' for track '{trackId}' (only 'durationUs' is supported).");
            }
            return;
        }
        // Workbench Data Flow module (#327) — three new track families. All share the {entitiesProcessed, chunkCount} field set.
        // The trackId encodes the rollup key; resolution happens once per query (TryFind* below).
        if (trackId.StartsWith("system-archetype/", StringComparison.Ordinal))
        {
            // Order matters: this prefix must be checked BEFORE the "system/" prefix to avoid false matches.
            var rest = trackId["system-archetype/".Length..];
            var sep = rest.IndexOf('/');
            if (sep <= 0 || sep >= rest.Length - 1)
            {
                throw new WorkbenchException(400, "bad-trackid",
                    $"Invalid system-archetype track id '{trackId}'. Expected 'system-archetype/<systemName>/<archetypeLabel>'.");
            }
            var sysName = rest[..sep];
            var archLabel = rest[(sep + 1)..];
            if (!TryFindSystemIndex(metadata, sysName, out _))
            {
                throw new WorkbenchException(400, "unknown-system", $"No system named '{sysName}' in topology.");
            }
            if (!TryFindArchetypeId(metadata, archLabel, out _))
            {
                throw new WorkbenchException(400, "unknown-archetype", $"No archetype labelled '{archLabel}' in topology.");
            }
            if (!SystemArchetypeFields.Contains(query.Field))
            {
                throw new WorkbenchException(400, "unknown-field", $"Unknown field '{query.Field}' for track '{trackId}'");
            }
            return;
        }
        if (trackId.StartsWith("archetype/", StringComparison.Ordinal))
        {
            var label = trackId["archetype/".Length..];
            if (!TryFindArchetypeId(metadata, label, out _))
            {
                throw new WorkbenchException(400, "unknown-archetype", $"No archetype labelled '{label}' in topology.");
            }
            if (!SystemArchetypeFields.Contains(query.Field))
            {
                throw new WorkbenchException(400, "unknown-field", $"Unknown field '{query.Field}' for track '{trackId}'");
            }
            return;
        }
        if (trackId.StartsWith("component-family/", StringComparison.Ordinal))
        {
            var family = trackId["component-family/".Length..];
            if (!FamilyExists(metadata, family))
            {
                throw new WorkbenchException(400, "unknown-family",
                    $"No component family '{family}' in topology. Check ComponentFamilies.FamilyOrder.");
            }
            if (!SystemArchetypeFields.Contains(query.Field))
            {
                throw new WorkbenchException(400, "unknown-field", $"Unknown field '{query.Field}' for track '{trackId}'");
            }
            return;
        }

        throw new WorkbenchException(400, "unknown-track", $"Unknown track ID: '{trackId}'");
    }

    private static void ValidateOpParams(AggregationQueryDto query)
    {
        switch (query.Op)
        {
            case "histogram":
                if (query.Buckets == null || query.Buckets.Value <= 0)
                {
                    throw new WorkbenchException(400, "missing-param", "histogram requires 'buckets' > 0.");
                }
                break;
            case "topk":
                if (query.N == null || query.N.Value <= 0)
                {
                    throw new WorkbenchException(400, "missing-param", "topk requires 'n' > 0.");
                }
                break;
            case "cdf":
                if (query.Samples == null || query.Samples.Value < 2)
                {
                    throw new WorkbenchException(400, "missing-param", "cdf requires 'samples' >= 2.");
                }
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Track-family lookups
    // ──────────────────────────────────────────────────────────────────────────

    private static bool TryFindSystemIndex(ProfilerMetadataDto metadata, string name, out ushort sysIdx)
    {
        for (var i = 0; i < metadata.Systems.Length; i++)
        {
            if (metadata.Systems[i].Name == name)
            {
                sysIdx = metadata.Systems[i].Index;
                return true;
            }
        }
        sysIdx = 0;
        return false;
    }

    private static bool TryFindQueueId(ProfilerMetadataDto metadata, string name, out ushort queueId)
    {
        foreach (var (id, n) in metadata.QueueIdToName)
        {
            if (n == name)
            {
                queueId = id;
                return true;
            }
        }
        queueId = 0;
        return false;
    }

    private static bool TryFindArchetypeId(ProfilerMetadataDto metadata, string labelOrName, out ushort archId)
    {
        for (var i = 0; i < metadata.Archetypes.Length; i++)
        {
            var a = metadata.Archetypes[i];
            // Label is the user-facing handle; fall back to Name so URLs that use the raw type name still work.
            if (a.Label == labelOrName || a.Name == labelOrName)
            {
                archId = a.ArchetypeId;
                return true;
            }
        }
        archId = 0;
        return false;
    }

    private static bool FamilyExists(ProfilerMetadataDto metadata, string family)
    {
        // Topology is fetched separately from metadata; AggregationService doesn't have direct access to TopologyDto.
        // Walk SystemArchetypeTouches for any archetype whose ComponentTypeNames intersect the family's component set
        // would require the family map. Simpler: check whether ANY component name maps to this family by walking the
        // component list. We accept the family as long as it appears as a value in the archetype-component map; the
        // family map itself lives on the topology endpoint, but the same name appears on ComponentFamilies which the
        // controller injected into the topology DTO. Without that here, we treat any non-empty string as valid and let
        // the per-tick walk return zero rows when the family has no member archetype.
        return !string.IsNullOrEmpty(family);
    }

    /// <summary>
    /// Returns the set of archetype ids whose component-type-names list contains at least one component classified into
    /// the given family by the heuristic. Computed per query — small (cap is the canonical family count × archetypes).
    /// Caller should treat an empty result as "no archetypes match this family in this trace".
    /// </summary>
    private static HashSet<ushort> ResolveFamilyArchetypes(ProfilerMetadataDto metadata, string family)
    {
        var archIds = new HashSet<ushort>();
        for (var i = 0; i < metadata.Archetypes.Length; i++)
        {
            var a = metadata.Archetypes[i];
            for (var c = 0; c < a.ComponentTypeNames.Length; c++)
            {
                if (ComponentFamilyResolver.ResolveByHeuristic(a.ComponentTypeNames[c]) == family)
                {
                    archIds.Add(a.ArchetypeId);
                    break;
                }
            }
        }
        return archIds;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Count + fill — track-family dispatch
    // ──────────────────────────────────────────────────────────────────────────

    private static int CountMatching(TickSummaryDto[] ticks, AggregationQueryDto query, ProfilerMetadataDto metadata)
    {
        var t0 = query.Range[0];
        var t1 = query.Range[1];
        var trackId = query.TrackId;

        if (trackId == "tick/summary" || trackId == "metronome/wait")
        {
            var n = 0;
            for (var i = 0; i < ticks.Length; i++)
            {
                var no = ticks[i].TickNumber;
                if (no >= t0 && no <= t1) { n++; }
                else if (no > t1) { break; }
            }
            return n;
        }

        if (trackId.StartsWith("system/", StringComparison.Ordinal))
        {
            TryFindSystemIndex(metadata, trackId["system/".Length..], out var sysIdx);
            var rows = metadata.SystemTickSummaries;
            var n = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.SystemIndex == sysIdx && r.TickNumber >= t0 && r.TickNumber <= t1) { n++; }
            }
            return n;
        }

        if (trackId.StartsWith("queue/", StringComparison.Ordinal))
        {
            TryFindQueueId(metadata, trackId["queue/".Length..], out var qid);
            var rows = metadata.QueueTickSummaries;
            var n = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.QueueId == qid && r.TickNumber >= t0 && r.TickNumber <= t1) { n++; }
            }
            return n;
        }

        if (trackId.StartsWith("system-archetype/", StringComparison.Ordinal))
        {
            // One row per tick (the tick's rolled-up (sysIdx, archId) entry; cache builder emits it sparsely so direct count works).
            var rest = trackId["system-archetype/".Length..];
            var sep = rest.IndexOf('/');
            TryFindSystemIndex(metadata, rest[..sep], out var sysIdx);
            TryFindArchetypeId(metadata, rest[(sep + 1)..], out var archId);
            var rows = metadata.SystemArchetypeTouches;
            var n = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.SystemIndex == sysIdx && r.ArchetypeId == archId && r.TickNumber >= t0 && r.TickNumber <= t1) { n++; }
            }
            return n;
        }
        if (trackId.StartsWith("archetype/", StringComparison.Ordinal))
        {
            // Multi-row rollup per tick: one value per tick where ANY system touched the archetype. Count distinct ticks.
            TryFindArchetypeId(metadata, trackId["archetype/".Length..], out var archId);
            return CountDistinctTicksForArchetype(metadata.SystemArchetypeTouches, archId, archetypeIds: null, t0, t1);
        }
        if (trackId.StartsWith("component-family/", StringComparison.Ordinal))
        {
            var family = trackId["component-family/".Length..];
            var familyArchIds = ResolveFamilyArchetypes(metadata, family);
            return CountDistinctTicksForArchetype(metadata.SystemArchetypeTouches, archetypeId: 0, archetypeIds: familyArchIds, t0, t1);
        }

        // posttick/<phase>
        var prows = metadata.PostTickSummaries;
        var pn = 0;
        for (var i = 0; i < prows.Length; i++)
        {
            ref readonly var r = ref prows[i];
            if (r.TickNumber >= t0 && r.TickNumber <= t1) { pn++; }
        }
        return pn;
    }

    // Counts distinct tick numbers that have at least one matching SystemArchetypeTouchSummary row.
    // archetypeIds != null → match any archetype in the set; otherwise match the single archetypeId.
    private static int CountDistinctTicksForArchetype(
        SystemArchetypeTouchSummary[] rows, ushort archetypeId, HashSet<ushort> archetypeIds, uint t0, uint t1)
    {
        if (rows.Length == 0)
        {
            return 0;
        }
        var seen = new HashSet<uint>();
        for (var i = 0; i < rows.Length; i++)
        {
            ref readonly var r = ref rows[i];
            if (r.TickNumber < t0 || r.TickNumber > t1) { continue; }
            var match = archetypeIds != null ? archetypeIds.Contains(r.ArchetypeId) : r.ArchetypeId == archetypeId;
            if (match) { seen.Add(r.TickNumber); }
        }
        return seen.Count;
    }

    private static void FillValues(Span<double> dest, TickSummaryDto[] ticks, AggregationQueryDto query, ProfilerMetadataDto metadata)
    {
        var t0 = query.Range[0];
        var t1 = query.Range[1];
        var trackId = query.TrackId;
        var field = query.Field;
        var idx = 0;

        if (trackId == "tick/summary")
        {
            for (var i = 0; i < ticks.Length && idx < dest.Length; i++)
            {
                var t = ticks[i];
                if (t.TickNumber < t0 || t.TickNumber > t1) { if (t.TickNumber > t1) break; continue; }
                dest[idx++] = ExtractTickSummaryField(t, field);
            }
            return;
        }
        if (trackId == "metronome/wait")
        {
            for (var i = 0; i < ticks.Length && idx < dest.Length; i++)
            {
                var t = ticks[i];
                if (t.TickNumber < t0 || t.TickNumber > t1) { if (t.TickNumber > t1) break; continue; }
                dest[idx++] = field == "waitUs" ? t.MetronomeWaitUs : t.MetronomeIntentClass;
            }
            return;
        }

        if (trackId.StartsWith("system/", StringComparison.Ordinal))
        {
            TryFindSystemIndex(metadata, trackId["system/".Length..], out var sysIdx);
            var rows = metadata.SystemTickSummaries;
            for (var i = 0; i < rows.Length && idx < dest.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.SystemIndex != sysIdx || r.TickNumber < t0 || r.TickNumber > t1) { continue; }
                dest[idx++] = ExtractSystemField(in r, field);
            }
            return;
        }
        if (trackId.StartsWith("queue/", StringComparison.Ordinal))
        {
            TryFindQueueId(metadata, trackId["queue/".Length..], out var qid);
            var rows = metadata.QueueTickSummaries;
            for (var i = 0; i < rows.Length && idx < dest.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.QueueId != qid || r.TickNumber < t0 || r.TickNumber > t1) { continue; }
                dest[idx++] = ExtractQueueField(in r, field);
            }
            return;
        }

        if (trackId.StartsWith("system-archetype/", StringComparison.Ordinal))
        {
            var rest = trackId["system-archetype/".Length..];
            var sep = rest.IndexOf('/');
            TryFindSystemIndex(metadata, rest[..sep], out var sysIdx);
            TryFindArchetypeId(metadata, rest[(sep + 1)..], out var archId);
            var rows = metadata.SystemArchetypeTouches;
            for (var i = 0; i < rows.Length && idx < dest.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.SystemIndex != sysIdx || r.ArchetypeId != archId || r.TickNumber < t0 || r.TickNumber > t1) { continue; }
                dest[idx++] = ExtractSystemArchetypeField(in r, field);
            }
            return;
        }
        if (trackId.StartsWith("archetype/", StringComparison.Ordinal))
        {
            TryFindArchetypeId(metadata, trackId["archetype/".Length..], out var archId);
            FillArchetypeRollup(dest, ref idx, metadata.SystemArchetypeTouches, archId, archetypeIds: null, t0, t1, field);
            return;
        }
        if (trackId.StartsWith("component-family/", StringComparison.Ordinal))
        {
            var family = trackId["component-family/".Length..];
            var familyArchIds = ResolveFamilyArchetypes(metadata, family);
            FillArchetypeRollup(dest, ref idx, metadata.SystemArchetypeTouches, archetypeId: 0, archetypeIds: familyArchIds, t0, t1, field);
            return;
        }

        // posttick/<phase>
        var phase = trackId["posttick/".Length..];
        var prows = metadata.PostTickSummaries;
        for (var i = 0; i < prows.Length && idx < dest.Length; i++)
        {
            ref readonly var r = ref prows[i];
            if (r.TickNumber < t0 || r.TickNumber > t1) { continue; }
            dest[idx++] = ExtractPostTickField(in r, phase);
        }
    }

    // Same as FillArchetypeRollup but also captures the source tick number for each rolled-up value (used by topk).
    private static void FillArchetypeRollupWithTicks(
        Span<double> values, Span<uint> ticks, ref int idx, SystemArchetypeTouchSummary[] rows,
        ushort archetypeId, HashSet<ushort> archetypeIds, uint t0, uint t1, string field)
    {
        uint currentTick = 0;
        var currentEntities = 0u;
        var currentChunks = 0u;
        var currentHasData = false;
        for (var i = 0; i < rows.Length; i++)
        {
            ref readonly var r = ref rows[i];
            if (r.TickNumber < t0 || r.TickNumber > t1) { continue; }
            var match = archetypeIds != null ? archetypeIds.Contains(r.ArchetypeId) : r.ArchetypeId == archetypeId;
            if (!match) { continue; }

            if (currentHasData && r.TickNumber != currentTick)
            {
                if (idx < values.Length)
                {
                    values[idx] = field == "chunkCount" ? currentChunks : currentEntities;
                    ticks[idx] = currentTick;
                    idx++;
                }
                currentEntities = 0;
                currentChunks = 0;
            }
            currentTick = r.TickNumber;
            currentEntities += r.EntityCount;
            currentChunks += r.ChunkCount;
            currentHasData = true;
        }

        if (currentHasData && idx < values.Length)
        {
            values[idx] = field == "chunkCount" ? currentChunks : currentEntities;
            ticks[idx] = currentTick;
            idx++;
        }
    }

    // Per-tick rollup: one value per distinct tick where any matching row exists. Sums entitiesProcessed/chunkCount.
    private static void FillArchetypeRollup(
        Span<double> dest, ref int idx, SystemArchetypeTouchSummary[] rows,
        ushort archetypeId, HashSet<ushort> archetypeIds, uint t0, uint t1, string field)
    {
        // Walk in order — rows are stored sorted by (tick, sys, arch) so consecutive entries with the same tick can be summed in-place.
        uint currentTick = 0;
        var currentEntities = 0u;
        var currentChunks = 0u;
        var currentHasData = false;
        for (var i = 0; i < rows.Length; i++)
        {
            ref readonly var r = ref rows[i];
            if (r.TickNumber < t0 || r.TickNumber > t1) { continue; }
            var match = archetypeIds != null ? archetypeIds.Contains(r.ArchetypeId) : r.ArchetypeId == archetypeId;
            if (!match) { continue; }

            if (currentHasData && r.TickNumber != currentTick)
            {
                if (idx < dest.Length)
                {
                    dest[idx++] = field == "chunkCount" ? currentChunks : currentEntities;
                }
                currentEntities = 0;
                currentChunks = 0;
            }
            currentTick = r.TickNumber;
            currentEntities += r.EntityCount;
            currentChunks += r.ChunkCount;
            currentHasData = true;
        }

        if (currentHasData && idx < dest.Length)
        {
            dest[idx++] = field == "chunkCount" ? currentChunks : currentEntities;
        }
    }

    /// <summary>Same as FillValues but also captures the source tick number for each value (used by topk).</summary>
    private static void FillValuesAndTicks(
        Span<double> values,
        Span<uint> tickNumbers,
        TickSummaryDto[] ticks,
        AggregationQueryDto query,
        ProfilerMetadataDto metadata)
    {
        var t0 = query.Range[0];
        var t1 = query.Range[1];
        var trackId = query.TrackId;
        var field = query.Field;
        var idx = 0;

        if (trackId == "tick/summary")
        {
            for (var i = 0; i < ticks.Length && idx < values.Length; i++)
            {
                var t = ticks[i];
                if (t.TickNumber < t0 || t.TickNumber > t1) { if (t.TickNumber > t1) break; continue; }
                values[idx] = ExtractTickSummaryField(t, field);
                tickNumbers[idx] = t.TickNumber;
                idx++;
            }
            return;
        }
        if (trackId == "metronome/wait")
        {
            for (var i = 0; i < ticks.Length && idx < values.Length; i++)
            {
                var t = ticks[i];
                if (t.TickNumber < t0 || t.TickNumber > t1) { if (t.TickNumber > t1) break; continue; }
                values[idx] = field == "waitUs" ? t.MetronomeWaitUs : t.MetronomeIntentClass;
                tickNumbers[idx] = t.TickNumber;
                idx++;
            }
            return;
        }

        if (trackId.StartsWith("system/", StringComparison.Ordinal))
        {
            TryFindSystemIndex(metadata, trackId["system/".Length..], out var sysIdx);
            var rows = metadata.SystemTickSummaries;
            for (var i = 0; i < rows.Length && idx < values.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.SystemIndex != sysIdx || r.TickNumber < t0 || r.TickNumber > t1) { continue; }
                values[idx] = ExtractSystemField(in r, field);
                tickNumbers[idx] = r.TickNumber;
                idx++;
            }
            return;
        }
        if (trackId.StartsWith("queue/", StringComparison.Ordinal))
        {
            TryFindQueueId(metadata, trackId["queue/".Length..], out var qid);
            var rows = metadata.QueueTickSummaries;
            for (var i = 0; i < rows.Length && idx < values.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.QueueId != qid || r.TickNumber < t0 || r.TickNumber > t1) { continue; }
                values[idx] = ExtractQueueField(in r, field);
                tickNumbers[idx] = r.TickNumber;
                idx++;
            }
            return;
        }

        if (trackId.StartsWith("system-archetype/", StringComparison.Ordinal))
        {
            var rest = trackId["system-archetype/".Length..];
            var sep = rest.IndexOf('/');
            TryFindSystemIndex(metadata, rest[..sep], out var sysIdx);
            TryFindArchetypeId(metadata, rest[(sep + 1)..], out var archId);
            var rows = metadata.SystemArchetypeTouches;
            for (var i = 0; i < rows.Length && idx < values.Length; i++)
            {
                ref readonly var r = ref rows[i];
                if (r.SystemIndex != sysIdx || r.ArchetypeId != archId || r.TickNumber < t0 || r.TickNumber > t1) { continue; }
                values[idx] = ExtractSystemArchetypeField(in r, field);
                tickNumbers[idx] = r.TickNumber;
                idx++;
            }
            return;
        }
        if (trackId.StartsWith("archetype/", StringComparison.Ordinal))
        {
            TryFindArchetypeId(metadata, trackId["archetype/".Length..], out var archId);
            FillArchetypeRollupWithTicks(values, tickNumbers, ref idx, metadata.SystemArchetypeTouches, archId, archetypeIds: null, t0, t1, field);
            return;
        }
        if (trackId.StartsWith("component-family/", StringComparison.Ordinal))
        {
            var family = trackId["component-family/".Length..];
            var familyArchIds = ResolveFamilyArchetypes(metadata, family);
            FillArchetypeRollupWithTicks(values, tickNumbers, ref idx, metadata.SystemArchetypeTouches, archetypeId: 0, archetypeIds: familyArchIds, t0, t1, field);
            return;
        }

        var phase = trackId["posttick/".Length..];
        var prows = metadata.PostTickSummaries;
        for (var i = 0; i < prows.Length && idx < values.Length; i++)
        {
            ref readonly var r = ref prows[i];
            if (r.TickNumber < t0 || r.TickNumber > t1) { continue; }
            values[idx] = ExtractPostTickField(in r, phase);
            tickNumbers[idx] = r.TickNumber;
            idx++;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Per-row field extractors
    // ──────────────────────────────────────────────────────────────────────────

    private static double ExtractTickSummaryField(TickSummaryDto t, string field) => field switch
    {
        "durationUs"          => t.DurationUs,
        "eventCount"          => t.EventCount,
        "maxSystemDurationUs" => t.MaxSystemDurationUs,
        "startUs"             => t.StartUs,
        "overloadLevel"       => t.OverloadLevel,
        "tickMultiplier"      => t.TickMultiplier,
        "consecutiveOverrun"  => t.ConsecutiveOverrun,
        "consecutiveUnderrun" => t.ConsecutiveUnderrun,
        _                     => 0.0, // unreachable: validated.
    };

    private static double ExtractSystemField(in SystemTickSummary r, string field) => field switch
    {
        "durationUs"        => r.DurationUs,
        "startUs"           => r.StartUs,
        "endUs"             => r.EndUs,
        "readyUs"           => r.ReadyUs,
        "entitiesProcessed" => r.EntitiesProcessed,
        "workersTouched"    => r.WorkersTouched,
        "chunksProcessed"   => r.ChunksProcessed,
        "skipReason"        => r.SkipReasonCode,
        "totalCpuUs"        => r.TotalCpuUs,
        _                   => 0.0,
    };

    private static double ExtractQueueField(in QueueTickSummary r, string field) => field switch
    {
        "peakDepth"      => r.PeakDepth,
        "endOfTickDepth" => r.EndOfTickDepth,
        "overflowCount"  => r.OverflowCount,
        "produced"       => r.Produced,
        "consumed"       => r.Consumed,
        _                => 0.0,
    };

    private static double ExtractSystemArchetypeField(in SystemArchetypeTouchSummary r, string field) => field switch
    {
        "entitiesProcessed" => r.EntityCount,
        "chunkCount"        => r.ChunkCount,
        _                   => 0.0,
    };

    private static double ExtractPostTickField(in PostTickSummary r, string phase) => phase switch
    {
        "walFlush"           => r.WalFlushUs,
        "writeTickFence"     => r.WriteTickFenceUs,
        "tierBudget"         => r.TierBudgetUs,
        "subscriptionOutput" => r.SubscriptionOutputUs,
        "tierIndexRebuild"   => r.TierIndexRebuildUs,
        "dormancySweep"      => r.DormancySweepUs,
        _                    => 0.0,
    };

    // ──────────────────────────────────────────────────────────────────────────
    // Tier 1 operators — single-pass (no allocation beyond the input span)
    // ──────────────────────────────────────────────────────────────────────────

    private static double Sum(Span<double> values)
    {
        var sum = 0.0;
        for (var i = 0; i < values.Length; i++) { sum += values[i]; }
        return sum;
    }

    private static double Mean(Span<double> values) => Sum(values) / values.Length;

    private static double Min(Span<double> values)
    {
        var m = double.MaxValue;
        for (var i = 0; i < values.Length; i++) { if (values[i] < m) m = values[i]; }
        return m;
    }

    private static double Max(Span<double> values)
    {
        var m = double.MinValue;
        for (var i = 0; i < values.Length; i++) { if (values[i] > m) m = values[i]; }
        return m;
    }

    /// <summary>Welford's online algorithm. <paramref name="varianceOut"/> is population variance.</summary>
    private static double StdDev(Span<double> values, out double varianceOut)
    {
        var n = 0;
        var mean = 0.0;
        var m2 = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            n++;
            var v = values[i];
            var delta = v - mean;
            mean += delta / n;
            m2 += delta * (v - mean);
        }
        varianceOut = n < 2 ? 0.0 : m2 / n;
        return Math.Sqrt(varianceOut);
    }

    private static double VarianceOnly(Span<double> values)
    {
        StdDev(values, out var v);
        return v;
    }

    /// <summary>Sorts a copy of <paramref name="values"/> and returns the value at the requested percentile (nearest-rank floor).</summary>
    private static double Percentile(Span<double> values, int percentile)
    {
        // values is ours to mutate (caller copy).
        values.Sort();
        var index = (int)Math.Floor(percentile / 100.0 * (values.Length - 1));
        return values[index];
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tier 2 — histogram / topk / cdf
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Equal-width buckets across [min, max]. Last bucket includes max. Values may be mutated.</summary>
    private static HistogramBucketDto[] ComputeHistogram(Span<double> values, int buckets)
    {
        var min = Min(values);
        var max = Max(values);
        var result = new HistogramBucketDto[buckets];

        // Degenerate range — single value (or all equal): one full bucket, others empty.
        if (max <= min)
        {
            for (var b = 0; b < buckets; b++)
            {
                result[b] = new HistogramBucketDto(min, min, b == 0 ? values.Length : 0);
            }
            return result;
        }

        var width = (max - min) / buckets;
        var counts = new int[buckets];
        for (var i = 0; i < values.Length; i++)
        {
            var idx = (int)Math.Floor((values[i] - min) / width);
            if (idx < 0) idx = 0;
            else if (idx >= buckets) idx = buckets - 1;
            counts[idx]++;
        }
        for (var b = 0; b < buckets; b++)
        {
            var lo = min + b * width;
            var hi = b == buckets - 1 ? max : min + (b + 1) * width;
            result[b] = new HistogramBucketDto(lo, hi, counts[b]);
        }
        return result;
    }

    /// <summary>
    /// Sample the empirical CDF at <paramref name="samples"/> evenly-spaced quantiles in [0, 1].
    /// Quantile <c>q</c> maps to the value at index <c>round(q * (n-1))</c> of the sorted set.
    /// </summary>
    private static CdfSampleDto[] ComputeCdf(Span<double> values, int samples)
    {
        values.Sort();
        var n = values.Length;
        var result = new CdfSampleDto[samples];
        for (var i = 0; i < samples; i++)
        {
            var q = (double)i / (samples - 1);
            var idx = (int)Math.Round(q * (n - 1));
            if (idx < 0) idx = 0;
            else if (idx >= n) idx = n - 1;
            result[i] = new CdfSampleDto(q, values[idx]);
        }
        return result;
    }

    private static AggregationResultDto ComputeTopK(
        TickSummaryDto[] ticks,
        AggregationQueryDto query,
        ProfilerMetadataDto metadata)
    {
        var n = query.N!.Value;
        var count = CountMatching(ticks, query, metadata);
        if (count == 0)
        {
            return new AggregationResultDto(TopK: System.Array.Empty<TopKEntryDto>());
        }

        // Need parallel sort on (value, tickNumber) — Array.Sort(keys, items) only operates on arrays.
        // Negate then ascending-sort to get descending; restore sign before reading.
        var valuesRent = ArrayPool<double>.Shared.Rent(count);
        var ticksRent = ArrayPool<uint>.Shared.Rent(count);
        try
        {
            FillValuesAndTicks(valuesRent.AsSpan(0, count), ticksRent.AsSpan(0, count), ticks, query, metadata);
            for (var i = 0; i < count; i++) { valuesRent[i] = -valuesRent[i]; }
            System.Array.Sort(valuesRent, ticksRent, 0, count);
            for (var i = 0; i < count; i++) { valuesRent[i] = -valuesRent[i]; }

            var take = Math.Min(n, count);
            var entries = new TopKEntryDto[take];
            for (var i = 0; i < take; i++)
            {
                entries[i] = new TopKEntryDto(ticksRent[i], valuesRent[i]);
            }
            return new AggregationResultDto(TopK: entries);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(valuesRent);
            ArrayPool<uint>.Shared.Return(ticksRent);
        }
    }
}
