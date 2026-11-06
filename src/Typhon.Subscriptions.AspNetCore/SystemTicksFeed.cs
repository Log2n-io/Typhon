using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Text;
using Typhon.Engine;

namespace Typhon.Subscriptions.AspNetCore;

/// <summary>
/// The binary body of <c>GET /typhon/systems/ticks</c> (<see cref="TyphonSubscriptionsEndpoints.MapTyphonSystemTicks"/>): every tick the telemetry ring holds
/// after a given one, with each system's CPU time, wall time, effective cores and workers touched — what a timeline of "which system cost what, second by
/// second" is drawn from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Little-endian, fixed-width, and versioned</b> rather than JSON: it is a hot poll (every 2 s per dashboard) of up to forty systems a tick, which as JSON
/// is three to four times the bytes and a parse the reader then pays. Layout, version 1:
/// </para>
/// <code>
/// header   u32 magic 'TSYT' · u16 version · u16 systemCount S · u16 flags (bit 0: gap) · u16 reserved · i64 firstTick · i32 tickCount N · i32 workerCount
/// names    only when asked: S × (u16 byteLength + UTF-8)
/// per tick N × (i64 tickNumber · f32 wallMs · S × (f32 cpuUs · f32 wallUs · u8 effectiveCoresX10 · u8 workersTouched))
/// </code>
/// <para>
/// <b>CPU time, not wall time, is what stacks.</b> A system's span overlaps its neighbours' on a parallel schedule (live: the spans summed to 16 ms on an
/// 11.4 ms tick), so a stack of spans would claim more time than the tick had. CPU is <see cref="SystemTelemetry.WorkUs"/> for a parallel QuerySystem —
/// the sum of its chunks — and <see cref="SystemTelemetry.DurationUs"/> for a serial or callback one, whose span is its CPU. Effective cores is their ratio,
/// 1 for a serial system; a skipped system writes zeros.
/// </para>
/// <para>
/// <b>Read from a live ring, so it tears the way <see cref="TyphonRuntime.ReadStats"/> does</b>: a tick evicted between the range and its read is skipped,
/// not raised (<see cref="TickTelemetryRing.TryGetTick"/>). <c>after</c> older than the ring's oldest tick sets the gap flag — the reader was away for longer
/// than the ring holds and must not interpolate across it.
/// </para>
/// </remarks>
[PublicAPI]
public static class SystemTicksFeed
{
    /// <summary>'TSYT', as its four bytes read little-endian.</summary>
    public const uint Magic = 0x54595354;

    /// <summary>The layout's version.</summary>
    public const ushort Version = 1;

    /// <summary>Flag bit 0: ticks between <c>after</c> and <c>firstTick</c> were evicted before this read.</summary>
    public const ushort FlagGap = 1;

    /// <summary>The header's bytes: magic 4, version 2, systems 2, flags 2, reserved 2, firstTick 8, tickCount 4, workerCount 4.</summary>
    public const int HeaderBytes = 28;

    /// <summary>A tick's own bytes, before its systems.</summary>
    public const int TickBytes = 12;

    /// <summary>One system's bytes in a tick.</summary>
    public const int SystemBytes = 10;

    /// <summary>The most ticks one answer carries: a reader far behind gets the oldest of them and asks again.</summary>
    public const int MaxTicks = 1024;

    /// <summary>The most bytes <see cref="Write"/> can need for these names and this many ticks.</summary>
    public static int MaxBytes(ReadOnlySpan<string> names, int ticks, bool withNames)
    {
        var size = HeaderBytes + (Math.Min(ticks, MaxTicks) * (TickBytes + (names.Length * SystemBytes)));
        if (withNames)
        {
            foreach (var name in names)
            {
                size += 2 + Encoding.UTF8.GetMaxByteCount(Math.Min(name?.Length ?? 0, ushort.MaxValue / 3));
            }
        }

        return size;
    }

    /// <summary>
    /// The number of ticks the ring holds after <paramref name="after"/>, at most <see cref="MaxTicks"/>: what <see cref="MaxBytes"/> is sized for.
    /// </summary>
    public static int TicksAfter(TickTelemetryRing ring, long after)
    {
        ArgumentNullException.ThrowIfNull(ring);
        return ring.TryGetRange(Math.Max(-1, after) + 1, out var first, out var last) ? (int)Math.Min(MaxTicks, last - first + 1) : 0;
    }

    /// <summary>Writes the ticks the ring holds after <paramref name="after"/> into <paramref name="into"/>.</summary>
    /// <param name="into">At least <see cref="MaxBytes"/> for these names and <see cref="TicksAfter"/> ticks.</param>
    /// <param name="ring">The engine's telemetry ring.</param>
    /// <param name="names">The systems' names, by system index — the ring's system count of them.</param>
    /// <param name="workerCount">The scheduler's workers: what a reader divides CPU by for the pool's utilisation.</param>
    /// <param name="after">The last tick the reader holds; −1 for none, which starts at the oldest the ring holds.</param>
    /// <param name="withNames">Whether to write the names block: asked once, and again when the system count changes.</param>
    /// <returns>The bytes written.</returns>
    public static int Write(Span<byte> into, TickTelemetryRing ring, ReadOnlySpan<string> names, int workerCount, long after, bool withNames)
    {
        ArgumentNullException.ThrowIfNull(ring);
        after = Math.Max(-1, after);
        var systems = names.Length;
        var oldest = ring.OldestAvailableTick;
        var gap = after >= 0 && oldest > after + 1;

        var at = HeaderBytes;
        if (withNames)
        {
            foreach (var name in names)
            {
                var text = name ?? string.Empty;
                var length = Encoding.UTF8.GetBytes(text.AsSpan(0, Math.Min(text.Length, ushort.MaxValue / 3)), into[(at + 2)..]);
                BinaryPrimitives.WriteUInt16LittleEndian(into[at..], (ushort)length);
                at += 2 + length;
            }
        }

        var count = 0;
        var firstWritten = after + 1;
        if (ring.TryGetRange(after + 1, out var first, out var last))
        {
            last = Math.Min(last, first + MaxTicks - 1);
            for (var t = first; t <= last; t++)
            {
                // Evicted between the range and here: skipped, the tearing a reader of a live ring accepts (TickTelemetryRing.TryGetTick's remarks).
                if (!ring.TryGetTick(t, out var tick) || !ring.TryGetSystemMetrics(t, out var metrics))
                {
                    continue;
                }

                if (count == 0)
                {
                    firstWritten = t;
                }

                BinaryPrimitives.WriteInt64LittleEndian(into[at..], t);
                BinaryPrimitives.WriteSingleLittleEndian(into[(at + 8)..], tick.ActualDurationMs);
                at += TickBytes;
                for (var s = 0; s < systems; s++)
                {
                    WriteSystem(into[at..], s < metrics.Length ? metrics[s] : default);
                    at += SystemBytes;
                }

                count++;
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(into, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(into[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(into[6..], (ushort)systems);
        BinaryPrimitives.WriteUInt16LittleEndian(into[8..], gap ? FlagGap : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(into[10..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(into[12..], firstWritten);
        BinaryPrimitives.WriteInt32LittleEndian(into[20..], count);
        BinaryPrimitives.WriteInt32LittleEndian(into[24..], workerCount);
        return at;
    }

    /// <summary>One system's 10 bytes: CPU µs, wall µs, effective cores × 10, workers touched; zeros for a skipped system.</summary>
    private static void WriteSystem(Span<byte> into, in SystemTelemetry system)
    {
        if (system.WasSkipped)
        {
            into[..SystemBytes].Clear();
            return;
        }

        var cpu = system.WorkUs > 0f ? system.WorkUs : system.DurationUs;
        var effective = system.WorkUs > 0f && system.DurationUs > 0f
            ? (byte)Math.Round(Math.Clamp(system.WorkUs / system.DurationUs, 0f, 25.5f) * 10f)
            : (byte)10;
        BinaryPrimitives.WriteSingleLittleEndian(into, cpu);
        BinaryPrimitives.WriteSingleLittleEndian(into[4..], system.DurationUs);
        into[8] = effective;
        into[9] = (byte)Math.Clamp(system.WorkersTouched, 0, 255);
    }
}
