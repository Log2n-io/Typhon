// TraceEvent 3.x marks TimeStampQPC as "discouraged" but still ships it. We use raw QPC ticks so values cross-walk with Stopwatch.GetTimestamp()
// (= QueryPerformanceCounter on Windows). TimeStampRelativeMSec would force a unit conversion at every callback and lose that direct equality.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Windows-only ETW pump that observes the kernel scheduler and emits one <see cref="TraceEventKind.ThreadContextSwitch"/> record per ON-CPU slice closed for a
/// Typhon-registered OS thread. Output flows through the standard producer pipeline: the pump claims its own <see cref="ThreadSlot"/>, each emit goes into that
/// slot's SPSC ring buffer, and the workbench re-attributes the record to the affected thread via the <c>TargetSlotIdx</c> field on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mechanism</b> (per Avocat / "Measuring processor utilization" / Vance Morrison): opens the singleton NT Kernel Logger ETW session with the
/// <c>ContextSwitch</c> + <c>Dispatcher</c> kernel keywords, then runs <c>TraceEventSession.Source.Process()</c> on a dedicated thread. The kernel delivers a
/// <see cref="CSwitchTraceData"/> for every CPU/thread swap and a <see cref="DispatcherReadyThreadTraceData"/> every time a thread becomes runnable. The pump
/// keeps a small per-OS-TID state machine that records when each registered thread got the CPU (<c>StartTick</c>) and last became ready (<c>ReadyTick</c>); on
/// each <c>CSwitchOldThread</c> it emits a <see cref="ThreadContextSwitchEvent"/> carrying the closing slice's start tick, duration, CPU number, wait reason,
/// post-switch thread state, and ready-queue latency.
/// </para>
/// <para>
/// <b>Scope:</b> per design decision (Loïc), only threads with a live <see cref="ThreadSlot"/> in <see cref="ThreadSlotRegistry"/> are tracked. Pool /
/// finalizer / GC server threads are filtered out at the callback via <see cref="ThreadSlotRegistry.TryGetSlotByOsThreadId"/>. The pump's own slot is also
/// excluded so it doesn't emit cswitch records about itself.
/// </para>
/// <para>
/// <b>Privileges:</b> requires Administrator or membership in <c>Performance Log Users</c>. <see cref="Start"/> catches
/// <see cref="UnauthorizedAccessException"/> and writes one diagnostic line to <see cref="Console.Error"/> — the engine continues without scheduling data.
/// <b>Singleton:</b> the NT Kernel Logger is a per-machine singleton; PerfView / WPR / xperf will collide. The pump catches that exception the same way.
/// </para>
/// <para>
/// <b>Clock:</b> ETW's <c>TimeStampQPC</c> is the raw <c>QueryPerformanceCounter</c> reading, identical to what <see cref="System.Diagnostics.Stopwatch"/>
/// returns on Windows via <c>GetTimestamp()</c>. Slice-start QPC values cross-walk directly into the trace's time space — no scaling, no offset.
/// </para>
/// </remarks>
internal sealed class EtwSchedulingPump : IDisposable
{
    /// <summary>Per-OS-thread slice tracking state held by the pump callbacks. Mutated only from the single Process() pump thread, so no sync needed.</summary>
    private struct ThreadSliceState
    {
        /// <summary>QPC tick when this thread last got the CPU. <c>-1</c> = no <c>CSwitchNewThread</c> observed yet (skip slice close).</summary>
        public long StartTick;
        /// <summary>QPC tick when this thread last became ready (from <c>DispatcherReadyThread</c>). <c>0</c> = unknown (no Ready before this slice).</summary>
        public long ReadyTick;
        /// <summary>Logical CPU id where the in-progress slice runs.</summary>
        public ushort ProcessorNumber;
    }

    private readonly int _profiledProcessId;

    /// <summary>
    /// Per-OS-TID slice state. Mutated only from the pump thread (Process() runs all callbacks serially on that thread), so no locking needed. Keyed by raw OS
    /// TID (<c>uint</c> from ETW data). Entries are kept across slice transitions to avoid dict churn — a closed slice leaves <c>StartTick = -1</c> sentinel.
    /// </summary>
    private readonly Dictionary<uint, ThreadSliceState> _threadStates = new();

    private TraceEventSession _session;
    private Thread _pumpThread;
    private byte _pumpSlotIdx;
    private uint _pumpOsThreadId;
    private volatile bool _started;
    private volatile bool _stopRequested;

    public EtwSchedulingPump()
    {
        _profiledProcessId = Environment.ProcessId;
    }

    /// <summary>Whether the pump's background thread is currently running and the ETW session is open.</summary>
    public bool IsRunning => _started && !_stopRequested;

    /// <summary>
    /// Open the NT Kernel Logger session and spawn the pump thread. Idempotent — re-invocation while running is a no-op and returns <c>true</c>. Returns
    /// <c>false</c> when the pump cannot come up (non-Windows, insufficient privileges, NT Kernel Logger already owned by another tool); a single diagnostic
    /// line is written to <see cref="Console.Error"/> and the caller is expected to treat scheduling data as unavailable for the rest of the session.
    /// </summary>
    public bool Start()
    {
        if (_started)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("[Typhon] EtwSchedulingPump: thread scheduling tracing requires Windows; skipping.");
            return false;
        }

        var nativePrepReport = PrepareKernelTraceControlLoad();

        try
        {
            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.Dispatcher);

            var parser = _session.Source.Kernel;
            parser.ThreadCSwitch += OnContextSwitch;
            parser.DispatcherReadyThread += OnReadyThread;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Most common failure mode in practice — operator forgot to run elevated. One stderr line, no rethrow, caller sees Start() == false.
            Console.Error.WriteLine("[Typhon] EtwSchedulingPump: insufficient privileges to open NT Kernel Logger (needs Administrator or 'Performance Log "
                                  + "Users' membership). Thread scheduling tracing disabled. " + ex.Message);
            SafeDispose();
            return false;
        }
        catch (Exception ex)
        {
            // Any other failure: NT Kernel Logger owned by another tool (PerfView/WPR/xperf), a native-DLL load miss, etc. The exception
            // chain, stack trace, and native prep report below are printed verbatim so the real cause is diagnosable from the engine's stderr.
            Console.Error.WriteLine("[Typhon] EtwSchedulingPump: failed to open NT Kernel Logger. Thread scheduling tracing disabled. "
                                  + DescribeException(ex));
            Console.Error.WriteLine("[Typhon] EtwSchedulingPump: KernelTraceControl.dll native prep — " + nativePrepReport);
            Console.Error.WriteLine("[Typhon] EtwSchedulingPump: stack —\n" + ex.StackTrace);
            SafeDispose();
            return false;
        }
        finally
        {
            // Undo the temporary DLL-search-directory override. TraceEvent only needs it while it lazily loads
            // KernelTraceControl.dll inside EnableKernelProvider; that load is one-shot and process-cached, so once the
            // try block has run the override has served its purpose and the default search order is restored.
            SetDllDirectory(null);
        }

        _started = true;
        _pumpThread = new Thread(RunPump) { IsBackground = true, Name = "Typhon.EtwSchedulingPump" };
        _pumpThread.Start();
        return true;
    }

    /// <summary>
    /// Make TraceEvent's lazy load of <c>KernelTraceControl.dll</c> succeed regardless of the .NET host. TraceEvent's
    /// <c>ETWKernelControl.LoadKernelTraceControl()</c> resolves the DLL relative to its own assembly directory; under a host that loads the managed assemblies
    /// from a byte array (e.g. the Godot runtime, where <c>ManifestModule.FullyQualifiedName</c> is <c>"&lt;Unknown&gt;"</c>) that directory collapses to empty
    /// and the load degrades to the bare relative path <c>&lt;arch&gt;/KernelTraceControl.dll</c> — resolved against the process working directory, where the
    /// DLL is not present, surfacing as a "specified module could not be found" <see cref="System.ComponentModel.Win32Exception"/>. The DLL ships in an
    /// <c>amd64</c> / <c>x86</c> / <c>arm64</c> subfolder next to <c>Microsoft.Diagnostics.Tracing.TraceEvent.dll</c> (see the package's build props); this
    /// method finds that folder and points the OS loader's DLL-search directory at its parent via <c>SetDllDirectory</c>, so TraceEvent's relative load
    /// resolves. The DLL is also pre-loaded by absolute path as a backstop. Returns a short human-readable report — surfaced in the <see cref="Start"/> failure
    /// diagnostic so a miss is debuggable from the field. Best-effort: never throws — TraceEvent's own resolution still runs as the fallback.
    /// </summary>
    private static string PrepareKernelTraceControlLoad()
    {
        try
        {
            var archDir = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => "amd64",
                Architecture.X86   => "x86",
                Architecture.Arm64 => "arm64",
                _                  => null,
            };
            if (archDir is null)
            {
                return $"unsupported process architecture {RuntimeInformation.ProcessArchitecture}";
            }

            var probed = new List<string>();
            foreach (var baseDir in EnumerateNativeProbeDirs())
            {
                var dllPath = Path.Combine(baseDir, archDir, "KernelTraceControl.dll");
                probed.Add(dllPath);
                if (File.Exists(dllPath))
                {
                    // SetDllDirectory(baseDir) makes TraceEvent's relative `<arch>/KernelTraceControl.dll` load resolve against the real
                    // deployment folder; the absolute pre-load is a backstop for any bare-name load path TraceEvent might take instead.
                    NativeLibrary.TryLoad(dllPath, out _);
                    SetDllDirectory(baseDir);
                    return "DLL search dir set to " + baseDir;
                }
            }

            return "KernelTraceControl.dll not found; probed: " + string.Join(" | ", probed);
        }
        catch (Exception ex)
        {
            return "native prep threw " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Adds <paramref name="lpPathName"/> to the OS DLL-search path for subsequent <c>LoadLibrary</c> calls in this process (and drops the current directory
    /// from that path). Passing <c>null</c> restores the default search order. Used only to bracket TraceEvent's one-shot native load — see
    /// <see cref="PrepareKernelTraceControlLoad"/>.
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    /// <summary>
    /// Candidate root directories to probe for TraceEvent's native <c>KernelTraceControl.dll</c>. The DLL is deployed alongside the managed TraceEvent assembly
    /// (in an architecture subfolder), but under hosts that load assemblies from a byte array (<see cref="System.Reflection.Assembly.Location"/> empty) that
    /// path is not reflectively discoverable — so several host-independent roots are tried in turn.
    /// </summary>
    private static IEnumerable<string> EnumerateNativeProbeDirs()
    {
        yield return AppContext.BaseDirectory;

        foreach (var asm in new[] { typeof(TraceEventSession).Assembly, typeof(EtwSchedulingPump).Assembly })
        {
            string dir = null;
            try
            {
                var loc = asm.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    dir = Path.GetDirectoryName(loc);
                }
            }
            catch
            {
                // Assembly.Location throws for assemblies loaded from a byte array — treat as "unknown" and skip.
            }
            if (dir is not null)
            {
                yield return dir;
            }
        }

        yield return Environment.CurrentDirectory;
    }

    /// <summary>Flatten an exception chain into one line: <c>TypeName: message -&gt; InnerTypeName: message -&gt; …</c>. Used by the <see cref="Start"/>
    /// failure diagnostic so the underlying cause (a Win32 module-load error, a privilege denial, …) is visible without a debugger.</summary>
    private static string DescribeException(Exception ex)
    {
        var sb = new StringBuilder();
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (sb.Length > 0)
            {
                sb.Append(" -> ");
            }
            sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pump body — runs on the dedicated <c>Typhon.EtwSchedulingPump</c> thread. Claims a <see cref="ThreadSlot"/> so emitted events carry a valid producer
    /// slot in their common header, captures its own OS TID for self-filtering at the cswitch callback, then blocks in
    /// <see cref="TraceEventSession.Source"/>.<c>Process</c> until <see cref="Dispose"/> tears the session down and unblocks the call.
    /// </summary>
    private void RunPump()
    {
        try
        {
            // Pre-warm: claim a Typhon slot for this thread via GetOrAssignSlot (lazy, fast on subsequent calls) and capture our OS TID so OnContextSwitch
            // can skip events about the pump itself. AssignClaim captures OS TID via GetCurrentThreadId, so reading OwnerOsThreadId here is safe and stable
            // for the lifetime of the pump thread.
            _pumpSlotIdx = (byte)ThreadSlotRegistry.GetOrAssignSlot();
            _pumpOsThreadId = ThreadSlotRegistry.GetSlot(_pumpSlotIdx).OwnerOsThreadId;

            _session.Source.Process();
        }
        catch (Exception ex)
        {
            // Process() can throw on session teardown (especially after Dispose). Swallow during shutdown; surface during normal operation so a regression
            // doesn't go unnoticed.
            if (!_stopRequested)
            {
                Console.Error.WriteLine("[Typhon] EtwSchedulingPump: pump terminated unexpectedly. " + ex);
            }
        }
    }

    private void OnContextSwitch(CSwitchTraceData data)
    {
        // ETW delivers cswitch events for every CPU machine-wide. Fast-rejection path: only consider events where at least one side belongs to our process.
        // Idle (PID 0) and other-process threads exit here without touching the dict or doing any lookup.
        if (data.OldProcessID != _profiledProcessId && data.NewProcessID != _profiledProcessId)
        {
            return;
        }

        var oldTid = (uint)data.OldThreadID;
        var newTid = (uint)data.NewThreadID;

        // Close the OLD thread's slice, if any. Filter: only emit when (a) the TID isn't the pump itself (self-records would be spam) and (b) the TID maps
        // to a live Typhon slot. Registry lookup is a linear scan over the high-water mark (~30 active slots typical, ~10 ns) — cheap at the cswitch rate.
        if (oldTid != 0 && oldTid != _pumpOsThreadId && ThreadSlotRegistry.TryGetSlotByOsThreadId(oldTid, out var oldSlotIdx))
        {
            if (_threadStates.TryGetValue(oldTid, out var st) && st.StartTick != -1)
            {
                EmitSliceClosing(in st, in data, (byte)oldSlotIdx);
            }
            // Reset the state — the next CSwitchNewThread will open a fresh slice. Keep the entry to avoid dict churn; just zero out StartTick + ReadyTick.
            _threadStates[oldTid] = new ThreadSliceState { StartTick = -1, ReadyTick = 0, ProcessorNumber = 0 };
        }

        // Open the NEW thread's slice if it's one we'll potentially care about. We don't gate this on the slot lookup yet — by the time the slice closes
        // (next CSwitchOldThread for this TID), TryGetSlotByOsThreadId runs again and decides whether to emit. Keeping latency low here matters because
        // the slot can be mid-claim during this exact callback (the slot-claim path emits its first event on the claiming thread, not the pump).
        if (newTid != 0 && newTid != _pumpOsThreadId && data.NewProcessID == _profiledProcessId)
        {
            ref var st = ref CollectionsMarshal.GetValueRefOrAddDefault(_threadStates, newTid, out _);
            st.StartTick = data.TimeStampQPC;
            st.ProcessorNumber = (ushort)data.ProcessorNumber;
            // ReadyTick stays set from the matching DispatcherReadyThread that fired just before this. If no Ready was observed (rare — happens at the first
            // slice of a brand-new thread, or when the kernel re-schedules without going through Wait), it stays 0 and the closing emit reports ReadyTime = 0.
        }
    }

    private void OnReadyThread(DispatcherReadyThreadTraceData data)
    {
        if (data.AwakenedProcessID != _profiledProcessId)
        {
            return;
        }
        var tid = (uint)data.AwakenedThreadID;
        if (tid == 0)
        {
            return;
        }
        ref var st = ref CollectionsMarshal.GetValueRefOrAddDefault(_threadStates, tid, out _);
        st.ReadyTick = data.TimeStampQPC;
    }

    private static void EmitSliceClosing(in ThreadSliceState st, in CSwitchTraceData data, byte targetSlotIdx)
    {
        var endTick = data.TimeStampQPC;
        var durationTicks = endTick - st.StartTick;
        if (durationTicks < 0)
        {
            // Clock anomaly (CPU-private QPC backstep observed on some hypervisors / older multi-socket boxes). Drop the slice rather than emit a negative.
            return;
        }
        var durationQpc = durationTicks > uint.MaxValue ? uint.MaxValue : (uint)durationTicks;

        uint readyTimeQpc = 0;
        if (st.ReadyTick > 0 && st.StartTick > st.ReadyTick)
        {
            var rt = st.StartTick - st.ReadyTick;
            readyTimeQpc = rt > uint.MaxValue ? uint.MaxValue : (uint)rt;
        }

        // Map kernel ThreadWaitReason to our wire-stable enum. Newer Windows builds can append entries past our snapshot — those land on the
        // MaximumWaitReason sentinel so the workbench can still render the slice with an "unknown reason" label rather than dropping it.
        var rawWaitReason = (byte)data.OldThreadWaitReason;
        var waitReason = rawWaitReason < (byte)ThreadWaitReason.MaximumWaitReason ? (ThreadWaitReason)rawWaitReason : ThreadWaitReason.MaximumWaitReason;

        var gettingIdle = data.NewProcessID == 0 && data.NewThreadID == 0;
        var processorNumber = (byte)(st.ProcessorNumber & 0xFF);
        var threadState = (byte)data.OldThreadState;

        // Emit with the slice's START tick as the common-header timestamp (historical QPC). Generator emits this partial method on TyphonEvent; the gate check
        // (RuntimeThreadSchedulingActive) is the first thing it does, so a disabled config makes the whole call zero-cost via JIT dead-code elimination.
        TyphonEvent.EmitThreadContextSwitch(st.StartTick, targetSlotIdx, processorNumber, waitReason, threadState, gettingIdle, durationQpc, readyTimeQpc);
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }
        _stopRequested = true;
        SafeDispose();
        // Process() returns once the session is disposed; join with a generous deadline so a stuck callback doesn't deadlock the engine shutdown.
        _pumpThread?.Join(TimeSpan.FromSeconds(2));
        _pumpThread = null;
        _started = false;
    }

    private void SafeDispose()
    {
        try
        {
            _session?.Dispose();
        }
        catch
        {
            // Best-effort during shutdown — ETW session teardown can race with in-flight callbacks. We don't have a logger here yet (#TBD profiler logging).
        }
        _session = null;
    }
}
