using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Typhon.Engine;

/// <summary>
/// Host-side helpers that turn a parsed <see cref="ProfilerLaunchConfig"/> into the side effects the host needs:
/// flipping the telemetry gate before <see cref="TelemetryConfig"/> is first read, building the exporter list, and
/// printing a diagnostic banner.
/// </summary>
/// <remarks>
/// Designed so that any Typhon host (AntHill, IOProfileRunner, MonitoringDemo, …) can use the same conventions and
/// the same code paths — no copy-pasted parsing or env-var setup logic across host repos.
/// </remarks>
public static class ProfilerLauncher
{
    /// <summary>
    /// Holds the in-process CPU sampler for the current profiling session, or <c>null</c> when none is running. Managed by
    /// <see cref="StartCpuSampler"/> / <see cref="StopCpuSampler"/>.
    /// </summary>
    private static CpuSamplerSession CpuSampler;

    /// <summary>
    /// The <see cref="FileExporter"/>(s) built by the most recent <see cref="CreateExporters"/> call. <see cref="StopCpuSampler"/> hands them the parsed
    /// CPU samples so the close path can embed them as a trace trailer section (#351). Null when no trace-file exporter was created.
    /// </summary>
    private static List<FileExporter> FileExporters;

    /// <summary>
    /// The background stop+parse task kicked by <see cref="BeginCpuSamplerStop"/>, or <c>null</c> when the stop has not been begun asynchronously.
    /// <see cref="StopCpuSampler"/> awaits it. Running the parse off-thread lets the (single-threaded, seconds-long) <c>.nettrace</c> transcode +
    /// symbol resolution overlap the host's engine teardown instead of serialising on the exit path.
    /// </summary>
    private static Task<ParsedCpuSamples> ParseTask;

    /// <summary>Path of the <c>.nettrace</c> capture being parsed by <see cref="ParseTask"/> — retained so <see cref="StopCpuSampler"/> can delete it.</summary>
    private static string NetTracePath;

    /// <summary>
    /// If <paramref name="config"/> requests any profiler output: set <c>TYPHON__PROFILER__ENABLED</c>, call
    /// <see cref="TelemetryConfig.EnsureInitialized"/>, AND eagerly allocate the spillover ring pool. <b>Must run
    /// before any engine type JITs methods that read <see cref="TelemetryConfig.ProfilerActive"/></b> — i.e., before
    /// constructing the bridge / runtime — otherwise the JIT bakes the gate as <c>false</c> and no events are
    /// emitted.
    /// </summary>
    /// <param name="config">Parsed profiler launch options.</param>
    /// <param name="options">Tunables for the spillover pool (and any other engine-side options consumed at this
    /// stage). Defaults to <see cref="ProfilerOptions"/> if omitted, which currently gives an 8 × 16 MiB spillover
    /// pool. The pool is allocated HERE so that events emitted between gate-open and <see cref="TyphonProfiler.Start"/>
    /// — typically during host bridge construction's spawn burst — can extend the chain instead of dropping.</param>
    /// <returns><c>true</c> if the gate was opened, <c>false</c> if the config wasn't active.</returns>
    public static bool EnableTelemetryGateIfActive(ProfilerLaunchConfig config, ProfilerOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.IsActive)
        {
            return false;
        }
        // The TYPHON__PROFILER__ENABLED env var (double-underscore: .NET configuration section separator) is the
        // master gate. Setting it BEFORE EnsureInitialized ensures the static-readonly TelemetryConfig fields read
        // it in their first-load. After this point, all JIT'd code sees ProfilerActive == true.
        Environment.SetEnvironmentVariable("TYPHON__PROFILER__ENABLED", "true");
        TelemetryConfig.EnsureInitialized();

        // Allocate the spillover pool eagerly — events emitted before TyphonProfiler.Start (e.g. AntHill's bulk
        // spawn during bridge initialization) need a place to chain to. Without this, ~11 MiB of EcsSpawn records
        // would silently drop on primary overflow because the pool wasn't allocated yet.
        options ??= new ProfilerOptions();
        options.Validate();
        if (!SpilloverRingPool.IsInitialized)
        {
            SpilloverRingPool.Initialize(options.SpilloverBufferCount, options.SpilloverBufferSizeBytes);
        }
        return true;
    }

    /// <summary>
    /// Construct the exporter list per <paramref name="config"/>. Returns an empty list when the config isn't active
    /// (no trace file, no live port). The caller attaches each via <see cref="TyphonProfiler.AttachExporter"/> and
    /// then calls <see cref="TyphonProfiler.Start"/> — at that point each exporter's <c>Initialize</c> runs, which
    /// for a <see cref="TcpExporter"/> with <see cref="ProfilerLaunchConfig.LiveWaitMs"/> &gt; 0 blocks until the
    /// first viewer connects.
    /// </summary>
    public static List<IProfilerExporter> CreateExporters(ProfilerLaunchConfig config, IResource profilerParent)
    {
        ArgumentNullException.ThrowIfNull(config);
        var exporters = new List<IProfilerExporter>(2);
        FileExporters = null;
        if (!config.IsActive)
        {
            return exporters;
        }
        ArgumentNullException.ThrowIfNull(profilerParent);

        if (config.TraceFilePath != null)
        {
            var fileExporter = new FileExporter(config.TraceFilePath, profilerParent);
            exporters.Add(fileExporter);
            (FileExporters ??= []).Add(fileExporter);
        }
        if (config.LivePort >= 0)
        {
            exporters.Add(new TcpExporter(config.LivePort, profilerParent, config.LiveWaitMs));
        }
        return exporters;
    }

    /// <summary>
    /// Start an in-process CPU stack-sampling session for the profiling run, if requested. Returns the captured
    /// <c>SamplingSessionStartQpc</c> anchor (to be threaded into <c>ProfilerSessionMetadata</c>), or <c>0</c> when sampling is not active.
    /// </summary>
    /// <remarks>
    /// A no-op returning <c>0</c> unless <see cref="TelemetryConfig.ProfilerCpuSamplingActive"/> is set AND a trace file is configured — CPU sampling is
    /// file-mode only (it embeds into the <c>.typhon-trace</c>; live/TCP sampling is a later phase). Must be called <b>before</b> the host builds its
    /// <c>ProfilerSessionMetadata</c>, so the QPC anchor lands in the trace header.
    /// </remarks>
    public static long StartCpuSampler(ProfilerLaunchConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!TelemetryConfig.ProfilerCpuSamplingActive)
        {
            return 0;
        }
        if (config.TraceFilePath == null)
        {
            Console.Error.WriteLine("[Typhon] CpuSampler: CpuSampling is enabled but no trace file is configured; CPU sampling skipped (file mode only).");
            return 0;
        }

        CpuSampler = new CpuSamplerSession();
        CpuSampler.Start(config.TraceFilePath);
        return CpuSampler.SamplingSessionStartQpc;
    }

    /// <summary>
    /// Begin stopping the CPU-sampling session <i>asynchronously</i>: stop the EventPipe session and parse its <c>.nettrace</c> capture on a background
    /// thread. Idempotent and best-effort — safe when no sampler is running or when already begun. <see cref="StopCpuSampler"/> later awaits the result.
    /// </summary>
    /// <remarks>
    /// Call this <b>just before the host tears down its engine</b> (e.g. before <c>bridge.Dispose()</c>). The capture's transcode + symbol resolution is
    /// single-threaded and runs for seconds on a large session; kicking it here lets it overlap the engine-teardown work (dirty-page flush, etc.) instead
    /// of serialising on the exit path. The trade-off is that CPU samples no longer cover the engine teardown itself — a negligible slice for a
    /// statistical profile. A host that skips this call still works: <see cref="StopCpuSampler"/> falls back to a synchronous stop+parse.
    /// </remarks>
    public static void BeginCpuSamplerStop()
    {
        var sampler = CpuSampler;
        if (sampler == null || ParseTask != null)
        {
            return;
        }
        CpuSampler = null;
        var netTracePath = sampler.NetTracePath;
        NetTracePath = netTracePath;
        ParseTask = Task.Run(() =>
        {
            // Dispose stops the EventPipe session and finalizes the .nettrace on disk; the path stays readable afterwards.
            sampler.Dispose();
            return string.IsNullOrEmpty(netTracePath) || !File.Exists(netTracePath) ? ParsedCpuSamples.Empty : CpuSampleParser.Parse(netTracePath);
        });
    }

    /// <summary>
    /// Stop the CPU-sampling session started by <see cref="StartCpuSampler"/>, parse its <c>.nettrace</c> capture (or await the parse already kicked by
    /// <see cref="BeginCpuSamplerStop"/>), and hand the resolved samples to the trace-file exporter(s) so the close path embeds them as a trailer section
    /// (#351). The transient <c>.nettrace</c> is deleted afterwards — it is not a persisted artifact (design §2). Idempotent and best-effort: safe when no
    /// sampler is running, and a parse failure never throws into the host (the trace is simply written without CPU samples).
    /// </summary>
    /// <remarks>
    /// <b>Must be called before <c>TyphonProfiler.Stop()</c></b> — the samples have to reach the <see cref="FileExporter"/> before its <c>Dispose</c> writes
    /// the trace trailer.
    /// </remarks>
    public static void StopCpuSampler()
    {
        var parseTask = ParseTask;
        var sampler = CpuSampler;
        if (parseTask == null && sampler == null)
        {
            return;
        }

        var epilogueSw = Stopwatch.StartNew();
        ParsedCpuSamples parsed;
        string netTracePath;

        if (parseTask != null)
        {
            // BeginCpuSamplerStop already kicked the stop+parse off-thread — just await its result. With a good overlap this wait is near-zero.
            netTracePath = NetTracePath;
            ParseTask = null;
            NetTracePath = null;
            try
            {
                parsed = parseTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[Typhon] CpuSampler: background CPU-sample parse failed; the trace is written without CPU samples. "
                    + ex.GetType().Name + ": " + ex.Message);
                parsed = ParsedCpuSamples.Empty;
            }
        }
        else
        {
            // Synchronous fallback — a host that never called BeginCpuSamplerStop. Stop the session + parse inline.
            CpuSampler = null;
            netTracePath = sampler.NetTracePath;
            sampler.Dispose();
            parsed = ParsedCpuSamples.Empty;
            try
            {
                if (!string.IsNullOrEmpty(netTracePath) && File.Exists(netTracePath))
                {
                    parsed = CpuSampleParser.Parse(netTracePath);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[Typhon] CpuSampler: parsing the CPU-sample capture failed; the trace is written without CPU samples. "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        if (parsed.SampleCount > 0 && FileExporters != null)
        {
            foreach (var fileExporter in FileExporters)
            {
                fileExporter.SetCpuSamples(parsed);
            }
        }
        TryDeleteNetTrace(netTracePath);
        Console.WriteLine(
            $"[Typhon] CpuSampler: epilogue complete in {epilogueSw.ElapsedMilliseconds} ms — {parsed.SampleCount} samples "
            + "(the trailer-section encode is timed separately by FileExporter at trace close).");
    }

    private static void TryDeleteNetTrace(string path)
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
            // The .nettrace is a transient capture artifact (design §2 — never persisted); a failed delete is harmless.
        }
    }

    /// <summary>
    /// Print a multi-line diagnostic banner showing the active telemetry config + attached exporters. Useful at host
    /// startup when the operator wants visual confirmation that profiling is wired up. Logger delegate lets the host
    /// route output to its own log sink (Godot's <c>GD.Print</c>, <c>Console.WriteLine</c>, etc.).
    /// </summary>
    public static void PrintDiagnostics(Action<string> log, IList<IProfilerExporter> exporters)
    {
        if (log == null)
        {
            return;
        }

        log("───────────────────────────────────────────────────────────");
        log(" Typhon Telemetry Diagnostics");
        log("───────────────────────────────────────────────────────────");
        log(TelemetryConfig.GetActiveComponentsSummary());
        log("");
        log(TelemetryConfig.GetConfigurationSummary());
        log("");
        string exporterSummary;
        if (exporters == null || exporters.Count == 0)
        {
            exporterSummary = "(none — profiling not requested)";
        }
        else
        {
            var names = new string[exporters.Count];
            for (int i = 0; i < exporters.Count; i++)
            {
                names[i] = exporters[i].GetType().Name;
            }
            exporterSummary = string.Join(", ", names);
        }
        log($" Exporters:                 {exporterSummary}");
        log($" ProfilerActive (JIT gate): {TelemetryConfig.ProfilerActive}");
        log($" TyphonProfiler.IsRunning:  {TyphonProfiler.IsRunning}");
        log("───────────────────────────────────────────────────────────");
    }
}
