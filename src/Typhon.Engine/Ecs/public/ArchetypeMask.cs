using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Generic interface for archetype bitsets. Enables JIT specialization via constrained generics:
/// <c>where TMask : struct, IArchetypeMask&lt;TMask&gt;</c> compiles to direct calls with zero virtual dispatch.
/// Same pattern as <see cref="IPageStore"/> (PersistentStore/TransientStore).
/// </summary>
[PublicAPI]
public interface IArchetypeMask<TSelf> where TSelf : struct
{
    void Set(ushort archetypeId);
    void Clear(ushort archetypeId);
    bool Test(ushort archetypeId);
    TSelf And(in TSelf other);
    TSelf AndNot(in TSelf other);
    TSelf Or(in TSelf other);
    bool IsEmpty { get; }
    int PopCount { get; }
    int MaxId { get; }
}

/// <summary>
/// Compact archetype bitset covering up to 256 archetypes using 4 inline ulongs (32 bytes).
/// Used for Tier 1 query evaluation: a single AND instruction per entity to test archetype membership.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public unsafe struct ArchetypeMask256 : IArchetypeMask<ArchetypeMask256>
{
    private const int WordCount = 4;
    private fixed ulong _bits[WordCount];

    /// <summary>Set the bit for the given archetype ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ushort archetypeId)
    {
        Debug.Assert(archetypeId < 256, $"ArchetypeMask256 supports IDs 0-255, got {archetypeId}");
        _bits[archetypeId >> 6] |= 1UL << (archetypeId & 63);
    }

    /// <summary>Clear the bit for the given archetype ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear(ushort archetypeId)
    {
        Debug.Assert(archetypeId < 256, $"ArchetypeMask256 supports IDs 0-255, got {archetypeId}");
        _bits[archetypeId >> 6] &= ~(1UL << (archetypeId & 63));
    }

    /// <summary>Test whether the given archetype ID is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Test(ushort archetypeId)
    {
        Debug.Assert(archetypeId < 256, $"ArchetypeMask256 supports IDs 0-255, got {archetypeId}");
        return (_bits[archetypeId >> 6] & (1UL << (archetypeId & 63))) != 0;
    }

    /// <summary>Bitwise AND — inclusion filtering.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ArchetypeMask256 And(in ArchetypeMask256 other)
    {
        var result = new ArchetypeMask256();
        for (int i = 0; i < WordCount; i++)
        {
            result._bits[i] = _bits[i] & other._bits[i];
        }
        return result;
    }

    /// <summary>Bitwise AND NOT — exclusion filtering.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ArchetypeMask256 AndNot(in ArchetypeMask256 other)
    {
        var result = new ArchetypeMask256();
        for (int i = 0; i < WordCount; i++)
        {
            result._bits[i] = _bits[i] & ~other._bits[i];
        }
        return result;
    }

    /// <summary>Bitwise OR — union.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ArchetypeMask256 Or(in ArchetypeMask256 other)
    {
        var result = new ArchetypeMask256();
        for (int i = 0; i < WordCount; i++)
        {
            result._bits[i] = _bits[i] | other._bits[i];
        }
        return result;
    }

    /// <summary>True if no bits are set.</summary>
    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_bits[0] | _bits[1] | _bits[2] | _bits[3]) == 0;
    }

    /// <summary>Number of set bits.</summary>
    public readonly int PopCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitOperations.PopCount(_bits[0]) + BitOperations.PopCount(_bits[1]) + BitOperations.PopCount(_bits[2]) + BitOperations.PopCount(_bits[3]);
    }

    /// <summary>Maximum archetype ID supported (255).</summary>
    public int MaxId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 255;
    }

    /// <summary>Create a mask with a single archetype bit set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArchetypeMask256 FromArchetype(ushort archetypeId)
    {
        var mask = new ArchetypeMask256();
        mask.Set(archetypeId);
        return mask;
    }

    /// <summary>Create a mask with bits set for all archetype IDs in the subtree.</summary>
    public static ArchetypeMask256 FromSubtree(ushort[] archetypeIds)
    {
        var mask = new ArchetypeMask256();
        foreach (var id in archetypeIds)
        {
            mask.Set(id);
        }
        return mask;
    }
}

/// <summary>
/// Large archetype bitset for >256 archetypes, backed by a <c>ulong[]</c> array.
/// Same API as <see cref="ArchetypeMask256"/> but supports up to 4096 archetypes.
/// </summary>
[PublicAPI]
public struct ArchetypeMaskLarge : IArchetypeMask<ArchetypeMaskLarge>
{
    private ulong[] _bits;

    /// <summary>Create a mask sized for the given maximum archetype ID.</summary>
    public ArchetypeMaskLarge(int maxArchetypeId)
    {
        _bits = new ulong[(maxArchetypeId + 64) >> 6];
    }

    private ArchetypeMaskLarge(ulong[] bits)
    {
        _bits = bits;
    }

    /// <summary>Set the bit for the given archetype ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ushort archetypeId)
    {
        int word = archetypeId >> 6;
        if (word < _bits.Length)
        {
            _bits[word] |= 1UL << (archetypeId & 63);
        }
    }

    /// <summary>Clear the bit for the given archetype ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear(ushort archetypeId)
    {
        int word = archetypeId >> 6;
        if (word < _bits.Length)
        {
            _bits[word] &= ~(1UL << (archetypeId & 63));
        }
    }

    /// <summary>Test whether the given archetype ID is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Test(ushort archetypeId)
    {
        if (_bits == null)
        {
            return false;
        }
        int word = archetypeId >> 6;
        if (word >= _bits.Length)
        {
            return false;
        }
        return (_bits[word] & (1UL << (archetypeId & 63))) != 0;
    }

    /// <summary>Bitwise AND — inclusion filtering.</summary>
    public readonly ArchetypeMaskLarge And(in ArchetypeMaskLarge other)
    {
        int len = Math.Min(_bits.Length, other._bits.Length);
        var result = new ulong[len];
        for (int i = 0; i < len; i++)
        {
            result[i] = _bits[i] & other._bits[i];
        }
        return new ArchetypeMaskLarge(result);
    }

    /// <summary>Bitwise AND NOT — exclusion filtering.</summary>
    public readonly ArchetypeMaskLarge AndNot(in ArchetypeMaskLarge other)
    {
        var result = new ulong[_bits.Length];
        int len = Math.Min(_bits.Length, other._bits.Length);
        for (int i = 0; i < len; i++)
        {
            result[i] = _bits[i] & ~other._bits[i];
        }
        // Copy remaining bits from this (no corresponding other bits to negate)
        for (int i = len; i < _bits.Length; i++)
        {
            result[i] = _bits[i];
        }
        return new ArchetypeMaskLarge(result);
    }

    /// <summary>Bitwise OR — union.</summary>
    public readonly ArchetypeMaskLarge Or(in ArchetypeMaskLarge other)
    {
        int maxLen = Math.Max(_bits.Length, other._bits.Length);
        var result = new ulong[maxLen];
        int minLen = Math.Min(_bits.Length, other._bits.Length);
        for (int i = 0; i < minLen; i++)
        {
            result[i] = _bits[i] | other._bits[i];
        }
        var longer = _bits.Length > other._bits.Length ? _bits : other._bits;
        for (int i = minLen; i < maxLen; i++)
        {
            result[i] = longer[i];
        }
        return new ArchetypeMaskLarge(result);
    }

    /// <summary>True if no bits are set.</summary>
    public readonly bool IsEmpty
    {
        get
        {
            if (_bits == null)
            {
                return true;
            }
            foreach (var w in _bits)
            {
                if (w != 0)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Number of set bits.</summary>
    public readonly int PopCount
    {
        get
        {
            int count = 0;
            foreach (var w in _bits)
            {
                count += BitOperations.PopCount(w);
            }
            return count;
        }
    }

    /// <summary>Maximum archetype ID supported by this mask instance.</summary>
    public int MaxId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bits == null ? 0 : (_bits.Length << 6) - 1;
    }

    /// <summary>Create a mask with a single archetype bit set.</summary>
    public static ArchetypeMaskLarge FromArchetype(ushort archetypeId, int maxArchetypeId)
    {
        var mask = new ArchetypeMaskLarge(maxArchetypeId);
        mask.Set(archetypeId);
        return mask;
    }

    /// <summary>Create a mask with bits set for all archetype IDs in the subtree.</summary>
    public static ArchetypeMaskLarge FromSubtree(ushort[] archetypeIds, int maxArchetypeId)
    {
        var mask = new ArchetypeMaskLarge(maxArchetypeId);
        foreach (var id in archetypeIds)
        {
            mask.Set(id);
        }
        return mask;
    }
}
