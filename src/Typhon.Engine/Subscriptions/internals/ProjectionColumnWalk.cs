using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Reads one scalar out of a component column. Implemented by <c>struct</c>s so the column loop takes the reader as a type parameter and the JIT inlines the
/// read rather than dispatching it.
/// </summary>
internal interface IColumnReader
{
    /// <summary>Reads the value at <paramref name="offset"/> bytes into <paramref name="column"/>.</summary>
    /// <param name="column">The component column for one cluster.</param>
    /// <param name="offset">
    /// Byte offset of the value. A reader reads unchecked, so the offset must come from a slot the walk has already masked against the column's
    /// <see cref="ProjectionColumn.SlotCount"/> — never from an arbitrary index.
    /// </param>
    /// <returns>The value, widened to <see cref="double"/> — the one type every quantizing codec's arithmetic is defined over (W1).</returns>
    double Read(ReadOnlySpan<byte> column, int offset);
}

/// <summary>
/// Reads one integral value out of a component column, exactly: the integer paths never see a <see cref="double"/> (13 § 4, W1 scoped by ADR-069).
/// </summary>
internal interface IIntegerColumnReader
{
    /// <summary>Reads the value at <paramref name="offset"/> bytes into <paramref name="column"/>, sign- or zero-extended to 64 bits.</summary>
    /// <param name="column">The component column for one cluster.</param>
    /// <param name="offset">Byte offset of the value; masked by the walk, as for <see cref="IColumnReader"/>.</param>
    /// <returns>The value. A <see cref="ulong"/> reader either reinterprets its bits or saturates at <see cref="long.MaxValue"/>, by its kind.</returns>
    long Read(ReadOnlySpan<byte> column, int offset);
}

/// <summary>
/// Quantizes a projected value into its wire code. Implemented by <c>struct</c>s for the same reason as <see cref="IColumnReader"/>.
/// </summary>
/// <remarks>
/// A code, not bytes. The projection pass quantizes, compares against the stored copy and only then encodes, so the value that decided the change is the value
/// that reaches the wire — never quantized twice ([01 § 2]). Turning a code into bytes belongs to the record encoder.
/// </remarks>
internal interface IColumnCodec
{
    /// <summary>Encodes one value into its canonical wire code.</summary>
    /// <param name="value">The projected value.</param>
    /// <returns>The code, two's complement for the signed kinds.</returns>
    uint Encode(double value);
}

/// <summary>
/// One component column of one cluster, plus where inside each component the projected value sits.
/// </summary>
/// <remarks>
/// The span is the archetype's own SoA array — the same bytes <see cref="ClusterRef{TArch}.GetReadOnlySpan{T}"/> hands out — so a walk reads component values
/// through the cluster layout and through nothing else (SUB-01). A <c>ref struct</c> because it never outlives the cluster it describes.
/// </remarks>
internal readonly ref struct ProjectionColumn
{
    private readonly ReadOnlySpan<byte> _bytes;

    /// <summary>Creates a column over one component's SoA array.</summary>
    /// <param name="componentColumn">The component's bytes for the whole cluster: <c>slotCount × stride</c>.</param>
    /// <param name="stride">The component's storage size, which is the column's stride.</param>
    /// <param name="valueOffset">Byte offset of the projected value inside one component.</param>
    public ProjectionColumn(ReadOnlySpan<byte> componentColumn, int stride, int valueOffset)
    {
        if (stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "A component column strides by at least one byte");
        }

        if (valueOffset < 0 || valueOffset >= stride)
        {
            throw new ArgumentOutOfRangeException(nameof(valueOffset), valueOffset, "A projected value sits inside its component");
        }

        _bytes = componentColumn;
        Stride = stride;
        ValueOffset = valueOffset;
    }

    /// <summary>The component's storage size.</summary>
    public int Stride { get; }

    /// <summary>Byte offset of the projected value inside one component.</summary>
    public int ValueOffset { get; }

    /// <summary>How many slots the column covers.</summary>
    public int SlotCount => _bytes.Length / Stride;

    /// <summary>
    /// The slots this column actually holds, as a mask: bit <c>i</c> is set for <c>i &lt; SlotCount</c>. Every walk intersects the caller's slot set with it.
    /// </summary>
    /// <remarks>
    /// The readers index with <c>Unsafe.Add</c> and do not bound their own reads, so an unmasked walk would turn any stale bit of a
    /// watched mask into a read past the column — the mask is what makes an arbitrary <c>ulong</c> a safe input rather than a trusted one. A cluster holds at
    /// most 64 slots, so the mask is exact; at exactly 64 the shift would be undefined, which is why that case is written out.
    /// </remarks>
    public ulong SlotMask
    {
        get
        {
            var slots = SlotCount;
            return slots >= 64 ? ulong.MaxValue : slots <= 0 ? 0UL : (1UL << slots) - 1;
        }
    }

    /// <summary>The column's bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Carves one component's column out of a whole cluster's bytes, using the field's compiled offsets.</summary>
    /// <param name="clusterBytes">The cluster's bytes, from its base.</param>
    /// <param name="slotCount">The archetype's cluster slot count.</param>
    /// <param name="field">The compiled field.</param>
    /// <returns>The column.</returns>
    public static ProjectionColumn OverCluster(ReadOnlySpan<byte> clusterBytes, int slotCount, in CompiledField field) =>
        new(clusterBytes.Slice(field.ComponentOffsetInCluster, slotCount * field.ComponentSize), field.ComponentSize, field.FieldOffsetInComponent);

    /// <summary>Wraps a component column that has already been carved out — what <c>ClusterRef.GetReadOnlySpan</c> hands back.</summary>
    /// <param name="componentColumn">The component's bytes for the whole cluster.</param>
    /// <param name="field">The compiled field.</param>
    /// <returns>The column.</returns>
    public static ProjectionColumn Over(ReadOnlySpan<byte> componentColumn, in CompiledField field) =>
        new(componentColumn, field.ComponentSize, field.FieldOffsetInComponent);
}

/// <summary>
/// The per-column loop of the projection pass: for every watched slot of one cluster, read a field's value out of its column and quantize it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Column by column, not entity by entity.</b> A cluster stores each component as an array, so one field is contiguous; walking a whole column touches one
/// run of cache lines instead of one line per entity per field ([02 § 4]).
/// </para>
/// <para>
/// <b>Two struct type parameters, chosen once per column.</b> <see cref="Quantize(in CompiledField, in ProjectionColumn, ulong, Span{ulong})"/> switches on
/// the compiled field's path, source type and codec kind — a few switches per column, never per entity — and hands the loop a <c>struct</c> for each. The
/// JIT then specializes the loop: no delegate, no virtual call, no generated code, and nothing that runtime IL emission would be needed for (#409).
/// </para>
/// <para>
/// <b>Three scalar paths</b> (13 § 4). An integral source in an integer codec takes an exact path — its value is its code, with no rounding, no NaN test and,
/// when the codec covers the source, no clamp; a narrower codec clamps and counts. A <see cref="float"/> in <c>f32</c> copies its bits. Everything that
/// quantizes takes W1's binary64 path through <see cref="WireMath"/>. Codes are 64 bits wide so one row type serves every path.
/// </para>
/// </remarks>
internal static class ProjectionColumnWalk
{
    /// <summary>
    /// Turns one field's column into codes over the given slots.
    /// </summary>
    /// <param name="field">The compiled field; its path, codec kind and source type pick the specialization.</param>
    /// <param name="column">The field's column for one cluster.</param>
    /// <param name="slots">The slots to read, one bit each — the watched mask intersected with the cluster's occupancy.</param>
    /// <param name="codes">Receives one code per set bit, indexed by slot.</param>
    /// <returns>How many values a narrowing column clamped; 0 for every other path.</returns>
    public static int Quantize(in CompiledField field, in ProjectionColumn column, ulong slots, Span<ulong> codes)
    {
        switch (field.Path)
        {
            case ColumnPath.ExactInteger:
                WithIntegerReader(field, column, slots, codes, narrowing: false);
                return 0;
            case ColumnPath.NarrowingInteger:
                return WithIntegerReader(field, column, slots, codes, narrowing: true);
            case ColumnPath.ExactSingle:
                WalkSingleBits(column, slots, codes);
                return 0;
            case ColumnPath.ExactDouble:
                WalkDoubleBits(column, slots, codes);
                return 0;
            case ColumnPath.Quaternion:
                WalkQuaternion(field, column, slots, codes);
                return 0;
            case ColumnPath.Quantizing:
                break;
            case ColumnPath.Text:
                // No code: the section encoder reads text straight from the column into the wide body (13 § 6.3).
                return 0;
            case ColumnPath.EntityRef:
                // Resolved by the projection, which holds what a column cannot: the target's entry and the identities taken this tick (13 § 5). A caller
                // reaching here would leave the row as it found it.
                throw new InvalidOperationException($"Field '{field.Name}' is a reference: the projection resolves it, not the column walk.");
            default:
                throw new InvalidOperationException($"Field '{field.Name}' has no column path; the plan was not compiled.");
        }

        switch (field.CodecKind)
        {
            case CodecKind.Unorm:
                WithReader(field, column, slots, new UnormColumnCodec(field.CodecBits), codes);
                break;
            case CodecKind.Snorm:
                WithReader(field, column, slots, new SnormColumnCodec(field.CodecBits), codes);
                break;
            case CodecKind.Angle:
                WithReader(field, column, slots, new AngleColumnCodec(field.CodecBits), codes);
                break;
            case CodecKind.Quant:
                WithReader(field, column, slots, new QuantColumnCodec(field.QuantMin, field.QuantMax, field.CodecBits), codes);
                break;
            case CodecKind.F32:
                WithReader(field, column, slots, new SingleColumnCodec(), codes);
                break;
            case CodecKind.F16:
                WithReader(field, column, slots, new HalfColumnCodec(), codes);
                break;
            case CodecKind.Vec2:
            case CodecKind.Vec3:
                // One axis of a point (W4): each sub-field is one component's code.
                WithReader(field, column, slots, new VectorColumnCodec(field.VectorScale, field.CodecBits), codes);
                break;
            default:
                // An integer codec never quantizes: the pairing table sends every integral source to an integer path and refuses a float in one.
                throw new InvalidOperationException(
                    $"Field '{field.Name}' carries codec '{CodecTokens.ToToken(field.CodecKind)}' on the quantizing path, which only W1's codecs take.");
        }

        return 0;
    }

    /// <summary>
    /// The exact integer loop: the value read is the code. No rounding, no NaN test, no clamp — the pairing table proved the codec covers the source.
    /// </summary>
    /// <typeparam name="TReader">How the value is read.</typeparam>
    /// <param name="column">The field's column.</param>
    /// <param name="slots">The slots to read; intersected with the column's own <see cref="ProjectionColumn.SlotMask"/> before anything is read.</param>
    /// <param name="reader">The reader.</param>
    /// <param name="codes">Receives one code per set bit, indexed by slot: the value's two's complement, which every writer truncates to its width.</param>
    public static void WalkInteger<TReader>(in ProjectionColumn column, ulong slots, TReader reader, Span<ulong> codes)
        where TReader : struct, IIntegerColumnReader
    {
        var bytes = column.Bytes;
        var stride = column.Stride;
        var offset = column.ValueOffset;
        slots &= column.SlotMask;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            codes[slot] = unchecked((ulong)reader.Read(bytes, (slot * stride) + offset));
        }
    }

    /// <summary>
    /// The narrowing integer loop: the value clamped to the codec's range, every clamp counted — the declaration said <c>Saturate()</c>, or the field is an
    /// enum whose declared names fit and a clamp means a value from outside them.
    /// </summary>
    /// <typeparam name="TReader">How the value is read; a <see cref="ulong"/> source saturates at <see cref="long.MaxValue"/>, clamping the same.</typeparam>
    /// <param name="column">The field's column.</param>
    /// <param name="slots">The slots to read; intersected with the column's own <see cref="ProjectionColumn.SlotMask"/> before anything is read.</param>
    /// <param name="reader">The reader.</param>
    /// <param name="min">The codec's lowest code.</param>
    /// <param name="max">The codec's highest code.</param>
    /// <param name="codes">Receives one code per set bit, indexed by slot.</param>
    /// <returns>How many values were clamped.</returns>
    public static int WalkNarrowing<TReader>(in ProjectionColumn column, ulong slots, TReader reader, long min, long max, Span<ulong> codes)
        where TReader : struct, IIntegerColumnReader
    {
        var bytes = column.Bytes;
        var stride = column.Stride;
        var offset = column.ValueOffset;
        var clamps = 0;
        slots &= column.SlotMask;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            var value = reader.Read(bytes, (slot * stride) + offset);
            if (value < min)
            {
                value = min;
                clamps++;
            }
            else if (value > max)
            {
                value = max;
                clamps++;
            }

            codes[slot] = unchecked((ulong)value);
        }

        return clamps;
    }

    /// <summary>
    /// The exact <c>f32</c> loop for a <see cref="float"/> source: its bits are the code, with every NaN canonicalized to <c>0x7FC00000</c> — the bytes
    /// <see cref="WireMath.EncodeSingle"/> produces for the same value, without the widen and the narrow.
    /// </summary>
    /// <param name="column">The field's column.</param>
    /// <param name="slots">The slots to read; intersected with the column's own <see cref="ProjectionColumn.SlotMask"/> before anything is read.</param>
    /// <param name="codes">Receives one code per set bit, indexed by slot.</param>
    public static void WalkSingleBits(in ProjectionColumn column, ulong slots, Span<ulong> codes)
    {
        var bytes = column.Bytes;
        var stride = column.Stride;
        var offset = column.ValueOffset;
        slots &= column.SlotMask;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            var bits = Unsafe.ReadUnaligned<uint>(ref At(bytes, (slot * stride) + offset));
            codes[slot] = (bits & 0x7FFFFFFFu) > 0x7F800000u ? 0x7FC00000u : bits;
        }
    }

    /// <summary>
    /// The exact <c>f64</c> loop for a <see cref="double"/> source (W32): its bits are the code, with every NaN canonicalized to
    /// <c>0x7FF8000000000000</c> — the bytes <see cref="WireWriter.WriteF64"/> produces for the same value.
    /// </summary>
    /// <param name="column">The field's column.</param>
    /// <param name="slots">The slots to read; intersected with the column's own <see cref="ProjectionColumn.SlotMask"/> before anything is read.</param>
    /// <param name="codes">Receives one code per set bit, indexed by slot.</param>
    public static void WalkDoubleBits(in ProjectionColumn column, ulong slots, Span<ulong> codes)
    {
        var bytes = column.Bytes;
        var stride = column.Stride;
        var offset = column.ValueOffset;
        slots &= column.SlotMask;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            var bits = Unsafe.ReadUnaligned<ulong>(ref At(bytes, (slot * stride) + offset));
            codes[slot] = (bits & 0x7FFFFFFFFFFFFFFFUL) > 0x7FF0000000000000UL ? 0x7FF8000000000000UL : bits;
        }
    }

    /// <summary>
    /// The <c>quat3</c> loop (W8): four components of one quaternion, each at its member's offset, into one 32-bit smallest-three code.
    /// </summary>
    private static void WalkQuaternion(in CompiledField field, in ProjectionColumn column, ulong slots, Span<ulong> codes)
    {
        var bytes = column.Bytes;
        var stride = column.Stride;
        var offsets = field.ShapeOffsets;
        var isDouble = field.SourceType == ProjectionSourceType.Double;
        slots &= column.SlotMask;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            var at = slot * stride;
            double x, y, z, w;
            if (isDouble)
            {
                x = Unsafe.ReadUnaligned<double>(ref At(bytes, at + offsets[0]));
                y = Unsafe.ReadUnaligned<double>(ref At(bytes, at + offsets[1]));
                z = Unsafe.ReadUnaligned<double>(ref At(bytes, at + offsets[2]));
                w = Unsafe.ReadUnaligned<double>(ref At(bytes, at + offsets[3]));
            }
            else
            {
                x = Unsafe.ReadUnaligned<float>(ref At(bytes, at + offsets[0]));
                y = Unsafe.ReadUnaligned<float>(ref At(bytes, at + offsets[1]));
                z = Unsafe.ReadUnaligned<float>(ref At(bytes, at + offsets[2]));
                w = Unsafe.ReadUnaligned<float>(ref At(bytes, at + offsets[3]));
            }

            codes[slot] = WireMath.EncodeQuat3(x, y, z, w);
        }
    }

    /// <summary>
    /// A <see cref="ulong"/> narrowed into a signed 64-bit codec (<c>i64</c>, <c>vari64</c>): values above <see cref="long.MaxValue"/> clamp to it and
    /// are counted — the saturating reader the narrower codecs use would hide those clamps, since its saturated value is the codec's own top.
    /// </summary>
    private static int WalkUInt64IntoSigned(in ProjectionColumn column, ulong slots, Span<ulong> codes)
    {
        var bytes = column.Bytes;
        var stride = column.Stride;
        var offset = column.ValueOffset;
        var clamps = 0;
        slots &= column.SlotMask;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            var value = Unsafe.ReadUnaligned<ulong>(ref At(bytes, (slot * stride) + offset));
            if (value > long.MaxValue)
            {
                value = long.MaxValue;
                clamps++;
            }

            codes[slot] = value;
        }

        return clamps;
    }

    /// <summary>
    /// The column loop itself: one specialization per (reader, codec) pair the JIT ever sees.
    /// </summary>
    /// <typeparam name="TReader">How the value is read.</typeparam>
    /// <typeparam name="TCodec">How the value is quantized.</typeparam>
    /// <param name="column">The field's column.</param>
    /// <param name="slots">The slots to read; intersected with the column's own <see cref="ProjectionColumn.SlotMask"/> before anything is read.</param>
    /// <param name="reader">The reader.</param>
    /// <param name="codec">The codec.</param>
    /// <param name="codes">Receives one code per set bit, indexed by slot.</param>
    public static void Walk<TReader, TCodec>(in ProjectionColumn column, ulong slots, TReader reader, TCodec codec, Span<ulong> codes)
        where TReader : struct, IColumnReader
        where TCodec : struct, IColumnCodec
    {
        var bytes = column.Bytes;
        var stride = column.Stride;
        var offset = column.ValueOffset;
        slots &= column.SlotMask;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            codes[slot] = codec.Encode(reader.Read(bytes, (slot * stride) + offset));
        }
    }

    /// <summary>
    /// The column loop for a <c>Fraction</c>: two values of one component, sent as their ratio.
    /// </summary>
    /// <typeparam name="TReader">How both values are read; a fraction's numerator and denominator are the same type by construction.</typeparam>
    /// <typeparam name="TCodec">How the ratio is quantized — a <c>unorm</c>.</typeparam>
    /// <param name="column">The numerator's column.</param>
    /// <param name="denominatorOffset">Byte offset of the denominator inside the same component.</param>
    /// <param name="slots">The slots to read; intersected with the column's own <see cref="ProjectionColumn.SlotMask"/> before anything is read.</param>
    /// <param name="reader">The reader.</param>
    /// <param name="codec">The codec.</param>
    /// <param name="codes">Receives one code per set bit, indexed by slot.</param>
    public static void WalkRatio<TReader, TCodec>(in ProjectionColumn column, int denominatorOffset, ulong slots, TReader reader, TCodec codec,
        Span<ulong> codes)
        where TReader : struct, IColumnReader
        where TCodec : struct, IColumnCodec
    {
        var bytes = column.Bytes;
        var stride = column.Stride;
        var offset = column.ValueOffset;
        slots &= column.SlotMask;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            var at = slot * stride;
            var max = reader.Read(bytes, at + denominatorOffset);

            // A zero or negative maximum is a simulation state, not a declaration error: a freshly spawned entity whose MaxHealth has not been set yet would
            // otherwise divide by zero once per tick. It reads as an empty bar, which is what a client should show for it.
            codes[slot] = codec.Encode(max > 0 ? reader.Read(bytes, at + offset) / max : 0d);
        }
    }

    private static void WithReader<TCodec>(in CompiledField field, in ProjectionColumn column, ulong slots, TCodec codec, Span<ulong> codes)
        where TCodec : struct, IColumnCodec
    {
        switch (field.SourceType)
        {
            case ProjectionSourceType.Boolean:
                Run(field, column, slots, new BooleanColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.SByte:
                Run(field, column, slots, new SByteColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.Byte:
                Run(field, column, slots, new ByteColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.Int16:
                Run(field, column, slots, new Int16ColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.UInt16:
                Run(field, column, slots, new UInt16ColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.Int32:
                Run(field, column, slots, new Int32ColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.UInt32:
                Run(field, column, slots, new UInt32ColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.Int64:
                Run(field, column, slots, new Int64ColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.UInt64:
                Run(field, column, slots, new UInt64ColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.Single:
                Run(field, column, slots, new SingleColumnReader(), codec, codes);
                break;
            case ProjectionSourceType.Double:
                Run(field, column, slots, new DoubleColumnReader(), codec, codes);
                break;
            default:
                throw new InvalidOperationException($"Field '{field.Name}' has no resolved source type; the plan was not compiled.");
        }
    }

    private static void Run<TReader, TCodec>(in CompiledField field, in ProjectionColumn column, ulong slots, TReader reader, TCodec codec, Span<ulong> codes)
        where TReader : struct, IColumnReader
        where TCodec : struct, IColumnCodec
    {
        // One branch per column, so the ratio case costs the ordinary case nothing.
        if (field.RatioOffsetInComponent >= 0)
        {
            WalkRatio(column, field.RatioOffsetInComponent, slots, reader, codec, codes);
        }
        else
        {
            Walk(column, slots, reader, codec, codes);
        }
    }

    private static int WithIntegerReader(in CompiledField field, in ProjectionColumn column, ulong slots, Span<ulong> codes, bool narrowing)
    {
        switch (field.SourceType)
        {
            case ProjectionSourceType.Boolean:
                return RunInteger(field, column, slots, new BooleanIntegerReader(), codes, narrowing);
            case ProjectionSourceType.SByte:
                return RunInteger(field, column, slots, new SByteIntegerReader(), codes, narrowing);
            case ProjectionSourceType.Byte:
                return RunInteger(field, column, slots, new ByteIntegerReader(), codes, narrowing);
            case ProjectionSourceType.Int16:
                return RunInteger(field, column, slots, new Int16IntegerReader(), codes, narrowing);
            case ProjectionSourceType.UInt16:
                return RunInteger(field, column, slots, new UInt16IntegerReader(), codes, narrowing);
            case ProjectionSourceType.Int32:
                return RunInteger(field, column, slots, new Int32IntegerReader(), codes, narrowing);
            case ProjectionSourceType.UInt32:
                return RunInteger(field, column, slots, new UInt32IntegerReader(), codes, narrowing);
            case ProjectionSourceType.Int64:
                return RunInteger(field, column, slots, new Int64IntegerReader(), codes, narrowing);
            case ProjectionSourceType.UInt64:
                // A ulong above long.MaxValue clamps to any narrower codec's max, so saturating it there is exact; an exact path (a u64 codec) keeps its bits.
                // Into a signed 64-bit codec the saturated value IS the codec's top, so the clamp is counted by a walk of its own.
                if (narrowing && field.IntMax == long.MaxValue)
                {
                    return WalkUInt64IntoSigned(column, slots, codes);
                }

                return narrowing
                    ? RunInteger(field, column, slots, new UInt64SaturatingReader(), codes, true)
                    : RunInteger(field, column, slots, new UInt64IntegerReader(), codes, false);
            default:
                throw new InvalidOperationException($"Field '{field.Name}' is on an integer path with a non-integral source; the plan was not compiled.");
        }
    }

    private static int RunInteger<TReader>(in CompiledField field, in ProjectionColumn column, ulong slots, TReader reader, Span<ulong> codes, bool narrowing)
        where TReader : struct, IIntegerColumnReader
    {
        if (narrowing)
        {
            return WalkNarrowing(column, slots, reader, field.IntMin, field.IntMax, codes);
        }

        WalkInteger(column, slots, reader, codes);
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte At(ReadOnlySpan<byte> column, int offset) => ref Unsafe.Add(ref MemoryMarshal.GetReference(column), offset);

    // ── Readers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Each one is a stateless struct so the loop above becomes a load and a widen with nothing between them. Unaligned reads because a component's field
    // offsets follow the CLR's managed layout, which a cluster's SoA stride reproduces verbatim — a 4-byte field can and does land on a 2-byte boundary.

    private readonly struct BooleanColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => At(column, offset) != 0 ? 1d : 0d;
    }

    private readonly struct SByteColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => (sbyte)At(column, offset);
    }

    private readonly struct ByteColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => At(column, offset);
    }

    private readonly struct Int16ColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<short>(ref At(column, offset));
    }

    private readonly struct UInt16ColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<ushort>(ref At(column, offset));
    }

    private readonly struct Int32ColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<int>(ref At(column, offset));
    }

    private readonly struct UInt32ColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<uint>(ref At(column, offset));
    }

    private readonly struct Int64ColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<long>(ref At(column, offset));
    }

    private readonly struct UInt64ColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<ulong>(ref At(column, offset));
    }

    private readonly struct SingleColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<float>(ref At(column, offset));
    }

    private readonly struct DoubleColumnReader : IColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<double>(ref At(column, offset));
    }

    // ── Integer readers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // The exact paths' readers: a load and a sign or zero extension, nothing else. The double readers above stay for the quantizing path and for a Fraction,
    // whose ratio is a double whatever its two fields are.

    private readonly struct BooleanIntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => At(column, offset) != 0 ? 1L : 0L;
    }

    private readonly struct SByteIntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => (sbyte)At(column, offset);
    }

    private readonly struct ByteIntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => At(column, offset);
    }

    private readonly struct Int16IntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<short>(ref At(column, offset));
    }

    private readonly struct UInt16IntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<ushort>(ref At(column, offset));
    }

    private readonly struct Int32IntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<int>(ref At(column, offset));
    }

    private readonly struct UInt32IntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<uint>(ref At(column, offset));
    }

    private readonly struct Int64IntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => Unsafe.ReadUnaligned<long>(ref At(column, offset));
    }

    private readonly struct UInt64IntegerReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset) => unchecked((long)Unsafe.ReadUnaligned<ulong>(ref At(column, offset)));
    }

    private readonly struct UInt64SaturatingReader : IIntegerColumnReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(ReadOnlySpan<byte> column, int offset)
        {
            var value = Unsafe.ReadUnaligned<ulong>(ref At(column, offset));
            return value > long.MaxValue ? long.MaxValue : (long)value;
        }
    }

    // ── Codecs ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Every one defers to WireMath, which is the single definition of the arithmetic both SDKs implement (W1). None of them re-derives a formula: a second
    // spelling of a rounding rule is how a client's world stops being the server's.

    private readonly struct UnormColumnCodec : IColumnCodec
    {
        private readonly int _bits;

        public UnormColumnCodec(int bits) => _bits = bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value) => WireMath.EncodeUnorm(value, _bits);
    }

    private readonly struct SnormColumnCodec : IColumnCodec
    {
        private readonly int _bits;

        public SnormColumnCodec(int bits) => _bits = bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value) => unchecked((uint)WireMath.EncodeSnorm(value, _bits));
    }

    private readonly struct AngleColumnCodec : IColumnCodec
    {
        private readonly int _bits;

        public AngleColumnCodec(int bits) => _bits = bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value) => unchecked((uint)WireMath.EncodeAngle(value, _bits));
    }

    private readonly struct QuantColumnCodec : IColumnCodec
    {
        private readonly double _min;
        private readonly double _max;
        private readonly int _bits;

        public QuantColumnCodec(double min, double max, int bits)
        {
            _min = min;
            _max = max;
            _bits = bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value) => WireMath.EncodeQuant(value, _min, _max, _bits);
    }

    private readonly struct SingleColumnCodec : IColumnCodec
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value) => WireMath.EncodeSingle(value);
    }

    private readonly struct VectorColumnCodec : IColumnCodec
    {
        private readonly double _scale;
        private readonly int _bits;

        public VectorColumnCodec(double scale, int bits)
        {
            _scale = scale;
            _bits = bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value) => unchecked((uint)WireMath.EncodeVec(value, _scale, _bits));
    }

    private readonly struct HalfColumnCodec : IColumnCodec
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value) => WireMath.EncodeHalf(value);
    }
}
