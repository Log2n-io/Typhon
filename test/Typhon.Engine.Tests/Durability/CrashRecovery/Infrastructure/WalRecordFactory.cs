using NUnit.Framework;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Builds minimal but reader-valid WAL records directly into the commit buffer for crash-simulation and watermark tests. The chunk CRCs are left as placeholders —
/// the real <see cref="WalWriter"/> patches the PrevCRC chain and footer CRC during drain, so records produced here exercise the genuine write path.
/// </summary>
internal static class WalRecordFactory
{
    /// <summary>
    /// Claims, fills, and publishes one single-record Transaction frame for <paramref name="entityId"/>. Returns the LSN assigned to the record.
    /// </summary>
    public static long PublishTransactionRecord(WalCommitBuffer buffer, long entityId, int payloadLen = 4)
    {
        var chunkSize = WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payloadLen + WalChunkFooter.SizeInBytes;
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
        var claim = buffer.TryClaim(chunkSize, 1, ref ctx);
        Assert.That(claim.IsValid, Is.True);

        var span = claim.DataSpan;

        ref var ch = ref MemoryMarshal.AsRef<WalChunkHeader>(span);
        ch.ChunkType = (ushort)WalChunkType.Transaction;
        ch.ChunkSize = (ushort)chunkSize;
        ch.PrevCRC = 0;

        ref var rh = ref MemoryMarshal.AsRef<WalRecordHeader>(span[WalChunkHeader.SizeInBytes..]);
        rh = default;
        rh.LSN = claim.FirstLSN;
        rh.TransactionTSN = entityId;
        rh.UowEpoch = 1;
        rh.ComponentTypeId = 1;
        rh.EntityId = entityId;
        rh.PayloadLength = (ushort)payloadLen;
        rh.OperationType = (byte)WalOperationType.Create;
        rh.Flags = (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit);

        span.Slice(WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes, payloadLen).Fill(0xCD);
        MemoryMarshal.Write(span.Slice(chunkSize - WalChunkFooter.SizeInBytes, WalChunkFooter.SizeInBytes), (uint)0);

        var lsn = claim.FirstLSN;
        buffer.Publish(ref claim);
        return lsn;
    }
}
