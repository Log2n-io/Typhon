// LEGACY — will be removed after #168. Kept as reference for WAL record format.
// ECS WAL will use tick fence design (07-durability.md), not per-transaction serialization.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

internal static class WalSerializer
{
    /// <summary>Action struct for WAL size calculation pass.</summary>
    private struct WalSizeAction : IEntryAction
    {
        public int RecordCount;
        public int TotalPayload;
        public int StorageSize;

        public void Process(ref CommitContext ctx)
        {
            RecordCount++;
            var isDelete = (ctx.CompRevInfo.Operations & ComponentInfo.OperationType.Deleted) != 0;
            var bodyLen = WalRecordHeader.SizeInBytes + (isDelete ? 0 : StorageSize);
            TotalPayload += WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;
        }
    }

    /// <summary>
    /// Serializes all committed component changes into the WAL commit buffer. Called after CommitComponentCore loop completes (all conflicts resolved,
    /// revisions visible) but before State = Committed.
    /// </summary>
    /// <returns>The highest LSN assigned to the serialized records, or 0 if nothing was serialized.</returns>
    internal static long SerializeToWal(Dictionary<Type, ComponentInfo> componentInfos, WalManager walManager, long tsn, ushort uowId, ref UnitOfWorkContext ctx)
    {
        // Pass 1: Count non-Read operations and total payload size via ForEachMutableEntry
        var context = new CommitContext();
        int recordCount = 0;
        int totalPayload = 0;

        foreach (var kvp in componentInfos)
        {
            var info = kvp.Value;
            context.Info = info;
            var sizeAction = new WalSizeAction { StorageSize = info.ComponentTable.ComponentStorageSize };
            info.ForEachMutableEntry(ref context, ref sizeAction);
            recordCount += sizeAction.RecordCount;
            totalPayload += sizeAction.TotalPayload;
        }

        if (recordCount == 0)
        {
            return 0;
        }

        // Claim space in the commit buffer
        var wc = WaitContext.FromDeadline(Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout)));
        var claim = walManager.CommitBuffer.TryClaim(totalPayload, recordCount, ref wc);

        if (!claim.IsValid)
        {
            return 0;
        }

        try
        {
            // Pass 2: Write WAL records into the claimed region
            // WalRecordWriter is a ref struct (contains Span<byte>) and cannot be captured in a struct implementing IEntryAction,
            // so this pass uses inline iteration via ForEachMutableEntry with a local lambda-like pattern.
            var writer = new WalRecordWriter
            {
                DataSpan = claim.DataSpan,
                WriteOffset = 0,
                RecordIndex = 0,
                CurrentLsn = claim.FirstLSN,
                TotalRecordCount = recordCount
            };

            foreach (var kvp in componentInfos)
            {
                var info = kvp.Value;
                var componentTypeId = info.ComponentTable.WalTypeId;
                var storageSize = info.ComponentTable.ComponentStorageSize;

                foreach (var pk in info.SingleCache.Keys)
                {
                    ref var cri = ref CollectionsMarshal.GetValueRefOrNullRef(info.SingleCache, pk);
                    if (cri.Operations == ComponentInfo.OperationType.Read)
                    {
                        continue;
                    }

                    WriteWalRecord(ref writer, pk, componentTypeId, storageSize, ref cri, info, tsn, uowId);
                }
            }

            walManager.CommitBuffer.Publish(ref claim);
            return writer.HighestLsn;
        }
        catch
        {
            walManager.CommitBuffer.AbandonClaim(ref claim);
            throw;
        }
    }

    /// <summary>
    /// Writes a single WAL chunk (chunk header + record header + payload + chunk footer) into the claim data span.
    /// CRC and PrevCRC are left as 0 — the WAL writer thread patches them before disk write.
    /// </summary>
    internal static void WriteWalRecord(ref WalRecordWriter writer, long entityId, ushort componentTypeId, int storageSize,
        ref ComponentInfo.CompRevInfo cri, ComponentInfo info, long tsn, ushort uowId)
    {
        var isDelete = (cri.Operations & ComponentInfo.OperationType.Deleted) != 0;
        var isCreate = (cri.Operations & ComponentInfo.OperationType.Created) != 0;
        var payloadLength = isDelete ? 0 : storageSize;

        // Determine WAL operation type
        WalOperationType opType;
        if (isDelete)
        {
            opType = WalOperationType.Delete;
        }
        else if (isCreate)
        {
            opType = WalOperationType.Create;
        }
        else
        {
            opType = WalOperationType.Update;
        }

        // Build flags
        var flags = WalRecordFlags.None;
        if (writer.RecordIndex == 0)
        {
            flags |= WalRecordFlags.UowBegin;
        }

        if (writer.RecordIndex == writer.TotalRecordCount - 1)
        {
            flags |= WalRecordFlags.UowCommit;
        }

        var bodyLen = WalRecordHeader.SizeInBytes + payloadLength;
        var chunkSize = (ushort)(WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes);

        // Write chunk header (PrevCRC=0 — patched by WAL writer)
        var chunkHeader = new WalChunkHeader
        {
            ChunkType = (ushort)WalChunkType.Transaction,
            ChunkSize = chunkSize,
            PrevCRC = 0,
        };
        MemoryMarshal.Write(writer.DataSpan[writer.WriteOffset..], in chunkHeader);

        // Write record header
        var recordOffset = writer.WriteOffset + WalChunkHeader.SizeInBytes;
        var header = new WalRecordHeader
        {
            LSN = writer.CurrentLsn,
            TransactionTSN = tsn,
            UowEpoch = uowId,
            ComponentTypeId = componentTypeId,
            EntityId = entityId,
            PayloadLength = (ushort)payloadLength,
            OperationType = (byte)opType,
            Flags = (byte)flags,
        };
        MemoryMarshal.Write(writer.DataSpan[recordOffset..], in header);

        // Write payload (component data) for non-delete operations
        if (payloadLength > 0 && cri.CurCompContentChunkId > 0)
        {
            var srcSpan = info.CompContentAccessor.GetChunkAsReadOnlySpan(cri.CurCompContentChunkId);
            var payloadDst = writer.DataSpan.Slice(recordOffset + WalRecordHeader.SizeInBytes, payloadLength);
            srcSpan[..payloadLength].CopyTo(payloadDst);
        }

        // Write chunk footer (CRC=0 — patched by WAL writer)
        var footerOffset = writer.WriteOffset + chunkSize - WalChunkFooter.SizeInBytes;
        var footer = new WalChunkFooter { CRC = 0 };
        MemoryMarshal.Write(writer.DataSpan[footerOffset..], in footer);

        writer.WriteOffset += chunkSize;
        writer.CurrentLsn++;
        writer.RecordIndex++;
    }
}
