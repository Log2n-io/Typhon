using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

unsafe class EntityRecordTests
{
    [Test]
    public void EntityRecordHeader_SizeOf_14Bytes()
    {
        Assert.That(sizeof(EntityRecordHeader), Is.EqualTo(14));
    }

    [Test]
    public void BornTSN_RoundTrip_48Bit()
    {
        var header = new EntityRecordHeader();
        long tsn = (1L << 47) - 1; // max 48-bit value
        header.BornTSN = tsn;
        Assert.That(header.BornTSN, Is.EqualTo(tsn));
    }

    [Test]
    public void DiedTSN_RoundTrip_48Bit()
    {
        var header = new EntityRecordHeader();
        long tsn = 0x0000_ABCD_1234_5678L & 0x0000_FFFF_FFFF_FFFFL; // 48-bit
        header.DiedTSN = tsn;
        Assert.That(header.DiedTSN, Is.EqualTo(tsn));
    }

    [Test]
    public void BornTSN_SmallValue()
    {
        var header = new EntityRecordHeader();
        header.BornTSN = 42;
        Assert.That(header.BornTSN, Is.EqualTo(42));
    }

    [Test]
    public void DiedTSN_Zero_IsAlive()
    {
        var header = new EntityRecordHeader();
        Assert.That(header.IsAlive, Is.True);
    }

    [Test]
    public void DiedTSN_NonZero_NotAlive()
    {
        var header = new EntityRecordHeader();
        header.DiedTSN = 100;
        Assert.That(header.IsAlive, Is.False);
    }

    [Test]
    public void IsVisibleAt_GenesisEntity_AlwaysVisible()
    {
        var header = new EntityRecordHeader(); // BornTSN=0, DiedTSN=0
        Assert.That(header.IsVisibleAt(0), Is.True);
        Assert.That(header.IsVisibleAt(100), Is.True);
    }

    [Test]
    public void IsVisibleAt_BornAfterTx_Invisible()
    {
        var header = new EntityRecordHeader { BornTSN = 10 };
        Assert.That(header.IsVisibleAt(9), Is.False);  // born after tx
        Assert.That(header.IsVisibleAt(10), Is.True);   // born at same TSN = visible
        Assert.That(header.IsVisibleAt(11), Is.True);   // born before tx
    }

    [Test]
    public void IsVisibleAt_DiedBeforeOrAtTx_Invisible()
    {
        var header = new EntityRecordHeader { BornTSN = 5, DiedTSN = 10 };
        Assert.That(header.IsVisibleAt(9), Is.True);     // alive at TSN 9
        Assert.That(header.IsVisibleAt(10), Is.False);   // died at TSN 10 → invisible to TSN 10+
        Assert.That(header.IsVisibleAt(11), Is.False);   // dead
    }

    [Test]
    public void EnabledBits_SetAndCheck()
    {
        var header = new EntityRecordHeader { EnabledBits = 0b1010 };
        Assert.That(header.EnabledBits & (1 << 0), Is.EqualTo(0)); // slot 0 disabled
        Assert.That(header.EnabledBits & (1 << 1), Is.Not.EqualTo(0)); // slot 1 enabled
        Assert.That(header.EnabledBits & (1 << 3), Is.Not.EqualTo(0)); // slot 3 enabled
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EntityRecordAccessor
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RecordSize_VariousComponentCounts()
    {
        Assert.That(EntityRecordAccessor.RecordSize(1), Is.EqualTo(18));
        Assert.That(EntityRecordAccessor.RecordSize(2), Is.EqualTo(22));
        Assert.That(EntityRecordAccessor.RecordSize(4), Is.EqualTo(30));
        Assert.That(EntityRecordAccessor.RecordSize(8), Is.EqualTo(46));
        Assert.That(EntityRecordAccessor.RecordSize(16), Is.EqualTo(78));
    }

    [Test]
    public void Accessor_GetSetLocation_RoundTrip()
    {
        int componentCount = 4;
        byte* record = stackalloc byte[EntityRecordAccessor.RecordSize(componentCount)];
        EntityRecordAccessor.InitializeRecord(ref Unsafe.AsRef<byte>(record), componentCount);

        EntityRecordAccessor.SetLocation(ref Unsafe.AsRef<byte>(record), 0, 100);
        EntityRecordAccessor.SetLocation(ref Unsafe.AsRef<byte>(record), 1, 200);
        EntityRecordAccessor.SetLocation(ref Unsafe.AsRef<byte>(record), 2, 300);
        EntityRecordAccessor.SetLocation(ref Unsafe.AsRef<byte>(record), 3, 400);

        Assert.That(EntityRecordAccessor.GetLocation(ref Unsafe.AsRef<byte>(record), 0), Is.EqualTo(100));
        Assert.That(EntityRecordAccessor.GetLocation(ref Unsafe.AsRef<byte>(record), 1), Is.EqualTo(200));
        Assert.That(EntityRecordAccessor.GetLocation(ref Unsafe.AsRef<byte>(record), 2), Is.EqualTo(300));
        Assert.That(EntityRecordAccessor.GetLocation(ref Unsafe.AsRef<byte>(record), 3), Is.EqualTo(400));
    }

    [Test]
    public void Accessor_GetHeader_ModifiesTSN()
    {
        int componentCount = 2;
        byte* record = stackalloc byte[EntityRecordAccessor.RecordSize(componentCount)];
        EntityRecordAccessor.InitializeRecord(ref Unsafe.AsRef<byte>(record), componentCount);

        ref var header = ref EntityRecordAccessor.GetHeader(ref Unsafe.AsRef<byte>(record));
        header.BornTSN = 42;
        header.EnabledBits = 0b11;

        Assert.That(EntityRecordAccessor.GetHeader(ref Unsafe.AsRef<byte>(record)).BornTSN, Is.EqualTo(42));
        Assert.That(EntityRecordAccessor.GetHeader(ref Unsafe.AsRef<byte>(record)).EnabledBits, Is.EqualTo(0b11));
    }

    [Test]
    public void Accessor_CopyLocations()
    {
        int componentCount = 3;
        int size = EntityRecordAccessor.RecordSize(componentCount);
        byte* src = stackalloc byte[size];
        byte* dst = stackalloc byte[size];
        EntityRecordAccessor.InitializeRecord(ref Unsafe.AsRef<byte>(src), componentCount);
        EntityRecordAccessor.InitializeRecord(ref Unsafe.AsRef<byte>(dst), componentCount);

        EntityRecordAccessor.SetLocation(ref Unsafe.AsRef<byte>(src), 0, 10);
        EntityRecordAccessor.SetLocation(ref Unsafe.AsRef<byte>(src), 1, 20);
        EntityRecordAccessor.SetLocation(ref Unsafe.AsRef<byte>(src), 2, 30);

        EntityRecordAccessor.CopyLocations(ref Unsafe.AsRef<byte>(src), ref Unsafe.AsRef<byte>(dst), componentCount);

        Assert.That(EntityRecordAccessor.GetLocation(ref Unsafe.AsRef<byte>(dst), 0), Is.EqualTo(10));
        Assert.That(EntityRecordAccessor.GetLocation(ref Unsafe.AsRef<byte>(dst), 1), Is.EqualTo(20));
        Assert.That(EntityRecordAccessor.GetLocation(ref Unsafe.AsRef<byte>(dst), 2), Is.EqualTo(30));
    }

    [Test]
    public void Accessor_InitializeRecord_AllZeros()
    {
        int componentCount = 4;
        int size = EntityRecordAccessor.RecordSize(componentCount);
        byte* record = stackalloc byte[size];

        // Dirty the memory first
        Unsafe.InitBlock(record, 0xFF, (uint)size);

        EntityRecordAccessor.InitializeRecord(ref Unsafe.AsRef<byte>(record), componentCount);

        ref var header = ref EntityRecordAccessor.GetHeader(ref Unsafe.AsRef<byte>(record));
        Assert.That(header.BornTSN, Is.EqualTo(0));
        Assert.That(header.DiedTSN, Is.EqualTo(0));
        Assert.That(header.EnabledBits, Is.EqualTo(0));

        for (int i = 0; i < componentCount; i++)
        {
            Assert.That(EntityRecordAccessor.GetLocation(ref Unsafe.AsRef<byte>(record), i), Is.EqualTo(0));
        }
    }
}
