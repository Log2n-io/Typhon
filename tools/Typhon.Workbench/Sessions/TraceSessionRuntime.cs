using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Typhon.Engine.Profiler;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using ProfilerRecordDecoder = Typhon.Profiler.RecordDecoder;
using ProfilerCacheBuilder = Typhon.Profiler.TraceFileCacheBuilder;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Manages the lifecycle of a trace-session runtime: background cache build, metadata projection, lazy chunk reads.
/// Mirrors <see cref="EngineLifecycle"/>'s role for <see cref="OpenSession"/>, but for recorded traces (no engine hosted).
/// </summary>
/// <remarks>
/// <para>
/// <b>Async build.</b> <see cref="Start"/> is a synchronous factory that kicks off a background <see cref="Task"/> and
/// returns immediately. Clients poll <see cref="Metadata"/> (null until build completes) or subscribe to
/// <see cref="BuildProgressChanged"/> via the profiler build-progress SSE endpoint.
/// </para>
/// <para>
/// <b>Disposal.</b> Cancels the background build, disposes the cache reader. Safe to call multiple times.
/// </para>
/// </remarks>
public sealed partial class TraceSessionRuntime : IDisposable, IChunkProvider
{
    /// <inheritdoc />
    public bool IsReady => IsBuildComplete && Metadata != null;
    /// <summary>Public event-args shape for <see cref="BuildProgressChanged"/>. Neutral of internal builder types.</summary>
    public readonly record struct BuildProgressEventArgs(long BytesRead, long TotalBytes, int TickCount, long EventCount);

    private readonly string _filePath;
    private readonly string _cachePath;
    /// <summary>
    /// True when <see cref="_filePath"/> is a self-contained <c>.typhon-replay</c> file (its own cache, with embedded source metadata).
    /// In that mode the file IS the cache — no sidecar to rebuild, no parent <c>.typhon-trace</c> to open. False for the conventional path
    /// (open <c>.typhon-trace</c>, build/use <c>.typhon-trace-cache</c> sibling).
    /// </summary>
    private readonly bool _isReplayFile;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<ProfilerMetadataDto> _metadataTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TraceFileCacheReader _reader;
    private long _timestampFrequency;
    private string _buildError;
    private bool _disposed;

    /// <summary>The source <c>.typhon-trace</c> path, OR a self-contained <c>.typhon-replay</c> path. Use <see cref="IsReplayFile"/>
    /// to disambiguate.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Path to the cache backing this session. For source <c>.typhon-trace</c> files, this is the sibling
    /// <c>&lt;name&gt;.typhon-trace-cache</c>. For self-contained <c>.typhon-replay</c> files, the cache IS the input file —
    /// this property equals <see cref="FilePath"/>. Don't compare the two strings to detect replays; use <see cref="IsReplayFile"/>.
    /// </summary>
    public string CacheFilePath => _cachePath;

    /// <summary>
    /// True when <see cref="FilePath"/> is a self-contained <c>.typhon-replay</c>. Replay files embed their source metadata in the
    /// <see cref="CacheSectionId.SourceMetadata"/> section, so no companion <c>.typhon-trace</c> exists or is needed.
    /// </summary>
    public bool IsReplayFile => _isReplayFile;

    /// <summary>Projected metadata — null until the background build completes.</summary>
    public ProfilerMetadataDto Metadata { get; private set; }

    /// <summary>Task that resolves with the metadata DTO once the build completes, or faults on build error.</summary>
    public Task<ProfilerMetadataDto> MetadataReady => _metadataTcs.Task;

    /// <summary>True when the build has completed (success or failure).</summary>
    public bool IsBuildComplete => MetadataReady.IsCompleted;

    /// <summary>Source timestamp frequency (ticks/second from the source header). 0 until build completes.</summary>
    public long TimestampFrequency => _timestampFrequency;

    /// <summary>
    /// #302 — source-location manifest read from the source <c>.typhon-trace</c>'s trailer at build
    /// completion. Empty for traces that don't carry attribution (engine emitted no intercepted call
    /// sites) and for replay files (their source data path differs and isn't wired here yet).
    /// Mirrors <see cref="AttachSessionRuntime.SourceLocationManifest"/> so the controller can serve
    /// both session kinds through the same property without re-opening files per request.
    /// </summary>
    public SourceLocationManifestDto SourceLocationManifest { get; private set; } = SourceLocationManifestDto.Empty;

    /// <summary>
    /// Static-schema provider populated from the trace's v7 static-structure tables (component definitions, archetype
    /// definitions, index catalog). Null until the build completes — controllers should gate on <see cref="IsReady"/>
    /// before reading. Used by <see cref="Schema.SchemaService"/> for trace-mode schema requests.
    /// </summary>
    public Schema.IStaticSchemaProvider StaticSchemaProvider { get; private set; }

    /// <summary>Fires every ~200 ms during build with progress counters. Also fires at phase transitions (done / error).</summary>
    public event Action<BuildProgressEventArgs> BuildProgressChanged;

    /// <summary>Fires exactly once when the build finishes (success). Subscribers receive the final metadata DTO.</summary>
    public event Action<ProfilerMetadataDto> BuildCompleted;

    /// <summary>Fires exactly once when the build fails. Subscribers receive the error message.</summary>
    public event Action<string> BuildFailed;

    private TraceSessionRuntime(string filePath, string cachePath, bool isReplayFile, ILogger logger)
    {
        _filePath = filePath;
        _cachePath = cachePath;
        _isReplayFile = isReplayFile;
        _logger = logger;
    }

    /// <summary>
    /// Starts a new trace-session runtime. Throws <see cref="FileNotFoundException"/> synchronously if <paramref name="filePath"/>
    /// does not exist. Otherwise returns immediately — the sidecar cache is built on a background task.
    /// </summary>
    /// <remarks>
    /// Accepts both source <c>.typhon-trace</c> files (the conventional path: build/refresh a sidecar cache, open the cache, project
    /// metadata using the source file) AND self-contained <c>.typhon-replay</c> files saved from a live attach session (no source, no
    /// rebuild — open the file directly as a cache, project metadata from its embedded <see cref="CacheSectionId.SourceMetadata"/>
    /// section). Detection is by extension: <c>.typhon-replay</c> ⇒ replay path, anything else ⇒ source-file path.
    /// </remarks>
    public static TraceSessionRuntime Start(string filePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Trace file not found.", fullPath);
        }

        var isReplayFile = string.Equals(Path.GetExtension(fullPath), ".typhon-replay", StringComparison.OrdinalIgnoreCase);
        // For replay files the file IS the cache — no sibling rebuild path. For source traces, derive the conventional
        // <name>.typhon-trace-cache sidecar path next to the source.
        var cachePath = isReplayFile ? fullPath : ProfilerCacheBuilder.GetCachePathFor(fullPath);
        var runtime = new TraceSessionRuntime(fullPath, cachePath, isReplayFile, logger);
        // Fault-continuation — BuildAsync already catches its own exceptions and faults the
        // metadata TCS, but if an unexpected error escapes its top-level try/catch the task becomes
        // unobserved. Logging it here gives us a diagnostic breadcrumb either way.
        _ = Task.Run(runtime.BuildAsync)
            .ContinueWith(
                t => runtime.LogBuildTaskFaulted(t.Exception!, fullPath),
                default,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        return runtime;
    }

    /// <summary>
    /// Reads the raw LZ4-compressed bytes of a chunk. Awaits build completion (or rethrows the build error on fault).
    /// Returns a pooled array — caller is responsible for returning it via <see cref="ArrayPool{T}.Return"/> after use.
    /// </summary>
    /// <returns>(bytes, actual length). The pooled array may be larger than the actual data — use <paramref name="length"/>.</returns>
    public async ValueTask<(byte[] Bytes, int Length)> ReadChunkCompressedAsync(int chunkIdx)
    {
        ThrowIfDisposed();
        var metadata = await _metadataTcs.Task.ConfigureAwait(false);
        if (metadata == null || _reader == null)
        {
            throw new InvalidOperationException("Runtime not ready — build has not completed.");
        }
        if ((uint)chunkIdx >= (uint)_reader.ChunkManifest.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIdx),
                $"Chunk index {chunkIdx} out of range (manifest has {_reader.ChunkManifest.Count} entries).");
        }

        var entry = _reader.ChunkManifest[chunkIdx];
        var bytes = ArrayPool<byte>.Shared.Rent((int)entry.CacheByteLength);
        _reader.ReadChunkRaw(entry, bytes.AsSpan(0, (int)entry.CacheByteLength));
        return (bytes, (int)entry.CacheByteLength);
    }

    /// <summary>Returns the manifest entry for the given chunk — used by the controller to set response headers.</summary>
    public async ValueTask<ChunkManifestEntry> GetChunkManifestEntryAsync(int chunkIdx)
    {
        ThrowIfDisposed();
        var metadata = await _metadataTcs.Task.ConfigureAwait(false);
        if (metadata == null || _reader == null)
        {
            throw new InvalidOperationException("Runtime not ready — build has not completed.");
        }
        if ((uint)chunkIdx >= (uint)_reader.ChunkManifest.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIdx));
        }
        return _reader.ChunkManifest[chunkIdx];
    }

    private async Task BuildAsync()
    {
        try
        {
            var ct = _cts.Token;
            ct.ThrowIfCancellationRequested();

            byte[] fingerprint;
            if (_isReplayFile)
            {
                // .typhon-replay: the file IS the cache. No source to fingerprint, no sibling to rebuild. Skip straight to opening
                // the cache reader; the loader will read the embedded SourceMetadata section to project metadata.
                fingerprint = new byte[32];
                _reader = await OpenCacheWithRetryAsync(_cachePath, ct);
                if (!_reader.IsSelfContained)
                {
                    throw new InvalidDataException(
                        $"File '{_filePath}' has the .typhon-replay extension but is not a self-contained cache " +
                        "(IsSelfContained flag is not set on the cache header). Was it saved from an old Workbench version?");
                }
                // Identifier slot in the cache header is a sessionId for self-contained caches, not a source-file fingerprint.
                // Surface it through the metadata DTO for parity with the source-derived path.
                _reader.CopySourceFingerprint(fingerprint);
            }
            else
            {
                // Step 1 — check existing cache freshness via fingerprint. If the cache file exists AND its fingerprint matches the source's
                // current fingerprint, skip the rebuild and reuse it. This keeps reopens under 100 ms for traces that haven't changed.
                fingerprint = new byte[32];
                TraceFileCacheReader.ComputeSourceFingerprint(_filePath, fingerprint);

                var needsRebuild = true;
                if (File.Exists(_cachePath))
                {
                    try
                    {
                        using var probeStream = File.OpenRead(_cachePath);
                        using var probeReader = new TraceFileCacheReader(probeStream);
                        if (probeReader.VerifyFingerprint(fingerprint))
                        {
                            needsRebuild = false;
                        }
                    }
                    catch
                    {
                        // Any open/read failure on the probe → rebuild. Old/incompatible cache versions land here.
                        needsRebuild = true;
                    }
                }

                if (needsRebuild)
                {
                    var progress = new Progress<ProfilerCacheBuilder.BuildProgress>(p =>
                    {
                        BuildProgressChanged?.Invoke(new BuildProgressEventArgs(p.BytesRead, p.TotalBytes, p.TickCount, p.EventCount));
                    });
                    // Blocking synchronous call; Task.Run in Start already put us on a thread-pool thread, so no further scheduling needed.
                    ProfilerCacheBuilder.Build(_filePath, _cachePath, progress);
                }

                // Step 2 — open the cache reader with a Windows-MMF-style retry loop (defense against fresh-write-then-read races on NTFS).
                _reader = await OpenCacheWithRetryAsync(_cachePath, ct);
            }

            // Step 3 — project the metadata DTO. This is cheap (<10 ms even for 500K-tick traces) because the sections are already
            // loaded into memory by the reader's constructor.
            _timestampFrequency = _isReplayFile
                ? ReadTimestampFrequencyFromMetadataBytes(_reader.SourceMetadataBytes)
                : ReadSourceTimestampFrequency(_filePath);
            var metadata = BuildMetadataDto(
                _reader,
                _filePath,
                _timestampFrequency,
                fingerprint,
                _isReplayFile,
                staticSchemaProviderSink: provider => StaticSchemaProvider = provider);

            // #302: load the source-location manifest from the trace trailer once, keep on the runtime
            // for cheap reuse by the controller. Skipped for replay files (no source trace to read).
            if (!_isReplayFile)
            {
                SourceLocationManifest = TryLoadSourceLocationManifest(_filePath);
            }

            Metadata = metadata;
            _metadataTcs.TrySetResult(metadata);
            BuildCompleted?.Invoke(metadata);
        }
        catch (OperationCanceledException)
        {
            _metadataTcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _buildError = ex.Message;
            LogBuildFailed(ex, _filePath);
            _metadataTcs.TrySetException(ex);
            BuildFailed?.Invoke(ex.Message);
        }
    }

    private static async Task<TraceFileCacheReader> OpenCacheWithRetryAsync(string cachePath, CancellationToken ct)
    {
        // Mirrors EngineLifecycle.OpenAsync: 6 × 100ms retry on Windows-MMF transient sharing violations.
        const int maxAttempts = 6;
        const int retryDelayMs = 100;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = File.OpenRead(cachePath);
                return new TraceFileCacheReader(stream);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(retryDelayMs, ct);
            }
        }
    }

    // Workbench Data Flow module (#327): join slim ArchetypeRecord (id+name) with the v7 rich ArchetypeDefinitions
    // (carries Revision + ComponentTypeIds[]) and the ComponentType id→name table to produce the rich ArchetypeDto.
    // Trace sessions cannot recover [Archetype(Alias=...)] (the attribute is gone after recording), so Label = Name.
    internal static ArchetypeDto[] ProjectArchetypes(
        IReadOnlyList<ArchetypeRecord> slimRecords,
        IReadOnlyList<ArchetypeDefinitionRecord> richDefs,
        IReadOnlyList<ComponentTypeRecord> componentRecords)
    {
        var arr = new ArchetypeDto[slimRecords.Count];

        var richById = richDefs.Count > 0
            ? richDefs.GroupBy(d => d.ArchetypeId).ToDictionary(g => g.Key, g => g.First())
            : null;
        var componentNameById = componentRecords.GroupBy(c => c.ComponentTypeId)
            .ToDictionary(g => g.Key, g => g.First().Name);

        for (var i = 0; i < slimRecords.Count; i++)
        {
            var slim = slimRecords[i];
            var label = slim.Name;
            var revision = 0;
            string[] componentTypeNames = [];

            if (richById != null && richById.TryGetValue(slim.ArchetypeId, out var rich))
            {
                revision = rich.Revision;
                componentTypeNames = new string[rich.ComponentTypeIds.Length];
                for (var j = 0; j < rich.ComponentTypeIds.Length; j++)
                {
                    componentTypeNames[j] = componentNameById.TryGetValue(rich.ComponentTypeIds[j], out var n)
                        ? n
                        : $"#{rich.ComponentTypeIds[j]}";
                }
            }

            arr[i] = new ArchetypeDto(slim.ArchetypeId, slim.Name, label, revision, componentTypeNames);
        }

        return arr;
    }

    private static long ReadSourceTimestampFrequency(string sourcePath)
    {
        using var fs = File.OpenRead(sourcePath);
        using var reader = new TraceFileReader(fs);
        var header = reader.ReadHeader();
        return header.TimestampFrequency;
    }

    /// <summary>
    /// Reads the <c>FileTable</c> + <c>SourceLocationManifest</c> trailers from the source
    /// <c>.typhon-trace</c> and projects them into the wire DTO shape. Returns <see cref="SourceLocationManifestDto.Empty"/>
    /// for traces without attribution (engine emitted no intercepted call sites) and on any read
    /// failure (we surface absent attribution rather than failing the whole session over it).
    /// </summary>
    private static SourceLocationManifestDto TryLoadSourceLocationManifest(string sourcePath)
    {
        try
        {
            using var fs = File.OpenRead(sourcePath);
            using var reader = new TraceFileReader(fs);
            reader.ReadHeader();
            if (!reader.TryReadSourceLocationManifest(out var files, out var entries))
            {
                return SourceLocationManifestDto.Empty;
            }
            var fileDtos = new SourceLocationFileDto[files.Length];
            for (ushort i = 0; i < files.Length; i++)
            {
                fileDtos[i] = new SourceLocationFileDto(i, files[i] ?? string.Empty);
            }
            var entryDtos = new SourceLocationEntryDto[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                entryDtos[i] = new SourceLocationEntryDto(e.Id, e.FileId, e.Line, e.Kind, e.Method);
            }
            return new SourceLocationManifestDto(fileDtos, entryDtos);
        }
        catch
        {
            return SourceLocationManifestDto.Empty;
        }
    }

    /// <summary>
    /// Walks a <see cref="TraceFileReader"/> positioned at offset 0 (header + 3 tables + optional v6 phases table + v7 static structures)
    /// and projects each into the wire DTO shape. Shared between the source-file path and the embedded-metadata path so both produce
    /// byte-identical metadata DTOs.
    ///
    /// Side-effect: when <paramref name="staticSchemaProviderSink"/> is non-null, this method also snapshots the v7 static-structure
    /// records into a <see cref="Schema.TraceSchemaProvider"/> via the sink. Done inline here (rather than re-walking the file later)
    /// because the reader's internal state is exactly the parsed records right after <c>ReadStaticStructures</c> consumes them — the
    /// alternative is opening + re-parsing the file, which doubles I/O for no gain.
    /// </summary>
    private static (ProfilerHeaderDto, SystemDefinitionDto[], ArchetypeDto[], ComponentTypeDto[], string[]) ProjectHeaderAndTables(
        TraceFileReader traceReader,
        Action<Schema.IStaticSchemaProvider> staticSchemaProviderSink = null)
    {
        var h = traceReader.ReadHeader();
        var headerDto = new ProfilerHeaderDto(
            Version: h.Version,
            TimestampFrequency: h.TimestampFrequency,
            BaseTickRate: h.BaseTickRate,
            WorkerCount: h.WorkerCount,
            SystemCount: h.SystemCount,
            ArchetypeCount: h.ArchetypeCount,
            ComponentTypeCount: h.ComponentTypeCount,
            CreatedUtcTicks: h.CreatedUtcTicks,
            SamplingSessionStartQpc: h.SamplingSessionStartQpc);

        var systemRecords = traceReader.ReadSystemDefinitions();
        var systems = new SystemDefinitionDto[systemRecords.Count];
        for (var i = 0; i < systemRecords.Count; i++)
        {
            var sr = systemRecords[i];
            systems[i] = new SystemDefinitionDto(
                Index: sr.Index,
                Name: sr.Name,
                Type: sr.Type,
                Priority: sr.Priority,
                IsParallel: sr.IsParallel,
                TierFilter: sr.TierFilter,
                Predecessors: sr.Predecessors,
                Successors: sr.Successors,
                PhaseName: sr.PhaseName,
                IsExclusivePhase: sr.IsExclusivePhase,
                Reads: sr.Reads,
                ReadsFresh: sr.ReadsFresh,
                ReadsSnapshot: sr.ReadsSnapshot,
                AdditionalReads: sr.AdditionalReads,
                Writes: sr.Writes,
                SideWrites: sr.SideWrites,
                WritesEvents: sr.WritesEvents,
                ReadsEvents: sr.ReadsEvents,
                WritesResources: sr.WritesResources,
                ReadsResources: sr.ReadsResources,
                ExplicitAfter: sr.ExplicitAfter,
                ExplicitBefore: sr.ExplicitBefore);
        }

        var archetypeRecords = traceReader.ReadArchetypes();

        var componentRecords = traceReader.ReadComponentTypes();

        // Phases section — present in v6+ traces only; reader returns empty for v5.
        var phases = traceReader.ReadPhases().ToArray();
        // v7 static-structure tables. Walk past them so any subsequent block reads land at the right offset.
        traceReader.ReadStaticStructures();

        // #327 fallback: some hosts (AntHill) don't populate the thin id→name tables (they were left empty in
        // ProfilerSessionMetadata before the May-2026 fix). When the v7 rich definitions ARE populated, project them
        // back into the thin records so consumers depending on the thin tables (TopologyDto.Archetypes /
        // .ComponentTypes drives the Workbench Data Flow + Access Matrix panels) still see the full registry.
        if (archetypeRecords.Count == 0 && traceReader.ArchetypeDefinitions.Count > 0)
        {
            var derived = new ArchetypeRecord[traceReader.ArchetypeDefinitions.Count];
            for (var i = 0; i < traceReader.ArchetypeDefinitions.Count; i++)
            {
                var d = traceReader.ArchetypeDefinitions[i];
                derived[i] = new ArchetypeRecord { ArchetypeId = d.ArchetypeId, Name = d.Name };
            }
            archetypeRecords = derived;
        }
        if (componentRecords.Count == 0 && traceReader.ComponentDefinitions.Count > 0)
        {
            var derived = new ComponentTypeRecord[traceReader.ComponentDefinitions.Count];
            for (var i = 0; i < traceReader.ComponentDefinitions.Count; i++)
            {
                var d = traceReader.ComponentDefinitions[i];
                derived[i] = new ComponentTypeRecord { ComponentTypeId = d.ComponentTypeId, Name = d.Name };
            }
            componentRecords = derived;
        }

        var componentTypes = new ComponentTypeDto[componentRecords.Count];
        for (var i = 0; i < componentRecords.Count; i++)
        {
            componentTypes[i] = new ComponentTypeDto(componentRecords[i].ComponentTypeId, componentRecords[i].Name);
        }

        // Workbench Data Flow module (#327): project the slim ArchetypeRecord into the rich ArchetypeDto, joining the
        // v7 ArchetypeDefinitions table when present. v6 traces (no rich defs) fall back to Label = Name, Revision = 0,
        // empty ComponentTypeNames — but v6 is rejected at header read so this branch is effectively dead.
        var archetypes = ProjectArchetypes(archetypeRecords, traceReader.ArchetypeDefinitions, componentRecords);

        // Snapshot the v7 records into a TraceSchemaProvider for the schema panels. Toolist (.ToList()) so the
        // arrays are independent of the reader's internal state — traceReader is disposed shortly after this call,
        // and the IReadOnlyList exposed off it would dangle. The provider holds these snapshots for the session's
        // lifetime; they're a few KB in total.
        if (staticSchemaProviderSink != null)
        {
            var provider = new Schema.TraceSchemaProvider(
                components: traceReader.ComponentDefinitions.ToList(),
                archetypes: traceReader.ArchetypeDefinitions.ToList(),
                indexes: traceReader.IndexCatalog.ToList());
            staticSchemaProviderSink(provider);
        }

        return (headerDto, systems, archetypes, componentTypes, phases);
    }

    /// <summary>
    /// Equivalent of <see cref="ReadSourceTimestampFrequency"/> for self-contained replay files: parse the embedded
    /// <see cref="CacheSectionId.SourceMetadata"/> bytes to recover the source's <c>TraceFileHeader.TimestampFrequency</c>.
    /// </summary>
    private static long ReadTimestampFrequencyFromMetadataBytes(ReadOnlySpan<byte> metadataBytes)
    {
        if (metadataBytes.IsEmpty)
        {
            throw new InvalidDataException("Self-contained cache has no SourceMetadata bytes — cannot read timestamp frequency.");
        }
        // The reader needs a Stream. Copy to a heap array (small — typically < 4 KB for header + tables) and wrap in a MemoryStream.
        var copy = metadataBytes.ToArray();
        using var ms = new MemoryStream(copy, writable: false);
        using var reader = new TraceFileReader(ms);
        var header = reader.ReadHeader();
        return header.TimestampFrequency;
    }

    /// <summary>
    /// Projects the cache reader's sections into a wire-ready metadata DTO. For source-derived caches the header / system / archetype /
    /// component tables are read from <paramref name="sourcePath"/> (the parent <c>.typhon-trace</c>). For self-contained caches
    /// (<paramref name="isReplayFile"/> true) those same tables are read from the cache's embedded
    /// <see cref="CacheSectionId.SourceMetadata"/> bytes — no source file is opened.
    /// </summary>
    private static ProfilerMetadataDto BuildMetadataDto(
        TraceFileCacheReader reader,
        string sourcePath,
        long timestampFrequency,
        byte[] fingerprint,
        bool isReplayFile,
        Action<Schema.IStaticSchemaProvider> staticSchemaProviderSink = null)
    {
        ProfilerHeaderDto headerDto;
        SystemDefinitionDto[] systems;
        ArchetypeDto[] archetypes;
        ComponentTypeDto[] componentTypes;
        string[] phases;

        if (isReplayFile)
        {
            // Pull header + tables out of the embedded SourceMetadata section. Bytes are in the same wire format the engine produced
            // — TraceFileReader walks them identically over a MemoryStream.
            var metaCopy = reader.SourceMetadataBytes.ToArray();
            using var ms = new MemoryStream(metaCopy, writable: false);
            using var traceReader = new TraceFileReader(ms);
            (headerDto, systems, archetypes, componentTypes, phases) = ProjectHeaderAndTables(traceReader, staticSchemaProviderSink);
        }
        else
        {
            // Read source tables (header is already read by ReadSourceTimestampFrequency, but we need the tables — re-open and walk).
            using var fs = File.OpenRead(sourcePath);
            using var traceReader = new TraceFileReader(fs);
            (headerDto, systems, archetypes, componentTypes, phases) = ProjectHeaderAndTables(traceReader, staticSchemaProviderSink);
        }

        // Tick summaries, manifest, metrics, aggregates all come from the cache reader (already in memory).
        var tickSummaries = new TickSummaryDto[reader.TickSummaries.Count];
        for (var i = 0; i < reader.TickSummaries.Count; i++)
        {
            var ts = reader.TickSummaries[i];
            tickSummaries[i] = new TickSummaryDto(
                TickNumber: ts.TickNumber,
                StartUs: ts.StartUs,
                DurationUs: ts.DurationUs,
                EventCount: ts.EventCount,
                MaxSystemDurationUs: ts.MaxSystemDurationUs,
                ActiveSystemsBitmask: ts.ActiveSystemsBitmask.ToString(),
                OverloadLevel: ts.OverloadLevel,
                TickMultiplier: ts.TickMultiplier,
                MetronomeWaitUs: ts.MetronomeWaitUs,
                MetronomeIntentClass: ts.MetronomeIntentClass,
                ConsecutiveOverrun: ts.ConsecutiveOverrun,
                ConsecutiveUnderrun: ts.ConsecutiveUnderrun);
        }

        var manifest = new ChunkManifestEntryDto[reader.ChunkManifest.Count];
        for (var i = 0; i < reader.ChunkManifest.Count; i++)
        {
            var e = reader.ChunkManifest[i];
            var isContinuation = (e.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;
            manifest[i] = new ChunkManifestEntryDto(
                FromTick: e.FromTick,
                ToTick: e.ToTick,
                EventCount: e.EventCount,
                IsContinuation: isContinuation);
        }

        var metrics = reader.GlobalMetrics;
        var systemAggregates = new SystemAggregateDto[reader.SystemAggregates.Count];
        for (var i = 0; i < reader.SystemAggregates.Count; i++)
        {
            var sa = reader.SystemAggregates[i];
            systemAggregates[i] = new SystemAggregateDto(sa.SystemIndex, sa.InvocationCount, sa.TotalDurationUs);
        }

        var globalMetrics = new GlobalMetricsDto(
            GlobalStartUs: metrics.GlobalStartUs,
            GlobalEndUs: metrics.GlobalEndUs,
            MaxTickDurationUs: metrics.MaxTickDurationUs,
            MaxSystemDurationUs: metrics.MaxSystemDurationUs,
            P95TickDurationUs: metrics.P95TickDurationUs,
            TotalEvents: metrics.TotalEvents,
            TotalTicks: metrics.TotalTicks,
            SystemAggregates: systemAggregates);

        // GC suspensions — walk every chunk once, filter GcSuspension records. Ported from old TraceSessionService.
        // baselineQpc is 0 for file-based traces (the startTs field is already a QPC value in the same time base as the summaries).
        var gcSuspensions = ComputeGcSuspensions(reader, baselineQpc: 0, timestampFrequency);

        var fingerprintHex = Convert.ToHexString(fingerprint);

        // v12 (#311) — pull per-tick rollups directly off the cache reader. v11-or-older caches return empty lists.
        var sysTicks = reader.SystemTickSummaries is { Count: > 0 } st ? ((List<Typhon.Profiler.SystemTickSummary>)st).ToArray() : [];
        var qTicks = reader.QueueTickSummaries is { Count: > 0 } qt ? ((List<Typhon.Profiler.QueueTickSummary>)qt).ToArray() : [];
        var postTicks = reader.PostTickSummaries is { Count: > 0 } pt ? ((List<Typhon.Profiler.PostTickSummary>)pt).ToArray() : [];
        var qNames = reader.QueueIdToName is { Count: > 0 } qn ? new Dictionary<ushort, string>(qn) : new Dictionary<ushort, string>();
        var satTouches = reader.SystemArchetypeTouches is { Count: > 0 } sat
            ? ((List<Typhon.Profiler.SystemArchetypeTouchSummary>)sat).ToArray()
            : [];

        return new ProfilerMetadataDto(
            Fingerprint: fingerprintHex,
            Header: headerDto,
            Systems: systems,
            Archetypes: archetypes,
            ComponentTypes: componentTypes,
            SpanNames: new Dictionary<int, string>(reader.SpanNames),
            GlobalMetrics: globalMetrics,
            TickSummaries: tickSummaries,
            ChunkManifest: manifest,
            GcSuspensions: gcSuspensions,
            Phases: phases,
            SystemTickSummaries: sysTicks,
            QueueTickSummaries: qTicks,
            PostTickSummaries: postTicks,
            QueueIdToName: qNames,
            SystemArchetypeTouches: satTouches);
    }

    /// <summary>
    /// Walk every chunk in the cache reader, extracting GC-suspension records. Cached once per session in the returned array
    /// (the runtime's <see cref="Metadata"/> holds it, so this is called exactly once per build).
    /// </summary>
    private static GcSuspensionDto[] ComputeGcSuspensions(TraceFileCacheReader reader, long baselineQpc, long timestampFrequency)
    {
        if (timestampFrequency <= 0 || reader.ChunkManifest.Count == 0)
        {
            return [];
        }

        var maxCompressed = 0;
        var maxUncompressed = 0;
        foreach (var entry in reader.ChunkManifest)
        {
            if ((int)entry.CacheByteLength > maxCompressed) maxCompressed = (int)entry.CacheByteLength;
            if ((int)entry.UncompressedBytes > maxUncompressed) maxUncompressed = (int)entry.UncompressedBytes;
        }
        if (maxUncompressed == 0) return [];

        var compressedScratch = ArrayPool<byte>.Shared.Rent(maxCompressed);
        var uncompressedScratch = ArrayPool<byte>.Shared.Rent(maxUncompressed);
        var result = new List<GcSuspensionDto>();
        try
        {
            foreach (var entry in reader.ChunkManifest)
            {
                var compSpan = compressedScratch.AsSpan(0, (int)entry.CacheByteLength);
                var uncompSpan = uncompressedScratch.AsSpan(0, (int)entry.UncompressedBytes);
                reader.DecompressChunk(entry, uncompSpan, compSpan);
                WalkRecordsForSuspensions(uncompSpan, baselineQpc, timestampFrequency, result);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedScratch);
            ArrayPool<byte>.Shared.Return(uncompressedScratch);
        }

        result.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));
        return result.ToArray();
    }

    private static void WalkRecordsForSuspensions(
        ReadOnlySpan<byte> records,
        long baselineQpc,
        long timestampFrequency,
        List<GcSuspensionDto> sink)
    {
        var pos = 0;
        while (pos + 3 <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size == 0 || size == 0xFFFF) break;
            if (pos + size > records.Length) break;
            var kind = (TraceEventKind)records[pos + 2];
            if (kind == TraceEventKind.GcSuspension)
            {
                var data = GcSuspensionEventCodec.Decode(records.Slice(pos, size));
                var startUs = (data.StartTimestamp - baselineQpc) * 1_000_000.0 / timestampFrequency;
                var durationUs = data.DurationTicks * 1_000_000.0 / timestampFrequency;
                sink.Add(new GcSuspensionDto(startUs, durationUs, data.ThreadSlot));
            }
            pos += size;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TraceSessionRuntime));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
    }
}
