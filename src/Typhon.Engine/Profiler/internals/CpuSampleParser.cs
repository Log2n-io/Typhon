// TraceEvent 3.x marks TimeStampQPC as "discouraged" but still ships it. We use raw QPC ticks so sample timestamps cross-walk with
// CpuSamplerSession.SamplingSessionStartQpc (= Stopwatch.GetTimestamp() = QueryPerformanceCounter on Windows) with no unit conversion.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Parses the <c>.nettrace</c> companion captured by <see cref="CpuSamplerSession"/> (Phase 1) into an <i>already-interned</i>
/// <see cref="ParsedCpuSamples"/> batch. Runs at session stop, off any engine hot path. Best-effort throughout: a missing / empty / corrupt stream
/// yields <see cref="ParsedCpuSamples.Empty"/> and one <see cref="Console.Error"/> diagnostic — it never throws into the host (the profiling session
/// still produces its <c>.typhon-trace</c>).
/// </summary>
/// <remarks>
/// <para>
/// Per-sample call stacks require <see cref="TraceLog"/> — the layer that correlates the EventPipe stack table and resolves code addresses to methods.
/// <see cref="TraceLog.CreateFromEventPipeDataFile(string, string, TraceLogOptions)"/> converts the <c>.nettrace</c> to a temporary <c>.etlx</c>, which
/// is deleted once parsing completes.
/// </para>
/// <para>
/// <b>Interning while parsing — the scalability contract.</b> TraceLog already interns call stacks (a <c>CallStackIndex</c>) and code addresses (a
/// <c>CodeAddressIndex</c>). The parser keys its own caches on those indices, so each unique stack is walked once and each unique frame is resolved
/// once — a real session is millions of samples but only thousands of unique stacks / hundreds of unique frames. Parse memory is therefore
/// <c>O(uniqueStacks + uniqueFrames + samples)</c>, never the <c>O(samples × stackDepth)</c> blow-up of materialising a resolved-frame array per sample.
/// </para>
/// <para>
/// Each <c>Microsoft-DotNETCore-SampleProfiler</c> <c>ThreadSample</c> event carries the OS thread id, so a sample on a Typhon thread maps straight to
/// its <see cref="ThreadSlot"/> via <see cref="ThreadSlotRegistry.TryGetSlotByOsThreadId"/>. Samples on non-Typhon threads (GC, thread-pool, finalizer,
/// the sampler / IPC threads) are kept with <see cref="CpuSampleRecord.ThreadSlot"/> = -1. <c>Error</c>-type samples (failed stack walks) are dropped.
/// </para>
/// <para>
/// <b>GC-safepoint-poll samples are dropped — observer-effect noise.</b> The sampler suspends the EE ~1 kHz to walk every thread's stack; a thread it
/// catches at a GC-safe point is parked in <c>Thread.PollGC</c> / its <c>&lt;PollGC&gt;g__PollGCWorker</c> local function. A sample whose <i>leaf</i> frame
/// is that poll is not program execution — it is the cost of being sampled, and it pads whatever method sits beneath it. Such samples are discarded here,
/// before the trace is written, mirroring how <c>GcEventListener</c> already refuses to record the sampler's own <c>GcSuspendReason.Other</c> EE-suspensions.
/// </para>
/// </remarks>
internal static class CpuSampleParser
{
    /// <summary>Guard against a pathological call stack — TraceLog stacks are bounded, but a cap keeps a corrupt stream from allocating without limit.</summary>
    private const int MaxStackDepth = 1024;

    /// <summary>
    /// Hard cap on retained sample records (#351 — volumetry guard). Sample volume is <c>1 kHz × managedThreadCount × sessionSeconds</c> — unbounded
    /// with session length. Past this cap the parsed set is uniformly stride-decimated: a statistical profiler's accuracy depends on the <i>ratio</i> of
    /// samples between code paths, which a uniform stride preserves. Bounds the trailer-section size on disk, the Workbench's resident sample array, and
    /// every server-side fold. Override with <c>TYPHON__PROFILER__CPUSAMPLING__MAXSAMPLES</c>.
    /// </summary>
    private static readonly int MaxSamples = ReadMaxSamples();

    private static int ReadMaxSamples()
    {
        var raw = Environment.GetEnvironmentVariable("TYPHON__PROFILER__CPUSAMPLING__MAXSAMPLES");
        return int.TryParse(raw, out var v) && v > 0 ? v : 3_000_000;
    }

    /// <summary>
    /// Parses <paramref name="netTracePath"/> into an interned CPU-sample batch. Returns <see cref="ParsedCpuSamples.Empty"/> (never null, never throws)
    /// when the file is absent, empty, or cannot be parsed.
    /// </summary>
    public static ParsedCpuSamples Parse(string netTracePath)
    {
        if (string.IsNullOrEmpty(netTracePath) || !File.Exists(netTracePath))
        {
            Console.Error.WriteLine($"[CpuSampleParser] .nettrace companion not found: '{netTracePath}' — no CPU samples parsed.");
            return ParsedCpuSamples.Empty;
        }

        var etlxPath = Path.Combine(Path.GetTempPath(), $"typhon-cpusample-{Guid.NewGuid():N}.etlx");
        try
        {
            // The parse epilogue is timed in three parts — the module map, the .nettrace→.etlx transcode, and event
            // processing — with per-frame symbol resolution accumulated separately so a slow PDB lookup is visible.
            var totalSw = Stopwatch.StartNew();
            var moduleMap = BuildModuleMap();
            var moduleMapMs = totalSw.ElapsedMilliseconds;

            var options = new TraceLogOptions { ContinueOnError = true };
            // CreateFromEventPipeDataFile converts the .nettrace to the .etlx at etlxPath and returns that path; TraceLog itself is opened over the .etlx.
            var convertSw = Stopwatch.StartNew();
            TraceLog.CreateFromEventPipeDataFile(netTracePath, etlxPath, options);
            var convertMs = convertSw.ElapsedMilliseconds;

            var ctx = new ParseContext(moduleMap);
            var processSw = Stopwatch.StartNew();
            using (var traceLog = new TraceLog(etlxPath))
            {
                var source = traceLog.Events.GetSource();
                var sampleParser = new SampleProfilerTraceEventParser(source);
                sampleParser.ThreadSample += ctx.Add;
                source.Process();
            }
            var processMs = processSw.ElapsedMilliseconds;

            var result = ctx.Build(out var decimatedFrom);
            var decimNote = decimatedFrom > 0 ? $" (decimated from {decimatedFrom}, cap {MaxSamples})" : string.Empty;
            var gcPollNote = ctx.GcPollDropped > 0 ? $", dropped {ctx.GcPollDropped} GC-safepoint-poll samples" : string.Empty;
            Console.WriteLine(
                $"[CpuSampleParser] parsed {result.SampleCount} samples{decimNote}{gcPollNote} — {result.Stacks.Length} unique stacks, "
                + $"{result.Frames.Length} unique frames in {totalSw.ElapsedMilliseconds} ms — module map {moduleMapMs} ms, "
                + $".nettrace→.etlx convert {convertMs} ms, event processing {processMs} ms (of which symbol resolution {ctx.ResolveMs} ms)");
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CpuSampleParser] failed to parse '{netTracePath}': {ex.Message} — no CPU samples produced.");
            return ParsedCpuSamples.Empty;
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    /// <summary>
    /// Per-parse interning state + the <see cref="ThreadSample"/> handler. One instance lives for a single <see cref="Parse"/> call; not thread-safe by
    /// design — <c>source.Process()</c> dispatches events single-threaded.
    /// </summary>
    private sealed class ParseContext
    {
        private readonly Dictionary<string, Module> _moduleMap;
        private readonly List<CpuSampleRecord> _records = [];

        // Stack table: content-interned (StackComparer) so distinct CallStackIndex values that collapse to the same frame-id sequence share one entry,
        // and a CallStackIndex → final-stack-index memo so each unique TraceLog stack is walked exactly once.
        private readonly List<ushort[]> _stacks = [];
        private readonly Dictionary<ushort[], int> _stackInterner = new(StackComparer.Instance);
        private readonly Dictionary<CallStackIndex, int> _callStackToStackIndex = [];

        // Frame table: (method, file, line)-interned, plus a CodeAddressIndex → frame-id memo so each unique code address is symbol-resolved once.
        private readonly List<ParsedCpuFrame> _frames = [];
        private readonly Dictionary<FrameKey, ushort> _frameInterner = [];
        private readonly Dictionary<CodeAddressIndex, ushort> _codeAddrToFrameId = [];

        // CodeAddressIndex → "is this the GC-safepoint poll" memo. The poll leaf recurs on a huge share of samples, all sharing
        // one code address, so the name check runs once per unique code address, not once per sample.
        private readonly Dictionary<CodeAddressIndex, bool> _gcPollLeafCache = [];

        private long _resolveTicks;
        private int _emptyStackIndex = -1;
        private int _gcPollDropped;

        public ParseContext(Dictionary<string, Module> moduleMap) => _moduleMap = moduleMap;

        /// <summary>Accumulated symbol-resolution time, rendered as milliseconds.</summary>
        public long ResolveMs => _resolveTicks * 1000 / Stopwatch.Frequency;

        /// <summary>Count of samples discarded because their leaf frame was the GC-safepoint poll (observer-effect noise).</summary>
        public int GcPollDropped => _gcPollDropped;

        /// <summary><see cref="SampleProfilerTraceEventParser.ThreadSample"/> handler — interns one sample's stack and appends its record.</summary>
        public void Add(ClrThreadSampleTraceData data)
        {
            // Error samples are failed stack walks — they carry no usable stack and are dropped (design §6.4).
            if (data.Type == ClrThreadSampleType.Error)
            {
                return;
            }
            var callStack = data.CallStack();
            // A sample whose leaf frame is the GC-safepoint poll is a thread the sampler froze at a safepoint to walk its
            // stack — observer-effect noise, not program execution. Drop it before it reaches the trace (see class remarks).
            if (callStack != null && IsGcPollLeaf(callStack.CodeAddress))
            {
                _gcPollDropped++;
                return;
            }
            var sampleType = data.Type == ClrThreadSampleType.External ? (byte)1 : (byte)0;
            var threadSlot = ThreadSlotRegistry.TryGetSlotByOsThreadId((uint)data.ThreadID, out var slot) ? slot : -1;
            var stackIndex = InternStack(callStack);
            _records.Add(new CpuSampleRecord(data.TimeStampQPC, threadSlot, sampleType, (uint)stackIndex));
        }

        /// <summary>True when <paramref name="leaf"/> resolves to the runtime GC-safepoint poll. Memoised per code address.</summary>
        private bool IsGcPollLeaf(TraceCodeAddress leaf)
        {
            var cai = leaf.CodeAddressIndex;
            if (_gcPollLeafCache.TryGetValue(cai, out var cached))
            {
                return cached;
            }
            var method = leaf.Method;
            var name = method != null ? method.FullMethodName : leaf.FullMethodName;
            var isPoll = IsGcPollMethodName(name);
            _gcPollLeafCache[cai] = isPoll;
            return isPoll;
        }

        /// <summary>Finalises the batch: applies the <see cref="MaxSamples"/> decimation cap, then sorts records by <c>(threadSlot, qpc)</c>.</summary>
        public ParsedCpuSamples Build(out int decimatedFrom)
        {
            decimatedFrom = 0;
            var records = _records;
            if (records.Count > MaxSamples)
            {
                decimatedFrom = records.Count;
                records = Decimate(records, MaxSamples);
            }

            var arr = records.ToArray();
            // Group per thread slot, ordered by qpc within each group — the Workbench's per-thread sample index is then a direct slice (design §6.5).
            Array.Sort(arr, static (a, b) =>
            {
                var c = a.ThreadSlot.CompareTo(b.ThreadSlot);
                return c != 0 ? c : a.Qpc.CompareTo(b.Qpc);
            });
            return new ParsedCpuSamples(arr, [.. _stacks], [.. _frames]);
        }

        private int InternStack(TraceCallStack callStack)
        {
            if (callStack == null)
            {
                return _emptyStackIndex >= 0 ? _emptyStackIndex : _emptyStackIndex = InternStackContent([]);
            }
            var csi = callStack.CallStackIndex;
            if (_callStackToStackIndex.TryGetValue(csi, out var existing))
            {
                return existing;
            }

            var frameIds = new List<ushort>(32);
            var frame = callStack;
            while (frame != null && frameIds.Count < MaxStackDepth)
            {
                frameIds.Add(InternFrame(frame.CodeAddress));
                frame = frame.Caller;
            }
            var stackIndex = InternStackContent([.. frameIds]);
            _callStackToStackIndex[csi] = stackIndex;
            return stackIndex;
        }

        private int InternStackContent(ushort[] frameIds)
        {
            if (_stackInterner.TryGetValue(frameIds, out var existing))
            {
                return existing;
            }
            var index = _stacks.Count;
            _stacks.Add(frameIds);
            _stackInterner[frameIds] = index;
            return index;
        }

        private ushort InternFrame(TraceCodeAddress codeAddress)
        {
            var cai = codeAddress.CodeAddressIndex;
            if (_codeAddrToFrameId.TryGetValue(cai, out var cached))
            {
                return cached;
            }

            var method = codeAddress.Method;
            var methodName = method != null ? method.FullMethodName : codeAddress.FullMethodName;
            var token = method?.MethodToken ?? 0;
            var moduleName = ResolveModuleName(codeAddress, method);

            string filePath = null;
            var line = 0;
            if (token != 0 && moduleName != null && _moduleMap.TryGetValue(moduleName, out var module))
            {
                var resolveStart = Stopwatch.GetTimestamp();
                // A call-tree node is a *method*, not a call site — resolve the method's entry line (no IL offset), not the
                // covering line of this sample's IL offset. Keying frame identity on the covering line would fragment one
                // method into a node per call site (a non-leaf frame's IL offset is the call site of its callee).
                var resolved = SystemSourceResolver.ResolveByToken(module, token);
                _resolveTicks += Stopwatch.GetTimestamp() - resolveStart;
                if (resolved.HasValue)
                {
                    filePath = resolved.Value.FilePath;
                    line = resolved.Value.Line;
                }
            }

            var displayName = string.IsNullOrEmpty(methodName) ? "?" : methodName;
            var key = new FrameKey(displayName, filePath);
            ushort frameId;
            if (_frameInterner.TryGetValue(key, out var existingFrame))
            {
                frameId = existingFrame;
            }
            else if (_frames.Count > ushort.MaxValue)
            {
                // u16 frame-id space exhausted (a sampled session realistically has hundreds-to-low-thousands of unique frames, far below 65 536).
                // The surplus folds onto frame id 0 — a harmless display degradation, never a crash.
                frameId = 0;
            }
            else
            {
                frameId = (ushort)_frames.Count;
                _frames.Add(new ParsedCpuFrame(displayName, filePath, line));
                _frameInterner[key] = frameId;
            }
            _codeAddrToFrameId[cai] = frameId;
            return frameId;
        }
    }

    /// <summary>Uniform stride-decimation: keeps every <c>k</c>-th record so the retained set is ≤ <paramref name="cap"/> and proportions are preserved.</summary>
    private static List<CpuSampleRecord> Decimate(List<CpuSampleRecord> records, int cap)
    {
        var stride = (records.Count + cap - 1) / cap;
        if (stride <= 1)
        {
            return records;
        }
        var kept = new List<CpuSampleRecord>(records.Count / stride + 1);
        for (var i = 0; i < records.Count; i += stride)
        {
            kept.Add(records[i]);
        }
        return kept;
    }

    /// <summary>
    /// True when <paramref name="methodName"/> is the runtime GC-safepoint poll — <c>System.Threading.Thread.PollGC</c> or its
    /// compiler-generated <c>&lt;PollGC&gt;g__PollGCWorker|NN_N</c> local function. Matched on the substring <c>PollGC</c> only:
    /// the trailing <c>|NN_N</c> local-function ordinal is a per-build compiler artifact and must not be matched, but
    /// <c>PollGC</c> itself is the stable method name. A <c>null</c> name is not the poll.
    /// </summary>
    internal static bool IsGcPollMethodName(string methodName)
        => methodName != null && methodName.Contains("PollGC", StringComparison.Ordinal);

    private static string ResolveModuleName(TraceCodeAddress codeAddress, TraceMethod method)
    {
        var moduleFile = method?.MethodModuleFile ?? codeAddress.ModuleFile;
        var name = moduleFile?.Name;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Builds a case-insensitive simple-assembly-name → manifest <see cref="Module"/> map from the loaded assemblies. The host is alive when parsing runs
    /// (session stop), so every engine / user assembly whose frames can resolve to source is loaded; modules absent from the map (native, dynamic, unloaded)
    /// resolve name-only.
    /// </summary>
    private static Dictionary<string, Module> BuildModuleMap()
    {
        var map = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic)
            {
                continue;
            }
            var name = asm.GetName().Name;
            if (!string.IsNullOrEmpty(name))
            {
                map[name] = asm.ManifestModule;
            }
        }
        return map;
    }

    /// <summary>
    /// A <c>(method, file)</c> frame-symbol identity — a call-tree node is a <i>method</i>, so every sample of that method (at any IL offset) interns to one
    /// frame. Keying on line as well would fragment a method into one frame per call site.
    /// </summary>
    private readonly struct FrameKey : IEquatable<FrameKey>
    {
        private readonly string _method;
        private readonly string _filePath;

        public FrameKey(string method, string filePath)
        {
            _method = method;
            _filePath = filePath;
        }

        public bool Equals(FrameKey other) => 
            string.Equals(_method, other._method, StringComparison.Ordinal) && string.Equals(_filePath, other._filePath, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is FrameKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_method, _filePath);
    }

    /// <summary>Content equality / hash over a frame-id stack array — interns distinct <c>CallStackIndex</c> values that fold to the same frame sequence.</summary>
    private sealed class StackComparer : IEqualityComparer<ushort[]>
    {
        public static readonly StackComparer Instance = new();

        public bool Equals(ushort[] x, ushort[] y) => x.AsSpan().SequenceEqual(y.AsSpan());

        public int GetHashCode(ushort[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(MemoryMarshal.AsBytes(obj.AsSpan()));
            return hash.ToHashCode();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover temp .etlx in the OS temp dir is harmless — never let cleanup failure surface.
        }
    }
}
