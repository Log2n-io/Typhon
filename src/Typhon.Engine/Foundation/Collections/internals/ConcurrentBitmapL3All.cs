// unset

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// A lock-free, thread-safe hierarchical bitmap with 3 levels of acceleration structures.
///
/// Structure:
/// - L0All: Ground truth bitmap (each bit = one allocation slot)
/// - L1All: "All full" summary (bit set when ALL 64 corresponding L0 bits are set)
/// - L1Any: "Any set" summary (bit set when ANY corresponding L0 bit is set)
/// - L2All: Top-level summary (bit set when ALL 64 corresponding L1All bits are set)
///
/// Concurrency model:
/// - All state is encapsulated in an immutable-ish BitmapState object
/// - L0 operations use Interlocked CAS - the result determines success/failure
/// - L1/L2 are acceleration hints with best-effort updates (temporal incoherence is acceptable)
/// - Resize creates a new state with recounted TotalBitSet, then atomically swaps
/// - TotalBitSet is always exact: each state owns its counter, orphaned states don't matter
/// </summary>
internal unsafe class ConcurrentBitmapL3All : IResource, IMetricSource, IDebugPropertiesProvider
{
    private Bank[] _banks;
    private readonly int _l0Size;
    private readonly int _l1Size;
    private readonly int _l2Size;
    private readonly int _l0Shift;
    private readonly int _indexInBankMask;
    private readonly IMemoryAllocator _memoryAllocator;

    // Operation counters (use plain ++ per §7.3 - hot path, accept occasional misses)
    private long _setL0Count;
    private long _clearL0Count;
    private long _setL1Count;
    private long _growCount;

    private class Bank : IDisposable
    {
        public int TotalBitSet;
        public bool IsFull => TotalBitSet == _owner.BankBitCountCapacity;
        public PinnedMemoryBlock MemoryBlock;

        public long* L0All;
        public long* L1All;
        public long* L1Any;
        public long* L2All;
        private readonly ConcurrentBitmapL3All _owner;

        public Bank(ConcurrentBitmapL3All owner, int bankIndex)
        {
            _owner = owner;
            var sizeAsLong = _owner._l0Size + (_owner._l1Size * 2) + _owner._l2Size;
            MemoryBlock = _owner._memoryAllocator.AllocatePinned($"Bank{bankIndex}", owner, sizeAsLong * sizeof(long), true, 64);

            L0All = (long*)MemoryBlock.DataAsIntPtr.ToPointer();
            L1All = L0All + _owner._l0Size;
            L1Any = L1All + _owner._l1Size;
            L2All = L1Any + _owner._l1Size;
        }

        public void Dispose()
        {
            L0All = L1All = L1Any = L2All = null;
            MemoryBlock.Dispose();
            MemoryBlock = null;
        }
    }
    
    public int Capacity
    {
        get => _banks.Length * BankBitCountCapacity;
    }
    
    public int BankBitCountCapacity { get; }

    public int TotalBitSet => _banks.Aggregate(0, (current, bank) => current + bank.TotalBitSet);

    public bool IsFull => _banks.All(b => b.IsFull);

    /// <summary>
    /// Creates a new concurrent bitmap with the specified capacity per bank.
    /// </summary>
    /// <param name="id">Unique identifier for this resource.</param>
    /// <param name="parent">Parent resource (required, cannot be null).</param>
    /// <param name="memoryAllocator">Memory allocator for bank storage (required, cannot be null).</param>
    /// <param name="bankBitCountCapacity">Capacity per bank (must be power of 2).</param>
    /// <exception cref="ArgumentNullException">Thrown if parent or memoryAllocator is null.</exception>
    /// <exception cref="ArgumentException">Thrown if capacity is not a power of 2.</exception>
    public ConcurrentBitmapL3All(string id, IResource parent, IMemoryAllocator memoryAllocator, int bankBitCountCapacity)
    {
        if (!MathHelpers.IsPow2(bankBitCountCapacity))
        {
            throw new ArgumentException($"BankBitCountCapacity must be a power of 2 but {bankBitCountCapacity} was given", nameof(bankBitCountCapacity));
        }

        Parent = parent ?? throw new ArgumentNullException(nameof(parent), "Parent resource cannot be null. Resources must have an explicit parent.");
        _memoryAllocator = memoryAllocator ?? throw new ArgumentNullException(nameof(memoryAllocator));
        Id = id ?? Guid.NewGuid().ToString();
        Owner = Parent.Owner;
        CreatedAt = DateTime.UtcNow;
        BankBitCountCapacity = bankBitCountCapacity;
        _l0Size = Math.Max(1, (bankBitCountCapacity + 63) / 64);
        _l1Size = Math.Max(1, (_l0Size + 63) / 64);
        _l2Size = Math.Max(1, (_l1Size + 63) / 64);
        _l0Shift = BitOperations.Log2((uint)bankBitCountCapacity);
        _indexInBankMask = (1 << _l0Shift) - 1;
        _banks = [new Bank(this, 0)];
    }

    public void Grow()
    {
        var banks = _banks;
        var newBanks = new Bank[banks.Length +1];
        Array.Copy(banks, newBanks, banks.Length);
        newBanks[banks.Length] = new Bank(this, banks.Length);

        if (Interlocked.CompareExchange(ref _banks, newBanks, banks) == banks)
        {
            _growCount++; // Plain ++ - rare operation, no contention
        }
        else
        {
            // CAS failed - another thread already grew the array
            // Dispose the bank we created to avoid leaking memory
            newBanks[banks.Length].Dispose();
        }
    }

    (Bank[], int, int) GetBankAndIndex(int bitIndex)
    {
        var banks = _banks;
        var bankLength = banks.Length;
        var bankIndex = bitIndex >> _l0Shift;
        var index = bitIndex & _indexInBankMask;
        return (banks, (bankIndex < bankLength) ? bankIndex : -1, index);
    }
    
    /// <summary>
    /// Sets a single bit at the given index.
    /// Returns true if the bit was successfully set by this call, false if it was already set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL0(int bitIndex)
    {
        (Bank[] banks, int bankIndex, int indexInBank) = GetBankAndIndex(bitIndex);

        // Check bounds before accessing arrays
        if (bankIndex < 0)
        {
            return false;
        }

        var bank = banks[bankIndex];
        
        var l0Offset = indexInBank >> 6;
        var l0Mask = 1L << (indexInBank & 0x3F);

        // CAS operation on L0 - this IS the ground truth
        var prevL0 = Interlocked.Or(ref bank.L0All[l0Offset], l0Mask);
        if ((prevL0 & l0Mask) != 0)
        {
            // Bit was already set - optimistic failure, caller retries elsewhere
            return false;
        }

        // Successfully set the bit in this state - now update hints (best-effort)

        // Update L1All if L0 word became fully set
        if ((prevL0 | l0Mask) == -1)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            // Best-effort: Interlocked.Or is safe even if concurrent
            var prevL1 = Interlocked.Or(ref bank.L1All[l1Offset], l1Mask);

            // Self-correction: A concurrent ClearL0 may have cleared a bit in L0 between
            // our L0 Or and our L1All Or. If L0 is no longer full, undo the L1All set.
            // This prevents false positives where L1All claims "full" but L0 has free slots.
            if (bank.L0All[l0Offset] != -1)
            {
                Interlocked.And(ref bank.L1All[l1Offset], ~l1Mask);
            }
            // Update L2All if L1 word became fully set
            else if ((prevL1 | l1Mask) == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                Interlocked.Or(ref bank.L2All[l2Offset], l2Mask);

                // Self-correction for L2All: verify L1All is still full
                if (bank.L1All[l1Offset] != -1)
                {
                    Interlocked.And(ref bank.L2All[l2Offset], ~l2Mask);
                }
            }
        }

        // Update L1Any if L0 word transitioned from empty to non-empty
        if (prevL0 == 0)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);
            Interlocked.Or(ref bank.L1Any[l1Offset], l1Mask);
        }

        // State unchanged - increment counter
        Interlocked.Increment(ref bank.TotalBitSet);
        _setL0Count++; // Plain ++ - hot path, accept occasional misses

        return true;
    }

    /// <summary>
    /// Sets all 64 bits of an L0 word atomically (bulk allocation).
    /// Uses CompareExchange to ensure the word was completely empty.
    /// Returns true if successful, false if any bit was already set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL1(int bitIndex)
    {
        (Bank[] banks, int bankIndex, int indexInBank) = GetBankAndIndex(bitIndex);

        // Check bounds before accessing arrays
        if (bankIndex < 0)
        {
            return false;
        }

        var bank = banks[bankIndex];
        var l0Offset = indexInBank;

        // CompareExchange: Only set all bits if word was completely empty
        var prevL0 = Interlocked.CompareExchange(ref bank.L0All[l0Offset], -1L, 0L);
        if (prevL0 != 0)
        {
            // Word wasn't empty - we didn't modify anything
            return false;
        }

        // Successfully claimed all 64 bits - update hints (best-effort)
        var l1Offset = l0Offset >> 6;
        var l1Mask = 1L << (l0Offset & 0x3F);

        var prevL1 = Interlocked.Or(ref bank.L1All[l1Offset], l1Mask);

        // Self-correction: verify L0 is still full after setting L1All
        if (bank.L0All[l0Offset] != -1)
        {
            Interlocked.And(ref bank.L1All[l1Offset], ~l1Mask);
        }
        // Update L2All if L1 word became fully set
        else if ((prevL1 | l1Mask) == -1)
        {
            var l2Offset = l1Offset >> 6;
            var l2Mask = 1L << (l1Offset & 0x3F);
            Interlocked.Or(ref bank.L2All[l2Offset], l2Mask);

            // Self-correction for L2All: verify L1All is still full
            if (bank.L1All[l1Offset] != -1)
            {
                Interlocked.And(ref bank.L2All[l2Offset], ~l2Mask);
            }
        }

        // L1Any: word definitely has bits now
        Interlocked.Or(ref bank.L1Any[l1Offset], l1Mask);

        // State unchanged - increment counter
        Interlocked.Add(ref bank.TotalBitSet, 64);
        _setL1Count++; // Plain ++ - hot path, accept occasional misses

        return true;
    }

    /// <summary>
    /// Clears a single bit at the given index.
    /// This operation is idempotent - clearing an already-clear bit is a no-op.
    /// Returns true if successful (bit cleared or was already clear), false if state changed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool ClearL0(int bitIndex)
    {
        (Bank[] banks, int bankIndex, int indexInBank) = GetBankAndIndex(bitIndex);

        // Check bounds before accessing arrays
        if (bankIndex < 0)
        {
            return false;
        }

        var bank = banks[bankIndex];
        
        var l0Offset = indexInBank >> 6;
        var l0Mask = ~(1L << (indexInBank & 0x3F));

        // CAS: Clear the bit
        var prevL0 = Interlocked.And(ref bank.L0All[l0Offset], l0Mask);

        // If bit wasn't set, nothing to do (idempotent)
        if ((prevL0 & ~l0Mask) == 0)
        {
            return true;
        }

        // Bit was set, now cleared - update hints (best-effort)

        // Update L1All if L0 word was fully set (no longer is)
        if (prevL0 == -1)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = Interlocked.And(ref bank.L1All[l1Offset], ~l1Mask);

            // Update L2All if L1 word was fully set
            if (prevL1 == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                Interlocked.And(ref bank.L2All[l2Offset], ~l2Mask);
            }
        }

        // Update L1Any if L0 word became empty
        if ((prevL0 & l0Mask) == 0)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);
            Interlocked.And(ref bank.L1Any[l1Offset], ~l1Mask);
        }

        // Decrement THIS state's counter
        Interlocked.Decrement(ref bank.TotalBitSet);
        _clearL0Count++; // Plain ++ - hot path, accept occasional misses

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool IsSet(int bitIndex)
    {
        (Bank[] banks, int bankIndex, int indexInBank) = GetBankAndIndex(bitIndex);

        // Check bounds before accessing arrays
        if (bankIndex < 0)
        {
            return false;
        }

        var bank = banks[bankIndex];
        
        var offset = indexInBank >> 6;
        var mask = 1L << (indexInBank & 0x3F);

        return (bank.L0All[offset] & mask) != 0L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool FindNextUnsetL0(ref int bitIndex)
    {
        (Bank[] banks, int bankIndex, int indexInBank) = GetBankAndIndex(++bitIndex);

        // Check bounds before accessing arrays
        if (bankIndex < 0)
        {
            return false;
        }

        var capacity = BankBitCountCapacity;
        var c0 = indexInBank;

        for ( ; bankIndex < banks.Length; bankIndex++)
        {
            var bank = banks[bankIndex];

            if (bank.IsFull)
            {
                continue;
            }
            
            var ll0 = capacity >> 6;
            var ll1 = _l1Size;
            var ll2 = _l2Size;
            var v0 = bank.L0All[indexInBank>>6];
            
            while (c0 < capacity)
            {
                // Do we have to fetch a new L0?
                if (((c0 & 0x3F) == 0) || (v0 == -1))
                {
                    // Check if we can skip the rest of the level 0
                    for (int i0 = c0 >> 6; i0 < ll0; i0 = c0 >> 6)
                    {
                        var t0 = 1L << (c0 & 0x3F);
                        v0 = bank.L0All[i0] | (t0 - 1);

                        if (v0 != -1)
                        {
                            break;
                        }
                        c0 = ++i0 << 6;

                        // Check if we can skip the rest of the level 1
                        for (int i1 = c0 >> 12; i1 < ll1; i1 = c0 >> 12)
                        {
                            var v1 = bank.L1All[i1] >> (i0 & 0x3F);
                            if (v1 != -1)
                            {
                                break;
                            }

                            i0 = 0;
                            c0 = ++i1 << 12;

                            // Check if we can skip the rest of the level 2
                            for (int i2 = c0 >> 18; i2 < ll2; i2 = c0 >> 18)
                            {
                                var v2 = bank.L2All[i2] >> (i1 & 0x3F);
                                if (v2 != -1)
                                {
                                    break;
                                }
                                i1 = 0;
                                c0 = ++i2 << 18;
                            }
                        }
                    }
                }

                // After hierarchical skip, verify we're still within bounds
                if (c0 >= capacity)
                {
                    c0 = 0;
                    indexInBank = 0;
                    break;
                }

                bitIndex = (bankIndex << _l0Shift) + ((c0 & ~0x3F) + BitOperations.TrailingZeroCount(~v0));
                return true;
            }
        }
        
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool FindNextUnsetL1(ref int bitIndex)
    {
        (Bank[] banks, int bankIndex, int indexInBank) = GetBankAndIndex(++bitIndex);

        // Check bounds before accessing arrays
        if (bankIndex < 0)
        {
            return false;
        }

        var c1 = indexInBank;

        for ( ; bankIndex < banks.Length; bankIndex++)
        {
            var bank = banks[bankIndex];
            
            var ll1 = _l1Size;
            var ll2 = _l2Size;
            var v1 = bank.L1All[indexInBank>>12];

            while (c1 < (ll1 << 6))
            {
                if (((c1 & 0x3F) == 0) || (v1 == -1))
                {
                    // Check if we can skip the rest of the level 1
                    for (int i1 = c1 >> 6; i1 < ll1; i1 = c1 >> 6)
                    {
                        var t1 = 1L << (c1 & 0x3F);
                        v1 = bank.L1All[i1] | (t1 - 1);
                        if (v1 != -1)
                        {
                            break;
                        }

                        c1 = ++i1 << 6;

                        // Check if we can skip the rest of the level 2
                        for (int i2 = c1 >> 12; i2 < ll2; i2 = c1 >> 12)
                        {
                            var v2 = bank.L2All[i2] >> (i1 & 0x3F);
                            if (v2 != -1)
                            {
                                break;
                            }

                            i1 = 0;
                            c1 = ++i2 << 12;
                        }
                    }

                    // After hierarchical skip, verify we're still within bounds
                    if (c1 >= (ll1 << 6))
                    {
                        c1 = 0;
                        break;
                    }
                }

                var t = 1L << (c1 & 0x3F);
                v1 = bank.L1Any[c1 >> 6] | (t - 1);
                bitIndex = (bankIndex << _l0Shift) + ((c1 & ~0x3F) + BitOperations.TrailingZeroCount(~v1));
                return true;
            }
        }
    
        return false;
    }

    public void Dispose()
    {
        var curBanks = _banks;
        if (Interlocked.CompareExchange(ref _banks, null, curBanks) == curBanks)
        {
            foreach (var bank in curBanks ?? [])
            {
                bank.Dispose();
            }
        }
    }

    public string Id { get; }
    public string Name => Id;
    public int? Count => null;
    public ResourceType Type => ResourceType.Bitmap;
    public IResource Parent { get; }

    public IEnumerable<IResource> Children => _banks.Select(bank => bank.MemoryBlock);

    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }
    public bool RegisterChild(IResource child) => false;

    public bool RemoveChild(IResource resource) => false;

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        // Capacity: bits set vs total capacity
        writer.WriteCapacity(TotalBitSet, Capacity);

        // Throughput: bitmap operations
        writer.WriteThroughput("SetL0", _setL0Count);
        writer.WriteThroughput("ClearL0", _clearL0Count);
        writer.WriteThroughput("SetL1", _setL1Count);
        writer.WriteThroughput("Grows", _growCount);
    }

    /// <inheritdoc />
    public void ResetPeaks()
    {
        // No high-water marks to reset
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties()
    {
        var banks = _banks;
        var props = new Dictionary<string, object>
        {
            // Overall stats
            ["Banks.Count"] = banks?.Length ?? 0,
            ["Capacity.Total"] = Capacity,
            ["Capacity.Used"] = TotalBitSet,
            ["Capacity.Utilization"] = Capacity > 0 ? (double)TotalBitSet / Capacity : 0.0,
            ["IsFull"] = IsFull,

            // Per-bank capacity
            ["BankBitCountCapacity"] = BankBitCountCapacity,

            // Operation counters
            ["Operations.SetL0"] = _setL0Count,
            ["Operations.ClearL0"] = _clearL0Count,
            ["Operations.SetL1"] = _setL1Count,
            ["Operations.Grows"] = _growCount,
        };

        // Per-bank breakdown (if not too many banks)
        if (banks != null && banks.Length <= 8)
        {
            for (int i = 0; i < banks.Length; i++)
            {
                props[$"Bank[{i}].TotalBitSet"] = banks[i].TotalBitSet;
                props[$"Bank[{i}].IsFull"] = banks[i].IsFull;
            }
        }

        return props;
    }
}
