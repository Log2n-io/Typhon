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
    /// <returns>The value, widened to <see cref="double"/> — the one type every codec's arithmetic is defined over (W1).</returns>
    double Read(ReadOnlySpan<byte> column, int offset);
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
/// <b>Two struct type parameters, chosen once per column.</b> <see cref="Quantize(in CompiledField, in ProjectionColumn, ulong, Span{uint})"/> switches on the
/// compiled field's source type and codec kind — two switches per column, never per entity — and hands the loop a <c>struct</c> for each. The JIT then
/// specializes the loop over that pair: no delegate, no virtual call, no generated code, and nothing that runtime IL emission would be needed for (#409).
/// </para>
/// </remarks>
internal static class ProjectionColumnWalk
{
    /// <summary>
    /// Quantizes one field's column over the given slots.
    /// </summary>
    /// <param name="field">The compiled field; its codec kind and source type pick the specialization.</param>
    /// <param name="column">The field's column for one cluster.</param>
    /// <param name="slots">The slots to read, one bit each — the watched mask intersected with the cluster's occupancy.</param>
    /// <param name="codes">Receives one code per set bit, indexed by slot.</param>
    public static void Quantize(in CompiledField field, in ProjectionColumn column, ulong slots, Span<uint> codes)
    {
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
            default:
                // Every remaining kind a projected field may carry is an integer on the wire — u8…i32, varu, vari, bits, bool, entityRef, tickLo. They differ
                // only in the range they clamp to, which the compiler already reduced to a pair of numbers, so one specialization serves them all.
                WithReader(field, column, slots, new IntegerColumnCodec(field.CodeMin, field.CodeMax), codes);
                break;
        }
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
    public static void Walk<TReader, TCodec>(in ProjectionColumn column, ulong slots, TReader reader, TCodec codec, Span<uint> codes)
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
        Span<uint> codes)
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

    private static void WithReader<TCodec>(in CompiledField field, in ProjectionColumn column, ulong slots, TCodec codec, Span<uint> codes)
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

    private static void Run<TReader, TCodec>(in CompiledField field, in ProjectionColumn column, ulong slots, TReader reader, TCodec codec, Span<uint> codes)
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

    // ── Codecs ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Every one defers to WireMath, which is the single definition of the arithmetic both SDKs implement (W1). None of them re-derives a formula: a second
    // spelling of a rounding rule is how a client's world stops being the server's.

    private readonly struct IntegerColumnCodec : IColumnCodec
    {
        private readonly double _min;
        private readonly double _max;

        public IntegerColumnCodec(double min, double max)
        {
            _min = min;
            _max = max;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value)
        {
            var rounded = WireMath.RoundHalfAwayFromZero(WireMath.CanonicalizeNaN(value));
            if (double.IsNaN(rounded))
            {
                return 0;
            }

            var clamped = rounded < _min ? _min : rounded > _max ? _max : rounded;
            return unchecked((uint)(long)clamped);
        }
    }

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

    private readonly struct HalfColumnCodec : IColumnCodec
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Encode(double value) => WireMath.EncodeHalf(value);
    }
}
