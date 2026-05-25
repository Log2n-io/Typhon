using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Typhon.Engine.Internals; // QueryPlanEvent / phase span ref structs (internal `[TraceEvent]` encoders — #376 Stage-3 4A)
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Workbench.Fixtures;

/// <summary>
/// Build minimal valid (or deliberately malformed) <c>.typhon-trace</c> files on demand for tests
/// and dev scenarios. Keeps binary fixtures out of git — each caller writes into a temp dir and
/// cleans up afterwards (the <c>WorkbenchFactory</c> per-test temp dir; the DEBUG-gated
/// <c>/api/fixtures/trace</c> endpoint uses the Workbench's local app-data fixtures folder).
///
/// The "minimal valid" path exercises the same <see cref="TraceFileWriter"/> code the real profiler
/// uses, so serialisation bugs surface here too (not just in production). Malformed variants (bad
/// magic, truncated header) drive the error-path tests for <c>TraceSessionRuntime</c>.
/// </summary>
public static class TraceFixtureBuilder
{
    /// <summary>Common record header size in bytes (mirrors <c>TraceRecordHeader.CommonHeaderSize</c>).</summary>
    private const int CommonHeaderSize = 12;

    /// <summary>
    /// Build a valid minimal trace with <paramref name="tickCount"/> ticks. Each tick emits a
    /// <c>TickStart</c> + <c>TickEnd</c> pair plus <paramref name="instantsPerTick"/> generic
    /// instant records so the decoder has something to chew on.
    ///
    /// Returns the absolute path on disk. Caller is responsible for deletion.
    /// </summary>
    public static string BuildMinimalTrace(string directory, int tickCount = 3, int instantsPerTick = 5)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-{Guid.NewGuid():N}.typhon-trace");

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);

        writer.WriteHeader(in DefaultHeader);
        writer.WriteSystemDefinitions([]);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes([]);
        writer.WriteTracks([]);
        writer.WriteDags([]);
        writer.WriteEmptyStaticStructures();

        // One block containing every record. Record sizes follow the production codec:
        //   TickStart = 12 B (header only)
        //   Instant   = 20 B (header + i32 nameId + i32 payload)
        //   TickEnd   = 14 B (header + u8 overloadLevel + u8 tickMultiplier)
        // Worst-case budget @ 1000 ticks × (12 + 5*20 + 14) = 126 KB — under the 256 KiB block cap.
        const int tickStartSize = CommonHeaderSize;
        const int instantSize = CommonHeaderSize + 8;
        const int tickEndSize = CommonHeaderSize + 2;
        var totalRecords = tickCount * (2 + instantsPerTick);
        var blockSize = tickCount * (tickStartSize + instantsPerTick * instantSize + tickEndSize);
        var block = new byte[blockSize];
        long ts = 100; // non-zero start timestamp — builder rejects firstTs <= 0

        var offset = 0;
        for (var tick = 0; tick < tickCount; tick++)
        {
            WriteRecordHeader(block.AsSpan(offset), tickStartSize, TraceEventKind.TickStart, ts);
            offset += tickStartSize;
            ts++;

            for (var i = 0; i < instantsPerTick; i++)
            {
                WriteRecordHeader(block.AsSpan(offset), instantSize, TraceEventKind.Instant, ts);
                // Payload stays zero (nameId=0, payload=0) — decoder tolerates it.
                offset += instantSize;
                ts++;
            }

            WriteRecordHeader(block.AsSpan(offset), tickEndSize, TraceEventKind.TickEnd, ts);
            block[offset + CommonHeaderSize] = 0;     // overloadLevel
            block[offset + CommonHeaderSize + 1] = 1; // tickMultiplier
            offset += tickEndSize;
            ts++;
        }

        writer.WriteRecords(block, totalRecords);
        writer.WriteSpanNames(new Dictionary<int, string>());
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Build a minimal trace whose system table carries RFC 07 access declarations and a Phases section.
    /// Exercises the v6 wire path end-to-end — used by topology / who-writes / who-reads endpoint tests.
    /// </summary>
    public static string BuildTraceWithAccessDeclarations(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-rfc07-{Guid.NewGuid():N}.typhon-trace");

        var systems = new[]
        {
            new SystemDefinitionRecord
            {
                Index = 0,
                Name = "Movement",
                Type = 0,
                Priority = 0,
                IsParallel = false,
                TierFilter = 0x0F,
                Predecessors = [],
                Successors = [1],
                PhaseName = "Simulation",
                IsExclusivePhase = false,
                Reads = ["Game.Velocity"],
                Writes = ["Game.Position"],
                ReadsEvents = ["Damage"],
                WritesResources = ["world.physics"],
            },
            new SystemDefinitionRecord
            {
                Index = 1,
                Name = "Damage",
                Type = 0,
                Priority = 0,
                IsParallel = false,
                TierFilter = 0x0F,
                Predecessors = [0],
                Successors = [],
                PhaseName = "Simulation",
                IsExclusivePhase = true,
                ReadsSnapshot = ["Game.Position"],
                Writes = ["Game.Health"],
                WritesEvents = ["Death"],
            },
        };

        var components = new[]
        {
            new ComponentTypeRecord { ComponentTypeId = 1, Name = "Game.Position" },
            new ComponentTypeRecord { ComponentTypeId = 2, Name = "Game.Velocity" },
            new ComponentTypeRecord { ComponentTypeId = 3, Name = "Game.Health" },
        };

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        writer.WriteHeader(in DefaultHeader);
        writer.WriteSystemDefinitions(systems);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes(components);
        writer.WriteTracks([new TrackRecord { Name = "Main", OrderIndex = 0, Tags = [] }]);
        writer.WriteDags([new DagRecord { Id = 0, Name = "Main", TrackIndex = 0, PhaseNames = ["Input", "Simulation", "Output"] }]);
        writer.WriteEmptyStaticStructures();

        // Same record body as BuildMinimalTrace — 3 ticks of TickStart + Instant + TickEnd. Anything thinner
        // can prevent the cache builder from producing a non-empty manifest, which leaves /topology stuck at 202.
        const int tickStartSize = CommonHeaderSize;
        const int instantSize = CommonHeaderSize + 8;
        const int tickEndSize = CommonHeaderSize + 2;
        const int tickCount = 3;
        const int instantsPerTick = 2;
        var totalRecords = tickCount * (2 + instantsPerTick);
        var blockSize = tickCount * (tickStartSize + instantsPerTick * instantSize + tickEndSize);
        var block = new byte[blockSize];
        long ts = 100;
        var offset = 0;
        for (var t = 0; t < tickCount; t++)
        {
            WriteRecordHeader(block.AsSpan(offset), tickStartSize, TraceEventKind.TickStart, ts);
            offset += tickStartSize;
            ts++;
            for (var i = 0; i < instantsPerTick; i++)
            {
                WriteRecordHeader(block.AsSpan(offset), instantSize, TraceEventKind.Instant, ts);
                offset += instantSize;
                ts++;
            }
            WriteRecordHeader(block.AsSpan(offset), tickEndSize, TraceEventKind.TickEnd, ts);
            block[offset + CommonHeaderSize] = 0;
            block[offset + CommonHeaderSize + 1] = 1;
            offset += tickEndSize;
            ts++;
        }
        writer.WriteRecords(block, totalRecords);
        writer.WriteSpanNames(new Dictionary<int, string>());
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Build a minimal trace exercising the #354 Track → DAG hierarchy: three ordered tracks (Engine-Pre and Engine-Post
    /// tagged <c>engine</c>, a Public track in between), a user DAG in the Public track and a Fence DAG in Engine-Post,
    /// with each system stamped with its <c>DagId</c>. Used by the topology hierarchy round-trip test.
    /// </summary>
    public static string BuildTraceWithTrackHierarchy(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-trackdag-{Guid.NewGuid():N}.typhon-trace");

        // Two systems in the Public-track "World" DAG (DagId 0), two in the Engine-Post "Fence" DAG (DagId 1).
        var systems = new[]
        {
            new SystemDefinitionRecord
            {
                Index = 0, Name = "Movement", Type = 0, Priority = 0, IsParallel = false, TierFilter = 0x0F,
                Predecessors = [], Successors = [1], PhaseName = "Simulation", IsExclusivePhase = false, DagId = 0,
            },
            new SystemDefinitionRecord
            {
                Index = 1, Name = "Render", Type = 0, Priority = 0, IsParallel = false, TierFilter = 0x0F,
                Predecessors = [0], Successors = [], PhaseName = "Render", IsExclusivePhase = false, DagId = 0,
            },
            new SystemDefinitionRecord
            {
                Index = 2, Name = "FencePrep", Type = 0, Priority = 0, IsParallel = false, TierFilter = 0x0F,
                Predecessors = [], Successors = [3], PhaseName = "Default", IsExclusivePhase = false, DagId = 1,
            },
            new SystemDefinitionRecord
            {
                Index = 3, Name = "FenceFinalize", Type = 0, Priority = 0, IsParallel = false, TierFilter = 0x0F,
                Predecessors = [2], Successors = [], PhaseName = "Default", IsExclusivePhase = false, DagId = 1,
            },
        };

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        writer.WriteHeader(in DefaultHeader);
        writer.WriteSystemDefinitions(systems);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes([]);
        writer.WriteTracks(
        [
            new TrackRecord { Name = "Engine-Pre",  OrderIndex = 0, Tags = ["engine"] },
            new TrackRecord { Name = "Public",      OrderIndex = 1, Tags = [] },
            new TrackRecord { Name = "Engine-Post", OrderIndex = 2, Tags = ["engine"] },
        ]);
        writer.WriteDags(
        [
            new DagRecord { Id = 0, Name = "World", TrackIndex = 1, PhaseNames = ["Input", "Simulation", "Render"] },
            new DagRecord { Id = 1, Name = "Fence", TrackIndex = 2, PhaseNames = ["Default"] },
        ]);
        writer.WriteEmptyStaticStructures();

        // Same 3-tick record body as BuildTraceWithAccessDeclarations — enough for the cache builder to emit a manifest.
        const int tickStartSize = CommonHeaderSize;
        const int instantSize = CommonHeaderSize + 8;
        const int tickEndSize = CommonHeaderSize + 2;
        const int tickCount = 3;
        const int instantsPerTick = 2;
        var totalRecords = tickCount * (2 + instantsPerTick);
        var blockSize = tickCount * (tickStartSize + instantsPerTick * instantSize + tickEndSize);
        var block = new byte[blockSize];
        long ts = 100;
        var offset = 0;
        for (var t = 0; t < tickCount; t++)
        {
            WriteRecordHeader(block.AsSpan(offset), tickStartSize, TraceEventKind.TickStart, ts);
            offset += tickStartSize;
            ts++;
            for (var i = 0; i < instantsPerTick; i++)
            {
                WriteRecordHeader(block.AsSpan(offset), instantSize, TraceEventKind.Instant, ts);
                offset += instantSize;
                ts++;
            }
            WriteRecordHeader(block.AsSpan(offset), tickEndSize, TraceEventKind.TickEnd, ts);
            block[offset + CommonHeaderSize] = 0;
            block[offset + CommonHeaderSize + 1] = 1;
            offset += tickEndSize;
            ts++;
        }
        writer.WriteRecords(block, totalRecords);
        writer.WriteSpanNames(new Dictionary<int, string>());
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Build a trace that extends <see cref="BuildTraceWithAccessDeclarations"/> with two registered archetypes
    /// and a handful of <see cref="TraceEventKind.SchedulerSystemArchetype"/> span events per tick — enough to
    /// drive the Workbench Data Flow timeline's per-tick bar rendering. Used by the cross-panel Playwright
    /// canary's bar-click + hover cases (#327 Phase D acceptance), which need a tick with at least one
    /// (system, archetype) touch row to have a clickable bar in the canvas.
    /// </summary>
    public static string BuildTraceWithArchetypeTouches(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-archtouches-{Guid.NewGuid():N}.typhon-trace");

        var systems = new[]
        {
            new SystemDefinitionRecord
            {
                Index = 0,
                Name = "Movement",
                Type = 0,
                Priority = 0,
                IsParallel = true,
                TierFilter = 0x0F,
                Predecessors = [],
                Successors = [1],
                PhaseName = "Simulation",
                IsExclusivePhase = false,
                Reads = ["Game.Velocity"],
                Writes = ["Game.Position"],
                ReadsEvents = ["Damage"],
                WritesResources = ["world.physics"],
            },
            new SystemDefinitionRecord
            {
                Index = 1,
                Name = "Damage",
                Type = 0,
                Priority = 0,
                IsParallel = true,
                TierFilter = 0x0F,
                Predecessors = [0],
                Successors = [],
                PhaseName = "Simulation",
                IsExclusivePhase = true,
                ReadsSnapshot = ["Game.Position"],
                Writes = ["Game.Health"],
                WritesEvents = ["Death"],
            },
        };

        var archetypes = new[]
        {
            new ArchetypeRecord { ArchetypeId = 100, Name = "Player" },
            new ArchetypeRecord { ArchetypeId = 101, Name = "Enemy" },
        };
        var components = new[]
        {
            new ComponentTypeRecord { ComponentTypeId = 1, Name = "Game.Position" },
            new ComponentTypeRecord { ComponentTypeId = 2, Name = "Game.Velocity" },
            new ComponentTypeRecord { ComponentTypeId = 3, Name = "Game.Health" },
        };

        // Rich archetype + component definitions so the trace's L4 fan-out has data to enumerate. Slots
        // mirror the systems' declared writes — Movement writes Position, Damage writes Health — so the
        // Access Matrix and Data Flow tooltip both light up convincingly when the bar is clicked.
        var componentDefs = new[]
        {
            new ComponentDefinitionRecord { ComponentTypeId = 1, Name = "Game.Position", Revision = 1, StorageMode = 0, AllowMultiple = false, ComponentStorageSize = 16, ComponentStorageOverhead = 0, ComponentStorageTotalSize = 16 },
            new ComponentDefinitionRecord { ComponentTypeId = 2, Name = "Game.Velocity", Revision = 1, StorageMode = 0, AllowMultiple = false, ComponentStorageSize = 16, ComponentStorageOverhead = 0, ComponentStorageTotalSize = 16 },
            new ComponentDefinitionRecord { ComponentTypeId = 3, Name = "Game.Health",   Revision = 1, StorageMode = 0, AllowMultiple = false, ComponentStorageSize = 8,  ComponentStorageOverhead = 0, ComponentStorageTotalSize = 8  },
        };
        var archetypeDefs = new[]
        {
            new ArchetypeDefinitionRecord { ArchetypeId = 100, Name = "Player", Revision = 1, ComponentCount = 2, ComponentTypeIds = [1, 2] },
            new ArchetypeDefinitionRecord { ArchetypeId = 101, Name = "Enemy",  Revision = 1, ComponentCount = 2, ComponentTypeIds = [1, 3] },
        };

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        writer.WriteHeader(in DefaultHeader);
        writer.WriteSystemDefinitions(systems);
        writer.WriteArchetypes(archetypes);
        writer.WriteComponentTypes(components);
        writer.WriteTracks([new TrackRecord { Name = "Main", OrderIndex = 0, Tags = [] }]);
        writer.WriteDags([new DagRecord { Id = 0, Name = "Main", TrackIndex = 0, PhaseNames = ["Input", "Simulation", "Output"] }]);
        // Replace the "everything-empty" static-structures block with the rich definitions above. The cache
        // builder folds these into ProfilerMetadataDto.archetypes / componentTypes for the topology endpoint.
        writer.WriteComponentDefinitions(componentDefs);
        writer.WriteArchetypeDefinitions(archetypeDefs);
        writer.WriteIndexCatalog([]);
        writer.WriteRuntimeConfig(new RuntimeConfigRecord { BaseTickRate = 1_000, WorkerCount = 1 });
        writer.WriteEventQueueCatalog([]);
        writer.WriteResourceGraphSnapshot([]);

        // Per-tick record layout: TickStart + 2× SchedulerSystemArchetype span events + TickEnd. Span events
        // are 49 B each (12 B common + 25 B span ext + 12 B payload, no trace context, no source location).
        // Drives both: SystemArchetypeTouchSummary[] (one row per (sys, arch) per tick) and the dominant-tick
        // selection (TickEnd defines tick wall-clock; we space `ts` so each tick takes 100 µs of timestamp space).
        const int tickStartSize = CommonHeaderSize;
        const int spanArchSize = CommonHeaderSize + 25 + 12; // 49 B
        const int tickEndSize = CommonHeaderSize + 2;
        const int tickCount = 3;
        const int eventsPerTick = 2;

        var totalRecords = tickCount * (1 + eventsPerTick + 1);
        var blockSize = tickCount * (tickStartSize + eventsPerTick * spanArchSize + tickEndSize);
        var block = new byte[blockSize];
        long ts = 100;
        var offset = 0;
        for (var t = 0; t < tickCount; t++)
        {
            // TickStart anchors the tick's wall-clock. The cache builder uses it as `tickStartTs` to compute
            // SystemTickSummary.StartUs/EndUs by subtracting from each system's first/last chunk timestamps.
            // We don't emit chunk events here, so SystemTickSummary won't materialize — the e2e tests only need
            // SystemArchetypeTouchSummary rows for the bar render path.
            WriteRecordHeader(block.AsSpan(offset), tickStartSize, TraceEventKind.TickStart, ts);
            offset += tickStartSize;
            ts++;

            // (Movement, Player) and (Damage, Enemy) — one event per pair per tick.
            WriteSchedulerSystemArchetypeEvent(block.AsSpan(offset, spanArchSize), startTs: ts, durationTicks: 30,
                spanId: 100UL + (ulong)t * 2, systemIdx: 0, archetypeId: 100, entityCount: 8 + t, chunkCount: 2);
            offset += spanArchSize;
            ts += 30;
            WriteSchedulerSystemArchetypeEvent(block.AsSpan(offset, spanArchSize), startTs: ts, durationTicks: 20,
                spanId: 101UL + (ulong)t * 2, systemIdx: 1, archetypeId: 101, entityCount: 4 + t, chunkCount: 1);
            offset += spanArchSize;
            ts += 20;

            WriteRecordHeader(block.AsSpan(offset), tickEndSize, TraceEventKind.TickEnd, ts);
            block[offset + CommonHeaderSize] = 0;     // overloadLevel
            block[offset + CommonHeaderSize + 1] = 1; // tickMultiplier
            offset += tickEndSize;
            ts++;
        }

        writer.WriteRecords(block, totalRecords);
        writer.WriteSpanNames(new Dictionary<int, string>());
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Encode a single <see cref="TraceEventKind.SchedulerSystemArchetype"/> span event in-place. Layout matches
    /// <c>SchedulerSystemArchetypeEventCodec.Decode</c>: 12 B common header + 25 B span extension (no trace
    /// context, no source location) + 12 B payload (u16 sysIdx, u16 archId, i32 entities, i32 chunks).
    /// </summary>
    private static void WriteSchedulerSystemArchetypeEvent(Span<byte> dest, long startTs, long durationTicks, ulong spanId,
        ushort systemIdx, ushort archetypeId, int entityCount, int chunkCount)
    {
        // 49 B is the single-record size; caller is expected to pass exactly that slice.
        WriteRecordHeader(dest, dest.Length, TraceEventKind.SchedulerSystemArchetype, startTs);
        // Span extension at offset 12.
        BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(CommonHeaderSize, 8), durationTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(CommonHeaderSize + 8, 8), spanId);
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(CommonHeaderSize + 16, 8), 0UL); // parentSpanId
        dest[CommonHeaderSize + 24] = 0; // spanFlags — no trace context, no source location
        // Payload at offset 37.
        var payload = dest[(CommonHeaderSize + 25)..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, systemIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[2..], archetypeId);
        BinaryPrimitives.WriteInt32LittleEndian(payload[4..], entityCount);
        BinaryPrimitives.WriteInt32LittleEndian(payload[8..], chunkCount);
    }

    /// <summary>
    /// Build a trace whose ticks carry <see cref="TraceEventKind.ThreadContextSwitch"/> (kind 254) records — one
    /// ON-CPU slice each — so the Workbench off-CPU overlay has data to render. Each tick emits four slices on
    /// thread slot 0, spaced so three off-CPU GAPS fall between them; the gaps cross categories (QuantumEnd /
    /// SyncWait / Preempted) so the colour mapping is exercised too. Used by the off-CPU Playwright canary and
    /// the trace-mode round-trip server tests.
    /// </summary>
    public static string BuildTraceWithContextSwitches(string directory, int tickCount = 4)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-cswitch-{Guid.NewGuid():N}.typhon-trace");

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        writer.WriteHeader(in DefaultHeader);
        writer.WriteSystemDefinitions([]);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes([]);
        writer.WriteTracks([]);
        writer.WriteDags([]);
        writer.WriteEmptyStaticStructures();

        // Per-tick layout: TickStart + 4× ThreadContextSwitch (25 B each) + TickEnd. The four slices start at
        // tick-local offsets 0/20/40/60 (raw ticks) with a 2-tick ON-CPU duration, so the gaps [2,20], [22,40],
        // [42,60] become three off-CPU intervals. Wait reasons rotate across categories.
        const int tickStartSize = CommonHeaderSize;
        const int cswitchSize = CommonHeaderSize + 13;
        const int tickEndSize = CommonHeaderSize + 2;
        const int slicesPerTick = 4;
        byte[] waitReasons = [30, 7, 32, 0]; // QuantumEnd, SyncWait (WrExecutive), Preempted, SyncWait (Executive)

        var totalRecords = tickCount * (2 + slicesPerTick);
        var blockSize = tickCount * (tickStartSize + slicesPerTick * cswitchSize + tickEndSize);
        var block = new byte[blockSize];
        long ts = 100;
        var offset = 0;
        for (var t = 0; t < tickCount; t++)
        {
            WriteRecordHeader(block.AsSpan(offset), tickStartSize, TraceEventKind.TickStart, ts);
            offset += tickStartSize;
            var tickBaseTs = ts;
            ts++;

            for (var s = 0; s < slicesPerTick; s++)
            {
                var sliceTs = tickBaseTs + s * 20;
                WriteContextSwitchEvent(block.AsSpan(offset, cswitchSize), startTs: sliceTs, targetSlotIdx: 0,
                    processorNumber: (byte)(s & 3), waitReason: waitReasons[s], threadState: 5,
                    gettingIdle: false, durationQpc: 2, readyTimeQpc: (uint)(s + 1));
                offset += cswitchSize;
            }
            ts = tickBaseTs + slicesPerTick * 20;

            WriteRecordHeader(block.AsSpan(offset), tickEndSize, TraceEventKind.TickEnd, ts);
            block[offset + CommonHeaderSize] = 0;     // overloadLevel
            block[offset + CommonHeaderSize + 1] = 1; // tickMultiplier
            offset += tickEndSize;
            ts++;
        }

        writer.WriteRecords(block, totalRecords);
        writer.WriteSpanNames(new Dictionary<int, string>());
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Encode a single <see cref="TraceEventKind.ThreadContextSwitch"/> instant record in-place. Layout mirrors
    /// <c>ThreadContextSwitchEvent</c>: 12 B common header + 13 B payload (u8 targetSlotIdx, u8 processorNumber,
    /// u8 waitReason, u8 threadState, u8 gettingIdle, u32 durationQpc @+5, u32 readyTimeQpc @+9). No span extension.
    /// </summary>
    private static void WriteContextSwitchEvent(Span<byte> dest, long startTs, byte targetSlotIdx, byte processorNumber,
        byte waitReason, byte threadState, bool gettingIdle, uint durationQpc, uint readyTimeQpc)
    {
        WriteRecordHeader(dest, dest.Length, TraceEventKind.ThreadContextSwitch, startTs);
        var payload = dest[CommonHeaderSize..];
        payload[0] = targetSlotIdx;
        payload[1] = processorNumber;
        payload[2] = waitReason;
        payload[3] = threadState;
        payload[4] = (byte)(gettingIdle ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[5..], durationQpc);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[9..], readyTimeQpc);
    }

    /// <summary>
    /// Build a trace carrying a #351 <c>CpuSampleSection</c> trailer: a small FileTable, three resolved frame symbols
    /// (an <c>Ecs</c> engine frame, a <c>Storage</c> engine frame, and a source-less BCL frame), two interned stacks,
    /// and three samples (two Managed / on-CPU, one External / off-CPU). Drives the Phase-4 Call Tree endpoint tests.
    /// </summary>
    public static string BuildTraceWithCpuSamples(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-cpusamples-{Guid.NewGuid():N}.typhon-trace");

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        var header = DefaultHeader;
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions([]);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes([]);
        writer.WriteTracks([]);
        writer.WriteDags([]);
        writer.WriteEmptyStaticStructures();

        // Minimal record body — same shape as BuildMinimalTrace, enough for the cache build to complete.
        const int tickStartSize = CommonHeaderSize;
        const int instantSize = CommonHeaderSize + 8;
        const int tickEndSize = CommonHeaderSize + 2;
        const int tickCount = 3;
        const int instantsPerTick = 2;
        var totalRecords = tickCount * (2 + instantsPerTick);
        var block = new byte[tickCount * (tickStartSize + instantsPerTick * instantSize + tickEndSize)];
        long ts = 100;
        var offset = 0;
        for (var t = 0; t < tickCount; t++)
        {
            WriteRecordHeader(block.AsSpan(offset), tickStartSize, TraceEventKind.TickStart, ts);
            offset += tickStartSize;
            ts++;
            for (var i = 0; i < instantsPerTick; i++)
            {
                WriteRecordHeader(block.AsSpan(offset), instantSize, TraceEventKind.Instant, ts);
                offset += instantSize;
                ts++;
            }
            WriteRecordHeader(block.AsSpan(offset), tickEndSize, TraceEventKind.TickEnd, ts);
            block[offset + CommonHeaderSize] = 0;
            block[offset + CommonHeaderSize + 1] = 1;
            offset += tickEndSize;
            ts++;
        }
        writer.WriteRecords(block, totalRecords);
        writer.WriteSpanNames(new Dictionary<int, string>());

        // FileTable (+ empty source-location manifest) so CPU frame symbols resolve to paths.
        string[] files =
        [
            "src/Typhon.Engine/Ecs/MovementSystem.cs",
            "src/Typhon.Engine/Storage/PagedMmf.cs",
        ];
        var (fileTableOffset, manifestOffset) = writer.WriteSourceLocationManifest(files, []);

        CpuFrameSymbol[] frameSymbols =
        [
            new(0, 0, 42, "AntHill.MovementSystem.Execute"),       // Ecs, sourced
            new(1, 1, 100, "Typhon.Engine.Storage.PagedMmf.GetPage"), // Storage, sourced
            new(2, 0, 0, "System.Threading.Thread.Sleep"),          // BCL, no source (line 0)
        ];
        ushort[][] stacks =
        [
            [1, 0], // leaf PagedMmf.GetPage  ← MovementSystem.Execute (root)
            [2, 0], // leaf Thread.Sleep      ← MovementSystem.Execute (root)
        ];
        CpuSampleRecord[] samples =
        [
            new(1000, 0, 0, 0), // Managed, stack 0
            new(2000, 0, 0, 0), // Managed, stack 0
            new(3000, 0, 1, 1), // External, stack 1
        ];
        var cpuOffset = writer.WriteCpuSampleSection(samples, stacks, frameSymbols);

        header.FileTableOffset = fileTableOffset;
        header.SourceLocationManifestOffset = manifestOffset;
        header.CpuSampleSectionOffset = cpuOffset;
        writer.RewriteHeader(in header);
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Build a trace carrying query definitions + executions (#376 Stage-3 Phase 4A — the Query Analyzer prerequisite).
    /// Records two View query definitions over the seeded components, each with multiple executions of varying wall
    /// time and a Parse → Iterate → Filter → Count phase tree, plus a <c>QuerySourceStringTable</c> so <c>UserSource</c>
    /// resolves and an <c>OwnerSystemIdx</c> so the owning system resolves. Drives the catalog (ranked by
    /// <c>TotalWallNs</c> — definition #1 heavier than #2), the Executions tab, and the per-phase breakdown.
    ///
    /// <para>The execution/phase span tree is emitted via the engine's own <c>[TraceEvent]</c> encoders
    /// (<see cref="QueryPlanEvent"/> et al.) — faithful by construction, no hand-packed wire format. Each QueryPlan span
    /// is timestamped <b>before</b> its phase children so the time-sorted decode keeps the parent-before-child order
    /// <c>QueryCatalogBuilder</c> requires (a phase only attaches once its parent execution is mapped).</para>
    /// </summary>
    public static string BuildTraceWithQueries(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-queries-{Guid.NewGuid():N}.typhon-trace");

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        var header = DefaultHeader;
        writer.WriteHeader(in header);
        // Seed a system (OwnerSystemIdx → name) + two components (TargetComponentType → name).
        writer.WriteSystemDefinitions(
        [
            new SystemDefinitionRecord
            {
                Index = 0, Name = "Movement", Type = 0, Priority = 0, IsParallel = false, TierFilter = 0x0F,
                Predecessors = [], Successors = [], PhaseName = "Simulation", IsExclusivePhase = false, DagId = 0,
            },
        ]);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes(
        [
            new ComponentTypeRecord { ComponentTypeId = 1, Name = "Position" },
            new ComponentTypeRecord { ComponentTypeId = 2, Name = "AABB" },
        ]);
        writer.WriteTracks([]);
        writer.WriteDags([]);
        writer.WriteEmptyStaticStructures();

        // Source string table (sentinel @ 0). Definition source file/method ids reference these → UserSource = File:Line:Method.
        string[] queryStrings = [string.Empty, "src/Game/Queries.cs", "FindByPosition", "RangeAabb"];
        const ushort srcFileId = 1, methodAId = 2, methodBId = 3;

        // One evaluator each (fieldIdx, op, reserved) — op 4 = ">=", op 3 = ">". Packed 4 B/entry.
        static byte[] OneEvaluator(ushort fieldIdx, byte op) => [(byte)(fieldIdx & 0xFF), (byte)(fieldIdx >> 8), op, 0];

        var block = new byte[64 * 1024];
        var offset = 0;
        var recordCount = 0;
        long ts = 100;
        ulong nextSpanId = 1;

        void TickStart()
        {
            WriteRecordHeader(block.AsSpan(offset), CommonHeaderSize, TraceEventKind.TickStart, ts);
            offset += CommonHeaderSize; ts += 1; recordCount++;
        }
        void TickEnd()
        {
            WriteRecordHeader(block.AsSpan(offset), CommonHeaderSize + 2, TraceEventKind.TickEnd, ts);
            block[offset + CommonHeaderSize] = 0;     // overloadLevel
            block[offset + CommonHeaderSize + 1] = 1; // tickMultiplier
            offset += CommonHeaderSize + 2; ts += 1; recordCount++;
        }
        void Describe(byte kind, uint localId, ushort target, ushort methodId, ReadOnlySpan<byte> evaluators)
        {
            QueryDefinitionDescribeEventCodec.Write(block.AsSpan(offset), threadSlot: 0, timestamp: ts, kind: kind, localId: localId,
                targetComponentType: target, primaryIndexFieldIdx: -1, sortFieldIdx: -1, sortDescending: 0,
                definitionSourceFileId: srcFileId, definitionSourceLine: 42, definitionSourceMethodId: methodId,
                evaluators: evaluators, fieldDependencies: ReadOnlySpan<byte>.Empty, out var w);
            offset += w; ts += 1; recordCount++;
        }
        // Emit one execution = QueryPlan span (earliest ts) + QueryArgs + Parse/Iterate/Filter/Count phase children.
        void Execution(byte kind, uint localId, long durTicks, int rowsScanned, int rowsReturned, int rejected)
        {
            var planTs = ts;
            var planSpanId = nextSpanId++;
            var plan = new QueryPlanEvent
            {
                Header = new TraceSpanHeader { ThreadSlot = 0, StartTimestamp = planTs, SpanId = planSpanId, ParentSpanId = 0 },
                EvaluatorCount = 1, IndexFieldIdx = 0, RangeMin = 0, RangeMax = 100,
                QueryInstanceKind = kind, QueryInstanceLocalId = localId,
                ExecutionSourceFileId = 0, ExecutionSourceLine = 0, ExecutionSourceMethodId = 0,
                OwnerSystemIdx = 0, // the seeded "Movement" system
            };
            plan.EncodeTo(block.AsSpan(offset), planTs + durTicks, out var pw);
            offset += pw; recordCount++;

            // QueryArgs (1 threshold) — attaches to the latest execution on this thread.
            Span<byte> thresholds = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(thresholds, 5L);
            QueryArgsEventCodec.Write(block.AsSpan(offset), threadSlot: 0, timestamp: planTs + 1, thresholds: thresholds, out var aw);
            offset += aw; recordCount++;

            // Phase children (parentSpanId = the QueryPlan span) — each timestamped after the plan, inside its window,
            // so the time-sorted decode keeps parent-before-child. Inlined (the events are ref structs → can't be
            // passed through a generic helper without `allows ref struct`).
            const long phaseDur = 10;
            var parse = new QueryParseEvent
            {
                Header = new TraceSpanHeader { ThreadSlot = 0, StartTimestamp = planTs + 2, SpanId = nextSpanId++, ParentSpanId = planSpanId },
                PredicateCount = 1, BranchCount = 1,
            };
            parse.EncodeTo(block.AsSpan(offset), planTs + 2 + phaseDur, out var w1); offset += w1; recordCount++;

            var iterate = new QueryExecuteIterateEvent
            {
                Header = new TraceSpanHeader { ThreadSlot = 0, StartTimestamp = planTs + 3, SpanId = nextSpanId++, ParentSpanId = planSpanId },
                ChunkCount = 1, EntryCount = rowsScanned,
            };
            iterate.EncodeTo(block.AsSpan(offset), planTs + 3 + phaseDur, out var w2); offset += w2; recordCount++;

            var filter = new QueryExecuteFilterEvent
            {
                Header = new TraceSpanHeader { ThreadSlot = 0, StartTimestamp = planTs + 4, SpanId = nextSpanId++, ParentSpanId = planSpanId },
                FilterCount = 1, RejectedCount = rejected,
            };
            filter.EncodeTo(block.AsSpan(offset), planTs + 4 + phaseDur, out var w3); offset += w3; recordCount++;

            var count = new QueryCountEvent
            {
                Header = new TraceSpanHeader { ThreadSlot = 0, StartTimestamp = planTs + 5, SpanId = nextSpanId++, ParentSpanId = planSpanId },
                ResultCount = rowsReturned,
            };
            count.EncodeTo(block.AsSpan(offset), planTs + 5 + phaseDur, out var w4); offset += w4; recordCount++;

            ts = planTs + durTicks + 50; // advance past this execution's window (+ gap) for the next one
        }

        // Tick 1 — both definitions + their first execution. (freq 10 MHz → 1 µs = 10 ticks.)
        TickStart();
        Describe(0, 1, target: 1, methodId: methodAId, OneEvaluator(0, 4)); // View #1 on Position, X >= ?
        Describe(0, 2, target: 2, methodId: methodBId, OneEvaluator(1, 3)); // View #2 on AABB, Y > ?
        Execution(0, 1, durTicks: 500, rowsScanned: 100, rowsReturned: 20, rejected: 80); // #1 exec a = 50 µs
        Execution(0, 2, durTicks: 200, rowsScanned: 40, rowsReturned: 30, rejected: 10);  // #2 exec a = 20 µs
        TickEnd();

        // Tick 2 — second execution of each, longer, so percentiles/totals differ and #1 outranks #2.
        TickStart();
        Execution(0, 1, durTicks: 900, rowsScanned: 200, rowsReturned: 25, rejected: 175); // #1 exec b = 90 µs → total 140 µs
        Execution(0, 2, durTicks: 400, rowsScanned: 60, rowsReturned: 35, rejected: 25);    // #2 exec b = 40 µs → total 60 µs
        TickEnd();

        // Tick 3 — empty trailer tick (matches the other fixtures' 3-tick shape).
        TickStart();
        TickEnd();

        writer.WriteRecords(block.AsSpan(0, offset), recordCount);
        writer.WriteSpanNames(new Dictionary<int, string>());

        var qsstOffset = writer.WriteQuerySourceStringTable(queryStrings);
        header.QuerySourceStringTableOffset = qsstOffset;
        writer.RewriteHeader(in header);
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Build a trace for #351 Phase 5 span-kind scoping: the record body carries two <see cref="TraceEventKind.SchedulerSystemArchetype"/>
    /// span records (kind 245) at QPC windows <c>[1000, 1500)</c> and <c>[3000, 3500)</c>, and the <c>CpuSampleSection</c>
    /// carries three samples — two inside those windows (qpc 1200, 3200) and one between them (qpc 2000). A
    /// <c>calltree</c> request scoped to span kind 245 must fold exactly the two in-window samples.
    /// </summary>
    public static string BuildTraceWithScopableCpuSamples(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-scopable-cpusamples-{Guid.NewGuid():N}.typhon-trace");

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        var header = DefaultHeader;
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions([]);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes([]);
        writer.WriteTracks([]);
        writer.WriteDags([]);
        writer.WriteEmptyStaticStructures();

        // One tick: TickStart + two SchedulerSystemArchetype spans + TickEnd. Span A → [1000, 1500), span B → [3000, 3500).
        const int tickStartSize = CommonHeaderSize;
        const int spanArchSize = CommonHeaderSize + 25 + 12; // 49 B
        const int tickEndSize = CommonHeaderSize + 2;
        const int totalRecords = 1 + 2 + 1;
        var block = new byte[tickStartSize + 2 * spanArchSize + tickEndSize];
        var offset = 0;
        WriteRecordHeader(block.AsSpan(offset), tickStartSize, TraceEventKind.TickStart, 100);
        offset += tickStartSize;
        WriteSchedulerSystemArchetypeEvent(block.AsSpan(offset, spanArchSize), startTs: 1000, durationTicks: 500,
            spanId: 1, systemIdx: 0, archetypeId: 100, entityCount: 8, chunkCount: 1);
        offset += spanArchSize;
        WriteSchedulerSystemArchetypeEvent(block.AsSpan(offset, spanArchSize), startTs: 3000, durationTicks: 500,
            spanId: 2, systemIdx: 0, archetypeId: 100, entityCount: 8, chunkCount: 1);
        offset += spanArchSize;
        WriteRecordHeader(block.AsSpan(offset), tickEndSize, TraceEventKind.TickEnd, 4000);
        block[offset + CommonHeaderSize] = 0;
        block[offset + CommonHeaderSize + 1] = 1;

        writer.WriteRecords(block, totalRecords);
        writer.WriteSpanNames(new Dictionary<int, string>());

        string[] files = ["src/Typhon.Engine/Ecs/MovementSystem.cs"];
        var (fileTableOffset, manifestOffset) = writer.WriteSourceLocationManifest(files, []);

        CpuFrameSymbol[] frameSymbols = [new(0, 0, 42, "AntHill.MovementSystem.Execute")];
        ushort[][] stacks = [[0]];
        CpuSampleRecord[] samples =
        [
            new(1200, 0, 0, 0), // inside span A [1000, 1500)
            new(2000, 0, 0, 0), // between the spans — out of any span-kind window
            new(3200, 0, 0, 0), // inside span B [3000, 3500)
        ];
        var cpuOffset = writer.WriteCpuSampleSection(samples, stacks, frameSymbols);

        header.FileTableOffset = fileTableOffset;
        header.SourceLocationManifestOffset = manifestOffset;
        header.CpuSampleSectionOffset = cpuOffset;
        writer.RewriteHeader(in header);
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Build a trace whose #351 <c>CpuSampleSection</c> carries one very deep call stack (<paramref name="depth"/>
    /// frames). Folding it produces a <paramref name="depth"/>-deep call tree — past System.Text.Json's nesting
    /// limit had the wire form been a nested-object tree. Drives the Phase-4 deep-tree serialization regression test.
    /// </summary>
    public static string BuildTraceWithDeepCpuStack(string directory, int depth = 50)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fixture-deepcpustack-{Guid.NewGuid():N}.typhon-trace");

        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        var header = DefaultHeader;
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions([]);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes([]);
        writer.WriteTracks([]);
        writer.WriteDags([]);
        writer.WriteEmptyStaticStructures();

        // Minimal record body so the cache build completes.
        const int tickStartSize = CommonHeaderSize;
        const int instantSize = CommonHeaderSize + 8;
        const int tickEndSize = CommonHeaderSize + 2;
        const int tickCount = 3;
        const int instantsPerTick = 2;
        var totalRecords = tickCount * (2 + instantsPerTick);
        var block = new byte[tickCount * (tickStartSize + instantsPerTick * instantSize + tickEndSize)];
        long ts = 100;
        var offset = 0;
        for (var t = 0; t < tickCount; t++)
        {
            WriteRecordHeader(block.AsSpan(offset), tickStartSize, TraceEventKind.TickStart, ts);
            offset += tickStartSize;
            ts++;
            for (var i = 0; i < instantsPerTick; i++)
            {
                WriteRecordHeader(block.AsSpan(offset), instantSize, TraceEventKind.Instant, ts);
                offset += instantSize;
                ts++;
            }
            WriteRecordHeader(block.AsSpan(offset), tickEndSize, TraceEventKind.TickEnd, ts);
            block[offset + CommonHeaderSize] = 0;
            block[offset + CommonHeaderSize + 1] = 1;
            offset += tickEndSize;
            ts++;
        }
        writer.WriteRecords(block, totalRecords);
        writer.WriteSpanNames(new Dictionary<int, string>());

        // One interned stack, leaf-first: frame 0 is the leaf, frame depth-1 is the stack root.
        var frameSymbols = new CpuFrameSymbol[depth];
        var stack = new ushort[depth];
        for (var i = 0; i < depth; i++)
        {
            frameSymbols[i] = new CpuFrameSymbol((ushort)i, 0, 0, $"Frame{i}"); // line 0 → name-only, no FileTable needed
            stack[i] = (ushort)i;
        }
        ushort[][] stacks = [stack];
        CpuSampleRecord[] samples = [new(1000, 0, 0, 0), new(2000, 0, 0, 0)];
        var cpuOffset = writer.WriteCpuSampleSection(samples, stacks, frameSymbols);

        header.CpuSampleSectionOffset = cpuOffset;
        writer.RewriteHeader(in header);
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Build a trace with a deliberately wrong magic number. Used for the "malformed file" path in
    /// <c>TraceSessionRuntime</c> tests — the runtime should reject it before reading further.
    /// </summary>
    public static string BuildBadMagic(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"bad-magic-{Guid.NewGuid():N}.typhon-trace");
        Span<byte> buf = stackalloc byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, 0xDEADBEEF); // wrong magic
        File.WriteAllBytes(path, buf.ToArray());
        return path;
    }

    /// <summary>
    /// Build a truncated trace that has a valid header but no blocks. Exercises the decoder's
    /// "unexpected EOF" path.
    /// </summary>
    public static string BuildTruncated(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"truncated-{Guid.NewGuid():N}.typhon-trace");
        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);
        writer.WriteHeader(in DefaultHeader);
        writer.WriteSystemDefinitions([]);
        writer.WriteArchetypes([]);
        writer.WriteComponentTypes([]);
        writer.WriteTracks([]);
        writer.WriteDags([]);
        writer.WriteEmptyStaticStructures();
        // No records, no span-name table → reader sees EOF mid-way through block scan.
        writer.Flush();
        return path;
    }

    private static readonly TraceFileHeader DefaultHeader = new()
    {
        Magic = TraceFileHeader.MagicValue,
        Version = TraceFileHeader.CurrentVersion,
        Flags = 0,
        TimestampFrequency = 10_000_000, // Stopwatch-like 10 MHz
        BaseTickRate = 1_000f,
        WorkerCount = 1,
        SystemCount = 0,
        ArchetypeCount = 0,
        ComponentTypeCount = 0,
        CreatedUtcTicks = 0, // deterministic fixture — don't bake wall-clock time into test bytes
        SamplingSessionStartQpc = 0,
    };

    private static void WriteRecordHeader(Span<byte> dest, int size, TraceEventKind kind, long timestamp)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)size);
        dest[2] = (byte)kind;
        dest[3] = 0; // threadSlot
        BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(4, 8), timestamp);
    }
}
