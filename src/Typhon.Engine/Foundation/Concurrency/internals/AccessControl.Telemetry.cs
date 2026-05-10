
#if TELEMETRY

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Typhon.Engine.Internals;

[InlineArray(Count)]
internal struct  AccessOperations
{
    internal const int Count = 6;
    private AccessOperation _element;
    
    // 108 bytes structure
}

internal static partial class AccessControlImpl
{
    internal static readonly ChainedBlockAllocator<AccessOperations> Allocator;

    static AccessControlImpl()
    {
        // 6 Operations are 108 bytes, +16 for the chain header = 124. Let's take 2 cache lines
        Allocator = new ChainedBlockAllocator<AccessOperations>(65536, 128-ChainedBlockAllocatorBase.BlockHeaderSize);
    }

    private static readonly ThreadLocal<StringBuilder> CachedToDebugStringBuilders = new(() => new StringBuilder(2048));
    private static readonly ThreadLocal<StringBuilder> CachedLogDataStringBuilders = new(() => new StringBuilder(512));
    
    private static string LogData(ulong data)
    {
        var sb = CachedLogDataStringBuilders.Value.Clear();

        sb.Append($"State: {GetAlignedStateName(data)}\t");
        if ((data & SharedState) != 0)
        {
            sb.Append($"Shared Counter: {data&SharedCounterMask}\t");
        }
        else
        {
            sb.Append($"Shared Waiters: {data & SharedCounterMask}\t");
        }
        sb.Append($"Exclusive Waiters: {(data & ExclusiveWaitersMask) >> ExclusiveWaitersShift}\t");
        sb.Append($"Promoter Waiters: {(data & PromoterWaitersMask) >> PromoterWaitersShift}\t");
        sb.Append($"BlockId: {(data & OperationsBlockIdMask) >> OperationsBlockIdShift}\t");
        if ((data & ExclusiveState) != 0)
        {
            sb.Append($" (Thread:{(data&ThreadIdMask) >> ThreadIdShift}) ");
        }

        return sb.ToString();
    }

    private static string ToAlignedOp(OperationType type)
    {
        switch (type)
        {
            case OperationType.EnterSharedAccess:       return "Shared    Start";
            case OperationType.ExitSharedAccess:        return "Shared    Exit ";
            case OperationType.EnterExclusiveAccess:    return "Exclusive Start";
            case OperationType.ExitExclusiveAccess:     return "Exclusive Exit ";
            case OperationType.SharedStartWait:         return "Wait (S)  Start";
            //case OperationType.SharedEndWait:           return "Wait (S)  End  ";
            case OperationType.ExclusiveStartWait:      return "Wait (E)  Start";
            //case OperationType.ExclusiveEndWait:        return "Wait (E)  End  ";
            case OperationType.TimedOutOrCanceled:      return "Timeout/Cancel ";
            default: return "?";
        }
    }
    
    private static void LogOp(ref AccessOperation op, StringBuilder sb)
    {
        var dt = new DateTime(op.Tick);
        var data = LogData(op.LockData);
        sb.AppendLine($"[{dt:O}]\t| Thread: {op.ThreadId}\t| Op: {ToAlignedOp(op.Type)}\t| Data: [{data}]");
    }

    private static string ToDebugString(int blockId, ref AccessOperation lastOp)
    {
        var sb = CachedToDebugStringBuilders.Value.Clear();
        sb.AppendLine($"Lock #{blockId}:");

        var stop = false;
        foreach (var curBlockId in Allocator.EnumerateChainedBlock(blockId))
        {
            ref var ops = ref Allocator.Get(curBlockId);
            for (int i = 0; i < AccessOperations.Count; i++)
            {
                ref var op = ref ops[i];
                if (op.IsEmpty)
                {
                    stop = true;
                    break;
                }
                LogOp(ref op, sb);
            }
            if (stop)
            {
                break;
            }
        }
        
        if (!Unsafe.IsNullRef(ref lastOp))
        {
            LogOp(ref lastOp, sb);
        }
        
        return sb.ToString();
    }
    
    private static string GetAlignedStateName(ulong state)
    {
        switch (state&StateMask)
        {
            case IdleState:
                return "Idle  ";
            case SharedState:
                return "Shared";
            case ExclusiveState:
                return "Exclus";
        }

        return "Unknown";
    }
}
#endif