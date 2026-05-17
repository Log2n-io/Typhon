using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Fluent builder for constructing a runtime schedule — the public API game developers use to declare the <c>Track → DAG → Phase → System</c> hierarchy and
/// build a <see cref="DagScheduler"/>.
/// </summary>
/// <remarks>
/// <para>
/// A schedule owns three built-in <see cref="Track"/>s — Engine-Pre, <see cref="PublicTrack"/>, Engine-Post — in that execution order. Apps declare their DAGs
/// on the Public track (<c>schedule.PublicTrack.DeclareDag("Game").Add(...)</c>) and may add further app tracks via <see cref="DeclareTrack"/>; those slot into
/// the app region between Public and Engine-Post in declaration (execution) order. The engine declares its own DAGs (the Fence) on the Engine-Post track.
/// Declaring a DAG is mandatory — there is no default-DAG convenience.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class RuntimeSchedule
{
    private readonly RuntimeOptions _options;
    private readonly List<Track> _tracks;
    private readonly List<EventQueueBase> _eventQueues = [];
    private readonly Dictionary<string, List<EventQueueBase>> _produces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EventQueueBase>> _consumes = new(StringComparer.Ordinal);
    private bool _built;

    private RuntimeSchedule(RuntimeOptions options)
    {
        _options = options ?? new RuntimeOptions();

        // Built-in tracks, in execution order. Engine-Pre / Engine-Post carry the `engine` tag so tooling can hide them by default; Public is the app's track.
        // Engine-Pre is empty initially — declared for symmetry.
        EnginePreTrack = new Track(this, "Engine-Pre", 0, [Track.EngineTag]);
        PublicTrack = new Track(this, "Public", 1, []);
        EnginePostTrack = new Track(this, "Engine-Post", 2, [Track.EngineTag]);
        _tracks = [EnginePreTrack, PublicTrack, EnginePostTrack];
    }

    /// <summary>Creates a new runtime schedule builder.</summary>
    /// <param name="options">Runtime configuration. If null, defaults are used.</param>
    public static RuntimeSchedule Create(RuntimeOptions options = null) => new(options);

    // ═══════════════════════════════════════════════════════════════
    // Tracks
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The built-in Engine-Pre track — engine work before the app. Empty initially; declared for symmetry.</summary>
    public Track EnginePreTrack { get; }

    /// <summary>The built-in Public track — the app declares its DAGs here.</summary>
    public Track PublicTrack { get; }

    /// <summary>The built-in Engine-Post track — engine work after the app (currently the parallel Fence DAG).</summary>
    public Track EnginePostTrack { get; }

    /// <summary>All tracks in execution order: Engine-Pre, Public, any app tracks (see <see cref="DeclareTrack"/>), Engine-Post.</summary>
    public IReadOnlyList<Track> Tracks => _tracks;

    /// <summary>
    /// Declares an application <see cref="Track"/> and appends it to the app region — after <see cref="PublicTrack"/> and any previously declared app tracks,
    /// before the engine's Engine-Post track. Declaration order is execution order: every DAG of a track completes before any DAG of the next track begins
    /// (a coarse engine-level barrier).
    /// </summary>
    /// <param name="name">
    /// Track name — must be non-empty, unique across the schedule, and must not start with the reserved
    /// <c>Engine-</c> prefix.
    /// </param>
    /// <param name="tags">Optional open tag set for tooling. The reserved <see cref="Track.EngineTag"/> is rejected.</param>
    /// <returns>The new track — declare DAGs on it via <see cref="Track.DeclareDag"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The schedule is already built, the name duplicates an existing track or uses the reserved prefix, or the tag set contains the engine tag.
    /// </exception>
    public Track DeclareTrack(string name, params string[] tags)
    {
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.StartsWith("Engine-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Track name '{name}' uses the reserved 'Engine-' prefix. Engine tracks are declared by the engine, not the app.");
        }

        foreach (var existing in _tracks)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Duplicate track name: '{name}'. Track names must be unique across the schedule.");
            }
        }

        if (tags != null)
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag, Track.EngineTag, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Track '{name}' may not carry the reserved '{Track.EngineTag}' tag — it marks engine-internal tracks.");
                }
            }
        }

        // Slot into the app region: after the last app track, before Engine-Post. OrderIndex is reassigned by position so the execution-order contract
        // (Engine-Pre → app tracks → Engine-Post) always holds.
        var track = new Track(this, name, 0, tags ?? []);
        _tracks.Insert(_tracks.Count - 1, track);
        for (var i = 0; i < _tracks.Count; i++)
        {
            _tracks[i].OrderIndex = i;
        }

        return track;
    }

    // ═══════════════════════════════════════════════════════════════
    // Event queues
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a typed event queue for inter-system communication.
    /// </summary>
    /// <typeparam name="T">Event type.</typeparam>
    /// <param name="name">Diagnostic name for the queue.</param>
    /// <param name="capacity">Maximum events per tick. Must be a power of 2.</param>
    public EventQueue<T> CreateEventQueue<T>(string name, int capacity = 1024)
    {
        ThrowIfBuilt();
        var queue = new EventQueue<T>(name, capacity);
        _eventQueues.Add(queue);
        return queue;
    }

    /// <summary>Declares that a system produces (writes to) the specified event queues.</summary>
    public RuntimeSchedule Produces(string systemName, params EventQueueBase[] queues)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(systemName);

        if (!_produces.TryGetValue(systemName, out var list))
        {
            list = [];
            _produces[systemName] = list;
        }

        list.AddRange(queues);
        return this;
    }

    /// <summary>Declares that a system consumes (reads from) the specified event queues.</summary>
    public RuntimeSchedule Consumes(string systemName, params EventQueueBase[] queues)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(systemName);

        if (!_consumes.TryGetValue(systemName, out var list))
        {
            list = [];
            _consumes[systemName] = list;
        }

        list.AddRange(queues);
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // Build
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates the schedule and builds a <see cref="DagScheduler"/>.
    /// </summary>
    /// <param name="parent">Parent resource node (typically <see cref="IResourceRegistry.Runtime"/>).</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A ready-to-start <see cref="DagScheduler"/>.</returns>
    /// <exception cref="InvalidOperationException">A DAG contains a cycle, duplicate names, or invalid references.</exception>
    public DagScheduler Build(IResource parent, ILogger logger = null)
    {
        ThrowIfBuilt();

        // ── Collect every DAG in track order; assign flat global DAG ids ──
        var orderedDags = new List<Dag>();
        foreach (var track in _tracks)
        {
            foreach (var dag in track.Dags)
            {
                dag.Id = orderedDags.Count;
                orderedDags.Add(dag);
            }
        }

        // Flatten registrations, recording each system's owning DAG. Iteration order = DAG order = system index order.
        var allRegs = new List<(Dag.SystemRegistration Reg, Dag Dag)>();
        foreach (var dag in orderedDags)
        {
            foreach (var reg in dag.Registrations)
            {
                allRegs.Add((reg, dag));
            }
        }

        // ── Per-DAG phase index maps + default-phase validation ──
        var dagPhaseIndexMap = new Dictionary<int, Dictionary<string, int>>(orderedDags.Count);
        var dagDefaultPhaseIndex = new Dictionary<int, int>(orderedDags.Count);
        foreach (var dag in orderedDags)
        {
            var map = BuildPhaseIndexMap(dag);
            dagPhaseIndexMap[dag.Id] = map;

            if (!map.TryGetValue(dag.ResolvedDefaultPhase.Name ?? string.Empty, out var defIdx))
            {
                throw new InvalidOperationException(
                    $"DAG '{dag.Name}': default phase '{dag.ResolvedDefaultPhase}' must be one of the DAG's declared phases. " +
                    "Add it to Phases(...) or set DefaultPhase(...) to a listed phase.");
            }

            dagDefaultPhaseIndex[dag.Id] = defIdx;
        }

        // ── Build-time validation (global duplicate names + per-registration rules) ──
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (reg, dag) in allRegs)
        {
            if (!seenNames.Add(reg.Name))
            {
                throw new InvalidOperationException($"Duplicate system name: '{reg.Name}'. System names must be unique across the whole schedule.");
            }

            ValidateRegistration(reg);
        }

        _built = true;

        var dagBuilder = new DagBuilder();

        // Phase 1: register all systems into one flat global DAG builder. The dag-side index matches the allRegs iteration order; mirror it into
        // ISystem.Index for class-based systems.
        var systemDagId = new int[allRegs.Count];
        var dagIndex = 0;
        foreach (var (reg, dag) in allRegs)
        {
            systemDagId[dagIndex] = dag.Id;
            var sourceOverride = reg.SystemInstance != null ? SystemSourceResolver.ResolveOverride(reg.SystemInstance, "Execute") : null;
            switch (reg.Type)
            {
                case SystemType.CallbackSystem:
                    dagBuilder.AddCallbackSystemInternal(reg.Name, reg.CallbackAction, reg.Priority, reg.ShouldRun, sourceOverride, reg.SystemInstance);
                    if (reg.SystemInstance is CallbackSystem cbInstance)
                    {
                        cbInstance.Index = dagIndex;
                    }
                    break;
                case SystemType.QuerySystem:
                    dagBuilder.AddQuerySystemInternal(reg.Name, reg.CallbackAction, reg.Priority, reg.ShouldRun, sourceOverride, reg.SystemInstance);
                    if (reg.SystemInstance is QuerySystem qsInstance)
                    {
                        qsInstance.Index = dagIndex;
                    }
                    break;
                case SystemType.PipelineSystem:
                    dagBuilder.AddPipelineSystem(reg.Name, reg.PipelineChunkAction, reg.TotalChunks, reg.Priority, reg.ShouldRun, reg.SystemInstance);
                    if (reg.SystemInstance is PipelineSystem psInstance)
                    {
                        psInstance.Index = dagIndex;
                    }
                    break;
            }
            dagIndex++;
        }

        // Phase 1b: resolve each registration's DAG-local phase index. Systems that didn't call b.Phase(...) get their DAG's default phase (RFC 07 / Unit 5
        // — every system lands in some phase).
        var regPhaseIndex = new Dictionary<string, int>(allRegs.Count, StringComparer.Ordinal);
        foreach (var (reg, dag) in allRegs)
        {
            if (!reg.PhaseSet)
            {
                regPhaseIndex[reg.Name] = dagDefaultPhaseIndex[dag.Id];
                continue;
            }

            if (!dagPhaseIndexMap[dag.Id].TryGetValue(reg.Phase.Name ?? string.Empty, out var phaseIdx))
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}' declares phase '{reg.Phase}' which is not in DAG '{dag.Name}'.Phases(...). " +
                    "Add it to the DAG's phase list or use a phase already declared there.");
            }

            regPhaseIndex[reg.Name] = phaseIdx;
        }

        // name → owning DAG id, for explicit-edge same-DAG validation.
        var nameToDagId = new Dictionary<string, int>(allRegs.Count, StringComparer.Ordinal);
        foreach (var (reg, dag) in allRegs)
        {
            nameToDagId[reg.Name] = dag.Id;
        }

        // Phase 2: collect explicit dependency edges (.After / .AfterAll / .Before). Edges must stay within one DAG.
        var explicitEdges = new List<(string From, string To)>();
        foreach (var (reg, dag) in allRegs)
        {
            if (reg.After != null)
            {
                ValidateSameDag(reg.After, reg.Name, dag, nameToDagId);
                explicitEdges.Add((reg.After, reg.Name));
            }

            if (reg.AfterAll != null)
            {
                foreach (var dep in reg.AfterAll)
                {
                    ValidateSameDag(dep, reg.Name, dag, nameToDagId);
                    explicitEdges.Add((dep, reg.Name));
                }
            }

            if (reg.Before != null)
            {
                ValidateSameDag(reg.Before, reg.Name, dag, nameToDagId);
                explicitEdges.Add((reg.Name, reg.Before));
            }
        }

        // Phase 2b: per-DAG access-based edge derivation + conflict validation (RFC 07 — Unit 3). DAGs are independent — phases are DAG-local, so derivation
        // runs once per DAG over that DAG's systems only.
        var derivedEdges = new List<(string From, string To)>();
        foreach (var dag in orderedDags)
        {
            var dagSystemNames = new HashSet<string>(StringComparer.Ordinal);
            var systemInfos = new List<AccessDagDeriver.SystemInfo>();
            foreach (var (reg, regDag) in allRegs)
            {
                if (regDag.Id != dag.Id)
                {
                    continue;
                }

                dagSystemNames.Add(reg.Name);
                systemInfos.Add(new AccessDagDeriver.SystemInfo(reg.Name, regPhaseIndex[reg.Name], reg.Access));
            }

            if (systemInfos.Count == 0)
            {
                continue;
            }

            var dagExplicitEdges = new List<(string From, string To)>();
            foreach (var edge in explicitEdges)
            {
                if (dagSystemNames.Contains(edge.From) && dagSystemNames.Contains(edge.To))
                {
                    dagExplicitEdges.Add(edge);
                }
            }

            derivedEdges.AddRange(AccessDagDeriver.DeriveAndValidate(systemInfos, dagExplicitEdges));
        }

        // Phase 2c: apply all edges (explicit + derived) to the DAG builder.
        foreach (var (from, to) in explicitEdges)
        {
            dagBuilder.AddEdge(from, to);
        }

        foreach (var (from, to) in derivedEdges)
        {
            dagBuilder.AddEdge(from, to);
        }

        // Phase 3: build the graph (validates acyclicity, computes predecessors/successors/topological order).
        var (systems, topologicalOrder) = dagBuilder.Build();

        // Phase 4: wire event queue indices into SystemDefinitions.
        var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sys in systems)
        {
            nameToIndex[sys.Name] = sys.Index;
        }

        var queueToIndex = new Dictionary<EventQueueBase, int>();
        for (var i = 0; i < _eventQueues.Count; i++)
        {
            queueToIndex[_eventQueues[i]] = i;
        }

        foreach (var (sysName, queues) in _produces)
        {
            if (!nameToIndex.TryGetValue(sysName, out var sysIdx))
            {
                throw new InvalidOperationException($"Produces: system '{sysName}' not found.");
            }

            var indices = new int[queues.Count];
            for (var i = 0; i < queues.Count; i++)
            {
                indices[i] = queueToIndex[queues[i]];
            }

            systems[sysIdx].ProducesQueueIndices = indices;
        }

        foreach (var (sysName, queues) in _consumes)
        {
            if (!nameToIndex.TryGetValue(sysName, out var sysIdx))
            {
                throw new InvalidOperationException($"Consumes: system '{sysName}' not found.");
            }

            var indices = new int[queues.Count];
            for (var i = 0; i < queues.Count; i++)
            {
                indices[i] = queueToIndex[queues[i]];
            }

            systems[sysIdx].ConsumesQueueIndices = indices;
        }

        // Phase 4b: validate parallel QuerySystem constraints (must run after queue wiring).
        foreach (var (reg, _) in allRegs)
        {
            if (reg.Parallel && nameToIndex.TryGetValue(reg.Name, out var pIdx))
            {
                if (systems[pIdx].ConsumesQueueIndices is { Length: > 0 })
                {
                    throw new InvalidOperationException(
                        $"System '{reg.Name}': Parallel QuerySystem cannot consume event queues. " +
                        "Event queue drain is single-consumer. Move event processing to a separate non-parallel system upstream.");
                }
            }
        }

        // Phase 5: store overload params, input, change filter, and access from registrations.
        foreach (var (reg, _) in allRegs)
        {
            if (!nameToIndex.TryGetValue(reg.Name, out var sysIdx))
            {
                continue;
            }

            systems[sysIdx].TickDivisor = reg.TickDivisor;
            systems[sysIdx].ThrottledTickDivisor = reg.ThrottledTickDivisor;
            systems[sysIdx].CanShed = reg.CanShed;
            systems[sysIdx].InputFactory = reg.InputFactory;
            systems[sysIdx].ChangeFilterTypes = reg.ChangeFilter;
            systems[sysIdx].IsParallelQuery = reg.Parallel;
            systems[sysIdx].WritesVersioned = reg.WritesVersioned;
            systems[sysIdx].TierFilter = reg.TierFilter;
            systems[sysIdx].CellAmortize = reg.CellAmortize;
            systems[sysIdx].IsCheckerboard = reg.Checkerboard;
            systems[sysIdx].ChunksPerWorker = reg.ChunksPerWorker;
            systems[sysIdx].ExplicitChunkCount = reg.ExplicitChunkCount;

            if (reg.Access != null)
            {
                systems[sysIdx].Access = reg.Access;
            }
        }

        // Phase 5b: stamp resolved phase + DAG-local phase index + owning DAG id onto each SystemDefinition.
        foreach (var (reg, dag) in allRegs)
        {
            if (!nameToIndex.TryGetValue(reg.Name, out var sysIdx))
            {
                continue;
            }

            systems[sysIdx].Phase = reg.PhaseSet ? reg.Phase : dag.ResolvedDefaultPhase;
            systems[sysIdx].PhaseIndex = regPhaseIndex[reg.Name];
            systems[sysIdx].DagId = dag.Id;
        }

        // Phase 5c: record which system indices belong to each DAG (track-by-track dispatch needs this).
        var dagSystemIndices = new List<int>[orderedDags.Count];
        for (var i = 0; i < orderedDags.Count; i++)
        {
            dagSystemIndices[i] = [];
        }
        foreach (var sys in systems)
        {
            dagSystemIndices[sys.DagId].Add(sys.Index);
        }
        for (var i = 0; i < orderedDags.Count; i++)
        {
            orderedDags[i].SystemIndices = [.. dagSystemIndices[i]];
        }

        // Phase 6: create the scheduler. Engine-Post is dispatched by the runtime after serial fence prep,
        // so the in-tick track loop stops at it.
        return new DagScheduler(systems, topologicalOrder, _tracks, EnginePostTrack.OrderIndex, _options, parent, [.. _eventQueues], logger);
    }

    /// <summary>Builds the phase-token → DAG-local index map for a DAG. Rejects an empty or duplicate-containing phase list.</summary>
    private static Dictionary<string, int> BuildPhaseIndexMap(Dag dag)
    {
        var phases = dag.ResolvedPhases;
        var map = new Dictionary<string, int>(phases.Length, StringComparer.Ordinal);
        for (var i = 0; i < phases.Length; i++)
        {
            var name = phases[i].Name ?? string.Empty;
            if (!map.TryAdd(name, i))
            {
                throw new InvalidOperationException($"DAG '{dag.Name}': duplicate phase '{name}' in Phases(...) at index {i}. Each phase must appear at most once.");
            }
        }

        return map;
    }

    private static void ValidateSameDag(string target, string source, Dag dag, Dictionary<string, int> nameToDagId)
    {
        if (!nameToDagId.TryGetValue(target, out var targetDagId))
        {
            throw new InvalidOperationException($"System '{source}' in DAG '{dag.Name}' declares a dependency on unknown system '{target}'.");
        }

        if (targetDagId != dag.Id)
        {
            throw new InvalidOperationException(
                $"System '{source}' in DAG '{dag.Name}' declares a dependency on '{target}', which belongs to a different DAG. " +
                "DAGs are independent — dependency edges cannot cross DAG boundaries.");
        }
    }

    private static void ValidateRegistration(Dag.SystemRegistration reg)
    {
        if (reg.Type == SystemType.CallbackSystem)
        {
            if (reg.ChangeFilter is { Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': ChangeFilter is not valid for CallbackSystem. CallbackSystem is proactive and does not have a View input.");
            }

            if (reg.InputFactory != null)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': Input is not valid for CallbackSystem. CallbackSystem is proactive and does not process entities from a View.");
            }

            if (reg.Parallel && reg.ExplicitChunkCount == 0)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': Parallel is not valid for CallbackSystem. Use b.ChunkedParallel(N) for explicit chunked parallel dispatch.");
            }
        }

        if (reg.Type == SystemType.PipelineSystem && reg.Parallel)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': Parallel is not valid for PipelineSystem. PipelineSystem has its own chunk-parallel execution model.");
        }

        if (reg.Parallel && reg.InputFactory == null && reg.ExplicitChunkCount == 0)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': Parallel requires an Input View. Specify b.Input(() => view) alongside b.Parallel(), " +
                "or use b.ChunkedParallel(N) for non-entity-iterating chunked work.");
        }

        if (reg.ExplicitChunkCount > 0)
        {
            if (reg.Type != SystemType.CallbackSystem)
            {
                throw new InvalidOperationException($"System '{reg.Name}': ChunkedParallel is only valid for CallbackSystem.");
            }
            if (reg.InputFactory != null)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': ChunkedParallel is incompatible with Input — chunked callbacks have no entity context.");
            }
            if (reg.ChangeFilter is { Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': ChunkedParallel is incompatible with ChangeFilter — chunked callbacks have no entity context.");
            }
            if (reg.WritesVersioned)
            {
                throw new InvalidOperationException($"System '{reg.Name}': ChunkedParallel is incompatible with WritesVersioned.");
            }
            if (reg.TierFilter != SimTier.All)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': ChunkedParallel is incompatible with Tier — chunked callbacks have no cluster context.");
            }
            if (reg.CellAmortize > 0)
            {
                throw new InvalidOperationException($"System '{reg.Name}': ChunkedParallel is incompatible with CellAmortize.");
            }
            if (reg.Checkerboard)
            {
                throw new InvalidOperationException($"System '{reg.Name}': ChunkedParallel is incompatible with Checkerboard.");
            }
            if (reg.ChunksPerWorker != 1f)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': ChunkedParallel is incompatible with ChunksPerWorker — chunk count is explicit, not derived.");
            }
        }

        if (reg.ChangeFilter is { Length: > 0 } && reg.InputFactory == null)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': ChangeFilter requires an Input View. Specify input: () => view alongside changeFilter.");
        }

        if (reg.WritesVersioned && !reg.Parallel)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': WritesVersioned is only meaningful for parallel QuerySystems. Add b.Parallel() or parallel: true.");
        }

        if (!float.IsFinite(reg.ChunksPerWorker) || reg.ChunksPerWorker < 1f || reg.ChunksPerWorker > 64f)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': ChunksPerWorker must be finite and in [1.0, 64.0], got {reg.ChunksPerWorker}.");
        }

        if (reg.ChunksPerWorker != 1f && !reg.Parallel)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': ChunksPerWorker is only meaningful for parallel QuerySystems. Add b.Parallel() or parallel: true.");
        }

        if (reg.TierFilter == SimTier.None)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': tier: SimTier.None would dispatch zero clusters. Use a specific tier (e.g. SimTier.Tier0), " +
                "a flag combination (e.g. SimTier.Near), or SimTier.All (the default) to disable tier filtering.");
        }

        if (reg.CellAmortize < 0)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': cellAmortize must be >= 0 (0 = no amortization), got {reg.CellAmortize}.");
        }

        if (reg.CellAmortize > 0 && reg.TierFilter == SimTier.All)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': cellAmortize requires a tier filter. Amortizing the full cluster set without tier scoping " +
                "is not supported — amortization is a per-tier policy (typically used with coarse tiers like SimTier.Tier2).");
        }

        if (reg.TierFilter != SimTier.All && reg.TierFilter != SimTier.None && reg.Type != SystemType.QuerySystem)
        {
            throw new InvalidOperationException(
                $"System '{reg.Name}': tier filter is only supported on QuerySystem, not {reg.Type}.");
        }

        if (reg.Checkerboard && !reg.Parallel)
        {
            throw new InvalidOperationException($"System '{reg.Name}': checkerboard dispatch requires parallel: true. Add b.Parallel() or parallel: true.");
        }
    }

    internal void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("This schedule has already been built. Create a new RuntimeSchedule.");
        }
    }
}
