namespace Typhon.Profiler;

/// <summary>
/// Reason a thread left the CPU at a context-switch point. Hand-rolled mirror of Windows' <c>KWAIT_REASON</c> kernel enum (a.k.a. <c>KTHREAD::WaitReason</c>),
/// captured by the ETW scheduling pump from <c>CSwitchTraceData.OldThreadWaitReason</c> and carried on <see cref="TraceEventKind.ThreadContextSwitch"/> records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire stability:</b> numeric values are part of the <c>.typhon-trace</c> file format — never renumber. Mirror of the kernel enum as of Windows 11 24H2;
/// newer Windows builds may append entries (e.g. <c>WrPhysicalFault</c>) that we do not currently mirror. Out-of-range values land on
/// <see cref="MaximumWaitReason"/> at decode time so the viewer can still display the slice with an "unknown reason" label.
/// </para>
/// <para>
/// <b>Naming:</b> Microsoft uses two conventions in the same enum. Unprefixed names (e.g. <c>Executive</c>, <c>UserRequest</c>) describe the kernel object
/// family the thread is transitioning into a wait on. <c>Wr</c>-prefixed variants (e.g. <c>WrExecutive</c>, <c>WrUserRequest</c>) describe the kernel object
/// family the thread is currently blocked on while in the <c>Wait</c> state. In practice the pairs are observed together as <c>(threadState, waitReason)</c>
/// tuples; the viewer collapses them into a single label.
/// </para>
/// <para>
/// <b>Most informative values for the viewer:</b>
/// <list type="bullet">
///   <item><see cref="WrQuantumEnd"/> — thread used its quantum; pure CPU pressure with peers competing.</item>
///   <item><see cref="WrPreempted"/> — higher-priority thread took the core.</item>
///   <item><see cref="Executive"/> / <see cref="WrExecutive"/> — blocked on a kernel object (event, mutex, sem, I/O).</item>
///   <item><see cref="UserRequest"/> / <see cref="WrUserRequest"/> — explicit user-mode wait (Sleep, WaitForSingleObject, etc.).</item>
///   <item><see cref="WrPushLock"/>, <see cref="WrMutex"/>, <see cref="WrFastMutex"/>, <see cref="WrGuardedMutex"/>,
///         <see cref="WrKeyedEvent"/> — sync-primitive blocking.</item>
///   <item><see cref="WrQueue"/> — threadpool / IO-completion queue wait.</item>
///   <item><see cref="WrVirtualMemory"/> / <see cref="PageIn"/> / <see cref="WrPageIn"/> — paging stall.</item>
///   <item><see cref="WrYieldExecution"/> — voluntary <c>SwitchToThread</c> / <c>Yield</c>.</item>
/// </list>
/// </para>
/// </remarks>
public enum ThreadWaitReason : byte
{
    /// <summary>Waiting on a generic kernel object (event, mutex, semaphore, I/O completion). Most common "I'm blocked" reason.</summary>
    Executive = 0,
    /// <summary>Backing pages need to be brought in before the thread can run.</summary>
    FreePage = 1,
    /// <summary>Page-in for code/data is in flight.</summary>
    PageIn = 2,
    /// <summary>System allocation / mapping operation in progress.</summary>
    PoolAllocation = 3,
    /// <summary>Delay-execution wait (e.g. <c>KeDelayExecutionThread</c>).</summary>
    DelayExecution = 4,
    /// <summary>Suspended via <c>SuspendThread</c> / debugger break-in.</summary>
    Suspended = 5,
    /// <summary>Explicit user-mode wait (<c>Sleep</c>, <c>WaitForSingleObject</c>, etc.).</summary>
    UserRequest = 6,
    /// <summary>Wr-prefixed mirror of <see cref="Executive"/> — currently blocked on a kernel object.</summary>
    WrExecutive = 7,
    /// <summary>Wr-prefixed mirror of <see cref="FreePage"/>.</summary>
    WrFreePage = 8,
    /// <summary>Wr-prefixed mirror of <see cref="PageIn"/> — paging stall.</summary>
    WrPageIn = 9,
    /// <summary>Wr-prefixed mirror of <see cref="PoolAllocation"/>.</summary>
    WrPoolAllocation = 10,
    /// <summary>Wr-prefixed mirror of <see cref="DelayExecution"/>.</summary>
    WrDelayExecution = 11,
    /// <summary>Wr-prefixed mirror of <see cref="Suspended"/>.</summary>
    WrSuspended = 12,
    /// <summary>Wr-prefixed mirror of <see cref="UserRequest"/> — explicit user-mode wait.</summary>
    WrUserRequest = 13,
    /// <summary>Spare slot from the kernel definition; retained for wire stability.</summary>
    WrSpare0 = 14,
    /// <summary>Queue wait — typically a threadpool worker waiting for work, or an IO completion port.</summary>
    WrQueue = 15,
    /// <summary>LPC (Local Procedure Call) receive — waiting for a request.</summary>
    WrLpcReceive = 16,
    /// <summary>LPC reply wait — waiting for the responder.</summary>
    WrLpcReply = 17,
    /// <summary>Waiting for virtual-memory commit / fault resolution.</summary>
    WrVirtualMemory = 18,
    /// <summary>Page-write completion.</summary>
    WrPageOut = 19,
    /// <summary>Lazy-mapping operation in progress.</summary>
    WrRendezvous = 20,
    /// <summary>Push-lock acquire (lightweight reader/writer lock).</summary>
    WrKeyedEvent = 21,
    /// <summary>Terminated — thread is exiting.</summary>
    WrTerminated = 22,
    /// <summary>Processor-affinity / hot-removal pause.</summary>
    WrProcessInSwap = 23,
    /// <summary>CPU rate-control throttle.</summary>
    WrCpuRateControl = 24,
    /// <summary>Calling out — heap manager / debugger callout in progress.</summary>
    WrCalloutStack = 25,
    /// <summary>Kernel mutex blocking.</summary>
    WrKernel = 26,
    /// <summary>Resource manager I/O wait.</summary>
    WrResource = 27,
    /// <summary>Push-lock (ERESOURCE-style fast lock) blocking.</summary>
    WrPushLock = 28,
    /// <summary>Kernel mutex blocking (KMUTEX).</summary>
    WrMutex = 29,
    /// <summary>Quantum end — the thread used up its scheduling quantum. Pure CPU pressure indicator.</summary>
    WrQuantumEnd = 30,
    /// <summary>Dispatch interrupt — switched via DPC/dispatch.</summary>
    WrDispatchInt = 31,
    /// <summary>Preempted by a higher-priority thread.</summary>
    WrPreempted = 32,
    /// <summary>Voluntary <c>SwitchToThread</c> / <c>Yield</c>.</summary>
    WrYieldExecution = 33,
    /// <summary>Fast-mutex (FMUTEX) blocking.</summary>
    WrFastMutex = 34,
    /// <summary>Guarded mutex blocking — guarded regions disable APC delivery while held.</summary>
    WrGuardedMutex = 35,
    /// <summary>Object-rundown protection wait.</summary>
    WrRundown = 36,
    /// <summary>Sentinel for values outside the kernel range observed at runtime — surfaces "unknown reason" in the viewer.</summary>
    MaximumWaitReason = 37,
}
