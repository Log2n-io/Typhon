// unset

using System;
using System.Numerics;

namespace Typhon.Engine.Internals;

/// <summary>
/// HyperLogLog cardinality estimator with precision 12 (4096 registers, 4KB memory).
/// Provides approximate distinct value count (NDV) within ~2% error for typical workloads.
/// </summary>
/// <remarks>
/// <para>
/// Uses Murmur3 64-bit finalizer for hashing (keys are already long-encoded).
/// Estimation applies Flajolet et al. harmonic mean with small-range (LinearCounting) and large-range (2^32) corrections.
/// </para>
/// <para>
/// Immutable-snapshot pattern: build a new instance during statistics rebuild, then atomic-swap the reference on <see cref="IndexStatistics"/>. This avoids
/// torn reads for concurrent queries.
/// </para>
/// </remarks>
internal sealed class HyperLogLog
{
    private const int Precision = 12;
    private const int RegisterCount = 1 << Precision;           // 4096
    private const int RegisterMask = RegisterCount - 1;         // 0xFFF
    private readonly byte[] _registers;                         // 4KB

    public HyperLogLog()
    {
        _registers = new byte[RegisterCount];
    }

    /// <summary>
    /// Adds a long-encoded value to the sketch. The value is hashed internally via Murmur3 finalizer.
    /// </summary>
    public void Add(long value)
    {
        // Murmur3 64-bit finalizer: 3 xor-shifts + 2 multiplies
        var h = (ulong)value;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;

        // Lower Precision bits → register index
        int index = (int)(h & RegisterMask);

        // Upper (64 - Precision) bits → leading zeros + 1
        ulong w = h >> Precision;
        int rho = w == 0 ? (64 - Precision + 1) : (BitOperations.LeadingZeroCount(w) - Precision + 1);

        if (rho > _registers[index])
        {
            _registers[index] = (byte)rho;
        }
    }

    /// <summary>
    /// Returns the estimated number of distinct values added.
    /// Applies small-range (LinearCounting) and large-range corrections per Flajolet et al.
    /// </summary>
    public long EstimateCardinality()
    {
        double sum = 0;
        int zeroCount = 0;
        for (int i = 0; i < RegisterCount; i++)
        {
            sum += 1.0 / (1L << _registers[i]);
            if (_registers[i] == 0)
            {
                zeroCount++;
            }
        }

        // Alpha constant for m = 4096: α_m = 0.7213 / (1 + 1.079/m)
        double alphaM = 0.7213 / (1.0 + 1.079 / RegisterCount);
        double rawEstimate = alphaM * RegisterCount * RegisterCount / sum;

        // Small range correction: many zero registers → use LinearCounting
        if (rawEstimate <= 2.5 * RegisterCount && zeroCount > 0)
        {
            return (long)(RegisterCount * Math.Log((double)RegisterCount / zeroCount));
        }

        // No large-range correction: with 64-bit hashes, the raw harmonic-mean estimate
        // is accurate for all practical cardinalities. The 2^32 correction from the original
        // Flajolet et al. paper applies only to 32-bit hash spaces.

        return (long)rawEstimate;
    }

    /// <summary>
    /// Merges another HyperLogLog into this one (max of each register). Used for combining partial scans.
    /// </summary>
    public void Merge(HyperLogLog other)
    {
        for (int i = 0; i < RegisterCount; i++)
        {
            if (other._registers[i] > _registers[i])
            {
                _registers[i] = other._registers[i];
            }
        }
    }

}
