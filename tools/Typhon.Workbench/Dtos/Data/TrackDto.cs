namespace Typhon.Workbench.Dtos.Data;

public record TrackFieldDescriptorDto(string Name, string Type); // Type = "u32", "f32", "f64", "u8", "u16"
public record TrackSchemaDto(string Id, string Kind, TrackFieldDescriptorDto[] Fields); // Kind = "perTick"
public record TracksResponseDto(TrackSchemaDto[] Tracks);

public record TickSummaryRecordDto(
    uint TickNumber,
    double StartUs,
    float DurationUs,
    uint EventCount,
    float MaxSystemDurationUs,
    byte OverloadLevel,
    byte TickMultiplier,
    ushort ConsecutiveOverrun,
    ushort ConsecutiveUnderrun);

public record MetronomeWaitRecordDto(uint TickNumber, ushort WaitUs, byte IntentClass);

// ── v2 tracks (#311) ─────────────────────────────────────────────────────────

/// <summary>One row in a <c>system/&lt;name&gt;</c> track. Captures per-tick execution data for a single system.</summary>
public record SystemTickRecordDto(
    uint TickNumber,
    double StartUs,
    double EndUs,
    double ReadyUs,
    float DurationUs,
    uint EntitiesProcessed,
    byte WorkersTouched,
    ushort ChunksProcessed,
    byte SkipReason);

/// <summary>One row in a <c>queue/&lt;name&gt;</c> track. Captures per-tick depth telemetry for a single event queue.</summary>
public record QueueTickRecordDto(
    uint TickNumber,
    uint PeakDepth,
    uint EndOfTickDepth,
    uint OverflowCount,
    uint Produced,
    uint Consumed);

/// <summary>One row in a <c>posttick/*</c> track. Single-field record (the duration of the named phase for a tick).</summary>
public record PostTickRecordDto(uint TickNumber, float DurationUs);

public record TrackDataResponseDto(string TrackId, object[] Records);
