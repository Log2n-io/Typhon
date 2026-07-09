using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Typhon.Schema.Definition;

/// <summary>
/// A 6-byte (48-bit) packed UTC timestamp: <see cref="DateTime"/> ticks offset from the 1970-01-01 epoch and shifted right by <see cref="PackedShift"/> bits,
/// giving ~102.4 µs resolution in half the footprint of a <see cref="DateTime"/>. Values before <see cref="MinValue"/> are not representable.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
[PublicAPI]
public readonly struct PackedDateTime48
{
    /// <summary>Epoch offset in <see cref="DateTime"/> ticks — the tick count at 1970-01-01T00:00:00Z, subtracted before packing.</summary>
    public const long BaseTicks = 621355968000000000; // Ticks at 1970-01-01T00:00:00Z

    /// <summary>Number of low bits dropped from the tick count when packing (10), trading precision for range.</summary>
    public const int PackedShift = 10;

    /// <summary>Effective resolution of one packed tick, in seconds (≈102.4 µs).</summary>
    public const double PackedTickResolution = 0.0000001 * (1 << PackedShift); // 102.4 microseconds

    /// <summary>Smallest representable value — the 1970-01-01T00:00:00Z epoch (a <see cref="DateTime"/> at <see cref="BaseTicks"/>).</summary>
    public static readonly DateTime MinValue = new(BaseTicks);

    private readonly uint _high;
    private readonly ushort _low;

    /// <summary>The current UTC time, packed.</summary>
    public static PackedDateTime48 UtcNow => new(DateTime.UtcNow);

    /// <summary>Creates a value from unpacked <see cref="DateTime"/> ticks (must be ≥ <see cref="BaseTicks"/>).</summary>
    /// <param name="ticks">Unpacked <see cref="DateTime"/> ticks.</param>
    public static PackedDateTime48 FromDateTimeTicks(long ticks) => new(ticks, false);

    /// <summary>Creates a value from an already-packed tick count (see <see cref="PackedTicks"/>).</summary>
    /// <param name="ticks">Packed ticks.</param>
    public static PackedDateTime48 FromPackedDateTimeTicks(long ticks) => new(ticks, true);

    /// <summary>Converts unpacked <see cref="DateTime"/> ticks to packed ticks (subtract <see cref="BaseTicks"/>, shift right by <see cref="PackedShift"/>).</summary>
    /// <param name="dateTimeTicks">Unpacked <see cref="DateTime"/> ticks.</param>
    /// <returns>The packed tick count.</returns>
    public static long ToPackedTicks(long dateTimeTicks) => (dateTimeTicks - BaseTicks) >> PackedShift;

    /// <summary>Converts packed ticks back to unpacked <see cref="DateTime"/> ticks (shift left by <see cref="PackedShift"/>, add <see cref="BaseTicks"/>).</summary>
    /// <param name="packedTicks">Packed ticks.</param>
    /// <returns>The equivalent <see cref="DateTime"/> ticks (quantized to <see cref="PackedTickResolution"/>).</returns>
    public static long ToDateTimeTicks(long packedTicks) => (packedTicks << PackedShift) + BaseTicks;

    /// <summary>Packs the given <paramref name="dateTime"/> from its raw <see cref="DateTime.Ticks"/>.</summary>
    /// <param name="dateTime">The timestamp to pack; must be at or after <see cref="MinValue"/>.</param>
    public PackedDateTime48(DateTime dateTime) : this(dateTime.Ticks, false)
    {
    }

    /// <summary>Constructs from either packed or unpacked ticks.</summary>
    /// <param name="value">Packed ticks when <paramref name="isPacked"/> is <c>true</c>, otherwise unpacked <see cref="DateTime"/> ticks (≥ <see cref="BaseTicks"/>).</param>
    /// <param name="isPacked"><c>true</c> if <paramref name="value"/> is already packed; <c>false</c> to pack it.</param>
    public PackedDateTime48(long value, bool isPacked)
    {
        Debug.Assert(isPacked || value >= BaseTicks, $"Can't store the given value {value}, it is before {MinValue}.");
        var packedTicks = isPacked ? value : ToPackedTicks(value);

        _high = (uint)(packedTicks >> 16);
        _low = (ushort)(packedTicks & 0xFFFF);
    }

    /// <summary>The unpacked <see cref="DateTime"/> ticks this value represents. The round-trip is lossy — precision is <see cref="PackedTickResolution"/>.</summary>
    public long Ticks => ((((long)_high << 16) | _low) << PackedShift) + BaseTicks;

    /// <summary>The raw 48-bit packed tick count (epoch-relative and shifted).</summary>
    public long PackedTicks => (((long)_high << 16) | _low);

    /// <summary>Unpacks to a <see cref="DateTime"/>.</summary>
    public static explicit operator DateTime(PackedDateTime48 packed) => new(packed.Ticks);

    /// <summary>Packs a <see cref="DateTime"/> from its raw <see cref="DateTime.Ticks"/>.</summary>
    public static explicit operator PackedDateTime48(DateTime dateTime) => new(dateTime.Ticks, false);

    /// <summary>Formats the value as a round-trippable UTC ISO-8601 string (<c>yyyy-MM-ddTHH:mm:ss.fffffffK</c>, invariant culture).</summary>
    public override string ToString() => ((DateTime)this).ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture);
}

/// <summary>
/// A 4-byte (32-bit) packed <see cref="TimeSpan"/>: the tick count shifted right by <see cref="PackedDateTime48.PackedShift"/> bits, giving ~102.4 µs
/// resolution. Durations outside [<see cref="MinValue"/>, <see cref="MaxValue"/>] are not representable.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public readonly struct PackedTimeSpan32
{
    /// <summary>Smallest (most negative) representable duration.</summary>
    public static readonly TimeSpan MinValue = ((TimeSpan)new PackedTimeSpan32(int.MinValue, true));

    /// <summary>Largest representable duration.</summary>
    public static readonly TimeSpan MaxValue = ((TimeSpan)new PackedTimeSpan32(int.MaxValue, true));
    private readonly int _packedTicks;

    /// <summary>The unpacked <see cref="TimeSpan"/> tick count (precision <see cref="PackedDateTime48.PackedTickResolution"/>).</summary>
    public long Ticks => (long)_packedTicks << PackedDateTime48.PackedShift;

    /// <summary>Unpacks to a <see cref="TimeSpan"/>.</summary>
    public static explicit operator TimeSpan(PackedTimeSpan32 packed) => new(packed.Ticks);

    /// <summary>Packs the given <paramref name="timeSpan"/>.</summary>
    /// <param name="timeSpan">The duration to pack; must be within [<see cref="MinValue"/>, <see cref="MaxValue"/>].</param>
    public PackedTimeSpan32(TimeSpan timeSpan) : this(timeSpan.Ticks, false)
    {

    }

    /// <summary>Constructs from either packed or unpacked ticks.</summary>
    /// <param name="value">Packed ticks when <paramref name="isPacked"/> is <c>true</c>, otherwise unpacked <see cref="TimeSpan"/> ticks.</param>
    /// <param name="isPacked"><c>true</c> if <paramref name="value"/> is already packed; <c>false</c> to pack it.</param>
    public PackedTimeSpan32(long value, bool isPacked)
    {
        var ticks = isPacked ? value : (value >> PackedDateTime48.PackedShift);
        Debug.Assert(ticks is >= int.MinValue and <= int.MaxValue, $"Given value {value} too large to be packed");
        _packedTicks = (int)ticks;
    }

    /// <summary>Formats the unpacked duration via <see cref="TimeSpan.ToString()"/>.</summary>
    public override string ToString() => ((TimeSpan)this).ToString();
}

/// <summary>
/// A 6-byte (48-bit) packed <see cref="TimeSpan"/>: the tick count shifted right by <see cref="PackedDateTime48.PackedShift"/> bits, giving ~102.4 µs
/// resolution over a much wider range than <see cref="PackedTimeSpan32"/>. Durations outside [<see cref="MinValue"/>, <see cref="MaxValue"/>] are not representable.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
[PublicAPI]
public readonly struct PackedTimeSpan48
{
    private readonly uint _high;
    private readonly ushort _low;

    /// <summary>Smallest (most negative) representable duration.</summary>
    public static readonly TimeSpan MinValue = ((TimeSpan)new PackedTimeSpan48(long.MinValue >> 16, true));

    /// <summary>Largest representable duration.</summary>
    public static readonly TimeSpan MaxValue = ((TimeSpan)new PackedTimeSpan48(long.MaxValue >> 16, true));

    /// <summary>The unpacked <see cref="TimeSpan"/> tick count (precision <see cref="PackedDateTime48.PackedTickResolution"/>).</summary>
    public long Ticks => ((((long)_high << 16) | _low) << PackedDateTime48.PackedShift);

    /// <summary>Unpacks to a <see cref="TimeSpan"/>.</summary>
    public static explicit operator TimeSpan(PackedTimeSpan48 packed) => new(packed.Ticks);

    /// <summary>Packs the given <paramref name="timeSpan"/>.</summary>
    /// <param name="timeSpan">The duration to pack; must be within [<see cref="MinValue"/>, <see cref="MaxValue"/>].</param>
    public PackedTimeSpan48(TimeSpan timeSpan) : this(timeSpan.Ticks, false)
    {
    }

    /// <summary>Constructs from either packed or unpacked ticks.</summary>
    /// <param name="value">Packed ticks when <paramref name="isPacked"/> is <c>true</c>, otherwise unpacked <see cref="TimeSpan"/> ticks.</param>
    /// <param name="isPacked"><c>true</c> if <paramref name="value"/> is already packed; <c>false</c> to pack it.</param>
    public PackedTimeSpan48(long value, bool isPacked)
    {
        var ticks = isPacked ? value : (value >> PackedDateTime48.PackedShift);
        Debug.Assert(ticks is >= long.MinValue >> 16 and <= long.MaxValue >> 16, $"Given value {value} too large to be packed");
        _high = (uint)(ticks >> 16);
        _low = (ushort)(ticks & 0xFFFF);
    }

    /// <summary>Formats the unpacked duration via <see cref="TimeSpan.ToString()"/>.</summary>
    public override string ToString() => ((TimeSpan)this).ToString();
}
