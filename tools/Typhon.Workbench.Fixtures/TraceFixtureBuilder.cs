using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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
        writer.WritePhases([]);

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
        writer.WritePhases(["Input", "Simulation", "Output"]);

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
        writer.WritePhases([]);
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
