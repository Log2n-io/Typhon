using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable AccessToDisposedClosure

namespace Typhon.Engine.Tests;

/// <summary>
/// Shared DI helper for allocator tests. Provides singleton IResourceRegistry and IMemoryAllocator.
/// </summary>
internal static class AllocatorTestServices
{
    private static readonly Lazy<IServiceProvider> _serviceProvider = new(() =>
    {
        var services = new ServiceCollection()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .BuildServiceProvider();
        return services;
    });

    public static IResourceRegistry ResourceRegistry => _serviceProvider.Value.GetRequiredService<IResourceRegistry>();
    public static IMemoryAllocator MemoryAllocator => _serviceProvider.Value.GetRequiredService<IMemoryAllocator>();
    public static IResource AllocationResource => ResourceRegistry.Allocation;
}

[TestFixture]
public class BlockAllocatorTests
{
    // Helper methods to create allocators with required DI dependencies
    private static BlockAllocator CreateBlockAllocator(int stride, int entryCountPerPage)
        => new(stride, entryCountPerPage, AllocatorTestServices.AllocationResource, AllocatorTestServices.MemoryAllocator);

    #region Constructor and Properties Tests

    [Test]
    public void Constructor_ValidParameters_CreatesAllocator()
    {
        using var allocator = CreateBlockAllocator(16, 64);

        Assert.That(allocator.Capacity, Is.EqualTo(64));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(32)]
    [TestCase(64)]
    [TestCase(128)]
    [TestCase(256)]
    [TestCase(512)]
    [TestCase(1024)]
    public void Constructor_PowerOfTwoEntryCounts_Succeeds(int entryCountPerPage)
    {
        using var allocator = CreateBlockAllocator(8, entryCountPerPage);
        Assert.That(allocator.Capacity, Is.EqualTo(entryCountPerPage));
    }

    [TestCase(3)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(9)]
    [TestCase(10)]
    [TestCase(15)]
    [TestCase(17)]
    [TestCase(100)]
    public void Constructor_NonPowerOfTwoEntryCount_ThrowsException(int entryCountPerPage)
        => Assert.Throws<ArgumentException>(() =>
        {
            _ = CreateBlockAllocator(8, entryCountPerPage);
        });

    [TestCase(1)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(32)]
    [TestCase(64)]
    [TestCase(128)]
    [TestCase(256)]
    [TestCase(1024)]
    public void Constructor_DifferentStrides_WorksCorrectly(int stride)
    {
        using var allocator = CreateBlockAllocator(stride, 64);

        var span = allocator.AllocateBlock(out _);
        Assert.That(span.Length, Is.EqualTo(stride));
    }

    [Test]
    public void Capacity_InitialValue_MatchesEntryCount()
    {
        const int entryCount = 128;
        using var allocator = CreateBlockAllocator(16, entryCount);

        Assert.That(allocator.Capacity, Is.EqualTo(entryCount));
    }

    [Test]
    public void AllocatedCount_InitialValue_IsZero()
    {
        using var allocator = CreateBlockAllocator(16, 64);

        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    #endregion

    #region Allocate Tests

    [Test]
    public void AllocateBlock_SingleBlock_ReturnsValidSpanAndId()
    {
        using var allocator = CreateBlockAllocator(16, 64);

        var span = allocator.AllocateBlock(out var blockId);

        Assert.That(blockId, Is.GreaterThanOrEqualTo(0));
        Assert.That(span.Length, Is.EqualTo(16));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1));
    }

    [Test]
    public void AllocateBlock_MultipleBlocks_ReturnsUniqueIds()
    {
        using var allocator = CreateBlockAllocator(16, 64);
        var ids = new HashSet<int>();

        for (int i = 0; i < 50; i++)
        {
            allocator.AllocateBlock(out var blockId);
            Assert.That(ids.Add(blockId), Is.True, $"Block ID {blockId} was already allocated");
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(50));
    }

    [Test]
    public void AllocateBlock_DataIntegrity_PreservesWrittenData()
    {
        using var allocator = CreateBlockAllocator(sizeof(long), 64);
        var allocations = new (int id, long value)[50];

        // Allocate multiple blocks and write unique values
        for (int i = 0; i < 50; i++)
        {
            var span = allocator.AllocateBlock(out var blockId);
            var value = (long)(i * 12345 + 67890);
            MemoryMarshal.Write(span, in value);
            allocations[i] = (blockId, value);
        }

        // Verify all values are intact
        for (int i = 0; i < allocations.Length; i++)
        {
            var span = allocator.GetBlock(allocations[i].id);
            var actualValue = MemoryMarshal.Read<long>(span);
            Assert.That(actualValue, Is.EqualTo(allocations[i].value),
                $"Data corruption at block {allocations[i].id}");
        }
    }

    [Test]
    public void AllocateBlock_SpanCanBeWrittenAndRead()
    {
        using var allocator = CreateBlockAllocator(sizeof(int) * 4, 64);

        var span = allocator.AllocateBlock(out var blockId);
        var intSpan = MemoryMarshal.Cast<byte, int>(span);

        intSpan[0] = 100;
        intSpan[1] = 200;
        intSpan[2] = 300;
        intSpan[3] = 400;

        var retrievedSpan = allocator.GetBlock(blockId);
        var retrievedIntSpan = MemoryMarshal.Cast<byte, int>(retrievedSpan);

        Assert.That(retrievedIntSpan[0], Is.EqualTo(100));
        Assert.That(retrievedIntSpan[1], Is.EqualTo(200));
        Assert.That(retrievedIntSpan[2], Is.EqualTo(300));
        Assert.That(retrievedIntSpan[3], Is.EqualTo(400));
    }

    [Test]
    public void AllocateBlock_SequentialAllocations_HaveSequentialIds()
    {
        using var allocator = CreateBlockAllocator(8, 64);
        var ids = new List<int>();

        for (int i = 0; i < 10; i++)
        {
            allocator.AllocateBlock(out var id);
            ids.Add(id);
        }

        // IDs should be sequential (0, 1, 2, ...)
        for (int i = 0; i < ids.Count; i++)
        {
            Assert.That(ids[i], Is.EqualTo(i));
        }
    }

    #endregion

    #region GetBlock Tests

    [Test]
    public void GetBlock_ValidBlockId_ReturnsSameDataAsAllocate()
    {
        using var allocator = CreateBlockAllocator(sizeof(long), 64);

        var allocatedSpan = allocator.AllocateBlock(out var blockId);
        var value = 0x123456789ABCDEFL;
        MemoryMarshal.Write(allocatedSpan, in value);

        var retrievedSpan = allocator.GetBlock(blockId);
        var retrievedValue = MemoryMarshal.Read<long>(retrievedSpan);

        Assert.That(retrievedValue, Is.EqualTo(value));
    }

    [Test]
    public void GetBlock_MultipleBlocks_ReturnsCorrectData()
    {
        using var allocator = CreateBlockAllocator(sizeof(int), 64);
        var blocks = new (int id, int value)[20];

        for (int i = 0; i < 20; i++)
        {
            var span = allocator.AllocateBlock(out var id);
            var value = i * 1000;
            MemoryMarshal.Write(span, in value);
            blocks[i] = (id, value);
        }

        for (int i = 0; i < blocks.Length; i++)
        {
            var span = allocator.GetBlock(blocks[i].id);
            var retrievedValue = MemoryMarshal.Read<int>(span);
            Assert.That(retrievedValue, Is.EqualTo(blocks[i].value));
        }
    }

    [Test]
    public void GetBlock_SpanLengthMatchesStride()
    {
        const int stride = 32;
        using var allocator = CreateBlockAllocator(stride, 64);

        allocator.AllocateBlock(out var blockId);
        var span = allocator.GetBlock(blockId);

        Assert.That(span.Length, Is.EqualTo(stride));
    }

    [Test]
    public void GetBlock_ModifyingSpan_PersistsChanges()
    {
        using var allocator = CreateBlockAllocator(sizeof(long), 64);

        allocator.AllocateBlock(out var blockId);

        // First modification
        var span1 = allocator.GetBlock(blockId);
        var value1 = 111L;
        MemoryMarshal.Write(span1, in value1);

        // Verify first modification
        var span2 = allocator.GetBlock(blockId);
        Assert.That(MemoryMarshal.Read<long>(span2), Is.EqualTo(111L));

        // Second modification
        var value2 = 222L;
        MemoryMarshal.Write(span2, in value2);

        // Verify second modification
        var span3 = allocator.GetBlock(blockId);
        Assert.That(MemoryMarshal.Read<long>(span3), Is.EqualTo(222L));
    }

    #endregion

    #region FreeBlock Tests

    [Test]
    public void FreeBlock_SingleBlock_DecreasesAllocatedCount()
    {
        using var allocator = CreateBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockId);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1));

        allocator.FreeBlock(blockId);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [Test]
    public void FreeBlock_MultipleBlocks_DecreasesAllocatedCountCorrectly()
    {
        using var allocator = CreateBlockAllocator(16, 64);
        var ids = new int[10];

        for (int i = 0; i < 10; i++)
        {
            allocator.AllocateBlock(out ids[i]);
        }
        Assert.That(allocator.AllocatedCount, Is.EqualTo(10));

        for (int i = 0; i < 5; i++)
        {
            allocator.FreeBlock(ids[i]);
        }
        Assert.That(allocator.AllocatedCount, Is.EqualTo(5));

        for (int i = 5; i < 10; i++)
        {
            allocator.FreeBlock(ids[i]);
        }
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [Test]
    public void FreeBlock_ThenReallocate_ReusesFreedSlots()
    {
        using var allocator = CreateBlockAllocator(sizeof(int), 64);

        // Allocate blocks
        allocator.AllocateBlock(out int _);
        allocator.AllocateBlock(out var id2);
        allocator.AllocateBlock(out int _);

        // Free the middle one
        allocator.FreeBlock(id2);

        // Reallocate - should get back the freed slot
        allocator.AllocateBlock(out var newId);
        Assert.That(newId, Is.EqualTo(id2));
    }

    [Test]
    public void FreeBlock_AllBlocks_ResetsAllocatedCount()
    {
        using var allocator = CreateBlockAllocator(16, 64);
        var ids = new List<int>();

        for (int i = 0; i < 32; i++)
        {
            allocator.AllocateBlock(out var id);
            ids.Add(id);
        }

        foreach (var id in ids)
        {
            allocator.FreeBlock(id);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [Test]
    public void FreeBlock_InterleavedAllocateAndFree_MaintainsIntegrity()
    {
        using var allocator = CreateBlockAllocator(sizeof(int), 64);
        var activeBlocks = new Dictionary<int, int>();

        for (int iteration = 0; iteration < 50; iteration++)
        {
            // Allocate some
            for (int i = 0; i < 5; i++)
            {
                var span = allocator.AllocateBlock(out var id);
                var value = iteration * 1000 + i;
                MemoryMarshal.Write(span, in value);
                activeBlocks[id] = value;
            }

            // Free some (roughly half)
            var toRemove = activeBlocks.Keys.Where(k => k % 2 == 0).ToList();
            foreach (var id in toRemove)
            {
                allocator.FreeBlock(id);
                activeBlocks.Remove(id);
            }

            // Verify remaining blocks
            foreach (var kvp in activeBlocks)
            {
                var span = allocator.GetBlock(kvp.Key);
                Assert.That(MemoryMarshal.Read<int>(span), Is.EqualTo(kvp.Value));
            }
        }
    }

    #endregion

    #region Resize Tests

    [Test]
    public void Resize_TriggerByAllocation_ExpandsCapacity()
    {
        const int initialPageSize = 4;
        using var allocator = CreateBlockAllocator(16, initialPageSize);

        Assert.That(allocator.Capacity, Is.EqualTo(initialPageSize));

        // Allocate more than the initial capacity
        for (int i = 0; i < initialPageSize + 1; i++)
        {
            allocator.AllocateBlock(out _);
        }

        Assert.That(allocator.Capacity, Is.GreaterThan(initialPageSize));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(initialPageSize + 1));
    }

    [Test]
    public void Resize_MultipleResizes_MaintainsDataIntegrity()
    {
        const int pageSize = 4;
        const int allocationCount = 64; // Will trigger multiple resizes

        using var allocator = CreateBlockAllocator(sizeof(long), pageSize);
        var allocations = new (int id, long value)[allocationCount];

        for (int i = 0; i < allocationCount; i++)
        {
            var span = allocator.AllocateBlock(out var id);
            var value = (long)(i * 11111);
            MemoryMarshal.Write(span, in value);
            allocations[i] = (id, value);
        }

        // Verify all data is intact after resizes
        for (int i = 0; i < allocationCount; i++)
        {
            var span = allocator.GetBlock(allocations[i].id);
            var actualValue = MemoryMarshal.Read<long>(span);
            Assert.That(actualValue, Is.EqualTo(allocations[i].value),
                $"Data corruption at block {allocations[i].id} after resize");
        }
    }

    [Test]
    public void Resize_CapacityGrowsInPowerOfTwo()
    {
        const int pageSize = 8;
        using var allocator = CreateBlockAllocator(16, pageSize);

        // Fill up first page
        for (int i = 0; i < pageSize; i++)
        {
            allocator.AllocateBlock(out _);
        }
        Assert.That(allocator.Capacity, Is.EqualTo(pageSize));

        // Trigger resize
        allocator.AllocateBlock(out _);

        // Capacity should be at least 2x original
        Assert.That(allocator.Capacity, Is.GreaterThanOrEqualTo(pageSize * 2));
    }

    [Test]
    public void Resize_AfterFreeAndReallocate_NoUnnecessaryGrowth()
    {
        const int pageSize = 8;
        using var allocator = CreateBlockAllocator(16, pageSize);
        var ids = new int[pageSize];

        // Fill up first page
        for (int i = 0; i < pageSize; i++)
        {
            allocator.AllocateBlock(out ids[i]);
        }
        var initialCapacity = allocator.Capacity;

        // Free all
        for (int i = 0; i < pageSize; i++)
        {
            allocator.FreeBlock(ids[i]);
        }

        // Reallocate same count - should not grow
        for (int i = 0; i < pageSize; i++)
        {
            allocator.AllocateBlock(out _);
        }

        Assert.That(allocator.Capacity, Is.EqualTo(initialCapacity));
    }

    [Test]
    public void Resize_LargeAllocationCount_HandlesGracefully()
    {
        const int pageSize = 16;
        const int allocationCount = 1024;

        using var allocator = CreateBlockAllocator(sizeof(int), pageSize);

        for (int i = 0; i < allocationCount; i++)
        {
            var span = allocator.AllocateBlock(out _);
            MemoryMarshal.Write(span, in i);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(allocationCount));
        Assert.That(allocator.Capacity, Is.GreaterThanOrEqualTo(allocationCount));
    }

    #endregion

    #region Dispose Tests

    [Test]
    public void Dispose_NormalDispose_DoesNotThrow()
    {
        var allocator = CreateBlockAllocator(16, 64);

        allocator.AllocateBlock(out _);
        allocator.AllocateBlock(out _);

        Assert.DoesNotThrow(() => allocator.Dispose());
    }

    [Test]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var allocator = CreateBlockAllocator(16, 64);

        allocator.Dispose();

        Assert.DoesNotThrow(() => allocator.Dispose());
    }

    [Test]
    public void Dispose_WithActiveAllocations_DoesNotThrow()
    {
        var allocator = CreateBlockAllocator(16, 64);

        for (int i = 0; i < 10; i++)
        {
            allocator.AllocateBlock(out _);
        }

        Assert.DoesNotThrow(() => allocator.Dispose());
    }

    #endregion

    #region Edge Cases

    [Test]
    public void EdgeCase_LargeStride_WorksCorrectly()
    {
        const int largeStride = 4096;
        using var allocator = CreateBlockAllocator(largeStride, 16);

        var span = allocator.AllocateBlock(out var blockId);
        Assert.That(span.Length, Is.EqualTo(largeStride));

        // Fill the entire block
        for (int i = 0; i < largeStride; i++)
        {
            span[i] = (byte)(i % 256);
        }

        // Verify
        var retrieved = allocator.GetBlock(blockId);
        for (int i = 0; i < largeStride; i++)
        {
            Assert.That(retrieved[i], Is.EqualTo((byte)(i % 256)));
        }
    }

    [Test]
    public void EdgeCase_MinimalStride_WorksCorrectly()
    {
        const int minimalStride = 1;
        using var allocator = CreateBlockAllocator(minimalStride, 64);

        var span = allocator.AllocateBlock(out var blockId);
        span[0] = 42;

        Assert.That(allocator.GetBlock(blockId)[0], Is.EqualTo(42));
        Assert.That(span.Length, Is.EqualTo(minimalStride));
    }

    [Test]
    public void EdgeCase_MinimalPageSize_WorksCorrectly()
    {
        const int minimalPageSize = 1;
        using var allocator = CreateBlockAllocator(16, minimalPageSize);

        // Should trigger resize on second allocation
        allocator.AllocateBlock(out var id1);
        allocator.AllocateBlock(out var id2);

        Assert.That(id1, Is.Not.EqualTo(id2));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(2));
    }

    [Test]
    public void EdgeCase_AllocateFreeAllocatePattern_WorksCorrectly()
    {
        using var allocator = CreateBlockAllocator(sizeof(int), 64);

        for (int round = 0; round < 100; round++)
        {
            var span = allocator.AllocateBlock(out var id);
            MemoryMarshal.Write(span, in round);

            Assert.That(MemoryMarshal.Read<int>(allocator.GetBlock(id)), Is.EqualTo(round));

            allocator.FreeBlock(id);
        }

        // After all allocate/free cycles, count should be 0
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [Test]
    public void EdgeCase_AllocateAllThenFreeAll_WorksCorrectly()
    {
        const int count = 256;
        using var allocator = CreateBlockAllocator(sizeof(long), 32);
        var ids = new int[count];

        // Allocate all
        for (int i = 0; i < count; i++)
        {
            var span = allocator.AllocateBlock(out ids[i]);
            var value = (long)i;
            MemoryMarshal.Write(span, in value);
        }

        // Verify all
        for (int i = 0; i < count; i++)
        {
            Assert.That(MemoryMarshal.Read<long>(allocator.GetBlock(ids[i])), Is.EqualTo((long)i));
        }

        // Free all
        for (int i = 0; i < count; i++)
        {
            allocator.FreeBlock(ids[i]);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    #endregion
}

[TestFixture]
public class StructAllocatorTests
{
    // Helper to create StructAllocator with required DI dependencies
    private static StructAllocator<T> CreateStructAllocator<T>(int entryCountPerPage) where T : struct, ICleanable
        => new(entryCountPerPage, AllocatorTestServices.AllocationResource, AllocatorTestServices.MemoryAllocator);

    [StructLayout(LayoutKind.Sequential)]
    public struct TestCleanableStruct : ICleanable
    {
        public int Value;
        public float FloatValue;
        public long LongValue;
        private int _cleanupCount;

        public int CleanupCallCount => _cleanupCount;

        public void Cleanup()
        {
            _cleanupCount++;
            // Reset values on cleanup
            Value = 0;
            FloatValue = 0;
            LongValue = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SimpleCleanableStruct : ICleanable
    {
        public int A;
        public int B;

        public void Cleanup()
        {
            A = -1;
            B = -1;
        }
    }

    #region Constructor Tests

    [Test]
    public void Constructor_ValidEntryCount_CreatesAllocator()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);

        Assert.That(allocator.Capacity, Is.EqualTo(64));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(64)]
    [TestCase(256)]
    public void Constructor_PowerOfTwoEntryCounts_Succeeds(int entryCount)
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(entryCount);
        Assert.That(allocator.Capacity, Is.EqualTo(entryCount));
    }

    #endregion

    #region Allocate and Get Tests

    [Test]
    public void Allocate_ReturnsValidRef()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);

        ref var item = ref allocator.Allocate(out var blockId);
        item.A = 100;
        item.B = 200;

        Assert.That(blockId, Is.GreaterThanOrEqualTo(0));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1));
    }

    [Test]
    public void Allocate_MultipleItems_ReturnsUniqueIds()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);
        var ids = new HashSet<int>();

        for (int i = 0; i < 32; i++)
        {
            ref var item = ref allocator.Allocate(out var id);
            item.A = i;
            item.B = i * 2;
            Assert.That(ids.Add(id), Is.True);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(32));
    }

    [Test]
    public void Get_ReturnsCorrectData()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);

        ref var allocated = ref allocator.Allocate(out var blockId);
        allocated.A = 42;
        allocated.B = 84;

        ref var retrieved = ref allocator.Get(blockId);

        Assert.That(retrieved.A, Is.EqualTo(42));
        Assert.That(retrieved.B, Is.EqualTo(84));
    }

    [Test]
    public void Get_ModifyingRef_PersistsChanges()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);

        allocator.Allocate(out var blockId);

        ref var item1 = ref allocator.Get(blockId);
        item1.A = 111;

        ref var item2 = ref allocator.Get(blockId);
        Assert.That(item2.A, Is.EqualTo(111));

        item2.B = 222;

        ref var item3 = ref allocator.Get(blockId);
        Assert.That(item3.A, Is.EqualTo(111));
        Assert.That(item3.B, Is.EqualTo(222));
    }

    [Test]
    public void Allocate_DataIntegrity_PreservesWrittenData()
    {
        using var allocator = CreateStructAllocator<TestCleanableStruct>(64);
        var allocations = new (int id, int value, float floatValue, long longValue)[30];

        for (int i = 0; i < 30; i++)
        {
            ref var item = ref allocator.Allocate(out var id);
            item.Value = i * 10;
            item.FloatValue = i * 1.5f;
            item.LongValue = i * 100000L;
            allocations[i] = (id, item.Value, item.FloatValue, item.LongValue);
        }

        for (int i = 0; i < allocations.Length; i++)
        {
            ref var item = ref allocator.Get(allocations[i].id);
            Assert.That(item.Value, Is.EqualTo(allocations[i].value));
            Assert.That(item.FloatValue, Is.EqualTo(allocations[i].floatValue));
            Assert.That(item.LongValue, Is.EqualTo(allocations[i].longValue));
        }
    }

    #endregion

    #region Free and Cleanup Tests

    [Test]
    public void Free_CallsCleanup()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);

        ref var item = ref allocator.Allocate(out var blockId);
        item.A = 100;
        item.B = 200;

        allocator.Free(blockId);

        // The cleanup should have been called - values should be -1
        // Note: After free, the memory is still accessible but the values were cleaned up
        ref var freed = ref allocator.Get(blockId);
        Assert.That(freed.A, Is.EqualTo(-1));
        Assert.That(freed.B, Is.EqualTo(-1));
    }

    [Test]
    public void Free_DecreasesAllocatedCount()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);

        allocator.Allocate(out var id1);
        allocator.Allocate(out var id2);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(2));

        allocator.Free(id1);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1));

        allocator.Free(id2);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [Test]
    public void Free_ThenReallocate_ReusesSlot()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);

        allocator.Allocate(out int _);
        allocator.Allocate(out var id2);
        allocator.Allocate(out int _);

        allocator.Free(id2);

        allocator.Allocate(out var newId);
        Assert.That(newId, Is.EqualTo(id2));
    }

    [Test]
    public void Free_InterleavedOperations_MaintainsIntegrity()
    {
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(64);
        var active = new Dictionary<int, (int a, int b)>();

        for (int iteration = 0; iteration < 50; iteration++)
        {
            // Allocate some
            for (int i = 0; i < 3; i++)
            {
                ref var item = ref allocator.Allocate(out var id);
                item.A = iteration * 100 + i;
                item.B = iteration * 1000 + i;
                active[id] = (item.A, item.B);
            }

            // Free some
            var toRemove = active.Keys.Where(k => k % 2 == 0).ToList();
            foreach (var id in toRemove)
            {
                allocator.Free(id);
                active.Remove(id);
            }

            // Verify remaining
            foreach (var kvp in active)
            {
                ref var item = ref allocator.Get(kvp.Key);
                Assert.That(item.A, Is.EqualTo(kvp.Value.a));
                Assert.That(item.B, Is.EqualTo(kvp.Value.b));
            }
        }
    }

    #endregion

    #region Resize Tests

    [Test]
    public void Resize_TriggeredByAllocation_MaintainsData()
    {
        const int pageSize = 4;
        using var allocator = CreateStructAllocator<SimpleCleanableStruct>(pageSize);
        var allocations = new (int id, int a, int b)[pageSize * 4];

        for (int i = 0; i < allocations.Length; i++)
        {
            ref var item = ref allocator.Allocate(out var id);
            item.A = i;
            item.B = i * 2;
            allocations[i] = (id, i, i * 2);
        }

        // Verify after multiple resizes
        for (int i = 0; i < allocations.Length; i++)
        {
            ref var item = ref allocator.Get(allocations[i].id);
            Assert.That(item.A, Is.EqualTo(allocations[i].a));
            Assert.That(item.B, Is.EqualTo(allocations[i].b));
        }
    }

    #endregion
}

[TestFixture]
public class UnmanagedStructAllocatorTests
{
    // Helper to create UnmanagedStructAllocator with required DI dependencies
    private static UnmanagedStructAllocator<T> CreateUnmanagedStructAllocator<T>(int entryCountPerPage) where T : unmanaged
        => new(entryCountPerPage, AllocatorTestServices.AllocationResource, AllocatorTestServices.MemoryAllocator);

    public struct TestUnmanagedStruct
    {
        public int Value;
        public float FloatValue;
        public long LongValue;
        public double DoubleValue;
    }

    public struct SimpleUnmanagedStruct
    {
        public int X;
        public int Y;
        public int Z;
    }

    #region Constructor Tests

    [Test]
    public void Constructor_ValidEntryCount_CreatesAllocator()
    {
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(64);

        Assert.That(allocator.Capacity, Is.EqualTo(64));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(64)]
    [TestCase(256)]
    public void Constructor_PowerOfTwoEntryCounts_Succeeds(int entryCount)
    {
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(entryCount);
        Assert.That(allocator.Capacity, Is.EqualTo(entryCount));
    }

    #endregion

    #region Allocate and Get Tests

    [Test]
    public void Allocate_ReturnsValidRef()
    {
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(64);

        ref var item = ref allocator.Allocate(out var blockId);
        item.X = 10;
        item.Y = 20;
        item.Z = 30;

        Assert.That(blockId, Is.GreaterThanOrEqualTo(0));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1));
    }

    [Test]
    public void Allocate_MultipleItems_ReturnsUniqueIds()
    {
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(64);
        var ids = new HashSet<int>();

        for (int i = 0; i < 32; i++)
        {
            ref var item = ref allocator.Allocate(out var id);
            item.X = i;
            Assert.That(ids.Add(id), Is.True);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(32));
    }

    [Test]
    public void Get_ReturnsCorrectData()
    {
        using var allocator = CreateUnmanagedStructAllocator<TestUnmanagedStruct>(64);

        ref var allocated = ref allocator.Allocate(out var blockId);
        allocated.Value = 42;
        allocated.FloatValue = 3.14f;
        allocated.LongValue = 123456789L;
        allocated.DoubleValue = 2.71828;

        ref var retrieved = ref allocator.Get(blockId);

        Assert.That(retrieved.Value, Is.EqualTo(42));
        Assert.That(retrieved.FloatValue, Is.EqualTo(3.14f));
        Assert.That(retrieved.LongValue, Is.EqualTo(123456789L));
        Assert.That(retrieved.DoubleValue, Is.EqualTo(2.71828));
    }

    [Test]
    public void Get_ModifyingRef_PersistsChanges()
    {
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(64);

        allocator.Allocate(out var blockId);

        ref var item1 = ref allocator.Get(blockId);
        item1.X = 100;
        item1.Y = 200;
        item1.Z = 300;

        ref var item2 = ref allocator.Get(blockId);
        Assert.That(item2.X, Is.EqualTo(100));
        Assert.That(item2.Y, Is.EqualTo(200));
        Assert.That(item2.Z, Is.EqualTo(300));
    }

    [Test]
    public void Allocate_DataIntegrity_PreservesWrittenData()
    {
        using var allocator = CreateUnmanagedStructAllocator<TestUnmanagedStruct>(64);
        var allocations = new (int id, int value, float floatValue, long longValue, double doubleValue)[30];

        for (int i = 0; i < 30; i++)
        {
            ref var item = ref allocator.Allocate(out var id);
            item.Value = i * 10;
            item.FloatValue = i * 1.5f;
            item.LongValue = i * 100000L;
            item.DoubleValue = i * 0.001;
            allocations[i] = (id, item.Value, item.FloatValue, item.LongValue, item.DoubleValue);
        }

        for (int i = 0; i < allocations.Length; i++)
        {
            ref var item = ref allocator.Get(allocations[i].id);
            Assert.That(item.Value, Is.EqualTo(allocations[i].value));
            Assert.That(item.FloatValue, Is.EqualTo(allocations[i].floatValue));
            Assert.That(item.LongValue, Is.EqualTo(allocations[i].longValue));
            Assert.That(item.DoubleValue, Is.EqualTo(allocations[i].doubleValue));
        }
    }

    #endregion

    #region Free Tests

    [Test]
    public void Free_DecreasesAllocatedCount()
    {
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(64);

        allocator.Allocate(out var id1);
        allocator.Allocate(out var id2);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(2));

        allocator.Free(id1);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1));

        allocator.Free(id2);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [Test]
    public void Free_ThenReallocate_ReusesSlot()
    {
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(64);

        allocator.Allocate(out int _);
        allocator.Allocate(out var id2);
        allocator.Allocate(out int _);

        allocator.Free(id2);

        allocator.Allocate(out var newId);
        Assert.That(newId, Is.EqualTo(id2));
    }

    [Test]
    public void Free_InterleavedOperations_MaintainsIntegrity()
    {
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(64);
        var active = new Dictionary<int, (int x, int y, int z)>();

        for (int iteration = 0; iteration < 50; iteration++)
        {
            // Allocate some
            for (int i = 0; i < 3; i++)
            {
                ref var item = ref allocator.Allocate(out var id);
                item.X = iteration * 100 + i;
                item.Y = iteration * 1000 + i;
                item.Z = iteration * 10000 + i;
                active[id] = (item.X, item.Y, item.Z);
            }

            // Free some
            var toRemove = active.Keys.Where(k => k % 2 == 0).ToList();
            foreach (var id in toRemove)
            {
                allocator.Free(id);
                active.Remove(id);
            }

            // Verify remaining
            foreach (var kvp in active)
            {
                ref var item = ref allocator.Get(kvp.Key);
                Assert.That(item.X, Is.EqualTo(kvp.Value.x));
                Assert.That(item.Y, Is.EqualTo(kvp.Value.y));
                Assert.That(item.Z, Is.EqualTo(kvp.Value.z));
            }
        }
    }

    #endregion

    #region Resize Tests

    [Test]
    public void Resize_TriggeredByAllocation_MaintainsData()
    {
        const int pageSize = 4;
        using var allocator = CreateUnmanagedStructAllocator<SimpleUnmanagedStruct>(pageSize);
        var allocations = new (int id, int x, int y, int z)[pageSize * 4];

        for (int i = 0; i < allocations.Length; i++)
        {
            ref var item = ref allocator.Allocate(out var id);
            item.X = i;
            item.Y = i * 2;
            item.Z = i * 3;
            allocations[i] = (id, i, i * 2, i * 3);
        }

        // Verify after multiple resizes
        for (int i = 0; i < allocations.Length; i++)
        {
            ref var item = ref allocator.Get(allocations[i].id);
            Assert.That(item.X, Is.EqualTo(allocations[i].x));
            Assert.That(item.Y, Is.EqualTo(allocations[i].y));
            Assert.That(item.Z, Is.EqualTo(allocations[i].z));
        }
    }

    #endregion
}

[TestFixture]
public class BlockAllocatorThreadSafetyTests
{
    // Helper to create BlockAllocator with required DI dependencies
    private static BlockAllocator CreateBlockAllocator(int stride, int entryCountPerPage)
        => new(stride, entryCountPerPage, AllocatorTestServices.AllocationResource, AllocatorTestServices.MemoryAllocator);

    #region Concurrent Allocation Tests

    [Test]
    public void ConcurrentAllocation_MultipleThreads_NoDataCorruption()
    {
        const int threadCount = 8;
        const int allocationsPerThread = 100;

        using var allocator = CreateBlockAllocator(sizeof(long), 64);
        var allAllocations = new ConcurrentBag<(int id, long value)>();
        var errors = new ConcurrentBag<string>();

        var threads = new Thread[threadCount];
        var barrier = new Barrier(threadCount);

        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait(); // Synchronize start

                for (int i = 0; i < allocationsPerThread; i++)
                {
                    var span = allocator.AllocateBlock(out var id);
                    var value = (long)(threadIndex * 10000 + i);
                    MemoryMarshal.Write(span, in value);
                    allAllocations.Add((id, value));
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Verify all allocations
        Assert.That(allocator.AllocatedCount, Is.EqualTo(threadCount * allocationsPerThread));

        foreach (var (id, expectedValue) in allAllocations)
        {
            var span = allocator.GetBlock(id);
            var actualValue = MemoryMarshal.Read<long>(span);
            if (actualValue != expectedValue)
            {
                errors.Add($"Block {id}: expected {expectedValue}, got {actualValue}");
            }
        }

        Assert.That(errors, Is.Empty, $"Data corruption detected:\n{string.Join("\n", errors.Take(10))}");
    }

    [Test]
    public void ConcurrentAllocation_UniqueBlockIds()
    {
        const int threadCount = 8;
        const int allocationsPerThread = 100;

        using var allocator = CreateBlockAllocator(16, 64);
        var allIds = new ConcurrentBag<int>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (int i = 0; i < allocationsPerThread; i++)
                {
                    allocator.AllocateBlock(out var id);
                    allIds.Add(id);
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        var idList = allIds.ToList();
        var uniqueIds = new HashSet<int>(idList);

        Assert.That(uniqueIds.Count, Is.EqualTo(idList.Count),
            "Duplicate block IDs were allocated");
    }

    [Test]
    public void ConcurrentAllocation_TriggersResize_NoDataLoss()
    {
        const int threadCount = 4;
        const int allocationsPerThread = 500;
        const int smallPageSize = 8; // Force many resizes

        using var allocator = CreateBlockAllocator(sizeof(int), smallPageSize);
        var allAllocations = new ConcurrentDictionary<int, int>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (int i = 0; i < allocationsPerThread; i++)
                {
                    var span = allocator.AllocateBlock(out var id);
                    var value = threadIndex * 10000 + i;
                    MemoryMarshal.Write(span, in value);
                    allAllocations[id] = value;
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Verify
        Assert.That(allocator.AllocatedCount, Is.EqualTo(threadCount * allocationsPerThread));

        foreach (var kvp in allAllocations)
        {
            var span = allocator.GetBlock(kvp.Key);
            Assert.That(MemoryMarshal.Read<int>(span), Is.EqualTo(kvp.Value));
        }
    }

    #endregion

    #region Concurrent Free Tests

    [Test]
    public void ConcurrentFree_MultipleThreads_CorrectCount()
    {
        const int totalAllocations = 1000;
        const int threadCount = 4;

        using var allocator = CreateBlockAllocator(16, 64);
        var ids = new int[totalAllocations];

        // Allocate all first
        for (int i = 0; i < totalAllocations; i++)
        {
            allocator.AllocateBlock(out ids[i]);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(totalAllocations));

        // Free concurrently
        var allocationsPerThread = totalAllocations / threadCount;
        var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var startIndex = t * allocationsPerThread;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (int i = 0; i < allocationsPerThread; i++)
                {
                    allocator.FreeBlock(ids[startIndex + i]);
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    #endregion

    #region Mixed Concurrent Operations Tests

    [Test]
    [CancelAfter(15000)]
    [Category("Sensitive")] // timing-dependent concurrency test — flaky under parallel CPU load; runs in the gate's serial quiet pass
    public void ConcurrentAllocateAndFree_MaintainsConsistency()
    {
        const int threadCount = 8;
        const int operationsPerThread = 500;

        using var allocator = CreateBlockAllocator(sizeof(int), 64);
        var activeBlocks = new ConcurrentDictionary<int, int>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                var random = new Random(threadIndex * 1000);
                barrier.SignalAndWait();

                for (int i = 0; i < operationsPerThread; i++)
                {
                    if (random.Next(2) == 0 || activeBlocks.IsEmpty)
                    {
                        // Allocate
                        var span = allocator.AllocateBlock(out var id);
                        var value = threadIndex * 100000 + i;
                        MemoryMarshal.Write(span, in value);
                        activeBlocks[id] = value;
                    }
                    else
                    {
                        // Free a random block (thread-safe removal)
                        foreach (var key in activeBlocks.Keys)
                        {
                            if (activeBlocks.TryRemove(key, out _))
                            {
                                allocator.FreeBlock(key);
                                break;
                            }
                        }
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Verify remaining allocations
        Assert.That(allocator.AllocatedCount, Is.EqualTo(activeBlocks.Count));

        foreach (var kvp in activeBlocks)
        {
            var span = allocator.GetBlock(kvp.Key);
            Assert.That(MemoryMarshal.Read<int>(span), Is.EqualTo(kvp.Value));
        }
    }

    [Test]
    public void ConcurrentReadWrite_NoCorruption()
    {
        const int threadCount = 8;
        const int iterationsPerThread = 1000;
        const int blockCount = 100;

        using var allocator = CreateBlockAllocator(sizeof(long), 64);
        var ids = new int[blockCount];

        // Pre-allocate blocks
        for (int i = 0; i < blockCount; i++)
        {
            var span = allocator.AllocateBlock(out ids[i]);
            var value = (long)i;
            MemoryMarshal.Write(span, in value);
        }

        var errors = new ConcurrentBag<string>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                var random = new Random(threadIndex * 1000);
                barrier.SignalAndWait();

                for (int i = 0; i < iterationsPerThread; i++)
                {
                    var blockIndex = random.Next(blockCount);
                    var id = ids[blockIndex];

                    // Read current value
                    var span = allocator.GetBlock(id);
                    var currentValue = MemoryMarshal.Read<long>(span);

                    // Verify it's a valid value
                    if (currentValue < 0 || currentValue >= blockCount * 1000000L + threadCount * iterationsPerThread)
                    {
                        errors.Add($"Invalid value {currentValue} at block {id}");
                    }

                    // Write new value
                    var newValue = blockIndex * 1000000L + threadIndex * iterationsPerThread + i;
                    MemoryMarshal.Write(span, in newValue);
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.That(errors, Is.Empty,
            $"Corruption detected:\n{string.Join("\n", errors.Take(10))}");
    }

    #endregion

    #region Stress Tests

    [Test]
    [CancelAfter(30000)] // 30 second timeout
    public void StressTest_HighContention_NoDeadlock()
    {
        const int threadCount = 16;
        const int operationsPerThread = 1000;

        using var allocator = CreateBlockAllocator(sizeof(int), 8); // Small page to force contention
        var activeBlocks = new ConcurrentDictionary<int, int>();
        var completedOperations = 0;
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                var random = new Random(threadIndex);
                barrier.SignalAndWait();

                for (int i = 0; i < operationsPerThread; i++)
                {
                    var op = random.Next(3);
                    switch (op)
                    {
                        case 0: // Allocate
                            var span = allocator.AllocateBlock(out var id);
                            var value = threadIndex * 100000 + i;
                            MemoryMarshal.Write(span, in value);
                            activeBlocks[id] = value;
                            break;

                        case 1: // Free
                            foreach (var key in activeBlocks.Keys)
                            {
                                if (activeBlocks.TryRemove(key, out _))
                                {
                                    allocator.FreeBlock(key);
                                    break;
                                }
                            }
                            break;

                        case 2: // Read/Verify
                            foreach (var kvp in activeBlocks)
                            {
                                var readSpan = allocator.GetBlock(kvp.Key);
                                _ = MemoryMarshal.Read<int>(readSpan);
                                break;
                            }
                            break;
                    }

                    Interlocked.Increment(ref completedOperations);
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.That(completedOperations, Is.EqualTo(threadCount * operationsPerThread));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(activeBlocks.Count));
    }

    [Test]
    public async Task StressTest_AsyncOperations_NoCorruption()
    {
        const int taskCount = 16;
        const int operationsPerTask = 500;

        using var allocator = CreateBlockAllocator(sizeof(long), 64);
        var activeBlocks = new ConcurrentDictionary<int, long>();
        var errors = new ConcurrentBag<string>();

        var tasks = new Task[taskCount];
        for (int t = 0; t < taskCount; t++)
        {
            var taskIndex = t;
            tasks[t] = Task.Run(() =>
            {
                var random = new Random(taskIndex * 1000);

                for (int i = 0; i < operationsPerTask; i++)
                {
                    if (random.Next(3) != 0 || activeBlocks.IsEmpty)
                    {
                        // Allocate
                        var span = allocator.AllocateBlock(out var id);
                        var value = (long)(taskIndex * 1000000 + i);
                        MemoryMarshal.Write(span, in value);
                        activeBlocks[id] = value;
                    }
                    else
                    {
                        // Free
                        foreach (var key in activeBlocks.Keys)
                        {
                            if (activeBlocks.TryRemove(key, out var expectedValue))
                            {
                                // Verify before freeing
                                var span = allocator.GetBlock(key);
                                var actualValue = MemoryMarshal.Read<long>(span);
                                if (actualValue != expectedValue)
                                {
                                    errors.Add($"Block {key}: expected {expectedValue}, got {actualValue}");
                                }
                                allocator.FreeBlock(key);
                                break;
                            }
                        }
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.That(errors, Is.Empty,
            $"Corruption detected:\n{string.Join("\n", errors.Take(10))}");
        Assert.That(allocator.AllocatedCount, Is.EqualTo(activeBlocks.Count));
    }

    [Test]
    public void StressTest_RapidAllocFree_SameThread()
    {
        const int iterations = 10000;

        using var allocator = CreateBlockAllocator(sizeof(int), 64);

        for (int i = 0; i < iterations; i++)
        {
            var span = allocator.AllocateBlock(out var id);
            MemoryMarshal.Write(span, in i);
            allocator.FreeBlock(id);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [Test]
    public void StressTest_AllocateInBatches_ThenFreeInBatches()
    {
        const int batchSize = 100;
        const int batchCount = 50;
        const int threadCount = 4;

        using var allocator = CreateBlockAllocator(sizeof(long), 32);
        var allIds = new ConcurrentBag<int>();
        var barrier = new Barrier(threadCount);

        var allocThreads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            allocThreads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();

                for (int batch = 0; batch < batchCount; batch++)
                {
                    for (int i = 0; i < batchSize; i++)
                    {
                        var span = allocator.AllocateBlock(out var id);
                        var value = (long)(threadIndex * 1000000 + batch * 1000 + i);
                        MemoryMarshal.Write(span, in value);
                        allIds.Add(id);
                    }
                }
            });
            allocThreads[t].Start();
        }

        foreach (var thread in allocThreads)
        {
            thread.Join();
        }

        var totalAllocated = threadCount * batchCount * batchSize;
        Assert.That(allocator.AllocatedCount, Is.EqualTo(totalAllocated));

        // Free all in batches across threads
        var idArray = allIds.ToArray();
        var idsPerThread = idArray.Length / threadCount;
        var freeBarrier = new Barrier(threadCount);

        var freeThreads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var start = t * idsPerThread;
            var count = (t == threadCount - 1) ? idArray.Length - start : idsPerThread;
            freeThreads[t] = new Thread(() =>
            {
                freeBarrier.SignalAndWait();

                for (int i = 0; i < count; i++)
                {
                    allocator.FreeBlock(idArray[start + i]);
                }
            });
            freeThreads[t].Start();
        }

        foreach (var thread in freeThreads)
        {
            thread.Join();
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    #endregion
}

[TestFixture]
public class TypedAllocatorThreadSafetyTests
{
    // Helper methods to create allocators with required DI dependencies
    private static BlockAllocator CreateBlockAllocator(int stride, int entryCountPerPage)
        => new(stride, entryCountPerPage, AllocatorTestServices.AllocationResource, AllocatorTestServices.MemoryAllocator);

    private static UnmanagedStructAllocator<T> CreateUnmanagedStructAllocator<T>(int entryCountPerPage) where T : unmanaged
        => new(entryCountPerPage, AllocatorTestServices.AllocationResource, AllocatorTestServices.MemoryAllocator);

    private static StructAllocator<T> CreateStructAllocator<T>(int entryCountPerPage) where T : struct, ICleanable
        => new(entryCountPerPage, AllocatorTestServices.AllocationResource, AllocatorTestServices.MemoryAllocator);

    public struct ThreadTestStruct
    {
        public long ThreadId;
        public long Iteration;
        public long Value;
    }

    public struct CleanableThreadTestStruct : ICleanable
    {
        public long ThreadId;
        public long Iteration;
        public long Value;

        public void Cleanup()
        {
            ThreadId = -1;
            Iteration = -1;
            Value = -1;
        }
    }

    [Test]
    public void UnmanagedStructAllocator_ConcurrentOperations_NoCorruption()
    {
        const int threadCount = 8;
        const int operationsPerThread = 200;

        using var allocator = CreateUnmanagedStructAllocator<ThreadTestStruct>(64);
        var activeBlocks = new ConcurrentDictionary<int, (long threadId, long iteration)>();
        var errors = new ConcurrentBag<string>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                var random = new Random(threadIndex * 1000);
                barrier.SignalAndWait();

                for (int i = 0; i < operationsPerThread; i++)
                {
                    if (random.Next(2) == 0 || activeBlocks.IsEmpty)
                    {
                        // Allocate
                        ref var item = ref allocator.Allocate(out var id);
                        item.ThreadId = threadIndex;
                        item.Iteration = i;
                        item.Value = threadIndex * 100000 + i;
                        activeBlocks[id] = (threadIndex, i);
                    }
                    else
                    {
                        // Free
                        foreach (var key in activeBlocks.Keys)
                        {
                            if (activeBlocks.TryRemove(key, out var expected))
                            {
                                ref var item = ref allocator.Get(key);
                                if (item.ThreadId != expected.threadId || item.Iteration != expected.iteration)
                                {
                                    errors.Add($"Block {key}: expected ({expected.threadId}, {expected.iteration}), got ({item.ThreadId}, {item.Iteration})");
                                }
                                allocator.Free(key);
                                break;
                            }
                        }
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.That(errors, Is.Empty,
            $"Corruption detected:\n{string.Join("\n", errors.Take(10))}");
        Assert.That(allocator.AllocatedCount, Is.EqualTo(activeBlocks.Count));
    }

    [Test]
    public void StructAllocator_ConcurrentOperations_CleanupCalled()
    {
        const int threadCount = 4;
        const int operationsPerThread = 100;

        using var allocator = CreateStructAllocator<CleanableThreadTestStruct>(64);
        var activeBlocks = new ConcurrentDictionary<int, (long threadId, long iteration)>();
        var barrier = new Barrier(threadCount);

        // Phase 1: Concurrent stress testing (allocate/free operations)
        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                var random = new Random(threadIndex * 1000);
                barrier.SignalAndWait();

                for (int i = 0; i < operationsPerThread; i++)
                {
                    if (random.Next(2) == 0 || activeBlocks.IsEmpty)
                    {
                        // Allocate
                        ref var item = ref allocator.Allocate(out var id);
                        item.ThreadId = threadIndex;
                        item.Iteration = i;
                        item.Value = threadIndex * 100000 + i;
                        activeBlocks[id] = (threadIndex, i);
                    }
                    else
                    {
                        // Free
                        foreach (var key in activeBlocks.Keys)
                        {
                            if (activeBlocks.TryRemove(key, out _))
                            {
                                allocator.Free(key);
                                break;
                            }
                        }
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Verify concurrent operations maintained correct state
        Assert.That(allocator.AllocatedCount, Is.EqualTo(activeBlocks.Count));

        // Phase 2: Verify cleanup is called when freeing remaining blocks
        // This phase has no race conditions because all threads are done
        var blockCount = 0;
        foreach (var kvp in activeBlocks)
        {
            var id = kvp.Key;

            // Verify data is still intact before freeing
            ref var item = ref allocator.Get(id);
            Assert.That(item.ThreadId, Is.EqualTo(kvp.Value.threadId),
                $"Block {id} data corrupted before free");
            Assert.That(item.Iteration, Is.EqualTo(kvp.Value.iteration));

            // Free the block
            allocator.Free(id);

            // Verify cleanup was called (fields should be -1)
            ref var cleaned = ref allocator.Get(id);
            Assert.That(cleaned.ThreadId, Is.EqualTo(-1),
                $"Block {id}: Cleanup() not called - ThreadId should be -1");
            Assert.That(cleaned.Iteration, Is.EqualTo(-1),
                $"Block {id}: Cleanup() not called - Iteration should be -1");
            Assert.That(cleaned.Value, Is.EqualTo(-1),
                $"Block {id}: Cleanup() not called - Value should be -1");

            blockCount++;
        }

        // Verify we actually tested some blocks
        Assert.That(blockCount, Is.GreaterThan(0),
            "Test should have some remaining blocks to verify cleanup");

        // All blocks should now be freed
        Assert.That(allocator.AllocatedCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(30000)]
    public void AllAllocators_ConcurrentMixedUse_NoDeadlock()
    {
        const int threadCount = 12;
        const int operationsPerThread = 300;

        using var blockAllocator = CreateBlockAllocator(sizeof(long), 32);
        using var unmanagedAllocator = CreateUnmanagedStructAllocator<ThreadTestStruct>(32);
        using var structAllocator = CreateStructAllocator<CleanableThreadTestStruct>(32);

        var completedOperations = 0;
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            var allocatorIndex = t % 3; // Distribute across allocators

            threads[t] = new Thread(() =>
            {
                var random = new Random(threadIndex * 1000);
                var localIds = new List<int>();
                barrier.SignalAndWait();

                for (int i = 0; i < operationsPerThread; i++)
                {
                    switch (allocatorIndex)
                    {
                        case 0:
                            if (random.Next(2) == 0 || localIds.Count == 0)
                            {
                                var span = blockAllocator.AllocateBlock(out var id);
                                var value = (long)(threadIndex * 100000 + i);
                                MemoryMarshal.Write(span, in value);
                                localIds.Add(id);
                            }
                            else
                            {
                                var idx = random.Next(localIds.Count);
                                blockAllocator.FreeBlock(localIds[idx]);
                                localIds.RemoveAt(idx);
                            }
                            break;

                        case 1:
                            if (random.Next(2) == 0 || localIds.Count == 0)
                            {
                                ref var item = ref unmanagedAllocator.Allocate(out var id);
                                item.ThreadId = threadIndex;
                                item.Iteration = i;
                                localIds.Add(id);
                            }
                            else
                            {
                                var idx = random.Next(localIds.Count);
                                unmanagedAllocator.Free(localIds[idx]);
                                localIds.RemoveAt(idx);
                            }
                            break;

                        case 2:
                            if (random.Next(2) == 0 || localIds.Count == 0)
                            {
                                ref var item = ref structAllocator.Allocate(out var id);
                                item.ThreadId = threadIndex;
                                item.Iteration = i;
                                localIds.Add(id);
                            }
                            else
                            {
                                var idx = random.Next(localIds.Count);
                                structAllocator.Free(localIds[idx]);
                                localIds.RemoveAt(idx);
                            }
                            break;
                    }

                    Interlocked.Increment(ref completedOperations);
                }

                // Cleanup remaining
                foreach (var id in localIds)
                {
                    switch (allocatorIndex)
                    {
                        case 0: blockAllocator.FreeBlock(id); break;
                        case 1: unmanagedAllocator.Free(id); break;
                        case 2: structAllocator.Free(id); break;
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.That(completedOperations, Is.EqualTo(threadCount * operationsPerThread));
    }
}