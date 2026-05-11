using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Profiler;
using Typhon.Profiler;
using Typhon.Schema.Definition;

namespace Typhon.IOProfileRunner;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// A single component sized just under the 112-byte ComponentValue payload cap (4 B Id + 26 × 4 B float = 108 B).
// Large enough that 50K entities (~5.4 MB of live component data) overflow the default 8 MB cache after header /
// metadata / revision-chain overhead, generating sustained DiskRead + DiskWrite + eviction traffic during the
// workload. Versioned storage keeps revision chains so each spawn writes a new row rather than overwriting in place.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[Component("IOProfile.Blob", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct Blob
{
    public int Id;
    public float F00, F01, F02, F03, F04, F05, F06, F07, F08, F09, F10, F11, F12;
    public float G00, G01, G02, G03, G04, G05, G06, G07, G08, G09, G10, G11, G12;

    public static Blob New(int id)
    {
        // Fill with a deterministic but non-trivial pattern so pages can't be zero-compressed to nothing in practice.
        return new Blob
        {
            Id = id,
            F00 = id * 0.1f, F01 = id * 0.2f, F02 = id * 0.3f, F03 = id * 0.4f, F04 = id * 0.5f, F05 = id * 0.6f,
            F06 = id * 0.7f, F07 = id * 0.8f, F08 = id * 0.9f, F09 = id * 1.0f, F10 = id * 1.1f, F11 = id * 1.2f,
            F12 = id * 1.3f,
            G00 = id * 2.1f, G01 = id * 2.2f, G02 = id * 2.3f, G03 = id * 2.4f, G04 = id * 2.5f, G05 = id * 2.6f,
            G06 = id * 2.7f, G07 = id * 2.8f, G08 = id * 2.9f, G09 = id * 3.0f, G10 = id * 3.1f, G11 = id * 3.2f,
            G12 = id * 3.3f,
        };
    }
}

[Archetype(100)]
partial class BlobArch : Archetype<BlobArch>
{
    public static readonly Comp<Blob> Blob = Register<Blob>();
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Transient-mode twin of <see cref="Blob"/>. Same 112-byte payload so the page-allocation cadence is directly comparable
// with the Versioned path (identical stride → identical chunks-per-page). The difference is storage routing:
//   • Versioned Blob  → PersistentStore, page cache, WAL, checkpoint — goes through the full durability pipeline.
//   • Transient Blob  → heap-backed TransientStore via IMemoryAllocator.AllocatePinned — no cache, no WAL, no checkpoint.
// Exists to drive the TransientStore gauge in the viewer (BytesUsed = Σ PageCount × PageSize across all live stores).
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[Component("IOProfile.TransientBlob", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct TransientBlob
{
    public int Id;
    public float F00, F01, F02, F03, F04, F05, F06, F07, F08, F09, F10, F11, F12;
    public float G00, G01, G02, G03, G04, G05, G06, G07, G08, G09, G10, G11, G12;

    public static TransientBlob New(int id)
    {
        return new TransientBlob
        {
            Id = id,
            F00 = id * 0.1f, F01 = id * 0.2f, F02 = id * 0.3f, F03 = id * 0.4f, F04 = id * 0.5f, F05 = id * 0.6f,
            F06 = id * 0.7f, F07 = id * 0.8f, F08 = id * 0.9f, F09 = id * 1.0f, F10 = id * 1.1f, F11 = id * 1.2f,
            F12 = id * 1.3f,
            G00 = id * 2.1f, G01 = id * 2.2f, G02 = id * 2.3f, G03 = id * 2.4f, G04 = id * 2.5f, G05 = id * 2.6f,
            G06 = id * 2.7f, G07 = id * 2.8f, G08 = id * 2.9f, G09 = id * 3.0f, G10 = id * 3.1f, G11 = id * 3.2f,
            G12 = id * 3.3f,
        };
    }
}

[Archetype(101)]
partial class TransientBlobArch : Archetype<TransientBlobArch>
{
    public static readonly Comp<TransientBlob> Data = Register<TransientBlob>();
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Config file shape — System.Text.Json deserializes directly into these records. Fields starting with underscore in
// the JSON (_doc, _comment, etc.) are ignored because they have no matching property — JsonSerializerOptions with
// case-insensitive matching keeps things forgiving.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public sealed class IOProfileConfig
{
    public OutputConfig Output { get; set; } = new();
    public DatabaseConfig Database { get; set; } = new();
    public WorkloadConfig Workload { get; set; } = new();

    // JsonElement rather than bool because the user's config peppers string-valued "_doc" / "_comment" keys across the profile section
    // for readability. Boolean values become real opt-in flags; everything else is ignored as documentation.
    public Dictionary<string, JsonElement> Profile { get; set; } = new();
}

public sealed class OutputConfig
{
    public string TracePath { get; set; } = "ioprofile.typhon-trace";
    public string WorkingDirectory { get; set; } = "ioprofile-data";
}

public sealed class DatabaseConfig
{
    public int CacheSizeMB { get; set; } = 8;
    public bool WalEnabled { get; set; } = true;
}

public sealed class WorkloadConfig
{
    public Dictionary<string, StageConfig> Stages { get; set; } = new();
}

public sealed class StageConfig
{
    public bool Enabled { get; set; } = true;
    public int EntityCount { get; set; }
    public int BatchSize { get; set; }
    public int Iterations { get; set; }
    public int EntitiesPerIteration { get; set; }
    public int Passes { get; set; }
}

public static class Program
{
    public static int Main(string[] args)
    {
        // Name the main thread so the profiler's ThreadInfo records (emitted when this thread first claims its slot inside the engine)
        // carry something meaningful instead of an empty string. The viewer then renders "Slot 0 — IOProfileRunner.Main" on the main
        // lane; without this the main lane falls back to the slot-only label because Thread.CurrentThread.Name is null on the default
        // main thread. Must be set BEFORE any engine code runs that would claim a slot.
        System.Threading.Thread.CurrentThread.Name = "IOProfileRunner.Main";

        var configPath = args.Length > 0 ? args[0] : "ioprofile.json";
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Config file not found: {configPath}");
            Console.Error.WriteLine("Usage: Typhon.IOProfileRunner [path/to/ioprofile.json]");
            return 1;
        }

        Console.WriteLine($"IO Profile Runner");
        Console.WriteLine($"Config: {Path.GetFullPath(configPath)}");

        var config = LoadConfig(configPath);

        // Gate check: the profiler is a JIT-time static-readonly gate. If TelemetryConfig.ProfilerActive is false the whole producer path
        // is dead-code eliminated and no spans will be emitted regardless of what we do here. That's driven by typhon.telemetry.json, not
        // ioprofile.json — we surface it loudly because it's the #1 "why is my trace empty" gotcha.
        if (!TelemetryConfig.ProfilerActive)
        {
            Console.Error.WriteLine("!! TelemetryConfig.ProfilerActive == false — check typhon.telemetry.json in the working directory.");
            Console.Error.WriteLine("!! The profiler producer path is JIT-eliminated when this flag is off, so no trace will be captured.");
            return 2;
        }
        Console.WriteLine($"ProfilerActive: true");

        var cwd = Path.GetFullPath(config.Output.WorkingDirectory);
        Directory.CreateDirectory(cwd);
        var tracePath = Path.IsPathRooted(config.Output.TracePath)
            ? config.Output.TracePath
            : Path.Combine(cwd, config.Output.TracePath);

        Console.WriteLine($"Working dir: {cwd}");
        Console.WriteLine($"Trace out:   {tracePath}");
        Console.WriteLine($"Cache size:  {config.Database.CacheSizeMB} MB");
        Console.WriteLine($"WAL:         {(config.Database.WalEnabled ? "enabled" : "disabled")}");
        Console.WriteLine();

        // ── Apply per-kind suppression from ioprofile.json BEFORE starting the profiler ────────────────────────────
        // Each 'true' in config.Profile calls UnsuppressKind; anything missing or false leaves the default in place.
        // Unknown keys are reported but ignored — helps the user catch typos without failing the run.
        ApplyProfileConfig(config.Profile);

        // ── DI setup (mirrors AssemblyWarmup.cs — the canonical WAL-enabled configuration) ─────────────────────────
        var services = new ServiceCollection();
        services
            .AddLogging(cfg => cfg.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddSingleton<IWalFileIO>(new WalFileIO())
            .AddScopedManagedPagedMemoryMappedFile(opt =>
            {
                opt.DatabaseName = "IOProfileRunner";
                opt.DatabaseDirectory = cwd;
                // The point of a small cache is to force the clock-sweep evictor to run constantly during the workload,
                // which generates PageEvicted markers + DiskRead cache-miss traffic on the read-burst stage.
                opt.DatabaseCacheSize = (ulong)config.Database.CacheSizeMB * 1024UL * 1024UL;
                opt.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(opt =>
            {
                if (config.Database.WalEnabled)
                {
                    // Real async I/O path: WalManager's background writer fsyncs batched commits, which is what the
                    // Flush / DiskWrite completion events are designed to capture. Without the WAL the commit path
                    // stays entirely in memory and no async disk activity is generated.
                    opt.Wal = new WalWriterOptions
                    {
                        WalDirectory = Path.Combine(cwd, "wal"),
                        GroupCommitIntervalMs = 5,
                        UseFUA = false,
                        SegmentSize = 4 * 1024 * 1024,
                        PreAllocateSegments = 1,
                    };
                }
                else
                {
                    opt.Wal = null;
                }

                // PagesPerBlock bump is a DELIBERATE workaround for a latent engine bug around TransientStore's
                // `_pageAddresses` array growth. The default 32 makes the initial `_pageAddresses` capacity = 32 × 4 = 128. Once
                // the live page count crosses 128 (≈9K component chunks at 112-byte stride), the array must grow — but because
                // TransientStore is a mutable struct and ChunkAccessor holds a COPY of the struct, the accessor's copy still
                // points to the pre-growth array and subsequent chunk reads dereference a null / stale pointer (NullRef in Memmove).
                // Bumping to 256 raises initial capacity to 1024 pages, which safely contains our ~12K-entity transient workload
                // (≈170 pages) without ever triggering the array-reallocation path.
                opt.Transient.PagesPerBlock = 256;
            });

        using var sp = services.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();

        // Wipe any leftover WAL directory from a previous run to keep startup deterministic.
        var walDir = Path.Combine(cwd, "wal");
        if (Directory.Exists(walDir))
        {
            try { Directory.Delete(walDir, recursive: true); } catch { /* fall through — WalManager will recreate */ }
        }

        using var scope = sp.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Archetype<BlobArch>.Touch();
        Archetype<TransientBlobArch>.Touch();
        dbe.RegisterComponentFromAccessor<Blob>();
        dbe.RegisterComponentFromAccessor<TransientBlob>();
        dbe.InitializeArchetypes();

        // ── Attach FileExporter + start profiler BEFORE the workload so every stage is captured ────────────────────
        var resources = sp.GetRequiredService<IResourceRegistry>();
        var exporter = new FileExporter(tracePath, resources.Profiler);
        TyphonProfiler.AttachExporter(exporter);

        // workerCount is metadata-only (displayed in the viewer's header; not used to filter records). We pass a reasonable default — the
        // actual slot distribution in the trace is whatever the thread-pool completion workers end up occupying during the workload,
        // which is visible in the readback histogram and the viewer's lane list regardless of this value.
        var metadata = new ProfilerSessionMetadata(
            systems: Array.Empty<SystemDefinitionRecord>(),
            archetypes: Array.Empty<ArchetypeRecord>(),
            componentTypes: Array.Empty<ComponentTypeRecord>(),
            workerCount: 8,
            baseTickRate: 60f,
            startTimestamp: Stopwatch.GetTimestamp(),
            stopwatchFrequency: Stopwatch.Frequency,
            startedUtc: DateTime.UtcNow,
            samplingSessionStartQpc: 0);
        TyphonProfiler.Start(resources.Profiler, metadata);

        Console.WriteLine("── Running workload ──────────────────────────────────────");

        // The viewer buckets records by tick number, and tick numbers are derived by the decoder from TraceEventKind.TickStart records
        // as they arrive in the stream. Without a TickStart the decoder leaves _currentTick at 0, and everything piles into a synthetic
        // "tick 0" container that the viewer's main display treats as pre-session and hides. Since we're not running the scheduler
        // here, nothing emits tick boundaries naturally — we synthesize tick boundaries around each workload stage (+ one per iteration
        // of the spawn-checkpoint loop) so the viewer shows a multi-tick trace rather than one monolithic wrapper tick. With the default
        // loop iteration count of 16, this produces exactly 20 ticks end-to-end.
        var workloadStart = Stopwatch.GetTimestamp();
        RunWorkload(dbe, config.Workload);
        var workloadEndTs = Stopwatch.GetTimestamp();
        var workloadMs = (workloadEndTs - workloadStart) * 1000.0 / Stopwatch.Frequency;

        Console.WriteLine($"── Workload done in {workloadMs:F1} ms ──────────────────────");
        Console.WriteLine();

        // Stop BEFORE the service provider disposes so the exporter thread still exists to drain the final records.
        TyphonProfiler.Stop();
        TyphonProfiler.DetachExporter(exporter);

        Console.WriteLine($"Total dropped (producer ring overflow): {TyphonProfiler.TotalDroppedEvents}");
        // Per-kind TickStart drop counters retired with the generator-emitted EmitTickStart migration. Equivalent
        // per-kind drop attribution lives on TraceRecordRing (per-kind drop counter via TryReserve(size, kind, ...)).
        // Exporter-queue drops — consumer produced batches faster than the file writer could drain them. Non-zero means records made
        // it past the producer ring but got dropped by drop-newest when enqueuing to the FileExporter (the "48/49 PerTickSnapshot" case).
        Console.WriteLine($"Exporter batches dropped (queue full): {TyphonProfiler.TotalDroppedExporterBatches}");
        Console.WriteLine($"Consumer fanned out:   {TyphonProfiler.TotalBatchesFannedOut} batches / {TyphonProfiler.TotalRecordsFannedOut} records");
        Console.WriteLine($"FileExporter processed: {TyphonProfiler.FirstExporterBatchesProcessed} batches / {TyphonProfiler.FirstExporterRecordsProcessed} records");
        Console.WriteLine($"FinalDrain: {TyphonProfiler.FinalDrainPasses} passes ({TyphonProfiler.FinalDrainZeroProgressPasses} zero-progress), {TyphonProfiler.FinalDrainPendingBytes} bytes stranded");
        Console.WriteLine($"Slots @ stop: {TyphonProfiler.SlotStateDump}");
        Console.WriteLine($"Snapshot published: {TyphonEvent.SnapshotPublished}  skipped: inactive={TyphonEvent.SnapshotSkippedGaugesInactive} noSlot={TyphonEvent.SnapshotSkippedNoSlot} nullRing={TyphonEvent.SnapshotSkippedNullRing} ringFull={TyphonEvent.SnapshotSkippedRingFull}");
        Console.WriteLine();

        // ── Readback histogram ─────────────────────────────────────────────────────────────────────────────────────
        PrintTraceSummary(tracePath);

        // Clean up the working directory on shutdown. The caller can comment this out to keep the .typhon-trace
        // file alongside the cache files for deeper inspection.
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Config loading — case-insensitive + allow trailing commas + ignore comments so the JSON stays forgiving
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Canonical list of supported workload stage names. Entries in <c>workload.stages</c> of the JSON config not listed here
    /// are typos or stale references from earlier versions — we log them loudly so the user knows their "stage" did nothing.
    /// Keep in sync with the <c>TryStage(…, "name", …)</c> call sites in <see cref="RunWorkload"/>.
    /// </summary>
    private static readonly HashSet<string> s_knownStageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "spawnBurst",
        "checkpointAfterSpawn",
        "spawnCheckpointLoop",
        "gcChurn",
        "txFlood",
        "transientChurn",
        "readBurst",
        "finalCheckpoint",
    };

    private static IOProfileConfig LoadConfig(string path)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<IOProfileConfig>(json, opts);
        if (cfg == null)
        {
            throw new InvalidDataException("Config file parsed as null");
        }
        // Surface unknown stage names loudly — otherwise a typo (e.g. "spawnBust" instead of "spawnBurst") silently expands to
        // "stage not found, skip" and the user wonders why their config change had no effect. We don't throw (forward-compat
        // with newer configs read by older runner builds), just warn on stderr so the signal is visible in CI logs too.
        if (cfg.Workload?.Stages != null)
        {
            foreach (var key in cfg.Workload.Stages.Keys)
            {
                if (!s_knownStageNames.Contains(key))
                {
                    Console.Error.WriteLine($"  [warn] workload.stages.{key} is not a known stage name — typo? (known: {string.Join(", ", s_knownStageNames)})");
                }
            }
        }
        return cfg;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Map config 'profile' dict → TyphonEvent.UnsuppressKind calls. Key matching is camelCase-insensitive.
    // Unknown keys are logged as warnings so typos are visible without failing the run.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, TraceEventKind> s_profileKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pageCacheFetch"] = TraceEventKind.PageCacheFetch,
        ["pageCacheDiskRead"] = TraceEventKind.PageCacheDiskRead,
        ["pageCacheDiskWrite"] = TraceEventKind.PageCacheDiskWrite,
        ["pageCacheAllocatePage"] = TraceEventKind.PageCacheAllocatePage,
        ["pageCacheFlush"] = TraceEventKind.PageCacheFlush,
        ["pageEvicted"] = TraceEventKind.PageEvicted,
        ["pageCacheDiskReadCompleted"] = TraceEventKind.PageCacheDiskReadCompleted,
        ["pageCacheDiskWriteCompleted"] = TraceEventKind.PageCacheDiskWriteCompleted,
        ["pageCacheFlushCompleted"] = TraceEventKind.PageCacheFlushCompleted,
        ["transactionCommit"] = TraceEventKind.TransactionCommit,
        ["transactionRollback"] = TraceEventKind.TransactionRollback,
        ["transactionCommitComponent"] = TraceEventKind.TransactionCommitComponent,
        ["ecsSpawn"] = TraceEventKind.EcsSpawn,
        ["ecsDestroy"] = TraceEventKind.EcsDestroy,
        ["ecsQueryExecute"] = TraceEventKind.EcsQueryExecute,
        ["ecsQueryCount"] = TraceEventKind.EcsQueryCount,
        ["ecsQueryAny"] = TraceEventKind.EcsQueryAny,
        ["ecsViewRefresh"] = TraceEventKind.EcsViewRefresh,
        ["btreeInsert"] = TraceEventKind.BTreeInsert,
        ["btreeDelete"] = TraceEventKind.BTreeDelete,
        ["btreeNodeSplit"] = TraceEventKind.BTreeNodeSplit,
        ["btreeNodeMerge"] = TraceEventKind.BTreeNodeMerge,
        ["clusterMigration"] = TraceEventKind.ClusterMigration,
        ["transactionPersist"] = TraceEventKind.TransactionPersist,
        ["pageCacheBackpressure"] = TraceEventKind.PageCacheBackpressure,
        ["walFlush"] = TraceEventKind.WalFlush,
        ["walSegmentRotate"] = TraceEventKind.WalSegmentRotate,
        ["walWait"] = TraceEventKind.WalWait,
        ["checkpointCycle"] = TraceEventKind.CheckpointCycle,
        ["checkpointCollect"] = TraceEventKind.CheckpointCollect,
        ["checkpointWrite"] = TraceEventKind.CheckpointWrite,
        ["checkpointFsync"] = TraceEventKind.CheckpointFsync,
        ["checkpointTransition"] = TraceEventKind.CheckpointTransition,
        ["checkpointRecycle"] = TraceEventKind.CheckpointRecycle,
        ["statisticsRebuild"] = TraceEventKind.StatisticsRebuild,
    };

    private static void ApplyProfileConfig(Dictionary<string, JsonElement> profile)
    {
        if (profile == null || profile.Count == 0)
        {
            Console.WriteLine("profile: (empty — all shipped defaults active)");
            return;
        }

        var enabled = new List<string>();
        var disabled = new List<string>();
        var unknown = new List<string>();

        foreach (var kv in profile)
        {
            // Skip documentation keys — anything starting with '_' is a comment by convention, regardless of its value type.
            if (kv.Key.StartsWith('_'))
            {
                continue;
            }

            // Non-boolean values are tolerated as embedded comments (e.g. `"pageCacheFetch": "see note"`). Only explicit true/false
            // translate into UnsuppressKind/SuppressKind calls.
            if (kv.Value.ValueKind != JsonValueKind.True && kv.Value.ValueKind != JsonValueKind.False)
            {
                continue;
            }

            if (!s_profileKeyMap.TryGetValue(kv.Key, out var kind))
            {
                unknown.Add(kv.Key);
                continue;
            }

            if (kv.Value.GetBoolean())
            {
                TyphonEvent.UnsuppressKind(kind);
                enabled.Add(kind.ToString());
            }
            else
            {
                TyphonEvent.SuppressKind(kind);
                disabled.Add(kind.ToString());
            }
        }

        if (enabled.Count > 0) Console.WriteLine($"profile enabled:  {string.Join(", ", enabled)}");
        if (disabled.Count > 0) Console.WriteLine($"profile disabled: {string.Join(", ", disabled)}");
        if (unknown.Count > 0) Console.WriteLine($"profile unknown (ignored): {string.Join(", ", unknown)}");
        Console.WriteLine();
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Workload stages
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static void RunWorkload(DatabaseEngine dbe, WorkloadConfig workload)
    {
        var stages = workload.Stages ?? new Dictionary<string, StageConfig>();
        var tickNumber = 0;
        // Track whether the first gauge snapshot has been emitted. Fixed-at-init capacity gauges (PageCacheTotalPages etc.) are only
        // included in the first snapshot of the session; GaugeSnapshotEmitter flips this flag the first time it runs.
        bool firstGaugeSnapshotEmitted = false;

        // Each RunTick call produces one visible tick in the viewer: emits TickStart, runs the action, emits TickEnd. Tick numbers are
        // assigned monotonically so the viewer's tick axis renders them in order. Because this runner bypasses TyphonRuntime (and
        // therefore DagScheduler.GaugeSnapshotCallback), we invoke GaugeSnapshotEmitter directly after TickEnd — the same work
        // TyphonRuntime.EmitGaugeSnapshotFromScheduler does when it's wired. No-op when ProfilerGaugesActive is false.
        void RunTick(Action action)
        {
            tickNumber++;
            TyphonEvent.SetCurrentTickNumber(tickNumber);
            TyphonEvent.EmitTickStart(Stopwatch.GetTimestamp());
            action();
            TyphonEvent.EmitTickEnd(Stopwatch.GetTimestamp(), overloadLevel: 0, tickMultiplier: 1);
            GaugeSnapshotEmitter.EmitSnapshot((uint)tickNumber, dbe, ref firstGaugeSnapshotEmitted);
        }

        if (TryStage(stages, "spawnBurst", out var spawnBurst))
        {
            RunTick(() => SpawnBurst(dbe, spawnBurst));
        }

        if (TryStage(stages, "checkpointAfterSpawn", out _))
        {
            RunTick(() => Checkpoint(dbe, "checkpointAfterSpawn"));
        }

        if (TryStage(stages, "spawnCheckpointLoop", out var loop))
        {
            // Split the loop into one tick per iteration so each spawn+checkpoint cycle is independently visible in the viewer — otherwise
            // all iterations would compress into a single huge tick bar and the viewer's tick-level zoom would be useless.
            var iterations = loop.Iterations > 0 ? loop.Iterations : 16;
            var perIter = loop.EntitiesPerIteration > 0 ? loop.EntitiesPerIteration : 5_000;
            var sw = Stopwatch.StartNew();
            for (var iter = 0; iter < iterations; iter++)
            {
                var capturedIter = iter;
                RunTick(() => SpawnCheckpointIteration(dbe, perIter, capturedIter));
            }
            sw.Stop();
            Console.WriteLine($"  [ok]   spawnCheckpointLoop: {iterations} × {perIter:N0} spawn+checkpoint cycles in {sw.Elapsed.TotalMilliseconds:F1} ms ({iterations} ticks)");
        }

        if (TryStage(stages, "gcChurn", out var churn))
        {
            // One tick per churn iteration — each iteration allocates a fresh Hashtable + LOH arrays, measures GC movement, and lets the
            // previous tick's allocations age into Gen1/Gen2 before the NEXT tick triggers another wave. This produces a visible sawtooth
            // on the heap-composition stacked area in the Memory group: Gen0 spikes mid-tick, Gen1/Gen2 drift upward as promotions
            // accumulate, LOH jumps on the 85KB-plus allocations. Without a per-tick split the whole thing would collapse to one wide tick.
            var churnIters = churn.Iterations > 0 ? churn.Iterations : 25;
            var itemsPerIter = churn.EntitiesPerIteration > 0 ? churn.EntitiesPerIteration : 40_000;
            var sw = Stopwatch.StartNew();
            for (var iter = 0; iter < churnIters; iter++)
            {
                var capturedIter = iter;
                RunTick(() => GcChurn(capturedIter, itemsPerIter));
            }
            sw.Stop();
            Console.WriteLine($"  [ok]   gcChurn: {churnIters} iterations × {itemsPerIter:N0} items in {sw.Elapsed.TotalMilliseconds:F1} ms");
        }

        if (TryStage(stages, "txFlood", out var txFlood))
        {
            // Many small transactions per tick, designed to make the Tx+UoW gauge's per-tick commit/created rate lines visibly
            // plateau during this stage. Each inner transaction spawns a single entity and commits — so a single tick produces
            // `commitsPerTick` ΔTxChainCommitTotal, plainly readable on the gauge's delta-rate overlay.
            //
            // Note on the "active" signal: `TxChainActiveCount` is sampled at snapshot time (end of tick), by which point every
            // inner QuickTransaction has committed + disposed. The active-count area chart on the Tx+UoW renderer therefore
            // stays at 0 for this stage too — that's a runner limitation, not a gauge-emitter problem. Real apps that carry
            // long-lived transactions across scheduler ticks will exercise the area chart.
            var floodIters = txFlood.Iterations > 0 ? txFlood.Iterations : 10;
            var commitsPerTick = txFlood.EntitiesPerIteration > 0 ? txFlood.EntitiesPerIteration : 50;
            var sw = Stopwatch.StartNew();
            int totalCommits = 0;
            for (var iter = 0; iter < floodIters; iter++)
            {
                var capturedIter = iter;
                var capturedCommits = commitsPerTick;
                RunTick(() => TxFloodIteration(dbe, capturedCommits, capturedIter));
                totalCommits += commitsPerTick;
            }
            sw.Stop();
            Console.WriteLine($"  [ok]   txFlood: {floodIters} × {commitsPerTick:N0} commits/tick = {totalCommits:N0} commits in {sw.Elapsed.TotalMilliseconds:F1} ms ({floodIters} ticks)");
        }

        if (TryStage(stages, "transientChurn", out var transient))
        {
            // Iteratively spawn Transient-mode entities one tick at a time. TransientStore allocates heap pages on demand; pages are
            // never freed until the store is disposed, so this stage produces a monotonically climbing Transient Store gauge that
            // flattens as the chunk pool has spare capacity. One tick per iteration so each allocation wave is visible as its own
            // step in the viewer instead of collapsing into a single wide tick.
            //
            // NOTE: the per-tick spawn count is kept well under the threshold that would trigger TransientStore's `_pageAddresses`
            // array reallocation (see the PagesPerBlock=256 workaround in the DI setup). Crossing that boundary currently crashes
            // with NullRef in Memmove because ChunkAccessor caches a struct-copy of the underlying store.
            var transientIters = transient.Iterations > 0 ? transient.Iterations : 8;
            var perTick = transient.EntitiesPerIteration > 0 ? transient.EntitiesPerIteration : 1_500;
            var sw = Stopwatch.StartNew();
            int transientSpawned = 0;
            for (var iter = 0; iter < transientIters; iter++)
            {
                var capturedIter = iter;
                RunTick(() => TransientSpawnIteration(dbe, perTick, capturedIter));
                transientSpawned += perTick;
            }
            sw.Stop();
            Console.WriteLine($"  [ok]   transientChurn: {transientIters} × {perTick:N0} transient spawns = {transientSpawned:N0} entities in {sw.Elapsed.TotalMilliseconds:F1} ms ({transientIters} ticks)");
        }

        if (TryStage(stages, "readBurst", out var read))
        {
            RunTick(() => ReadBurst(dbe, read));
        }

        if (TryStage(stages, "finalCheckpoint", out _))
        {
            RunTick(() => Checkpoint(dbe, "finalCheckpoint"));
        }

        Console.WriteLine($"  Emitted {tickNumber} ticks total.");
    }

    private static bool TryStage(Dictionary<string, StageConfig> stages, string name, out StageConfig cfg)
    {
        if (stages.TryGetValue(name, out cfg) && cfg.Enabled)
        {
            return true;
        }
        Console.WriteLine($"  [skip] {name}");
        cfg = default;
        return false;
    }

    private static void SpawnBurst(DatabaseEngine dbe, StageConfig cfg)
    {
        var total = cfg.EntityCount > 0 ? cfg.EntityCount : 50_000;
        var batchSize = cfg.BatchSize > 0 ? cfg.BatchSize : 500;
        var sw = Stopwatch.StartNew();
        int spawned = 0;

        while (spawned < total)
        {
            var count = Math.Min(batchSize, total - spawned);
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                var blob = Blob.New(spawned + i);
                tx.Spawn<BlobArch>(BlobArch.Blob.Set(in blob));
            }
            tx.Commit();
            spawned += count;
        }

        sw.Stop();
        Console.WriteLine($"  [ok]   spawnBurst: {spawned:N0} entities in {sw.Elapsed.TotalMilliseconds:F1} ms ({spawned / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0}/s)");
    }

    private static void Checkpoint(DatabaseEngine dbe, string stageName)
    {
        var sw = Stopwatch.StartNew();
        dbe.ForceCheckpoint();
        sw.Stop();
        Console.WriteLine($"  [ok]   {stageName}: ForceCheckpoint took {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>
    /// One iteration of the tx-flood stage. Runs <paramref name="commitsPerTick"/> independent <see cref="DatabaseEngine.CreateQuickTransaction"/>
    /// cycles within a single tick — each one spawns a single Versioned entity and commits. Designed to drive the Tx+UoW gauge's
    /// per-tick commit/created rate lines to a dramatic plateau so the renderer's overlay lines are clearly visible. The entity
    /// IDs offset by a big constant to keep them visually distinguishable from other stages in the detail pane.
    /// </summary>
    private static void TxFloodIteration(DatabaseEngine dbe, int commitsPerTick, int iter)
    {
        for (int i = 0; i < commitsPerTick; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var blob = Blob.New(iter * commitsPerTick + i + 9_000_000);
            tx.Spawn<BlobArch>(BlobArch.Blob.Set(in blob));
            tx.Commit();
        }
    }

    /// <summary>
    /// One iteration of the transient-churn stage. Spawns <paramref name="perTick"/> entities into the Transient-mode archetype, which
    /// routes allocations through <see cref="TransientStore"/> (heap-backed, no page cache, no WAL). Since TransientStore never frees
    /// pages until disposal, each iteration grows the aggregated <c>TransientStoreBytesUsed</c> gauge until the chunk pool has spare
    /// capacity. No checkpoint here — transient data isn't persisted, so ForceCheckpoint would be a no-op for this archetype.
    /// </summary>
    private static void TransientSpawnIteration(DatabaseEngine dbe, int perTick, int iter)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < perTick; i++)
        {
            var blob = TransientBlob.New(iter * perTick + i + 5_000_000);
            tx.Spawn<TransientBlobArch>(TransientBlobArch.Data.Set(in blob));
        }
        tx.Commit();
    }

    /// <summary>
    /// One iteration of the spawn+checkpoint loop. Called once per tick so each iteration shows up as its own bar in the viewer rather than all
    /// iterations compressing into a single tick. The iteration index is used to offset the spawned blob IDs so records from different ticks are
    /// trivially distinguishable in the detail pane.
    /// </summary>
    private static void SpawnCheckpointIteration(DatabaseEngine dbe, int perIter, int iter)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < perIter; i++)
            {
                var blob = Blob.New(iter * perIter + i + 1_000_000);
                tx.Spawn<BlobArch>(BlobArch.Blob.Set(in blob));
            }
            tx.Commit();
        }
        dbe.ForceCheckpoint();
    }

    /// <summary>
    /// Deliberately-ugly managed-memory churn stage. Spins up <see cref="System.Collections.Hashtable"/> (non-generic, so every int key and
    /// value pair boxes to two heap objects), fills it with random entries, periodically allocates LOH-sized byte arrays, and synthesises
    /// enough short-lived strings to keep Gen0 hot. Designed to make the profiler's GC gauges (Gen0/Gen1/Gen2/LOH/POH + committed bytes)
    /// visibly oscillate rather than reading as flat lines. Does NOT interact with the Typhon engine — purely a managed-heap stress test
    /// for gauge-signal validation.
    /// </summary>
    /// <param name="iteration">The current churn iteration (0-based). Used to seed a deterministic PRNG so runs are reproducible.</param>
    /// <param name="itemsPerIteration">How many Hashtable entries to insert this iteration. ~40K items creates ~10 MB of Gen0 pressure per tick via boxing alone.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void GcChurn(int iteration, int itemsPerIteration)
    {
        // ReSharper disable CollectionNeverQueried.Local — the entire point is to allocate; nothing reads these collections back.

        var rng = new Random(0x5EED + iteration);

        // Non-generic Hashtable — each int key and int value boxes to a heap object. At 40K entries that's ~80K small objects per tick,
        // ~1.3 MB of boxing overhead before the Hashtable's own bucket array is counted.
        var ht = new System.Collections.Hashtable(capacity: itemsPerIteration);
        for (int j = 0; j < itemsPerIteration; j++)
        {
            ht[j] = rng.Next();  // both key and value box
        }

        // Short-lived string churn — each string is an independent heap object, forcing Gen0 through its collection cycle fast.
        // 5000 × 64-byte strings ≈ 320 KB of transient Gen0, most of which dies before the next tick.
        string[] strings = new string[5000];
        for (int j = 0; j < strings.Length; j++)
        {
            strings[j] = new string('x', 64 + (j & 31));
        }

        // LOH allocation — arrays ≥ 85_000 bytes go to the Large Object Heap. One 128 KB array per iteration plus a mid-range 40 KB one
        // keeps the LOH gauge moving without blowing RAM over a 25-iteration run (25 × ~170 KB = ~4.3 MB peak LOH footprint, trivially
        // collected on Gen2).
        byte[] loh = new byte[128 * 1024];
        for (int j = 0; j < loh.Length; j += 4096)
        {
            loh[j] = (byte)j;  // touch pages so the OS actually commits them (otherwise the allocation is virtual-only)
        }

        byte[] small = new byte[40 * 1024];
        for (int j = 0; j < small.Length; j += 1024) small[j] = 1;

        // Dictionary<object, object> — another boxing path, this time with reference-type semantics. Keeps the allocator busy in a
        // different code path than Hashtable.
        var d = new Dictionary<object, object>();
        for (int j = 0; j < itemsPerIteration / 4; j++)
        {
            d[j] = (long)j * 17;  // both box
        }

        // Occasional forced collection to make gen promotions visible in the trace — every 5th iteration we nudge the GC so the
        // viewer's heap-composition stacked area shows an actual staircase rather than monotone growth. Real apps never need this;
        // for a gauge demo it makes the signal self-explanatory.
        if (iteration % 5 == 0)
        {
            GC.Collect(generation: 1, mode: GCCollectionMode.Default, blocking: false);
        }

        // Prevent the JIT from eliding any of the above as dead stores. These references escape via a side-effecting GC.KeepAlive.
        GC.KeepAlive(ht);
        GC.KeepAlive(strings);
        GC.KeepAlive(loh);
        GC.KeepAlive(small);
        GC.KeepAlive(d);
    }

    private static void ReadBurst(DatabaseEngine dbe, StageConfig cfg)
    {
        var passes = cfg.Passes > 0 ? cfg.Passes : 3;
        // Per-batch transaction size. A single transaction epoch-protects every page it touches — not just component pages but also
        // revision chains and index pages. For a Versioned blob that can easily be 3–5 pages per entity, so 300 reads × ~4 pages ≈
        // 1200 pages ≈ 9.4 MB, well under the 16 MB default cache. Too-big batches trigger PageCacheBackpressureTimeoutException when
        // every cache slot is epoch-protected and nothing can be evicted.
        const int readsPerTx = 300;

        var sw = Stopwatch.StartNew();
        long hits = 0;
        long checksum = 0;

        // First, snapshot the entity IDs once. The view's entity set doesn't change across read passes so we only need one enumeration.
        var entityIds = new List<long>();
        {
            using var snapTx = dbe.CreateQuickTransaction();
            var snapView = snapTx.Query<BlobArch>().ToView();
            var e = snapView.GetEntityEnumerator();
            while (e.MoveNext())
            {
                entityIds.Add((long)e.Current.RawValue);
            }
        }

        for (int p = 0; p < passes; p++)
        {
            // Walk the entity-ID list in fixed-size sub-batches. Each sub-batch runs inside its own QuickTransaction so the epoch
            // pin releases at transaction dispose, letting the cache evict earlier batches' pages and bring in the next batch's.
            // This is what actually drives the PageCacheDiskRead / DiskReadCompleted traffic on the trace.
            for (int start = 0; start < entityIds.Count; start += readsPerTx)
            {
                var end = Math.Min(start + readsPerTx, entityIds.Count);
                using var tx = dbe.CreateQuickTransaction();
                for (int i = start; i < end; i++)
                {
                    if (tx.QueryRead<Blob>(entityIds[i], out var blob))
                    {
                        checksum ^= blob.Id ^ BitConverter.SingleToInt32Bits(blob.F00);
                        hits++;
                    }
                }
            }
        }

        sw.Stop();
        Console.WriteLine($"  [ok]   readBurst: {passes} passes × {entityIds.Count:N0} entities = {hits:N0} reads in {sw.Elapsed.TotalMilliseconds:F1} ms (checksum={checksum:X8})");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Readback — reopen the .typhon-trace we just wrote and print a kind/threadSlot histogram so the user can see at
    // a glance what the profiler captured. Mirrors the pattern AntHill.ProfileRunner uses.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static void PrintTraceSummary(string tracePath)
    {
        if (!File.Exists(tracePath))
        {
            Console.WriteLine($"!! trace file not found: {tracePath}");
            return;
        }

        var size = new FileInfo(tracePath).Length;
        Console.WriteLine($"── Trace readback ({size:N0} bytes) ──────────────────────────");

        try
        {
            using var stream = File.OpenRead(tracePath);
            using var reader = new TraceFileReader(stream);
            reader.ReadHeader();
            reader.ReadSystemDefinitions();
            reader.ReadArchetypes();
            reader.ReadComponentTypes();
            reader.ReadPhases();
            reader.ReadStaticStructures();

            var kindCounts = new SortedDictionary<TraceEventKind, int>();
            var threadSlotCounts = new SortedDictionary<int, int>();
            var threadNames = new SortedDictionary<int, string>();
            int total = 0;
            int blocks = 0;

            while (reader.ReadNextBlock(out var blockMem, out _))
            {
                blocks++;
                var records = blockMem.Span;
                var pos = 0;
                while (pos + TraceRecordHeader.CommonHeaderSize <= records.Length)
                {
                    var recSize = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
                    if (recSize < TraceRecordHeader.CommonHeaderSize || pos + recSize > records.Length) break;

                    var record = records.Slice(pos, recSize);
                    var kind = (TraceEventKind)record[2];
                    var threadSlot = record[3];

                    total++;
                    kindCounts.TryGetValue(kind, out var kc);
                    kindCounts[kind] = kc + 1;
                    threadSlotCounts.TryGetValue(threadSlot, out var sc);
                    threadSlotCounts[threadSlot] = sc + 1;

                    // Decode ThreadInfo inline so we can surface the captured names in the summary — this is the single source of truth
                    // for "what does lane N show up as in the viewer?" Useful for eyeballing whether thread naming is wired up correctly.
                    if (kind == TraceEventKind.ThreadInfo && recSize >= TraceRecordHeader.CommonHeaderSize + 6)
                    {
                        var payload = record[TraceRecordHeader.CommonHeaderSize..];
                        var nameByteCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
                        if (6 + nameByteCount <= payload.Length)
                        {
                            var name = nameByteCount > 0 ? System.Text.Encoding.UTF8.GetString(payload.Slice(6, nameByteCount)) : "(unnamed)";
                            threadNames[threadSlot] = name;
                        }
                    }

                    pos += recSize;
                }
            }

            Console.WriteLine($"  blocks:       {blocks}");
            Console.WriteLine($"  records:      {total:N0}");
            Console.WriteLine($"  thread slots: {string.Join(", ", threadSlotCounts.Select(kv => $"slot{kv.Key}={kv.Value}"))}");
            if (threadNames.Count > 0)
            {
                Console.WriteLine($"  thread names:");
                foreach (var kv in threadNames)
                {
                    Console.WriteLine($"    slot{kv.Key}: {kv.Value}");
                }
            }
            Console.WriteLine($"  kinds:");
            foreach (var kv in kindCounts)
            {
                Console.WriteLine($"    {kv.Key,-32} {kv.Value,10:N0}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!! readback failed: {ex.Message}");
        }
    }
}
