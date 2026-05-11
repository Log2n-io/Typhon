using System;
using System.Buffers.Binary;
using System.Collections.Generic;

// ReSharper disable once CheckNamespace
namespace Typhon.Profiler.Events;

/// <summary>
/// Block-walker extension to the generator-emitted <see cref="TraceEventDecoder"/>. Walks a packed
/// record buffer (the LZ4-decompressed shape produced by <c>TraceRecordRing.Drain</c>) and emits one
/// <see cref="TraceEventDto"/> per record into the output list. Threads the consumer-side tick counter
/// across <c>TickStart</c> markers so each emitted DTO carries the correct <see cref="TraceEventDto.TickNumber"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sits
/// alongside the generated dispatch (<c>TraceEventDecoder.Decode(record, currentTick, ticksPerUs)</c>),
/// keeping all the trace-event consumer logic in one place.
/// </para>
/// <para>
/// <b>Tick-counter semantics</b> match the legacy decoder: the first <c>TickStart</c> record advances
/// the counter by one (so a fresh trace starting at tick 1 needs <paramref name="currentTick"/> seeded
/// at 0). Continuation chunks (no leading <c>TickStart</c>) seed the counter at the chunk's
/// <c>FromTick</c> directly.
/// </para>
/// <para>
/// <b>Malformed records</b> (size byte too small, size overruns buffer) abort the walk; partial output
/// is rolled back (<paramref name="output"/> trimmed to its starting length) and the input
/// <paramref name="currentTick"/> is restored. This matches the legacy behaviour of returning a clean
/// state on bad input rather than partial-and-undefined.
/// </para>
/// </remarks>
public static partial class TraceEventDecoder
{
    /// <summary>
    /// Walk <paramref name="recordBytes"/> as a sequence of size-prefixed records and append one
    /// <see cref="TraceEventDto"/> to <paramref name="output"/> per record. Advances
    /// <paramref name="currentTick"/> on every <see cref="TraceEventKind.TickStart"/> record.
    /// Returns the new tick value so the caller can keep walking subsequent blocks.
    /// </summary>
    public static int DecodeBlock(ReadOnlySpan<byte> recordBytes, int currentTick, long ticksPerUs, List<TraceEventDto> output)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (ticksPerUs <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerUs), "Stopwatch ticks-per-µs must be positive.");

        var savedTick = currentTick;
        var savedOutputCount = output.Count;
        var pos = 0;

        while (pos + TraceRecordHeader.CommonHeaderSize <= recordBytes.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(recordBytes[pos..]);
            if (size < TraceRecordHeader.CommonHeaderSize || pos + size > recordBytes.Length)
            {
                // Roll back any partial output + tick advance — clean failure beats half-decoded chaos.
                if (output.Count > savedOutputCount)
                {
                    output.RemoveRange(savedOutputCount, output.Count - savedOutputCount);
                }
                return savedTick;
            }

            var record = recordBytes.Slice(pos, size);
            var kind = (TraceEventKind)record[2];

            if (kind == TraceEventKind.TickStart)
            {
                currentTick++;
            }

            var dto = Decode(record, currentTick, ticksPerUs);
            if (dto != null)
            {
                output.Add(dto);
            }

            pos += size;
        }

        return currentTick;
    }
}
