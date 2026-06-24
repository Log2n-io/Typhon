using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Verifies binary layout, size, and alignment of <see cref="WalRecordHeader"/>,
/// <see cref="WalFrameHeader"/>, <see cref="WalChunkHeader"/>, and <see cref="WalChunkFooter"/>
/// to catch any accidental struct changes that would break the on-disk WAL format.
/// </summary>
[TestFixture]
public class WalRecordHeaderTests
{
    #region WalRecordHeader — Size

    [Test]
    public void SizeOf_Is32Bytes()
    {
        Assert.That(Unsafe.SizeOf<WalRecordHeader>(), Is.EqualTo(32));
        Assert.That(Unsafe.SizeOf<WalRecordHeader>(), Is.EqualTo(WalRecordHeader.SizeInBytes));
    }

    #endregion

    #region WalRecordHeader — Field Offsets

    [Test]
    public void FieldOffset_LSN_Is0() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.LSN)).ToInt32(), Is.EqualTo(0));

    [Test]
    public void FieldOffset_TransactionTSN_Is8() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.TransactionTSN)).ToInt32(), Is.EqualTo(8));

    [Test]
    public void FieldOffset_UowEpoch_Is16() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.UowEpoch)).ToInt32(), Is.EqualTo(16));

    [Test]
    public void FieldOffset_ComponentTypeId_Is18() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.ComponentTypeId)).ToInt32(), Is.EqualTo(18));

    [Test]
    public void FieldOffset_EntityId_Is20() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.EntityId)).ToInt32(), Is.EqualTo(20));

    [Test]
    public void FieldOffset_PayloadLength_Is28() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.PayloadLength)).ToInt32(), Is.EqualTo(28));

    [Test]
    public void FieldOffset_OperationType_Is30() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.OperationType)).ToInt32(), Is.EqualTo(30));

    [Test]
    public void FieldOffset_Flags_Is31() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.Flags)).ToInt32(), Is.EqualTo(31));

    #endregion

    #region WalFrameHeader — Size & Layout

    [Test]
    public void FrameHeader_SizeOf_Is16Bytes()
    {
        // 16 bytes since P0.2 (LOG-05): FrameLength(4) + RecordCount(4) + LastLsn(8). LastLsn carries the frame's highest LSN so the consumer can compute an honest
        // durable watermark without peeking NextLsn. Growing the in-band frame header is an on-disk WAL format change — paired with the P1 WAL v2 rewrite (same PR).
        Assert.That(Unsafe.SizeOf<WalFrameHeader>(), Is.EqualTo(16));
        Assert.That(Unsafe.SizeOf<WalFrameHeader>(), Is.EqualTo(WalFrameHeader.SizeInBytes));
    }

    [Test]
    public void FrameHeader_FieldOffset_FrameLength_Is0() =>
        Assert.That(Marshal.OffsetOf<WalFrameHeader>(nameof(WalFrameHeader.FrameLength)).ToInt32(), Is.EqualTo(0));

    [Test]
    public void FrameHeader_FieldOffset_RecordCount_Is4() =>
        Assert.That(Marshal.OffsetOf<WalFrameHeader>(nameof(WalFrameHeader.RecordCount)).ToInt32(), Is.EqualTo(4));

    [Test]
    public void FrameHeader_FieldOffset_LastLsn_Is8() =>
        Assert.That(Marshal.OffsetOf<WalFrameHeader>(nameof(WalFrameHeader.LastLsn)).ToInt32(), Is.EqualTo(8));

    #endregion

    #region WalRecordFlags

    [Test]
    public void Flags_CombineCorrectly()
    {
        var flags = WalRecordFlags.UowBegin | WalRecordFlags.UowCommit;

        Assert.That(flags.HasFlag(WalRecordFlags.UowBegin), Is.True);
        Assert.That(flags.HasFlag(WalRecordFlags.UowCommit), Is.True);
        Assert.That(flags.HasFlag(WalRecordFlags.None), Is.True);
    }

    [Test]
    public void Flags_None_IsZero() =>
        Assert.That((byte)WalRecordFlags.None, Is.EqualTo(0));

    #endregion

    #region WalOperationType

    [Test]
    public void OperationType_Values_MatchSpec()
    {
        Assert.That((byte)WalOperationType.Create, Is.EqualTo(1));
        Assert.That((byte)WalOperationType.Update, Is.EqualTo(2));
        Assert.That((byte)WalOperationType.Delete, Is.EqualTo(3));
    }

    #endregion

    #region WalChunkHeader — Size & Layout

    [Test]
    public void ChunkHeader_SizeOf_Is8Bytes()
    {
        Assert.That(Unsafe.SizeOf<WalChunkHeader>(), Is.EqualTo(8));
        Assert.That(Unsafe.SizeOf<WalChunkHeader>(), Is.EqualTo(WalChunkHeader.SizeInBytes));
    }

    [Test]
    public void ChunkHeader_FieldOffset_ChunkType_Is0() =>
        Assert.That(Marshal.OffsetOf<WalChunkHeader>(nameof(WalChunkHeader.ChunkType)).ToInt32(), Is.EqualTo(0));

    [Test]
    public void ChunkHeader_FieldOffset_ChunkSize_Is2() =>
        Assert.That(Marshal.OffsetOf<WalChunkHeader>(nameof(WalChunkHeader.ChunkSize)).ToInt32(), Is.EqualTo(2));

    [Test]
    public void ChunkHeader_FieldOffset_PrevCRC_Is4() =>
        Assert.That(Marshal.OffsetOf<WalChunkHeader>(nameof(WalChunkHeader.PrevCRC)).ToInt32(), Is.EqualTo(4));

    #endregion

    #region WalChunkFooter — Size & Layout

    [Test]
    public void ChunkFooter_SizeOf_Is4Bytes()
    {
        Assert.That(Unsafe.SizeOf<WalChunkFooter>(), Is.EqualTo(4));
        Assert.That(Unsafe.SizeOf<WalChunkFooter>(), Is.EqualTo(WalChunkFooter.SizeInBytes));
    }

    [Test]
    public void ChunkFooter_FieldOffset_CRC_Is0() =>
        Assert.That(Marshal.OffsetOf<WalChunkFooter>(nameof(WalChunkFooter.CRC)).ToInt32(), Is.EqualTo(0));

    #endregion

    #region WalChunkType

    [Test]
    public void ChunkType_Values_MatchSpec()
    {
        Assert.That((ushort)WalChunkType.Transaction, Is.EqualTo(1));
    }

    #endregion
}