#if TELEMETRY
using System;
using System.Runtime.InteropServices;
#endif

namespace Typhon.Engine.Internals;

internal enum OperationType : byte
{
    None = 0,
    EnterSharedAccess,
    ExitSharedAccess,
    EnterExclusiveAccess,
    ExitExclusiveAccess,
    SharedStartWait,
    ExclusiveStartWait,
    PromoteToExclusive,
    PromoteStartWait,
    DemoteFromExclusive,
    TimedOutOrCanceled
}

#if TELEMETRY

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AccessOperation
{
    internal ulong LockData;            //  0 + 8
    internal long Tick;                 //  8 + 8
    internal OperationType Type;        // 16 + 1
    internal byte ThreadId;             // 17 + 1 = 18

    public bool IsEmpty => Type == OperationType.None;

    public static AccessOperation Wait(OperationType type)
    {
        var res = new AccessOperation(type);
        res.Now();
        return res;
    }

    public static AccessOperation TimedOutOrCanceled()
    {
        var res = new AccessOperation(OperationType.TimedOutOrCanceled);
        res.Now();
        return res;
    }

    public AccessOperation(OperationType type)
    {
        Type = type;
        ThreadId = (byte)Environment.CurrentManagedThreadId;
    }

    public void Now() => Tick = DateTime.UtcNow.Ticks;
}

#endif