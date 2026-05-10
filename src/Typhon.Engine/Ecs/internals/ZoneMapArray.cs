using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype per-indexed-field zone map for cluster-level query pruning.
/// Maintains min/max bounds per cluster; allows queries to skip clusters entirely when the query range doesn't overlap the cluster's [min, max] interval.
/// </summary>
/// <remarks>
/// <para>Zone maps are NOT persisted — rebuilt from cluster data on reopen/recovery.</para>
/// <para>Maintenance: lazy full recompute at tick fence for dirty clusters; eager widen on spawn.</para>
/// <para>Staleness: between tick fences, bounds may be wider than actual data (destroyed boundary entity lingers).
/// False positives acceptable (cluster checked but no match). False negatives impossible.</para>
/// </remarks>
internal sealed unsafe class ZoneMapArray
{
    private long[] _mins;       // [clusterChunkId] → min value (ordered long, sign-flipped for float/unsigned ordering)
    private long[] _maxs;       // [clusterChunkId] → max value (ordered long, sign-flipped for float/unsigned ordering)
    private bool[] _valid;      // [clusterChunkId] → true if min/max are initialized
    private int _capacity;
    private readonly int _fieldSize;
    private readonly bool _isFloat;
    private readonly bool _isDouble;
    private readonly bool _isUnsigned;

    internal ZoneMapArray(int initialCapacity, int fieldSize, bool isFloat, bool isDouble, bool isUnsigned = false)
    {
        _capacity = Math.Max(16, initialCapacity);
        _mins = new long[_capacity];
        _maxs = new long[_capacity];
        _valid = new bool[_capacity];
        _fieldSize = fieldSize;
        _isFloat = isFloat;
        _isDouble = isDouble;
        _isUnsigned = isUnsigned;
    }

    /// <summary>
    /// Recompute min/max for a single cluster by scanning all occupied entities.
    /// Called at tick fence for each dirty cluster.
    /// </summary>
    public void Recompute(int clusterChunkId, byte* clusterBase, ArchetypeClusterInfo layout, int compSlot, int fieldOffset)
    {
        EnsureCapacity(clusterChunkId);

        ulong occupancy = *(ulong*)clusterBase;
        if (occupancy == 0)
        {
            _valid[clusterChunkId] = false;
            return;
        }

        int compSize = layout.ComponentSize(compSlot);
        byte* compBase = clusterBase + layout.ComponentOffset(compSlot);

        long min = long.MaxValue;
        long max = long.MinValue;
        ulong bits = occupancy;

        while (bits != 0)
        {
            int slotIndex = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            byte* fieldPtr = compBase + slotIndex * compSize + fieldOffset;
            long val = ReadFieldAsOrderedLong(fieldPtr);
            if (val < min)
            {
                min = val;
            }
            if (val > max)
            {
                max = val;
            }
        }

        _mins[clusterChunkId] = min;
        _maxs[clusterChunkId] = max;
        _valid[clusterChunkId] = true;
    }

    /// <summary>
    /// Widen bounds to include a new value (eager, on spawn). Never narrows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Widen(int clusterChunkId, byte* fieldPtr)
    {
        EnsureCapacity(clusterChunkId);
        long val = ReadFieldAsOrderedLong(fieldPtr);

        if (!_valid[clusterChunkId])
        {
            _mins[clusterChunkId] = val;
            _maxs[clusterChunkId] = val;
            _valid[clusterChunkId] = true;
            return;
        }

        if (val < _mins[clusterChunkId])
        {
            _mins[clusterChunkId] = val;
        }
        if (val > _maxs[clusterChunkId])
        {
            _maxs[clusterChunkId] = val;
        }
    }

    /// <summary>
    /// Check if a cluster's zone map overlaps the query range [queryMin, queryMax].
    /// Returns true if the cluster MAY contain matching entities (or if the zone map is not initialized).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MayContain(int clusterChunkId, long queryMin, long queryMax)
    {
        if ((uint)clusterChunkId >= (uint)_capacity || !_valid[clusterChunkId])
        {
            return true; // Unknown → don't skip (conservative)
        }

        // Standard interval overlap: !(clusterMax < queryMin || clusterMin > queryMax)
        return _maxs[clusterChunkId] >= queryMin && _mins[clusterChunkId] <= queryMax;
    }

    /// <summary>
    /// Invalidate a cluster's zone map (e.g., when cluster is freed).
    /// </summary>
    public void Invalidate(int clusterChunkId)
    {
        if ((uint)clusterChunkId < (uint)_capacity)
        {
            _valid[clusterChunkId] = false;
        }
    }

    /// <summary>
    /// Read a field value as a long that preserves sort order across types.
    /// For floats: sign-flip so that negative floats sort before positive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadFieldAsOrderedLong(byte* ptr)
    {
        if (_isFloat)
        {
            return FloatToOrderedLong(*(float*)ptr);
        }

        if (_isDouble)
        {
            return DoubleToOrderedLong(*(double*)ptr);
        }

        if (_isUnsigned)
        {
            // Unsigned types: XOR with sign bit to preserve ordering in signed comparison.
            // Maps unsigned 0 → signed MIN, unsigned MAX → signed MAX.
            return _fieldSize switch
            {
                1 => *ptr,                                             // byte: 0..255 fits, no XOR needed
                2 => *(ushort*)ptr ^ (1L << 15),                       // ushort: XOR bit 15
                4 => *(uint*)ptr ^ (1L << 31),                         // uint: XOR bit 31
                8 => *(long*)ptr ^ long.MinValue,                      // ulong: XOR bit 63
                _ => *(uint*)ptr ^ (1L << 31),
            };
        }

        Debug.Assert(_fieldSize is 1 or 2 or 4 or 8, $"Unexpected zone map field size: {_fieldSize}");
        return _fieldSize switch
        {
            1 => *(sbyte*)ptr,
            2 => *(short*)ptr,
            4 => *(int*)ptr,
            8 => *(long*)ptr,
            _ => *(int*)ptr,
        };
    }

    // Float ordering: flip all bits if negative (sign bit set), else flip only sign bit.
    // This converts IEEE 754 to a representation where memcmp order = numeric order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long FloatToOrderedLong(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        // Cast to long BEFORE XOR/NOT to avoid sign-extension of int result to long.
        // Without cast: (0 ^ int.MinValue) = int -2147483648, sign-extends to long -2147483648 (wrong ordering).
        // With cast: (0L ^ (long)(uint)int.MinValue) = long 2147483648 (correct ordering).
        return bits < 0 ? ~(long)bits : (uint)(bits ^ int.MinValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long DoubleToOrderedLong(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return bits < 0 ? ~bits : bits ^ long.MinValue;
    }

    /// <summary>
    /// Convert a <see cref="FieldEvaluator"/> into zone map query bounds [min, max] as ordered longs.
    /// The bounds define the interval that must overlap the zone map for a potential match.
    /// Returns false if the evaluator's CompareOp cannot be expressed as a range (e.g., NotEqual).
    /// </summary>
    internal static bool TryGetQueryBounds(ref FieldEvaluator eval, out long queryMin, out long queryMax)
    {
        long orderedThreshold = ThresholdToOrdered(eval.Threshold, eval.KeyType);

        switch (eval.CompareOp)
        {
            case CompareOp.Equal:
                queryMin = orderedThreshold;
                queryMax = orderedThreshold;
                return true;
            case CompareOp.GreaterThan:
                queryMin = orderedThreshold + 1;
                queryMax = long.MaxValue;
                return true;
            case CompareOp.GreaterThanOrEqual:
                queryMin = orderedThreshold;
                queryMax = long.MaxValue;
                return true;
            case CompareOp.LessThan:
                queryMin = long.MinValue;
                queryMax = orderedThreshold - 1;
                return true;
            case CompareOp.LessThanOrEqual:
                queryMin = long.MinValue;
                queryMax = orderedThreshold;
                return true;
            case CompareOp.NotEqual:
            default:
                queryMin = long.MinValue;
                queryMax = long.MaxValue;
                return false; // Cannot prune with NotEqual
        }
    }

    /// <summary>
    /// Convert a <see cref="FieldEvaluator.Threshold"/> to the ordered long encoding used by zone maps.
    /// Same sign-flip logic as <see cref="ReadFieldAsOrderedLong"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ThresholdToOrdered(long threshold, KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Float:
            {
                int bits = (int)threshold;
                float value = Unsafe.As<int, float>(ref bits);
                return FloatToOrderedLong(value);
            }
            case KeyType.Double:
            {
                double value = Unsafe.As<long, double>(ref threshold);
                return DoubleToOrderedLong(value);
            }
            // Unsigned types: XOR with sign bit (must match ReadFieldAsOrderedLong encoding)
            case KeyType.Byte:
                return threshold; // 0..255 fits in signed long, no XOR needed
            case KeyType.UShort:
                return threshold ^ (1L << 15);
            case KeyType.UInt:
                return threshold ^ (1L << 31);
            case KeyType.ULong:
                return threshold ^ long.MinValue;
            default:
                // Signed integers are already in sort order as longs.
                return threshold;
        }
    }

    private void EnsureCapacity(int index)
    {
        if (index >= _capacity)
        {
            int newCap = Math.Max(_capacity * 2, index + 1);
            Array.Resize(ref _mins, newCap);
            Array.Resize(ref _maxs, newCap);
            Array.Resize(ref _valid, newCap);
            _capacity = newCap;
        }
    }
}
